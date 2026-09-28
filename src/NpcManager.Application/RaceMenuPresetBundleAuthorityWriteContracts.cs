using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RaceMenuPresetBundleAuthorityWriteRequest(
    PresetDocument Preset,
    RaceMenuPresetCompanionExport Companion,
    BlankNpcProviderBindingRequest ProviderContext,
    RaceMenuPresetRecordAuthorityArtifact RecordAuthority,
    WorkspacePath CandidateRoot);

public sealed record RaceMenuPresetBundleAuthorityArtifact(
    string BundleId,
    RaceMenuNpcPresetBundle Bundle,
    RaceMenuNpcRuntimeRouteAuthority RuntimeRoutes,
    ImmutableArray<WorkspacePath> CreatedFiles);

public sealed record RaceMenuPresetBundleAuthorityWriteResult(
    bool Written,
    RaceMenuPresetBundleAuthorityArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetBundleAuthorityWriter
{
    ValueTask<RaceMenuPresetBundleAuthorityWriteResult> WriteAsync(
        RaceMenuPresetBundleAuthorityWriteRequest request,
        CancellationToken cancellationToken);
}
