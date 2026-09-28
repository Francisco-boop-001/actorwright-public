using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// One already-resolved Skyrim headpart model admitted to a product-owned
/// FaceGeom carrier. The first implementation deliberately accepts one
/// reachable dynamic shape per model and refuses more complex NIFs.
/// </summary>
public sealed record SseFaceGeomCarrierAssemblyPart(
    FormReference HeadPart,
    AssetPath SourcePath,
    Sha256Hash ExpectedSourceSha256,
    ImmutableArray<byte> SourceBytes,
    string OutputShapeName,
    bool UsesFaceTint,
    ImmutableArray<Vector3> BakedPositions = default,
    ImmutableArray<byte> ExpectedPackedNormalBytes = default)
{
    public ImmutableArray<string> TextureSetOverride { get; init; } = [];
    public uint? HairTintPackedRgb { get; init; }
}

public enum SseFaceGeomCarrierSkeletonAuthority
{
    SourceModelWorldTranslations = 0,
    IdentityFaceGenBones = 1
}

public sealed record SseFaceGeomCarrierAssemblyRequest(
    ImmutableArray<SseFaceGeomCarrierAssemblyPart> Parts,
    AssetPath FaceTintPath,
    SseFaceGeomCarrierSkeletonAuthority SkeletonAuthority =
        SseFaceGeomCarrierSkeletonAuthority.SourceModelWorldTranslations);

public sealed record SseFaceGeomCarrierAssemblyShape(
    FormReference HeadPart,
    AssetPath SourcePath,
    Sha256Hash SourceSha256,
    string SourceShapeName,
    string OutputShapeName,
    int VertexCount,
    Sha256Hash TopologySha256,
    Sha256Hash SourcePositionSha256,
    Sha256Hash OutputPositionSha256,
    bool PositionsChanged,
    bool UsesFaceTint);

/// <summary>
/// Reparsed product bytes. RuntimeAuthority is always false;
/// game loadability and rendering remain separate authorities.
/// </summary>
public sealed record SseFaceGeomCarrierAssemblyArtifact(
    ImmutableArray<byte> Bytes,
    Sha256Hash Sha256,
    int ByteLength,
    int BlockCount,
    AssetPath FaceTintPath,
    ImmutableArray<SseFaceGeomCarrierAssemblyShape> Shapes,
    bool RuntimeAuthority);

public sealed record SseFaceGeomCarrierAssemblyResult(
    bool Assembled,
    bool Verified,
    SseFaceGeomCarrierAssemblyArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISseFaceGeomCarrierAssembler
{
    SseFaceGeomCarrierAssemblyResult Assemble(
        SseFaceGeomCarrierAssemblyRequest request);
}
