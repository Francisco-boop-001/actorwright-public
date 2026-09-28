using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Bounded read-only Skyrim SE NIF material reader. It follows only the
/// shape -> BSLightingShaderProperty -> BSShaderTextureSet and optional
/// NiAlphaProperty edges needed by the preview. Unknown blocks are skipped
/// through the declared block table and are never interpreted.
/// </summary>
internal static class SseNifVisualMaterialReader
{
    private const uint ExpectedVersion = 0x14020007;
    private const uint ExpectedStreamVersion = 100;
    private const int MaximumCount = 100_000;
    private static readonly Encoding NifEncoding =
        Encoding.Latin1;

    public static ImmutableArray<SseNifVisualMaterialDescriptor>
        Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        int lineEnd = Array.IndexOf(
            data,
            (byte)'\n',
            0,
            Math.Min(data.Length, 256));
        if (lineEnd < 0)
            throw Invalid("NIF header line is missing.");
        string header = Encoding.ASCII.GetString(
            data,
            0,
            lineEnd).TrimEnd('\r');
        if (!string.Equals(
                header,
                "Gamebryo File Format, Version 20.2.0.7",
                StringComparison.Ordinal))
            throw Invalid(
                "Only Skyrim SE Gamebryo 20.2.0.7 NIFs are supported.");

        int position = lineEnd + 1;
        if (ReadUInt32(data, ref position) != ExpectedVersion)
            throw Invalid("The NIF version is not Skyrim SE 20.2.0.7.");
        if (ReadByte(data, ref position) != 1)
            throw Invalid("Only little-endian Skyrim SE NIFs are supported.");
        ReadUInt32(data, ref position);
        int blockCount = Count(ReadUInt32(data, ref position));
        if (ReadUInt32(data, ref position) != ExpectedStreamVersion)
            throw Invalid("The NIF Bethesda stream version is not 100.");
        for (int index = 0; index < 3; index++)
            SkipByteString(data, ref position);

        int typeCount = ReadUInt16(data, ref position);
        if (typeCount is < 1 or > 4096)
            throw Invalid("The NIF block type count is outside bounds.");
        var types = new string[typeCount];
        for (int index = 0; index < typeCount; index++)
            types[index] = ReadString(
                data,
                ref position,
                4096);

        var typeIndexes = new ushort[blockCount];
        for (int index = 0; index < blockCount; index++)
        {
            typeIndexes[index] = ReadUInt16(
                data,
                ref position);
            if (typeIndexes[index] >= typeCount)
                throw Invalid(
                    "A NIF block type index exceeds its table.");
        }
        var sizes = new int[blockCount];
        for (int index = 0; index < blockCount; index++)
        {
            uint size = ReadUInt32(data, ref position);
            if (size > int.MaxValue)
                throw Invalid("A NIF block exceeds supported size.");
            sizes[index] = checked((int)size);
        }

        int stringCount = Count(ReadUInt32(data, ref position));
        uint maximumStringLength =
            ReadUInt32(data, ref position);
        var strings = new string[stringCount];
        for (int index = 0; index < stringCount; index++)
        {
            strings[index] = ReadString(
                data,
                ref position,
                1024 * 1024);
            if (maximumStringLength != 0 &&
                strings[index].Length >
                maximumStringLength)
                throw Invalid(
                    "A NIF string exceeds its declared maximum.");
        }
        int groupCount = Count(ReadUInt32(data, ref position));
        Skip(
            data,
            ref position,
            checked(groupCount * 4));

        var offsets = new int[blockCount];
        for (int index = 0; index < blockCount; index++)
        {
            offsets[index] = position;
            Skip(data, ref position, sizes[index]);
        }

        var blocks = new Block[blockCount];
        for (int index = 0; index < blockCount; index++)
            blocks[index] = new(
                types[typeIndexes[index]],
                offsets[index],
                sizes[index]);

