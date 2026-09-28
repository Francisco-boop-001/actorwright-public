using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceGenBatchEntryStatus
{
    Passed,
    Skipped,
    Failed
}

public sealed record FaceGenBatchEntry(
    string ManifestPath,
    string? NpcFormId,
    FaceGenBatchEntryStatus Status,
    int ValidHeadShapeCount,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record FaceGenBatchArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string InputBatchSha256,
    int Attempted,
    int Passed,
    int Skipped,
    int Failed,
    ImmutableArray<FaceGenBatchEntry> Entries);

public sealed record FaceGenBatchRequest(
    GameEdition Edition,
    WorkspacePath BatchManifestPath,
    WorkspacePath OutputPath,
    bool StrictShapes = true);

public sealed record FaceGenBatchResult(
    bool Written,
    bool HasFailures,
    FaceGenBatchArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenBatchService
{
    ValueTask<FaceGenBatchResult> BuildAsync(FaceGenBatchRequest request, CancellationToken cancellationToken);
}
