using System.Buffers.Binary;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Byte-preserving PNAM/HCLF writer. Mutagen overlays are read authority; this narrow
/// writer avoids deep-copying unrelated lazy Skyrim fields and changes only the target NPC section.</summary>
internal static class BethesdaNpcFaceBinaryWriter
{
    private const uint CompressedRecordFlag = 0x00040000;

    public static void Write(NpcFacePatchRequest request, string destination)
    {
        var source = File.ReadAllBytes(request.InputPlugin.Value);
        uint targetId = request.TargetFormId.Value;
        if (request.Patch.Replacement is not null)
        {
            using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(request.InputPlugin.Value, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
            targetId |= (uint)mod.ModHeader.MasterReferences.Count << 24;
        }
        var targetCount = 0;
        using var output = new MemoryStream(source.Length + 256);
        RewriteRange(source, 0, source.Length, request, targetId, output, ref targetCount);
        if (targetCount != 1) throw new InvalidDataException($"Expected exactly one NPC record for {request.TargetFormId}, found {targetCount}.");
        File.WriteAllBytes(destination, output.ToArray());
    }

    private static void RewriteRange(byte[] source, int start, int end, NpcFacePatchRequest request, uint targetId,
        Stream destination, ref int targetCount)
    {
        var position = start;
        while (position < end)
        {
            if (position + 8 > end) throw new InvalidDataException("TES4 record header is truncated.");
            var signature = Text(source, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 4, 4));
            var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
            if (recordEnd > end || recordEnd < position + 8) throw new InvalidDataException("TES4 record exceeds its container.");
            if (signature == "GRUP")
            {
                destination.Write(source, position, 24);
                using var children = new MemoryStream((int)size);
                RewriteRange(source, position + 24, recordEnd, request, targetId, children, ref targetCount);
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
                     BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)) == targetId)
            {
                if ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 8, 4)) & CompressedRecordFlag) != 0)
                    throw new InvalidDataException("Compressed NPC records are not supported by the byte-preserving face writer.");
                var patched = PatchNpc(source, position, recordEnd, request);
                destination.Write(patched);
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

    private static byte[] PatchNpc(byte[] source, int start, int end, NpcFacePatchRequest request)
    {
        using var record = new MemoryStream(end - start + 128);
        record.Write(source, start, 24);
        var cursor = start + 24;
        var replacements = 0;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            const string hairField = "HCLF";
            if (request.Patch.Replacement is { } replacement && field == "PNAM")
            {
                if (size != 4) throw new InvalidDataException("PNAM must contain one FormID.");
                using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(request.InputPlugin.Value, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
                var masters = mod.ModHeader.MasterReferences.Select(item => item.Master.ToString()).ToArray();
                int oldIndex = string.Equals(replacement.Old.Plugin.Value, Path.GetFileName(request.InputPlugin.Value), StringComparison.OrdinalIgnoreCase)
                    ? masters.Length : Array.FindIndex(masters, item => string.Equals(item, replacement.Old.Plugin.Value, StringComparison.OrdinalIgnoreCase));
                if (oldIndex < 0) throw new InvalidDataException("Old headpart is absent from the master table.");
                uint oldRaw = ((uint)oldIndex << 24) | replacement.Old.FormId.Value;
                if (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(cursor + 6, 4)) == oldRaw)
                {
                    WriteField(record, "PNAM", ((uint)masters.Length << 24) | replacement.NewLocalFormId.Value);
                    replacements++; cursor = fieldEnd; continue;
                }
            }
            if ((request.Patch.HeadParts is not null && field == "PNAM") ||
                (request.Patch.HairColor.IsSpecified && field == hairField))
            {
                cursor = fieldEnd;
                continue;
            }
            record.Write(source, cursor, fieldEnd - cursor);
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        if (request.Patch.Replacement is not null && replacements != 1) throw new InvalidDataException("Expected exactly one PNAM replacement.");
        if (request.Patch.HeadParts is { } headParts)
            foreach (var selection in headParts) WriteField(record, "PNAM", selection.Reference.FormId.Value);
        if (request.Patch.HairColor is { IsSpecified: true, Value: { } hair })
            WriteField(record, "HCLF", hair.FormId.Value);
        var bytes = record.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 24)));
        return bytes;
    }

    private static void WriteField(Stream destination, string signature, uint value)
    {
        Span<byte> header = stackalloc byte[6];
        header[0] = (byte)signature[0]; header[1] = (byte)signature[1]; header[2] = (byte)signature[2]; header[3] = (byte)signature[3];
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 4);
        destination.Write(header);
        Span<byte> form = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(form, value);
        destination.Write(form);
    }

    private static string Text(byte[] source, int offset, int length) =>
        System.Text.Encoding.ASCII.GetString(source, offset, length);
}
