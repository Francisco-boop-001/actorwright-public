using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Complete read-only source record required to seed a face-editor transaction
/// and later preserve every appearance field owned by the verified writer.
/// </summary>
public sealed record SkyrimFaceEditSourceSnapshot(
    EditorId SourceEditorId,
    FormReference Race,
    NpcSex Sex,
    ImmutableArray<FormReference> OrderedHeadParts,
    FormReference HairColor,
    FormReference? HeadTexture,
    bool IsCharGenFacePreset,
    float? Weight,
    SkyrimFaceMorphSnapshot FaceMorphs,
    SkyrimFaceTintPatch FaceTints,
    SkyrimQnamRgb? Qnam,
    ImmutableArray<string> ScriptNames);

/// <summary>Read-only request for one reviewed copied-workspace face transaction.</summary>
public sealed record SkyrimFaceEditLoadRequest(
    ReviewedGameIntake Intake,
    PluginName SourcePlugin,
    FormId TargetFormId);

public sealed record SkyrimFaceEditCatalogs(
    FormChoiceSearchResult TypedForms,
    ImmutableDictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult> HeadParts,
    ImmutableDictionary<SkyrimRaceMenuPaintCategory, SkyrimRaceMenuPaintChoiceResult> Paints,
    SkyrimRaceMenuSliderCatalog? SliderCatalog,
    ImmutableArray<NpcHeadPartSelection> RaceDefaultHeadParts);

public sealed record SkyrimFaceEditLoadedState(
    WorkspacePath SourcePluginPath,
    Sha256Hash SourcePluginSha256,
    FormId TargetFormId,
    EditorId SourceEditorId,
    FormReference Race,
    string RaceEditorId,
    NpcSex Sex,
    SkyrimFaceEditorDocument Document,
    SkyrimFaceEditWriterBaseline WriterBaseline,
    SkyrimFaceEditCatalogs Catalogs);

