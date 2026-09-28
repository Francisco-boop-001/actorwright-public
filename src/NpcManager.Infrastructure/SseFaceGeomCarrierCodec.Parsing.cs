using System.Collections.Immutable;

namespace NpcManager.Infrastructure;

internal static partial class SseFaceGeomCarrierCodec
{
    private static SseNifBlock ParseBlock(
        byte[] data,
        int index,
        string type,
        int offset,
        int size,
        int blockCount,
        ImmutableArray<string>.Builder strings,
        SseFaceGeomCarrierCodec.ParseProfile profile)
    {
        var position = offset;
        var end = checked(offset + size);
        var references = ImmutableArray.CreateBuilder<SseNifReference>();
        var textures = ImmutableArray<string>.Empty;
        SseNifGeometryPayload? geometryPayload = null;
        SseNifDynamicGeometryLayout? dynamicGeometry = null;
        string? name = null;
        int? nameIndexOffset = null;

        if (NodeTypes.Contains(type))
        {
            name = ReadAvObject(data, ref position, end, index, blockCount, strings,
                references, out nameIndexOffset);
            var childCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {index} child count"),
                $"block {index} child count");
            for (var slot = 0; slot < childCount; slot++)
                ReadReference(data, ref position, end, index, blockCount, "child", references);
            var effectCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {index} effect count"),
                $"block {index} effect count");
            for (var slot = 0; slot < effectCount; slot++)
                ReadReference(data, ref position, end, index, blockCount, "effect", references);
            RequireBlockEnd(position, end, index, type);
        }
        else if (ShapeTypes.Contains(type))
        {
            name = ReadAvObject(data, ref position, end, index, blockCount, strings,
                references, out nameIndexOffset);
            var boundsOffset = position;
            Skip(data, ref position, end, 16, $"block {index} bounding sphere");
            ReadReference(data, ref position, end, index, blockCount, "skin", references);
            ReadReference(data, ref position, end, index, blockCount, "shader", references);
            ReadReference(data, ref position, end, index, blockCount, "alpha", references);
            if (end - position < 16)
                throw Invalid($"Block {index} {type} has a truncated geometry header.");
            geometryPayload = new SseNifGeometryPayload(position, end - position);
            if (string.Equals(type, "BSDynamicTriShape", StringComparison.Ordinal))
            {
                var geometryPosition = position;
                Skip(data, ref geometryPosition, end, 8, $"block {index} vertex descriptor");
                var triangleCount = ReadUInt16(data, ref geometryPosition, end,
                    $"block {index} triangle count");
                var vertexCount = ReadUInt16(data, ref geometryPosition, end,
                    $"block {index} vertex count");
                var dataSize = ReadUInt32(data, ref geometryPosition, end,
                    $"block {index} data size");
                var dynamicPrefix = ReadUInt32(data, ref geometryPosition, end,
                    $"block {index} dynamic prefix");
                var dynamicSize = ReadUInt32(data, ref geometryPosition, end,
                    $"block {index} dynamic size");
                const int vertexStride = 16;
                var expectedSize = checked((uint)(vertexCount * vertexStride));
                if (triangleCount != 0 || dataSize != 0 || dynamicPrefix != 0 ||
                    dynamicSize != expectedSize || end - geometryPosition != dynamicSize)
                    throw Invalid($"Block {index} does not use the admitted packed float4 BSDynamicTriShape layout.");
                dynamicGeometry = new SseNifDynamicGeometryLayout(boundsOffset,
                    boundsOffset + 12, geometryPosition, vertexCount, vertexStride);
            }
        }
        else if (SkinTypes.Contains(type))
        {
            ReadReference(data, ref position, end, index, blockCount, "skindata", references);
            ReadReference(data, ref position, end, index, blockCount, "skinpartition", references);
            ReadReference(data, ref position, end, index, blockCount, "skeletonroot", references);
            var boneCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {index} bone count"),
                $"block {index} bone count");
            for (var bone = 0; bone < boneCount; bone++)
                ReadReference(data, ref position, end, index, blockCount, "bone", references);
            if (string.Equals(type, "BSDismemberSkinInstance", StringComparison.Ordinal))
            {
                var partitionCount = CheckedCount(
                    ReadUInt32(data, ref position, end, $"block {index} partition count"),
                    $"block {index} partition count");
                Skip(data, ref position, end, checked(partitionCount * 4),
                    $"block {index} dismember partitions");
            }
            RequireBlockEnd(position, end, index, type);
        }
        else if (string.Equals(type, "BSLightingShaderProperty", StringComparison.Ordinal))
        {
            if (size < 16)
                throw Invalid(
                    $"Block {index} BSLightingShaderProperty is too small to keep shader type and HairTint color disjoint.");
            var shaderType = ReadUInt32(data, ref position, end, $"block {index} shader type");
            if (shaderType >= 64)
                throw Invalid($"Block {index} shader type {shaderType} exceeds the safety bound.");
            name = ReadName(data, ref position, end, index, strings,
                out nameIndexOffset);
            var extraCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {index} extra count"),
                $"block {index} extra count");
            for (var extra = 0; extra < extraCount; extra++)
                ReadReference(data, ref position, end, index, blockCount, "extra", references);
            ReadReference(data, ref position, end, index, blockCount, "controller", references);
            Skip(data, ref position, end, 24, $"block {index} shader flags and UV");
            ReadReference(data, ref position, end, index, blockCount, "textureset", references);
        }
        else if (string.Equals(type, "NiAlphaProperty", StringComparison.Ordinal))
        {
            name = ReadName(data, ref position, end, index, strings,
                out nameIndexOffset);
            var extraCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {index} extra count"),
                $"block {index} extra count");
            for (var extra = 0; extra < extraCount; extra++)
                ReadReference(data, ref position, end, index, blockCount, "extra", references);
            ReadReference(data, ref position, end, index, blockCount, "controller", references);
            Skip(data, ref position, end, 3, $"block {index} alpha flags");
            RequireBlockEnd(position, end, index, type);
        }
        else if (string.Equals(type, "BSShaderTextureSet", StringComparison.Ordinal))
        {
            var textureCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {index} texture count"),
                $"block {index} texture count");
            if (textureCount is 0 or > 32)
                throw Invalid($"Block {index} texture count {textureCount} is outside the safety bound.");
            var builder = ImmutableArray.CreateBuilder<string>(textureCount);
            for (var slot = 0; slot < textureCount; slot++)
                builder.Add(ReadSizedString(data, ref position, end, 4096,
                    $"block {index} texture slot {slot}"));
            RequireBlockEnd(position, end, index, type);
            textures = builder.ToImmutable();
        }
        else if (profile is ParseProfile.CompleteCarrier or ParseProfile.ExternalHeadPartProvider &&
                 string.Equals(type, "NiStringExtraData", StringComparison.Ordinal))
        {
            if (size != 8)
                throw Unsupported(index, type,
                    $"Block {index} NiStringExtraData carries a {size}-byte payload; only the two-index 8-byte layout is admitted.");
            name = ReadName(data, ref position, end, index, strings, out nameIndexOffset);
            _ = ReadName(data, ref position, end, index, strings, out _);
            RequireBlockEnd(position, end, index, type);
        }
        else if (!LeafTypes.Contains(type))
        {
            throw Unsupported(index, type,
                $"Block {index} type {type} is outside the admitted complete-carrier parser.");
        }

        return new SseNifBlock(index, type, name, offset, size, references.ToImmutable(), textures,
            geometryPayload, dynamicGeometry, nameIndexOffset);
    }

    private static string? ReadAvObject(
        byte[] data,
        ref int position,
        int end,
        int blockIndex,
        int blockCount,
        ImmutableArray<string>.Builder strings,
        ImmutableArray<SseNifReference>.Builder references,
        out int? nameIndexOffset)
    {
        var name = ReadName(data, ref position, end, blockIndex, strings,
            out nameIndexOffset);
        var extraCount = CheckedCount(ReadUInt32(data, ref position, end, $"block {blockIndex} extra count"),
            $"block {blockIndex} extra count");
        for (var extra = 0; extra < extraCount; extra++)
            ReadReference(data, ref position, end, blockIndex, blockCount, "extra", references);
        ReadReference(data, ref position, end, blockIndex, blockCount, "controller", references);
        Skip(data, ref position, end, 56, $"block {blockIndex} transform");
        ReadReference(data, ref position, end, blockIndex, blockCount, "collision", references);
        return name;
    }

    private static string? ReadName(
        byte[] data,
        ref int position,
        int end,
        int blockIndex,
        ImmutableArray<string>.Builder strings,
        out int? nameIndexOffset)
    {
        nameIndexOffset = position;
        var nameIndex = ReadUInt32(data, ref position, end, $"block {blockIndex} name index");
        if (nameIndex == uint.MaxValue) return null;
        if (nameIndex >= strings.Count)
            throw Invalid($"Block {blockIndex} name index {nameIndex} exceeds the string table.");
        return strings[checked((int)nameIndex)];
    }

    private static void ReadReference(
        byte[] data,
        ref int position,
        int end,
        int blockIndex,
        int blockCount,
        string kind,
        ImmutableArray<SseNifReference>.Builder references)
    {
        var referenceOffset = position;
        var target = ReadInt32(data, ref position, end, $"block {blockIndex} {kind} reference");
        if (target < -1 || target >= blockCount)
            throw Invalid($"Block {blockIndex} {kind} reference {target} is outside the block table.");
        references.Add(new SseNifReference(kind, target, referenceOffset));
    }

    private static void ValidateReferenceTypes(ImmutableArray<SseNifBlock>.Builder blocks)
    {
        foreach (var block in blocks)
            foreach (var reference in block.References.Where(reference => reference.Target >= 0))
            {
                var targetType = blocks[reference.Target].Type;
                var valid = reference.Kind switch
                {
                    "child" => NodeTypes.Contains(targetType) || ShapeTypes.Contains(targetType),
                    "skin" => SkinTypes.Contains(targetType),
                    "shader" => string.Equals(targetType, "BSLightingShaderProperty", StringComparison.Ordinal),
                    "alpha" => string.Equals(targetType, "NiAlphaProperty", StringComparison.Ordinal),
                    "skindata" => string.Equals(targetType, "NiSkinData", StringComparison.Ordinal),
                    "skinpartition" => string.Equals(targetType, "NiSkinPartition", StringComparison.Ordinal),
                    "skeletonroot" or "bone" => NodeTypes.Contains(targetType),
                    "textureset" => string.Equals(targetType, "BSShaderTextureSet", StringComparison.Ordinal),
                    _ => true
                };
                if (!valid)
                    throw Invalid($"Block {block.Index} {reference.Kind} reference targets incompatible block type {targetType}.");
            }
    }
}
