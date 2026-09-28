using System.Collections.Immutable;
using System.Text;

namespace NpcManager.Infrastructure;

/// <summary>
/// Fail-closed in-process reader and one-field patcher translated from the
/// workspace's admitted nif_graph_tool.py and nif_texset_patch.py algorithms.
/// Its qualification and rewrite profiles remain Skyrim SE-only. The separate
/// independent geometry-readback service admits the bounded Fallout 4 triangle
/// shape envelope without making it a transport or runtime authority.
/// </summary>
internal static partial class SseFaceGeomCarrierCodec
{
    internal enum ParseProfile
    {
        CompleteCarrier = 0,
        ExternalHeadPartProvider = 1
    }

    private const uint ExpectedVersion = 0x14020007;
    private const uint ExpectedUserVersion = 12;
    private const uint ExpectedStreamVersion = 100;
    private const int MaxCount = 10_000;

    private static readonly Encoding NifEncoding = Encoding.Latin1;
    private static readonly ImmutableHashSet<string> NodeTypes =
        ["NiNode", "BSFadeNode"];
    private static readonly ImmutableHashSet<string> ShapeTypes =
        ["BSTriShape", "BSDynamicTriShape", "BSSubIndexTriShape"];
    private static readonly ImmutableHashSet<string> SkinTypes =
        ["NiSkinInstance", "BSDismemberSkinInstance"];
    private static readonly ImmutableHashSet<string> LeafTypes =
        ["BSShaderTextureSet", "NiSkinData", "NiSkinPartition"];

    internal static SseNifDocument Parse(byte[] data) =>
        Parse(data, ParseProfile.CompleteCarrier);

    internal static SseNifDocument ParseExternalHeadPartProvider(byte[] data) =>
        Parse(data, ParseProfile.ExternalHeadPartProvider);

    private static SseNifDocument Parse(byte[] data, ParseProfile profile)
    {
        var lineEnd = Array.IndexOf(data, (byte)'\n', 0, Math.Min(data.Length, 256));
        if (lineEnd < 0) throw Invalid("NIF header line is missing or exceeds 255 bytes.");
        var header = Encoding.ASCII.GetString(data, 0, lineEnd).TrimEnd('\r');
        if (!string.Equals(header, "Gamebryo File Format, Version 20.2.0.7", StringComparison.Ordinal))
            throw Invalid("Only the Skyrim SE Gamebryo 20.2.0.7 carrier envelope is supported.");

        var position = lineEnd + 1;
        var version = ReadUInt32(data, ref position, data.Length, "version");
        if (version != ExpectedVersion)
            throw Invalid($"NIF version 0x{version:X8} is not the admitted 0x{ExpectedVersion:X8} version.");
        var endian = ReadByte(data, ref position, data.Length, "endian");
        if (endian != 1) throw Invalid("Only little-endian Skyrim SE NIFs are supported.");
        var userVersion = ReadUInt32(data, ref position, data.Length, "user version");
        var blockCount = CheckedCount(ReadUInt32(data, ref position, data.Length, "block count"),
            "block count");
        var streamVersion = ReadUInt32(data, ref position, data.Length, "Bethesda stream version");
        if (streamVersion != ExpectedStreamVersion)
            throw Invalid($"Bethesda stream {streamVersion} is not the admitted Skyrim SE stream 100.");
        for (var index = 0; index < 3; index++)
            SkipByteSizedString(data, ref position, data.Length, $"export string {index}");

        var typeCount = ReadUInt16(data, ref position, data.Length, "block type count");
        if (typeCount is 0 or > 1024)
            throw Invalid($"Block type count {typeCount} is outside the safety bound.");
        var typeNames = ImmutableArray.CreateBuilder<string>(typeCount);
        for (var index = 0; index < typeCount; index++)
            typeNames.Add(ReadSizedString(data, ref position, data.Length, 256, $"block type {index}"));

        var typeIndexes = new ushort[blockCount];
        for (var index = 0; index < blockCount; index++)
        {
            typeIndexes[index] = ReadUInt16(data, ref position, data.Length, $"block type index {index}");
            if (typeIndexes[index] >= typeCount)
                throw Invalid($"Block {index} type index {typeIndexes[index]} exceeds the type table.");
        }

        var blockSizeTableOffset = position;
        var blockSizes = new int[blockCount];
        for (var index = 0; index < blockCount; index++)
        {
            var size = ReadUInt32(data, ref position, data.Length, $"block size {index}");
            if (size > int.MaxValue) throw Invalid($"Block {index} exceeds the supported size.");
            blockSizes[index] = checked((int)size);
        }

        var stringCount = CheckedCount(ReadUInt32(data, ref position, data.Length, "string count"),
            "string count");
        var maxStringLength = ReadUInt32(data, ref position, data.Length, "maximum string length");
        var strings = ImmutableArray.CreateBuilder<string>(stringCount);
        for (var index = 0; index < stringCount; index++)
        {
            var value = ReadSizedString(data, ref position, data.Length, 1024 * 1024,
                $"string table entry {index}");
            if (maxStringLength != 0 && value.Length > maxStringLength)
                throw Invalid($"String table entry {index} exceeds the declared maximum length.");
            strings.Add(value);
        }

        var groupCount = CheckedCount(ReadUInt32(data, ref position, data.Length, "group count"),
            "group count");
        Skip(data, ref position, data.Length, checked(groupCount * sizeof(uint)), "group table");
        var blockDataOffset = position;
        var blockOffsets = new int[blockCount];
        for (var index = 0; index < blockCount; index++)
        {
            blockOffsets[index] = position;
            Skip(data, ref position, data.Length, blockSizes[index], $"block {index}");
        }

        var footerOffset = position;
        var rootCount = CheckedCount(ReadUInt32(data, ref position, data.Length, "root count"),
            "root count");
        if (rootCount > blockCount) throw Invalid("The NIF root count exceeds the block count.");
        var roots = ImmutableArray.CreateBuilder<int>(rootCount);
        for (var index = 0; index < rootCount; index++)
        {
            var root = ReadInt32(data, ref position, data.Length, $"root {index}");
            if (root < 0 || root >= blockCount)
                throw Invalid($"Root reference {root} is outside the block table.");
            roots.Add(root);
        }
        if (position != data.Length) throw Invalid("The NIF footer does not consume the file exactly.");

        var blocks = ImmutableArray.CreateBuilder<SseNifBlock>(blockCount);
        for (var index = 0; index < blockCount; index++)
        {
            var type = typeNames[typeIndexes[index]];
            blocks.Add(ParseBlock(
                data,
                index,
                type,
                blockOffsets[index],
                blockSizes[index],
                blockCount,
                strings,
                profile));
        }

        ValidateReferenceTypes(blocks);
        return new SseNifDocument(data, userVersion, streamVersion, blockSizeTableOffset,
            blockDataOffset, footerOffset, typeNames.ToImmutable(), strings.ToImmutable(),
            roots.ToImmutable(), blocks.ToImmutable());
    }
}