public sealed record SkyrimFaceEditLoadResult(
    bool Accepted,
    SkyrimFaceEditLoadedState? State,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceEditLoadService
{
    ValueTask<SkyrimFaceEditLoadResult> LoadAsync(
        SkyrimFaceEditLoadRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact, read-only NPC appearance authority used by the aggregate face-edit
/// loader. Implementations must report source absence rather than inventing
/// writer-owned values.
/// </summary>
public interface ISkyrimFaceEditSourceReader
{
    SkyrimFaceEditSourceSnapshot Read(
        WorkspacePath pluginPath,
        FormId targetFormId);
}

/// <summary>
/// Exact writer authority captured with the immutable document before the
/// editor opens. Values not represented by the document remain source-owned.
/// </summary>
public sealed record SkyrimFaceEditWriterBaseline(
    SkyrimFaceEditorDocument Document,
    FullyAuthoredSkyrimNpcAppearanceSource Appearance,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance);

/// <summary>
/// Result of projecting one accepted editor document into the existing
/// appearance writer. A null appearance means that no writer request may be
/// created.
/// </summary>
public sealed record SkyrimFaceEditProjectionResult(
    bool Accepted,
    FullyAuthoredSkyrimNpcAppearanceSource? Appearance,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Fail-closed bridge between the complete editor transaction and the bounded
/// plugin writer. Sidecar-only fields may be inspected in the editor but may
/// not be silently discarded by this projection.
/// </summary>
public static class SkyrimFaceEditProjector
{
    public static SkyrimFaceEditProjectionResult Project(
        SkyrimFaceEditWriterBaseline baseline,
        SkyrimFaceEditorDocument accepted)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(accepted);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SkyrimFaceEditorValidationResult baselineValidation =
            SkyrimFaceEditorDocumentRules.Validate(baseline.Document);
        SkyrimFaceEditorValidationResult acceptedValidation =
            SkyrimFaceEditorDocumentRules.Validate(accepted);
        diagnostics.AddRange(baselineValidation.Diagnostics);
        diagnostics.AddRange(acceptedValidation.Diagnostics);

        if (baseline.Document.Parts.IsCharGenFacePreset !=
            accepted.Parts.IsCharGenFacePreset)
        {
            diagnostics.Add(Error(
                "face-edit-chargen-flag-unsupported",
                "The verified appearance writer does not author the editor's ACBS CharGen face-preset flag."));
        }
        if (!accepted.CustomMorphs.SequenceEqual(baseline.Document.CustomMorphs))
        {
            diagnostics.Add(Error(
                "face-edit-custom-morph-sidecar-only",
                "Changed RaceMenu custom morphs require a verified FaceGen or preset-sidecar transaction."));
        }
        if (!SculptEquivalent(accepted.SculptParts, baseline.Document.SculptParts))
        {
            diagnostics.Add(Error(
                "face-edit-sculpt-sidecar-only",
                "Changed RaceMenu sculpt data requires a verified FaceGen or preset-sidecar transaction."));
        }
        if (!OverlaysEquivalent(accepted.BodyOverlays, baseline.Document.BodyOverlays))
        {
            diagnostics.Add(Error(
                "face-edit-overlay-runtime-only",
                "Changed face overlays require a complete verified runtime payload and cannot be flattened into NPC fields."));
        }
        if (!MaskOverridesEquivalent(accepted.Tints, baseline.Document.Tints))
        {
            diagnostics.Add(Error(
                "face-edit-mask-sidecar-only",
                "Changed RaceMenu tint-mask paths have no NPC-record home and require a verified preset sidecar."));
        }
        if (accepted.Parts.HairColor.IsSpecified &&
            accepted.Parts.HairColor.Value is null)
        {
            diagnostics.Add(Error(
                "face-edit-hair-clear-unsupported",
                "The complete appearance writer requires a typed HCLF source and cannot represent an explicit clear."));
        }
        if (accepted.Parts.HeadTexture is null)
        {
            diagnostics.Add(Error(
                "face-edit-head-texture-required",
                "The complete appearance writer requires a typed FTST source."));
        }

        if (HasErrors(diagnostics))
        {
            return new SkyrimFaceEditProjectionResult(
                false,
                null,
                null,
                diagnostics.ToImmutable());
        }

        ImmutableArray<SkyrimNpcHeadPartSource> headParts =
            accepted.Parts.OrderedHeadParts.SequenceEqual(
                baseline.Document.Parts.OrderedHeadParts)
                ? baseline.Appearance.OrderedHeadParts
                : accepted.Parts.OrderedHeadParts
                    .Select(item =>
                        (SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(item))
                    .ToImmutableArray();

        SkyrimNpcHairColorSource hairColor =
            accepted.Parts.HairColor == baseline.Document.Parts.HairColor
                ? baseline.Appearance.HairColor
                : new ExternalSkyrimNpcHairColor(
                    accepted.Parts.HairColor.Value!.Value);

        SkyrimNpcFaceTextureSetSource faceTexture =
            accepted.Parts.HeadTexture == baseline.Document.Parts.HeadTexture
                ? baseline.Appearance.FaceTextureSet
                : new ExternalSkyrimNpcFaceTextureSet(
                    accepted.Parts.HeadTexture!.Value);

        ImmutableArray<SkyrimFaceTintLayer> acceptedTints = accepted.Tints
            .Where(item => item.IsAuthored)
            .Select(item => item.Value)
            .ToImmutableArray();
        ImmutableArray<SkyrimFaceTintLayer> baselineTints = baseline.Document.Tints
            .Where(item => item.IsAuthored)
            .Select(item => item.Value)
            .ToImmutableArray();
        SkyrimFaceTintPatch faceTints = acceptedTints.SequenceEqual(baselineTints)
            ? baseline.Appearance.FaceTints
            : new SkyrimFaceTintPatch(acceptedTints);

        var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
            headParts,
            hairColor,
            faceTexture,
            baseline.Appearance.Weight,
            accepted.NativeMorphs,
            faceTints,
            baseline.Appearance.Qnam);
        return new SkyrimFaceEditProjectionResult(
            true,
            appearance,
            baseline.RuntimeAppearance,
            diagnostics.ToImmutable());
    }

    private static bool MaskOverridesEquivalent(
        ImmutableArray<SkyrimFaceEditorTintLayer> left,
        ImmutableArray<SkyrimFaceEditorTintLayer> right) =>
        !left.IsDefault && !right.IsDefault && left.Length == right.Length &&
        left.Zip(right).All(pair =>
            pair.First.Value.Index == pair.Second.Value.Index &&
            pair.First.RaceDefaultMask == pair.Second.RaceDefaultMask &&
            pair.First.MaskOverride == pair.Second.MaskOverride);

    private static bool SculptEquivalent(
        ImmutableArray<RaceMenuSculptPart> left,
        ImmutableArray<RaceMenuSculptPart> right) =>
        !left.IsDefault && !right.IsDefault && left.Length == right.Length &&
        left.Zip(right).All(pair =>
            string.Equals(pair.First.Host, pair.Second.Host, StringComparison.Ordinal) &&
            pair.First.VertexCount == pair.Second.VertexCount &&
            pair.First.HasVertexCount == pair.Second.HasVertexCount &&
            pair.First.HasData == pair.Second.HasData &&
            pair.First.Vertices.SequenceEqual(pair.Second.Vertices));

    private static bool OverlaysEquivalent(
        ImmutableArray<RaceMenuBodyOverlay> left,
        ImmutableArray<RaceMenuBodyOverlay> right) =>
        !left.IsDefault && !right.IsDefault && left.Length == right.Length &&
        left.Zip(right).All(pair =>
            string.Equals(pair.First.Node, pair.Second.Node, StringComparison.Ordinal) &&
            string.Equals(pair.First.Diffuse, pair.Second.Diffuse, StringComparison.Ordinal) &&
            string.Equals(pair.First.Normal, pair.Second.Normal, StringComparison.Ordinal) &&
            pair.First.Alpha == pair.Second.Alpha &&
            pair.First.Tint.SequenceEqual(pair.Second.Tint) &&
            Normalize(pair.First.Values).SequenceEqual(Normalize(pair.Second.Values)));

    private static ImmutableArray<RaceMenuValue> Normalize(
        ImmutableArray<RaceMenuValue> value) =>
        value.IsDefault ? ImmutableArray<RaceMenuValue>.Empty : value;

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
