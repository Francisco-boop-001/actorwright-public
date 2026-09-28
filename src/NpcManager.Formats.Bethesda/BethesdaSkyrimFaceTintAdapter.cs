using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Raw Skyrim NPC TINI/TINC/TINV/TIAS adapter. It models the authored layer
/// sequence used by the pinned editor and leaves race defaults and RaceMenu-only mask
/// paths outside the plugin writer.</summary>
public static class BethesdaSkyrimFaceTintAdapter
{
    private static readonly string[] TintFields = ["TINI", "TINC", "TINV", "TIAS"];

    public static SkyrimFaceTintSnapshot Read(GameEdition edition, WorkspacePath pluginPath, FormId formId)
    {
        EnsureEdition(edition);
        var source = File.ReadAllBytes(pluginPath.Value);
        var found = 0;
        SkyrimFaceTintSnapshot? result = null;
        ReadRange(source, 0, source.Length, formId, ref found, ref result);
        if (found != 1 || result is null)
            throw new InvalidDataException($"Expected exactly one NPC record for {formId}, found {found}.");
        return result;
    }

    public static void Write(SkyrimFaceTintPatchRequest request, WorkspacePath destination)
    {
        EnsureEdition(request.Edition);
        var source = File.ReadAllBytes(request.InputPlugin.Value);
        var targetCount = 0;
        using var output = new MemoryStream(source.Length + request.Patch.Layers.Length * 32);
        RewriteRange(source, 0, source.Length, request.TargetFormId, request.Patch.Layers, output, ref targetCount);
        if (targetCount != 1)
            throw new InvalidDataException($"Expected exactly one NPC record for {request.TargetFormId}, found {targetCount}.");
        using var file = new FileStream(destination.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(output.GetBuffer(), 0, checked((int)output.Length));
        file.Flush(true);
    }

    private static void EnsureEdition(GameEdition edition)
    {
        if (edition != GameEdition.SkyrimSpecialEdition)
            throw new NotSupportedException("Skyrim face tint layers are available for Skyrim SE only.");
    }

    private static void ReadRange(byte[] source, int start, int end, FormId formId, ref int found,
        ref SkyrimFaceTintSnapshot? result)
    {
        var position = start;
        while (position < end)
        {
            var (signature, recordEnd) = ReadContainer(source, position, end);
            if (signature == "GRUP")
                ReadRange(source, position + 24, recordEnd, formId, ref found, ref result);
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     MatchesPluginLocalFormId(
                         BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)), formId))
            {
                found++;
                result = ParseNpc(source, position, recordEnd);
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static void RewriteRange(byte[] source, int start, int end, FormId formId,
        ImmutableArray<SkyrimFaceTintLayer> layers, Stream destination, ref int targetCount)
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
                var afterHeader = destination.Position;
                destination.Position = afterHeader - 20;
                var groupSize = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(groupSize, checked((uint)(24 + children.Length)));
                destination.Write(groupSize);
                destination.Position = afterHeader;
                children.Position = 0;
                children.CopyTo(destination);
            }
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     MatchesPluginLocalFormId(
                         BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)), formId))
            {
                if ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 8, 4)) & 0x00040000) != 0)
                    throw new InvalidDataException("Compressed NPC records are not supported by the byte-preserving Skyrim face-tint writer.");
                destination.Write(PatchNpc(source, position, recordEnd, layers));
                targetCount++;
            }
            else
                destination.Write(source, position, recordEnd - position);
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static byte[] PatchNpc(byte[] source, int start, int end, ImmutableArray<SkyrimFaceTintLayer> layers)
    {
        using var record = new MemoryStream(end - start + layers.Length * 32);
        record.Write(source, start, 24);
        var cursor = start + 24;
        var inserted = false;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            if (TintFields.Contains(field, StringComparer.Ordinal))
            {
                if (!inserted)
                {
                    WriteLayers(record, layers);
                    inserted = true;
                }
            }
            else
                record.Write(source, cursor, fieldEnd - cursor);
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        if (!inserted) WriteLayers(record, layers);
        var bytes = record.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start: 4, length: 4), checked((uint)(bytes.Length - 24)));
        return bytes;
    }

    private static SkyrimFaceTintSnapshot ParseNpc(byte[] source, int start, int end)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(start + 8, 4)) & 0x00040000) != 0)
            throw new InvalidDataException("Compressed NPC records are not supported by the byte-preserving Skyrim face-tint reader.");
        var layers = ImmutableArray.CreateBuilder<SkyrimFaceTintLayer>();
        PendingLayer? pending = null;
        var cursor = start + 24;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            var data = source.AsSpan(cursor + 6, size);
            switch (field)
            {
                case "TINI":
                    if (pending is not null) throw new InvalidDataException("NPC contains a TINI without a closing TIAS.");
                    if (data.Length != 2) throw new InvalidDataException("Skyrim TINI must be exactly 2 bytes.");
                    pending = new PendingLayer(BinaryPrimitives.ReadUInt16LittleEndian(data));
                    break;
                case "TINC":
                    if (pending is null || data.Length != 4) throw new InvalidDataException("Skyrim TINC must be 4 bytes after TINI.");
                    pending.Color = (data[0], data[1], data[2], data[3]);
                    break;
                case "TINV":
                    if (pending is null || data.Length != 4) throw new InvalidDataException("Skyrim TINV must be 4 bytes after TINI.");
                    pending.Coverage = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    if (pending.Coverage > 100) throw new InvalidDataException("Skyrim TINV coverage must be 0..100.");
                    break;
                case "TIAS":
                    if (pending is null || data.Length != 2 || pending.Color is null || pending.Coverage is null)
                        throw new InvalidDataException("Skyrim TIAS requires a complete TINI/TINC/TINV layer.");
                    layers.Add(new SkyrimFaceTintLayer(pending.Index, pending.Color.Value.Red, pending.Color.Value.Green,
                        pending.Color.Value.Blue, pending.Color.Value.Alpha, pending.Coverage.Value,
                        BinaryPrimitives.ReadInt16LittleEndian(data)));
                    pending = null;
                    break;
            }
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        if (pending is not null) throw new InvalidDataException("NPC contains an unclosed Skyrim tint layer.");
        return new SkyrimFaceTintSnapshot(layers.ToImmutable());
    }

    private static void WriteLayers(Stream destination, ImmutableArray<SkyrimFaceTintLayer> layers)
    {
        foreach (var layer in layers)
        {
            var index = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(index, layer.Index);
            WriteField(destination, "TINI", index.ToArray());
            WriteField(destination, "TINC", [layer.Red, layer.Green, layer.Blue, layer.Alpha]);
            var coverage = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(coverage, layer.Coverage);
            WriteField(destination, "TINV", coverage.ToArray());
            var preset = new byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(preset, layer.PresetIndex);
            WriteField(destination, "TIAS", preset.ToArray());
        }
    }

    private sealed class PendingLayer(ushort index)
    {
        public ushort Index { get; } = index;
        public (byte Red, byte Green, byte Blue, byte Alpha)? Color { get; set; }
        public uint? Coverage { get; set; }
    }

    private static void WriteField(Stream destination, string signature, byte[] data)
    {
        Span<byte> header = stackalloc byte[6];
        Encoding.ASCII.GetBytes(signature, header[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], checked((ushort)data.Length));
        destination.Write(header);
        destination.Write(data);
    }

    private static (string Signature, int End) ReadContainer(byte[] source, int position, int limit)
    {
        if (position + 8 > limit) throw new InvalidDataException("TES4 record header is truncated.");
        var signature = Text(source, position, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 4, 4));
        var end = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (end > limit || end < position + 8 || (signature == "GRUP" && size < 24))
            throw new InvalidDataException("TES4 record exceeds its container.");
        return (signature, end);
    }

    private static string Text(byte[] source, int offset, int length) => Encoding.ASCII.GetString(source, offset, length);

    private static bool MatchesPluginLocalFormId(uint rawFormId, FormId requested) =>
        (rawFormId & 0x00FF_FFFFu) == requested.Value;
}
