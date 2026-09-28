using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Already-materialized, hash-bound bytes for one canonical Skyrim SE
/// headpart-model asset. Resolution of loose/archive winners is a separate
/// concern owned by the Assets layer.
/// </summary>
public sealed record SseSelectedHeadpartNifGeometryReadRequest(
    AssetPath SourcePath,
    Sha256Hash ExpectedSourceSha256,
    ImmutableArray<byte> Bytes);

public sealed record SseSelectedHeadpartTextureSlot(
    int Slot,
    AssetPath Path);

/// <summary>
/// One exact shape-owned shader binding. Triangle ordinals address the
/// corresponding shape's immutable <see cref="SseSelectedHeadpartNifRestShape.TriangleIndices"/>.
/// </summary>
public sealed record SseSelectedHeadpartMaterial(
    string MaterialIdentity,
    int FirstTriangleOrdinal,
    int TriangleCount,
    int ShaderBlockIndex,
    int TextureSetBlockIndex,
    ImmutableArray<AssetPath> TexturePaths)
{
    public int TextureSlotCount { get; init; }
    public ImmutableArray<SseSelectedHeadpartTextureSlot> TextureSlots { get; init; } = [];
    public bool AlphaTestEnabled { get; init; }
    public byte AlphaTestThreshold { get; init; }
}

/// <summary>
/// Affine NIF-rest-to-render placement for one selected skinned headpart.
/// The 3x3 linear component is row-major and the translation is applied
/// after it. This placement is derived from the shape, NiSkinData, and exact
/// bone bind transforms; it is not an inferred visual offset.
/// </summary>
public sealed record SseSelectedHeadpartNifPlacement(
    float M11,
    float M12,
    float M13,
    float M21,
    float M22,
    float M23,
    float M31,
    float M32,
    float M33,
    float TranslationX,
    float TranslationY,
    float TranslationZ)
{
    public static SseSelectedHeadpartNifPlacement Identity { get; } = new(
        1, 0, 0,
        0, 1, 0,
        0, 0, 1,
        0, 0, 0);

    public Vector3 TransformPoint(Vector3 point) =>
        TransformDirection(point) +
        new Vector3(TranslationX, TranslationY, TranslationZ);

    public Vector3 TransformDirection(Vector3 direction) => new(
        M11 * direction.X + M12 * direction.Y + M13 * direction.Z,
        M21 * direction.X + M22 * direction.Y + M23 * direction.Z,
        M31 * direction.X + M32 * direction.Y + M33 * direction.Z);

    public bool IsFinite =>
        float.IsFinite(M11) && float.IsFinite(M12) && float.IsFinite(M13) &&
        float.IsFinite(M21) && float.IsFinite(M22) && float.IsFinite(M23) &&
        float.IsFinite(M31) && float.IsFinite(M32) && float.IsFinite(M33) &&
        float.IsFinite(TranslationX) && float.IsFinite(TranslationY) &&
        float.IsFinite(TranslationZ);
}

/// <summary>
/// Typed evidence for the one CVEO packed-normal sentinel admitted by the
/// selected-headpart reader. The sentinel is valid only while every triangle
/// incident to the vertex remains exactly degenerate under the bound topology.
/// RawOffset is the absolute source-NIF byte offset of the four packed bytes.
/// </summary>
public sealed record SseSelectedHeadpartPackedNormalSentinel(
    int SourceBlockIndex,
    int PartitionIndex,
    int VertexIndex,
    int RawOffset,
    Sha256Hash TopologySha256,
    ImmutableArray<int> IncidentTriangleIndices,
    ImmutableArray<byte> RawBytes)
{
    public const string PolicyVersion = "cveo-zero-normal-degenerate-v1";
}

public static class SseSelectedHeadpartPackedNormalPolicy
{
    public const string Version =
        SseSelectedHeadpartPackedNormalSentinel.PolicyVersion;

    public static bool IsExactDegenerate(
        Vector3 first,
        Vector3 second,
        Vector3 third) =>
        Vector3.Cross(second - first, third - first) == Vector3.Zero;
}

/// <summary>
/// Exact NIF-rest positions and topology identity for one reachable dynamic
/// shape. Positions are read from packed float4 vertex data, never inferred
/// from a TRI file's BaseVertices array.
/// </summary>
public sealed record SseSelectedHeadpartNifRestShape(
    string Name,
    int SourceBlockIndex,
    int VertexCount,
    ImmutableArray<Vector3> RestPositions,
    Sha256Hash PackedPositionSha256,
    Sha256Hash TopologySha256)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsShaderlessDummy { get; init; }

    public ImmutableArray<int> TriangleIndices { get; init; } = [];
    public ImmutableArray<Vector2> TextureCoordinates { get; init; } = [];
    public ImmutableArray<Vector3> Normals { get; init; } = [];
    public ImmutableArray<SseSelectedHeadpartPackedNormalSentinel>
        PackedNormalSentinels { get; init; } = [];
    public ImmutableArray<byte> PackedNormalBytes { get; init; } = [];
    public ImmutableArray<SseSelectedHeadpartMaterial> Materials { get; init; } = [];
    public SseSelectedHeadpartNifPlacement RenderPlacement { get; init; } =
        SseSelectedHeadpartNifPlacement.Identity;
}

/// <summary>Strictly read geometry from one selected Skyrim SE headpart NIF.</summary>
public sealed record SseSelectedHeadpartNifGeometryDocument(
    AssetPath SourcePath,
    Sha256Hash SourceSha256,
    int SourceByteLength,
    int BlockCount,
    int ReachableBlockCount,
    ImmutableArray<SseSelectedHeadpartNifRestShape> Shapes)
{
    /// <summary>
    /// Canonical Data-relative DDS routes retained by reachable shader texture
    /// sets in this exact headpart model. These are dependency evidence, not
    /// package assets.
    /// </summary>
    public ImmutableArray<AssetPath> ReferencedTextures { get; init; } = [];
}

public sealed record SseSelectedHeadpartNifGeometryReadResult(
    bool Accepted,
    SseSelectedHeadpartNifGeometryDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISseSelectedHeadpartNifGeometryReader
{
    SseSelectedHeadpartNifGeometryReadResult Read(
        SseSelectedHeadpartNifGeometryReadRequest request);
}
