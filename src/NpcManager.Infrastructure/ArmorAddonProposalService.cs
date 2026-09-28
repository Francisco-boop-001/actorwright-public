using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound ARMA proposal without binary plugin mutation.</summary>
public sealed class ArmorAddonProposalService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IArmorAddonProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ArmorAddonProposalResult> ProposeAsync(ArmorAddonProposalRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        ValidatePatch(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("armor-addon-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }

        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("armor-addon-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId &&
            string.Equals(record.Signature, "ARMA", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("armor-addon-source-not-found", DiagnosticSeverity.Error,
            $"ARMA source record {request.SourceFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("armor-addon-source-duplicate", DiagnosticSeverity.Error,
            $"ARMA source record {request.SourceFormId} is duplicated in the source plugin."));
        if (request.Mode == ArmorAddonProposalMode.Override && matches.Length == 1 && string.IsNullOrWhiteSpace(matches[0].EditorId))
            diagnostics.Add(new Diagnostic("armor-addon-source-editor-id-missing", DiagnosticSeverity.Error,
                "Override proposals require the source ARMA EditorID."));
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        ValidateReferences(request.Patch, allowed, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var editorId = request.Mode == ArmorAddonProposalMode.Override ? matches[0].EditorId! : request.Patch.EditorId!.Value.Value;
        var patchBytes = JsonSerializer.SerializeToUtf8Bytes(request.Patch, JsonOptions);
        var artifact = new ArmorAddonProposalArtifact("1", "armor-addon-record-proposal", request.Edition.ToWireName(),
            request.Mode, request.SourcePlugin.Value, request.SourceFormId.ToString(), editorId, inputHash.Value,
            Convert.ToHexString(SHA256.HashData(patchBytes)), request.Patch.SlotMask, Ref(request.Patch.Race),
            Ref(request.Patch.FootstepSet), request.Patch.MalePriority, request.Patch.FemalePriority,
            request.Patch.MaleWeightSliderFlags, request.Patch.FemaleWeightSliderFlags, request.Patch.DetectionSound,
            request.Patch.WeaponAdjust, request.Patch.MaleModel, request.Patch.FemaleModel,
            request.Patch.MaleFirstPersonModel, request.Patch.FemaleFirstPersonModel, request.Patch.MaleModelFlags,
            request.Patch.FemaleModelFlags, request.Patch.MaleColorRemapIndex, request.Patch.FemaleColorRemapIndex,
            Ref(request.Patch.MaleSkinTexture), Ref(request.Patch.FemaleSkinTexture),
            Ref(request.Patch.MaleSkinTextureSwapList), Ref(request.Patch.FemaleSkinTextureSwapList),
            Ref(request.Patch.MaleMaterialSwap), Ref(request.Patch.FemaleMaterialSwap),
            Ref(request.Patch.MaleFirstPersonMaterialSwap), Ref(request.Patch.FemaleFirstPersonMaterialSwap),
            Ref(request.Patch.ArtObject), request.Patch.AdditionalRaces?.Select(RefRequired).ToImmutableArray() ?? [],
            request.Patch.Sculpt?.Select(item => new ArmorAddonSculptArtifact(item.Gender, item.BoneName, item.DeltaX,
                item.DeltaY, item.DeltaZ)).ToImmutableArray() ?? [], request.Patch.NoUnderarmorScaling,
            request.Patch.HasSculptData, request.Patch.HiResFirstPersonOnly,
            ChangedFields(request.Patch, request.CompleteDocument),
            inspection.Masters.Select(item => item.Value).ToImmutableArray(), true, request.TargetFormId?.ToString(),
            request.CompleteDocument, request.SeedFromSource, request.TargetPlugin?.Value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new ArmorAddonProposalResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("armor-addon-write-failed", DiagnosticSeverity.Error, exception.Message)); return new ArmorAddonProposalResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private void ValidatePaths(ArmorAddonProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var sourceParent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (sourceParent is null) diagnostics.Add(new Diagnostic("armor-addon-source-parent", DiagnosticSeverity.Error,
            "The armor-addon source plugin must have a parent Data directory."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        if (!request.OutputProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-addon-output-outside-lab",
            DiagnosticSeverity.Error, "Armor-addon proposals must remain under the K-only lab root."));
        if (!request.OutputProposal.Value.EndsWith(".armor-addon-proposal.json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("armor-addon-output-extension", DiagnosticSeverity.Error,
                "Armor-addon proposals must use the .armor-addon-proposal.json extension."));
        if (File.Exists(request.OutputProposal.Value)) diagnostics.Add(new Diagnostic("armor-addon-output-exists",
            DiagnosticSeverity.Error, "Armor-addon proposals never overwrite existing artifacts."));
        var outputParent = Path.GetDirectoryName(request.OutputProposal.Value);
        if (outputParent is null || !Directory.Exists(outputParent)) diagnostics.Add(new Diagnostic("armor-addon-output-parent-missing",
            DiagnosticSeverity.Error, "The armor-addon output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(outputParent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("armor-addon-source-missing",
            DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("armor-addon-source-reparse", DiagnosticSeverity.Error,
                        "The source plugin may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("armor-addon-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("armor-addon-source-form-invalid",
            DiagnosticSeverity.Error, "The source ARMA FormID may not be null."));
    }

    private static void ValidatePatch(ArmorAddonProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Mode is not (ArmorAddonProposalMode.New or ArmorAddonProposalMode.Override))
            diagnostics.Add(new Diagnostic("armor-addon-mode-invalid", DiagnosticSeverity.Error, "Armor-addon proposal mode must be new or override."));
        if (request.Mode == ArmorAddonProposalMode.New && request.Patch.EditorId is null)
            diagnostics.Add(new Diagnostic("armor-addon-editor-id-required", DiagnosticSeverity.Error, "A new armor-addon proposal requires an explicit EditorID."));
        if (request.Mode == ArmorAddonProposalMode.Override && request.Patch.EditorId is not null)
            diagnostics.Add(new Diagnostic("armor-addon-override-editor-id", DiagnosticSeverity.Error, "Override proposals inherit the source EditorID and may not supply a replacement."));
        if (request.Mode == ArmorAddonProposalMode.New && (request.TargetFormId is null || request.TargetFormId.Value.Value == 0 || request.TargetFormId.Value.Value > 0x00FF_FFFF))
            diagnostics.Add(new Diagnostic("armor-addon-new-target-invalid", DiagnosticSeverity.Error,
                "New armor-addon proposals require a nonzero plugin-local 24-bit target FormID."));
        if (request.Mode == ArmorAddonProposalMode.Override && request.TargetFormId is not null && request.TargetFormId != request.SourceFormId)
            diagnostics.Add(new Diagnostic("armor-addon-override-target-invalid", DiagnosticSeverity.Error,
                "Override armor-addon proposals must target the source FormID."));
        if (request.CompleteDocument && request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("armor-addon-complete-edition", DiagnosticSeverity.Error,
                "Complete Armor-addon documents are supported for Skyrim Special Edition only."));
        if (request.SeedFromSource &&
            (!request.CompleteDocument || request.Edition != GameEdition.SkyrimSpecialEdition ||
             request.Mode != ArmorAddonProposalMode.New))
            diagnostics.Add(new Diagnostic("armor-addon-seed-source-invalid", DiagnosticSeverity.Error,
                "Source seeding is supported only for complete new Skyrim Armor-addon documents."));
        if (request.CompleteDocument && request.Mode == ArmorAddonProposalMode.New && request.TargetPlugin is null)
            diagnostics.Add(new Diagnostic("armor-addon-target-plugin-required", DiagnosticSeverity.Error,
                "A complete new Armor-addon document requires its target plugin identity."));
        if (request.Mode == ArmorAddonProposalMode.Override && request.TargetPlugin is not null)
            diagnostics.Add(new Diagnostic("armor-addon-override-target-plugin", DiagnosticSeverity.Error,
                "An Armor-addon override retains its source owner and may not declare a target plugin."));
        ValidateAsset(request.Patch.MaleModel, "male-model", diagnostics);
        ValidateAsset(request.Patch.FemaleModel, "female-model", diagnostics);
        ValidateAsset(request.Patch.MaleFirstPersonModel, "male-first-person-model", diagnostics);
        ValidateAsset(request.Patch.FemaleFirstPersonModel, "female-first-person-model", diagnostics);
        if (request.Patch.WeaponAdjust is { } weapon &&
            (!double.IsFinite(weapon) || weapon < -100000 || weapon > 100000))
            diagnostics.Add(new Diagnostic("armor-addon-weapon-adjust-range",
                DiagnosticSeverity.Error,
                "Weapon adjust must be finite and within -100000..100000."));
        if (request.Patch.MaleColorRemapIndex is { } maleColor && (!double.IsFinite(maleColor) || maleColor < 0 || maleColor > 255))
            diagnostics.Add(new Diagnostic("armor-addon-male-color-range", DiagnosticSeverity.Error, "Male color remap index must be finite and within 0..255."));
        if (request.Patch.FemaleColorRemapIndex is { } femaleColor && (!double.IsFinite(femaleColor) || femaleColor < 0 || femaleColor > 255))
            diagnostics.Add(new Diagnostic("armor-addon-female-color-range", DiagnosticSeverity.Error, "Female color remap index must be finite and within 0..255."));
        if (HasUnsupportedFields(request.Patch, request.Edition)) diagnostics.Add(new Diagnostic("armor-addon-field-unsupported",
            DiagnosticSeverity.Error, "The requested ARMA field is Fallout 4-only in this contract."));
        if (request.Patch.AdditionalRaces is { } races && (races.IsDefault || races.Any(item => item.FormId.Value == 0) || races.Distinct().Count() != races.Length))
            diagnostics.Add(new Diagnostic("armor-addon-races-invalid", DiagnosticSeverity.Error, "Additional races must be unique, non-null FormReferences."));
        if (request.Patch.Sculpt is { } sculpt)
        {
            if (sculpt.IsDefault || sculpt.Length > 4096) diagnostics.Add(new Diagnostic("armor-addon-sculpt-count", DiagnosticSeverity.Error, "Sculpt data must contain at most 4096 rows."));
            if (sculpt.Any(item => item.Gender > 1 || string.IsNullOrWhiteSpace(item.BoneName) || item.BoneName.Length > 256 ||
                !double.IsFinite(item.DeltaX) || !double.IsFinite(item.DeltaY) || !double.IsFinite(item.DeltaZ) ||
                item.DeltaX < -100 || item.DeltaX > 100 || item.DeltaY < -100 || item.DeltaY > 100 || item.DeltaZ < -100 || item.DeltaZ > 100))
                diagnostics.Add(new Diagnostic("armor-addon-sculpt-invalid", DiagnosticSeverity.Error, "Sculpt rows require gender 0/1, safe bone names, and finite deltas within -100..100."));
        }
        if (request.CompleteDocument)
            ValidateCompleteSkyrimPatch(request.Patch, diagnostics);
        if (ChangedFields(request.Patch, request.CompleteDocument).IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("armor-addon-empty-patch", DiagnosticSeverity.Error,
                "The armor-addon proposal must contain at least one supported field."));
    }

    private static void ValidateReferences(ArmorAddonProposalPatch patch, ImmutableHashSet<PluginName> allowed,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var refs = new List<FormReference>();
        foreach (var reference in new[] { patch.Race, patch.FootstepSet, patch.MaleSkinTexture, patch.FemaleSkinTexture,
            patch.MaleSkinTextureSwapList, patch.FemaleSkinTextureSwapList, patch.MaleMaterialSwap, patch.FemaleMaterialSwap,
            patch.MaleFirstPersonMaterialSwap, patch.FemaleFirstPersonMaterialSwap, patch.ArtObject })
            if (reference is { } value) refs.Add(value);
        if (patch.AdditionalRaces is { } races) refs.AddRange(races);
        foreach (var reference in refs)
        {
            if (reference.FormId.Value == 0) diagnostics.Add(new Diagnostic("armor-addon-reference-null", DiagnosticSeverity.Error, $"Armor-addon reference {reference} uses the null FormID."));
            if (!allowed.Contains(reference.Plugin)) diagnostics.Add(new Diagnostic("armor-addon-master-unresolved", DiagnosticSeverity.Error, $"Armor-addon reference {reference} is not provided by the source plugin or its masters."));
        }
    }

    private static void ValidateAsset(string? value, string field, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value is null) return;
        try { _ = new AssetPath(value); } catch (ArgumentException exception) { diagnostics.Add(new Diagnostic($"armor-addon-{field}-invalid", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static bool HasUnsupportedFields(ArmorAddonProposalPatch patch, GameEdition edition) =>
        edition == GameEdition.SkyrimSpecialEdition && (patch.MaleModelFlags is not null || patch.FemaleModelFlags is not null ||
            patch.MaleColorRemapIndex is not null || patch.FemaleColorRemapIndex is not null || patch.MaleMaterialSwap is not null ||
            patch.FemaleMaterialSwap is not null || patch.MaleFirstPersonMaterialSwap is not null || patch.FemaleFirstPersonMaterialSwap is not null ||
            patch.Sculpt is not null || patch.NoUnderarmorScaling is not null || patch.HasSculptData is not null || patch.HiResFirstPersonOnly is not null);

    private static void ValidateCompleteSkyrimPatch(
        ArmorAddonProposalPatch patch,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch.SlotMask is null || patch.MalePriority is null ||
            patch.FemalePriority is null || patch.MaleWeightSliderFlags is null ||
            patch.FemaleWeightSliderFlags is null || patch.DetectionSound is null ||
            patch.WeaponAdjust is null || patch.AdditionalRaces is null ||
            patch.AdditionalRaces.Value.IsDefault)
            diagnostics.Add(new Diagnostic("armor-addon-complete-fields", DiagnosticSeverity.Error,
                "A complete Skyrim Armor-addon document must initialize slots, priorities, weight-slider state, detection sound, weapon adjustment, and additional races."));
        if (patch.MaleWeightSliderFlags is > 1 || patch.FemaleWeightSliderFlags is > 1)
            diagnostics.Add(new Diagnostic("armor-addon-complete-weight-slider", DiagnosticSeverity.Error,
                "Complete Skyrim weight-slider state must be zero or one per gender."));
    }

    private static ImmutableArray<string> ChangedFields(
        ArmorAddonProposalPatch patch,
        bool completeDocument = false)
    {
        var fields = ImmutableArray.CreateBuilder<string>();
        var values = new (string Name, object? Value)[] { ("slotMask", patch.SlotMask), ("race", patch.Race), ("footstepSet", patch.FootstepSet),
            ("malePriority", patch.MalePriority), ("femalePriority", patch.FemalePriority), ("maleWeightSliderFlags", patch.MaleWeightSliderFlags),
            ("femaleWeightSliderFlags", patch.FemaleWeightSliderFlags), ("detectionSound", patch.DetectionSound), ("weaponAdjust", patch.WeaponAdjust),
            ("maleModel", patch.MaleModel), ("femaleModel", patch.FemaleModel), ("maleFirstPersonModel", patch.MaleFirstPersonModel),
            ("femaleFirstPersonModel", patch.FemaleFirstPersonModel), ("maleModelFlags", patch.MaleModelFlags), ("femaleModelFlags", patch.FemaleModelFlags),
            ("maleColorRemapIndex", patch.MaleColorRemapIndex), ("femaleColorRemapIndex", patch.FemaleColorRemapIndex),
            ("maleSkinTexture", patch.MaleSkinTexture), ("femaleSkinTexture", patch.FemaleSkinTexture), ("maleSkinTextureSwapList", patch.MaleSkinTextureSwapList),
            ("femaleSkinTextureSwapList", patch.FemaleSkinTextureSwapList), ("maleMaterialSwap", patch.MaleMaterialSwap), ("femaleMaterialSwap", patch.FemaleMaterialSwap),
            ("maleFirstPersonMaterialSwap", patch.MaleFirstPersonMaterialSwap), ("femaleFirstPersonMaterialSwap", patch.FemaleFirstPersonMaterialSwap),
            ("artObject", patch.ArtObject), ("additionalRaces", patch.AdditionalRaces), ("sculpt", patch.Sculpt),
            ("noUnderarmorScaling", patch.NoUnderarmorScaling), ("hasSculptData", patch.HasSculptData), ("hiResFirstPersonOnly", patch.HiResFirstPersonOnly) };
        if (patch.EditorId is not null) fields.Add("editorId");
        foreach (var item in values)
            if (item.Value is not null) fields.Add(item.Name);
        if (completeDocument)
        {
            foreach (string field in new[]
            {
                "slotMask", "race", "footstepSet", "malePriority", "femalePriority",
                "maleWeightSliderFlags", "femaleWeightSliderFlags", "detectionSound",
                "weaponAdjust", "maleModel", "femaleModel", "maleFirstPersonModel",
                "femaleFirstPersonModel", "maleSkinTexture", "femaleSkinTexture",
                "maleSkinTextureSwapList", "femaleSkinTextureSwapList", "artObject",
                "additionalRaces"
            })
                if (!fields.Contains(field, StringComparer.Ordinal)) fields.Add(field);
        }
        return fields.ToImmutable();
    }

    private static string? Ref(FormReference? reference) => reference?.ToString();
    private static string RefRequired(FormReference reference) => reference.ToString();
    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ArmorAddonProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
