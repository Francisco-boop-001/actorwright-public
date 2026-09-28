using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NpcManager.TestInfrastructure;

internal static class SkyrimFinishMasterFixture
{
    internal const string InputEnvironmentVariable =
        "ACTORWRIGHT_TEST_SKYRIM_MASTER";

    private const uint TemplateRawFormId = 0x0001B217;
    private const int TemplateRecordLength = 532;
    private const string TemplateGroupPath = "PACK:00000000";
    private const string TemplateGroupBoundDigest =
        "fba3cca0eff98528da3985962ff9058ee7662ea944c250f3ffb90a0490d52685";
    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static long _cachedLength;
    private static long _cachedLastWriteUtcTicks;
    private static byte[]? _cachedRecord;

    internal static byte[] ReadCanonicalPackRecord()
    {
        string path = ResolveMasterPath();
        FileStamp stamp = GetFileStamp(path);
        lock (CacheLock)
        {
            if (_cachedRecord is not null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedLength == stamp.Length &&
                _cachedLastWriteUtcTicks == stamp.LastWriteUtcTicks)
                return (byte[])_cachedRecord.Clone();
        }

        byte[] bytes = ReadMaster(path, stamp.Length);
        byte[] record = ValidateCanonicalPackRecord(bytes);
        FileStamp afterRead = GetFileStamp(path);
        if (afterRead != stamp)
            throw new InvalidDataException(
                "The supplied Skyrim.esm changed while its Finish fixture was validated.");

        lock (CacheLock)
        {
            _cachedPath = path;
            _cachedLength = stamp.Length;
            _cachedLastWriteUtcTicks = stamp.LastWriteUtcTicks;
            _cachedRecord = record;
        }
        return (byte[])record.Clone();
    }

    internal static void EnsureAvailable() => _ = ReadCanonicalPackRecord();

    internal static RawPluginRecordInfo? FindRawRecordInfo(
        byte[] bytes,
        int start,
        int end,
        string groupPath,
        string wantedSignature,
        uint localFormId,
        out int matchCount)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (wantedSignature.Length != 4)
            throw new ArgumentException(
                "A plugin record signature must contain four characters.",
                nameof(wantedSignature));
        if (start < 0 || end < start || end > bytes.Length)
            throw Malformed("record range is outside the input");

