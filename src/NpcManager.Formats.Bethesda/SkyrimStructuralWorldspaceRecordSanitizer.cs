using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

internal sealed record SkyrimStructuralWorldspaceSanitization(
    int OriginalByteLength,
    int SanitizedByteLength,
    int RemovedOffset,
    int RemovedByteLength,
    uint OriginalTes4RecordCount,
    uint SanitizedTes4RecordCount,
    string RemovedRecordSha256);

internal sealed record SkyrimStructuralCellMinimization(
    int OriginalByteLength,
    int MinimizedByteLength,
    int CellRecordOffset,
    int RemovedByteLength,
    uint Tes4RecordCount,
    string OriginalCellBodySha256,
    string MinimizedCellBodySha256);

/// <summary>
/// Removes the empty master WRLD record that Mutagen emits when a plugin owns
/// only a worldspace child-group branch. The group branch containing CELL and
/// placed-reference records is retained byte-for-byte.
/// </summary>
internal static class SkyrimStructuralWorldspaceRecordSanitizer
{
    private const int HeaderLength = 24;
    private const int ExpectedBodyLength = 57;
    private const int ExpectedRecordLength =
        HeaderLength + ExpectedBodyLength;

    public static SkyrimStructuralWorldspaceSanitization
        RemoveRequiredPartialMasterRecord(
            string pluginPath,
            string masterName,
            uint localWorldspaceFormId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterName);
        byte[] source = File.ReadAllBytes(pluginPath);
        ParsedPlugin parsed = Parse(
            source,
            masterName,
            localWorldspaceFormId);
        if (parsed.WorldspaceRecords.Count != 1 ||
            parsed.WorldspaceRecords[0] !=
                parsed.PartialRecordOffset)
            throw new InvalidDataException(
                "follower-finish-world-partial-master-record: " +
                "The staged plugin must contain exactly one expected " +
                "partial master WRLD record before sanitization.");

        ValidateExactPartialRecord(
            source,
            parsed.PartialRecordOffset,
            parsed.ExpectedRawWorldspaceFormId);
        if (parsed.Tes4RecordCount == 0)
            throw new InvalidDataException(
                "follower-finish-world-record-count: " +
                "The TES4 HEDR record count cannot be decremented.");

        byte[] sanitized =
            new byte[source.Length - ExpectedRecordLength];
        source.AsSpan(0, parsed.PartialRecordOffset)
            .CopyTo(sanitized);
        source.AsSpan(
                parsed.PartialRecordOffset +
                ExpectedRecordLength)
            .CopyTo(
                sanitized.AsSpan(
                    parsed.PartialRecordOffset));

        uint oldGroupSize =
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(
                    parsed.TopWorldGroupOffset + 4,
                    4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            sanitized.AsSpan(
                parsed.TopWorldGroupOffset + 4,
                4),
            checked(oldGroupSize -
                    ExpectedRecordLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            sanitized.AsSpan(
                parsed.HedrRecordCountOffset,
                4),
            parsed.Tes4RecordCount - 1);

        ParsedPlugin sanitizedParsed = Parse(
            sanitized,
            masterName,
            localWorldspaceFormId);
        if (sanitizedParsed.WorldspaceRecords.Count != 0 ||
            sanitizedParsed.TopWorldGroupOffset !=
                parsed.TopWorldGroupOffset ||
            sanitizedParsed.WorldChildGroupCount != 1 ||
            sanitizedParsed.Tes4RecordCount !=
                parsed.Tes4RecordCount - 1)
            throw new InvalidDataException(
                "follower-finish-world-sanitization: " +
                "The sanitized plugin does not retain exactly one " +
                "world-child branch with zero WRLD records.");

        string temporary =
            pluginPath +
            $".world-clean-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(sanitized);
                stream.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(temporary)
                    .AsSpan()
                    .SequenceEqual(sanitized))
                throw new IOException(
                    "follower-finish-world-sanitization-write: " +
                    "The independently reopened temporary file differs.");
            File.Move(
                temporary,
                pluginPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return new SkyrimStructuralWorldspaceSanitization(
            source.Length,
            sanitized.Length,
            parsed.PartialRecordOffset,
            ExpectedRecordLength,
            parsed.Tes4RecordCount,
            parsed.Tes4RecordCount - 1,
            Convert.ToHexString(
                SHA256.HashData(
                    source.AsSpan(
                        parsed.PartialRecordOffset,
                        ExpectedRecordLength))));
    }

