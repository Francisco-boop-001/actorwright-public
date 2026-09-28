using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>TES4 head-part buckets used by the pinned editor (0=misc, 1..9 required main parts).</summary>
public enum NpcHeadPartType
{
    Misc = 0,
    Face = 1,
    Eyes = 2,
    Hair = 3,
    FacialHair = 4,
    Scar = 5,
    Eyebrows = 6,
    Meatcaps = 7,
    Teeth = 8,
    HeadRear = 9
}

public static class NpcHeadPartTypeExtensions
{
    public static string ToWireName(this NpcHeadPartType value) => value switch
    {
        NpcHeadPartType.Misc => "misc",
        NpcHeadPartType.Face => "face",
        NpcHeadPartType.Eyes => "eyes",
        NpcHeadPartType.Hair => "hair",
        NpcHeadPartType.FacialHair => "facial-hair",
        NpcHeadPartType.Scar => "scar",
        NpcHeadPartType.Eyebrows => "eyebrows",
        NpcHeadPartType.Meatcaps => "meatcaps",
        NpcHeadPartType.Teeth => "teeth",
        NpcHeadPartType.HeadRear => "head-rear",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown head-part type.")
    };

    /// <summary>
    /// Projects a raw HDPT PNAM value onto the closed editor buckets. Mod-defined
    /// types beyond 9 (UBE eye wetness 71, Nerissa 72/41) become Misc so the
    /// part routes by its model and flags instead of being refused.
    /// </summary>
    public static NpcHeadPartType FromPnam(uint value) =>
        value <= (uint)NpcHeadPartType.HeadRear ? (NpcHeadPartType)value : NpcHeadPartType.Misc;

    public static string ToWireName(this NpcHeadPartType value, uint? rawPnamType) =>
        rawPnamType > (uint)NpcHeadPartType.HeadRear
            ? rawPnamType.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToWireName();

    public static bool TryParseWireName(string value, out NpcHeadPartType type)
    {
        foreach (var candidate in Enum.GetValues<NpcHeadPartType>())
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }
}

public sealed record NpcHeadPartSelection(FormReference Reference, NpcHeadPartType Type)
{
    /// <summary>Exact HDPT PNAM when provider bytes established it.</summary>
    [JsonIgnore]
    public uint? RawPnamType { get; init; }

    [JsonIgnore]
    public uint PnamType => RawPnamType ?? (uint)Type;
}

public sealed record NpcFaceSnapshot(
    NpcSex Sex,
    FormReference? Race,
    ImmutableArray<NpcHeadPartSelection> HeadParts,
    FormReference? HairColor);

public sealed record NpcHeadPartProvider(
    FormReference Reference,
    NpcHeadPartType Type,
    bool IsExtra,
    string? ModelPath,
    ImmutableArray<FormReference> ValidRaces,
    ImmutableArray<FormReference> ExtraParts,
    string Flags)
{
    /// <summary>Exact HDPT PNAM retained beside its closed editor projection.</summary>
    [JsonIgnore]
    public uint? RawPnamType { get; init; }

    [JsonIgnore]
    public uint PnamType => RawPnamType ?? (uint)Type;
}

public sealed record NpcFacePatch(
    ImmutableArray<NpcHeadPartSelection>? HeadParts,
    OptionalFormReference HairColor,
    NpcHeadPartReplacement? Replacement = null)
{
    public bool IsEmpty => HeadParts is null && !HairColor.IsSpecified && Replacement is null;
}

public sealed record NpcHeadPartReplacement(FormReference Old, FormId NewLocalFormId);

public sealed record NpcFacePatchRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    NpcFacePatch Patch,
    WorkspacePath DataRoot,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record NpcFacePatchProposal(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    Sha256Hash InputHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<NpcHeadPartProvider> Providers,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record NpcFacePatchResult(
    bool Applied,
    NpcFacePatchProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcFacePatchService
{
    ValueTask<NpcFacePatchProposal> AnalyzeAsync(NpcFacePatchRequest request, CancellationToken cancellationToken);

    ValueTask<NpcFacePatchResult> ApplyAsync(NpcFacePatchRequest request, NpcFacePatchProposal proposal,
        CancellationToken cancellationToken);
}