        var materials =
            ImmutableArray.CreateBuilder<
                SseNifVisualMaterialDescriptor>();
        for (int index = 0; index < blocks.Length; index++)
        {
            Block shape = blocks[index];
            if (shape.Type is not (
                    "BSTriShape" or
                    "BSDynamicTriShape" or
                    "BSSubIndexTriShape"))
                continue;
            ShapeLinks links = ReadShape(
                data,
                shape,
                strings);
            if (links.Shader < 0 ||
                links.Shader >= blocks.Length ||
                blocks[links.Shader].Type !=
                "BSLightingShaderProperty")
                continue;
            Shader shader = ReadShader(
                data,
                blocks[links.Shader],
                strings);
            ImmutableArray<string> textures = [];
            if (shader.TextureSet >= 0 &&
                shader.TextureSet < blocks.Length &&
                blocks[shader.TextureSet].Type ==
                "BSShaderTextureSet")
                textures = ReadTextureSet(
                    data,
                    blocks[shader.TextureSet]);
            bool hasAlpha =
                links.Alpha >= 0 &&
                links.Alpha < blocks.Length &&
                blocks[links.Alpha].Type ==
                "NiAlphaProperty";
            materials.Add(new(
                links.Name,
                shader.ShaderType,
                shader.Flags1,
                shader.Flags2,
                hasAlpha,
                hasAlpha ? 1.0F : 1.0F,
                shader.TintHex,
                textures)
            {
                ShapeBlockId = index,
                ShapeBlockType = shape.Type,
                ShapeShaderReferenceByteOffset =
                    links.ShaderReferenceByteOffset,
                ShaderBlockId = links.Shader,
                TextureSetBlockId = shader.TextureSet,
                TintByteOffset = shader.TintByteOffset,
                TintFloatBits = shader.TintFloatBits
            });
        }
        ImmutableArray<SseNifVisualMaterialDescriptor> result =
            materials.ToImmutable();
        return result
            .Select(material => material with
            {
                ShaderOwnerShapeBlockIds = result
                    .Where(candidate =>
                        candidate.ShaderBlockId ==
                        material.ShaderBlockId)
                    .Select(candidate =>
                        candidate.ShapeBlockId)
                    .Order()
                    .ToImmutableArray()
            })
            .ToImmutableArray();
    }

    private static ShapeLinks ReadShape(
        byte[] data,
        Block block,
        string[] strings)
    {
        int position = block.Offset;
        int end = checked(block.Offset + block.Size);
        string name = ReadName(
            data,
            ref position,
            end,
            strings) ?? $"shape@{block.Offset}";
        int extras = Count(ReadUInt32(
            data,
            ref position,
            end));
        Skip(data, ref position, end, checked(extras * 4));
        Skip(data, ref position, end, 4);
        Skip(data, ref position, end, 56);
        Skip(data, ref position, end, 4);
        Skip(data, ref position, end, 16);
        Skip(data, ref position, end, 4);
        int shaderReferenceByteOffset = position;
        int shader = ReadInt32(
            data,
            ref position,
            end);
        int alpha = ReadInt32(
            data,
            ref position,
            end);
        return new(
            name,
            shader,
            alpha,
            shaderReferenceByteOffset);
    }

    private static Shader ReadShader(
        byte[] data,
        Block block,
        string[] strings)
    {
        if (block.Size < 16)
            throw Invalid(
                "A BSLightingShaderProperty is too small to keep shader type and HairTint color disjoint.");
        int position = block.Offset;
        int end = checked(block.Offset + block.Size);
        uint shaderType = ReadUInt32(
            data,
            ref position,
            end);
        ReadName(data, ref position, end, strings);
        int extras = Count(ReadUInt32(
            data,
            ref position,
            end));
        Skip(data, ref position, end, checked(extras * 4));
        Skip(data, ref position, end, 4);
        uint flags1 = ReadUInt32(
            data,
            ref position,
            end);
        uint flags2 = ReadUInt32(
            data,
            ref position,
            end);
        Skip(data, ref position, end, 16);
        int textureSet = ReadInt32(
            data,
            ref position,
            end);
        string? tint = null;
        int tintByteOffset = -1;
        ImmutableArray<uint> tintFloatBits = [];
        if (shaderType == 6)
        {
            int tintOffset = end - 12;
            tintByteOffset = tintOffset;
            tintFloatBits =
            [
                ReadUInt32(data, tintOffset),
                ReadUInt32(data, tintOffset + 4),
                ReadUInt32(data, tintOffset + 8)
            ];
            float red = ReadSingle(data, tintOffset);
            float green = ReadSingle(data, tintOffset + 4);
            float blue = ReadSingle(data, tintOffset + 8);
            if (float.IsFinite(red) &&
                float.IsFinite(green) &&
                float.IsFinite(blue) &&
                red is >= 0 and <= 1 &&
                green is >= 0 and <= 1 &&
                blue is >= 0 and <= 1)
                tint = $"#{ToByte(red):X2}{ToByte(green):X2}{ToByte(blue):X2}";
        }
        return new(
            shaderType,
            flags1,
            flags2,
            textureSet,
            tint,
            tintByteOffset,
            tintFloatBits);
    }

    private static ImmutableArray<string> ReadTextureSet(
        byte[] data,
        Block block)
    {
        int position = block.Offset;
        int end = checked(block.Offset + block.Size);
        int count = Count(ReadUInt32(
            data,
            ref position,
            end));
        if (count is < 1 or > 32)
            throw Invalid(
                "A NIF texture-set slot count is outside bounds.");
        var textures =
            ImmutableArray.CreateBuilder<string>(count);
        for (int index = 0; index < count; index++)
            textures.Add(ReadString(
                data,
                ref position,
                end,
                4096));
        return textures.ToImmutable();
    }

    private static string? ReadName(
        byte[] data,
        ref int position,
        int end,
        string[] strings)
    {
        uint index = ReadUInt32(
            data,
            ref position,
            end);
        if (index == uint.MaxValue) return null;
        if (index >= strings.Length)
            throw Invalid(
                "A NIF name index exceeds its string table.");
        return strings[index];
    }

    private static int Count(uint value)
    {
        if (value > MaximumCount)
            throw Invalid("A NIF count exceeds the safety bound.");
        return checked((int)value);
    }

    private static string ReadString(
        byte[] data,
        ref int position,
        int maximum) =>
        ReadString(
            data,
            ref position,
            data.Length,
            maximum);

    private static string ReadString(
        byte[] data,
        ref int position,
        int end,
        int maximum)
    {
        uint length = ReadUInt32(
            data,
            ref position,
            end);
        if (length > maximum)
            throw Invalid(
                "A NIF string exceeds the safety bound.");
        int count = checked((int)length);
        Require(data, position, count, end);
        string value = NifEncoding.GetString(
            data,
            position,
            count);
        position += count;
        return value;
    }

    private static void SkipByteString(
        byte[] data,
        ref int position)
    {
        int length = ReadByte(data, ref position);
        Skip(data, ref position, length);
    }

    private static byte ReadByte(
        byte[] data,
        ref int position)
    {
        Require(data, position, 1, data.Length);
        return data[position++];
    }

    private static ushort ReadUInt16(
        byte[] data,
        ref int position)
    {
        Require(data, position, 2, data.Length);
        ushort value =
            BinaryPrimitives.ReadUInt16LittleEndian(
                data.AsSpan(position, 2));
        position += 2;
        return value;
    }

    private static uint ReadUInt32(
        byte[] data,
        ref int position) =>
        ReadUInt32(
            data,
            ref position,
            data.Length);

    private static uint ReadUInt32(
        byte[] data,
        int offset)
    {
        Require(data, offset, 4, data.Length);
        return BinaryPrimitives.ReadUInt32LittleEndian(
            data.AsSpan(offset, 4));
    }

    private static uint ReadUInt32(
        byte[] data,
        ref int position,
        int end)
    {
        Require(data, position, 4, end);
        uint value =
            BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(position, 4));
        position += 4;
        return value;
    }

    private static int ReadInt32(
        byte[] data,
        ref int position,
        int end)
    {
        Require(data, position, 4, end);
        int value =
            BinaryPrimitives.ReadInt32LittleEndian(
                data.AsSpan(position, 4));
        position += 4;
        return value;
    }

    private static float ReadSingle(
        byte[] data,
        int offset)
    {
        Require(data, offset, 4, data.Length);
        return BinaryPrimitives.ReadSingleLittleEndian(
            data.AsSpan(offset, 4));
    }

    private static void Skip(
        byte[] data,
        ref int position,
        int count) =>
        Skip(data, ref position, data.Length, count);

    private static void Skip(
        byte[] data,
        ref int position,
        int end,
        int count)
    {
        Require(data, position, count, end);
        position += count;
    }

    private static void Require(
        byte[] data,
        int position,
        int count,
        int end)
    {
        if (position < 0 ||
            count < 0 ||
            end > data.Length ||
            position > end - count)
            throw Invalid("The NIF is truncated.");
    }

    private static byte ToByte(float value) =>
        checked((byte)Math.Round(
            value * 255F,
            MidpointRounding.AwayFromZero));

    private static InvalidDataException Invalid(
        string message) =>
        new(message);

    private readonly record struct Block(
        string Type,
        int Offset,
        int Size);

    private readonly record struct ShapeLinks(
        string Name,
        int Shader,
        int Alpha,
        int ShaderReferenceByteOffset);

    private readonly record struct Shader(
        uint ShaderType,
        uint Flags1,
        uint Flags2,
        int TextureSet,
        string? TintHex,
        int TintByteOffset,
        ImmutableArray<uint> TintFloatBits);
}

internal sealed record SseNifVisualMaterialDescriptor(
    string Shape,
    uint ShaderType,
    uint ShaderFlags1,
    uint ShaderFlags2,
    bool HasAlpha,
    float Alpha,
    string? TintHex,
    ImmutableArray<string> Textures)
{
    public int ShapeBlockId { get; init; } = -1;
    public string ShapeBlockType { get; init; } = string.Empty;
    public int ShapeShaderReferenceByteOffset { get; init; } = -1;
    public int ShaderBlockId { get; init; } = -1;
    public ImmutableArray<int> ShaderOwnerShapeBlockIds { get; init; } = [];
    public int TextureSetBlockId { get; init; } = -1;
    public int TintByteOffset { get; init; } = -1;
    public ImmutableArray<uint> TintFloatBits { get; init; } = [];
}
