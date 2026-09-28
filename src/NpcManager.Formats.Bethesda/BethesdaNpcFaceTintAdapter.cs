using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Raw FO4 NPC_.TETI/TEND adapter. It deliberately knows only the bounded tint wire
/// records and refuses compressed NPCs or unsupported discriminator/length combinations.</summary>
public static class BethesdaNpcFaceTintAdapter
{
    public static NpcFaceTintSnapshot Read(GameEdition edition, WorkspacePath pluginPath, FormId formId)
    {
        if (edition != GameEdition.Fallout4)
            throw new NotSupportedException("FO4 face tints are not available for Skyrim SE in this bounded slice.");

        var source = File.ReadAllBytes(pluginPath.Value);
        var found = 0;
        NpcFaceTintSnapshot? result = null;
        ReadRange(source, 0, source.Length, formId, ref found, ref result);
        if (found != 1 || result is null)
            throw new InvalidDataException($"Expected exactly one NPC record for {formId}, found {found}.");
        return result;
    }

    public static void Write(NpcFaceTintPatchRequest request, WorkspacePath destination)
    {
        var source = File.ReadAllBytes(request.InputPlugin.Value);
        var targetCount = 0;
        using var output = new MemoryStream(source.Length + 256);
        RewriteRange(source, 0, source.Length, request.TargetFormId, request.Patch.Layers, output, ref targetCount);
        if (targetCount != 1)
            throw new InvalidDataException($"Expected exactly one NPC record for {request.TargetFormId}, found {targetCount}.");
        File.WriteAllBytes(destination.Value, output.ToArray());
    }