    public static SkyrimStructuralCellMinimization
        MinimizeRequiredExteriorCellRecord(
            string pluginPath,
            string masterName,
            uint localWorldspaceFormId,
            uint localCellFormId,
            int cellGridX,
            int cellGridY)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterName);
        if (localWorldspaceFormId > 0x00FFFFFF)
            throw new ArgumentOutOfRangeException(
                nameof(localWorldspaceFormId),
                localWorldspaceFormId,
                "A master-local worldspace FormID must fit in 24 bits.");
        if (localCellFormId > 0x00FFFFFF)
            throw new ArgumentOutOfRangeException(
                nameof(localCellFormId),
                localCellFormId,
                "A master-local cell FormID must fit in 24 bits.");
        byte[] source = File.ReadAllBytes(pluginPath);
        ParsedPlugin parsed = Parse(
            source,
            masterName,
            localWorldspaceFormId);
        List<RawRecordNode> sourceRecords = CollectRecords(
            source,
            parsed.FirstGroupOffset);
        uint rawCellFormId = checked(
            ((uint)parsed.RequestedMasterIndex << 24) |
            localCellFormId);
        ImmutableArray<GroupNode> expectedGroups =
            ExpectedCellGroups(
                parsed.ExpectedRawWorldspaceFormId,
                cellGridX,
                cellGridY);
        RawRecordNode[] cells = sourceRecords
            .Where(record =>
                record.Signature == "CELL" &&
                record.FormId == rawCellFormId &&
                record.Groups.SequenceEqual(expectedGroups))
            .ToArray();
        if (cells.Length != 1 ||
            sourceRecords.Count(record =>
                record.Signature == "CELL") != 1)
            throw new InvalidDataException(
                "follower-finish-world-cell-minimization-path: " +
                "The requested structural CELL is missing, duplicated, " +
                "or outside the exact WRLD/world-child/block/subblock path.");

        byte[] expectedOriginalBody = BuildSubrecords(
        [
            ("DATA", new byte[2]),
            ("XCLC", CellGridPayload(
                cellGridX,
                cellGridY)),
            ("LTMP", new byte[4])
        ]);
        byte[] minimizedBody = BuildSubrecords(
        [
            ("XCLC", CellGridPayload(
                cellGridX,
                cellGridY))
        ]);
        RawRecordNode cell = cells[0];
        int removedLength = checked(
            expectedOriginalBody.Length -
            minimizedBody.Length);
        if (removedLength != 18 ||
            cell.Flags != 0 ||
            cell.FormVersion != 44 ||
            !cell.Body.SequenceEqual(expectedOriginalBody) ||
            cell.AncestorGroupOffsets.Length != 4)
            throw new InvalidDataException(
                "follower-finish-world-cell-minimization-preimage: " +
                "The structural CELL is not the exact flags-0, " +
                "FormVersion-44 DATA/XCLC/LTMP preimage.");

        byte[] minimized = new byte[
            source.Length - removedLength];
        source.AsSpan(0, cell.Offset + HeaderLength)
            .CopyTo(minimized);
        minimizedBody.CopyTo(
            minimized,
            cell.Offset + HeaderLength);
        source.AsSpan(
                cell.Offset + HeaderLength +
                cell.Body.Length)
            .CopyTo(
                minimized.AsSpan(
                    cell.Offset + HeaderLength +
                    minimizedBody.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            minimized.AsSpan(cell.Offset + 4, 4),
            checked((uint)minimizedBody.Length));
        foreach (int groupOffset in
                 cell.AncestorGroupOffsets)
        {
            uint oldSize =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    source.AsSpan(groupOffset + 4, 4));
            if (oldSize < removedLength)
                throw new InvalidDataException(
                    "follower-finish-world-cell-minimization-size: " +
                    "An ancestor group is smaller than the admitted delta.");
            BinaryPrimitives.WriteUInt32LittleEndian(
                minimized.AsSpan(groupOffset + 4, 4),
                checked(oldSize - (uint)removedLength));
        }

        string temporary =
            pluginPath +
            $".cell-minimal-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(minimized);
                stream.Flush(flushToDisk: true);
            }
            byte[] reopened = File.ReadAllBytes(temporary);
            ParsedPlugin reopenedParsed = Parse(
                reopened,
                masterName,
                localWorldspaceFormId);
            List<RawRecordNode> reopenedRecords =
                CollectRecords(
                    reopened,
                    reopenedParsed.FirstGroupOffset);
            RawRecordNode[] reopenedCells = reopenedRecords
                .Where(record =>
                    record.Signature == "CELL" &&
                    record.FormId == rawCellFormId &&
                    record.Groups.SequenceEqual(
                        expectedGroups))
                .ToArray();
            if (reopenedCells.Length != 1 ||
                reopenedRecords.Count(record =>
                    record.Signature == "CELL") != 1 ||
                reopenedCells[0].Flags != 0 ||
                reopenedCells[0].FormVersion != 44 ||
                !reopenedCells[0].Body.SequenceEqual(
                    minimizedBody) ||
                reopenedParsed.Tes4RecordCount !=
                    parsed.Tes4RecordCount ||
                !RecordInventory(sourceRecords)
                    .SequenceEqual(
                        RecordInventory(reopenedRecords)) ||
                !PlacedReferenceBodies(sourceRecords)
                    .SequenceEqual(
                        PlacedReferenceBodies(reopenedRecords)))
                throw new InvalidDataException(
                    "follower-finish-world-cell-minimization-verify: " +
                    "The reopened temporary plugin does not preserve its " +
                    "record inventory, TES4 count, or placed-reference bodies.");
            File.Move(
                temporary,
                pluginPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return new SkyrimStructuralCellMinimization(
            source.Length,
            minimized.Length,
            cell.Offset,
            removedLength,
            parsed.Tes4RecordCount,
            Convert.ToHexString(
                SHA256.HashData(cell.Body)),
            Convert.ToHexString(
                SHA256.HashData(minimizedBody)));
    }

    public static void RequireNoMasterRecord(
        string pluginPath,
        string masterName,
        uint localWorldspaceFormId)
    {
        byte[] bytes = File.ReadAllBytes(pluginPath);
        ParsedPlugin parsed = Parse(
            bytes,
            masterName,
            localWorldspaceFormId);
        if (parsed.WorldspaceRecords.Count != 0)
            throw new InvalidDataException(
                "follower-finish-world-partial-master-record: " +
                "An actual master WRLD record is forbidden; only its " +
                "world-child placement group may be present.");
        if (parsed.WorldChildGroupCount != 1)
            throw new InvalidDataException(
                "follower-finish-world-group-tree: " +
                "The exact world-child placement group is missing or " +
                "duplicated.");
    }

    public static void RequireNoForbiddenWorldRecords(
        string pluginPath,
        string masterName,
        uint localWorldspaceFormId)
    {
        byte[] bytes = File.ReadAllBytes(pluginPath);
        ParsedPlugin parsed = Parse(
            bytes,
            masterName,
            localWorldspaceFormId);
        string[] forbidden =
        [
            "WRLD", "LAND", "NAVM", "NAVI", "WATR",
            "LTEX", "LCTN", "REGN", "CLMT", "MUSC",
            "IMGS"
        ];
        string[] present = CollectRecords(
                bytes,
                parsed.FirstGroupOffset)
            .Select(record => record.Signature)
            .Where(forbidden.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (present.Length != 0)
            throw new InvalidDataException(
                "follower-finish-pair-output-world-surface: " +
                "Forbidden master-world, terrain, water, or " +
                "navigation records are present: " +
                string.Join(", ", present));
    }

    public static void RequireSelfOwnedInventory(
        string pluginPath,
        string masterName,
        uint localWorldspaceFormId,
        IEnumerable<string> expectedInventory)
    {
        byte[] bytes = File.ReadAllBytes(pluginPath);
        ParsedPlugin parsed = Parse(
            bytes,
            masterName,
            localWorldspaceFormId);
        string[] actual = CollectRecords(
                bytes,
                parsed.FirstGroupOffset)
            .Where(record =>
                (record.FormId >> 24) ==
                (uint)parsed.MasterCount)
            .Select(record =>
                $"{record.Signature} " +
                $"0x{(record.FormId & 0x00FF_FFFFu):X8}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = expectedInventory
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException(
                "follower-finish-pair-output-inventory: " +
                "The self-owned raw record inventory is not closed. " +
                $"expected=[{string.Join(", ", expected)}]; " +
                $"actual=[{string.Join(", ", actual)}]");
    }

    public static void RequireSinglePlacedNpc(
        string pluginPath,
        string masterName,
        uint localWorldspaceFormId,
        uint localCellFormId,
        int cellGridX,
        int cellGridY,
        uint localPlacedNpcFormId,
        uint localNpcFormId,
        SkyrimFollowerFinishPairTransform expectedTransform)
    {
        byte[] bytes = File.ReadAllBytes(pluginPath);
        ParsedPlugin parsed = Parse(
            bytes,
            masterName,
            localWorldspaceFormId);
        if (parsed.WorldspaceRecords.Count != 0)
            throw new InvalidDataException(
                "follower-finish-world-partial-master-record: " +
                "An actual master WRLD record is forbidden.");
        List<RawRecordNode> records = CollectRecords(
            bytes,
            parsed.FirstGroupOffset);
        uint rawCell = checked(
            ((uint)parsed.RequestedMasterIndex << 24) |
            localCellFormId);
        uint rawPlacedNpc = checked(
            ((uint)parsed.MasterCount << 24) |
            localPlacedNpcFormId);
        uint rawNpc = checked(
            ((uint)parsed.MasterCount << 24) |
            localNpcFormId);

        RawRecordNode[] cells = records.Where(record =>
                record.Signature == "CELL")
            .ToArray();
        RawRecordNode[] placed = records.Where(record =>
                record.Signature == "ACHR")
            .ToArray();
        if (cells.Length != 1 ||
            cells[0].FormId != rawCell ||
            placed.Length != 1 ||
            placed[0].FormId != rawPlacedNpc ||
            records.Any(record =>
                record.Signature == "REFR"))
            throw new InvalidDataException(
                "follower-finish-pair-output-placement: " +
                "The structural cell does not contain exactly the " +
                "reviewed adjacent ACHR.");

        ImmutableArray<GroupNode> cellGroups =
            ExpectedCellGroups(
                parsed.ExpectedRawWorldspaceFormId,
                cellGridX,
                cellGridY);
        ImmutableArray<GroupNode> placedGroups =
            cellGroups
                .Add(new GroupNode(6, rawCell))
                .Add(new GroupNode(8, rawCell));
        if (!cells[0].Groups.SequenceEqual(cellGroups) ||
            !placed[0].Groups.SequenceEqual(placedGroups))
            throw new InvalidDataException(
                "follower-finish-pair-output-placement: " +
                "The CELL/ACHR group path differs from the reviewed " +
                "world-child structure.");

        byte[] expectedCellBody = BuildSubrecords(
        [
            ("XCLC", CellGridPayload(
                cellGridX,
                cellGridY))
        ]);
        byte[] expectedPlacedBody = BuildSubrecords(
        [
            ("NAME", UInt32Payload(rawNpc)),
            ("DATA", TransformPayload(expectedTransform))
        ]);
        if (cells[0].Flags != 0 ||
            cells[0].FormVersion != 44 ||
            !cells[0].Body.SequenceEqual(expectedCellBody) ||
            placed[0].Flags != 0x400 ||
            placed[0].FormVersion != 44 ||
            !placed[0].Body.SequenceEqual(expectedPlacedBody))
            throw new InvalidDataException(
                "follower-finish-pair-output-placement: " +
                "The CELL or ACHR header/payload differs from the " +
                "reviewed identity and transform.");
    }

    private static ParsedPlugin Parse(
        byte[] bytes,
        string masterName,
        uint localWorldspaceFormId)
    {
        if (bytes.Length < HeaderLength ||
            Signature(bytes, 0) != "TES4")
            throw new InvalidDataException(
                "The plugin does not begin with a TES4 record.");
        int tes4BodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(4, 4)));
        int firstGroup = checked(
            HeaderLength + tes4BodyLength);
        if (firstGroup > bytes.Length)
            throw new InvalidDataException(
                "The TES4 record exceeds the plugin.");

        (int hedrCountOffset, uint recordCount,
            int masterIndex, int masterCount) = ParseTes4(
            bytes.AsSpan(HeaderLength, tes4BodyLength),
            masterName);
        uint rawWorldspaceFormId = checked(
            ((uint)masterIndex << 24) |
            localWorldspaceFormId);

        int topWorldGroup = -1;
        int partialRecord = -1;
        int worldChildGroupCount = 0;
        var worldspaceRecords = new List<int>();
        int position = firstGroup;
        while (position < bytes.Length)
        {
            RequireHeader(bytes, position, bytes.Length);
            string signature = Signature(bytes, position);
            uint size =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
            if (signature != "GRUP")
                throw new InvalidDataException(
                    "A non-GRUP item appears at plugin top level.");
            int end = checked(position + (int)size);
            if (size < HeaderLength || end > bytes.Length)
                throw new InvalidDataException(
                    "A top-level group exceeds the plugin.");
            uint type =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 12, 4));
            string label = Signature(bytes, position + 8);
            if (type == 0 && label == "WRLD")
            {
                if (topWorldGroup >= 0)
                    throw new InvalidDataException(
                        "The plugin contains duplicate top-level WRLD groups.");
                topWorldGroup = position;
                InspectWorldGroup(
                    bytes,
                    position,
                    end,
                    rawWorldspaceFormId,
                    worldspaceRecords,
                    ref partialRecord,
                    ref worldChildGroupCount);
            }
            else
            {
                WalkAllRecords(
                    bytes,
                    position + HeaderLength,
                    end,
                    worldspaceRecords);
            }
            position = end;
        }
        if (position != bytes.Length ||
            topWorldGroup < 0)
            throw new InvalidDataException(
                "The plugin lacks one bounded top-level WRLD group.");

        return new ParsedPlugin(
            topWorldGroup,
            partialRecord,
            firstGroup,
            checked(HeaderLength + hedrCountOffset),
            recordCount,
            rawWorldspaceFormId,
            masterIndex,
            masterCount,
            worldChildGroupCount,
            worldspaceRecords);
    }

    private static void InspectWorldGroup(
        byte[] bytes,
        int groupStart,
        int groupEnd,
        uint rawWorldspaceFormId,
        List<int> worldspaceRecords,
        ref int partialRecord,
        ref int worldChildGroupCount)
    {
        int position = groupStart + HeaderLength;
        while (position < groupEnd)
        {
            RequireHeader(bytes, position, groupEnd);
            string signature = Signature(bytes, position);
            uint size =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
            if (signature == "GRUP")
            {
                int end = checked(position + (int)size);
                if (size < HeaderLength || end > groupEnd)
                    throw new InvalidDataException(
                        "A WRLD child group exceeds its parent.");
                uint groupType =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4));
                uint label =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 8, 4));
                if (groupType == 1 &&
                    label == rawWorldspaceFormId)
                    worldChildGroupCount++;
                WalkAllRecords(
                    bytes,
                    position + HeaderLength,
                    end,
                    worldspaceRecords);
                position = end;
                continue;
            }

            int recordEnd = checked(
                position + HeaderLength + (int)size);
            if (recordEnd > groupEnd)
                throw new InvalidDataException(
                    "A WRLD record exceeds its parent group.");
            if (signature == "WRLD")
            {
                worldspaceRecords.Add(position);
                uint formId =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4));
                if (formId == rawWorldspaceFormId)
                {
                    if (partialRecord >= 0)
                        throw new InvalidDataException(
                            "The expected partial WRLD record is duplicated.");
                    partialRecord = position;
                }
            }
            position = recordEnd;
        }
        if (position != groupEnd)
            throw new InvalidDataException(
                "The top-level WRLD group has an invalid boundary.");
    }

    private static void WalkAllRecords(
        byte[] bytes,
        int start,
        int end,
        List<int> worldspaceRecords)
    {
        int position = start;
        while (position < end)
        {
            RequireHeader(bytes, position, end);
            string signature = Signature(bytes, position);
            uint size =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
            if (signature == "GRUP")
            {
                int groupEnd = checked(position + (int)size);
                if (size < HeaderLength || groupEnd > end)
                    throw new InvalidDataException(
                        "A nested group exceeds its parent.");
                WalkAllRecords(
                    bytes,
                    position + HeaderLength,
                    groupEnd,
                    worldspaceRecords);
                position = groupEnd;
                continue;
            }
            int recordEnd = checked(
                position + HeaderLength + (int)size);
            if (recordEnd > end)
                throw new InvalidDataException(
                    "A record exceeds its parent.");
            if (signature == "WRLD")
                worldspaceRecords.Add(position);
            position = recordEnd;
        }
        if (position != end)
            throw new InvalidDataException(
                "A group has an invalid boundary.");
    }

    private static (
        int HedrRecordCountOffset,
        uint RecordCount,
        int MasterIndex,
        int MasterCount) ParseTes4(
            ReadOnlySpan<byte> body,
            string masterName)
    {
        int position = 0;
        int hedrCountOffset = -1;
        uint recordCount = 0;
        int masterIndex = -1;
        int currentMasterIndex = 0;
        while (position < body.Length)
        {
            if (position + 6 > body.Length)
                throw new InvalidDataException(
                    "A TES4 subrecord header is truncated.");
            string signature =
                Encoding.ASCII.GetString(
                    body.Slice(position, 4));
            int length =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.Slice(position + 4, 2));
            if (position + 6 + length > body.Length)
                throw new InvalidDataException(
                    "A TES4 subrecord exceeds its record.");
            ReadOnlySpan<byte> payload =
                body.Slice(position + 6, length);
            if (signature == "HEDR")
            {
                if (length != 12 || hedrCountOffset >= 0)
                    throw new InvalidDataException(
                        "The TES4 HEDR envelope is invalid.");
                hedrCountOffset = position + 10;
                recordCount =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        payload.Slice(4, 4));
            }
            else if (signature == "MAST")
            {
                string name = Encoding.UTF8
                    .GetString(payload)
                    .TrimEnd('\0');
                if (string.Equals(
                        name,
                        masterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (masterIndex >= 0)
                        throw new InvalidDataException(
                            "The requested worldspace master is duplicated.");
                    masterIndex = currentMasterIndex;
                }
                currentMasterIndex++;
            }
            position += 6 + length;
        }
        if (position != body.Length ||
            hedrCountOffset < 0 ||
            masterIndex < 0)
            throw new InvalidDataException(
                "The TES4 record lacks its HEDR or requested master.");
        return (
            hedrCountOffset,
            recordCount,
            masterIndex,
            currentMasterIndex);
    }

    private static List<RawRecordNode> CollectRecords(
        byte[] bytes,
        int firstGroup)
    {
        var records = new List<RawRecordNode>();
        Walk(
            firstGroup,
            bytes.Length,
            [],
            []);
        return records;

        void Walk(
            int start,
            int end,
            ImmutableArray<GroupNode> groups,
            ImmutableArray<int> groupOffsets)
        {
            int position = start;
            while (position < end)
            {
                RequireHeader(bytes, position, end);
                string signature = Signature(bytes, position);
                uint size =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 4, 4));
                if (signature == "GRUP")
                {
                    int groupEnd =
                        checked(position + (int)size);
                    if (size < HeaderLength ||
                        groupEnd > end)
                        throw new InvalidDataException(
                            "A group exceeds its parent.");
                    uint label =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(position + 8, 4));
                    uint type =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(position + 12, 4));
                    Walk(
                        position + HeaderLength,
                        groupEnd,
                        groups.Add(new GroupNode(type, label)),
                        groupOffsets.Add(position));
                    position = groupEnd;
                    continue;
                }
                int bodyLength = checked((int)size);
                int recordEnd = checked(
                    position + HeaderLength + bodyLength);
                if (recordEnd > end)
                    throw new InvalidDataException(
                        "A record exceeds its parent.");
                records.Add(new RawRecordNode(
                    signature,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 8, 4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        bytes.AsSpan(position + 20, 2)),
                    groups,
                    bytes.AsSpan(
                            position + HeaderLength,
                            bodyLength)
                        .ToArray(),
                    position,
                    groupOffsets));
                position = recordEnd;
            }
            if (position != end)
                throw new InvalidDataException(
                    "A group has an invalid boundary.");
        }
    }

    private static IEnumerable<string> RecordInventory(
        IEnumerable<RawRecordNode> records) =>
        records.Select(record =>
            $"{record.Signature}|{record.FormId:X8}|" +
            string.Join(
                "/",
                record.Groups.Select(group =>
                    $"{group.Type}:{group.Label:X8}")));

    private static IEnumerable<string> PlacedReferenceBodies(
        IEnumerable<RawRecordNode> records) =>
        records
            .Where(record =>
                record.Signature is "ACHR" or "REFR")
            .Select(record =>
                $"{record.Signature}|{record.FormId:X8}|" +
                Convert.ToHexString(record.Body));

    private static ImmutableArray<GroupNode>
        ExpectedCellGroups(
            uint rawWorldspaceFormId,
            int gridX,
            int gridY) =>
        [
            new GroupNode(
                0,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    Encoding.ASCII.GetBytes("WRLD"))),
            new GroupNode(1, rawWorldspaceFormId),
            new GroupNode(
                4,
                CoordinateLabel(
                    FloorDivide(gridX, 32),
                    FloorDivide(gridY, 32))),
            new GroupNode(
                5,
                CoordinateLabel(
                    FloorDivide(gridX, 8),
                    FloorDivide(gridY, 8)))
        ];

    private static short FloorDivide(
        int value,
        int divisor)
    {
        int quotient =
            Math.DivRem(value, divisor, out int remainder);
        if (remainder < 0)
            quotient--;
        return checked((short)quotient);
    }

    private static uint CoordinateLabel(
        short x,
        short y)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan(0, 2),
            y);
        BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan(2, 2),
            x);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static byte[] CellGridPayload(
        int x,
        int y)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(0, 4),
            x);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4, 4),
            y);
        return bytes;
    }

    private static byte[] UInt32Payload(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] TransformPayload(
        SkyrimFollowerFinishPairTransform transform)
    {
        float[] values =
        [
            checked((float)transform.X),
            checked((float)transform.Y),
            checked((float)transform.Z),
            checked((float)transform.RotationX),
            checked((float)transform.RotationY),
            checked((float)transform.RotationZ)
        ];
        byte[] bytes = new byte[24];
        for (int index = 0;
             index < values.Length;
             index++)
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(index * 4, 4),
                values[index]);
        return bytes;
    }

    private static byte[] BuildSubrecords(
        IEnumerable<(string Signature, byte[] Payload)> rows)
    {
        int length = rows.Sum(row =>
            checked(6 + row.Payload.Length));
        byte[] bytes = new byte[length];
        int position = 0;
        foreach ((string signature, byte[] payload) in rows)
        {
            Encoding.ASCII.GetBytes(signature)
                .CopyTo(bytes, position);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2),
                checked((ushort)payload.Length));
            payload.CopyTo(bytes, position + 6);
            position += 6 + payload.Length;
        }
        return bytes;
    }

    private static void ValidateExactPartialRecord(
        byte[] bytes,
        int offset,
        uint expectedRawWorldspaceFormId)
    {
        if (Signature(bytes, offset) != "WRLD" ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 4, 4)) !=
                ExpectedBodyLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 8, 4)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 12, 4)) !=
                expectedRawWorldspaceFormId ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 16, 4)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 20, 2)) != 44 ||
            BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 22, 2)) != 0)
            throw new InvalidDataException(
                "follower-finish-world-partial-master-shape: " +
                "The candidate WRLD header is outside the admitted " +
                "81-byte empty-record shape.");

        ReadOnlySpan<byte> body =
            bytes.AsSpan(
                offset + HeaderLength,
                ExpectedBodyLength);
        (string Signature, int Length)[] expected =
        [
            ("ONAM", 16),
            ("DATA", 1),
            ("NAM0", 8),
            ("NAM9", 8)
        ];
        int position = 0;
        foreach ((string signature, int length) in expected)
        {
            if (position + 6 + length > body.Length ||
                Encoding.ASCII.GetString(
                    body.Slice(position, 4)) != signature ||
                BinaryPrimitives.ReadUInt16LittleEndian(
                    body.Slice(position + 4, 2)) != length ||
                body.Slice(position + 6, length)
                    .ContainsAnyExcept((byte)0))
                throw new InvalidDataException(
                    "follower-finish-world-partial-master-shape: " +
                    "The candidate WRLD payload is not the exact " +
                    "zeroed ONAM/DATA/NAM0/NAM9 shape.");
            position += 6 + length;
        }
        if (position != body.Length)
            throw new InvalidDataException(
                "follower-finish-world-partial-master-shape: " +
                "The candidate WRLD contains additional payload.");
    }

    private static void RequireHeader(
        byte[] bytes,
        int offset,
        int end)
    {
        if (offset < 0 || offset + HeaderLength > end ||
            end > bytes.Length)
            throw new InvalidDataException(
                "A plugin group or record header is truncated.");
    }

    private static string Signature(
        byte[] bytes,
        int offset) =>
        Encoding.ASCII.GetString(bytes, offset, 4);

    private sealed record ParsedPlugin(
        int TopWorldGroupOffset,
        int PartialRecordOffset,
        int FirstGroupOffset,
        int HedrRecordCountOffset,
        uint Tes4RecordCount,
        uint ExpectedRawWorldspaceFormId,
        int RequestedMasterIndex,
        int MasterCount,
        int WorldChildGroupCount,
        List<int> WorldspaceRecords);

    private sealed record GroupNode(
        uint Type,
        uint Label);

    private sealed record RawRecordNode(
        string Signature,
        uint FormId,
        uint Flags,
        ushort FormVersion,
        ImmutableArray<GroupNode> Groups,
        byte[] Body,
        int Offset,
        ImmutableArray<int> AncestorGroupOffsets);
}
