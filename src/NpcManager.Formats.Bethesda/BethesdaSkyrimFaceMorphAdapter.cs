using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Byte-preserving Skyrim NAM9/NAMA adapter. It edits only the target NPC's native
/// morph subrecords and keeps the engine-owned trailing NAM9 float typed and explicit.</summary>
public static class BethesdaSkyrimFaceMorphAdapter
{
    private const int Nam9Length = 76;
    private const int NamaLength = 16;
    private const int MaximumInflatedNpcPayloadBytes = 16 * 1024 * 1024;

    public static SkyrimFaceMorphSnapshot Read(GameEdition edition, WorkspacePath pluginPath, FormId formId)
    {
        if (edition != GameEdition.SkyrimSpecialEdition)
            throw new NotSupportedException("NAM9/NAMA face morphs are supported for Skyrim SE only in this bounded slice.");
        var source = File.ReadAllBytes(pluginPath.Value);
        var found = 0;
        SkyrimFaceMorphSnapshot? result = null;
        ReadRange(source, 0, source.Length, formId, ref found, ref result);
        if (found != 1 || result is null)
            throw new InvalidDataException($"Expected exactly one NPC record for {formId}, found {found}.");
        return result;
    }

    public static void Write(SkyrimFaceMorphPatchRequest request, WorkspacePath destination)
    {
        var source = File.ReadAllBytes(request.InputPlugin.Value);
        var targetCount = 0;
        using var output = new MemoryStream(source.Length + 256);
        RewriteRange(source, 0, source.Length, request.TargetFormId, request.Patch, output, ref targetCount);
        if (targetCount != 1)
            throw new InvalidDataException($"Expected exactly one NPC record for {request.TargetFormId}, found {targetCount}.");
        File.WriteAllBytes(destination.Value, output.ToArray());
    }

