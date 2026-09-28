using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal static partial class SseFaceGeomCarrierCodec
{
    private const int PackedVertexFlagNormal = 0x008;
    private const int PackedVertexFlagFullPrecision = 0x400;
    private const int PackedVertexSupportedFlags =
        0x001 | 0x002 | 0x004 | 0x008 | 0x010 | 0x020 | 0x040 | 0x100 |
        PackedVertexFlagFullPrecision;

    /// <summary>
    /// Computes the exact NiSkinPartition block hash used to bind a dynamic
    /// shape to a carrier topology. Both selected-model intake and carrier
    /// merge call this implementation so their topology identities cannot
    /// silently diverge.
    /// </summary>
    internal static Sha256Hash ComputeDynamicShapeTopologyHash(
        SseNifDocument document,
        SseNifBlock shape)
    {
        if (!string.Equals(shape.Type, "BSDynamicTriShape", StringComparison.Ordinal) ||
            shape.Index < 0 || shape.Index >= document.Blocks.Length ||
            !ReferenceEquals(document.Blocks[shape.Index], shape))
        {
            throw new InvalidDataException(
                "Topology hashing requires a BSDynamicTriShape owned by the parsed NIF.");
        }

        var skinReferences = shape.References.Where(item =>
                string.Equals(item.Kind, "skin", StringComparison.Ordinal) && item.Target >= 0)
            .ToArray();
        if (skinReferences.Length != 1)
            throw new InvalidDataException(
                $"Shape '{shape.Name}' lacks one exact skin authority.");
        var skin = document.Blocks[skinReferences[0].Target];
        var partitions = skin.References.Where(item =>
                string.Equals(item.Kind, "skinpartition", StringComparison.Ordinal) &&
                item.Target >= 0)
            .ToArray();
        if (partitions.Length != 1 ||
            !string.Equals(document.Blocks[partitions[0].Target].Type, "NiSkinPartition",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Shape '{shape.Name}' lacks one exact NiSkinPartition topology authority.");
        }

        var partition = document.Blocks[partitions[0].Target];
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(
            document.Data.AsSpan(partition.Offset, partition.Size))));
    }

    internal static SsePackedNormalExtraction ExtractPackedNormals(
        SseNifDocument document,
        SseNifBlock ownedDynamicShape)
    {
        if (!string.Equals(ownedDynamicShape.Type, "BSDynamicTriShape",
                StringComparison.Ordinal) ||
            ownedDynamicShape.DynamicGeometry is null ||
            ownedDynamicShape.Index < 0 ||
            ownedDynamicShape.Index >= document.Blocks.Length ||
            !ReferenceEquals(document.Blocks[ownedDynamicShape.Index],
                ownedDynamicShape))
        {
            throw Invalid(
                "Packed-normal extraction requires a BSDynamicTriShape owned by the parsed NIF.");
        }

        SseNifBlock skin = RequirePackedNormalTarget(
            document, ownedDynamicShape, "skin",
            ["NiSkinInstance", "BSDismemberSkinInstance"]);
        SseNifBlock partition = RequirePackedNormalTarget(
            document, skin, "skinpartition", ["NiSkinPartition"]);
        int position = partition.Offset;
        int end = checked(partition.Offset + partition.Size);
        int partitionCount = CheckedCount(
            ReadUInt32(document.Data, ref position, end,
                $"shape '{ownedDynamicShape.Name}' partition count"),
            "skin partition count");
        if (partitionCount <= 0)
            throw Invalid(
                $"Shape '{ownedDynamicShape.Name}' has no skin partitions.");

        uint vertexDataLengthValue = ReadUInt32(
            document.Data, ref position, end,
            $"shape '{ownedDynamicShape.Name}' shared vertex data size");
        uint vertexSizeValue = ReadUInt32(
            document.Data, ref position, end,
            $"shape '{ownedDynamicShape.Name}' shared vertex size");
        ulong vertexDescription = ReadPackedNormalUInt64(
            document.Data, ref position, end,
            $"shape '{ownedDynamicShape.Name}' shared vertex description");
        int vertexDataLength = checked((int)vertexDataLengthValue);
        int vertexSize = checked((int)vertexSizeValue);
        if (vertexSize <= 0 || vertexDataLength <= 0 ||
            vertexDataLength % vertexSize != 0)
        {
            throw Invalid(
                $"Shape '{ownedDynamicShape.Name}' has an invalid NiSkinPartition shared vertex layout.");
        }

        int flags = checked((int)(vertexDescription >> 44));
        if ((flags & ~PackedVertexSupportedFlags) != 0)
        {
            throw Invalid(
                $"Shape '{ownedDynamicShape.Name}' vertex descriptor 0x{vertexDescription:X16} contains unsupported vertex flags.");
        }

        int computedVertexSize = ComputeVertexSize(vertexDescription);
        if (computedVertexSize != vertexSize)
        {
            throw Invalid(
                $"Shape '{ownedDynamicShape.Name}' NiSkinPartition vertex size {vertexSize} does not match descriptor size {computedVertexSize} for 0x{vertexDescription:X16}.");
        }

        int vertexCount = vertexDataLength / vertexSize;
        if (vertexCount != ownedDynamicShape.DynamicGeometry.VertexCount)
        {
            throw Invalid(
                $"Shape '{ownedDynamicShape.Name}' NiSkinPartition vertex count {vertexCount} does not match dynamic vertex count {ownedDynamicShape.DynamicGeometry.VertexCount}.");
        }

        bool hasPackedNormals = (flags & PackedVertexFlagNormal) != 0;
        int normalOffset = hasPackedNormals
            ? GetVertexAttributeOffset(vertexDescription, 3)
            : -1;
        if (hasPackedNormals &&
            (normalOffset < 0 || normalOffset > vertexSize - 4))
        {
            throw Invalid(
                $"Shape '{ownedDynamicShape.Name}' normal offset {normalOffset} is outside its {vertexSize}-byte vertex row.");
        }

        int vertexDataOffset = position;
        Skip(document.Data, ref position, end, vertexDataLength,
            $"shape '{ownedDynamicShape.Name}' shared vertex data");
        ValidatePackedNormalPartitions(
            document.Data,
            ref position,
            end,
            partitionCount,
            vertexCount,
            vertexDescription,
            ownedDynamicShape.Name ?? $"block-{ownedDynamicShape.Index}");
        RequireBlockEnd(position, end, partition.Index, partition.Type);

        var packedNormalBytes = ImmutableArray.CreateBuilder<byte>(
            hasPackedNormals ? checked(vertexCount * 4) : 0);
        if (hasPackedNormals)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int offset = checked(vertexDataOffset + vertex * vertexSize +
                                     normalOffset);
                packedNormalBytes.AddRange(
                    document.Data.AsSpan(offset, 4).ToArray());
            }
        }

        return new SsePackedNormalExtraction(
            packedNormalBytes.MoveToImmutable(),
            vertexDescription,
            vertexSize,
            normalOffset,
            vertexCount,
            vertexDataOffset,
            vertexDataLength);
    }

    internal static int ComputeVertexSize(ulong description)
    {
        int flags = checked((int)(description >> 44));
        int size = 0;
        if ((flags & 0x001) != 0)
            size += (flags & PackedVertexFlagFullPrecision) != 0 ? 16 : 8;
        if ((flags & 0x002) != 0) size += 4;
        if ((flags & 0x004) != 0) size += 4;
        if ((flags & 0x008) != 0)
        {
            size += 4;
            if ((flags & 0x010) != 0) size += 4;
        }
        if ((flags & 0x020) != 0) size += 4;
        if ((flags & 0x040) != 0) size += 12;
        if ((flags & 0x100) != 0) size += 4;
        return size;
    }

    internal static int GetVertexAttributeOffset(
        ulong description,
        int attribute) =>
        checked((int)((description >> (4 * attribute + 2)) & 0x3C));

    private static SseNifBlock RequirePackedNormalTarget(
        SseNifDocument document,
        SseNifBlock owner,
        string kind,
        ImmutableArray<string> allowedTypes)
    {
        SseNifReference[] references = owner.References.Where(reference =>
                string.Equals(reference.Kind, kind, StringComparison.Ordinal))
            .ToArray();
        if (references.Length != 1 || references[0].Target < 0 ||
            references[0].Target >= document.Blocks.Length)
        {
            throw Invalid(
                $"Shape '{owner.Name}' lacks one exact {kind} authority.");
        }

        SseNifBlock target = document.Blocks[references[0].Target];
        if (!allowedTypes.Contains(target.Type, StringComparer.Ordinal))
        {
            throw Invalid(
                $"Block {owner.Index} {kind} targets unsupported block type '{target.Type}'.");
        }
        return target;
    }

    private static void ValidatePackedNormalPartitions(
        byte[] data,
        ref int position,
        int end,
        int partitionCount,
        int vertexCount,
        ulong vertexDescription,
        string shapeName)
    {
        int totalTriangleCount = 0;
        for (int partitionOrdinal = 0;
             partitionOrdinal < partitionCount;
             partitionOrdinal++)
        {
            int partitionVertexCount = ReadUInt16(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} vertex count");
            int triangleCount = ReadUInt16(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} triangle count");
            int boneCount = ReadUInt16(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} bone count");
            int stripCount = ReadUInt16(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} strip count");
            int weightsPerVertex = ReadUInt16(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} weights per vertex");
            totalTriangleCount = checked(totalTriangleCount + triangleCount);
            if (partitionVertexCount <= 0 ||
                partitionVertexCount > vertexCount ||
                triangleCount <= 0 || totalTriangleCount > 2_000_000 ||
                boneCount > 512 || weightsPerVertex > 16 || stripCount != 0)
            {
                throw Invalid(
                    $"Shape '{shapeName}' partition {partitionOrdinal} counts exceed the packed-normal safety bounds.");
            }

            Skip(data, ref position, end, checked(boneCount * sizeof(ushort)),
                $"shape '{shapeName}' partition {partitionOrdinal} bone palette");
            bool hasVertexMap = ReadPackedNormalBoolean(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} vertex-map flag");
            if (hasVertexMap)
            {
                for (int vertex = 0; vertex < partitionVertexCount; vertex++)
                {
                    int mapped = ReadUInt16(data, ref position, end,
                        $"shape '{shapeName}' partition {partitionOrdinal} vertex map");
                    if (mapped >= vertexCount)
                        throw Invalid(
                            $"Shape '{shapeName}' partition {partitionOrdinal} maps vertex {mapped} outside {vertexCount} vertices.");
                }
            }

            bool hasWeights = ReadPackedNormalBoolean(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} vertex-weight flag");
            if (hasWeights)
                Skip(data, ref position, end,
                    checked(partitionVertexCount * weightsPerVertex * sizeof(float)),
                    $"shape '{shapeName}' partition {partitionOrdinal} vertex weights");

            bool hasFaces = ReadPackedNormalBoolean(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} face flag");
            if (!hasFaces)
                throw Invalid(
                    $"Shape '{shapeName}' partition {partitionOrdinal} omits its declared faces.");
            var legacyTriangles = new ushort[checked(triangleCount * 3)];
            for (int index = 0; index < legacyTriangles.Length; index++)
            {
                legacyTriangles[index] = ReadUInt16(data, ref position, end,
                    $"shape '{shapeName}' partition {partitionOrdinal} legacy triangle index");
                if (legacyTriangles[index] >= vertexCount)
                    throw Invalid(
                        $"Shape '{shapeName}' partition {partitionOrdinal} triangle index {legacyTriangles[index]} exceeds {vertexCount} vertices.");
            }

            bool hasBoneIndices = ReadPackedNormalBoolean(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} bone-index flag");
            if (hasBoneIndices)
                Skip(data, ref position, end,
                    checked(partitionVertexCount * weightsPerVertex),
                    $"shape '{shapeName}' partition {partitionOrdinal} bone indices");

            ushort levelOfDetail = ReadUInt16(data, ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} level of detail");
            if (levelOfDetail != 0)
                throw Invalid(
                    $"Shape '{shapeName}' partition {partitionOrdinal} uses unsupported LOD {levelOfDetail}.");
            ulong partitionDescription = ReadPackedNormalUInt64(data,
                ref position, end,
                $"shape '{shapeName}' partition {partitionOrdinal} vertex description");
            if (partitionDescription != vertexDescription)
                throw Invalid(
                    $"Shape '{shapeName}' partition {partitionOrdinal} vertex description differs from the shared layout.");

            for (int index = 0; index < legacyTriangles.Length; index++)
            {
                ushort triangleIndex = ReadUInt16(data, ref position, end,
                    $"shape '{shapeName}' partition {partitionOrdinal} SSE triangle index");
                if (triangleIndex != legacyTriangles[index])
                    throw Invalid(
                        $"Shape '{shapeName}' partition {partitionOrdinal} duplicate triangle arrays differ.");
            }
        }
    }

    private static bool ReadPackedNormalBoolean(
        byte[] data,
        ref int position,
        int end,
        string role)
    {
        byte value = ReadByte(data, ref position, end, role);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw Invalid($"{role} has invalid boolean value {value}.")
        };
    }

    private static ulong ReadPackedNormalUInt64(
        byte[] data,
        ref int position,
        int end,
        string role)
    {
        EnsureAvailable(position, sizeof(ulong), end, role);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(
            data.AsSpan(position, sizeof(ulong)));
        position += sizeof(ulong);
        return value;
    }

    internal static HashSet<int> FindReachableBlockIndexes(SseNifDocument document)
    {
        var reachable = document.Roots.ToHashSet();
        var pending = new Stack<int>(document.Roots);
        while (pending.TryPop(out var index))
        {
            foreach (var reference in document.Blocks[index].References)
            {
                if (reference.Target >= 0 && reachable.Add(reference.Target))
                    pending.Push(reference.Target);
            }
        }

        return reachable;
    }
}
