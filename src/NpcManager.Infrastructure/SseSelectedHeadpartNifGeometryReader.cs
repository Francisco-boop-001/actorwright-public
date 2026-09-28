using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Strict read-only geometry intake for already-resolved Skyrim SE headpart
/// NIF bytes. It exposes NIF-rest positions without resolving assets, reading
/// files, or consulting TRI BaseVertices.
/// </summary>
public sealed class SseSelectedHeadpartNifGeometryReader :
    ISseSelectedHeadpartNifGeometryReader
{
    private const int MaximumNifBytes = 64 * 1024 * 1024;
    private const int MaximumBlockCount = 10_000;
    private const int MaximumShapeCount = 128;
    private const int MaximumTotalVertexCount = 1_000_000;
    private const int MaximumTotalTriangleCount = 2_000_000;
    private const int VertexFlagUv = 1 << 1;
    private const int VertexFlagNormal = 1 << 3;
    private const int AlphaFlagTestEnabled = 1 << 9;

    public SseSelectedHeadpartNifGeometryReadResult Read(
        SseSelectedHeadpartNifGeometryReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!IsCanonicalNifPath(request.SourcePath))
        {
            diagnostics.Add(Error("sse-headpart-nif-asset-path",
                "The source must be a canonical meshes-relative .nif AssetPath."));
        }

        if (request.Bytes.IsDefaultOrEmpty || request.Bytes.Length > MaximumNifBytes)
        {
            diagnostics.Add(Error("sse-headpart-nif-size",
                $"The materialized NIF must contain 1 to {MaximumNifBytes} bytes."));
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var actualHash = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(request.Bytes.AsSpan())));
        if (actualHash != request.ExpectedSourceSha256)
        {
            diagnostics.Add(Error("sse-headpart-nif-hash-mismatch",
                $"Selected headpart NIF hash {actualHash} does not match {request.ExpectedSourceSha256}."));
            return Refused(diagnostics);
        }

        try
        {
            var document = SseFaceGeomCarrierCodec.Parse(request.Bytes.ToArray());
            if (document.UserVersion != 12 || document.BethesdaStreamVersion != 100)
                throw Invalid("The NIF is not a Skyrim SE user-version 12 / Bethesda-stream 100 asset.");
            if (document.Blocks.Length is <= 0 or > MaximumBlockCount)
                throw Invalid($"NIF block count {document.Blocks.Length} is outside the safety bound.");
            if (document.Roots.Length != 1)
                throw Invalid("A selected headpart NIF must have exactly one unambiguous root.");

            var reachable = SseFaceGeomCarrierCodec.FindReachableBlockIndexes(document);
            ImmutableArray<AssetPath> referencedTextures = reachable.Order()
                .Select(index => document.Blocks[index])
                .Where(block => string.Equals(block.Type, "BSShaderTextureSet",
                    StringComparison.Ordinal))
                .SelectMany(block => block.Textures)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(CanonicalTexturePath)
                .DistinctBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Value, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var allDynamicShapes = document.Blocks.Where(IsDynamicShape).ToImmutableArray();
            var reachableShapes = reachable.Order()
                .Select(index => document.Blocks[index])
                .Where(IsDynamicShape)
                .ToImmutableArray();
            if (reachableShapes.Length is <= 0 or > MaximumShapeCount)
                throw Invalid(
                    $"Reachable dynamic-shape count {reachableShapes.Length} is outside the safety bound.");
            if (allDynamicShapes.Length != reachableShapes.Length)
                throw Invalid("The NIF contains a hidden unreachable BSDynamicTriShape.");
            if (reachableShapes.Any(shape => string.IsNullOrWhiteSpace(shape.Name)) ||
                reachableShapes.Select(shape => shape.Name!).Distinct(StringComparer.Ordinal).Count() !=
                reachableShapes.Length)
                throw Invalid("Reachable BSDynamicTriShape names must be distinct and non-empty.");

            var shapes = ImmutableArray.CreateBuilder<SseSelectedHeadpartNifRestShape>(
                reachableShapes.Length);
            var totalVertexCount = 0;
            foreach (var shape in reachableShapes)
            {
                var layout = shape.DynamicGeometry ?? throw Invalid(
                    $"Reachable shape '{shape.Name}' lacks an admitted packed float4 geometry layout.");
                if (layout.VertexStride != 16 || layout.VertexCount <= 0)
                    throw Invalid(
                        $"Reachable shape '{shape.Name}' does not use positive-count 16-byte vertices.");
                totalVertexCount = checked(totalVertexCount + layout.VertexCount);
                if (totalVertexCount > MaximumTotalVertexCount)
                    throw Invalid(
                        $"Total vertex count exceeds the {MaximumTotalVertexCount} safety bound.");

                bool shaderlessDummy =
                    IsShaderlessDummyLens(request.SourcePath, shape);
                shapes.Add(ReadShape(document, shape, layout,
                    shaderlessDummy));
                if (shaderlessDummy)
                {
                    diagnostics.Add(new Diagnostic(
                        "sse-headpart-nif-shaderless-dummy-admitted",
                        DiagnosticSeverity.Warning,
                        $"Admitted explicitly named shaderless dummy lens '{request.SourcePath}' for downstream bounded omission; it carries no render material authority."));
                }
            }

            diagnostics.Add(new Diagnostic("sse-headpart-nif-geometry-read",
                DiagnosticSeverity.Info,
                $"Read {shapes.Count} reachable dynamic shape(s) and {totalVertexCount} exact NIF-rest positions from '{request.SourcePath}'."));
            return new SseSelectedHeadpartNifGeometryReadResult(true,
                new SseSelectedHeadpartNifGeometryDocument(
                    request.SourcePath,
                    actualHash,
                    request.Bytes.Length,
                    document.Blocks.Length,
                    reachable.Count,
                    shapes.MoveToImmutable())
                {
                    ReferencedTextures = referencedTextures
                },
                diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception) when (
            SseFaceGeomCarrierCodec.TryGetUnsupportedBlock(exception, out int blockIndex, out string blockType))
        {
            diagnostics.Add(Error(
                "sse-headpart-nif-unsupported-block",
                $"Selected headpart NIF '{request.SourcePath.Value}' carries unsupported block {blockIndex} " +
                $"of type {blockType}: {exception.Message}"));
            return Refused(diagnostics);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error(
                "sse-headpart-nif-malformed",
                $"Selected headpart NIF '{request.SourcePath.Value}' is malformed: {exception.Message}"));
            return Refused(diagnostics);
        }
        catch (OverflowException)
        {
            diagnostics.Add(Error("sse-headpart-nif-count-overflow",
                "Selected headpart NIF count arithmetic overflowed the supported address space."));
            return Refused(diagnostics);
        }
    }

    private static SseSelectedHeadpartNifRestShape ReadShape(
        SseNifDocument document,
        SseNifBlock shape,
        SseNifDynamicGeometryLayout layout,
        bool shaderlessDummy)
    {
        var packedPositions = new byte[checked(layout.VertexCount * 12)];
        var positions = ImmutableArray.CreateBuilder<Vector3>(layout.VertexCount);
        for (var vertex = 0; vertex < layout.VertexCount; vertex++)
        {
            var source = document.Data.AsSpan(
                layout.VertexDataOffset + vertex * layout.VertexStride, 12);
            source.CopyTo(packedPositions.AsSpan(vertex * 12, 12));
            var position = new Vector3(
                ReadSingle(source, 0),
                ReadSingle(source, sizeof(float)),
                ReadSingle(source, 2 * sizeof(float)));
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z))
                throw Invalid(
                    $"Reachable shape '{shape.Name}' vertex {vertex} contains a non-finite position.");
            positions.Add(position);
        }

        ImmutableArray<Vector3> restPositions = positions.MoveToImmutable();
        Sha256Hash topologySha256 =
            SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(document, shape);
        var partitionGeometry = ReadSkinPartitionGeometry(
            document, shape, layout.VertexCount, restPositions, topologySha256);
        ImmutableArray<SseSelectedHeadpartMaterial> materials =
            shaderlessDummy
                ? []
                : [ReadMaterial(document, shape,
                    partitionGeometry.TriangleIndices.Length / 3)];
        SseSelectedHeadpartNifPlacement placement =
            ReadRenderPlacement(document, shape, layout.VertexCount);
        return new SseSelectedHeadpartNifRestShape(
            shape.Name!,
            shape.Index,
            layout.VertexCount,
            restPositions,
            Hash(packedPositions),
            topologySha256)
        {
            TriangleIndices = partitionGeometry.TriangleIndices,
            TextureCoordinates = partitionGeometry.TextureCoordinates,
            Normals = partitionGeometry.Normals,
            PackedNormalSentinels = partitionGeometry.PackedNormalSentinels,
            PackedNormalBytes = partitionGeometry.PackedNormalBytes,
            Materials = materials,
            IsShaderlessDummy = shaderlessDummy,
            RenderPlacement = placement
        };
    }

    private static SseSelectedHeadpartNifPlacement ReadRenderPlacement(
        SseNifDocument document,
        SseNifBlock shape,
        int vertexCount)
    {
        SseNifBlock skin = RequireSingleTarget(
            document, shape, "skin", "NiSkinInstance",
            "BSDismemberSkinInstance");
        SseNifBlock skinData = RequireSingleTarget(
            document, skin, "skindata", "NiSkinData");
        SseNifReference[] boneReferences = skin.References
            .Where(item => item.Kind == "bone" && item.Target >= 0)
            .ToArray();
        ReadOnlySpan<byte> bytes = document.Data.AsSpan(
            skinData.Offset, skinData.Size);
        var position = 0;
        SseSelectedHeadpartNifPlacement storedGlobalToSkin =
            ReadNiTransform(bytes, ref position, "stored global-to-skin");
        int boneCount = CheckedPositiveCount(
            ReadUInt32(bytes, ref position, "skin-data bone count"),
            512,
            "skin-data bone count");
        bool hasVertexWeights = ReadBoolean(
            bytes, ref position, "skin-data vertex-weight flag");
        if (!hasVertexWeights)
            throw Invalid(
                $"Shape '{shape.Name}' NiSkinData omits its bind vertex weights.");
        if (boneCount != boneReferences.Length)
            throw Invalid(
                $"Shape '{shape.Name}' NiSkinData bone count {boneCount} does not match its {boneReferences.Length} bone references.");

        Dictionary<int, int> parents = BuildParentMap(document);
        var globalCache =
            new Dictionary<int, SseSelectedHeadpartNifPlacement>();
        var skinToGlobal =
            new List<SseSelectedHeadpartNifPlacement>(boneCount);
        for (var boneOrdinal = 0; boneOrdinal < boneCount; boneOrdinal++)
        {
            SseSelectedHeadpartNifPlacement skinToBone =
                ReadNiTransform(
                    bytes,
                    ref position,
                    $"skin-data bone {boneOrdinal} skin-to-bone");
            Vector3 boundCenter = ReadVector3(
                bytes,
                ref position,
                $"skin-data bone {boneOrdinal} bound center");
            float boundRadius = ReadFloat(
                bytes,
                ref position,
                $"skin-data bone {boneOrdinal} bound radius");
            int weightCount = ReadUInt16(
                bytes,
                ref position,
                $"skin-data bone {boneOrdinal} weight count");
            if (!IsFinite(boundCenter) ||
                !float.IsFinite(boundRadius) ||
                boundRadius < 0 ||
                weightCount > vertexCount)
            {
                throw Invalid(
                    $"Shape '{shape.Name}' bone {boneOrdinal} has invalid bind bounds or weight count.");
            }
            for (var weightOrdinal = 0;
                 weightOrdinal < weightCount;
                 weightOrdinal++)
            {
                int weightedVertex = ReadUInt16(
                    bytes,
                    ref position,
                    $"skin-data bone {boneOrdinal} weight {weightOrdinal} vertex");
                float weight = ReadFloat(
                    bytes,
                    ref position,
                    $"skin-data bone {boneOrdinal} weight {weightOrdinal} value");
                if (weightedVertex >= vertexCount ||
                    !float.IsFinite(weight) ||
                    weight is < 0 or > 1.0001F)
                {
                    throw Invalid(
                        $"Shape '{shape.Name}' bone {boneOrdinal} contains an invalid vertex weight.");
                }
            }

            int boneBlockIndex = boneReferences[boneOrdinal].Target;
            SseSelectedHeadpartNifPlacement boneGlobal =
                ReadGlobalAvPlacement(
                    document,
                    boneBlockIndex,
                    parents,
                    globalCache,
                    []);
            skinToGlobal.Add(Compose(boneGlobal, skinToBone));
        }
        if (position != bytes.Length)
            throw Invalid(
                $"Shape '{shape.Name}' NiSkinData did not consume its block exactly.");

        SseSelectedHeadpartNifPlacement calculatedSkinToGlobal =
            AverageConsistentBindPlacements(shape, skinToGlobal);
        SseSelectedHeadpartNifPlacement calculatedGlobalToSkin =
            Invert(
                calculatedSkinToGlobal,
                $"shape '{shape.Name}' calculated global-to-skin");
        SseSelectedHeadpartNifPlacement shapePlacement =
            ReadAvPlacement(document, shape);
        SseSelectedHeadpartNifPlacement combinedGlobalToSkin =
            Compose(
                shapePlacement,
                Compose(storedGlobalToSkin, calculatedGlobalToSkin));
        SseSelectedHeadpartNifPlacement renderPlacement =
            Invert(
                combinedGlobalToSkin,
                $"shape '{shape.Name}' render placement");
        if (!renderPlacement.IsFinite)
            throw Invalid(
                $"Shape '{shape.Name}' produced a non-finite render placement.");
        return renderPlacement;
    }

    private static Dictionary<int, int> BuildParentMap(
        SseNifDocument document)
    {
        var parents = new Dictionary<int, int>();
        foreach (SseNifBlock candidate in document.Blocks)
        {
            foreach (SseNifReference child in candidate.References.Where(
                         item => item.Kind == "child" && item.Target >= 0))
            {
                if (!parents.TryAdd(child.Target, candidate.Index))
                    throw Invalid(
                        $"NIF block {child.Target} has multiple AV-object parents.");
            }
        }
        return parents;
    }

    private static SseSelectedHeadpartNifPlacement ReadGlobalAvPlacement(
        SseNifDocument document,
        int blockIndex,
        IReadOnlyDictionary<int, int> parents,
        IDictionary<int, SseSelectedHeadpartNifPlacement> cache,
        HashSet<int> visiting)
    {
        if (cache.TryGetValue(
                blockIndex,
                out SseSelectedHeadpartNifPlacement? cached))
            return cached;
        if (!visiting.Add(blockIndex))
            throw Invalid("The NIF AV-object hierarchy contains a cycle.");
        SseSelectedHeadpartNifPlacement local =
            ReadAvPlacement(document, document.Blocks[blockIndex]);
        SseSelectedHeadpartNifPlacement global =
            parents.TryGetValue(blockIndex, out int parent)
                ? Compose(
                    ReadGlobalAvPlacement(
                        document,
                        parent,
                        parents,
                        cache,
                        visiting),
                    local)
                : local;
        visiting.Remove(blockIndex);
        cache.Add(blockIndex, global);
        return global;
    }

    private static SseSelectedHeadpartNifPlacement ReadAvPlacement(
        SseNifDocument document,
        SseNifBlock block)
    {
        ReadOnlySpan<byte> bytes =
            document.Data.AsSpan(block.Offset, block.Size);
        var position = 0;
        Skip(bytes, ref position, sizeof(uint), "AV-object name");
        int extraCount = CheckedCount(
            ReadUInt32(bytes, ref position, "AV-object extra count"),
            "AV-object extra count");
        Skip(
            bytes,
            ref position,
            checked(extraCount * sizeof(int)),
            "AV-object extras");
        Skip(
            bytes,
            ref position,
            sizeof(int) + sizeof(uint),
            "AV-object controller and flags");
        Vector3 translation = ReadVector3(
            bytes, ref position, "AV-object translation");
        float[] rotation = ReadRotation(
            bytes, ref position, "AV-object rotation");
        float scale = ReadFloat(
            bytes, ref position, "AV-object scale");
        return FromNiTransform(rotation, translation, scale, block.Name);
    }

    private static SseSelectedHeadpartNifPlacement ReadNiTransform(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        float[] rotation = ReadRotation(
            bytes, ref position, $"{label} rotation");
        Vector3 translation = ReadVector3(
            bytes, ref position, $"{label} translation");
        float scale = ReadFloat(
            bytes, ref position, $"{label} scale");
        return FromNiTransform(rotation, translation, scale, label);
    }

    private static SseSelectedHeadpartNifPlacement FromNiTransform(
        float[] rotation,
        Vector3 translation,
        float scale,
        string? label)
    {
        if (rotation.Length != 9 ||
            rotation.Any(value => !float.IsFinite(value)) ||
            !IsFinite(translation) ||
            !float.IsFinite(scale) ||
            Math.Abs(scale) < 0.000001F ||
            Math.Abs(scale) > 1000F)
        {
            throw Invalid(
                $"NIF transform '{label}' contains invalid rotation, translation, or scale.");
        }
        return new SseSelectedHeadpartNifPlacement(
            rotation[0] * scale,
            rotation[1] * scale,
            rotation[2] * scale,
            rotation[3] * scale,
            rotation[4] * scale,
            rotation[5] * scale,
            rotation[6] * scale,
            rotation[7] * scale,
            rotation[8] * scale,
            translation.X,
            translation.Y,
            translation.Z);
    }

    private static SseSelectedHeadpartNifPlacement
        AverageConsistentBindPlacements(
            SseNifBlock shape,
            List<SseSelectedHeadpartNifPlacement> placements)
    {
        if (placements.Count == 0)
            throw Invalid(
                $"Shape '{shape.Name}' has no bind placement authority.");
        SseSelectedHeadpartNifPlacement first = placements[0];
        foreach (SseSelectedHeadpartNifPlacement candidate in
                 placements.Skip(1))
        {
            float linearDifference = MaximumLinearDifference(
                first, candidate);
            float translationDifference = Vector3.Distance(
                Translation(first), Translation(candidate));
            if (linearDifference > 0.02F ||
                translationDifference > 0.8F)
            {
                throw Invalid(
                    $"Shape '{shape.Name}' bone bind placements disagree (linear {linearDifference:R}, translation {translationDifference:R}).");
            }
        }
        float Average(Func<SseSelectedHeadpartNifPlacement, float> selector) =>
            placements.Average(item => (double)selector(item)) is double value
                ? checked((float)value)
                : throw new InvalidOperationException();
        return new SseSelectedHeadpartNifPlacement(
            Average(item => item.M11),
            Average(item => item.M12),
            Average(item => item.M13),
            Average(item => item.M21),
            Average(item => item.M22),
            Average(item => item.M23),
            Average(item => item.M31),
            Average(item => item.M32),
            Average(item => item.M33),
            Average(item => item.TranslationX),
            Average(item => item.TranslationY),
            Average(item => item.TranslationZ));
    }

    private static SseSelectedHeadpartNifPlacement Compose(
        SseSelectedHeadpartNifPlacement outer,
        SseSelectedHeadpartNifPlacement inner)
    {
        Vector3 translation =
            outer.TransformDirection(Translation(inner)) +
            Translation(outer);
        return new SseSelectedHeadpartNifPlacement(
            outer.M11 * inner.M11 + outer.M12 * inner.M21 +
            outer.M13 * inner.M31,
            outer.M11 * inner.M12 + outer.M12 * inner.M22 +
            outer.M13 * inner.M32,
            outer.M11 * inner.M13 + outer.M12 * inner.M23 +
            outer.M13 * inner.M33,
            outer.M21 * inner.M11 + outer.M22 * inner.M21 +
            outer.M23 * inner.M31,
            outer.M21 * inner.M12 + outer.M22 * inner.M22 +
            outer.M23 * inner.M32,
            outer.M21 * inner.M13 + outer.M22 * inner.M23 +
            outer.M23 * inner.M33,
            outer.M31 * inner.M11 + outer.M32 * inner.M21 +
            outer.M33 * inner.M31,
            outer.M31 * inner.M12 + outer.M32 * inner.M22 +
            outer.M33 * inner.M32,
            outer.M31 * inner.M13 + outer.M32 * inner.M23 +
            outer.M33 * inner.M33,
            translation.X,
            translation.Y,
            translation.Z);
    }

    private static SseSelectedHeadpartNifPlacement Invert(
        SseSelectedHeadpartNifPlacement value,
        string label)
    {
        float determinant =
            value.M11 * (value.M22 * value.M33 - value.M23 * value.M32) -
            value.M12 * (value.M21 * value.M33 - value.M23 * value.M31) +
            value.M13 * (value.M21 * value.M32 - value.M22 * value.M31);
        if (!float.IsFinite(determinant) ||
            Math.Abs(determinant) < 0.0000001F)
            throw Invalid($"{label} is singular.");
        float reciprocal = 1F / determinant;
        var inverseLinear = new SseSelectedHeadpartNifPlacement(
            (value.M22 * value.M33 - value.M23 * value.M32) * reciprocal,
            (value.M13 * value.M32 - value.M12 * value.M33) * reciprocal,
            (value.M12 * value.M23 - value.M13 * value.M22) * reciprocal,
            (value.M23 * value.M31 - value.M21 * value.M33) * reciprocal,
            (value.M11 * value.M33 - value.M13 * value.M31) * reciprocal,
            (value.M13 * value.M21 - value.M11 * value.M23) * reciprocal,
            (value.M21 * value.M32 - value.M22 * value.M31) * reciprocal,
            (value.M12 * value.M31 - value.M11 * value.M32) * reciprocal,
            (value.M11 * value.M22 - value.M12 * value.M21) * reciprocal,
            0,
            0,
            0);
        Vector3 translation =
            -inverseLinear.TransformDirection(Translation(value));
        return inverseLinear with
        {
            TranslationX = translation.X,
            TranslationY = translation.Y,
            TranslationZ = translation.Z
        };
    }

    private static float MaximumLinearDifference(
        SseSelectedHeadpartNifPlacement left,
        SseSelectedHeadpartNifPlacement right) =>
        new[]
        {
            Math.Abs(left.M11 - right.M11),
            Math.Abs(left.M12 - right.M12),
            Math.Abs(left.M13 - right.M13),
            Math.Abs(left.M21 - right.M21),
            Math.Abs(left.M22 - right.M22),
            Math.Abs(left.M23 - right.M23),
            Math.Abs(left.M31 - right.M31),
            Math.Abs(left.M32 - right.M32),
            Math.Abs(left.M33 - right.M33)
        }.Max();

    private static Vector3 Translation(
        SseSelectedHeadpartNifPlacement value) =>
        new(value.TranslationX, value.TranslationY, value.TranslationZ);

    private static float[] ReadRotation(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        var values = new float[9];
        for (var index = 0; index < values.Length; index++)
            values[index] = ReadFloat(
                bytes, ref position, $"{label} element {index}");
        return values;
    }

    private static Vector3 ReadVector3(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label) =>
        new(
            ReadFloat(bytes, ref position, $"{label} X"),
            ReadFloat(bytes, ref position, $"{label} Y"),
            ReadFloat(bytes, ref position, $"{label} Z"));

    private static float ReadFloat(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        RequireAvailable(bytes, position, sizeof(float), label);
        float value = ReadSingle(bytes, position);
        position += sizeof(float);
        return value;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsShaderlessDummyLens(
        AssetPath sourcePath,
        SseNifBlock shape) =>
        sourcePath.Value.Contains("dummy",
            StringComparison.OrdinalIgnoreCase) &&
        sourcePath.Value.Contains("lens",
            StringComparison.OrdinalIgnoreCase) &&
        shape.Name?.Contains("lens",
            StringComparison.OrdinalIgnoreCase) == true &&
        !shape.References.Any(reference =>
            reference.Kind == "shader" && reference.Target >= 0);

    private static SseSkinPartitionGeometry ReadSkinPartitionGeometry(
        SseNifDocument document,
        SseNifBlock shape,
        int expectedVertexCount,
        ImmutableArray<Vector3> restPositions,
        Sha256Hash topologySha256)
    {
        var skin = RequireSingleTarget(document, shape, "skin", "NiSkinInstance",
            "BSDismemberSkinInstance");
        var partition = RequireSingleTarget(document, skin, "skinpartition",
            "NiSkinPartition");
        SsePackedNormalExtraction packedNormals =
            SseFaceGeomCarrierCodec.ExtractPackedNormals(document, shape);
        ReadOnlySpan<byte> bytes = document.Data.AsSpan(partition.Offset, partition.Size);
        var position = 0;
        var partitionCount = CheckedPositiveCount(ReadUInt32(bytes, ref position,
            "partition count"), MaximumShapeCount, "partition count");
        int vertexSize = packedNormals.VertexSize;
        int vertexCount = packedNormals.VertexCount;
        if (vertexCount != expectedVertexCount)
            throw Invalid(
                $"Shape '{shape.Name}' NiSkinPartition vertex count {vertexCount} does not match dynamic vertex count {expectedVertexCount}.");
        int vertexDataOffset = checked(
            packedNormals.VertexDataOffset - partition.Offset);
        ReadOnlySpan<byte> vertexData = bytes.Slice(
            vertexDataOffset, packedNormals.VertexDataLength);
        position = checked(vertexDataOffset + packedNormals.VertexDataLength);

        var triangles = ImmutableArray.CreateBuilder<int>();
        var partitionTriangles =
            new List<(int PartitionIndex, ImmutableArray<int> TriangleIndices)>();
        for (var partitionOrdinal = 0; partitionOrdinal < partitionCount;
             partitionOrdinal++)
        {
            int partitionVertexCount = ReadUInt16(bytes, ref position,
                $"partition {partitionOrdinal} vertex count");
            int triangleCount = ReadUInt16(bytes, ref position,
                $"partition {partitionOrdinal} triangle count");
            int boneCount = ReadUInt16(bytes, ref position,
                $"partition {partitionOrdinal} bone count");
            int stripCount = ReadUInt16(bytes, ref position,
                $"partition {partitionOrdinal} strip count");
            int weightsPerVertex = ReadUInt16(bytes, ref position,
                $"partition {partitionOrdinal} weights per vertex");
            if (partitionVertexCount <= 0 || partitionVertexCount > vertexCount ||
                triangleCount <= 0 ||
                checked(triangles.Count / 3 + triangleCount) > MaximumTotalTriangleCount ||
                boneCount > 512 || weightsPerVertex > 16)
                throw Invalid(
                    $"Shape '{shape.Name}' partition {partitionOrdinal} counts exceed the safety bounds.");
            if (stripCount != 0)
                throw Invalid(
                    $"Shape '{shape.Name}' partition {partitionOrdinal} uses unsupported triangle strips.");

            Skip(bytes, ref position, checked(boneCount * sizeof(ushort)),
                $"partition {partitionOrdinal} bone palette");
            bool hasVertexMap = ReadBoolean(bytes, ref position,
                $"partition {partitionOrdinal} vertex-map flag");
            if (hasVertexMap)
            {
                for (var vertex = 0; vertex < partitionVertexCount; vertex++)
                {
                    int mapped = ReadUInt16(bytes, ref position,
                        $"partition {partitionOrdinal} vertex map");
                    if (mapped >= vertexCount)
                        throw Invalid(
                            $"Shape '{shape.Name}' partition {partitionOrdinal} maps vertex {mapped} outside {vertexCount} vertices.");
                }
            }

            bool hasWeights = ReadBoolean(bytes, ref position,
                $"partition {partitionOrdinal} vertex-weight flag");
            if (hasWeights)
                Skip(bytes, ref position,
                    checked(partitionVertexCount * weightsPerVertex * sizeof(float)),
                    $"partition {partitionOrdinal} vertex weights");

            bool hasFaces = ReadBoolean(bytes, ref position,
                $"partition {partitionOrdinal} face flag");
            if (!hasFaces)
                throw Invalid(
                    $"Shape '{shape.Name}' partition {partitionOrdinal} omits its declared faces.");
            var legacyTriangles = new int[checked(triangleCount * 3)];
            for (var index = 0; index < legacyTriangles.Length; index++)
                legacyTriangles[index] = ReadUInt16(bytes, ref position,
                    $"partition {partitionOrdinal} legacy triangle index");

            bool hasBoneIndices = ReadBoolean(bytes, ref position,
                $"partition {partitionOrdinal} bone-index flag");
            if (hasBoneIndices)
                Skip(bytes, ref position,
                    checked(partitionVertexCount * weightsPerVertex),
                    $"partition {partitionOrdinal} bone indices");

            ushort levelOfDetail = ReadUInt16(bytes, ref position,
                $"partition {partitionOrdinal} level of detail");
            if (levelOfDetail != 0)
                throw Invalid(
                    $"Shape '{shape.Name}' partition {partitionOrdinal} uses unsupported LOD {levelOfDetail}.");
            ulong partitionVertexDescription = ReadUInt64(bytes, ref position,
                $"partition {partitionOrdinal} vertex description");
            if (partitionVertexDescription != packedNormals.VertexDescription)
                throw Invalid(
                    $"Shape '{shape.Name}' partition {partitionOrdinal} vertex description differs from the shared layout.");

            for (var index = 0; index < legacyTriangles.Length; index++)
            {
                int triangleIndex = ReadUInt16(bytes, ref position,
                    $"partition {partitionOrdinal} SSE triangle index");
                if (triangleIndex != legacyTriangles[index])
                    throw Invalid(
                        $"Shape '{shape.Name}' partition {partitionOrdinal} duplicate triangle arrays differ.");
                if (triangleIndex >= vertexCount)
                    throw Invalid(
                        $"Shape '{shape.Name}' partition {partitionOrdinal} triangle index {triangleIndex} exceeds {vertexCount} vertices.");
                triangles.Add(triangleIndex);
            }
            partitionTriangles.Add((partitionOrdinal,
                legacyTriangles.ToImmutableArray()));
        }
        if (position != bytes.Length)
            throw Invalid(
                $"Shape '{shape.Name}' NiSkinPartition did not consume its block exactly.");

        int flags = checked((int)(packedNormals.VertexDescription >> 44));
        var uvs = (flags & VertexFlagUv) != 0
            ? ReadTextureCoordinates(vertexData, vertexCount, vertexSize,
                SseFaceGeomCarrierCodec.GetVertexAttributeOffset(
                    packedNormals.VertexDescription, 1), shape.Name!)
            : ImmutableArray<Vector2>.Empty;
        SsePackedNormalRead normalRead = (flags & VertexFlagNormal) != 0
            ? ReadNormals(
                packedNormals.PackedNormalBytes,
                vertexCount,
                vertexSize,
                packedNormals.NormalOffset,
                shape.Name!,
                partitionTriangles,
                restPositions,
                shape.Index,
                partition.Offset + vertexDataOffset,
                topologySha256)
            : new SsePackedNormalRead([], [], []);
        return new SseSkinPartitionGeometry(
            triangles.ToImmutable(),
            uvs,
            normalRead.Normals,
            normalRead.PackedNormalSentinels,
            normalRead.PackedNormalBytes);
    }

    private static SseSelectedHeadpartMaterial ReadMaterial(
        SseNifDocument document,
        SseNifBlock shape,
        int triangleCount)
    {
        var shader = RequireSingleTarget(document, shape, "shader",
            "BSLightingShaderProperty");
        var textureSet = RequireSingleTarget(document, shader, "textureset",
            "BSShaderTextureSet");
        var slots = textureSet.Textures
            .Select((value, slot) => (value, slot))
            .Where(item => !string.IsNullOrWhiteSpace(item.value))
            .Select(item => new SseSelectedHeadpartTextureSlot(
                item.slot, CanonicalTexturePath(item.value)))
            .ToImmutableArray();
        if (slots.IsDefaultOrEmpty)
            throw Invalid($"Shape '{shape.Name}' shader has no canonical texture route.");
        (bool alphaTestEnabled, byte alphaTestThreshold) =
            ReadAlphaTest(document, shape);
        return new SseSelectedHeadpartMaterial(
            $"shape:{shape.Index}/shader:{shader.Index}/textureset:{textureSet.Index}",
            0,
            triangleCount,
            shader.Index,
            textureSet.Index,
            slots.Select(item => item.Path).ToImmutableArray())
        {
            TextureSlotCount = textureSet.Textures.Length,
            TextureSlots = slots,
            AlphaTestEnabled = alphaTestEnabled,
            AlphaTestThreshold = alphaTestThreshold
        };
    }

    private static (bool Enabled, byte Threshold) ReadAlphaTest(
        SseNifDocument document,
        SseNifBlock shape)
    {
        SseNifReference[] references = shape.References.Where(item =>
                item.Kind == "alpha" && item.Target >= 0)
            .ToArray();
        if (references.Length == 0)
            return (false, 0);
        if (references.Length != 1)
            throw Invalid(
                $"Shape '{shape.Name}' has ambiguous alpha-property authority.");
        SseNifBlock alpha = document.Blocks[references[0].Target];
        if (alpha.Type != "NiAlphaProperty")
            throw Invalid(
                $"Shape '{shape.Name}' alpha authority is not NiAlphaProperty.");
        ReadOnlySpan<byte> bytes =
            document.Data.AsSpan(alpha.Offset, alpha.Size);
        var position = 0;
        Skip(bytes, ref position, sizeof(uint), "alpha-property name");
        int extraCount = CheckedCount(
            ReadUInt32(bytes, ref position, "alpha-property extra count"),
            "alpha-property extra count");
        Skip(
            bytes,
            ref position,
            checked(extraCount * sizeof(int)),
            "alpha-property extras");
        Skip(
            bytes,
            ref position,
            sizeof(int),
            "alpha-property controller");
        ushort flags = ReadUInt16(
            bytes, ref position, "alpha-property flags");
        byte threshold = ReadByte(
            bytes, ref position, "alpha-property threshold");
        if (position != bytes.Length)
            throw Invalid(
                $"Shape '{shape.Name}' NiAlphaProperty did not consume its block exactly.");
        return ((flags & AlphaFlagTestEnabled) != 0, threshold);
    }

    private static SseNifBlock RequireSingleTarget(
        SseNifDocument document,
        SseNifBlock source,
        string referenceKind,
        params string[] allowedTypes)
    {
        var references = source.References.Where(item =>
                string.Equals(item.Kind, referenceKind, StringComparison.Ordinal) &&
                item.Target >= 0)
            .ToArray();
        if (references.Length != 1)
            throw Invalid(
                $"Block {source.Index} '{source.Name}' lacks one exact {referenceKind} authority.");
        var target = document.Blocks[references[0].Target];
        if (!allowedTypes.Contains(target.Type, StringComparer.Ordinal))
            throw Invalid(
                $"Block {source.Index} '{source.Name}' {referenceKind} targets incompatible type {target.Type}.");
        return target;
    }

    private static ImmutableArray<Vector2> ReadTextureCoordinates(
        ReadOnlySpan<byte> data,
        int vertexCount,
        int vertexSize,
        int offset,
        string shapeName)
    {
        if (offset < 0 || offset + 4 > vertexSize)
            throw Invalid($"Shape '{shapeName}' UV offset is outside its vertex row.");
        var values = ImmutableArray.CreateBuilder<Vector2>(vertexCount);
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            ReadOnlySpan<byte> row = data.Slice(vertex * vertexSize, vertexSize);
            var value = new Vector2(
                (float)BitConverter.UInt16BitsToHalf(
                    BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset, 2))),
                (float)BitConverter.UInt16BitsToHalf(
                    BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset + 2, 2))));
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
                throw Invalid(
                    $"Shape '{shapeName}' vertex {vertex} contains a non-finite UV.");
            values.Add(value);
        }
        return values.MoveToImmutable();
    }

    private static SsePackedNormalRead ReadNormals(
        ImmutableArray<byte> packedNormalBytes,
        int vertexCount,
        int vertexSize,
        int offset,
        string shapeName,
        IReadOnlyList<(int PartitionIndex, ImmutableArray<int> TriangleIndices)>
            partitionTriangles,
        ImmutableArray<Vector3> restPositions,
        int sourceBlockIndex,
        int absoluteVertexDataOffset,
        Sha256Hash topologySha256)
    {
        if (offset < 0 || offset + 4 > vertexSize)
            throw Invalid($"Shape '{shapeName}' normal offset is outside its vertex row.");
        if (packedNormalBytes.Length != checked(vertexCount * 4))
            throw Invalid(
                $"Shape '{shapeName}' packed-normal byte count does not match its vertex count.");
        var values = ImmutableArray.CreateBuilder<Vector3>(vertexCount);
        var sentinels =
            ImmutableArray.CreateBuilder<SseSelectedHeadpartPackedNormalSentinel>();
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            ReadOnlySpan<byte> row = packedNormalBytes.AsSpan(vertex * 4, 4);
            if (row[0] == 0x80 && row[1] == 0x80 &&
                row[2] == 0x80 && row[3] == 0x80)
            {
                var incident = ImmutableArray.CreateBuilder<int>();
                foreach ((int partitionIndex, ImmutableArray<int> indices) in
                         partitionTriangles)
                {
                    for (var index = 0; index < indices.Length; index += 3)
                    {
                        int first = indices[index];
                        int second = indices[index + 1];
                        int third = indices[index + 2];
                        if (first != vertex && second != vertex && third != vertex)
                            continue;
                        if (!SseSelectedHeadpartPackedNormalPolicy.IsExactDegenerate(
                                restPositions[first], restPositions[second],
                                restPositions[third]))
                        {
                            throw Invalid(
                                $"Shape '{shapeName}' sentinel-normal vertex {vertex} opens incident partition {partitionIndex} triangle at rest under policy '{SseSelectedHeadpartPackedNormalPolicy.Version}'.");
                        }
                        incident.Add(first);
                        incident.Add(second);
                        incident.Add(third);
                    }
                }

                if (incident.Count == 0)
                {
                    throw Invalid(
                        $"Shape '{shapeName}' sentinel-normal vertex {vertex} has no incident triangle under policy '{SseSelectedHeadpartPackedNormalPolicy.Version}'.");
                }

                // A sentinel is associated with each owning partition. This
                // keeps the source partition authority explicit even when a
                // shared vertex participates in more than one partition.
                foreach ((int partitionIndex, ImmutableArray<int> indices) in
                         partitionTriangles)
                {
                    var owned = ImmutableArray.CreateBuilder<int>();
                    for (var index = 0; index < indices.Length; index += 3)
                    {
                        int first = indices[index];
                        int second = indices[index + 1];
                        int third = indices[index + 2];
                        if (first == vertex || second == vertex || third == vertex)
                        {
                            owned.Add(first);
                            owned.Add(second);
                            owned.Add(third);
                        }
                    }
                    if (owned.Count == 0) continue;
                    sentinels.Add(new SseSelectedHeadpartPackedNormalSentinel(
                        sourceBlockIndex,
                        partitionIndex,
                        vertex,
                        checked(absoluteVertexDataOffset + vertex * vertexSize + offset),
                        topologySha256,
                        owned.ToImmutable(),
                        row.ToArray().ToImmutableArray()));
                }
                values.Add(Vector3.Zero);
                continue;
            }

            var value = new Vector3(
                row[0] / 127.5f - 1.0f,
                row[1] / 127.5f - 1.0f,
                row[2] / 127.5f - 1.0f);
            float length = value.Length();
            if (!float.IsFinite(length) || length < 0.25f || length > 1.75f)
                throw Invalid(
                    $"Shape '{shapeName}' vertex {vertex} contains an invalid packed normal.");
            values.Add(Vector3.Normalize(value));
        }
        return new SsePackedNormalRead(
            values.MoveToImmutable(),
            sentinels.ToImmutable(),
            packedNormalBytes);
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(offset, sizeof(float))));

    private static uint ReadUInt32(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        RequireAvailable(bytes, position, sizeof(uint), label);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.Slice(position, sizeof(uint)));
        position += sizeof(uint);
        return value;
    }

    private static ulong ReadUInt64(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        RequireAvailable(bytes, position, sizeof(ulong), label);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.Slice(position, sizeof(ulong)));
        position += sizeof(ulong);
        return value;
    }

    private static ushort ReadUInt16(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        RequireAvailable(bytes, position, sizeof(ushort), label);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.Slice(position, sizeof(ushort)));
        position += sizeof(ushort);
        return value;
    }

    private static bool ReadBoolean(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        RequireAvailable(bytes, position, sizeof(byte), label);
        byte value = bytes[position++];
        if (value > 1)
            throw Invalid($"{label} must be encoded as 0 or 1.");
        return value == 1;
    }

    private static byte ReadByte(
        ReadOnlySpan<byte> bytes,
        ref int position,
        string label)
    {
        RequireAvailable(bytes, position, sizeof(byte), label);
        return bytes[position++];
    }

    private static void Skip(
        ReadOnlySpan<byte> bytes,
        ref int position,
        int count,
        string label)
    {
        RequireAvailable(bytes, position, count, label);
        position += count;
    }

    private static void RequireAvailable(
        ReadOnlySpan<byte> bytes,
        int position,
        int count,
        string label)
    {
        if (position < 0 || count < 0 || position > bytes.Length - count)
            throw Invalid($"{label} is truncated.");
    }

    private static int CheckedPositiveCount(uint value, int maximum, string label)
    {
        int count = CheckedCount(value, label);
        if (count <= 0 || count > maximum)
            throw Invalid($"{label} {count} is outside the 1-{maximum} safety bound.");
        return count;
    }

    private static int CheckedCount(uint value, string label)
    {
        if (value > int.MaxValue)
            throw Invalid($"{label} exceeds the supported address space.");
        return checked((int)value);
    }

    private static bool IsDynamicShape(SseNifBlock block) =>
        string.Equals(block.Type, "BSDynamicTriShape", StringComparison.Ordinal);

    private static bool IsCanonicalNifPath(AssetPath path) =>
        !string.IsNullOrWhiteSpace(path.Value) &&
        path.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) &&
        path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) &&
        !path.Value.Contains('\\');

    private static AssetPath CanonicalTexturePath(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Data/".Length..];
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        var path = new AssetPath(normalized);
        if (!path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw Invalid(
                $"Reachable shader texture '{value}' is not a canonical DDS route.");
        return path;
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static InvalidDataException Invalid(string message) => new(message);

    private static SseSelectedHeadpartNifGeometryReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record SseSkinPartitionGeometry(
        ImmutableArray<int> TriangleIndices,
        ImmutableArray<Vector2> TextureCoordinates,
        ImmutableArray<Vector3> Normals,
        ImmutableArray<SseSelectedHeadpartPackedNormalSentinel>
            PackedNormalSentinels,
        ImmutableArray<byte> PackedNormalBytes);

    private sealed record SsePackedNormalRead(
        ImmutableArray<Vector3> Normals,
        ImmutableArray<SseSelectedHeadpartPackedNormalSentinel>
            PackedNormalSentinels,
        ImmutableArray<byte> PackedNormalBytes);
}
