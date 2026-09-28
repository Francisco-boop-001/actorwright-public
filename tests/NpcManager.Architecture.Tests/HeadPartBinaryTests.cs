using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Architecture.Tests;

internal static class HeadPartBinaryTests
{
    public static void RunOwnership()
    {
        var proposal = new RecordProposalArtifact("1", "record-proposal", "skyrimse", RecordProposalMode.New, "HDPT", "0x0000081A", null,
            "OwnedBrow", null, ["Master.esm"], "explicit-local-form-id", new string('A', 64), true,
            new HeadPartComposition("brow.nif", "race.tri", "chargen.tri", "dialogue.tri", null, [], 5, NpcHeadPartType.Eyebrows));
        byte[] source = BethesdaHeadPartWriter.Compose(null, "Synthetic.esp", proposal);
        int headerSize = 24 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4)));
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(headerSize + 24 + 12), 0x81A); // Existing override owned by Master.esm.
        byte[] output = BethesdaHeadPartWriter.Compose(source, "Synthetic.esp", proposal);
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "hdpt-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "Synthetic.esp");
        File.WriteAllBytes(path, output);
        BethesdaHeadPartWriter.Verify(path, "OwnedBrow", 0x81A);
        Refuses(() => BethesdaHeadPartWriter.Compose(output, "Synthetic.esp", proposal with
        {
            FormId = "0x0000081B", EditorId = "InvalidClone",
            HeadPart = new HeadPartComposition(null, null, null, null, null, [], 0, NpcHeadPartType.Misc, "Synthetic.esp|0x0100081A")
        }), "Clone accepted upper owner bits in a local FormRef.");
        Check(Records(output).Where(item => Encoding.ASCII.GetString(item, 0, 4) == "HDPT")
            .Select(item => BinaryPrimitives.ReadUInt32LittleEndian(item.AsSpan(12))).SequenceEqual([0x81AU, 0x0100081AU]), "Master override and output-owned identities were conflated.");
    }

    public static void Run()
    {
        var part = new HeadPartComposition("meshes/brow.nif", "meshes/race.tri", "meshes/chargen.tri", "meshes/dialogue.tri", null, [], 5, NpcHeadPartType.Eyebrows);
        var proposal = new RecordProposalArtifact("1", "record-proposal", "skyrimse", RecordProposalMode.New, "HDPT", "0x0000081A", null,
            "SyntheticBrow", "Synthetic brow", [], "explicit-local-form-id", new string('A', 64), true, part);
        byte[] first = BethesdaHeadPartWriter.Compose(null, "Synthetic.esp", proposal);
        byte[] record = Records(first).Single(item => Encoding.ASCII.GetString(item, 0, 4) == "HDPT");
        var fields = Fields(record);
        Check(fields.Select(item => item.Name).SequenceEqual(["EDID", "FULL", "MODL", "MODT", "DATA", "PNAM", "NAM0", "NAM1", "NAM0", "NAM1", "NAM0", "NAM1", "TNAM"]),
            "New HDPT binary field layout differs from Skyrim's model/three-role format.");
        Check(fields.Single(item => item.Name == "DATA").Data.SequenceEqual([(byte)5]) &&
            fields.Where(item => item.Name == "NAM0").Select(item => BinaryPrimitives.ReadUInt32LittleEndian(item.Data)).SequenceEqual([0U, 1U, 2U]),
            "HDPT flags or NAM0 role IDs differ from the format.");
        Check(Text(fields.Single(item => item.Name == "MODL").Data) == "brow.nif" &&
            fields.Where(item => item.Name == "NAM1").Select(item => Text(item.Data))
                .SequenceEqual(["race.tri", "dialogue.tri", "chargen.tri"]),
            "HDPT persisted a visible meshes/ prefix instead of a Meshes-relative model path.");
        var secondProposal = proposal with { FormId = "0x0000081B", EditorId = "SecondBrow", HeadPart = part with { ExtraParts = ["Synthetic.esp|0x81A"] } };
        byte[] second = BethesdaHeadPartWriter.Compose(first, "Synthetic.esp", secondProposal);
        var records = Records(second).Where(item => Encoding.ASCII.GetString(item, 0, 4) == "HDPT").ToArray();
        Check(records.Length == 2 && records[0].SequenceEqual(record) &&
            BinaryPrimitives.ReadUInt32LittleEndian(Fields(records[1]).Single(item => item.Name == "HNAM").Data) == 0x81A,
            "Append changed the original HDPT or misencoded output-owned HNAM.");
        Check(Fields(records[1]).Select(item => item.Name).SequenceEqual(
                ["EDID", "FULL", "MODL", "MODT", "DATA", "PNAM", "HNAM", "NAM0", "NAM1", "NAM0", "NAM1", "NAM0", "NAM1", "TNAM"]),
            "Output-owned HDPT fields are not in Creation Kit order.");
        Check(GroupCount(second, "HDPT") == 1,
            "Appending an HDPT created a second top-level group instead of extending the existing group.");
        Refuses(() => BethesdaHeadPartWriter.Compose(first, "Synthetic.esp", proposal), "Occupied local allocation was admitted.");
        Refuses(() => BethesdaHeadPartWriter.Compose(first, "Synthetic.esp", secondProposal with { HeadPart = part with { ValidRaces = "Synthetic.esp|0x81A" } }), "An HDPT was admitted as a ValidRaces FLST.");
        Refuses(() => BethesdaHeadPartWriter.Compose(null, "Synthetic.esp", proposal with { HeadPart = part with { Model = "../outside.nif" } }), "A traversal model path was admitted.");
        Refuses(() => BethesdaHeadPartWriter.Compose(null, "Synthetic.esp", proposal with { HeadPart = part with { Flags = 128 } }), "Undefined HDPT flags were admitted.");
        Refuses(() => BethesdaHeadPartWriter.Compose(null, "Synthetic.esp", proposal with { HeadPart = part with { Model = "bad\0path.nif" } }), "An embedded-NUL model path was admitted.");
        Console.WriteLine("PASS output-owned HDPT binary format, HNAM, append preservation and refusal gates");
    }

    private static List<byte[]> Records(byte[] plugin)
    {
        var records = new List<byte[]>();
        void Walk(int start, int end)
        {
            while (start < end)
            {
                bool group = plugin.AsSpan(start, 4).SequenceEqual("GRUP"u8);
                int next = start + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(plugin.AsSpan(start + 4))) + (group ? 0 : 24);
                if (group) Walk(start + 24, next); else records.Add(plugin[start..next]);
                start = next;
            }
        }
        Walk(0, plugin.Length); return records;
    }

    private static int GroupCount(byte[] plugin, string signature)
    {
        int count = 0;
        for (int cursor = 0; cursor < plugin.Length;)
        {
            bool group = plugin.AsSpan(cursor, 4).SequenceEqual("GRUP"u8);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(plugin.AsSpan(cursor + 4))) +
                (group ? 0 : 24);
            if (group && Encoding.ASCII.GetString(plugin, cursor + 8, 4) == signature) count++;
            cursor += length;
        }
        return count;
    }

    private static List<(string Name, byte[] Data)> Fields(byte[] record)
    {
        var fields = new List<(string, byte[])>();
        for (int cursor = 24; cursor < record.Length;)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(cursor + 4));
            fields.Add((Encoding.ASCII.GetString(record, cursor, 4), record[(cursor + 6)..(cursor + 6 + size)])); cursor += 6 + size;
        }
        return fields;
    }
    private static void Refuses(Action action, string message)
    {
        try { action(); } catch (Exception exception) when (exception is InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static string Text(byte[] value) => Encoding.UTF8.GetString(value).TrimEnd('\0');
    private static void Check(bool accepted, string message) { if (!accepted) throw new InvalidOperationException(message); }
}
