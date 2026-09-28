using System.Buffers.Binary;
using System.Text;

namespace NpcManager.Formats.Bethesda;

/// <summary>Small raw fallback for localized/overlay NPC FULL values.</summary>
internal static class BethesdaRawNpcNameReader
{
    private const uint CompressedRecordFlag = 0x00040000;
    private const uint LocalizedPluginFlag = 0x00000080;
    private static readonly Encoding Windows1252 = CreateWindows1252();

    public static IReadOnlyDictionary<uint, string> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var names = new Dictionary<uint, string>();
        bool localized = ReadLocalizedFlag(bytes);
        Walk(bytes, 0, bytes.Length, localized, names);
        return names;
    }

    private static void Walk(
        byte[] bytes,
        int start,
        int end,
        bool localized,
        Dictionary<uint, string> names)
    {
        var position = start;
        while (position < end)
        {
            if (position + 8 > end) throw new InvalidDataException("TES4 record header is truncated.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
            if (recordEnd > end || recordEnd < position + 8) throw new InvalidDataException("TES4 record exceeds its container.");
            if (signature == "GRUP")
                Walk(bytes, position + 24, recordEnd, localized, names);
            else if (signature == "NPC_" && position + 24 <= recordEnd)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4));
                if ((flags & CompressedRecordFlag) == 0)
                {
                    var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                    ReadFull(
                        bytes,
                        position + 24,
                        recordEnd,
                        formId,
                        localized,
                        names);
                }
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static void ReadFull(
        byte[] bytes,
        int start,
        int end,
        uint formId,
        bool localized,
        Dictionary<uint, string> names)
    {
        var cursor = start;
        uint? extendedSize = null;
        while (cursor < end)
        {
            if (end - cursor < 6)
                throw new InvalidDataException("NPC subrecord header is truncated.");
            var field = Encoding.ASCII.GetString(bytes, cursor, 4);
            var fieldSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 4, 2));
            if (field == "XXXX")
            {
                if (fieldSize != 4 || extendedSize is not null || end - cursor < 10)
                    throw new InvalidDataException("NPC XXXX subrecord is malformed.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 6, 4));
                cursor += 10;
                continue;
            }

            var actualSize = extendedSize ?? fieldSize;
            extendedSize = null;
            if (actualSize > int.MaxValue)
                throw new InvalidDataException("NPC subrecord size exceeds the supported range.");
            var fieldEnd = checked(cursor + 6 + (int)actualSize);
            if (fieldEnd > end) throw new InvalidDataException("NPC subrecord exceeds its record.");
            if (field == "FULL" && !localized)
            {
                names[formId] = Windows1252
                    .GetString(bytes, cursor + 6, (int)actualSize)
                    .TrimEnd('\0');
                return;
            }
            cursor = fieldEnd;
        }
        if (extendedSize is not null)
            throw new InvalidDataException("NPC XXXX subrecord has no following payload.");
    }

    private static bool ReadLocalizedFlag(byte[] bytes)
    {
        if (bytes.Length < 24 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "TES4")
            throw new InvalidDataException(
                "Plugin does not begin with a complete TES4 header.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(8, 4)) &
                LocalizedPluginFlag) != 0;
    }

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            1252,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