        RawPluginRecordInfo? match = null;
        matchCount = 0;
        ScanRange(bytes, start, end, groupPath, wantedSignature, localFormId, 0,
            ref match, ref matchCount);
        return match;
    }

    internal static void AppendCanonicalPackGroupToMaster(
        string syntheticMasterPath,
        string destinationPath,
        string ownedScratchRoot)
    {
        byte[] packRecord = ReadCanonicalPackRecord();
        string source = Path.GetFullPath(syntheticMasterPath);
        string destination = Path.GetFullPath(destinationPath);
        string scratchRoot = Path.GetFullPath(ownedScratchRoot);
        string relativeDestination = Path.GetRelativePath(scratchRoot, destination);
        if (Path.IsPathRooted(relativeDestination) ||
            relativeDestination == ".." ||
            relativeDestination.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The Finish fixture destination must be a separate file inside owned scratch.");

        byte[] master = File.ReadAllBytes(source);
        if (master.Length < 38 ||
            !master.AsSpan(24, 4).SequenceEqual("HEDR"u8))
            throw new InvalidDataException(
                "The synthetic Skyrim.esm fixture has no complete TES4 HEDR record.");

        uint recordCount = BinaryPrimitives.ReadUInt32LittleEndian(
            master.AsSpan(34, 4));
        if (recordCount > uint.MaxValue - 2)
            throw new InvalidDataException(
                "The synthetic Skyrim.esm HEDR record count cannot be incremented.");

        int groupOffset = master.Length;
        byte[] extended = new byte[checked(master.Length + 24 + packRecord.Length)];
        master.CopyTo(extended, 0);
        "GRUP"u8.CopyTo(extended.AsSpan(groupOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            extended.AsSpan(groupOffset + 4, 4),
            checked((uint)(24 + packRecord.Length)));
        "PACK"u8.CopyTo(extended.AsSpan(groupOffset + 8, 4));
        packRecord.CopyTo(extended, groupOffset + 24);
        BinaryPrimitives.WriteUInt32LittleEndian(
            extended.AsSpan(34, 4), recordCount + 2);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, extended);
    }

    private static string ResolveMasterPath()
    {
        string? configured = Environment.GetEnvironmentVariable(
            InputEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"Set {InputEnvironmentVariable} to an absolute local Skyrim.esm containing the approved Finish template record.");
        if (configured.StartsWith("\\\\", StringComparison.Ordinal) ||
            configured.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException(
                $"{InputEnvironmentVariable} must be an absolute drive-letter local path; UNC and device paths are refused.");

        string path;
        try
        {
            path = Path.GetFullPath(configured);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"{InputEnvironmentVariable} is not a usable local Skyrim.esm path.");
        }

        if (path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            path[2] != Path.DirectorySeparatorChar ||
            !string.Equals(
                Path.GetFileName(path), "Skyrim.esm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{InputEnvironmentVariable} must name an absolute local drive-letter Skyrim.esm file.");

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "The supplied Skyrim.esm must be a regular non-reparse file.");
            for (DirectoryInfo? directory =
                 new(Path.GetDirectoryName(path)!);
                 directory is not null;
                 directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        "The supplied Skyrim.esm path has a reparse-point ancestor.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (exception is InvalidDataException)
                throw;
            throw new InvalidOperationException(
                "The supplied Skyrim.esm cannot be inspected as an ordinary local file.");
        }
        return path;
    }

    private static FileStamp GetFileStamp(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1, FileOptions.SequentialScan);
            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            return new FileStamp(stream.Length, writeTime.Ticks);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The supplied Skyrim.esm could not be opened read-only.");
        }
    }

    private static byte[] ReadMaster(string path, long length)
    {
        if (length is <= 0 or > int.MaxValue)
            throw new InvalidDataException(
                "The supplied Skyrim.esm has an unsupported byte length.");
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            if (stream.Length != length)
                throw new InvalidDataException(
                    "The supplied Skyrim.esm changed before it could be read.");
            byte[] bytes = new byte[checked((int)length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The supplied Skyrim.esm could not be read completely.");
        }
    }

    private static byte[] ValidateCanonicalPackRecord(byte[] bytes)
    {
        RawPluginRecordInfo? info = FindRawRecordInfo(
            bytes, 0, bytes.Length, string.Empty, "PACK", 0x1B217,
            out int matchCount);
        if (info is not { } record || matchCount != 1 ||
            record.GroupPath != TemplateGroupPath ||
            record.RawFormId != TemplateRawFormId ||
            record.Length != TemplateRecordLength)
            throw new InvalidDataException(
                "The supplied Skyrim.esm does not contain one exact canonical PACK 0x1B217 record.");
        if ((BinaryPrimitives.ReadUInt32LittleEndian(
                 bytes.AsSpan(record.Offset + 8, 4)) & 0x0004_0000u) != 0)
            throw new InvalidDataException(
                "The supplied Skyrim.esm Finish template PACK record is compressed.");

        byte[] raw = bytes.AsSpan(record.Offset, record.Length).ToArray();
        byte[] groupPath = Encoding.UTF8.GetBytes(record.GroupPath + "\n");
        byte[] digestInput = new byte[groupPath.Length + raw.Length];
        groupPath.CopyTo(digestInput, 0);
        raw.CopyTo(digestInput, groupPath.Length);
        string digest = Convert.ToHexString(
            SHA256.HashData(digestInput)).ToLowerInvariant();
        if (!string.Equals(digest, TemplateGroupBoundDigest, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The supplied Skyrim.esm PACK group path or raw template record digest does not match the approved fixture.");
        return raw;
    }

    private static void ScanRange(
        byte[] bytes,
        int start,
        int end,
        string groupPath,
        string wantedSignature,
        uint localFormId,
        int depth,
        ref RawPluginRecordInfo? match,
        ref int matchCount)
    {
        int position = start;
        while (position < end)
        {
            if (end - position < 24)
                throw Malformed("a record header is truncated");

            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            if (signature == "GRUP")
            {
                if (depth >= 64 || size < 24 || size > (uint)(end - position))
                    throw Malformed("a group has an invalid size or nesting depth");
                int groupEnd = checked(position + (int)size);
                if (groupEnd <= position)
                    throw Malformed("a group does not advance the parser");
                string label = Encoding.ASCII.GetString(
                    bytes, position + 8, 4);
                uint groupType = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 12, 4));
                string childPath = string.IsNullOrEmpty(groupPath)
                    ? $"{label}:{groupType:X8}"
                    : $"{groupPath}/{label}:{groupType:X8}";
                ScanRange(bytes, position + 24, groupEnd, childPath,
                    wantedSignature, localFormId, depth + 1,
                    ref match, ref matchCount);
                position = groupEnd;
                continue;
            }

            if (size > (uint)(end - position - 24))
                throw Malformed("a record data extent exceeds its parent");
            int recordLength = checked(24 + (int)size);
            uint rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 12, 4));
            if (signature == wantedSignature &&
                (rawFormId & 0x00FF_FFFFu) == localFormId)
            {
                matchCount++;
                match ??= new RawPluginRecordInfo(
                    position, recordLength, groupPath, rawFormId);
            }
            int next = checked(position + recordLength);
            if (next <= position)
                throw Malformed("a record does not advance the parser");
            position = next;
        }
    }

    private static InvalidDataException Malformed(string reason) =>
        new($"The supplied Skyrim plugin is malformed: {reason}.");

    private readonly record struct FileStamp(long Length, long LastWriteUtcTicks);
}

internal readonly record struct RawPluginRecordInfo(
    int Offset,
    int Length,
    string GroupPath,
    uint RawFormId);
