using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Computes a stable digest for every TES4 major record without attempting to
/// interpret unsupported subrecords. The digest supplements typed summaries
/// and includes the containing group path so the read-only surface audit
/// remains sensitive to record-byte and structural placement changes.
/// </summary>
internal static class BethesdaRawRecordDigestReader
{
    public static IReadOnlyDictionary<(uint FormId, string Signature), string> Read(string path, bool exactGroupLabels = false)
    {
        return Read(File.ReadAllBytes(path), exactGroupLabels);
    }

    internal static IReadOnlyDictionary<(uint FormId, string Signature), string> Read(byte[] bytes, bool exactGroupLabels = false)
    {
        var digests = new Dictionary<(uint FormId, string Signature), string>();
        Walk(bytes, 0, bytes.Length, string.Empty, digests, exactGroupLabels);
        return digests;
    }

    private static void Walk(
        byte[] bytes,
        int start,
        int end,
        string groupPath,
        Dictionary<(uint FormId, string Signature), string> digests, bool exactGroupLabels)
    {
        var position = start;
        while (position < end)
        {
            if (position + 8 > end)
            {
                throw new InvalidDataException("TES4 record header is truncated.");
            }

            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            // TES4 GRUP sizes include the group header; ordinary record sizes
            // describe only the payload and therefore need the 24-byte header.
            var recordLength = signature == "GRUP"
                ? checked((int)size)
                : checked(24 + (int)size);
            var recordEnd = checked(position + recordLength);
            if (recordEnd > end || recordEnd < position + 24)
            {
                throw new InvalidDataException("TES4 record exceeds its container.");
            }

            if (signature == "GRUP")
            {
                var groupType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                var label = exactGroupLabels && groupType != 0
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4)).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                    : Encoding.ASCII.GetString(bytes, position + 8, 4);
                var childPath = string.IsNullOrEmpty(groupPath)
                    ? $"{label}:{groupType:X8}"
                    : $"{groupPath}/{label}:{groupType:X8}";
                Walk(bytes, position + 24, recordEnd, childPath, digests, exactGroupLabels);
            }
            else
            {
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                var normalizedSignature = signature.ToUpperInvariant();
                var recordBytes = bytes.AsSpan(position, recordLength);
                var groupPrefix = Encoding.UTF8.GetBytes(groupPath + "\n");
                var digestInput = new byte[groupPrefix.Length + recordBytes.Length];
                groupPrefix.CopyTo(digestInput, 0);
                recordBytes.CopyTo(digestInput.AsSpan(groupPrefix.Length));
                var digest = Convert.ToHexString(SHA256.HashData(digestInput)).ToLowerInvariant();
                digests[(formId, normalizedSignature)] = digest;
            }

            position = recordEnd;
        }

        if (position != end)
        {
            throw new InvalidDataException("TES4 container has trailing bytes.");
        }
    }
}
