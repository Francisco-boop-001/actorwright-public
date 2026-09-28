using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record RaceMenuSelectedDependencyManifestReadResult(
    RaceMenuSelectedDependencyManifestArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuSelectedDependencyManifestReader
{
    ValueTask<RaceMenuSelectedDependencyManifestReadResult> ReadAsync(
        WorkspacePath manifestPath,
        Sha256Hash expectedHash,
        WorkspacePath packageRoot,
        CancellationToken cancellationToken);
}
