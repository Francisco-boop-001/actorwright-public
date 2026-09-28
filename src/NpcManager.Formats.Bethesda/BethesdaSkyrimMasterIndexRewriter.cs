using System.Buffers.Binary;
using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Relocates only FormID bytes demonstrated by two typed serializations of the
/// same source. Unrelated original bytes, including unknown subrecords, survive.
/// </summary>
internal static class BethesdaSkyrimMasterIndexRewriter
{
    internal static byte[] RewriteRecordIndices(byte[] original, SkyrimMod source, ImmutableArray<string> masters,
        IReadOnlyDictionary<FormKey, FormKey>? relocations = null, SkyrimMod? destination = null)
    {
        string[] beforeMasters = source.ModHeader.MasterReferences.Select(row => row.Master.ToString()).ToArray();
        if (relocations is null && destination is null && masters.SequenceEqual(beforeMasters, StringComparer.OrdinalIgnoreCase))
            return original;
        if (masters.Length > byte.MaxValue || masters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != masters.Length ||
            masters.Contains((destination ?? source).ModKey.ToString(), StringComparer.OrdinalIgnoreCase))
            throw Refusal("The target master table must contain distinct external owners within the Skyrim index range.");
        var targetIndices = masters.Select((owner, index) => (owner, index))
            .ToDictionary(row => row.owner, row => row.index, StringComparer.OrdinalIgnoreCase);
        uint RebaseId(uint value)
        {
            if (value == 0) return 0;
            int index = checked((int)(value >> 24));
            if (index > beforeMasters.Length) throw Refusal("Original FormID owner index exceeds the source master table.");
            string owner = index == beforeMasters.Length ? source.ModKey.ToString() : beforeMasters[index];
            var key = new FormKey(ModKey.FromNameAndExtension(owner), value & 0x00FF_FFFFu);
            if (relocations is not null && relocations.TryGetValue(key, out var mapped)) key = mapped;
            owner = key.ModKey.ToString();
            int target = key.ModKey == (destination ?? source).ModKey
                ? masters.Length : targetIndices.GetValueOrDefault(owner, -1);
            if (target < 0) throw Refusal("A retained FormID still references removed master " + owner);
            return ((uint)target << 24) | key.ID;
        }
        byte[] before = Serialize(source);
        var moved = destination ?? (SkyrimMod)source.DeepCopy();
        if (destination is null)
        {
            moved.ModHeader.MasterReferences.Clear();
            foreach (string master in masters)
                moved.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension(master) });
        }
        else if (!masters.SequenceEqual(moved.ModHeader.MasterReferences.Select(row => row.Master.ToString()),
                     StringComparer.OrdinalIgnoreCase))
            throw Refusal("The typed destination master table differs from the requested relocation layout.");
        byte[] after = Serialize(moved);
        var originalCensus = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(original);
        var beforeCensus = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(before);
        var afterCensus = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(after);
        var beforeRecords = beforeCensus.NonTes4Records.ToLookup(row => (row.Signature, row.RawFormId));
        var afterRecords = afterCensus.NonTes4Records.ToLookup(row => (row.Signature, row.RawFormId));
        byte[] result = original.ToArray();
        foreach (var record in originalCensus.NonTes4Records)
        {
            uint rebasedId = RebaseId(record.RawFormId);
            var oldRecord = ExactlyOne(beforeRecords[(record.Signature, record.RawFormId)],
                $"typed source {record.Signature} 0x{record.RawFormId:X8}");
            var newRecord = ExactlyOne(afterRecords[(record.Signature, rebasedId)],
                $"typed rebased {record.Signature} 0x{rebasedId:X8}");
            if (rebasedId != record.RawFormId)
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(record.Offset + 12, 4), rebasedId);
            if (oldRecord.Bytes.AsSpan(24).SequenceEqual(newRecord.Bytes.AsSpan(24)))
                continue;
            // A compressed stream is not an aligned subrecord surface. Refuse
            // an actual payload delta rather than guessing inside compressed data.
            if ((record.Flags & 0x0004_0000) != 0)
            {
                throw Refusal($"Compressed {record.Signature} 0x{record.RawFormId:X8} has an unaligned FormLink change.");
            }
            var oldSubs = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(before, oldRecord);
            var newSubs = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(after, newRecord);
            if (oldSubs.Length != newSubs.Length)
                throw Refusal($"Typed subrecord cardinality changed for {record.Signature} 0x{record.RawFormId:X8}.");
            var originalSubs = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(original, record)
                .ToLookup(row => row.Signature);
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < oldSubs.Length; index++)
            {
                var oldSub = oldSubs[index];
                var newSub = newSubs[index];
                int ordinal = ordinals.GetValueOrDefault(oldSub.Signature);
                ordinals[oldSub.Signature] = ordinal + 1;
                if (oldSub.Signature != newSub.Signature || oldSub.Length != newSub.Length)
                    throw Refusal($"Typed {record.Signature}/{oldSub.Signature} layout changed during relocation.");
                if (oldSub.Bytes.AsSpan().SequenceEqual(newSub.Bytes))
                    continue;
                var originalSub = originalSubs[oldSub.Signature].ElementAtOrDefault(ordinal);
                if (originalSub is null || !originalSub.Bytes.AsSpan().SequenceEqual(oldSub.Bytes))
                    throw Refusal($"Original {record.Signature}/{oldSub.Signature}[{ordinal}] cannot be aligned with typed FormLink evidence.");
                byte[] demonstrated = oldSub.Bytes.ToArray();
                for (int offset = 6; offset < oldSub.Length; offset++)
                {
                    if (demonstrated[offset] == newSub.Bytes[offset]) continue;
                    var candidates = new List<int>();
                    for (int begin = Math.Max(6, offset - 3); begin <= offset && begin + 4 <= oldSub.Length; begin++)
                    {
                        uint oldId = BinaryPrimitives.ReadUInt32LittleEndian(oldSub.Bytes.AsSpan(begin, 4));
                        uint newId = BinaryPrimitives.ReadUInt32LittleEndian(newSub.Bytes.AsSpan(begin, 4));
                        if (oldId == newId) continue;
                        try { if (RebaseId(oldId) == newId) candidates.Add(begin); }
                        catch (InvalidDataException) { }
                    }
                    if (candidates.Count != 1)
                        throw Refusal($"Typed {record.Signature}/{oldSub.Signature} change is not one unambiguous FormID relocation.");
                    int proven = candidates[0];
                    newSub.Bytes.AsSpan(proven, 4).CopyTo(demonstrated.AsSpan(proven, 4));
                    newSub.Bytes.AsSpan(proven, 4).CopyTo(result.AsSpan(originalSub.Offset + proven, 4));
                }
                if (!demonstrated.AsSpan().SequenceEqual(newSub.Bytes))
                    throw Refusal($"Typed {record.Signature}/{oldSub.Signature} contains an unexplained change.");
            }
        }

        // Only these Bethesda group types carry a FormID label. Other labels
        // are signatures, block numbers, or coordinates and must remain intact.
        foreach (var group in originalCensus.Groups.Where(row => row.Type is 1 or 6 or 7 or 8 or 9 or 10))
        {
            uint label = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(group.Offset + 8, 4));
            uint rebasedLabel = RebaseId(label);
            if (label == rebasedLabel)
                continue;
            var oldGroup = ExactlyOne(beforeCensus.Groups.Where(row => row.Type == group.Type &&
                    BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(row.Offset + 8, 4)) == label),
                $"typed source GRUP type={group.Type} owner=0x{label:X8}");
            var newGroup = ExactlyOne(afterCensus.Groups.Where(row => row.Type == group.Type &&
                    BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(row.Offset + 8, 4)) == rebasedLabel),
                $"typed rebased GRUP type={group.Type} owner=0x{rebasedLabel:X8}");
            if (oldGroup.Depth != newGroup.Depth || oldGroup.Depth != group.Depth)
                throw Refusal("Typed FormID-bearing GRUP depth changed during relocation.");
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(group.Offset + 8, 4), rebasedLabel);
        }
        return result;
    }

    private static byte[] Serialize(SkyrimMod mod)
    {
        using var stream = new MemoryStream();
        mod.WriteToBinary(stream, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        return stream.ToArray();
    }

    private static T ExactlyOne<T>(IEnumerable<T> rows, string identity)
    {
        T[] matches = rows.Take(2).ToArray();
        if (matches.Length != 1)
            throw Refusal($"{identity} requires exactly 1 record; observed={matches.Length} (2 means at least 2).");
        return matches[0];
    }

    private static InvalidDataException Refusal(string message) => new("plugin-master-index: " + message);
}
