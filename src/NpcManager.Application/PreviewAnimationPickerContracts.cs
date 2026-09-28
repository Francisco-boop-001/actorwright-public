using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Immutable caller context for one animation-picker session. NPC sex is a
/// target fact; whether to apply the sex filter remains a dialog choice.
/// </summary>
public sealed record PreviewAnimationPickerOpenRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    bool IsFemale,
    string CurrentAnimationPath = "");

/// <summary>
/// Hash-bound, fully parsed animation catalog. Filtering this value is pure
/// in-memory work and therefore cannot observe a changed manifest mid-dialog.
/// </summary>
public sealed record PreviewAnimationPickerSession(
    string SchemaVersion,
    string Edition,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    bool IsFemale,
    string CurrentAnimationPath,
    ImmutableArray<PreviewAnimationListItem> Items);

public sealed record PreviewAnimationPickerOpenResult(
    bool Succeeded,
    PreviewAnimationPickerSession? Session,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record PreviewAnimationPickerFilter(
    string Text,
    bool FilterByGender,
    bool ShowFirstPerson);

public sealed record PreviewAnimationPickerProjection(
    string SchemaVersion,
    Sha256Hash ManifestSha256,
    string Filter,
    bool FilterByGender,
    bool ShowFirstPerson,
    int TotalCount,
    int VisibleCount,
    ImmutableArray<PreviewAnimationTreeBranch> Groups);

public sealed record PreviewAnimationSelection(
    PreviewAnimationListItem Clip,
    Sha256Hash ManifestSha256);

public interface IPreviewAnimationPickerService
{
    ValueTask<PreviewAnimationPickerOpenResult> OpenAsync(
        PreviewAnimationPickerOpenRequest request,
        CancellationToken cancellationToken);

    PreviewAnimationPickerProjection Filter(
        PreviewAnimationPickerSession session,
        PreviewAnimationPickerFilter filter);
}
