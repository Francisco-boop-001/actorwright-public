using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal static class FaceGeomHairRegionsCliComposition
{
    public static FaceGeomHairRegionsCommandHandler Create(
        WorkspacePath workspaceRoot,
        IPreviewServiceFactory<FaceGeomHairRegionsPreviewServices>?
            previewFactory,
        TextWriter output,
        TextWriter error)
    {
        var boundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot);
        var documents =
            new FaceGeomHairRegionsDocumentCodec(boundary);
        return new FaceGeomHairRegionsCommandHandler(
            workspaceRoot,
            new FaceGeomHairRegionsAnalyzer(
                workspaceRoot,
                boundary),
            new FaceGeomHairRegionsProposer(
                workspaceRoot,
                documents),
            new FaceGeomHairRegionsApplyService(
                workspaceRoot,
                new BethesdaFaceGeomHairRegionsVerifier(),
                documents),
            documents,
            previewFactory ??
                new UnconfiguredFaceGeomHairRegionsPreviewFactory(),
            output,
            error);
    }
}

internal sealed class UnconfiguredFaceGeomHairRegionsPreviewFactory :
    IPreviewServiceFactory<FaceGeomHairRegionsPreviewServices>
{
    public ValueTask<PreviewServiceLease<FaceGeomHairRegionsPreviewServices>>
        CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new PreviewServiceLease<FaceGeomHairRegionsPreviewServices>(
                new FaceGeomHairRegionsPreviewServices(
                    new FaceGeomHairRegionsPreviewService(
                        new UnconfiguredFaceGeomHairRegionsRenderer()),
                    new UnconfiguredNpcVisualPreviewVisualValidator())));
    }
}

internal sealed class UnconfiguredNpcVisualPreviewVisualValidator :
    INpcVisualPreviewVisualValidator
{
    public ValueTask<NpcVisualPreviewVisualEvidence>
        ValidateAsync(
            NpcVisualPreviewView faceFront,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faceFront);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new NpcVisualPreviewVisualEvidence(
                0,
                0,
                0,
                0,
                false,
                [
                    new Diagnostic(
                        "preview-visual-validator-not-configured",
                        DiagnosticSeverity.Error,
                        "FaceGeom hair-region visual validation is not configured; Task 3 injects the admitted validator.")
                ]));
    }

    public ValueTask<NpcVisualPreviewVisualEvidence>
        ValidateEncodedAsync(
            NpcVisualPreviewView faceFront,
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken) =>
        ValidateAsync(
            faceFront,
            cancellationToken);
}

internal sealed class UnconfiguredFaceGeomHairRegionsRenderer :
    IFaceGeomHairRegionsRenderer
{
    public ValueTask<FaceGeomHairRegionsRenderResult> RenderAsync(
        FaceGeomHairRegionsRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new FaceGeomHairRegionsRenderResult(
                false,
                [],
                null,
                null,
                0,
                0,
                [
                    new Diagnostic(
                        "hair-regions-renderer-not-configured",
                        DiagnosticSeverity.Error,
                        "The real FaceGeom hair-region preview service is composed, but its Blender renderer is not configured.")
                ]));
    }
}

/// <summary>
/// Task 2 production boundary. Task 3 replaces this fail-closed service with
/// the real reviewed renderer; no render success is synthesized here.
/// </summary>
internal sealed class UnconfiguredFaceGeomHairRegionsPreviewService :
    IFaceGeomHairRegionsPreviewService
{
    public ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
        FaceGeomHairRegionsPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new FaceGeomHairRegionsPreviewResult(
                false,
                null,
                [],
                ImmutableArray.Create(
                    new Diagnostic(
                        "preview-not-configured",
                        DiagnosticSeverity.Error,
                        "FaceGeom hair-region preview is not configured; Task 3 supplies the truthful renderer.")),
                VisualAuthority: false,
                RuntimeAuthority: false));
    }
}
