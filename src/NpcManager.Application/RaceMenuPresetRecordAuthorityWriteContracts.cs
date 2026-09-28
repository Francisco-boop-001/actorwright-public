using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RaceMenuPresetRecordAuthorityWriteRequest(
    RaceMenuPresetRecordAuthorityDraft Draft,
    WorkspacePath Destination);

public sealed record RaceMenuPresetRecordAuthorityArtifact(
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    RaceMenuNpcRecordAuthority Authority);

public sealed record RaceMenuPresetRecordAuthorityWriteResult(
    bool Written,
    RaceMenuPresetRecordAuthorityArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetRecordAuthorityWriter
{
    ValueTask<RaceMenuPresetRecordAuthorityWriteResult> WriteAsync(
        RaceMenuPresetRecordAuthorityWriteRequest request,
        CancellationToken cancellationToken);
}
