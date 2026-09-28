using System.Collections.Immutable;

namespace NpcManager.Infrastructure;

internal sealed record NifTextureTarget(int BlockIndex, int SlotIndex, string OriginalPath);

internal sealed record SseNifReference(string Kind, int Target, int Offset = -1);

internal sealed record SseNifGeometryPayload(int Offset, int Length);

internal sealed record SseNifDynamicGeometryLayout(
    int BoundsOffset,
    int RadiusOffset,
    int VertexDataOffset,
    int VertexCount,
    int VertexStride);

internal sealed record SsePackedNormalExtraction(
    ImmutableArray<byte> PackedNormalBytes,
    ulong VertexDescription,
    int VertexSize,
    int NormalOffset,
    int VertexCount,
    int VertexDataOffset,
    int VertexDataLength);

internal sealed record SseNifBlock(
    int Index,
    string Type,
    string? Name,
    int Offset,
    int Size,
    ImmutableArray<SseNifReference> References,
    ImmutableArray<string> Textures,
    SseNifGeometryPayload? GeometryPayload,
    SseNifDynamicGeometryLayout? DynamicGeometry,
    int? NameIndexOffset = null);

internal sealed record SseNifDocument(
    byte[] Data,
    uint UserVersion,
    uint BethesdaStreamVersion,
    int BlockSizeTableOffset,
    int BlockDataOffset,
    int FooterOffset,
    ImmutableArray<string> TypeNames,
    ImmutableArray<string> Strings,
    ImmutableArray<int> Roots,
    ImmutableArray<SseNifBlock> Blocks);
