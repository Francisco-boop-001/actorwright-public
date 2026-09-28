using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceGenPackArchiveRole
{
    Main,
    Textures
}

public enum FaceGenPackSourceStatus
{
    Present,
    MissingRequired,
    MissingOptional
}

public sealed record FaceGenPackPlanEntry(
    string Kind,
    AssetPath SourcePath,
    AssetPath CanonicalEntryPath,
    FaceGenPackArchiveRole ArchiveRole,
    bool Required,
    FaceGenPackSourceStatus Status,
    long? Size,
    Sha256Hash? Sha256);

public sealed record FaceGenPackPlanArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string LocalFormId,
    string OriginatingPlugin,
    string WinningPlugin,
    string AnchorPlugin,
    string AnchorArchiveBaseName,
    bool DebugSandbox,
    bool UsesSharedNeutralDetail,
    bool WouldCommit,
    ImmutableArray<PluginName> OverrideChain,
    ImmutableArray<FaceGenPackPlanEntry> Entries);

public sealed record FaceGenPackPlanRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormId NpcFormId,
    ImmutableArray<PluginName> PluginOrder,
    PluginName AnchorPlugin,
    bool DebugSandbox = false,
    bool UsesSharedNeutralDetail = false);

public sealed record FaceGenPackPlanResult(
    bool Planned,
    FaceGenPackPlanArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenPackPlanService
{
    ValueTask<FaceGenPackPlanResult> PlanAsync(
        FaceGenPackPlanRequest request, CancellationToken cancellationToken);
}

public sealed record FaceGenPackFileArtifact(
    string RelativePath,
    string SourcePath,
    FaceGenPackArchiveRole ArchiveRole,
    long Size,
    Sha256Hash Sha256);

public sealed record FaceGenPackArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string AnchorPlugin,
    string OutputRoot,
    ImmutableArray<FaceGenPackFileArtifact> Files,
    bool NoWriteToSource,
    bool RuntimeProof);

public sealed record FaceGenPackRequest(
    FaceGenPackPlanRequest Plan,
    WorkspacePath OutputRoot);

public sealed record FaceGenPackResult(
    bool Written,
    WorkspacePath OutputRoot,
    FaceGenPackArtifact? Artifact,
    Sha256Hash? ManifestSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenPackService
{
    ValueTask<FaceGenPackResult> PackAsync(
        FaceGenPackRequest request, CancellationToken cancellationToken);
}
