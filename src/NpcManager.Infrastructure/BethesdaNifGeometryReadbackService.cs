using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reopens admitted Bethesda NIF bytes and derives geometry evidence from the
/// parsed block table. It deliberately does not consume Blender/PyNifly
/// status, source-side geometry hashes, or an in-memory writer result.
/// </summary>
public sealed class BethesdaNifGeometryReadbackService : INifGeometryReadbackService
{
    private const long MaximumNifBytes = 512L * 1024 * 1024;
    private const int MaximumBlockCount = 10_000;
    private const int MaximumShapeCount = 128;
    private const int MaximumVertexCount = 1_000_000;
    private const int MaximumTriangleCount = 2_000_000;
    private const uint NifVersion = 0x14020007;
    private const uint Fallout4StreamVersion = 130;

    public async ValueTask<NifGeometryReadbackResult> ReadAsync(
        NifGeometryReadbackRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.NifPath.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("nif-geometry-readback-path",
                "NIF geometry readback requires a .nif path."));
            return Refused(diagnostics);
        }

        byte[] bytes;
        try
        {
            if (!File.Exists(request.NifPath.Value))
            {
                diagnostics.Add(Error("nif-geometry-readback-missing",
                    $"NIF geometry readback source '{request.NifPath.Value}' does not exist."));
                return Refused(diagnostics);
            }

            var info = new FileInfo(request.NifPath.Value);
            if (info.Length <= 0 || info.Length > MaximumNifBytes)
            {
                diagnostics.Add(Error("nif-geometry-readback-size",
                    $"NIF geometry readback source must contain 1..{MaximumNifBytes} bytes."));
                return Refused(diagnostics);
            }

            bytes = await File.ReadAllBytesAsync(request.NifPath.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException exception)
        {
            diagnostics.Add(Error("nif-geometry-readback-io", exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(Error("nif-geometry-readback-access", exception.Message));
            return Refused(diagnostics);
        }

        Sha256Hash fileHash = Hash(bytes);
        if (request.ExpectedSha256 is { } expected && expected != fileHash)
        {
            diagnostics.Add(Error("nif-geometry-readback-hash-mismatch",
                $"NIF geometry readback source hash {fileHash} does not match {expected}."));
            return Refused(diagnostics);
        }

        try
        {
            ReadbackBundle bundle = request.Edition switch
            {
                GameEdition.SkyrimSpecialEdition => ReadSkyrim(bytes),
                GameEdition.Fallout4 => ReadFallout4(bytes),
                _ => throw Invalid("NIF geometry readback requires Skyrim SE or Fallout 4.")
            };
            var resultDocument = new NifGeometryReadbackDocument(
                request.Edition,
                request.NifPath,
                fileHash,
                bytes.LongLength,
                bundle.BlockCount,
                bundle.ReachableBlockCount,
                bundle.Shapes,
                bundle.AggregateGeometrySha256,
                bundle.GraphSha256);
            diagnostics.Add(new Diagnostic(
                "nif-geometry-readback-complete",
                DiagnosticSeverity.Info,
                $"Read {bundle.Shapes.Length} ordered triangle shape(s) and {bundle.Shapes.Sum(shape => shape.VertexCount)} vertices from '{request.NifPath.Value}'."));
            return new NifGeometryReadbackResult(true, resultDocument, diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("nif-geometry-readback-malformed", exception.Message));
            return Refused(diagnostics);
        }
        catch (OverflowException exception)
        {
            diagnostics.Add(Error("nif-geometry-readback-overflow", exception.Message));
            return Refused(diagnostics);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("nif-geometry-readback-malformed", exception.Message));
            return Refused(diagnostics);
        }
    }

    private static ReadbackBundle ReadSkyrim(byte[] bytes)
    {
        SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
        HashSet<int> reachable = SseFaceGeomCarrierCodec.FindReachableBlockIndexes(document);
        SseNifBlock[] recognized = document.Blocks
            .Where(block => block.Type is "BSDynamicTriShape" or "BSTriShape" or
                "BSSubIndexTriShape")
            .OrderBy(block => block.Index)
            .ToArray();
        if (recognized.Length is <= 0 or > MaximumShapeCount)
            throw Invalid($"Recognized Bethesda triangle-shape count {recognized.Length} is outside the safety bound.");
        if (recognized.Any(shape => !reachable.Contains(shape.Index)))
            throw Invalid("The NIF contains an unreachable recognized triangle shape.");

        var shapes = ImmutableArray.CreateBuilder<NifGeometryShapeReadback>(recognized.Length);
        foreach (SseNifBlock shape in recognized)
            shapes.Add(ReadSkyrimShape(document, shape));
        ImmutableArray<NifGeometryShapeReadback> ordered = shapes.MoveToImmutable();
        ValidateShapeIdentity(ordered);
        ValidateRanges(ordered, bytes.LongLength);
        return new ReadbackBundle(
            document.Blocks.Length,
            reachable.Count,
            ordered,
            ComputeAggregate(document.Data, ordered),
            SseFaceGeomCarrierCodec.BuildStructure(document).GraphSha256);
    }

    private static NifGeometryShapeReadback ReadSkyrimShape(
        SseNifDocument document,
        SseNifBlock shape)
    {
        if (shape.DynamicGeometry is { } dynamicGeometry)
        {
            int vertexCount = dynamicGeometry.VertexCount;
            int vertexStride = dynamicGeometry.VertexStride;
            int vertexPayloadLength = checked(vertexCount * vertexStride);
            int vertexDataOffset = dynamicGeometry.VertexDataOffset;
            int descriptorOffset = shape.GeometryPayload!.Offset;
            ulong descriptor = ReadUInt64At(document.Data, descriptorOffset);
            ValidateDescriptor(descriptor, skyrim: true, separatePositions: true);
            if (vertexCount <= 0 || vertexCount > MaximumVertexCount || vertexStride <= 0 ||
                vertexPayloadLength <= 0)
                throw Invalid($"Shape '{shape.Name}' has an invalid vertex count or stride.");
            ValidatePayloadInBlock(shape, vertexDataOffset, vertexPayloadLength);
            ValidateDescriptorInBlock(shape, descriptorOffset);
            return new NifGeometryShapeReadback(
                RequireShapeName(shape), shape.Type, shape.Index, vertexCount, descriptor,
                vertexDataOffset, vertexPayloadLength,
                Hash(document.Data.AsSpan(vertexDataOffset, vertexPayloadLength)),
                SseFaceGeomCarrierCodec.ComputeDynamicShapeTopologyHash(document, shape));
        }

        if (shape.GeometryPayload is not { } geometry)
            throw Invalid($"Recognized shape '{shape.Name}' lacks an admitted vertex layout.");
        var reader = new BoundedReader(document.Data, geometry.Offset,
            checked(shape.Offset + shape.Size));
        ulong staticDescriptor = reader.UInt64("vertex descriptor");
        int triangleCount = reader.UInt16("triangle count");
        int vertexCountStatic = reader.UInt16("vertex count");
        uint dataSize = reader.UInt32("vertex/triangle data size");
        int vertexStrideStatic = ValidateDescriptor(staticDescriptor, skyrim: true);
        int vertexPayloadLengthStatic = checked(vertexCountStatic * vertexStrideStatic);
        int triangleLength = checked(triangleCount * 6);
        int expectedDataSize = checked(vertexPayloadLengthStatic + triangleLength);
        if (vertexCountStatic <= 0 || vertexCountStatic > MaximumVertexCount ||
            triangleCount <= 0 || triangleCount > MaximumTriangleCount || dataSize != expectedDataSize)
            throw Invalid($"Shape '{shape.Name}' does not use an admitted contiguous Bethesda triangle-shape layout.");
        int vertexDataOffsetStatic = reader.Position;
        reader.Skip(vertexPayloadLengthStatic, "vertex data");
        int triangleDataOffset = reader.Position;
        reader.Skip(triangleLength, "triangle data");
        if (shape.Type == "BSTriShape" && shape.Offset + shape.Size - reader.Position == sizeof(uint) &&
            reader.UInt32("particle data size") != 0)
            throw Invalid("Only empty SSE BSTriShape particle data is admitted.");
        reader.RequireEnd("triangle shape");
        ValidatePayloadInBlock(shape, vertexDataOffsetStatic, vertexPayloadLengthStatic);
        ValidateDescriptorInBlock(shape, geometry.Offset);
        return new NifGeometryShapeReadback(
            RequireShapeName(shape), shape.Type, shape.Index, vertexCountStatic,
            staticDescriptor, vertexDataOffsetStatic, vertexPayloadLengthStatic,
            Hash(document.Data.AsSpan(vertexDataOffsetStatic, vertexPayloadLengthStatic)),
            Hash(document.Data.AsSpan(triangleDataOffset, triangleLength)));
    }

    private static ReadbackBundle ReadFallout4(byte[] bytes)
    {
        Fallout4Document document = Fallout4Parser.Parse(bytes);
        HashSet<int> reachable = FindReachable(document);
        Fallout4Block[] recognized = document.Blocks
            .Where(block => block.Geometry is not null)
            .OrderBy(block => block.Index)
            .ToArray();
        if (recognized.Length is <= 0 or > MaximumShapeCount)
            throw Invalid($"Recognized Fallout 4 triangle-shape count {recognized.Length} is outside the safety bound.");
        if (recognized.Any(shape => !reachable.Contains(shape.Index)))
            throw Invalid("The NIF contains an unreachable recognized triangle shape.");
        var shapes = ImmutableArray.CreateBuilder<NifGeometryShapeReadback>(recognized.Length);
        foreach (Fallout4Block shape in recognized)
        {
            Fallout4Geometry geometry = shape.Geometry!;
            shapes.Add(new NifGeometryShapeReadback(
                RequireShapeName(shape.Name, shape.Index), shape.Type, shape.Index,
                geometry.VertexCount, geometry.VertexDescriptor, geometry.VertexDataOffset,
                geometry.VertexPayloadLength,
                Hash(bytes.AsSpan(geometry.VertexDataOffset, geometry.VertexPayloadLength)),
                Hash(bytes.AsSpan(geometry.TriangleDataOffset, geometry.TriangleDataLength))));
        }
        ImmutableArray<NifGeometryShapeReadback> ordered = shapes.MoveToImmutable();
        ValidateShapeIdentity(ordered);
        ValidateRanges(ordered, bytes.LongLength);
        return new ReadbackBundle(
            document.Blocks.Length,
            reachable.Count,
            ordered,
            ComputeAggregate(bytes, ordered),
            ComputeFallout4Graph(document));
    }

    private static HashSet<int> FindReachable(Fallout4Document document)
    {
        var reachable = document.Roots.ToHashSet();
        var pending = new Stack<int>(document.Roots);
        while (pending.TryPop(out int index))
        {
            foreach (int target in document.Blocks[index].References)
                if (target >= 0 && reachable.Add(target))
                    pending.Push(target);
        }
        return reachable;
    }

    private static Sha256Hash ComputeFallout4Graph(Fallout4Document document)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(document.Roots.Length);
        foreach (int root in document.Roots)
            writer.Write(root);
        foreach (Fallout4Block block in document.Blocks.OrderBy(item => item.Index))
        {
            writer.Write(block.Index);
            WriteString(writer, block.Type);
            WriteString(writer, block.Name ?? string.Empty);
            writer.Write(block.References.Length);
            foreach (int target in block.References)
                writer.Write(target);
        }
        writer.Flush();
        return Hash(stream.ToArray());
    }

    private static Sha256Hash ComputeAggregate(
        byte[] data,
        ImmutableArray<NifGeometryShapeReadback> shapes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (NifGeometryShapeReadback shape in shapes)
        {
            WriteString(writer, shape.Name);
            WriteString(writer, shape.BlockType);
            writer.Write(shape.BlockIndex);
            writer.Write(shape.VertexCount);
            writer.Write(shape.VertexDescriptor);
            writer.Write(shape.VertexPayloadLength);
            writer.Write(data.AsSpan(checked((int)shape.VertexPayloadOffset),
                shape.VertexPayloadLength));
        }
        writer.Flush();
        return Hash(stream.ToArray());
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void ValidateShapeIdentity(
        ImmutableArray<NifGeometryShapeReadback> shapes)
    {
        if (shapes.Any(shape => string.IsNullOrWhiteSpace(shape.Name)) ||
            shapes.Select(shape => shape.Name).Distinct(StringComparer.Ordinal).Count() != shapes.Length)
            throw Invalid("Recognized triangle shape names must be non-empty and unique.");
    }

    private static void ValidateRanges(
        ImmutableArray<NifGeometryShapeReadback> shapes,
        long byteLength)
    {
        NifGeometryShapeReadback? previous = null;
        foreach (NifGeometryShapeReadback shape in shapes.OrderBy(item => item.VertexPayloadOffset))
        {
            if (shape.VertexPayloadOffset < 0 || shape.VertexPayloadLength <= 0 ||
                shape.VertexPayloadOffset > byteLength ||
                shape.VertexPayloadLength > byteLength - shape.VertexPayloadOffset)
                throw Invalid($"Shape '{shape.Name}' vertex payload is outside the NIF byte range.");
            if (previous is not null &&
                previous.VertexPayloadOffset + previous.VertexPayloadLength > shape.VertexPayloadOffset)
                throw Invalid($"Recognized vertex payloads for '{previous.Name}' and '{shape.Name}' overlap.");
            previous = shape;
        }
    }

    private static int ValidateDescriptor(ulong descriptor, bool skyrim = false, bool separatePositions = false)
    {
        int flags = checked((int)((descriptor >> 44) & 0xFFF));
        const int supportedAttributes =
            0x001 | 0x002 | 0x004 | 0x008 | 0x010 | 0x020 | 0x040 | 0x080 | 0x100 | 0x400;
        if ((flags & ~supportedAttributes) != 0 || (!separatePositions && (flags & 0x001) == 0) ||
            ((flags & 0x010) != 0 && (flags & 0x008) == 0))
            throw Invalid("Bethesda vertex descriptor contains unsupported attributes.");
        int stride = checked((int)(descriptor & 0xF) * 4);
        int computed = 0;
        if ((flags & 0x001) != 0)
            computed = skyrim || (flags & 0x400) != 0 ? 16 : 8;
        Attribute(0x002, 8, 4);
        Attribute(0x004, 12, 4);
        Attribute(0x008, 16, 4);
        Attribute(0x010, 20, 4);
        Attribute(0x020, 24, 4);
        Attribute(0x040, 28, 12);
        if ((flags & 0x080) != 0)
        {
            // LAND remains opaque; its declared interval must fit between known attributes.
            int end = (flags & 0x100) != 0 ? Offset(36) : stride;
            if (Offset(32) != computed || end <= computed || end > stride)
                throw Invalid("Bethesda LAND attribute interval overlaps or escapes the vertex stride.");
            computed = end;
        }
        Attribute(0x100, 36, 4);
        if (stride <= 0 || computed != stride)
            throw Invalid("Bethesda vertex descriptor stride is inconsistent.");
        return stride;

        int Offset(int shift) => (int)((descriptor >> shift) & 0xF) * 4;
        void Attribute(int flag, int shift, int size)
        {
            if ((flags & flag) == 0) return;
            if (Offset(shift) != computed || size > stride - computed)
                throw Invalid("Bethesda vertex attribute offset overlaps or escapes the vertex stride.");
            computed += size;
        }
    }

    private static void ValidatePayloadInBlock(SseNifBlock shape, int offset, int length)
    {
        int end = checked(shape.Offset + shape.Size);
        if (offset < shape.Offset || offset > end || length <= 0 || length > end - offset)
            throw Invalid($"Shape '{shape.Name}' vertex payload escapes its block bounds.");
    }

    private static void ValidateDescriptorInBlock(SseNifBlock shape, int offset)
    {
        int end = checked(shape.Offset + shape.Size);
        if (offset < shape.Offset || offset > end - sizeof(ulong))
            throw Invalid($"Shape '{shape.Name}' descriptor escapes its block bounds.");
    }

    private static string RequireShapeName(SseNifBlock shape) =>
        RequireShapeName(shape.Name, shape.Index);

    private static string RequireShapeName(string? name, int index) =>
        !string.IsNullOrWhiteSpace(name)
            ? name
            : throw Invalid($"Recognized triangle shape block {index} has no name.");

    private static ulong ReadUInt64At(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(ulong))
            throw Invalid("Bethesda vertex descriptor is outside the NIF byte range.");
        return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static InvalidDataException Invalid(string message) => new(message);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static NifGeometryReadbackResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record ReadbackBundle(
        int BlockCount,
        int ReachableBlockCount,
        ImmutableArray<NifGeometryShapeReadback> Shapes,
        Sha256Hash AggregateGeometrySha256,
        Sha256Hash GraphSha256);

    private sealed record Fallout4Document(
        ImmutableArray<Fallout4Block> Blocks,
        ImmutableArray<int> Roots);

    private sealed record Fallout4Block(
        int Index,
        string Type,
        string? Name,
        int Offset,
        int Size,
        ImmutableArray<int> References,
        Fallout4Geometry? Geometry);

    private sealed record Fallout4Geometry(
        int VertexDescriptorOffset,
        int VertexDataOffset,
        int VertexCount,
        int VertexStride,
        int VertexPayloadLength,
        int TriangleDataOffset,
        int TriangleDataLength,
        ulong VertexDescriptor);

    private sealed class Fallout4Parser
    {
        private readonly byte[] data;
        private readonly ImmutableArray<string> strings;
        private readonly ImmutableArray<string> typeNames;
        private readonly ushort[] typeIndexes;
        private readonly int[] blockOffsets;
        private readonly int[] blockSizes;

        private Fallout4Parser(
            byte[] data,
            ImmutableArray<string> strings,
            ImmutableArray<string> typeNames,
            ushort[] typeIndexes,
            int[] blockOffsets,
            int[] blockSizes)
        {
            this.data = data;
            this.strings = strings;
            this.typeNames = typeNames;
            this.typeIndexes = typeIndexes;
            this.blockOffsets = blockOffsets;
            this.blockSizes = blockSizes;
        }

        internal static Fallout4Document Parse(byte[] data)
        {
            int lineEnd = Array.IndexOf(data, (byte)'\n', 0, Math.Min(data.Length, 256));
            if (lineEnd < 0)
                throw Invalid("NIF header line is missing or exceeds 255 bytes.");
            string header = Encoding.ASCII.GetString(data, 0, lineEnd).TrimEnd('\r');
            if (!string.Equals(header, "Gamebryo File Format, Version 20.2.0.7", StringComparison.Ordinal))
                throw Invalid("Only the admitted Bethesda 20.2.0.7 NIF envelope is supported.");

            var reader = new BoundedReader(data, lineEnd + 1, data.Length);
            if (reader.UInt32("version") != NifVersion || reader.Byte("endian") != 1)
                throw Invalid("Only little-endian Bethesda 20.2.0.7 NIFs are supported.");
            _ = reader.UInt32("user version");
            int blockCount = CheckedCount(reader.UInt32("block count"), "block count");
            if (reader.UInt32("Bethesda stream version") != Fallout4StreamVersion)
                throw Invalid("Only the admitted Fallout 4 Bethesda stream 130 is supported.");
            for (int index = 0; index < 4; index++)
                reader.ByteSizedString($"export string {index}");

            int typeCount = reader.UInt16("block type count");
            if (typeCount <= 0 || typeCount > 1024)
                throw Invalid("Block type count is outside the safety bound.");
            var typeNames = ImmutableArray.CreateBuilder<string>(typeCount);
            for (int index = 0; index < typeCount; index++)
                typeNames.Add(reader.SizedString(256, $"block type {index}"));

            var typeIndexes = new ushort[blockCount];
            for (int index = 0; index < blockCount; index++)
            {
                typeIndexes[index] = reader.UInt16($"block type index {index}");
                if (typeIndexes[index] >= typeCount)
                    throw Invalid($"Block {index} type index exceeds the type table.");
            }

            var blockSizes = new int[blockCount];
            var blockOffsets = new int[blockCount];
            for (int index = 0; index < blockCount; index++)
            {
                uint size = reader.UInt32($"block size {index}");
                if (size > int.MaxValue)
                    throw Invalid($"Block {index} exceeds the supported size.");
                blockSizes[index] = checked((int)size);
            }

            int stringCount = CheckedCount(reader.UInt32("string count"), "string count");
            uint maxStringLength = reader.UInt32("maximum string length");
            var strings = ImmutableArray.CreateBuilder<string>(stringCount);
            for (int index = 0; index < stringCount; index++)
            {
                string value = reader.SizedString(1024 * 1024, $"string table entry {index}");
                if (maxStringLength != 0 && value.Length > maxStringLength)
                    throw Invalid($"String table entry {index} exceeds the declared maximum length.");
                strings.Add(value);
            }

            int groupCount = CheckedCount(reader.UInt32("group count"), "group count");
            reader.Skip(checked(groupCount * sizeof(uint)), "group table");
            for (int index = 0; index < blockCount; index++)
            {
                blockOffsets[index] = reader.Position;
                reader.Skip(blockSizes[index], $"block {index}");
            }

            int rootCount = CheckedCount(reader.UInt32("root count"), "root count");
            if (rootCount > blockCount)
                throw Invalid("The NIF root count exceeds the block count.");
            var roots = ImmutableArray.CreateBuilder<int>(rootCount);
            for (int index = 0; index < rootCount; index++)
            {
                int root = reader.Int32($"root {index}");
                if (root < 0 || root >= blockCount)
                    throw Invalid($"Root reference {root} is outside the block table.");
                roots.Add(root);
            }
            reader.RequireEnd("NIF footer");

            var parser = new Fallout4Parser(data, strings.ToImmutable(),
                typeNames.ToImmutable(), typeIndexes, blockOffsets, blockSizes);
            var blocks = ImmutableArray.CreateBuilder<Fallout4Block>(blockCount);
            for (int index = 0; index < blockCount; index++)
                blocks.Add(parser.ParseBlock(index));
            return new Fallout4Document(blocks.MoveToImmutable(), roots.MoveToImmutable());
        }

        private Fallout4Block ParseBlock(int index)
        {
            string type = typeNames[typeIndexes[index]];
            var reader = new BoundedReader(data, blockOffsets[index],
                checked(blockOffsets[index] + blockSizes[index]));
            return type switch
            {
                "NiNode" or "BSFadeNode" => ParseNode(index, type, reader),
                "BSTriShape" or "BSSubIndexTriShape" => ParseShape(index, type, reader),
                "BSDynamicTriShape" => throw Invalid($"Fallout 4 dynamic shape block {index} has an unknown layout."),
                _ => throw Invalid($"Fallout 4 block type '{type}' is outside the admitted readback layouts.")
            };
        }

        private Fallout4Block ParseNode(int index, string type, BoundedReader reader)
        {
            string? name = ReadAvObject(reader, index, out var references);
            int childCount = CheckedCount(reader.UInt32($"block {index} child count"), "child count");
            for (int child = 0; child < childCount; child++)
                references.Add(ReadReference(reader, index, "child"));
            int effectCount = CheckedCount(reader.UInt32($"block {index} effect count"), "effect count");
            for (int effect = 0; effect < effectCount; effect++)
                references.Add(ReadReference(reader, index, "effect"));
            reader.RequireEnd($"block {index} {type}");
            return new Fallout4Block(index, type, name, blockOffsets[index], blockSizes[index],
                references.ToImmutable(), null);
        }

        private Fallout4Block ParseShape(int index, string type, BoundedReader reader)
        {
            string? name = ReadAvObject(reader, index, out var references);
            reader.Skip(16, $"block {index} bounding sphere");
            references.Add(ReadReference(reader, index, "skin"));
            references.Add(ReadReference(reader, index, "shader"));
            references.Add(ReadReference(reader, index, "alpha"));
            int descriptorOffset = reader.Position;
            ulong descriptor = reader.UInt64($"block {index} vertex descriptor");
            int triangleCount = checked((int)reader.UInt32($"block {index} triangle count"));
            int vertexCount = reader.UInt16($"block {index} vertex count");
            uint dataSize = reader.UInt32($"block {index} data size");
            int stride = ValidateDescriptor(descriptor);
            if (triangleCount <= 0 || triangleCount > MaximumTriangleCount || vertexCount <= 0 ||
                vertexCount > MaximumVertexCount)
                throw Invalid($"Block {index} has invalid triangle or vertex counts.");
            int vertexLength = checked(vertexCount * stride);
            int triangleLength = checked(triangleCount * 6);
            if (dataSize != checked(vertexLength + triangleLength))
                throw Invalid($"Block {index} does not use an admitted contiguous Bethesda triangle-shape layout.");
            int vertexDataOffset = reader.Position;
            reader.Skip(vertexLength, $"block {index} vertex data");
            int triangleDataOffset = reader.Position;
            reader.Skip(triangleLength, $"block {index} triangle data");
            reader.RequireEnd($"block {index} {type}");
            var geometry = new Fallout4Geometry(descriptorOffset, vertexDataOffset, vertexCount,
                stride, vertexLength, triangleDataOffset, triangleLength, descriptor);
            return new Fallout4Block(index, type, name, blockOffsets[index], blockSizes[index],
                references.ToImmutable(), geometry);
        }

        private string? ReadAvObject(
            BoundedReader reader,
            int blockIndex,
            out ImmutableArray<int>.Builder references)
        {
            references = ImmutableArray.CreateBuilder<int>();
            uint nameIndex = reader.UInt32($"block {blockIndex} name index");
            string? name = nameIndex == uint.MaxValue
                ? null
                : nameIndex < strings.Length
                    ? strings[checked((int)nameIndex)]
                    : throw Invalid($"Block {blockIndex} name index exceeds the string table.");
            int extraCount = CheckedCount(reader.UInt32($"block {blockIndex} extra count"), "extra count");
            for (int extra = 0; extra < extraCount; extra++)
                references.Add(ReadReference(reader, blockIndex, "extra"));
            references.Add(ReadReference(reader, blockIndex, "controller"));
            reader.Skip(56, $"block {blockIndex} transform");
            references.Add(ReadReference(reader, blockIndex, "collision"));
            return name;
        }

        private int ReadReference(BoundedReader reader, int blockIndex, string role)
        {
            int target = reader.Int32($"block {blockIndex} {role} reference");
            if (target < -1 || target >= blockSizes.Length)
                throw Invalid($"Block {blockIndex} {role} reference is outside the block table.");
            return target;
        }
    }

    private sealed class BoundedReader
    {
        private readonly byte[] data;
        private readonly int end;

        internal BoundedReader(byte[] data, int position, int end)
        {
            if (position < 0 || end < position || end > data.Length)
                throw Invalid("Invalid bounded NIF reader range.");
            this.data = data;
            Position = position;
            this.end = end;
        }

        internal int Position { get; private set; }

        internal byte Byte(string role)
        {
            Ensure(1, role);
            return data[Position++];
        }

        internal ushort UInt16(string role)
        {
            Ensure(sizeof(ushort), role);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position, sizeof(ushort)));
            Position += sizeof(ushort);
            return value;
        }

        internal uint UInt32(string role)
        {
            Ensure(sizeof(uint), role);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position, sizeof(uint)));
            Position += sizeof(uint);
            return value;
        }

        internal int Int32(string role)
        {
            Ensure(sizeof(int), role);
            int value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Position, sizeof(int)));
            Position += sizeof(int);
            return value;
        }

        internal ulong UInt64(string role)
        {
            Ensure(sizeof(ulong), role);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(Position, sizeof(ulong)));
            Position += sizeof(ulong);
            return value;
        }

        internal string SizedString(int maximumBytes, string role)
        {
            uint length = UInt32($"{role} length");
            if (length > maximumBytes)
                throw Invalid($"{role} length exceeds the safety bound.");
            int byteLength = checked((int)length);
            Ensure(byteLength, role);
            string value = Encoding.Latin1.GetString(data, Position, byteLength);
            Position += byteLength;
            return value;
        }

        internal string ByteSizedString(string role)
        {
            int length = Byte($"{role} length");
            Ensure(length, role);
            string value = Encoding.Latin1.GetString(data, Position, length);
            Position += length;
            return value;
        }

        internal void Skip(int count, string role)
        {
            Ensure(count, role);
            Position += count;
        }

        internal void RequireEnd(string role)
        {
            if (Position != end)
                throw Invalid($"{role} consumed {Position} instead of its exact end {end}.");
        }

        private void Ensure(int count, string role)
        {
            if (count < 0 || Position < 0 || Position > end || count > end - Position)
                throw Invalid($"{role} exceeds its bounded NIF region.");
        }
    }

    private static int CheckedCount(uint value, string role)
    {
        if (value > MaximumBlockCount)
            throw Invalid($"{role} exceeds the safety bound.");
        return checked((int)value);
    }
}
