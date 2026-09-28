using System.Buffers.Binary;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

/// <summary>Preserves all source record bytes except the target NPC VTYP and TES4 HEDR.</summary>
public static class BethesdaSkyrimDialogueSplicer
{
    public static byte[] Splice(byte[] source, byte[] scratch, SkyrimDialogueProposal proposal)
    {
        var original = BethesdaSkyrimNpcFinishCoreRaw.Read(source, proposal.Manifest.Npc.FormId.Value);
        var addition = BethesdaSkyrimNpcFinishCoreRaw.Read(scratch, proposal.Manifest.Npc.FormId.Value);
        if (!original.Tes4.Masters.SequenceEqual(proposal.MasterOrder) || !addition.Tes4.Masters.SequenceEqual(proposal.MasterOrder))
            throw new InvalidDataException("dialogue-source-hash-mismatch: Raw master order changed.");
        var replacements = new Dictionary<int, byte[]>();
        var oldNpc = original.Target!;
        byte[] voice = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(scratch, addition.Target!).Single(x => x.Signature == "VTCK").Bytes;
        var subs = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(source, oldNpc);
        if (subs.Count(x => x.Signature == "VTCK") > 1) throw new InvalidDataException("dialogue-manifest-invalid: Duplicate NPC voice subrecord.");
        // Skyrim's voice link is VTCK; VTYP is the linked major record signature.
        var payload = new List<byte>(); bool replaced = false;
        foreach (var sub in subs)
        {
            if (sub.Signature == "VTCK") { payload.AddRange(voice); replaced = true; }
            else payload.AddRange(sub.Bytes);
        }
        if (!replaced)
        {
            // Match Mutagen's canonical NPC ordering: voice follows RNAM and precedes template/skin/head data.
            var race = subs.LastOrDefault(x => x.Signature == "RNAM");
            int insertion = race is null ? payload.Count : subs.TakeWhile(x => x != race).Sum(x => x.Length) + race.Length;
            payload.InsertRange(insertion, voice);
        }
        byte[] header = oldNpc.Bytes[..24]; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)payload.Count));
        replacements[oldNpc.Offset] = header.Concat(payload).ToArray();
        var append = new List<byte[]>();
        foreach (var group in addition.Groups.Where(x => x.Depth == 1 && x.Label != "NPC_"))
        {
            if (group.Label is not ("VTYP" or "QUST" or "DLBR" or "DIAL")) throw new InvalidDataException("dialogue-manifest-invalid: Unexpected scratch group.");
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(scratch.AsSpan(group.Offset + 4)));
            byte[] children = scratch.AsSpan(group.Offset + 24, size - 24).ToArray();
            var matches = original.Groups.Where(x => x.Depth == 1 && x.Label == group.Label).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("dialogue-manifest-invalid: Duplicate source top-level group.");
            if (matches.Length == 0) append.Add(BethesdaSkyrimNpcFinishCoreRaw.BuildGroup(group.Label, children));
            else
            {
                int offset = matches[0].Offset;
                int oldSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4)));
                byte[] combined = source.AsSpan(offset, oldSize).ToArray().Concat(children).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(combined.AsSpan(4), checked((uint)combined.Length));
                replacements[offset] = combined;
            }
        }
        byte[] output = BethesdaSkyrimNpcFinishCoreRaw.RewriteTree(source, source.Length, replacements).Concat(append.SelectMany(x => x)).ToArray();
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(output);
        var hedr = BethesdaSkyrimNpcFinishCoreRaw.Read(output, proposal.Manifest.Npc.FormId.Value).Tes4.Subrecords.Single(x => x.Signature == "HEDR");
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(hedr.Offset + 10), checked((uint)(census.NonTes4Records.Length + census.Groups.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(hedr.Offset + 14), proposal.NextFormId);
        return output;
    }
}
