using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Independently reopens one production Skyrim ARMO and compares the complete
/// supported document surface. It shares no readback helper with the writer.
/// </summary>
public sealed class BethesdaSkyrimArmorProductionOutputReader :
    ISkyrimArmorProductionOutputReader
{
    public async ValueTask<SkyrimArmorProductionVerification> ReadAsync(
        ArmorProposalArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(outputPlugin.Value))
        {
            diagnostics.Add(Error("armor-production-output-missing",
                "The ARMO output does not exist for independent readback."));
            return Empty(outputPlugin, diagnostics);
        }
        if (!string.Equals(artifact.Edition, "skyrimse", StringComparison.Ordinal) ||
            !artifact.CompleteDocument)
        {
            diagnostics.Add(Error("armor-production-artifact-contract",
                "Independent production readback requires one complete Skyrim SE ARMO artifact."));
            return Empty(outputPlugin, diagnostics);
        }

        try
        {
            ModKey outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(outputPlugin.Value));
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(outputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            IArmorGetter[] armors = mod.Armors.ToArray();
            int majorCount = mod.EnumerateMajorRecords().Count();
            int otherCount = majorCount - armors.Length;
            if (armors.Length != 1 || otherCount != 0)
                diagnostics.Add(Error("armor-production-record-surface",
                    "Independent readback requires exactly one ARMO and zero unrelated records."));

            IArmorGetter? armor = armors.Length == 1 ? armors[0] : null;
            FormId? expectedTarget = ExpectedTarget(artifact, diagnostics);
            ModKey expectedOwner = artifact.Mode == ArmorProposalMode.New
                ? outputKey
                : ModKey.FromNameAndExtension(
                    Path.GetFileName(artifact.SourcePlugin));
            bool ownerMatches = armor is not null && expectedTarget is not null &&
                                armor.FormKey == new FormKey(
                                    expectedOwner, expectedTarget.Value.Value);
            bool editorIdMatches = armor is not null && string.Equals(
                armor.EditorID,
                artifact.EditorId,
                StringComparison.Ordinal);
            bool documentMatches = armor is not null && MatchesDocument(
                armor,
                artifact);
            string[] actualMasters = mod.ModHeader.MasterReferences
                .Select(item => item.Master.FileName.String)
                .ToArray();
            string[] expectedMasters = ExpectedMasters(artifact, outputKey);
            bool masterSetMatches = actualMasters.SequenceEqual(
                expectedMasters,
                StringComparer.OrdinalIgnoreCase);

            if (!ownerMatches)
                diagnostics.Add(Error("armor-production-identity",
                    "Independent readback did not preserve the expected ARMO owner and FormID."));
            if (!editorIdMatches)
                diagnostics.Add(Error("armor-production-editor-id",
                    "Independent readback did not preserve the exact ARMO EditorID."));
            if (!documentMatches)
                diagnostics.Add(Error("armor-production-document",
                    "Independent readback did not preserve the complete supported ARMO document."));
            if (!masterSetMatches)
                diagnostics.Add(Error("armor-production-masters",
                    "Independent readback did not preserve the exact ARMO master order."));

            Sha256Hash hash = await HashFileAsync(
                outputPlugin.Value,
                cancellationToken).ConfigureAwait(false);
            bool valid = armors.Length == 1 && otherCount == 0 &&
                         ownerMatches && editorIdMatches && documentMatches &&
                         masterSetMatches && !HasErrors(diagnostics);
            return new(
                valid,
                outputPlugin,
                hash,
                armors.Length,
                otherCount,
                armor is null
                    ? null
                    : new PluginName(armor.FormKey.ModKey.FileName),
                armor is null ? null : new FormId(armor.FormKey.ID),
                ownerMatches,
                editorIdMatches,
                documentMatches,
                masterSetMatches,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           OverflowException)
        {
            diagnostics.Add(Error("armor-production-output-read",
                exception.Message));
            return Empty(outputPlugin, diagnostics);
        }
    }

    private static bool MatchesDocument(
        IArmorGetter armor,
        ArmorProposalArtifact artifact) =>
        string.Equals(armor.Name?.String ?? string.Empty,
            artifact.Name ?? string.Empty,
            StringComparison.Ordinal) &&
        string.Equals(armor.Description?.String ?? string.Empty,
            artifact.Description ?? string.Empty,
            StringComparison.Ordinal) &&
        artifact.NonPlayable is { } nonPlayable &&
        armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable) == nonPlayable &&
        artifact.Value is { } value && armor.Value == value &&
        artifact.Weight is { } weight &&
        Math.Abs(armor.Weight - weight) < 0.0001F &&
        artifact.ArmorRating is { } rating &&
        Math.Abs(armor.ArmorRating - rating) < 0.0001F &&
        artifact.SlotMask is { } slotMask &&
        armor.BodyTemplate?.FirstPersonFlags == (BipedObjectFlag)slotMask &&
        SameLink(armor.Race.FormKey, artifact.Race) &&
        SameLink(armor.ObjectEffect.FormKey, artifact.Enchantment) &&
        SameLink(armor.PickUpSound.FormKey, artifact.PickupSound) &&
        SameLink(armor.PutDownSound.FormKey, artifact.DropSound) &&
        SameLink(armor.EquipmentType.FormKey, artifact.EquipmentType) &&
        SameLink(armor.AlternateBlockMaterial.FormKey,
            artifact.AlternateBlockMaterial) &&
        SameLink(armor.TemplateArmor.FormKey, artifact.TemplateArmor) &&
        string.Equals(armor.WorldModel?.Male?.Model?.File ?? string.Empty,
            artifact.MaleWorldModel ?? string.Empty,
            StringComparison.Ordinal) &&
        string.Equals(armor.WorldModel?.Female?.Model?.File ?? string.Empty,
            artifact.FemaleWorldModel ?? string.Empty,
            StringComparison.Ordinal) &&
        SameBounds(armor.ObjectBounds, artifact.ObjectBounds) &&
        armor.Armature.Select(item => item.FormKey).SequenceEqual(
            artifact.ArmorAddons.Select(item => ParseFormKey(item.Addon))) &&
        (armor.Keywords?.Select(item => item.FormKey) ?? [])
            .SequenceEqual(artifact.Keywords.Select(ParseFormKey));

    private static bool SameLink(FormKey actual, string? expected) =>
        actual == (expected is null ? FormKey.Null : ParseFormKey(expected));

    private static bool SameBounds(
        IObjectBoundsGetter? actual,
        ArmorObjectBounds? expected) =>
        actual is not null && expected is not null &&
        actual.First.X == expected.MinimumX &&
        actual.First.Y == expected.MinimumY &&
        actual.First.Z == expected.MinimumZ &&
        actual.Second.X == expected.MaximumX &&
        actual.Second.Y == expected.MaximumY &&
        actual.Second.Z == expected.MaximumZ;

    private static FormId? ExpectedTarget(
        ArmorProposalArtifact artifact,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string value = artifact.Mode == ArmorProposalMode.New
            ? artifact.TargetFormId ?? string.Empty
            : artifact.SourceFormId;
        if (FormId.TryParse(value, out FormId target)) return target;
        diagnostics.Add(Error("armor-production-target-invalid",
            "The reviewed ARMO artifact has no valid target identity."));
        return null;
    }

    private static string[] ExpectedMasters(
        ArmorProposalArtifact artifact,
        ModKey outputKey)
    {
        var values = new List<ModKey>
        {
            ModKey.FromNameAndExtension(Path.GetFileName(artifact.SourcePlugin))
        };
        values.AddRange(artifact.MasterDependencies.Select(value =>
            ModKey.FromNameAndExtension(value)));
        values.AddRange(AllReferences(artifact).Select(item =>
            ParseFormKey(item).ModKey));
        return values.Where(item => item != outputKey)
            .Distinct()
            .Select(item => item.FileName.String)
            .ToArray();
    }

    private static IEnumerable<string> AllReferences(
        ArmorProposalArtifact artifact)
    {
        if (artifact.Race is not null) yield return artifact.Race;
        if (artifact.Enchantment is not null) yield return artifact.Enchantment;
        if (artifact.PickupSound is not null) yield return artifact.PickupSound;
        if (artifact.DropSound is not null) yield return artifact.DropSound;
        if (artifact.EquipmentType is not null) yield return artifact.EquipmentType;
        if (artifact.AlternateBlockMaterial is not null)
            yield return artifact.AlternateBlockMaterial;
        if (artifact.TemplateArmor is not null) yield return artifact.TemplateArmor;
        foreach (string keyword in artifact.Keywords) yield return keyword;
        foreach (ArmorAddonArtifact addon in artifact.ArmorAddons)
            yield return addon.Addon;
    }

    private static FormKey ParseFormKey(string value) =>
        FormReference.TryParse(value, out FormReference reference)
            ? new FormKey(
                ModKey.FromNameAndExtension(reference.Plugin.Value),
                reference.FormId.Value)
            : throw new InvalidDataException(
                $"Invalid ARMO FormReference '{value}'.");

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    private static SkyrimArmorProductionVerification Empty(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(
        false,
        output,
        null,
        0,
        0,
        null,
        null,
        false,
        false,
        false,
        false,
        diagnostics.ToImmutable());

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
