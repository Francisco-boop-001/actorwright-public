using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

/// <summary>
/// Exact, fail-closed HairTint patcher for complete Skyrim SE FaceGeom NIFs.
/// It changes only the trailing RGB float triplet of the named shapes'
/// BSLightingShaderProperty blocks.
/// </summary>
public sealed class SkyrimFollowerFinishPairFaceGeomService :
    ISkyrimFollowerFinishPairFaceGeomService
{
    private const uint HairTintShaderType = 6;

    public SkyrimFollowerFinishPairFaceGeomRewrite Rewrite(
        ImmutableArray<byte> source,
        SkyrimFollowerFinishPairHairFinish request)
    {
        ValidateRequest(request);
        byte[] sourceBytes = source.ToArray();
        SseNifDocument document =
            SseFaceGeomCarrierCodec.Parse(sourceBytes);
        ImmutableArray<int> tintOffsets =
            ResolveTintOffsets(document, request);
        byte[] output = sourceBytes.ToArray();
        foreach (int offset in tintOffsets)
        {
            WriteRgb(output, offset, request.NewFaceGeomRgb);
        }

        ImmutableArray<int> changed = Enumerable.Range(
                0,
                sourceBytes.Length)
            .Where(index => sourceBytes[index] != output[index])
            .ToImmutableArray();
        if (changed.IsDefaultOrEmpty)
            throw new InvalidDataException(
                "The FaceGeom hair-tint request produced no byte change.");

        Verify(
            source,
            output.ToImmutableArray(),
            request,
            changed);
        return new SkyrimFollowerFinishPairFaceGeomRewrite(
            output.ToImmutableArray(),
            changed);
    }

    public void Verify(
        ImmutableArray<byte> source,
        ImmutableArray<byte> output,
        SkyrimFollowerFinishPairHairFinish request,
        ImmutableArray<int> expectedChangedByteOffsets)
    {
        ValidateRequest(request);
        if (source.Length != output.Length)
            throw new InvalidDataException(
                "A FaceGeom HairTint rewrite changed file length.");

        byte[] sourceBytes = source.ToArray();
        byte[] outputBytes = output.ToArray();
        SseNifDocument sourceDocument =
            SseFaceGeomCarrierCodec.Parse(sourceBytes);
        SseNifDocument outputDocument =
            SseFaceGeomCarrierCodec.Parse(outputBytes);
        ImmutableArray<int> sourceOffsets =
            ResolveTintOffsets(sourceDocument, request);
        ImmutableArray<int> outputOffsets =
            ResolveTintOffsets(
                outputDocument,
                request with
                {
                    OldFaceGeomRgb = request.NewFaceGeomRgb
                });
        if (!sourceOffsets.SequenceEqual(outputOffsets))
            throw new InvalidDataException(
                "The FaceGeom HairTint rewrite changed NIF block routing.");

        ImmutableArray<int> actualChanged = Enumerable.Range(
                0,
                sourceBytes.Length)
            .Where(index => sourceBytes[index] != outputBytes[index])
            .ToImmutableArray();
        if (!actualChanged.SequenceEqual(expectedChangedByteOffsets))
            throw new InvalidDataException(
                "The FaceGeom HairTint rewrite changed unreviewed bytes.");

        ImmutableHashSet<int> allowed = sourceOffsets
            .SelectMany(offset => Enumerable.Range(offset, 12))
            .ToImmutableHashSet();
        if (actualChanged.Any(offset => !allowed.Contains(offset)))
            throw new InvalidDataException(
                "The FaceGeom HairTint rewrite escaped the named RGB fields.");
    }

    private static ImmutableArray<int> ResolveTintOffsets(
        SseNifDocument document,
        SkyrimFollowerFinishPairHairFinish request)
    {
        var requestedNames = request.FaceGeomShapeNames
            .ToImmutableHashSet(StringComparer.Ordinal);
        SseNifBlock[] shapes = document.Blocks
            .Where(block =>
                block.Name is not null &&
                requestedNames.Contains(block.Name))
            .ToArray();
        if (shapes.Length != requestedNames.Count ||
            shapes.Select(shape => shape.Name)
                .Distinct(StringComparer.Ordinal).Count() !=
            requestedNames.Count)
            throw new InvalidDataException(
                "Every requested FaceGeom hair shape must resolve exactly once.");

        var offsets = ImmutableArray.CreateBuilder<int>(
            requestedNames.Count);
        var shaderIndexes = new HashSet<int>();
        foreach (SseNifBlock shape in shapes
                     .OrderBy(value =>
                         request.FaceGeomShapeNames.IndexOf(value.Name!)))
        {
            if (shape.Type is not ("BSTriShape" or "BSDynamicTriShape"))
                throw new InvalidDataException(
                    $"Requested FaceGeom block is not a shape: {shape.Name}");
            SseNifReference shaderReference = shape.References
                .Where(reference => reference.Kind == "shader")
                .Single();
            if (shaderReference.Target < 0 ||
                !shaderIndexes.Add(shaderReference.Target))
                throw new InvalidDataException(
                    "Each requested hair shape must own one distinct shader.");
            SseNifBlock shader =
                document.Blocks[shaderReference.Target];
            if (shader.Type != "BSLightingShaderProperty" ||
                shader.Size < 16 ||
                BinaryPrimitives.ReadUInt32LittleEndian(
                    document.Data.AsSpan(shader.Offset, 4)) !=
                HairTintShaderType)
                throw new InvalidDataException(
                    $"Requested hair shape does not use HairTint: {shape.Name}");
            if (document.Blocks.Any(block =>
                    !requestedNames.Contains(block.Name ?? string.Empty) &&
                    block.References.Any(reference =>
                        reference.Kind == "shader" &&
                        reference.Target == shader.Index)))
                throw new InvalidDataException(
                    $"A requested HairTint shader is shared by another shape: {shape.Name}");
            int tintOffset = checked(
                shader.Offset + shader.Size - 12);
            RequireRgb(
                document.Data,
                tintOffset,
                request.OldFaceGeomRgb,
                shape.Name!);
            offsets.Add(tintOffset);
        }

        return offsets.ToImmutable();
    }

    private static void RequireRgb(
        byte[] bytes,
        int offset,
        ImmutableArray<byte> expected,
        string shapeName)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            float actual = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(offset + channel * 4, 4));
            float wanted = expected[channel] / 255f;
            if (BitConverter.SingleToInt32Bits(actual) !=
                BitConverter.SingleToInt32Bits(wanted))
                throw new InvalidDataException(
                    $"FaceGeom shape {shapeName} does not carry the bound source HairTint.");
        }
    }

    private static void WriteRgb(
        byte[] bytes,
        int offset,
        ImmutableArray<byte> rgb)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(offset + channel * 4, 4),
                rgb[channel] / 255f);
        }
    }

    private static void ValidateRequest(
        SkyrimFollowerFinishPairHairFinish request)
    {
        if (request.ColorFormId.Value is < 0x800 or > 0xFFF ||
            request.OldPackedRgb > 0xFFFFFF ||
            request.NewPackedRgb > 0xFFFFFF ||
            request.OldPackedRgb == request.NewPackedRgb ||
            request.FaceGeomShapeNames.IsDefaultOrEmpty ||
            request.FaceGeomShapeNames.Length > 16 ||
            request.FaceGeomShapeNames.Any(string.IsNullOrWhiteSpace) ||
            request.FaceGeomShapeNames.Distinct(StringComparer.Ordinal)
                .Count() != request.FaceGeomShapeNames.Length ||
            request.OldFaceGeomRgb.Length != 3 ||
            request.NewFaceGeomRgb.Length != 3 ||
            request.OldFaceGeomRgb.SequenceEqual(
                request.NewFaceGeomRgb))
            throw new InvalidDataException(
                "The paired FaceGeom HairTint request is invalid.");
    }
}
