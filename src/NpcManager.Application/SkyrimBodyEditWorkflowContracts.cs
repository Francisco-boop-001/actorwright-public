using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimBodyEditSourceSnapshot(
    EditorId SourceEditorId,
    float? Weight);

public sealed record SkyrimBodyEditLoadRequest(
    ReviewedGameIntake Intake,
    PluginName SourcePlugin,
    FormId TargetFormId);

public sealed record SkyrimBodyEditCatalogs(
    ImmutableArray<string> BodySlideNames,
    ImmutableArray<string> NodeNames,
    ImmutableDictionary<SkyrimRaceMenuPaintCategory, SkyrimRaceMenuPaintChoiceResult> Paints,
    ImmutableDictionary<BodyOverlayTarget, int> OverlaySlotLimits);

public sealed record SkyrimBodyEditWriterBaseline(
    SkyrimBodyEditorDocument Document);

public sealed record SkyrimBodyEditLoadedState(
    WorkspacePath SourcePluginPath,
    Sha256Hash SourcePluginSha256,
    FormId TargetFormId,
    EditorId SourceEditorId,
    SkyrimBodyEditorDocument Document,
    SkyrimBodyEditWriterBaseline WriterBaseline,
    SkyrimBodyEditCatalogs Catalogs);

public sealed record SkyrimBodyEditLoadResult(
    bool Accepted,
    SkyrimBodyEditLoadedState? State,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimBodyEditLoadService
{
    ValueTask<SkyrimBodyEditLoadResult> LoadAsync(
        SkyrimBodyEditLoadRequest request,
        CancellationToken cancellationToken);
}

public interface ISkyrimBodyEditSourceReader
{
    SkyrimBodyEditSourceSnapshot Read(
        WorkspacePath pluginPath,
        FormId targetFormId);
}

public sealed record SkyrimBodyEditProjectionResult(
    bool Accepted,
    NpcWeightPatch? WeightPatch,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Converts a complete accepted body document into the currently verified
/// plugin mutation surface. Every sidecar-only carrier must remain deeply
/// equivalent to its opening baseline or the projection is refused.
/// </summary>
public static class SkyrimBodyEditProjector
{
    public static SkyrimBodyEditProjectionResult Project(
        SkyrimBodyEditWriterBaseline baseline,
        SkyrimBodyEditorDocument accepted)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(accepted);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(SkyrimBodyEditorDocumentRules.Validate(
            baseline.Document).Diagnostics);
        diagnostics.AddRange(SkyrimBodyEditorDocumentRules.Validate(
            accepted).Diagnostics);

        if (!accepted.BodySlide.SequenceEqual(baseline.Document.BodySlide))
        {
            diagnostics.Add(Error(
                "body-edit-bodyslide-sidecar-only",
                "Changed BodySlide values require a verified RaceMenu preset or BodySlide transaction."));
        }
        if (!SectionEquivalent(
                baseline.Document,
                baseline.Document with { NodeTransforms = accepted.NodeTransforms }))
        {
            diagnostics.Add(Error(
                "body-edit-transform-sidecar-only",
                "Changed node transforms require a lossless verified RaceMenu sidecar writer."));
        }
        if (!SectionEquivalent(
                baseline.Document,
                baseline.Document with { SkinOverrides = accepted.SkinOverrides }))
        {
            diagnostics.Add(Error(
                "body-edit-skin-sidecar-only",
                "Changed skin overrides require a lossless verified RaceMenu sidecar writer."));
        }
        if (!SectionEquivalent(
                baseline.Document,
                baseline.Document with { BodyOverlays = accepted.BodyOverlays }))
        {
            diagnostics.Add(Error(
                "body-edit-overlay-sidecar-only",
                "Changed body paint requires a complete verified RaceMenu runtime or preset transaction."));
        }
        if (accepted.Weight.Equals(baseline.Document.Weight))
        {
            diagnostics.Add(Error(
                "body-edit-no-change",
                "The accepted body document does not contain a writer-representable change."));
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return new SkyrimBodyEditProjectionResult(
                false,
                null,
                diagnostics.ToImmutable());
        }

        return new SkyrimBodyEditProjectionResult(
            true,
            new NpcWeightPatch(accepted.Weight, null, null, null),
            diagnostics.ToImmutable());
    }

    private static bool SectionEquivalent(
        SkyrimBodyEditorDocument baseline,
        SkyrimBodyEditorDocument candidate) =>
        SkyrimBodyEditorDocumentRules.Equivalent(baseline, candidate);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
