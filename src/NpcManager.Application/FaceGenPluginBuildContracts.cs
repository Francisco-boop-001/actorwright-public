using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record FaceGenPluginTargetEntry(
    PluginName WinningPlugin,
    FormId NpcFormId,
    string ManifestPath);

public enum FaceGenPluginTargetDisposition
{
    Selected,
    Excluded
}

public sealed record FaceGenPluginTargetEntryResult(
    FaceGenPluginTargetDisposition Disposition,
    FaceGenBatchEntryStatus Status,
    string WinningPlugin,
    string NpcFormId,
    string ManifestPath,
    string? OutputRelativePath,
    int ValidHeadShapeCount,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record FaceGenPluginBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string TargetPlugin,
    string InputTargetManifestSha256,
    int Attempted,
    int Selected,
    int Excluded,
    int Passed,
    int Skipped,
    int Failed,
    ImmutableArray<FaceGenPluginTargetEntryResult> Entries);

public sealed record FaceGenPluginBuildRequest(
    GameEdition Edition,
    PluginName TargetPlugin,
    WorkspacePath TargetManifestPath,
    WorkspacePath OutputPath,
    bool StrictShapes = true);

public sealed record FaceGenPluginBuildResult(
    bool Written,
    bool HasFailures,
    FaceGenPluginBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenPluginBuildService
{
    ValueTask<FaceGenPluginBuildResult> BuildAsync(FaceGenPluginBuildRequest request,
        CancellationToken cancellationToken);
}
