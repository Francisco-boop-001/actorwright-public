using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Opens one immutable animation catalog and projects every subsequent picker
/// state from memory. This preserves the upstream cached-filter behavior while
/// retaining the replacement's exact input hash.
/// </summary>
public sealed class PreviewAnimationPickerService(
    IPreviewAnimationListService listService)
    : IPreviewAnimationPickerService
{
    public async ValueTask<PreviewAnimationPickerOpenResult> OpenAsync(
        PreviewAnimationPickerOpenRequest request,
        CancellationToken cancellationToken)
    {
        PreviewAnimationListResult listed = await listService.ListAsync(
            new PreviewAnimationListRequest(
                request.Edition,
                request.ManifestPath,
                IsFemale: true,
                ShowFirstPerson: true),
            cancellationToken).ConfigureAwait(false);
        if (!listed.Succeeded || listed.Artifact is null)
        {
            return new PreviewAnimationPickerOpenResult(
                false, null, listed.Diagnostics);
        }

        PreviewAnimationListArtifact artifact = listed.Artifact;
        if (artifact.VisibleCount != artifact.TotalCount ||
            artifact.Items.Length != artifact.TotalCount)
        {
            return new PreviewAnimationPickerOpenResult(
                false,
                null,
                listed.Diagnostics.Add(new Diagnostic(
                    "animation-picker-catalog-incomplete",
                    DiagnosticSeverity.Error,
                    "The one-time animation catalog did not retain every parsed clip.")));
        }

        var session = new PreviewAnimationPickerSession(
            "1",
            artifact.Edition,
            request.ManifestPath,
            new Sha256Hash(artifact.InputManifestSha256),
            request.IsFemale,
            request.CurrentAnimationPath?.Trim() ?? string.Empty,
            artifact.Items);
        return new PreviewAnimationPickerOpenResult(
            true, session, listed.Diagnostics);
    }

    public PreviewAnimationPickerProjection Filter(
        PreviewAnimationPickerSession session,
        PreviewAnimationPickerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(filter);
        string text = filter.Text?.Trim() ?? string.Empty;
        ImmutableArray<PreviewAnimationListItem> visible = session.Items
            .Where(item => PreviewAnimationItemFilter.PassesGender(
                item, session.IsFemale, filter.FilterByGender))
            .Where(item => PreviewAnimationItemFilter.PassesPerspective(
                item, filter.ShowFirstPerson))
            .Where(item => PreviewAnimationItemFilter.MatchesAll(item, text))
            .ToImmutableArray();
        return new PreviewAnimationPickerProjection(
            "1",
            session.ManifestSha256,
            text,
            filter.FilterByGender,
            filter.ShowFirstPerson,
            session.Items.Length,
            visible.Length,
            PreviewAnimationTreeProjector.Project(visible));
    }
}