    private static void ReadRange(byte[] source, int start, int end, FormId formId, ref int found,
        ref SkyrimFaceMorphSnapshot? result)
    {
        var position = start;
        while (position < end)
        {
            var (signature, recordEnd) = ReadContainer(source, position, end);
            if (signature == "GRUP") ReadRange(source, position + 24, recordEnd, formId, ref found, ref result);
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     MatchesPluginLocalFormId(
                         BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)),
                         formId))
            {
                found++;
                result = ParseNpc(source, position, recordEnd);
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static void RewriteRange(byte[] source, int start, int end, FormId formId,
        SkyrimFaceMorphPatch patch, Stream destination, ref int targetCount)
    {
        var position = start;
        while (position < end)
        {
            var (signature, recordEnd) = ReadContainer(source, position, end);
            if (signature == "GRUP")
            {
                destination.Write(source, position, 24);
                using var children = new MemoryStream(recordEnd - (position + 24));
                RewriteRange(source, position + 24, recordEnd, formId, patch, children, ref targetCount);
                var groupSize = checked((uint)(24 + children.Length));
                var sizeBytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, groupSize);
                var headerPosition = destination.Position - 20; destination.Position = headerPosition; destination.Write(sizeBytes);
                destination.Position = destination.Length; children.Position = 0; children.CopyTo(destination);
            }
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     MatchesPluginLocalFormId(
                         BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 12, 4)),
                         formId))
            {
                if ((BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 8, 4)) & 0x00040000) != 0)
                    throw new InvalidDataException("Compressed NPC records are not supported by the Skyrim morph writer.");
                destination.Write(PatchNpc(source, position, recordEnd, patch)); targetCount++;
            }
            else destination.Write(source, position, recordEnd - position);
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static byte[] PatchNpc(byte[] source, int start, int end, SkyrimFaceMorphPatch patch)
    {
        using var record = new MemoryStream(end - start + 192); record.Write(source, start, 24);
        var cursor = start + 24;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            if (field is not "NAM9" and not "NAMA") record.Write(source, cursor, fieldEnd - cursor);
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        WriteField(record, "NAM9", EncodeNam9(patch)); WriteField(record, "NAMA", EncodeNama(patch));
        var bytes = record.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 24)));
        return bytes;
    }

    private static SkyrimFaceMorphSnapshot ParseNpc(byte[] source, int start, int end)
    {
        var payloadStart = start + 24;
        var compressed = (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(start + 8, 4)) & 0x00040000) != 0;
        if (compressed)
        {
            var inflated = InflateCompressedPayload(source, payloadStart, end);
            return ParseNpcPayload(inflated, 0, inflated.Length);
        }
        return ParseNpcPayload(source, payloadStart, end);
    }

    private static SkyrimFaceMorphSnapshot ParseNpcPayload(byte[] source, int start, int end)
    {
        var nam9 = ImmutableArray.CreateBuilder<float>(); var nama = ImmutableArray.CreateBuilder<uint>();
        var hasNam9 = false; var hasNama = false;
        var cursor = start;
        while (cursor + 6 <= end)
        {
            var field = Text(source, cursor, 4); var size = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4, 2));
            var fieldEnd = checked(cursor + 6 + size); if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            var data = source.AsSpan(cursor + 6, size);
            if (field == "NAM9")
            {
                if (hasNam9 || data.Length != Nam9Length) throw new InvalidDataException("Skyrim NAM9 must occur once with exactly 76 bytes.");
                hasNam9 = true; for (var i = 0; i < 19; i++) nam9.Add(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..])));
            }
            else if (field == "NAMA")
            {
                if (hasNama || data.Length != NamaLength) throw new InvalidDataException("Skyrim NAMA must occur once with exactly 16 bytes.");
                hasNama = true; for (var i = 0; i < 4; i++) nama.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]));
            }
            cursor = fieldEnd;
        }
        if (cursor != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        var sliders = hasNam9 ? nam9.Take(18).ToImmutableArray() : Enumerable.Repeat(0F, 18).ToImmutableArray();
        var trailing = hasNam9 ? nam9[18] : 0F;
        var families = hasNama ? nama.ToImmutable() : Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray();
        return new SkyrimFaceMorphSnapshot(sliders, trailing, families, hasNam9, hasNama);
    }

    private static byte[] InflateCompressedPayload(byte[] source, int payloadStart, int end)
    {
        var declaredLength = ReadDeclaredUncompressedLength(source, payloadStart, end);
        if (declaredLength > int.MaxValue)
            throw new InvalidDataException("Compressed NPC payload length exceeds the supported range.");
        if (declaredLength > MaximumInflatedNpcPayloadBytes)
            throw new InvalidDataException("Compressed NPC payload length exceeds the 16 MiB maximum.");
        var compressedStart = checked(payloadStart + 4);
        var compressedLength = checked(end - compressedStart);
        if (compressedLength == 0)
            throw new InvalidDataException("Compressed NPC payload has no zlib data.");

        byte[] inflated;
        try
        {
            using var input = new MemoryStream(source, compressedStart, compressedLength, writable: false);
            inflated = InflateToDeclaredLength(input, checked((int)declaredLength));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Compressed NPC payload could not be inflated.", exception);
        }

        var expectedAdler = Adler32(inflated);
        var streamEnd = -1;
        for (var footerStart = 2; footerStart + 4 <= compressedLength; footerStart++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(
                    source.AsSpan(compressedStart + footerStart, 4)) != expectedAdler)
                continue;
            var candidateLength = footerStart + 4;
            if (InflatesExactly(source, compressedStart, candidateLength, inflated))
            {
                streamEnd = candidateLength;
                break;
            }
        }
        if (streamEnd < 0)
            throw new InvalidDataException("Compressed NPC zlib stream has no valid completion marker.");
        if (streamEnd != compressedLength)
            throw new InvalidDataException("Compressed NPC payload has a trailing zlib tail.");
        return inflated;
    }

    private static byte[] InflateToDeclaredLength(Stream compressed, int declaredLength)
    {
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var inflated = new byte[declaredLength];
        var offset = 0;
        while (offset < inflated.Length)
        {
            var read = zlib.Read(inflated, offset, inflated.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        if (offset != inflated.Length)
            throw new InvalidDataException("Compressed NPC payload is shorter than its declaration.");
        if (zlib.ReadByte() != -1)
            throw new InvalidDataException("Compressed NPC payload is longer than its declaration.");
        return inflated;
    }

    private static uint ReadDeclaredUncompressedLength(byte[] source, int payloadStart, int end)
    {
        if (end - payloadStart < 4)
            throw new InvalidDataException("Compressed NPC payload is missing its uncompressed length.");
        return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(payloadStart, 4));
    }

    private static bool InflatesExactly(
        byte[] source,
        int compressedStart,
        int compressedLength,
        byte[] expected)
    {
        try
        {
            using var input = new MemoryStream(
                source, compressedStart, compressedLength, writable: false);
            return InflateToDeclaredLength(input, expected.Length)
                .AsSpan().SequenceEqual(expected);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return false;
        }
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulo = 65_521;
        uint low = 1;
        uint high = 0;
        foreach (var value in data)
        {
            low = (low + value) % modulo;
            high = (high + low) % modulo;
        }
        return (high << 16) | low;
    }

    private static byte[] EncodeNam9(SkyrimFaceMorphPatch patch)
    {
        var data = new byte[Nam9Length];
        for (var i = 0; i < 18; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), BitConverter.SingleToInt32Bits(patch.Nam9Sliders[i]));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(72), BitConverter.SingleToInt32Bits(patch.Nam9Trailing));
        return data;
    }

    private static byte[] EncodeNama(SkyrimFaceMorphPatch patch)
    {
        var data = new byte[NamaLength]; for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), patch.NamaValues[i]);
        return data;
    }

    private static void WriteField(Stream destination, string signature, byte[] data)
    {
        Span<byte> header = stackalloc byte[6]; Encoding.ASCII.GetBytes(signature, header[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], checked((ushort)data.Length)); destination.Write(header); destination.Write(data);
    }

    private static (string Signature, int End) ReadContainer(byte[] source, int position, int limit)
    {
        if (position + 8 > limit) throw new InvalidDataException("TES4 record header is truncated.");
        var signature = Text(source, position, 4); var size = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(position + 4, 4));
        var end = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (end > limit || end < position + 8 || (signature == "GRUP" && size < 24)) throw new InvalidDataException("TES4 record exceeds its container.");
        return (signature, end);
    }

    private static bool MatchesPluginLocalFormId(uint rawFormId, FormId requested) =>
        (rawFormId & 0x00FF_FFFF) == requested.Value;

    private static string Text(byte[] source, int offset, int length) => Encoding.ASCII.GetString(source, offset, length);
}
