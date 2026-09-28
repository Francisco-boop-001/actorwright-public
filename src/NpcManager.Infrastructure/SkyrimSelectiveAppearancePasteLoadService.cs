using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reconstructs both selective-paste endpoints from reviewed NPC records and
/// explicit RaceMenu presets. No sidecar is inferred from an actor or save.
/// </summary>
public sealed class SkyrimSelectiveAppearancePasteLoadService(
    ISkyrimFaceEditLoadService faceLoadService,
    IPresetService presetService,
    ISkyrimSelectiveAppearanceNpcStateReader npcStateReader)
    : ISkyrimSelectiveAppearancePasteLoadService
{
    public async ValueTask<SkyrimSelectiveAppearancePasteLoadResult> LoadAsync(
        SkyrimSelectiveAppearancePasteLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Intake.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error(
                "selective-paste-load-edition",
                "A reviewed Skyrim SE/AE intake is required."));
            return Refused(diagnostics);
        }

        SkyrimFaceEditLoadResult sourceFace = await faceLoadService.LoadAsync(
            new SkyrimFaceEditLoadRequest(
                request.Intake,
                request.SourcePlugin,
                request.SourceNpcFormId),
            cancellationToken).ConfigureAwait(false);
        SkyrimFaceEditLoadResult targetFace = await faceLoadService.LoadAsync(
            new SkyrimFaceEditLoadRequest(
                request.Intake,
                request.TargetPlugin,
                request.TargetNpcFormId),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(sourceFace.Diagnostics);
        diagnostics.AddRange(targetFace.Diagnostics);

        PresetParseResult sourcePreset = await presetService.InspectAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                request.SourcePreset),
            cancellationToken).ConfigureAwait(false);
        PresetParseResult targetPreset = await presetService.InspectAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                request.TargetPreset),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(sourcePreset.Diagnostics);
        diagnostics.AddRange(targetPreset.Diagnostics);

        if (sourceFace.State is null || targetFace.State is null ||
            sourcePreset.Document is null || targetPreset.Document is null ||
            !sourceFace.Accepted || !targetFace.Accepted ||
            !sourcePreset.Document.IsValid || !targetPreset.Document.IsValid)
        {
            diagnostics.Add(Error(
                "selective-paste-load-incomplete",
                "Both reviewed NPC records and both explicit RaceMenu presets must load without errors."));
            return Refused(diagnostics);
        }
        ValidatePreset(sourcePreset.Document, "source", diagnostics);
        ValidatePreset(targetPreset.Document, "target", diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        try
        {
            SkyrimSelectiveAppearancePasteEndpoint source = BuildEndpoint(
                request.SourcePlugin,
                sourceFace.State,
                request.SourcePreset,
                sourcePreset.Document,
                npcStateReader.ReadOutfits(
                    sourceFace.State.SourcePluginPath,
                    sourceFace.State.TargetFormId));
            SkyrimSelectiveAppearancePasteEndpoint target = BuildEndpoint(
                request.TargetPlugin,
                targetFace.State,
                request.TargetPreset,
                targetPreset.Document,
                npcStateReader.ReadOutfits(
                    targetFace.State.SourcePluginPath,
                    targetFace.State.TargetFormId));
            diagnostics.AddRange(
                SkyrimFaceEditorDocumentRules.Validate(source.Document.Face).Diagnostics);
            diagnostics.AddRange(
                SkyrimBodyEditorDocumentRules.Validate(source.Document.Body).Diagnostics);
            diagnostics.AddRange(
                SkyrimFaceEditorDocumentRules.Validate(target.Document.Face).Diagnostics);
            diagnostics.AddRange(
                SkyrimBodyEditorDocumentRules.Validate(target.Document.Body).Diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);
            return new SkyrimSelectiveAppearancePasteLoadResult(
                true,
                new SkyrimSelectiveAppearancePasteLoadedState(source, target),
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           KeyNotFoundException)
        {
            diagnostics.Add(Error(
                "selective-paste-load-record-state",
                $"The complete endpoint state could not be reconstructed: {exception.Message}"));
            return Refused(diagnostics);
        }
    }

    private static SkyrimSelectiveAppearancePasteEndpoint BuildEndpoint(
        PluginName plugin,
        SkyrimFaceEditLoadedState face,
        WorkspacePath presetPath,
        PresetDocument preset,
        NpcOutfitSnapshot outfits)
    {
        PresetAppearance sidecar = preset.Appearance;
        RaceMenuPresetData raceMenu = sidecar.RaceMenu ??
            throw new InvalidDataException(
                "A complete selective-paste preset requires typed RaceMenu data.");
        ImmutableArray<BodySlideSliderValue> bodySlide = sidecar.BodyMorphs
            .Where(item => Math.Abs(item.Value) >=
                           SkyrimBodyEditorDocumentRules.BodySlideZeroEpsilon)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new BodySlideSliderValue(item.Key, item.Value))
            .ToImmutableArray();
        SkyrimFaceEditorDocument faceDocument = face.Document with
        {
            CustomMorphs = sidecar.OrderedCustomMorphs,
            SculptParts = raceMenu.SculptParts,
            BodyOverlays = raceMenu.BodyOverlays
        };
        var bodyDocument = new SkyrimBodyEditorDocument(
            face.WriterBaseline.Appearance.Weight,
            bodySlide,
            raceMenu.NodeTransforms,
            raceMenu.SkinOverrides,
            raceMenu.BodyOverlays);
        var document = new SkyrimSelectiveAppearancePasteDocument(
            faceDocument,
            bodyDocument,
            outfits);
        return new SkyrimSelectiveAppearancePasteEndpoint(
            plugin,
            face.SourcePluginPath,
            face.SourcePluginSha256,
            face.TargetFormId,
            face.SourceEditorId,
            face.Race,
            face.Sex,
            face.WriterBaseline.Appearance,
            face.WriterBaseline.RuntimeAppearance,
            presetPath,
            preset,
            document);
    }

    private static void ValidatePreset(
        PresetDocument preset,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!preset.Appearance.UnknownFields.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                $"selective-paste-{role}-preset-unknown",
                $"The {role} preset contains unsupported fields that the canonical writer cannot preserve."));
        if (preset.Appearance.RaceMenu is null)
            diagnostics.Add(Error(
                $"selective-paste-{role}-preset-racemenu",
                $"The {role} preset does not contain a typed RaceMenu carrier."));
        if (preset.Appearance.Weight is null)
            diagnostics.Add(Error(
                $"selective-paste-{role}-preset-weight",
                $"The {role} preset does not contain actor.weight."));
        if (preset.Appearance.OrderedCustomMorphs.IsDefault)
            diagnostics.Add(Error(
                $"selective-paste-{role}-preset-custom-order",
                $"The {role} preset did not preserve custom morph order."));
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimSelectiveAppearancePasteLoadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
