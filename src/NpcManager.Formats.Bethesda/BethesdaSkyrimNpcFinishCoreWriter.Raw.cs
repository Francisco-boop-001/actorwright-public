using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal static class BethesdaSkyrimNpcFinishCoreRaw
{
    internal sealed record RawRecord(
        string Signature,
        uint Flags,
        uint RawFormId,
        int Offset,
        int Length,
        int PayloadOffset,
        int PayloadLength,
        string GroupLabel,
        string GroupPath,
        byte[] Bytes);

    internal sealed record RawGroup(
        int Depth,
        string Label,
        string Path,
        int Offset,
        int Type,
        uint RawLabel);

    internal sealed record RawCensus(
        ImmutableArray<RawRecord> Records,
        ImmutableArray<RawRecord> NonTes4Records,
        ImmutableArray<RawGroup> Groups);

    internal sealed record RawSubrecord(
        string Signature,
        int Offset,
        int Length,
        byte[] Bytes);

    internal sealed record Tes4Snapshot(
        RawRecord Record,
        uint Flags,
        uint RecordCount,
        uint NextFormId,
        ImmutableArray<string> Masters,
        ImmutableArray<RawSubrecord> Subrecords);

    internal sealed record RawPluginSnapshot(
        Tes4Snapshot Tes4,
        RawRecord? Target,
        ImmutableArray<RawRecord> Records,
        ImmutableArray<RawGroup> Groups);

    internal static RawPluginSnapshot Read(
        byte[] bytes,
        uint targetFormId = 0x800,
        RawCensus? census = null)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The plugin must begin with TES4.");
        targetFormId &= 0x00FF_FFFFu;
        census ??= ReadCensus(bytes);
        ImmutableArray<RawRecord> records = census.Records;
        RawRecord tes4 = RequireExactlyOne(
            records.Where(row => row.Signature == "TES4"),
            "finish-core-writer-tes4-count",
            "TES4");
        ImmutableArray<RawSubrecord> tes4Subs = ReadSubrecords(bytes, tes4);
        RawSubrecord hedr = RequireExactlyOne(
            tes4Subs.Where(row => row.Signature == "HEDR"),
            "finish-core-writer-hedr-count",
            "TES4 HEDR");
        if (hedr.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(
                hedr.Bytes.AsSpan(4, 2)) < 12)
            throw new InvalidDataException("The plugin HEDR subrecord is malformed.");
        ReadOnlySpan<byte> hedrPayload = hedr.Bytes.AsSpan(6);
        var masters = ImmutableArray.CreateBuilder<string>();
        for (int i = 0; i < tes4Subs.Length; i++)
        {
            if (tes4Subs[i].Signature != "MAST")
                continue;
            string master = Encoding.UTF8.GetString(tes4Subs[i].Bytes.AsSpan(6))
                .TrimEnd('\0');
            masters.Add(master);
        }
        RawRecord target = RequireExactlyOne(
            records.Where(row =>
                row.Signature == "NPC_" &&
                row.RawFormId == ((uint)masters.Count << 24 | targetFormId)),
            "finish-core-writer-npc-count",
            $"NPC_ 0x{targetFormId:X8}");
        return new RawPluginSnapshot(
            new Tes4Snapshot(
                tes4,
                BinaryPrimitives.ReadUInt32LittleEndian(tes4.Bytes.AsSpan(8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(hedrPayload.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(hedrPayload.Slice(8, 4)),
                masters.ToImmutable(),
                tes4Subs),
            target,
            records,
            census.Groups);
    }

    internal static byte[] Splice(
        byte[] sourceBytes,
        byte[] scratchBytes,
        SkyrimNpcFinishCoreProposal proposal,
        RawPluginSnapshot source,
        CancellationToken cancellationToken)
    {
        uint targetFormId = proposal.Request!.Actor.FormId!.Value.Value;
        RawPluginSnapshot scratch = Read(scratchBytes, targetFormId);
        if (source.Target is null || scratch.Target is null)
            throw new InvalidDataException("The source or scratch target NPC is missing.");
        ValidateSourceHeader(source, proposal);
        RawRecord scratchTarget = scratch.Target;
        byte[] targetBytes = RewriteNpc(
            sourceBytes, source.Target, scratchBytes, scratchTarget, proposal);
        byte[] tes4Bytes = RewriteTes4(
            sourceBytes, source.Tes4, proposal, proposal.NewRecords.Length);
        var replacements = new Dictionary<int, byte[]>
        {
            [source.Tes4.Record.Offset] = tes4Bytes,
            [source.Target.Offset] = targetBytes
        };
        string? retainedCsty = proposal.ExistingRecordChanges
            .Where(row => row.StartsWith("CSTY ", StringComparison.Ordinal))
            .SingleOrDefault(row => !proposal.NewRecords.Contains(
                row.Split(':', 2)[0], StringComparer.Ordinal));
        if (retainedCsty is not null)
        {
            string identity = retainedCsty.Split(':', 2)[0];
            uint localId = ParseId(identity.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[1]);
            uint sourceRawFormId = ((uint)source.Tes4.Masters.Length << 24) | localId;
            RawRecord sourceStyle = RequireExactlyOne(
                source.Records.Where(row => row.Signature == "CSTY" &&
                    row.RawFormId == sourceRawFormId),
                "finish-core-writer-csty-count", identity);
            RawRecord scratchStyle = RequireExactlyOne(
                scratch.Records.Where(row => row.Signature == "CSTY" &&
                    row.RawFormId == ((uint)proposal.MasterOrder.Length << 24 | localId)),
                "finish-core-writer-csty-count", identity);
            replacements[sourceStyle.Offset] = scratchStyle.Bytes;
        }
        byte[] rebuilt = RewriteTree(
            sourceBytes, sourceBytes.Length, replacements);
        foreach (string recordName in proposal.NewRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] parts = recordName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            uint localId = ParseId(parts[1]);
            RawRecord record = RequireExactlyOne(
                scratch.Records.Where(row =>
                    row.Signature == parts[0] &&
                    row.RawFormId == ((uint)proposal.MasterOrder.Length << 24 | localId)),
                ScratchRecordCountCode(parts[0]),
                $"{parts[0]} 0x{localId:X8}");
            rebuilt = rebuilt.Concat(BuildGroup(record.Signature, record.Bytes)).ToArray();
        }
        return rebuilt;
    }

    private static void ValidateSourceHeader(
        RawPluginSnapshot source,
        SkyrimNpcFinishCoreProposal proposal)
    {
        if (source.Tes4.Flags != proposal.SourceTes4Flags)
            throw new InvalidDataException("finish-core-writer-tes4-flags: source TES4 flags drifted.");
        if (!proposal.MasterOrder.Take(source.Tes4.Masters.Length)
                .SequenceEqual(source.Tes4.Masters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("finish-core-writer-master-order: source masters drifted.");
        if (proposal.MasterOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            proposal.MasterOrder.Length)
            throw new InvalidDataException("finish-core-writer-master-order: duplicate masters.");
        if (source.Tes4.NextFormId > proposal.NextFormId.Value ||
            (source.Tes4.NextFormId == proposal.NextFormId.Value && !proposal.NewRecords.IsEmpty))
            throw new InvalidDataException("finish-core-writer-next-form-id: allocation did not advance.");
    }

    private static byte[] RewriteNpc(
        byte[] sourceBytes,
        RawRecord sourceNpc,
        byte[] scratchBytes,
        RawRecord scratchNpc,
        SkyrimNpcFinishCoreProposal proposal)
    {
        ImmutableArray<RawSubrecord> sourceSubs = ReadSubrecords(sourceBytes, sourceNpc);
        ImmutableArray<RawSubrecord> scratchSubs = ReadSubrecords(scratchBytes, scratchNpc);
        HashSet<string> allowed = ["ACBS", "SNAM", "ZNAM", "DOFT", "CNTO", "PKID"];
        if (proposal.Request!.AiPolicy is not null)
            allowed.Add("AIDT");
        if (!proposal.Request.PerkPolicy.IsDefault)
        {
            allowed.Add("PRKZ");
            allowed.Add("PRKR");
        }
        var payload = new List<byte>();
        HashSet<string> emitted = new(StringComparer.Ordinal);
        foreach (RawSubrecord sourceSub in sourceSubs)
        {
            if (!allowed.Contains(sourceSub.Signature))
            {
                payload.AddRange(sourceSub.Bytes);
                continue;
            }
            if (!emitted.Add(sourceSub.Signature))
                continue;
            if (sourceSub.Signature == "CNTO" &&
                proposal.Request!.InventoryPolicy.Policy ==
                SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory)
            {
                payload.AddRange(sourceSubs
                    .Where(row => row.Signature == sourceSub.Signature)
                    .SelectMany(row => row.Bytes));
            }
            else
            {
                payload.AddRange(scratchSubs
                    .Where(row => row.Signature == sourceSub.Signature)
                    .SelectMany(row => row.Bytes));
            }
        }
        foreach (string signature in allowed)
        {
            if (emitted.Contains(signature))
                continue;
            if (signature == "CNTO" && proposal.Request!.InventoryPolicy.Policy ==
                SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory)
                continue;
            payload.AddRange(scratchSubs
                .Where(row => row.Signature == signature)
                .SelectMany(row => row.Bytes));
        }
        byte[] header = sourceNpc.Bytes.AsSpan(0, 24).ToArray();
        const int maximumFormIdOwnerIndex = byte.MaxValue;
        int outputOwnerIndex = proposal.MasterOrder.Length;
        if (outputOwnerIndex > maximumFormIdOwnerIndex)
            throw new InvalidDataException(
                "finish-core-writer-master-index: the output plugin owner index exceeds the 8-bit Skyrim FormID range.");
        uint sourceFormId = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(12, 4));
        uint localFormId = sourceFormId & 0x00FF_FFFFu;
        if (localFormId == 0)
            throw new InvalidDataException(
                "finish-core-writer-npc-form-id: the target NPC uses the null local FormID.");
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(12, 4),
            checked(((uint)outputOwnerIndex << 24) | localFormId));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(4, 4), checked((uint)payload.Count));
        return header.Concat(payload).ToArray();
    }

    private static byte[] RewriteTes4(
        byte[] sourceBytes,
        Tes4Snapshot source,
        SkyrimNpcFinishCoreProposal proposal,
        int newRecordCount)
    {
        var payload = new List<byte>();
        bool hedrWritten = false;
        foreach (RawSubrecord subrecord in source.Subrecords)
        {
            if (subrecord.Signature != "HEDR")
            {
                payload.AddRange(subrecord.Bytes);
                continue;
            }
            byte[] hedr = subrecord.Bytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(
                hedr.AsSpan(10, 4),
                checked(source.RecordCount + checked((uint)newRecordCount * 2u)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                hedr.AsSpan(14, 4), proposal.NextFormId.Value);
            payload.AddRange(hedr);
            hedrWritten = true;
        }
        if (!hedrWritten)
            throw new InvalidDataException("finish-core-writer-tes4-hedr: HEDR is missing.");
        foreach (string master in proposal.MasterOrder.Skip(source.Masters.Length))
        {
            byte[] name = Encoding.UTF8.GetBytes(master + "\0");
            payload.AddRange(BuildSubrecord("MAST", name));
            payload.AddRange(BuildSubrecord("DATA", new byte[8]));
        }
        byte[] header = source.Record.Bytes.AsSpan(0, 24).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(4, 4), checked((uint)payload.Count));
        return header.Concat(payload).ToArray();
    }

    internal static byte[] RewriteTree(
        byte[] bytes,
        int end,
        Dictionary<int, byte[]> replacements)
    {
        byte[] result = RewriteRange(0, end);
        return result;

        byte[] RewriteRange(int start, int finish)
        {
            var output = new List<byte>();
            int position = start;
            while (position < finish)
            {
                string signature = Encoding.ASCII.GetString(bytes, position, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
                int length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                if (replacements.TryGetValue(position, out byte[]? replacement))
                {
                    output.AddRange(replacement);
                    position += length;
                    continue;
                }
                if (signature != "GRUP")
                {
                    output.AddRange(bytes.AsSpan(position, length).ToArray());
                    position += length;
                    continue;
                }
                byte[] children = RewriteRange(position + 24, position + length);
                byte[] header = bytes.AsSpan(position, 24).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(
                    header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                output.AddRange(header);
                output.AddRange(children);
                position += length;
            }
            return output.ToArray();
        }
    }

    internal static byte[] BuildGroup(string label, byte[] record)
    {
        byte[] header = new byte[24];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(4, 4), checked((uint)(24 + record.Length)));
        Encoding.ASCII.GetBytes(label).CopyTo(header, 8);
        return header.Concat(record).ToArray();
    }

    private static string ScratchRecordCountCode(string signature) =>
        signature switch
        {
            "CSTY" => "finish-core-writer-csty-count",
            "OTFT" => "finish-core-writer-otft-count",
            "PACK" => "finish-core-writer-pack-count",
            "RELA" => "finish-core-writer-rela-count",
            _ => "finish-core-writer-record-count"
        };

    private static T RequireExactlyOne<T>(
        IEnumerable<T> source,
        string code,
        string identity)
    {
        T[] matches = source.Take(2).ToArray();
        if (matches.Length == 1)
            return matches[0];
        string observed = matches.Length == 2
            ? "at least 2"
            : matches.Length.ToString(CultureInfo.InvariantCulture);
        throw new InvalidDataException(
            $"{code}: {identity} requires exactly 1 record; observed {observed} " +
            $"(expected=1; observed={observed}).");
    }

    private static ImmutableArray<RawRecord> ReadRecords(byte[] bytes) =>
        ReadCensus(bytes).Records;

    internal static RawCensus ReadCensus(byte[] bytes)
    {
        var records = ImmutableArray.CreateBuilder<RawRecord>();
        var groups = ImmutableArray.CreateBuilder<RawGroup>();
        Walk(0, bytes.Length, 0, string.Empty, string.Empty);
        ImmutableArray<RawRecord> allRecords = records.ToImmutable();
        return new RawCensus(
            allRecords,
            allRecords.Where(row => row.Signature != "TES4").ToImmutableArray(),
            groups.ToImmutable());

        void Walk(
            int start,
            int end,
            int depth,
            string groupLabel,
            string groupPath)
        {
            var siblingOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            int position = start;
            while (position < end)
            {
                if (position + 24 > end)
                    throw new InvalidDataException("A record header is truncated.");
                string signature = Encoding.ASCII.GetString(bytes, position, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
                int length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                if (length < 24 || position + length > end)
                    throw new InvalidDataException("A record exceeds its containing group.");
                if (signature == "GRUP")
                {
                    int type = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                    uint rawLabel = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4));
                    string label = type == 0
                        ? Encoding.ASCII.GetString(bytes, position + 8, 4)
                        : type.ToString(CultureInfo.InvariantCulture) + ":" + rawLabel.ToString("X8", CultureInfo.InvariantCulture);
                    int ordinal = siblingOrdinals.TryGetValue(label, out int existing)
                        ? existing
                        : 0;
                    siblingOrdinals[label] = checked(ordinal + 1);
                    string path = BuildGroupPath(groupPath, label, ordinal);
                    groups.Add(new RawGroup(depth + 1, label, path, position, type, rawLabel));
                    Walk(position + 24, position + length, depth + 1, label, path);
                }
                else
                {
                    records.Add(new RawRecord(
                        signature,
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4)),
                        position,
                        length,
                        position + 24,
                        checked((int)size),
                        groupLabel,
                        groupPath,
                        bytes.AsSpan(position, length).ToArray()));
                }
                position += length;
            }
        }
    }

    internal static string BuildGroupPath(string parentPath, string label, int ordinal) =>
        string.IsNullOrEmpty(parentPath)
            ? label + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]"
            : parentPath + "/" + label + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";

    internal static int CountRecords(byte[] bytes, string signature, uint localFormId)
    {
        localFormId &= 0x00FF_FFFFu;
        int count = 0;
        foreach (RawRecord record in ReadCensus(bytes).NonTes4Records)
        {
            if (record.Signature != signature ||
                (record.RawFormId & 0x00FF_FFFFu) != localFormId)
                continue;
            count++;
            if (count == 2)
                return count;
        }
        return count;
    }

    internal static int CountSelfRecords(byte[] bytes, string signature, uint localFormId)
    {
        RawCensus census = ReadCensus(bytes);
        RawRecord tes4 = RequireExactlyOne(census.Records.Where(row => row.Signature == "TES4"),
            "finish-core-writer-tes4-count", "TES4");
        int owner = ReadSubrecords(bytes, tes4).Count(row => row.Signature == "MAST");
        uint rawId = ((uint)owner << 24) | (localFormId & 0x00FF_FFFFu);
        return census.NonTes4Records.Count(row => row.Signature == signature && row.RawFormId == rawId);
    }

    private static ImmutableArray<RawSubrecord> ReadSubrecords(
        byte[] bytes,
        RawRecord record)
    {
        var rows = ImmutableArray.CreateBuilder<RawSubrecord>();
        int position = record.PayloadOffset;
        int end = record.PayloadOffset + record.PayloadLength;
        while (position < end)
        {
            if (position + 6 > end)
                throw new InvalidDataException("A subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            int length = checked(6 + size);
            if (position + length > end)
                throw new InvalidDataException("A subrecord exceeds its record.");
            rows.Add(new RawSubrecord(
                signature,
                position,
                length,
                bytes.AsSpan(position, length).ToArray()));
            position += length;
        }
        return rows.ToImmutable();
    }

    internal static ImmutableArray<RawSubrecord> GetSubrecords(
        byte[] bytes,
        RawRecord record) => ReadSubrecords(bytes, record);

    private static byte[] BuildSubrecord(string signature, byte[] payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new InvalidDataException("A Finish Core subrecord exceeds 16-bit length.");
        byte[] result = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), (ushort)payload.Length);
        payload.CopyTo(result, 6);
        return result;
    }

    private static uint ParseId(string value)
    {
        string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..] : value;
        if (!uint.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out uint id))
            throw new InvalidDataException($"Invalid allocation '{value}'.");
        return id;
    }
}
