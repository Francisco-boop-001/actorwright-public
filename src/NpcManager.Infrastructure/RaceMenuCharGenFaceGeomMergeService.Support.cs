using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuCharGenFaceGeomMergeService
{
    private static async ValueTask<ImmutableArray<DispositionWork>> BuildDispositionWorkAsync(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        SseNifDocument charGen,
        SseNifDocument carrier,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var charGenShapes = DynamicShapesByName(charGen, "CharGen", diagnostics);
        var carrierShapes = DynamicShapesByName(carrier, "carrier", diagnostics);
        if (HasErrors(diagnostics)) return [];
        if (charGenShapes.Count == 0 || charGenShapes.Count > carrierShapes.Count ||
            charGenShapes.Keys.Any(name => !carrierShapes.ContainsKey(name)))
        {
            diagnostics.Add(Error("chargen-carrier-shape-subset",
                "CharGen dynamic-shape names must be a non-empty subset of the complete-carrier names."));
            return [];
        }

        var authorities = request.ShapeAuthorities.ToDictionary(item => item.CarrierShapeName,
            StringComparer.Ordinal);
        if (!authorities.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(carrierShapes.Keys))
        {
            diagnostics.Add(Error("chargen-carrier-authority-coverage",
                "Every complete-carrier dynamic shape requires exactly one explicit authority row."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<DispositionWork>(carrierShapes.Count);
        var xyzCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var carrierShape in carrierShapes.Values.OrderBy(item => item.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = carrierShape.Name!;
            var carrierLayout = RequireDynamicLayout(carrierShape);
            var carrierPayload = carrierShape.GeometryPayload ??
                throw new InvalidDataException($"Carrier shape '{name}' has no geometry payload.");
            var carrierPositions = ExtractPositions(carrier.Data, carrierLayout);
            var carrierPositionHash = Hash(carrierPositions);
            var carrierFourthHash = Hash(ExtractFourthLanes(carrier.Data, carrierLayout));
            var carrierTopologyHash = TopologyHash(carrier, carrierShape);
            var authority = authorities[name];
            byte[] sourcePositions;
            RaceMenuCharGenFaceGeomSourceEvidence evidence;

            switch (authority)
            {
                case RaceMenuCharGenFaceGeomCharGenXyzAuthority charGenAuthority:
                    {
                        if (!charGenShapes.TryGetValue(name, out var sourceShape))
                            throw new InvalidDataException(
                                $"CharGenXyz authority '{name}' has no name-matched CharGen shape.");
                        var sourceLayout = RequireDynamicLayout(sourceShape);
                        var sourcePayload = sourceShape.GeometryPayload ??
                            throw new InvalidDataException($"CharGen shape '{name}' has no geometry payload.");
                        var sourceTopologyHash = TopologyHash(charGen, sourceShape);
                        if (!string.Equals(sourceShape.Type, carrierShape.Type, StringComparison.Ordinal) ||
                            sourceShape.Size != carrierShape.Size ||
                            sourcePayload.Length != carrierPayload.Length ||
                            sourceLayout.VertexCount != carrierLayout.VertexCount ||
                            sourceTopologyHash != carrierTopologyHash ||
                            sourceTopologyHash != charGenAuthority.ExpectedTopologySha256)
                            throw new InvalidDataException(
                                $"CharGen shape '{name}' does not exactly match carrier type, block size, payload length, vertex count, and topology.");
                        sourcePositions = ExtractPositions(charGen.Data, sourceLayout);
                        evidence = new RaceMenuCharGenFaceGeomCharGenSourceEvidence(
                            sourceShape.Index,
                            sourceShape.Size,
                            sourcePayload.Length,
                            sourceLayout.VertexDataOffset,
                            sourceTopologyHash,
                            Hash(sourcePositions));
                        break;
                    }
                case RaceMenuCharGenFaceGeomExternalXyzAuthority external:
                    {
                        if (external.ExpectedVertexCount != carrierLayout.VertexCount)
                            throw new InvalidDataException(
                                $"External oracle '{name}' vertex count does not match the carrier.");
                        if (external.ExpectedTopologySha256 != carrierTopologyHash)
                            throw new InvalidDataException(
                                $"External oracle '{name}' topology authority does not match the carrier.");
                        var cacheKey = $"{external.XyzFile.Value}|{external.ExpectedXyzSha256.Value}";
                        if (!xyzCache.TryGetValue(cacheKey, out sourcePositions!))
                        {
                            var oracle = await ReadBoundedFileAsync(external.XyzFile,
                                MaximumXyzBytes, $"external XYZ oracle '{name}'", diagnostics,
                                cancellationToken);
                            if (oracle is null) return [];
                            sourcePositions = oracle;
                            xyzCache.Add(cacheKey, oracle);
                        }
                        if (Hash(sourcePositions) != external.ExpectedXyzSha256 ||
                            sourcePositions.Length != checked(external.ExpectedVertexCount * 12))
                        {
                            diagnostics.Add(Error("chargen-carrier-oracle-binding",
                                $"External XYZ oracle '{name}' hash or byte length does not match its authority."));
                            return [];
                        }
                        evidence = new RaceMenuCharGenFaceGeomExternalXyzSourceEvidence(
                            external.XyzFile,
                            external.ExpectedXyzSha256,
                            external.ExpectedVertexCount,
                            external.ExpectedTopologySha256,
                            Hash(sourcePositions));
                        break;
                    }
                case RaceMenuCharGenFaceGeomGeneratedXyzAuthority generated:
                    {
                        if (generated.ExpectedVertexCount != carrierLayout.VertexCount)
                            throw new InvalidDataException(
                                $"Generated XYZ '{name}' vertex count does not match the carrier.");
                        if (generated.ExpectedTopologySha256 != carrierTopologyHash)
                            throw new InvalidDataException(
                                $"Generated XYZ '{name}' topology authority does not match the carrier.");
                        var cacheKey =
                            $"{generated.GeneratedXyzFile.Value}|{generated.ExpectedGeneratedXyzSha256.Value}";
                        if (!xyzCache.TryGetValue(cacheKey, out sourcePositions!))
                        {
                            var generatedXyz = await ReadBoundedFileAsync(
                                generated.GeneratedXyzFile, MaximumXyzBytes,
                                $"generated XYZ '{name}'", diagnostics, cancellationToken);
                            if (generatedXyz is null) return [];
                            sourcePositions = generatedXyz;
                            xyzCache.Add(cacheKey, generatedXyz);
                        }
                        if (Hash(sourcePositions) != generated.ExpectedGeneratedXyzSha256 ||
                            sourcePositions.Length != checked(generated.ExpectedVertexCount * 12))
                        {
                            diagnostics.Add(Error("chargen-carrier-generated-xyz-binding",
                                $"Generated XYZ '{name}' hash or byte length does not match its authority."));
                            return [];
                        }
                        evidence = new RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence(
                            generated.GeneratedXyzFile,
                            generated.ExpectedGeneratedXyzSha256,
                            generated.ExpectedVertexCount,
                            generated.ExpectedTopologySha256,
                            Hash(sourcePositions));
                        break;
                    }
                case RaceMenuCharGenFaceGeomCarrierPreservedAuthority preserved:
                    {
                        if (preserved.ExpectedCarrierPositionSha256 != carrierPositionHash)
                            throw new InvalidDataException(
                                $"CarrierPreserved authority '{name}' does not bind the exact carrier position hash.");
                        if (preserved.ExpectedTopologySha256 != carrierTopologyHash)
                            throw new InvalidDataException(
                                $"CarrierPreserved authority '{name}' topology does not match the carrier.");
                        sourcePositions = carrierPositions;
                        evidence = new RaceMenuCharGenFaceGeomCarrierPreservedSourceEvidence(
                            carrierPositionHash, carrierTopologyHash);
                        break;
                    }
                default:
                    throw new InvalidDataException(
                        $"Shape authority '{name}' has an unsupported typed route.");
            }

            ValidateFinitePositions(sourcePositions, name);
            var (carrierRadius, requiredRadius, outputRadius) = ResolveRadius(
                carrier.Data, carrierLayout, sourcePositions, name);
            var disposition = new RaceMenuCharGenFaceGeomShapeDisposition(
                name,
                authority.Reason,
                evidence,
                carrierShape.Index,
                carrierShape.Size,
                carrierPayload.Length,
                carrierLayout.VertexDataOffset,
                carrierLayout.VertexCount,
                carrierLayout.VertexStride,
                PositionLaneLength: 12,
                carrierTopologyHash,
                carrierPositionHash,
                carrierFourthHash,
                carrierLayout.RadiusOffset,
                carrierRadius,
                requiredRadius,
                outputRadius,
                PositionChanged: evidence.PositionSha256 != carrierPositionHash,
                RadiusChanged: BitConverter.SingleToInt32Bits(outputRadius) !=
                               BitConverter.SingleToInt32Bits(carrierRadius));
            result.Add(new DispositionWork(disposition, sourcePositions));
        }
        return result.ToImmutable();
    }

    private static byte[] ApplyPositions(
        SseNifDocument carrier,
        ImmutableArray<DispositionWork> routed)
    {
        var output = (byte[])carrier.Data.Clone();
        foreach (var item in routed)
        {
            var disposition = item.Disposition;
            for (var vertex = 0; vertex < disposition.VertexCount; vertex++)
            {
                item.PositionBytes.AsSpan(vertex * disposition.PositionLaneLength,
                        disposition.PositionLaneLength)
                    .CopyTo(output.AsSpan(
                        disposition.CarrierVertexDataOffset + vertex * disposition.VertexStride,
                        disposition.PositionLaneLength));
            }
            if (disposition.RadiusChanged)
                BinaryPrimitives.WriteInt32LittleEndian(
                    output.AsSpan(disposition.CarrierRadiusOffset, sizeof(float)),
                    BitConverter.SingleToInt32Bits(disposition.OutputRadius));
        }
        return output;
    }

    private static ImmutableArray<string> VerifyOutputSurface(
        SseNifDocument carrier,
        SseNifDocument output,
        ImmutableArray<RaceMenuCharGenFaceGeomShapeDisposition> dispositions,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var carrierShapes = DynamicShapesByName(carrier, "carrier", diagnostics);
        var outputShapes = DynamicShapesByName(output, "output", diagnostics);
        if (HasErrors(diagnostics)) return [];
        if (dispositions.Length != carrierShapes.Count ||
            !dispositions.Select(item => item.CarrierShapeName)
                .ToHashSet(StringComparer.Ordinal).SetEquals(carrierShapes.Keys))
        {
            diagnostics.Add(Error("chargen-carrier-verification-coverage",
                "The proposal does not explicitly cover every output carrier shape."));
            return [];
        }

        var authorized = new bool[carrier.Data.Length];
        var verified = ImmutableArray.CreateBuilder<string>(dispositions.Length);
        foreach (var disposition in dispositions)
        {
            var errorCountBefore = diagnostics.Count(item =>
                item.Severity == DiagnosticSeverity.Error);
            if (!carrierShapes.TryGetValue(disposition.CarrierShapeName, out var carrierShape) ||
                !outputShapes.TryGetValue(disposition.CarrierShapeName, out var outputShape))
            {
                diagnostics.Add(Error("chargen-carrier-verification-shape",
                    $"Required shape '{disposition.CarrierShapeName}' is missing after materialization."));
                continue;
            }
            var carrierLayout = RequireDynamicLayout(carrierShape);
            var outputLayout = RequireDynamicLayout(outputShape);
            var carrierPayload = carrierShape.GeometryPayload!;
            var outputPayload = outputShape.GeometryPayload!;
            if (carrierShape.Index != disposition.CarrierBlockIndex ||
                outputShape.Index != disposition.CarrierBlockIndex ||
                carrierShape.Size != disposition.CarrierBlockSize ||
                outputShape.Size != disposition.CarrierBlockSize ||
                carrierPayload.Length != disposition.CarrierGeometryPayloadLength ||
                outputPayload.Length != disposition.CarrierGeometryPayloadLength ||
                carrierLayout != outputLayout ||
                carrierLayout.VertexDataOffset != disposition.CarrierVertexDataOffset ||
                carrierLayout.VertexCount != disposition.VertexCount ||
                carrierLayout.VertexStride != disposition.VertexStride ||
                disposition.PositionLaneLength != 12)
            {
                diagnostics.Add(Error("chargen-carrier-verification-layout",
                    $"Shape '{disposition.CarrierShapeName}' layout differs from its proposal."));
                continue;
            }

            var outputPositions = ExtractPositions(output.Data, outputLayout);
            var carrierPositions = ExtractPositions(carrier.Data, carrierLayout);
            var outputFourth = ExtractFourthLanes(output.Data, outputLayout);
            if (Hash(outputPositions) != disposition.Source.PositionSha256 ||
                Hash(carrierPositions) != disposition.CarrierPositionSha256 ||
                Hash(outputFourth) != disposition.CarrierFourthLaneSha256 ||
                !outputFourth.AsSpan().SequenceEqual(
                    ExtractFourthLanes(carrier.Data, carrierLayout)))
                diagnostics.Add(Error("chargen-carrier-verification-lanes",
                    $"Shape '{disposition.CarrierShapeName}' does not preserve exact source XYZ and carrier fourth lanes."));
            if (TopologyHash(carrier, carrierShape) != disposition.CarrierTopologySha256 ||
                TopologyHash(output, outputShape) != disposition.CarrierTopologySha256)
                diagnostics.Add(Error("chargen-carrier-verification-topology",
                    $"Shape '{disposition.CarrierShapeName}' topology changed."));

            for (var vertex = 0; vertex < disposition.VertexCount; vertex++)
                Array.Fill(authorized, true,
                    disposition.CarrierVertexDataOffset + vertex * disposition.VertexStride,
                    disposition.PositionLaneLength);
            if (disposition.RadiusChanged)
                Array.Fill(authorized, true, disposition.CarrierRadiusOffset, sizeof(float));
            var carrierRadius = ReadSingle(carrier.Data, disposition.CarrierRadiusOffset);
            var outputRadius = ReadSingle(output.Data, disposition.CarrierRadiusOffset);
            if (BitConverter.SingleToInt32Bits(carrierRadius) !=
                    BitConverter.SingleToInt32Bits(disposition.CarrierRadius) ||
                BitConverter.SingleToInt32Bits(outputRadius) !=
                    BitConverter.SingleToInt32Bits(disposition.OutputRadius) ||
                outputRadius < disposition.RequiredRadius)
                diagnostics.Add(Error("chargen-carrier-verification-radius",
                    $"Shape '{disposition.CarrierShapeName}' bounds radius does not match its bounded proposal."));
            if (diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error) ==
                errorCountBefore)
                verified.Add(disposition.CarrierShapeName);
        }

        if (carrier.Data.Length != output.Data.Length)
            diagnostics.Add(Error("chargen-carrier-verification-length",
                "Output byte length differs from the complete carrier."));
        else
        {
            for (var index = 0; index < carrier.Data.Length; index++)
            {
                if (!authorized[index] && carrier.Data[index] != output.Data[index])
                {
                    diagnostics.Add(Error("chargen-carrier-verification-byte-surface",
                        $"Output byte {index} changed outside authorized XYZ/radius lanes."));
                    break;
                }
            }
        }
        return verified.ToImmutable();
    }

    private static Dictionary<string, SseNifBlock> DynamicShapesByName(
        SseNifDocument document,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var shapes = document.Blocks.Where(item =>
                string.Equals(item.Type, "BSDynamicTriShape", StringComparison.Ordinal))
            .ToArray();
        if (shapes.Length == 0 || shapes.Any(item => string.IsNullOrWhiteSpace(item.Name)) ||
            shapes.Select(item => item.Name!).Distinct(StringComparer.Ordinal).Count() != shapes.Length)
        {
            diagnostics.Add(Error("chargen-carrier-shape-names",
                $"The {role} must contain distinct non-empty BSDynamicTriShape names."));
            return new Dictionary<string, SseNifBlock>(StringComparer.Ordinal);
        }
        return shapes.ToDictionary(item => item.Name!, StringComparer.Ordinal);
    }

    internal static SseNifDynamicGeometryLayout RequireDynamicLayout(SseNifBlock shape) =>
        shape.DynamicGeometry ?? throw new InvalidDataException(
            $"Shape '{shape.Name}' has no admitted packed float4 dynamic layout.");

    internal static byte[] ExtractPositions(byte[] data, SseNifDynamicGeometryLayout layout)
    {
        var result = new byte[checked(layout.VertexCount * 12)];
        for (var vertex = 0; vertex < layout.VertexCount; vertex++)
            data.AsSpan(layout.VertexDataOffset + vertex * layout.VertexStride, 12)
                .CopyTo(result.AsSpan(vertex * 12, 12));
        return result;
    }

    private static byte[] ExtractFourthLanes(byte[] data, SseNifDynamicGeometryLayout layout)
    {
        var result = new byte[checked(layout.VertexCount * sizeof(float))];
        for (var vertex = 0; vertex < layout.VertexCount; vertex++)
            data.AsSpan(layout.VertexDataOffset + vertex * layout.VertexStride + 12, sizeof(float))
                .CopyTo(result.AsSpan(vertex * sizeof(float), sizeof(float)));
        return result;
    }

    internal static Sha256Hash TopologyHash(SseNifDocument document, SseNifBlock shape)
        => SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(document, shape);

    private static (float Carrier, float Required, float Output) ResolveRadius(
        byte[] carrier,
        SseNifDynamicGeometryLayout layout,
        byte[] positions,
        string name)
    {
        var centerX = ReadSingle(carrier, layout.BoundsOffset);
        var centerY = ReadSingle(carrier, layout.BoundsOffset + sizeof(float));
        var centerZ = ReadSingle(carrier, layout.BoundsOffset + 2 * sizeof(float));
        var radius = ReadSingle(carrier, layout.RadiusOffset);
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY) ||
            !float.IsFinite(centerZ) || !float.IsFinite(radius) || radius < 0F)
            throw new InvalidDataException($"Shape '{name}' has invalid carrier bounds.");
        double maximum = 0;
        for (var vertex = 0; vertex < layout.VertexCount; vertex++)
        {
            var offset = vertex * 12;
            var x = ReadSingle(positions, offset);
            var y = ReadSingle(positions, offset + sizeof(float));
            var z = ReadSingle(positions, offset + 2 * sizeof(float));
            var dx = (double)x - centerX;
            var dy = (double)y - centerY;
            var dz = (double)z - centerZ;
            maximum = Math.Max(maximum, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        var required = checked((float)maximum);
        if (!float.IsFinite(required))
            throw new InvalidDataException($"Shape '{name}' requires a non-finite radius.");
        var output = radius >= required ? radius : CeilingSingle(maximum);
        return (radius, required, output);
    }

    private static float CeilingSingle(double value)
    {
        var rounded = checked((float)value);
        if (rounded >= value) return rounded;
        var bits = BitConverter.SingleToInt32Bits(rounded);
        return BitConverter.Int32BitsToSingle(checked(bits + 1));
    }

    private static void ValidateFinitePositions(byte[] positions, string name)
    {
        if (positions.Length == 0 || positions.Length % 12 != 0)
            throw new InvalidDataException($"Shape '{name}' XYZ byte length is invalid.");
        for (var offset = 0; offset < positions.Length; offset += sizeof(float))
            if (!float.IsFinite(ReadSingle(positions, offset)))
                throw new InvalidDataException($"Shape '{name}' contains a non-finite XYZ value.");
    }

    private static float ReadSingle(byte[] data, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            data.AsSpan(offset, sizeof(float))));

    private static Sha256Hash Hash(byte[] bytes) => Hash(bytes.AsSpan());

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
