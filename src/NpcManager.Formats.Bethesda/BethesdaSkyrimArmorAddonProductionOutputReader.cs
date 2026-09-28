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
/// Independently reopens one production Skyrim ARMA output. It shares no
/// comparison or mutation helper with the binary writer.
/// </summary>
public sealed class BethesdaSkyrimArmorAddonProductionOutputReader :
    ISkyrimArmorAddonProductionOutputReader
{
    public async ValueTask<SkyrimArmorAddonProductionVerification> ReadAsync(
        ArmorAddonProposalArtifact artifact,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(outputPlugin.Value))
        {
            diagnostics.Add(Error("armor-addon-production-output-missing",
                "The ARMA output does not exist for independent readback."));
            return Empty(outputPlugin, diagnostics);
        }
        if (!string.Equals(
                artifact.Edition,
                "skyrimse",
                StringComparison.Ordinal) ||
            !artifact.CompleteDocument)
        {
            diagnostics.Add(Error("armor-addon-production-artifact-contract",
                "Independent production readback requires one complete Skyrim SE ARMA artifact."));
            return Empty(outputPlugin, diagnostics);
        }

        try
        {
            ModKey outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(outputPlugin.Value));
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(outputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            IArmorAddonGetter[] addons = mod.ArmorAddons.ToArray();
            int majorCount = mod.EnumerateMajorRecords().Count();
            int otherCount = majorCount - addons.Length;
            if (addons.Length != 1 || otherCount != 0)
                diagnostics.Add(Error("armor-addon-production-record-surface",
                    "Independent readback requires exactly one ARMA and zero unrelated records."));

            IArmorAddonGetter? addon = addons.Length == 1 ? addons[0] : null;
            FormId? expectedTarget = ExpectedTarget(artifact, diagnostics);
            ModKey expectedOwner = artifact.Mode == ArmorAddonProposalMode.New
                ? outputKey
                : ModKey.FromNameAndExtension(
                    Path.GetFileName(artifact.SourcePlugin));
            bool ownerMatches = addon is not null && expectedTarget is not null &&
                                addon.FormKey == new FormKey(
                                    expectedOwner,
                                    expectedTarget.Value.Value);
            bool editorIdMatches = addon is not null && string.Equals(
                addon.EditorID,
                artifact.EditorId,
                StringComparison.Ordinal);
            bool documentMatches = addon is not null && MatchesDocument(
                addon,
                artifact);
            string[] actualMasters = mod.ModHeader.MasterReferences
                .Select(item => item.Master.FileName.String)
                .ToArray();
            string[] expectedMasters = ExpectedMasters(artifact, outputKey);
            bool masterSetMatches = actualMasters.SequenceEqual(
                expectedMasters,
                StringComparer.OrdinalIgnoreCase);

            if (!ownerMatches)
                diagnostics.Add(Error("armor-addon-production-identity",
                    "Independent readback did not preserve the expected ARMA owner and FormID."));
            if (!editorIdMatches)
                diagnostics.Add(Error("armor-addon-production-editor-id",
                    "Independent readback did not preserve the exact ARMA EditorID."));
            if (!documentMatches)
                diagnostics.Add(Error("armor-addon-production-document",
                    "Independent readback did not preserve the complete supported ARMA document."));
            if (!masterSetMatches)
                diagnostics.Add(Error("armor-addon-production-masters",
                    "Independent readback did not preserve the exact ARMA master order."));

            Sha256Hash hash = await HashFileAsync(
                outputPlugin.Value,
                cancellationToken).ConfigureAwait(false);
            bool valid = addons.Length == 1 && otherCount == 0 &&
                         ownerMatches && editorIdMatches && documentMatches &&
                         masterSetMatches && !HasErrors(diagnostics);
            return new(
                valid,
                outputPlugin,
                hash,
                addons.Length,
                otherCount,
                new PluginName(outputKey.FileName),
                addon is null
                    ? null
                    : new PluginName(addon.FormKey.ModKey.FileName),
                addon is null ? null : new FormId(addon.FormKey.ID),
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
            diagnostics.Add(Error("armor-addon-production-output-read",
                exception.Message));
            return Empty(outputPlugin, diagnostics);
        }
    }

    private static bool MatchesDocument(
        IArmorAddonGetter addon,
        ArmorAddonProposalArtifact artifact) =>
        artifact.SlotMask is { } slotMask &&
        addon.BodyTemplate?.FirstPersonFlags == (BipedObjectFlag)slotMask &&
        SameLink(addon.Race, artifact.Race) &&
        SameLink(addon.FootstepSound, artifact.FootstepSet) &&
        SameLink(addon.ArtObject, artifact.ArtObject) &&
        artifact.MalePriority is { } malePriority &&
        addon.Priority?.Male == malePriority &&
        artifact.FemalePriority is { } femalePriority &&
        addon.Priority?.Female == femalePriority &&
        artifact.MaleWeightSliderFlags is { } maleWeight &&
        addon.WeightSliderEnabled?.Male == (maleWeight != 0) &&
        artifact.FemaleWeightSliderFlags is { } femaleWeight &&
        addon.WeightSliderEnabled?.Female == (femaleWeight != 0) &&
        artifact.DetectionSound is { } detection &&
        addon.DetectionSoundValue == detection &&
        artifact.WeaponAdjust is { } weaponAdjust &&
        Math.Abs(addon.WeaponAdjust - weaponAdjust) < 0.0001F &&
        addon.AdditionalRaces.Select(item => item.FormKey).SequenceEqual(
            artifact.AdditionalRaces.Select(ParseFormKey)) &&
        SameModel(addon.WorldModel?.Male, artifact.MaleModel) &&
        SameModel(addon.WorldModel?.Female, artifact.FemaleModel) &&
        SameModel(
            addon.FirstPersonModel?.Male,
            artifact.MaleFirstPersonModel) &&
        SameModel(
            addon.FirstPersonModel?.Female,
            artifact.FemaleFirstPersonModel) &&
        SameLink(addon.SkinTexture?.Male, artifact.MaleSkinTexture) &&
        SameLink(addon.SkinTexture?.Female, artifact.FemaleSkinTexture) &&
        SameLink(
            addon.TextureSwapList?.Male,
            artifact.MaleSkinTextureSwapList) &&
        SameLink(
            addon.TextureSwapList?.Female,
            artifact.FemaleSkinTextureSwapList);

    private static bool SameLink<T>(
        IFormLinkNullableGetter<T>? actual,
        string? expected)
        where T : class, IMajorRecordGetter =>
        expected is null
            ? actual is null || actual.IsNull
            : actual?.FormKey == ParseFormKey(expected);

    private static bool SameModel(IModelGetter? actual, string? expected) =>
        expected is null
            ? actual is null
            : string.Equals(actual?.File, expected, StringComparison.Ordinal);

    private static FormId? ExpectedTarget(
        ArmorAddonProposalArtifact artifact,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string value = artifact.Mode == ArmorAddonProposalMode.New
            ? artifact.TargetFormId ?? string.Empty
            : artifact.SourceFormId;
        if (FormId.TryParse(value, out FormId target)) return target;
        diagnostics.Add(Error("armor-addon-production-target-invalid",
            "The reviewed ARMA artifact has no valid target identity."));
        return null;
    }

    private static string[] ExpectedMasters(
        ArmorAddonProposalArtifact artifact,
        ModKey outputKey)
    {
        var values = new List<ModKey>
        {
            ModKey.FromNameAndExtension(Path.GetFileName(artifact.SourcePlugin))
        };
        if (artifact.Mode == ArmorAddonProposalMode.Override)
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
        ArmorAddonProposalArtifact artifact)
    {
        if (artifact.Race is not null) yield return artifact.Race;
        if (artifact.FootstepSet is not null) yield return artifact.FootstepSet;
        if (artifact.MaleSkinTexture is not null)
            yield return artifact.MaleSkinTexture;
        if (artifact.FemaleSkinTexture is not null)
            yield return artifact.FemaleSkinTexture;
        if (artifact.MaleSkinTextureSwapList is not null)
            yield return artifact.MaleSkinTextureSwapList;
        if (artifact.FemaleSkinTextureSwapList is not null)
            yield return artifact.FemaleSkinTextureSwapList;
        if (artifact.ArtObject is not null) yield return artifact.ArtObject;
        foreach (string race in artifact.AdditionalRaces) yield return race;
    }

    private static FormKey ParseFormKey(string value) =>
        FormReference.TryParse(value, out FormReference reference)
            ? new FormKey(
                ModKey.FromNameAndExtension(reference.Plugin.Value),
                reference.FormId.Value)
            : throw new InvalidDataException(
                $"Invalid ARMA FormReference '{value}'.");

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

    private static SkyrimArmorAddonProductionVerification Empty(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(
        false,
        output,
        null,
        0,
        0,
        null,
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
