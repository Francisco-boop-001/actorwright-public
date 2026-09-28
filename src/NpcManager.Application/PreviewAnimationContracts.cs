using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Manifest-bound animation clip metadata used by the semantic preview.</summary>
public sealed record PreviewAnimationClip(
    string Id,
    AssetPath Path,
    AssetPath Skeleton,
    int FrameCount,
    float FramesPerSecond,
    bool Additive,
    string ClipName = "",
    ImmutableArray<string> Roles = default,
    string Category = "",
    string StateAxes = "",
    bool RequiresFemale = false,
    bool IsFirstPersonOnly = false,
    bool FromBehaviorGraph = true,
    string Folder = "");

/// <summary>Manifest-bound animation discovery filters matching the upstream picker.</summary>
public sealed record PreviewAnimationListRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    bool IsFemale,
    bool ShowFirstPerson,
    string? Filter = null);

/// <summary>One deterministic animation-discovery row after picker filters are applied.</summary>
public sealed record PreviewAnimationListItem(
    string Id,
    string ClipName,
    string Path,
    string Skeleton,
    int FrameCount,
    float FramesPerSecond,
    bool Additive,
    ImmutableArray<string> Roles,
    string Category,
    string StateAxes,
    bool RequiresFemale,
    bool IsFirstPersonOnly,
    bool FromBehaviorGraph,
    string Folder);

public sealed record PreviewAnimationListArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string InputManifestSha256,
    string Filter,
    bool IsFemale,
    bool ShowFirstPerson,
    int TotalCount,
    int VisibleCount,
    ImmutableArray<PreviewAnimationListItem> Items);

public sealed record PreviewAnimationListResult(
    bool Succeeded,
    PreviewAnimationListArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewAnimationListService
{
    ValueTask<PreviewAnimationListResult> ListAsync(
        PreviewAnimationListRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolved animation selection and playback state emitted with a scene artifact.</summary>
public sealed record PreviewSceneAnimation(
    string Id,
    string Path,
    string Skeleton,
    int Frame,
    float TimeSeconds,
    float PlaybackRate,
    bool Playing,
    bool Additive);
