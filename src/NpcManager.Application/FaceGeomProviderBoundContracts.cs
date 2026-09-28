using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Binds one sandbox FaceGeom bake to the canonical provider selected by an
/// explicit copied-Data load order. The NIF remains a K-local artifact; plugin
/// loadability, deployment, and runtime rendering are separate authorities.
/// </summary>
public sealed record FaceGeomProviderBoundBuildRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormId NpcFormId,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath OutputRoot,
    ImmutableArray<FaceGeomBinaryMorph> Morphs);

public sealed record FaceGeomProviderBoundBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string LocalFormId,
    string OriginatingPlugin,
    string WinningPlugin,
    ImmutableArray<PluginName> OverrideChain,
    string ProviderKind,
    AssetPath ProviderPath,
    AssetPath OutputPath,
    string ProviderSha256,
    FaceGeomBinaryBuildArtifact BuildArtifact);

public sealed record FaceGeomProviderBoundBuildResult(
    bool Written,
    FaceGeomProviderBoundBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGeomProviderBoundBuildService
{
    ValueTask<FaceGeomProviderBoundBuildResult> BuildAsync(
        FaceGeomProviderBoundBuildRequest request, CancellationToken cancellationToken);
}
