using NpcManager.Application;

namespace NpcManager.Rendering;

/// <summary>
/// Projects the typed animation-list result into the hierarchical tree used
/// by both the CLI and desktop picker. Parsing and filtering remain owned by
/// the list service; taxonomy projection has one shared implementation.
/// </summary>
public sealed class PreviewAnimationTreeService(
    IPreviewAnimationListService listService)
    : IPreviewAnimationTreeService
{
    public async ValueTask<PreviewAnimationTreeResult> BuildAsync(
        PreviewAnimationListRequest request,
        CancellationToken cancellationToken)
    {
        PreviewAnimationListResult listed = await listService.ListAsync(
            request, cancellationToken).ConfigureAwait(false);
        if (!listed.Succeeded || listed.Artifact is null)
            return new PreviewAnimationTreeResult(
                false, null, listed.Diagnostics);

        PreviewAnimationListArtifact artifact = listed.Artifact;
        var tree = new PreviewAnimationTreeArtifact(
            "1",
            "preview-animation-tree",
            artifact.Edition,
            artifact.InputManifestSha256,
            artifact.Filter,
            artifact.IsFemale,
            artifact.ShowFirstPerson,
            artifact.TotalCount,
            artifact.VisibleCount,
            PreviewAnimationTreeProjector.Project(artifact.Items));
        return new PreviewAnimationTreeResult(
            true, tree, listed.Diagnostics);
    }
}
