using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>One typed headpart input carried by a FaceGeom build manifest.</summary>
public sealed record FaceGeomHeadPart(string EditorId, int PartType, string MeshPath);

/// <summary>One named morph value selected for a semantic FaceGeom build.</summary>
public sealed record FaceGeomMorphApplication(string Name, float Value);

/// <summary>One texture slot route retained in the build artifact.</summary>
public sealed record FaceGeomTextureRoute(string Slot, string Path, string Provider);

/// <summary>One shape decision in the deterministic FaceGeom build artifact.</summary>
public sealed record FaceGeomBuildShape(string Name, FaceGenShapeRole Role, string SourcePath,
    int VertexCount, string TopologySha256, bool Included, string Decision);

/// <summary>
/// A reproducible semantic FaceGeom build artifact. It is deliberately JSON rather
/// than a NIF byte writer until a vetted NIF codec is admitted to the workspace.
/// </summary>
public sealed record FaceGeomBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string InputManifestSha256,
    ImmutableArray<FaceGeomBuildShape> Shapes,
    ImmutableArray<FaceGeomHeadPart> HeadParts,
    ImmutableArray<FaceGeomMorphApplication> Morphs,
    ImmutableArray<FaceGeomTextureRoute> TextureRoutes);

public sealed record FaceGeomBuildRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    WorkspacePath OutputPath,
    FormId? NpcFormId,
    bool StrictShapes = true);

public sealed record FaceGeomBuildResult(
    bool Written,
    FaceGeomBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGeomBuildService
{
    ValueTask<FaceGeomBuildResult> BuildAsync(FaceGeomBuildRequest request, CancellationToken cancellationToken);
}
