using System.Collections.Immutable;
using System.Numerics;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

public sealed partial class SseFaceGeomCarrierAssembler
{
    private sealed record AdmittedPart(
        SseFaceGeomCarrierAssemblyPart Request,
        SseNifDocument Document,
        SseNifBlock Root,
        SseNifBlock HeadNode,
        SseNifBlock? SpineNode,
        ImmutableArray<SseNifBlock> BoneNodes,
        SseNifBlock Shape,
        SseNifBlock Shader,
        SseNifBlock TextureSet,
        ImmutableArray<SseNifBlock> Closure);

    private sealed record CarrierOutputBlock(
        string Type,
        string? Name,
        byte[] Bytes,
        int? NameIndexOffset);

    private sealed record ShapeBinding(
        AdmittedPart Part,
        int OutputBlockIndex);

    private sealed record OutputBoneBinding(
        string Name,
        int OutputBlockIndex,
        Vector3 Translation);

    private sealed record BuiltCarrier(
        ImmutableArray<CarrierOutputBlock> Blocks,
        ImmutableArray<OutputBoneBinding> Bones,
        int FaceNodeIndex,
        ImmutableArray<ShapeBinding> Shapes);
}
