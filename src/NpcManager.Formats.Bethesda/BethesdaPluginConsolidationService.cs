using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record PluginConsolidationRecord(
    string Signature, string SourceOwner, uint SourceId, string OutputOwner, uint OutputId);

public sealed record PluginConsolidationOutput(
    byte[] PluginBytes, ImmutableArray<string> Masters, ImmutableArray<PluginConsolidationRecord> Records);

/// <summary>Imports copied provider records while retaining proven original record bytes.</summary>
public static class BethesdaPluginConsolidationService
{
    public static PluginConsolidationOutput Consolidate(
        WorkspacePath input, ImmutableArray<WorkspacePath> providers, PluginName outputName, bool eslFlag)
    {
        var main = Read(input);
        var sources = providers.Select(Read).ToArray();
        var removed = sources.Select(row => row.Mod.ModKey).ToHashSet();
        if (removed.Count != sources.Length || removed.Contains(main.Mod.ModKey))
            throw Refusal("Provider names must be distinct and different from the input plugin.");
        var outputKey = ModKey.FromNameAndExtension(outputName.Value);
        if (removed.Contains(outputKey) || main.Mod.ModHeader.MasterReferences.Any(row => row.Master == outputKey))
            throw Refusal("The output filename conflicts with a provider or retained master.");
        var moved = (SkyrimMod)main.Mod.DeepCopy();
        uint next = Math.Max(moved.ModHeader.Stats.NextFormID,
            moved.EnumerateMajorRecords().Where(row => row.FormKey.ModKey == moved.ModKey)
                .Select(row => checked(row.FormKey.ID + 1)).DefaultIfEmpty(0x800u).Max());
        var links = new Dictionary<FormKey, FormKey>();
        foreach (var source in sources.OrderBy(row => row.Mod.ModKey.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            if (source.Census.Groups.Any(row => row.Type != 0 || row.Depth != 1))
                throw Refusal("Imported provider records require top-level groups; nested provider groups are not supported.");
            foreach (var record in source.Mod.EnumerateMajorRecords().OrderBy(row => row.FormKey.ID))
            {
                if (record.FormKey.ModKey != source.Mod.ModKey)
                    throw Refusal("Provider overrides are not imported; supply a provider-owned record plugin.");
                if (next > 0x00FF_FFFF) throw Refusal("No free local FormID remains.");
                var key = new FormKey(moved.ModKey, next++);
                links.Add(record.FormKey, key);
                moved.GetTopLevelGroup(record.GetType()).DuplicateInAsNewUntypedRecord(record, key);
            }
        }
        moved.RemapLinks(links);
        var masters = main.Mod.ModHeader.MasterReferences.Select(row => row.Master)
            .Concat(sources.SelectMany(row => row.Mod.ModHeader.MasterReferences.Select(master => master.Master)))
            .Where(key => key != moved.ModKey && !removed.Contains(key)).Distinct().ToArray();
        if (masters.Length > byte.MaxValue) throw Refusal("The consolidated master table exceeds 255 entries.");
        var available = masters.Append(moved.ModKey).ToHashSet();
        if (moved.EnumerateFormLinks().Any(link => !link.FormKey.IsNull && !available.Contains(link.FormKey.ModKey)))
            throw Refusal("A retained FormLink still needs an omitted provider record or undeclared master.");
        moved.ModHeader.MasterReferences.Clear();
        foreach (var master in masters) moved.ModHeader.MasterReferences.Add(new MasterReference { Master = master });
        ((IMod)moved).NextFormID = next;
        var masterNames = masters.Select(key => key.ToString()).ToImmutableArray();
        if ((links.Count > 0 || !main.Mod.ModHeader.MasterReferences.Select(row => row.Master).SequenceEqual(masters)) &&
            sources.Prepend(main).Any(source => BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(source.Bytes, source.Census.Records[0])
                .Any(row => row.Signature == "ONAM")))
            throw Refusal("TES4 ONAM override tables require dedicated relocation and are not admitted when FormIDs change.");
        byte[] relocated = BethesdaSkyrimMasterIndexRewriter.RewriteRecordIndices(
            main.Bytes, main.Mod, masterNames, links, moved);
        var imported = new List<BethesdaSkyrimNpcFinishCoreRaw.RawRecord>();
        foreach (var source in sources)
        {
            byte[] rebased = BethesdaSkyrimMasterIndexRewriter.RewriteRecordIndices(
                source.Bytes, source.Mod, masterNames, links, moved);
            imported.AddRange(BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(rebased).NonTes4Records);
        }
        if (eslFlag || main.Mod.IsSmallMaster)
        {
            if (next > 0x1000 || moved.EnumerateMajorRecords()
                .Any(row => row.FormKey.ModKey == moved.ModKey && row.FormKey.ID > 0xFFF))
                throw Refusal("ESL eligibility requires owned IDs within 0x000..0xFFF and next object ID at most 0x1000; compaction is not performed.");
            foreach (var record in main.Census.Records.Concat(sources.SelectMany(row => row.Census.Records)))
                if (BinaryPrimitives.ReadUInt16LittleEndian(record.Bytes.AsSpan(20, 2)) != 44 ||
                    record.Signature is "CELL" or "WRLD" or "ACHR" or "REFR" or "NAVM" or "NAVI")
                    throw Refusal("ESL eligibility requires Form44 and no CELL/WRLD/ACHR/REFR/NAVM/NAVI records.");
        }
        byte[] result = Merge(relocated, imported, masterNames, next, eslFlag);
        var mappings = main.Census.NonTes4Records.Select(record => MapRecord(main, record))
            .Concat(sources.SelectMany(source => source.Census.NonTes4Records.Select(record => MapRecord(source, record))))
            .ToImmutableArray();
        // Reopening is done from the fresh staged file by Verify. The complete
        // raw census here also validates newly constructed group boundaries.
        var final = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(result);
        if (final.NonTes4Records.Length != mappings.Length)
            throw Refusal("The consolidated raw record inventory is incomplete.");
        return new(result, masterNames, mappings);

        PluginConsolidationRecord MapRecord(Source source, BethesdaSkyrimNpcFinishCoreRaw.RawRecord record)
        {
            var old = Decode(record.RawFormId, source.Mod);
            var mapped = links.GetValueOrDefault(old, old);
            return new(record.Signature, old.ModKey.ToString(), old.ID,
                mapped.ModKey == main.Mod.ModKey ? outputName.Value : mapped.ModKey.ToString(), mapped.ID);
        }
    }

    public static void Verify(WorkspacePath path, PluginConsolidationOutput expected, bool eslFlag)
    {
        byte[] bytes = File.ReadAllBytes(path.Value);
        if (!bytes.AsSpan().SequenceEqual(expected.PluginBytes))
            throw Refusal("Staged plugin bytes differ from the proven relocation result.");
        var mod = SkyrimMod.CreateFromBinary(path.Value, SkyrimRelease.SkyrimSE);
        var actual = mod.EnumerateMajorRecords().ToArray();
        if (actual.Length != expected.Records.Length ||
            !mod.ModHeader.MasterReferences.Select(row => row.Master.ToString()).SequenceEqual(expected.Masters,
                StringComparer.OrdinalIgnoreCase) || (eslFlag && !mod.IsSmallMaster))
            throw Refusal("Independent plugin readback disagrees with the complete inventory, masters or ESL flag.");
        foreach (var row in expected.Records)
            if (actual.Count(record => record.FormKey == new FormKey(ModKey.FromNameAndExtension(row.OutputOwner), row.OutputId)) != 1)
                throw Refusal("Independent readback lost or duplicated an owner-qualified record.");
        var local = actual.Select(row => row.FormKey).ToHashSet();
        if (mod.EnumerateFormLinks().Any(link => link.FormKey.ModKey == mod.ModKey && !local.Contains(link.FormKey)))
            throw Refusal("Independent readback found a dangling output-local FormLink.");
    }

    public static byte[] RebaseSeq(byte[] bytes, string sourceOwner, ImmutableArray<PluginName> sourceMasters,
        PluginConsolidationOutput output)
    {
        if (bytes.Length == 0 || bytes.Length % 4 != 0) throw Refusal("SEQ must contain complete quest FormIDs.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int offset = 0; offset < bytes.Length; offset += 4)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            uint index = raw >> 24;
            if (index > sourceMasters.Length) throw Refusal("SEQ owner index exceeds its source master table.");
            string owner = index == sourceMasters.Length ? sourceOwner : sourceMasters[(int)index].Value;
            var rows = output.Records.Where(row => row.Signature == "QUST" && row.SourceId == (raw & 0x00FF_FFFF) &&
                row.SourceOwner.Equals(owner, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rows.Length != 1) throw Refusal("SEQ references a quest outside the copied record closure.");
            int targetIndex = output.Masters.IndexOf(rows[0].OutputOwner, StringComparer.OrdinalIgnoreCase);
            if (targetIndex < 0) targetIndex = output.Masters.Length;
            writer.Write(((uint)targetIndex << 24) | rows[0].OutputId);
        }
        return stream.ToArray();
    }

    private static Source Read(WorkspacePath path)
    {
        byte[] bytes = File.ReadAllBytes(path.Value);
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(bytes);
        if (census.Records.Count(row => row.Signature == "TES4") != 1 || census.Records[0].Signature != "TES4" ||
            (census.Records[0].Flags & 0x80) != 0)
            throw Refusal("Consolidation requires one nonlocalized TES4 header.");
        var mod = SkyrimMod.CreateFromBinary(path.Value, SkyrimRelease.SkyrimSE);
        var records = mod.EnumerateMajorRecords().ToArray();
        if (records.Length != census.NonTes4Records.Length || records.Select(row => row.FormKey).Distinct().Count() != records.Length)
            throw Refusal("All raw records must have one unambiguous typed owner-qualified identity.");
        var masters = mod.ModHeader.MasterReferences.Select(row => row.Master).ToArray();
        if (masters.Distinct().Count() != masters.Length || masters.Contains(mod.ModKey))
            throw Refusal("Duplicate or self-referencing masters are invalid.");
        return new(bytes, mod, census);
    }

    private static FormKey Decode(uint raw, SkyrimMod mod)
    {
        uint index = raw >> 24;
        if (index > mod.ModHeader.MasterReferences.Count) throw Refusal("Record owner index exceeds its master table.");
        return new(index == mod.ModHeader.MasterReferences.Count ? mod.ModKey : mod.ModHeader.MasterReferences[(int)index].Master,
            raw & 0x00FF_FFFF);
    }

    private static byte[] Merge(byte[] main, List<BethesdaSkyrimNpcFinishCoreRaw.RawRecord> imported,
        ImmutableArray<string> masters, uint next, bool eslFlag)
    {
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(main);
        var groups = imported.GroupBy(row => row.Signature).ToDictionary(group => group.Key,
            group => group.SelectMany(row => row.Bytes).ToArray(), StringComparer.Ordinal);
        var replacements = new Dictionary<int, byte[]>();
        foreach (var group in census.Groups.Where(row => row.Type == 0 && row.Depth == 1))
        {
            string signature = Encoding.ASCII.GetString(main.AsSpan(group.Offset + 8, 4));
            if (!groups.Remove(signature, out var records)) continue;
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(main.AsSpan(group.Offset + 4, 4)));
            byte[] combined = [.. main.AsSpan(group.Offset, length).ToArray(), .. records];
            BinaryPrimitives.WriteUInt32LittleEndian(combined.AsSpan(4, 4), checked((uint)combined.Length));
            replacements[group.Offset] = combined;
        }
        using var tail = new MemoryStream();
        int headerLength = census.Records[0].Length;
        for (int offset = headerLength; offset < main.Length;)
        {
            bool group = main.AsSpan(offset, 4).SequenceEqual("GRUP"u8);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(main.AsSpan(offset + 4, 4))) + (group ? 0 : 24);
            tail.Write(replacements.GetValueOrDefault(offset) ?? main.AsSpan(offset, length).ToArray());
            offset += length;
        }
        foreach (var group in groups.OrderBy(row => row.Key, StringComparer.Ordinal))
        {
            byte[] header = new byte[24];
            "GRUP"u8.CopyTo(header);
            Encoding.ASCII.GetBytes(group.Key).CopyTo(header, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), checked((uint)(24 + group.Value.Length)));
            tail.Write(header); tail.Write(group.Value);
        }
        var subs = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(main, census.Records[0]);
        if (subs.Count(row => row.Signature == "HEDR" && row.Length >= 18) != 1)
            throw Refusal("TES4 requires one complete HEDR.");
        var retainedMasterData = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < subs.Length; index++)
        {
            var sub = subs[index];
            if (sub.Signature != "MAST") continue;
            if (sub.Bytes.Length < 7 || sub.Bytes[^1] != 0) throw Refusal("MAST must be null-terminated.");
            if (++index >= subs.Length || subs[index].Signature != "DATA" || subs[index].Length != 14)
                throw Refusal("MAST requires an eight-byte DATA companion.");
            string master = Encoding.UTF8.GetString(sub.Bytes.AsSpan(6, sub.Bytes.Length - 7));
            retainedMasterData.Add(master, subs[index].Bytes.AsSpan(6, 8).ToArray());
        }
        using var payload = new MemoryStream();
        bool wroteMasters = false;
        for (int index = 0; index < subs.Length; index++)
        {
            var sub = subs[index];
            if (sub.Signature == "MAST")
            {
                if (!wroteMasters) WriteMasters();
                index++;
                continue;
            }
            byte[] bytes = sub.Bytes.ToArray();
            if (sub.Signature == "HEDR")
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4),
                    checked((uint)(census.NonTes4Records.Length + imported.Count + census.Groups.Length + groups.Count)));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), next);
            }
            payload.Write(bytes);
        }
        if (!wroteMasters) WriteMasters();
        void WriteMasters()
        {
            wroteMasters = true;
            using var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
            foreach (string master in masters)
            {
                byte[] name = Encoding.UTF8.GetBytes(master + "\0");
                writer.Write("MAST"u8); writer.Write(checked((ushort)name.Length)); writer.Write(name);
                writer.Write("DATA"u8); writer.Write((ushort)8); writer.Write(retainedMasterData.GetValueOrDefault(master) ?? new byte[8]);
            }
        }
        byte[] tes4 = main.AsSpan(0, 24).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(tes4.AsSpan(4, 4), checked((uint)payload.Length));
        if (eslFlag) BinaryPrimitives.WriteUInt32LittleEndian(tes4.AsSpan(8, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(tes4.AsSpan(8, 4)) | 0x200);
        return [.. tes4, .. payload.ToArray(), .. tail.ToArray()];
    }

    private sealed record Source(byte[] Bytes, SkyrimMod Mod, BethesdaSkyrimNpcFinishCoreRaw.RawCensus Census);
    private static InvalidDataException Refusal(string message) => new("plugin-consolidation: " + message);
}
