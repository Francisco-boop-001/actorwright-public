using System.Collections.Immutable;

namespace NpcManager.Application;

/// <summary>One leaf in the deterministic animation-picker tree.</summary>
public sealed record PreviewAnimationTreeLeaf(
    string Label,
    PreviewAnimationListItem Clip);

/// <summary>
/// A role, folder, gesture root, or category in the animation-picker tree.
/// Folder and category nodes use <see cref="Children"/>; leaves remain
/// separate so the JSON contract never relies on weakly typed polymorphism.
/// </summary>
public sealed record PreviewAnimationTreeBranch(
    string Kind,
    string Name,
    int ClipCount,
    ImmutableArray<PreviewAnimationTreeBranch> Children,
    ImmutableArray<PreviewAnimationTreeLeaf> Leaves);

public sealed record PreviewAnimationTreeArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string InputManifestSha256,
    string Filter,
    bool IsFemale,
    bool ShowFirstPerson,
    int TotalCount,
    int VisibleCount,
    ImmutableArray<PreviewAnimationTreeBranch> Groups);

public sealed record PreviewAnimationTreeResult(
    bool Succeeded,
    PreviewAnimationTreeArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewAnimationTreeService
{
    ValueTask<PreviewAnimationTreeResult> BuildAsync(
        PreviewAnimationListRequest request,
        CancellationToken cancellationToken);
}
