using NpcManager.Domain;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// The exact self-contained editor-location capability admitted to Finish
/// Core.  It is intentionally narrower than the historical follower-finish
/// placement template: no placed reference, owner, VMAD, or condition is
/// allowed to enter the world-clean package.
/// </summary>
public sealed record SkyrimNpcFinishCoreSandboxSnapshot(
    FormReference Template,
    string TemplateEditorId,
    string LocationKind,
    int LocationData,
    uint Radius,
    FormReference? PlacedTarget,
    string? OwnerQuest,
    bool HasVirtualMachineAdapter,
    int ConditionCount,
    int LocationCount,
    string Procedure,
    bool ContinuousSchedule,
    Sha256Hash RawRecordDigest);

/// <summary>
/// Internal typed candidate used to keep the hostile predicate tests
/// independent from Mutagen's binary overlay implementation.
/// </summary>
internal sealed record BethesdaSkyrimNpcFinishCoreSandboxCandidate(
    FormReference Template,
    string TemplateEditorId,
    string LocationKind,
    int LocationData,
    uint Radius,
    FormReference? PlacedTarget,
    string? OwnerQuest,
    bool HasVirtualMachineAdapter,
    int ConditionCount,
    int LocationCount,
    string Procedure,
    bool ContinuousSchedule,
    Sha256Hash RawRecordDigest);

public static class BethesdaSkyrimNpcFinishCoreSandboxGate
{
    private static readonly FormReference CanonicalTemplate =
        new(new PluginName("Skyrim.esm"), new FormId(0x0001B217));

    private const string CanonicalEditorId =
        "DefaultSandboxEditorLocation512";

    private static readonly Sha256Hash CanonicalRawRecordDigest =
        new(
            "fba3cca0eff98528da3985962ff9058ee" +
            "7662ea944c250f3ffb90a0490d52685");

    /// <summary>
    /// Inspects a private copy of the already admitted canonical template.
    /// The admission performs the file hash and raw-record proof; this gate
    /// adds the stricter no-reference typed predicate.
    /// </summary>
    public static SkyrimNpcFinishCoreSandboxSnapshot Inspect(
        BethesdaSkyrimFollowerFinishSandboxAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        Package template = authority.CreateTemplateCopy();
        PackageDataLocation[] locations = template.Data.Values
            .OfType<PackageDataLocation>()
            .ToArray();
        PackageDataLocation? location = locations.SingleOrDefault();
        string locationKind = "Unknown";
        int locationData = -1;
        uint radius = 0;
        FormReference? placedTarget = null;
        if (location?.Location is LocationTargetRadius targetRadius)
        {
            radius = targetRadius.Radius;
            switch (targetRadius.Target)
            {
                case LocationFallback fallback:
                    locationKind = fallback.Type.ToString();
                    locationData = fallback.Data;
                    break;
                case LocationTarget target
                    when !target.Link.FormKey.IsNull:
                    placedTarget = ToReference(target.Link.FormKey);
                    locationKind = "NearReference";
                    break;
            }
        }

        string? ownerQuest = template.OwnerQuest.FormKeyNullable is { } owner
            ? ToReference(owner).ToString()
            : null;
        var candidate = new BethesdaSkyrimNpcFinishCoreSandboxCandidate(
            ToReference(template.FormKey),
            template.EditorID ?? string.Empty,
            locationKind,
            locationData,
            radius,
            placedTarget,
            ownerQuest,
            template.VirtualMachineAdapter is not null,
            template.Conditions.Count,
            locations.Length,
            authority.ProcedureType,
            authority.ScheduleMonth == -1 &&
            authority.ScheduleDayOfWeek == Package.DayOfWeek.Any &&
            authority.ScheduleDate == 0 &&
            authority.ScheduleHour == -1 &&
            authority.ScheduleMinute == -1 &&
            authority.ScheduleDurationInMinutes == 0,
            authority.RawRecordDigest);

        return InspectCandidate(candidate);
    }

    internal static SkyrimNpcFinishCoreSandboxSnapshot InspectCandidateForTests(
        BethesdaSkyrimNpcFinishCoreSandboxCandidate candidate) =>
        InspectCandidate(candidate);

    private static SkyrimNpcFinishCoreSandboxSnapshot InspectCandidate(
        BethesdaSkyrimNpcFinishCoreSandboxCandidate candidate)
    {
        if (candidate.Template != CanonicalTemplate ||
            !string.Equals(
                candidate.TemplateEditorId,
                CanonicalEditorId,
                StringComparison.Ordinal))
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "npc-finish-core-sandbox-template",
                "The package is not the fixed Skyrim.esm editor-location template.");

        if (candidate.RawRecordDigest != CanonicalRawRecordDigest)
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "npc-finish-core-sandbox-record-digest",
                "The package raw record digest differs from the admitted canonical template.");

        if (candidate.LocationCount != 1 ||
            !string.Equals(
                candidate.LocationKind,
                "NearEditorLocation",
                StringComparison.Ordinal) ||
            candidate.LocationData != 0 ||
            candidate.Radius != 512 ||
            candidate.PlacedTarget is not null ||
            candidate.OwnerQuest is not null ||
            candidate.HasVirtualMachineAdapter ||
            candidate.ConditionCount != 0 ||
            !string.Equals(candidate.Procedure, "Sandbox", StringComparison.Ordinal) ||
            !candidate.ContinuousSchedule)
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "npc-finish-core-sandbox-not-self-contained",
                "The editor-location package contains a placed link or noncanonical typed payload.");

        return new SkyrimNpcFinishCoreSandboxSnapshot(
            candidate.Template,
            candidate.TemplateEditorId,
            candidate.LocationKind,
            candidate.LocationData,
            candidate.Radius,
            candidate.PlacedTarget,
            candidate.OwnerQuest,
            candidate.HasVirtualMachineAdapter,
            candidate.ConditionCount,
            candidate.LocationCount,
            candidate.Procedure,
            candidate.ContinuousSchedule,
            candidate.RawRecordDigest);
    }

    private static FormReference ToReference(FormKey key) =>
        new(new PluginName(key.ModKey.ToString()), new FormId(key.ID));
}
