using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Materializes the typed ARMA proposal surface into one ordinary plugin.
/// Unsupported proposal fields are refused rather than silently discarded.
/// </summary>
public sealed class BethesdaArmorAddonBinaryWriteService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IArmorAddonBinaryWriteService
{
    private const long MaximumProposalBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ArmorAddonBinaryWriteResult> WriteAsync(ArmorAddonBinaryWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());
        ArmorAddonProposalArtifact? proposal;
        try
        {
            var info = new FileInfo(request.Proposal.Value);
            if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                throw new InvalidDataException("The armor-addon proposal is empty or exceeds the size limit.");
            await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            proposal = await JsonSerializer.DeserializeAsync<ArmorAddonProposalArtifact>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, null, diagnostics.ToImmutable());
        }
        if (proposal is null)
        {
            diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-empty", DiagnosticSeverity.Error, "The armor-addon proposal must contain a JSON object."));
            return Refused(request, null, diagnostics.ToImmutable());
        }
        if (!string.Equals(proposal.ArtifactKind, "armor-addon-record-proposal", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-kind", DiagnosticSeverity.Error, "The proposal artifact kind must be armor-addon-record-proposal."));
        ValidateProposalArtifact(proposal, request.Edition, diagnostics);
        if (!GameEditionExtensions.TryParseWireName(proposal.Edition, out var edition) || edition != request.Edition)
            diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-edition", DiagnosticSeverity.Error, "The proposal edition does not match the write request."));
        if (!Enum.IsDefined(proposal.Mode))
            diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-mode", DiagnosticSeverity.Error, "The proposal mode is invalid."));
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) || sourceFormId.Value == 0)
            diagnostics.Add(new Diagnostic("armor-addon-binary-source-form", DiagnosticSeverity.Error, "The proposal source FormID is invalid."));
        if (string.IsNullOrWhiteSpace(proposal.EditorId))
            diagnostics.Add(new Diagnostic("armor-addon-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is required."));
        else
        {
            try { _ = new EditorId(proposal.EditorId); }
            catch (ArgumentException) { diagnostics.Add(new Diagnostic("armor-addon-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is invalid.")); }
        }
        if (edition == GameEdition.SkyrimSpecialEdition && HasUnsupportedSkyrimFields(proposal))
            diagnostics.Add(new Diagnostic("armor-addon-binary-field-unsupported", DiagnosticSeverity.Error, "The proposal contains Fallout 4-only ARMA fields."));
        if (proposal.MaleWeightSliderFlags is > 1 || proposal.FemaleWeightSliderFlags is > 1)
            diagnostics.Add(new Diagnostic("armor-addon-binary-weight-slider", DiagnosticSeverity.Error, "Binary ARMA weight-slider state supports only 0 or 1 per gender."));
        if (proposal.TargetFormId is not null && (!FormId.TryParse(proposal.TargetFormId, out var parsedTarget) || parsedTarget.Value == 0 || parsedTarget.Value > 0x00FF_FFFF))
            diagnostics.Add(new Diagnostic("armor-addon-binary-target-form", DiagnosticSeverity.Error, "The proposal target FormID is invalid."));
        var sourcePath = TryWorkspacePath(proposal.SourcePlugin, diagnostics);
        if (sourcePath is null || !File.Exists(sourcePath.Value.Value))
            diagnostics.Add(new Diagnostic("armor-addon-binary-source-missing", DiagnosticSeverity.Error, "The proposal source plugin does not exist under the K-only lab root."));
        else
        {
            AddReparseDiagnostic(diagnostics, sourcePath.Value.Value, "source");
            if (!string.Equals(Path.GetFileName(sourcePath.Value.Value), Path.GetFileName(proposal.SourcePlugin), StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("armor-addon-binary-source-name-mismatch", DiagnosticSeverity.Error, "The proposal source plugin filename must match the source path filename."));
        }
        var sourcePlugin = TryPluginName(proposal.SourcePlugin, diagnostics);
        var targetFormId = ResolveTarget(proposal, sourceFormId, diagnostics);
        var references = ParseReferences(proposal, diagnostics);
        if (sourcePath is null || sourcePlugin is null || targetFormId is null || HasErrors(diagnostics))
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        try
        {
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath.Value.Value, cancellationToken))));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("armor-addon-binary-input-hash-mismatch", DiagnosticSeverity.Error, "The proposal source hash does not match the current source plugin."));
            var outputModKey = ToModKey(request.Output.Value);
            if (outputModKey == sourcePlugin.Value)
                diagnostics.Add(new Diagnostic("armor-addon-binary-output-master-self", DiagnosticSeverity.Error, "The output plugin may not be the same plugin as the proposal source."));
            if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
            var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                switch (request.Edition)
                {
                    case GameEdition.Fallout4:
                        WriteFallout4(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, references, temporary);
                        VerifyFallout4(temporary, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, diagnostics);
                        break;
                    case GameEdition.SkyrimSpecialEdition:
                        WriteSkyrim(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, references, temporary);
                        VerifySkyrim(temporary, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new Diagnostic("armor-addon-binary-edition-unsupported", DiagnosticSeverity.Error, "The requested game edition is unsupported."));
                        break;
                }
                if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
                File.Move(temporary, request.Output.Value, overwrite: false);
                var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.Output.Value, cancellationToken))));
                return new ArmorAddonBinaryWriteResult(true, request.Proposal, request.Output, targetFormId, outputHash, diagnostics.ToImmutable());
            }
            finally { TryDelete(temporary); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            diagnostics.Add(new Diagnostic("armor-addon-binary-write-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        }
    }

    private static FormId? ResolveTarget(ArmorAddonProposalArtifact proposal, FormId source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (proposal.Mode == ArmorAddonProposalMode.Override)
        {
            if (proposal.TargetFormId is not null && (!FormId.TryParse(proposal.TargetFormId, out var target) || target != source))
                diagnostics.Add(new Diagnostic("armor-addon-binary-override-target", DiagnosticSeverity.Error, "Override proposals must target the source FormID."));
            return source;
        }
        if (!FormId.TryParse(proposal.TargetFormId ?? string.Empty, out var newTarget) || newTarget.Value == 0 || newTarget.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("armor-addon-binary-new-target", DiagnosticSeverity.Error, "New armor-addon proposals require a nonzero plugin-local 24-bit target FormID."));
        return newTarget.Value == 0 ? null : newTarget;
    }

    private static void ValidateProposalArtifact(
        ArmorAddonProposalArtifact proposal,
        GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(proposal.SchemaVersion, "1", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("armor-addon-binary-schema", DiagnosticSeverity.Error,
                "The armor-addon proposal schema version is unsupported."));
        if (proposal.ChangedFields.IsDefault ||
            proposal.ChangedFields.Any(string.IsNullOrWhiteSpace) ||
            proposal.ChangedFields.Distinct(StringComparer.Ordinal).Count() !=
            proposal.ChangedFields.Length)
            diagnostics.Add(new Diagnostic("armor-addon-binary-changed-fields", DiagnosticSeverity.Error,
                "Changed fields must be an initialized, unique list."));
        if (proposal.AdditionalRaces.IsDefault || proposal.Sculpt.IsDefault)
            diagnostics.Add(new Diagnostic("armor-addon-binary-collections", DiagnosticSeverity.Error,
                "Additional-race and sculpt collections must be initialized."));
        if (proposal.WeaponAdjust is { } weaponAdjust &&
            (!double.IsFinite(weaponAdjust) || weaponAdjust is < -100000 or > 100000))
            diagnostics.Add(new Diagnostic("armor-addon-binary-weapon-adjust", DiagnosticSeverity.Error,
                "Weapon adjustment must be finite and within -100000..100000."));
        if (!proposal.CompleteDocument) return;

        string[] requiredFields =
        [
            "slotMask", "race", "footstepSet", "malePriority", "femalePriority",
            "maleWeightSliderFlags", "femaleWeightSliderFlags", "detectionSound",
            "weaponAdjust", "maleModel", "femaleModel", "maleFirstPersonModel",
            "femaleFirstPersonModel", "maleSkinTexture", "femaleSkinTexture",
            "maleSkinTextureSwapList", "femaleSkinTextureSwapList", "artObject",
            "additionalRaces"
        ];
        if (edition != GameEdition.SkyrimSpecialEdition ||
            proposal.SlotMask is null || proposal.MalePriority is null ||
            proposal.FemalePriority is null || proposal.MaleWeightSliderFlags is null ||
            proposal.FemaleWeightSliderFlags is null || proposal.DetectionSound is null ||
            proposal.WeaponAdjust is null || proposal.AdditionalRaces.IsDefault ||
            requiredFields.Any(field =>
                !proposal.ChangedFields.Contains(field, StringComparer.Ordinal)))
            diagnostics.Add(new Diagnostic("armor-addon-binary-complete-document", DiagnosticSeverity.Error,
                "A complete Skyrim armor-addon proposal must initialize and mark every supported field."));
    }

    private static ImmutableArray<FormReference> ParseReferences(ArmorAddonProposalArtifact proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var values = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in new[] { proposal.Race, proposal.FootstepSet, proposal.MaleSkinTexture, proposal.FemaleSkinTexture,
            proposal.MaleSkinTextureSwapList, proposal.FemaleSkinTextureSwapList, proposal.MaleMaterialSwap, proposal.FemaleMaterialSwap,
            proposal.MaleFirstPersonMaterialSwap, proposal.FemaleFirstPersonMaterialSwap, proposal.ArtObject })
            if (value is not null) ParseReference(value, "reference", values, diagnostics);
        foreach (var value in proposal.AdditionalRaces) ParseReference(value, "additional-race", values, diagnostics);
        return values.ToImmutable();
    }

    private static void ParseReference(string value, string role, ImmutableArray<FormReference>.Builder values, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!FormReference.TryParse(value, out var reference) || reference.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("armor-addon-binary-reference-invalid", DiagnosticSeverity.Error, $"The {role} reference '{value}' is invalid."));
        else values.Add(reference);
    }

    private static void WriteFallout4(string sourcePath, ModKey sourceModKey, ModKey outputModKey, ArmorAddonProposalArtifact proposal,
        FormId sourceFormId, FormId targetFormId, ImmutableArray<FormReference> references, string destination)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        var source = overlay.ArmorAddons.FirstOrDefault(item => item.FormKey == new FormKey(sourceModKey, sourceFormId.Value));
        if (source is null) throw new InvalidDataException($"ARMA source record {proposal.SourceFormId} was not found in the source plugin.");
        var sourceMasters = overlay.ModHeader.MasterReferences.Select(item => item.Master).ToImmutableArray();
        ValidateMasters(references, sourceModKey, sourceMasters);
        var formKey = proposal.Mode == ArmorAddonProposalMode.Override ? source.FormKey : new FormKey(outputModKey, targetFormId.Value);
        var addon = proposal.Mode == ArmorAddonProposalMode.Override ? source.DeepCopy() : new Fo4.ArmorAddon(formKey, Fo4.Fallout4Release.Fallout4);
        ApplyFallout4(addon, proposal);
        var mod = new Fo4.Fallout4Mod(outputModKey, Fo4.Fallout4Release.Fallout4);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, references, outputModKey, proposal.Mode == ArmorAddonProposalMode.Override ? sourceMasters : []);
        mod.ArmorAddons.Add(addon);
        WriteMod(mod, destination);
    }

    private static void WriteSkyrim(string sourcePath, ModKey sourceModKey, ModKey outputModKey, ArmorAddonProposalArtifact proposal,
        FormId sourceFormId, FormId targetFormId, ImmutableArray<FormReference> references, string destination)
    {
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Sse.SkyrimRelease.SkyrimSE);
        var source = overlay.ArmorAddons.FirstOrDefault(item => item.FormKey == new FormKey(sourceModKey, sourceFormId.Value));
        if (source is null) throw new InvalidDataException($"ARMA source record {proposal.SourceFormId} was not found in the source plugin.");
        var sourceMasters = overlay.ModHeader.MasterReferences.Select(item => item.Master).ToImmutableArray();
        ValidateMasters(references, sourceModKey, sourceMasters);
        var formKey = proposal.Mode == ArmorAddonProposalMode.Override ? source.FormKey : new FormKey(outputModKey, targetFormId.Value);
        var addon = proposal.Mode == ArmorAddonProposalMode.Override ? source.DeepCopy() : new Sse.ArmorAddon(formKey, Sse.SkyrimRelease.SkyrimSE);
        ApplySkyrim(addon, proposal);
        var mod = new Sse.SkyrimMod(outputModKey, Sse.SkyrimRelease.SkyrimSE);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, references, outputModKey, proposal.Mode == ArmorAddonProposalMode.Override ? sourceMasters : []);
        mod.ArmorAddons.Add(addon);
        WriteMod(mod, destination);
    }

    private static void ApplyFallout4(Fo4.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        addon.EditorID = proposal.EditorId;
        if (HasChanged(proposal, "slotMask")) addon.BodyTemplate = new Fo4.BodyTemplate { FirstPersonFlags = (Fo4.BipedObjectFlag)proposal.SlotMask!.Value };
        if (HasChanged(proposal, "race")) addon.Race = new FormLinkNullable<Fo4.IRaceGetter>(ParseFormKey(proposal.Race!));
        if (HasChanged(proposal, "footstepSet")) addon.FootstepSound = new FormLinkNullable<Fo4.IFootstepSetGetter>(ParseFormKey(proposal.FootstepSet!));
        if (HasChanged(proposal, "artObject")) addon.ArtObject = new FormLinkNullable<Fo4.IArtObjectGetter>(ParseFormKey(proposal.ArtObject!));
        if (HasChanged(proposal, "detectionSound")) addon.DetectionSoundValue = proposal.DetectionSound!.Value;
        if (HasChanged(proposal, "weaponAdjust")) addon.WeaponAdjust = checked((float)proposal.WeaponAdjust!.Value);
        if (HasChanged(proposal, "additionalRaces"))
        {
            addon.AdditionalRaces.Clear();
            foreach (var race in proposal.AdditionalRaces) addon.AdditionalRaces.Add(new FormLink<Fo4.IRaceGetter>(ParseFormKey(race)));
        }
        ApplyGenderedModels(addon, proposal);
        ApplyGenderedLinks(addon, proposal);
        ApplyFlags(addon, proposal);
        ApplySculpt(addon, proposal);
    }

    private static void ApplySkyrim(Sse.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        addon.EditorID = proposal.EditorId;
        if (HasChanged(proposal, "slotMask")) addon.BodyTemplate = new Sse.BodyTemplate { FirstPersonFlags = (Sse.BipedObjectFlag)proposal.SlotMask!.Value };
        if (HasChanged(proposal, "race"))
            addon.Race = SkyrimLink(addon.Race, proposal.Race,
                proposal.CompleteDocument);
        if (HasChanged(proposal, "footstepSet"))
            addon.FootstepSound = SkyrimLink(addon.FootstepSound,
                proposal.FootstepSet, proposal.CompleteDocument);
        if (HasChanged(proposal, "artObject"))
            addon.ArtObject = SkyrimLink(addon.ArtObject, proposal.ArtObject,
                proposal.CompleteDocument);
        if (HasChanged(proposal, "detectionSound")) addon.DetectionSoundValue = proposal.DetectionSound!.Value;
        if (HasChanged(proposal, "weaponAdjust")) addon.WeaponAdjust = checked((float)proposal.WeaponAdjust!.Value);
        if (HasChanged(proposal, "additionalRaces"))
        {
            addon.AdditionalRaces.Clear();
            foreach (var race in proposal.AdditionalRaces) addon.AdditionalRaces.Add(new FormLink<Sse.IRaceGetter>(ParseFormKey(race)));
        }
        ApplyGenderedSkyrimModels(addon, proposal);
        ApplyGenderedSkyrimLinks(addon, proposal);
    }

    private static void ApplyGenderedModels(Fo4.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        if (!HasAny(proposal, "maleModel", "femaleModel", "maleModelFlags", "femaleModelFlags", "maleColorRemapIndex", "femaleColorRemapIndex", "maleMaterialSwap", "femaleMaterialSwap", "maleFirstPersonModel", "femaleFirstPersonModel", "maleFirstPersonMaterialSwap", "femaleFirstPersonMaterialSwap")) return;
        var male = addon.WorldModel?.Male ?? new Fo4.Model();
        var female = addon.WorldModel?.Female ?? new Fo4.Model();
        ApplyModel(male, proposal.MaleModel, proposal.MaleModelFlags, proposal.MaleColorRemapIndex, proposal.MaleMaterialSwap);
        ApplyModel(female, proposal.FemaleModel, proposal.FemaleModelFlags, proposal.FemaleColorRemapIndex, proposal.FemaleMaterialSwap);
        addon.WorldModel = new GenderedItem<Fo4.Model?>(male, female);
        var firstMale = addon.FirstPersonModel?.Male ?? new Fo4.Model();
        var firstFemale = addon.FirstPersonModel?.Female ?? new Fo4.Model();
        if (proposal.MaleFirstPersonModel is not null) firstMale.File = proposal.MaleFirstPersonModel;
        if (proposal.FemaleFirstPersonModel is not null) firstFemale.File = proposal.FemaleFirstPersonModel;
        if (proposal.MaleFirstPersonMaterialSwap is not null) firstMale.MaterialSwap = new FormLinkNullable<Fo4.IMaterialSwapGetter>(ParseFormKey(proposal.MaleFirstPersonMaterialSwap));
        if (proposal.FemaleFirstPersonMaterialSwap is not null) firstFemale.MaterialSwap = new FormLinkNullable<Fo4.IMaterialSwapGetter>(ParseFormKey(proposal.FemaleFirstPersonMaterialSwap));
        addon.FirstPersonModel = new GenderedItem<Fo4.Model?>(firstMale, firstFemale);
    }

    private static void ApplyModel(Fo4.Model model, string? path, byte? flags, double? color, string? material)
    {
        if (path is not null) model.File = path;
        if (flags is { } value) model.Flags = (Fo4.Model.Flag)value;
        if (color is { } index) model.ColorRemappingIndex = checked((float)index);
        if (material is not null) model.MaterialSwap = new FormLinkNullable<Fo4.IMaterialSwapGetter>(ParseFormKey(material));
    }

    private static void ApplyGenderedSkyrimModels(Sse.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        if (!HasAny(proposal, "maleModel", "femaleModel", "maleFirstPersonModel", "femaleFirstPersonModel")) return;
        Sse.Model? male = UpdateSkyrimModel(addon.WorldModel?.Male,
            proposal.MaleModel, HasChanged(proposal, "maleModel"),
            proposal.CompleteDocument);
        Sse.Model? female = UpdateSkyrimModel(addon.WorldModel?.Female,
            proposal.FemaleModel, HasChanged(proposal, "femaleModel"),
            proposal.CompleteDocument);
        addon.WorldModel = new GenderedItem<Sse.Model?>(male, female);
        Sse.Model? firstMale = UpdateSkyrimModel(
            addon.FirstPersonModel?.Male, proposal.MaleFirstPersonModel,
            HasChanged(proposal, "maleFirstPersonModel"),
            proposal.CompleteDocument);
        Sse.Model? firstFemale = UpdateSkyrimModel(
            addon.FirstPersonModel?.Female, proposal.FemaleFirstPersonModel,
            HasChanged(proposal, "femaleFirstPersonModel"),
            proposal.CompleteDocument);
        addon.FirstPersonModel = new GenderedItem<Sse.Model?>(firstMale, firstFemale);
    }

    private static Sse.Model? UpdateSkyrimModel(
        Sse.Model? existing,
        string? value,
        bool changed,
        bool completeDocument)
    {
        if (!changed) return existing;
        if (value is null) return completeDocument ? null : existing;
        Sse.Model model = existing ?? new Sse.Model();
        model.File = value;
        return model;
    }

    private static void ApplyGenderedLinks(Fo4.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        if (HasAny(proposal, "maleSkinTexture", "femaleSkinTexture")) addon.SkinTexture = new GenderedItem<IFormLinkNullableGetter<Fo4.ITextureSetGetter>>(Link(addon.SkinTexture?.Male, proposal.MaleSkinTexture), Link(addon.SkinTexture?.Female, proposal.FemaleSkinTexture));
        if (HasAny(proposal, "maleSkinTextureSwapList", "femaleSkinTextureSwapList")) addon.TextureSwapList = new GenderedItem<IFormLinkNullableGetter<Fo4.IFormListGetter>>(Link(addon.TextureSwapList?.Male, proposal.MaleSkinTextureSwapList), Link(addon.TextureSwapList?.Female, proposal.FemaleSkinTextureSwapList));
        if (HasChanged(proposal, "malePriority") || HasChanged(proposal, "femalePriority")) addon.Priority = new GenderedItem<byte>(proposal.MalePriority ?? addon.Priority?.Male ?? 0, proposal.FemalePriority ?? addon.Priority?.Female ?? 0);
        if (HasChanged(proposal, "maleWeightSliderFlags") || HasChanged(proposal, "femaleWeightSliderFlags")) addon.WeightSliderEnabled = new GenderedItem<bool>((proposal.MaleWeightSliderFlags ?? (addon.WeightSliderEnabled?.Male == true ? (byte)1 : (byte)0)) != 0, (proposal.FemaleWeightSliderFlags ?? (addon.WeightSliderEnabled?.Female == true ? (byte)1 : (byte)0)) != 0);
    }

    private static void ApplyGenderedSkyrimLinks(Sse.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        if (HasAny(proposal, "maleSkinTexture", "femaleSkinTexture"))
            addon.SkinTexture = new GenderedItem<IFormLinkNullableGetter<Sse.ITextureSetGetter>>(
                SkyrimLink(addon.SkinTexture?.Male, proposal.MaleSkinTexture,
                    HasChanged(proposal, "maleSkinTexture") && proposal.CompleteDocument),
                SkyrimLink(addon.SkinTexture?.Female, proposal.FemaleSkinTexture,
                    HasChanged(proposal, "femaleSkinTexture") && proposal.CompleteDocument));
        if (HasAny(proposal, "maleSkinTextureSwapList", "femaleSkinTextureSwapList"))
            addon.TextureSwapList = new GenderedItem<IFormLinkNullableGetter<Sse.IFormListGetter>>(
                SkyrimLink(addon.TextureSwapList?.Male,
                    proposal.MaleSkinTextureSwapList,
                    HasChanged(proposal, "maleSkinTextureSwapList") && proposal.CompleteDocument),
                SkyrimLink(addon.TextureSwapList?.Female,
                    proposal.FemaleSkinTextureSwapList,
                    HasChanged(proposal, "femaleSkinTextureSwapList") && proposal.CompleteDocument));
        if (HasChanged(proposal, "malePriority") || HasChanged(proposal, "femalePriority")) addon.Priority = new GenderedItem<byte>(proposal.MalePriority ?? addon.Priority?.Male ?? 0, proposal.FemalePriority ?? addon.Priority?.Female ?? 0);
        if (HasChanged(proposal, "maleWeightSliderFlags") || HasChanged(proposal, "femaleWeightSliderFlags")) addon.WeightSliderEnabled = new GenderedItem<bool>((proposal.MaleWeightSliderFlags ?? (addon.WeightSliderEnabled?.Male == true ? (byte)1 : (byte)0)) != 0, (proposal.FemaleWeightSliderFlags ?? (addon.WeightSliderEnabled?.Female == true ? (byte)1 : (byte)0)) != 0);
    }

    private static FormLinkNullable<T> SkyrimLink<T>(
        IFormLinkNullableGetter<T>? existing,
        string? value,
        bool clearWhenNull)
        where T : class, IMajorRecordGetter
    {
        if (value is not null)
            return new FormLinkNullable<T>(ParseFormKey(value));
        if (clearWhenNull || existing is null || existing.IsNull)
            return new FormLinkNullable<T>();
        return new FormLinkNullable<T>(existing.FormKey);
    }

    private static IFormLinkNullableGetter<T> Link<T>(IFormLinkNullableGetter<T>? existing, string? value) where T : class, IMajorRecordGetter
        => value is null ? existing ?? new FormLinkNullable<T>() : new FormLinkNullable<T>(ParseFormKey(value));

    private static void ApplyFlags(Fo4.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        if (proposal.NoUnderarmorScaling is { } noScale) addon.MajorFlags = noScale ? addon.MajorFlags | Fo4.ArmorAddon.MajorFlag.NoUnderarmorScaling : addon.MajorFlags & ~Fo4.ArmorAddon.MajorFlag.NoUnderarmorScaling;
        if (proposal.HiResFirstPersonOnly is { } hiRes) addon.MajorFlags = hiRes ? addon.MajorFlags | Fo4.ArmorAddon.MajorFlag.HiResFirstPersonOnly : addon.MajorFlags & ~Fo4.ArmorAddon.MajorFlag.HiResFirstPersonOnly;
    }

    private static void ApplySculpt(Fo4.ArmorAddon addon, ArmorAddonProposalArtifact proposal)
    {
        if (proposal.HasSculptData == false) { addon.BoneData = new GenderedItem<ExtendedList<Fo4.Bone>?>([], []); return; }
        if (proposal.Sculpt.IsDefaultOrEmpty) return;
        var male = new ExtendedList<Fo4.Bone>();
        var female = new ExtendedList<Fo4.Bone>();
        foreach (var row in proposal.Sculpt)
            (row.Gender == 0 ? male : female).Add(new Fo4.Bone { Name = row.BoneName, Values = [checked((float)row.DeltaX), checked((float)row.DeltaY), checked((float)row.DeltaZ)] });
        addon.BoneData = new GenderedItem<ExtendedList<Fo4.Bone>?>(male, female);
    }

    private static void VerifyFallout4(string path, ModKey sourceModKey, ModKey outputModKey, ArmorAddonProposalArtifact proposal, FormId sourceFormId, FormId targetFormId, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var expected = proposal.Mode == ArmorAddonProposalMode.Override ? new FormKey(sourceModKey, sourceFormId.Value) : new FormKey(outputModKey, targetFormId.Value);
        var records = mod.ArmorAddons.ToArray();
        var addon = records.FirstOrDefault(item => item.FormKey == expected);
        if (records.Length != 1 || addon is null || addon.EditorID != proposal.EditorId || !MatchesFallout4(addon, proposal))
            diagnostics.Add(new Diagnostic("armor-addon-binary-readback-mismatch", DiagnosticSeverity.Error, "Independent FO4 ARMA read-back did not match the proposal."));
    }

    private static void VerifySkyrim(string path, ModKey sourceModKey, ModKey outputModKey, ArmorAddonProposalArtifact proposal, FormId sourceFormId, FormId targetFormId, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
        var expected = proposal.Mode == ArmorAddonProposalMode.Override ? new FormKey(sourceModKey, sourceFormId.Value) : new FormKey(outputModKey, targetFormId.Value);
        var records = mod.ArmorAddons.ToArray();
        var addon = records.FirstOrDefault(item => item.FormKey == expected);
        if (records.Length != 1 || addon is null || addon.EditorID != proposal.EditorId || !MatchesSkyrim(addon, proposal))
            diagnostics.Add(new Diagnostic("armor-addon-binary-readback-mismatch", DiagnosticSeverity.Error, "Independent Skyrim ARMA read-back did not match the proposal."));
    }

    private static bool MatchesFallout4(Fo4.IArmorAddonGetter addon, ArmorAddonProposalArtifact proposal) =>
        (!HasChanged(proposal, "slotMask") || addon.BodyTemplate?.FirstPersonFlags == (Fo4.BipedObjectFlag)proposal.SlotMask.GetValueOrDefault()) &&
        (!HasChanged(proposal, "race") || addon.Race?.FormKey == ParseFormKey(proposal.Race!)) &&
        (!HasChanged(proposal, "footstepSet") || addon.FootstepSound?.FormKey == ParseFormKey(proposal.FootstepSet!)) &&
        (!HasChanged(proposal, "artObject") || addon.ArtObject?.FormKey == ParseFormKey(proposal.ArtObject!)) &&
        (!HasChanged(proposal, "detectionSound") || addon.DetectionSoundValue == proposal.DetectionSound.GetValueOrDefault()) &&
        (!HasChanged(proposal, "weaponAdjust") || Math.Abs(addon.WeaponAdjust - proposal.WeaponAdjust!.Value) < 0.0001f) &&
        (!HasChanged(proposal, "additionalRaces") || addon.AdditionalRaces.Select(item => item.FormKey).SequenceEqual(proposal.AdditionalRaces.Select(ParseFormKey))) &&
        (!HasChanged(proposal, "maleModel") || addon.WorldModel?.Male?.File == proposal.MaleModel) &&
        (!HasChanged(proposal, "femaleModel") || addon.WorldModel?.Female?.File == proposal.FemaleModel) &&
        (!HasChanged(proposal, "maleModelFlags") || addon.WorldModel?.Male?.Flags == (Fo4.Model.Flag)proposal.MaleModelFlags.GetValueOrDefault()) &&
        (!HasChanged(proposal, "femaleModelFlags") || addon.WorldModel?.Female?.Flags == (Fo4.Model.Flag)proposal.FemaleModelFlags.GetValueOrDefault()) &&
        (!HasChanged(proposal, "maleColorRemapIndex") || Math.Abs((addon.WorldModel?.Male?.ColorRemappingIndex ?? float.NaN) - proposal.MaleColorRemapIndex!.Value) < 0.0001f) &&
        (!HasChanged(proposal, "femaleColorRemapIndex") || Math.Abs((addon.WorldModel?.Female?.ColorRemappingIndex ?? float.NaN) - proposal.FemaleColorRemapIndex!.Value) < 0.0001f) &&
        (!HasChanged(proposal, "maleMaterialSwap") || addon.WorldModel?.Male?.MaterialSwap?.FormKey == ParseFormKey(proposal.MaleMaterialSwap!)) &&
        (!HasChanged(proposal, "femaleMaterialSwap") || addon.WorldModel?.Female?.MaterialSwap?.FormKey == ParseFormKey(proposal.FemaleMaterialSwap!)) &&
        (!HasChanged(proposal, "maleFirstPersonModel") || addon.FirstPersonModel?.Male?.File == proposal.MaleFirstPersonModel) &&
        (!HasChanged(proposal, "femaleFirstPersonModel") || addon.FirstPersonModel?.Female?.File == proposal.FemaleFirstPersonModel) &&
        (!HasChanged(proposal, "maleFirstPersonMaterialSwap") || addon.FirstPersonModel?.Male?.MaterialSwap?.FormKey == ParseFormKey(proposal.MaleFirstPersonMaterialSwap!)) &&
        (!HasChanged(proposal, "femaleFirstPersonMaterialSwap") || addon.FirstPersonModel?.Female?.MaterialSwap?.FormKey == ParseFormKey(proposal.FemaleFirstPersonMaterialSwap!)) &&
        (!HasChanged(proposal, "maleSkinTexture") || addon.SkinTexture?.Male?.FormKey == ParseFormKey(proposal.MaleSkinTexture!)) &&
        (!HasChanged(proposal, "femaleSkinTexture") || addon.SkinTexture?.Female?.FormKey == ParseFormKey(proposal.FemaleSkinTexture!)) &&
        (!HasChanged(proposal, "maleSkinTextureSwapList") || addon.TextureSwapList?.Male?.FormKey == ParseFormKey(proposal.MaleSkinTextureSwapList!)) &&
        (!HasChanged(proposal, "femaleSkinTextureSwapList") || addon.TextureSwapList?.Female?.FormKey == ParseFormKey(proposal.FemaleSkinTextureSwapList!)) &&
        (!HasChanged(proposal, "malePriority") || addon.Priority?.Male == proposal.MalePriority) &&
        (!HasChanged(proposal, "femalePriority") || addon.Priority?.Female == proposal.FemalePriority) &&
        (!HasChanged(proposal, "maleWeightSliderFlags") || addon.WeightSliderEnabled?.Male == (proposal.MaleWeightSliderFlags.GetValueOrDefault() != 0)) &&
        (!HasChanged(proposal, "femaleWeightSliderFlags") || addon.WeightSliderEnabled?.Female == (proposal.FemaleWeightSliderFlags.GetValueOrDefault() != 0)) &&
        (!HasChanged(proposal, "noUnderarmorScaling") || addon.MajorFlags.HasFlag(Fo4.ArmorAddon.MajorFlag.NoUnderarmorScaling) == proposal.NoUnderarmorScaling) &&
        (!HasChanged(proposal, "hiResFirstPersonOnly") || addon.MajorFlags.HasFlag(Fo4.ArmorAddon.MajorFlag.HiResFirstPersonOnly) == proposal.HiResFirstPersonOnly) &&
        MatchesSculpt(addon.BoneData, proposal);

    private static bool MatchesSkyrim(Sse.IArmorAddonGetter addon, ArmorAddonProposalArtifact proposal) =>
        (!HasChanged(proposal, "slotMask") || addon.BodyTemplate?.FirstPersonFlags == (Sse.BipedObjectFlag)proposal.SlotMask.GetValueOrDefault()) &&
        MatchesSkyrimLink(addon.Race, proposal.Race,
            HasChanged(proposal, "race"), proposal.CompleteDocument) &&
        MatchesSkyrimLink(addon.FootstepSound, proposal.FootstepSet,
            HasChanged(proposal, "footstepSet"), proposal.CompleteDocument) &&
        MatchesSkyrimLink(addon.ArtObject, proposal.ArtObject,
            HasChanged(proposal, "artObject"), proposal.CompleteDocument) &&
        (!HasChanged(proposal, "detectionSound") || addon.DetectionSoundValue == proposal.DetectionSound.GetValueOrDefault()) &&
        (!HasChanged(proposal, "weaponAdjust") || Math.Abs(addon.WeaponAdjust - proposal.WeaponAdjust!.Value) < 0.0001f) &&
        (!HasChanged(proposal, "additionalRaces") || addon.AdditionalRaces.Select(item => item.FormKey).SequenceEqual(proposal.AdditionalRaces.Select(ParseFormKey))) &&
        MatchesSkyrimModel(addon.WorldModel?.Male, proposal.MaleModel,
            HasChanged(proposal, "maleModel"), proposal.CompleteDocument) &&
        MatchesSkyrimModel(addon.WorldModel?.Female, proposal.FemaleModel,
            HasChanged(proposal, "femaleModel"), proposal.CompleteDocument) &&
        MatchesSkyrimModel(addon.FirstPersonModel?.Male,
            proposal.MaleFirstPersonModel,
            HasChanged(proposal, "maleFirstPersonModel"),
            proposal.CompleteDocument) &&
        MatchesSkyrimModel(addon.FirstPersonModel?.Female,
            proposal.FemaleFirstPersonModel,
            HasChanged(proposal, "femaleFirstPersonModel"),
            proposal.CompleteDocument) &&
        (!HasChanged(proposal, "malePriority") || addon.Priority?.Male == proposal.MalePriority) &&
        (!HasChanged(proposal, "femalePriority") || addon.Priority?.Female == proposal.FemalePriority) &&
        MatchesSkyrimLink(addon.SkinTexture?.Male,
            proposal.MaleSkinTexture,
            HasChanged(proposal, "maleSkinTexture"),
            proposal.CompleteDocument) &&
        MatchesSkyrimLink(addon.SkinTexture?.Female,
            proposal.FemaleSkinTexture,
            HasChanged(proposal, "femaleSkinTexture"),
            proposal.CompleteDocument) &&
        MatchesSkyrimLink(addon.TextureSwapList?.Male,
            proposal.MaleSkinTextureSwapList,
            HasChanged(proposal, "maleSkinTextureSwapList"),
            proposal.CompleteDocument) &&
        MatchesSkyrimLink(addon.TextureSwapList?.Female,
            proposal.FemaleSkinTextureSwapList,
            HasChanged(proposal, "femaleSkinTextureSwapList"),
            proposal.CompleteDocument) &&
        (!HasChanged(proposal, "maleWeightSliderFlags") || addon.WeightSliderEnabled?.Male == (proposal.MaleWeightSliderFlags.GetValueOrDefault() != 0)) &&
        (!HasChanged(proposal, "femaleWeightSliderFlags") || addon.WeightSliderEnabled?.Female == (proposal.FemaleWeightSliderFlags.GetValueOrDefault() != 0));

    private static bool MatchesSkyrimLink<T>(
        IFormLinkNullableGetter<T>? actual,
        string? expected,
        bool changed,
        bool completeDocument)
        where T : class, IMajorRecordGetter
    {
        if (!changed) return true;
        if (expected is not null)
            return actual?.FormKey == ParseFormKey(expected);
        return !completeDocument || actual is null || actual.IsNull;
    }

    private static bool MatchesSkyrimModel(
        Sse.IModelGetter? actual,
        string? expected,
        bool changed,
        bool completeDocument)
    {
        if (!changed) return true;
        if (expected is not null) return actual?.File == expected;
        return !completeDocument || actual is null;
    }

    private static bool MatchesSculpt(IGenderedItemGetter<IReadOnlyList<Fo4.IBoneGetter>?>? actual, ArmorAddonProposalArtifact proposal)
    {
        if (!HasChanged(proposal, "sculpt") && !HasChanged(proposal, "hasSculptData")) return true;
        if (proposal.HasSculptData == false) return actual?.Male is not { Count: > 0 } && actual?.Female is not { Count: > 0 };
        var rows = proposal.Sculpt;
        return MatchesSculptGender(actual?.Male, rows, 0) && MatchesSculptGender(actual?.Female, rows, 1);
    }

    private static bool MatchesSculptGender(IReadOnlyList<Fo4.IBoneGetter>? actual, ImmutableArray<ArmorAddonSculptArtifact> rows, byte gender)
    {
        var expected = rows.Where(row => row.Gender == gender).ToArray();
        if (actual is null) return expected.Length == 0;
        return actual.Count == expected.Length && actual.Zip(expected).All(pair => pair.First.Name == pair.Second.BoneName &&
            pair.First.Values is { Count: >= 3 } values &&
            Math.Abs(values[0] - pair.Second.DeltaX) < 0.0001f &&
            Math.Abs(values[1] - pair.Second.DeltaY) < 0.0001f &&
            Math.Abs(values[2] - pair.Second.DeltaZ) < 0.0001f);
    }

    private static bool HasUnsupportedSkyrimFields(ArmorAddonProposalArtifact proposal) =>
        proposal.MaleModelFlags is not null || proposal.FemaleModelFlags is not null || proposal.MaleColorRemapIndex is not null || proposal.FemaleColorRemapIndex is not null ||
        proposal.MaleMaterialSwap is not null || proposal.FemaleMaterialSwap is not null || proposal.MaleFirstPersonMaterialSwap is not null || proposal.FemaleFirstPersonMaterialSwap is not null ||
        proposal.Sculpt.Any() || proposal.NoUnderarmorScaling is not null || proposal.HasSculptData is not null || proposal.HiResFirstPersonOnly is not null;

    private static bool HasAny(ArmorAddonProposalArtifact proposal, params string[] fields) => fields.Any(field => HasChanged(proposal, field));
    private static bool HasChanged(ArmorAddonProposalArtifact proposal, string field) => proposal.ChangedFields.Contains(field, StringComparer.Ordinal);
    private static void ValidateMasters(ImmutableArray<FormReference> references, ModKey source, IEnumerable<ModKey> declaredMasters)
    {
        var allowed = declaredMasters.Append(source).ToHashSet();
        foreach (var reference in references)
        {
            var plugin = new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin);
            if (!allowed.Contains(plugin)) throw new InvalidDataException($"Reference '{reference}' is not provided by the source plugin or its declared masters.");
        }
    }

    private static void AddMasters(ExtendedList<MasterReference> masters, ModKey source, ImmutableArray<FormReference> references, ModKey output, IEnumerable<ModKey> sourceMasters)
    {
        foreach (var key in new[] { source }.Concat(sourceMasters).Concat(references.Select(ToFormKey).Select(key => key.ModKey)).Where(key => key != output).Distinct())
            masters.Add(new MasterReference { Master = key });
    }

    private ImmutableArray<Diagnostic> ValidatePaths(ArmorAddonBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-outside-lab", DiagnosticSeverity.Error, "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-addon-binary-output-outside-lab", DiagnosticSeverity.Error, "The output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("armor-addon-binary-output-extension", DiagnosticSeverity.Error, "The bounded ARMA writer emits ordinary .esp plugins only."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("armor-addon-binary-output-exists", DiagnosticSeverity.Error, "Binary armor-addon writes never overwrite an existing plugin."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("armor-addon-binary-output-parent", DiagnosticSeverity.Error, "The output plugin directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("armor-addon-binary-proposal-missing", DiagnosticSeverity.Error, "The armor-addon proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private WorkspacePath? TryWorkspacePath(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var path = new WorkspacePath(value);
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-addon-binary-source-outside-lab", DiagnosticSeverity.Error, "The source path must remain under the K-only lab root."));
            return path;
        }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("armor-addon-binary-source-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static ModKey? TryPluginName(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { return new ModKey(Path.GetFileNameWithoutExtension(value), ModType.Plugin); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("armor-addon-binary-plugin-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static FormKey ParseFormKey(string value) => FormReference.TryParse(value, out var reference) ? ToFormKey(reference) : throw new InvalidDataException($"Invalid ARMA FormReference '{value}'.");
    private static FormKey ToFormKey(FormReference reference) => new(new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin), reference.FormId.Value);
    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    private static void WriteMod(IModGetter mod, string destination) => mod.WriteToBinary(new FilePath(destination), new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck });
    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("armor-addon-binary-reparse", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("armor-addon-binary-path-inspection", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ArmorAddonBinaryWriteResult Refused(ArmorAddonBinaryWriteRequest request, FormId? target, ImmutableArray<Diagnostic> diagnostics) => new(false, request.Proposal, request.Output, target, null, diagnostics);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