    private static void ReadRange(byte[] source, int start, int end, FormId formId, ref int found,
        ref NpcFaceTintSnapshot? result)
    {
        var position = start;
        while (position < end)
        {
            var (signature, recordEnd) = ReadContainer(source, position, end);
            if (signature == "GRUP")
            {
                ReadRange(source, position + 24, recordEnd, formId, ref found, ref result);
            }
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)) == formId.Value)
            {
                found++;
                result = ParseNpc(source, position, recordEnd);
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static void RewriteRange(byte[] source, int start, int end, FormId formId,
        ImmutableArray<NpcFaceTintLayer> layers, Stream destination, ref int targetCount)
    {
        var position = start;
        while (position < end)
        {
            var (signature, recordEnd) = ReadContainer(source, position, end);
            if (signature == "GRUP")
            {
                destination.Write(source, position, 24);
                using var children = new MemoryStream(recordEnd - (position + 24));
                RewriteRange(source, position + 24, recordEnd, formId, layers, children, ref targetCount);
                var groupSize = checked((uint)(24 + children.Length));
                var sizeBytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, groupSize);
                var headerPosition = destination.Position - 20;
                destination.Position = headerPosition;
                destination.Write(sizeBytes);
                destination.Position = destination.Length;
                children.Position = 0;
                children.CopyTo(destination);
            }
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)) == formId.Value)
            {
                if ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 8, 4)) & 0x00040000) != 0)
                    throw new InvalidDataException("Compressed NPC records are not supported by the byte-preserving face-tint writer.");
                destination.Write(PatchNpc(source, position, recordEnd, layers));
                targetCount++;
            }
            else
            {
                destination.Write(source, position, recordEnd - position);
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static byte[] PatchNpc(byte[] source, int start, int end, ImmutableArray<NpcFaceTintLayer> layers)
    {
        using var record = new MemoryStream(end - start + 256);
        record.Write(source, start, 24);
        var cursor = start + 24;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            if (field is not "TETI" and not "TEND") record.Write(source, cursor, fieldEnd - cursor);
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        foreach (var layer in layers)
        {
            WriteTeti(record, layer);
            WriteField(record, "TEND", EncodeTend(layer));
        }
        var bytes = record.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 24)));
        return bytes;
    }

    private static NpcFaceTintSnapshot ParseNpc(byte[] source, int start, int end)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(start + 8, 4)) & 0x00040000) != 0)
            throw new InvalidDataException("Compressed NPC records are not supported by the byte-preserving face-tint reader.");
        var layers = ImmutableArray.CreateBuilder<NpcFaceTintLayer>();
        NpcFaceTintDataType? pendingType = null;
        ushort pendingIndex = 0;
        var cursor = start + 24;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            var data = source.AsSpan(cursor + 6, size);
            if (field == "TETI")
            {
                if (pendingType is not null) throw new InvalidDataException("NPC contains a TETI without a following TEND.");
                if (data.Length != 4) throw new InvalidDataException("NPC TETI must be exactly 4 bytes.");
                var value = BinaryPrimitives.ReadUInt16LittleEndian(data);
                if (!Enum.IsDefined((NpcFaceTintDataType)value)) throw new InvalidDataException($"Unsupported FO4 TETI discriminator {value}.");
                pendingType = (NpcFaceTintDataType)value;
                pendingIndex = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            }
            else if (field == "TEND")
            {
                if (pendingType is null) throw new InvalidDataException("NPC contains a TEND without a preceding TETI.");
                layers.Add(DecodeLayer(pendingType.Value, pendingIndex, data));
                pendingType = null;
            }
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        if (pendingType is not null) throw new InvalidDataException("NPC contains a TETI without a following TEND.");
        return new NpcFaceTintSnapshot(layers.ToImmutable());
    }

    private static NpcFaceTintLayer DecodeLayer(NpcFaceTintDataType type, ushort index, ReadOnlySpan<byte> data)
    {
        if (data.Length is not (1 or 5 or 7)) throw new InvalidDataException($"FO4 TEND length {data.Length} is unsupported; expected 1, 5, or 7.");
        var color = type == NpcFaceTintDataType.ValueColor && data.Length >= 5
            ? new NpcFaceTintColor(data[1], data[2], data[3]) : null;
        var template = type == NpcFaceTintDataType.ValueColor && data.Length >= 7
            ? (short?)BinaryPrimitives.ReadInt16LittleEndian(data[5..]) : null;
        return new NpcFaceTintLayer(type, index, data[0], color, template, Convert.ToBase64String(data));
    }

    private static byte[] EncodeTend(NpcFaceTintLayer layer)
    {
        byte[] bytes;
        if (layer.RawTendBase64 is not null)
        {
            try { bytes = Convert.FromBase64String(layer.RawTendBase64); }
            catch (FormatException exception) { throw new InvalidDataException("RawTendBase64 is not valid base64.", exception); }
        }
        else if (layer.DataType == NpcFaceTintDataType.ValueColor && layer.Color is not null && layer.TemplateColorIndex is not null)
        {
            bytes = new byte[7];
        }
        else
        {
            throw new InvalidDataException("TextureSet layers require RawTendBase64; new ValueColor layers require Color and TemplateColorIndex.");
        }
        if (bytes.Length is not (1 or 5 or 7)) throw new InvalidDataException($"FO4 TEND length {bytes.Length} is unsupported; expected 1, 5, or 7.");
        if (layer.DataType == NpcFaceTintDataType.TextureSet)
        {
            if (layer.Color is not null || layer.TemplateColorIndex is not null) throw new InvalidDataException("TextureSet layers cannot carry palette color fields.");
            bytes[0] = layer.Value;
            return bytes;
        }
        if (bytes.Length >= 5 && layer.Color is null) throw new InvalidDataException("A ValueColor TEND with color bytes requires Color.");
        if (bytes.Length < 5 && layer.Color is not null) throw new InvalidDataException("Color cannot be supplied for a one-byte ValueColor TEND.");
        if (bytes.Length >= 7 && layer.TemplateColorIndex is null) throw new InvalidDataException("A seven-byte ValueColor TEND requires TemplateColorIndex.");
        if (bytes.Length < 7 && layer.TemplateColorIndex is not null) throw new InvalidDataException("TemplateColorIndex requires a seven-byte ValueColor TEND.");
        bytes[0] = layer.Value;
        if (layer.Color is { } color)
        {
            bytes[1] = color.Red; bytes[2] = color.Green; bytes[3] = color.Blue;
        }
        if (layer.TemplateColorIndex is { } template)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(5), template);
        return bytes;
    }

    private static void WriteTeti(Stream destination, NpcFaceTintLayer layer)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)layer.DataType);
        BinaryPrimitives.WriteUInt16LittleEndian(data[2..], layer.OptionIndex);
        WriteField(destination, "TETI", data.ToArray());
    }

    private static void WriteField(Stream destination, string signature, byte[] data)
    {
        if (data.Length > ushort.MaxValue) throw new InvalidDataException($"Subrecord {signature} is too large.");
        Span<byte> header = stackalloc byte[6];
        Encoding.ASCII.GetBytes(signature, header[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)data.Length);
        destination.Write(header); destination.Write(data);
    }

    private static (string Signature, int End) ReadContainer(byte[] source, int position, int limit)
    {
        if (position + 8 > limit) throw new InvalidDataException("TES4 record header is truncated.");
        var signature = Text(source, position, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 4, 4));
        var end = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (end > limit || end < position + 8 || (signature == "GRUP" && size < 24)) throw new InvalidDataException("TES4 record exceeds its container.");
        return (signature, end);
    }

    private static string Text(byte[] source, int offset, int length) => Encoding.ASCII.GetString(source, offset, length);
}
