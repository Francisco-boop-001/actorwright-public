using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class SseFaceGeomCarrierAssembler
{
    private static void PatchShapePositions(
        byte[] blockBytes,
        SseNifBlock shape,
        ImmutableArray<Vector3> positions)
    {
        SseNifDynamicGeometryLayout layout = shape.DynamicGeometry ??
            throw Invalid($"Shape '{shape.Name}' lacks packed dynamic geometry.");
        int vertexOffset = checked(layout.VertexDataOffset - shape.Offset);
        for (int index = 0; index < positions.Length; index++)
        {
            int offset = checked(vertexOffset + index * layout.VertexStride);
            WriteSingle(blockBytes, offset, positions[index].X);
            WriteSingle(blockBytes, offset + sizeof(float), positions[index].Y);
            WriteSingle(blockBytes, offset + 2 * sizeof(float), positions[index].Z);
        }

        Vector3 minimum = positions[0];
        Vector3 maximum = positions[0];
        foreach (Vector3 position in positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        Vector3 center = (minimum + maximum) * 0.5f;
        float radius = 0f;
        foreach (Vector3 position in positions)
            radius = MathF.Max(radius, Vector3.Distance(center, position));
        if (!float.IsFinite(radius))
            throw Invalid($"Shape '{shape.Name}' baked bounds are non-finite.");
        int boundsOffset = checked(layout.BoundsOffset - shape.Offset);
        WriteSingle(blockBytes, boundsOffset, center.X);
        WriteSingle(blockBytes, boundsOffset + sizeof(float), center.Y);
        WriteSingle(blockBytes, boundsOffset + 2 * sizeof(float), center.Z);
        WriteSingle(blockBytes, boundsOffset + 3 * sizeof(float), radius);
    }

    private static (ImmutableArray<Vector3> Positions, Sha256Hash Sha256)
        ReadShapePositions(SseNifDocument document, SseNifBlock shape)
    {
        SseNifDynamicGeometryLayout layout = shape.DynamicGeometry ??
            throw Invalid($"Shape '{shape.Name}' lacks packed dynamic geometry.");
        var positions = ImmutableArray.CreateBuilder<Vector3>(layout.VertexCount);
        byte[] packed = new byte[checked(layout.VertexCount * 3 * sizeof(float))];
        for (int index = 0; index < layout.VertexCount; index++)
        {
            ReadOnlySpan<byte> source = document.Data.AsSpan(
                checked(layout.VertexDataOffset + index * layout.VertexStride),
                3 * sizeof(float));
            source.CopyTo(packed.AsSpan(index * 3 * sizeof(float)));
            positions.Add(new Vector3(
                ReadSingle(source, 0),
                ReadSingle(source, sizeof(float)),
                ReadSingle(source, 2 * sizeof(float))));
        }
        return (positions.MoveToImmutable(),
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(packed))));
    }

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(float)),
            BitConverter.SingleToInt32Bits(value));
}
