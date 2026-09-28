using System.Buffers.Binary;
using System.Text;

namespace NpcManager.Formats.Bethesda;

public sealed record InteriorPlacementTopologyVerificationResult(
    int Tes4Count,
    int CellCount,
    int AchrCount,
    bool HasOnlyEdidCell,
    bool HasPersistentActor);

/// <summary>
/// Test-independent raw reader for the admitted interior-placement topology.
/// It intentionally does not reuse the writer's scaffold parser.
/// </summary>
public static class BethesdaSkyrimInteriorPlacementTopologyVerifier
{
    private const int HeaderSize = 24;
    private const uint PersistentFlag = 0x400;

    public static InteriorPlacementTopologyVerificationResult Verify(
        ReadOnlySpan<byte> bytes,
        uint expectedCellFormId,
        uint expectedNpcFormId,
        uint expectedBlock,
        uint expectedSubBlock,
        uint? expectedLocationFormId = null)
    {
        byte[] data = bytes.ToArray();
        List<Node> roots = ParseRange(data, 0, data.Length);
        Node tes4 = Require(roots, node => !node.IsGroup && node.Signature == "TES4", "one TES4 header");
        if (roots.Count(node => !node.IsGroup && node.Signature == "TES4") != 1)
            throw new InvalidDataException("Interior placement must contain exactly one TES4 header.");
        ValidateTes4(data, tes4);

        Node cellGroup = Require(roots, node => node.IsGroup && node.Type == 0 && node.Label == 0x4C4C4543, "CELL group");
        Node block = Require(cellGroup.Children, node => node.IsGroup && node.Type == 2 && node.Label == expectedBlock, "interior block");
        Node subBlock = Require(block.Children, node => node.IsGroup && node.Type == 3 && node.Label == expectedSubBlock, "interior sub-block");
        Node cell = Require(subBlock.Children, node => !node.IsGroup && node.Signature == "CELL" && node.FormId == expectedCellFormId, "selected CELL");
        ValidateCell(data, cell);
        Node children = Require(subBlock.Children, node => node.IsGroup && node.Type == 6 && node.Label == expectedCellFormId, "cell children group");
        Node persistent = Require(children.Children, node => node.IsGroup && node.Type == 8 && node.Label == expectedCellFormId, "persistent children group");
        Node actor = Require(persistent.Children, node => !node.IsGroup && node.Signature == "ACHR" && (node.FormId & 0x00FF_FFFFu) == 0x800, "persistent ACHR");
        ValidateActor(data, actor, expectedNpcFormId, expectedLocationFormId);

        Node[] all = Flatten(roots).ToArray();
        int tes4Count = all.Count(node => !node.IsGroup && node.Signature == "TES4");
        int cellCount = all.Count(node => !node.IsGroup && node.Signature == "CELL");
        int achrCount = all.Count(node => !node.IsGroup && node.Signature == "ACHR");
        if (cellCount != 1 || achrCount != 1 || all.Any(node => !node.IsGroup && node.Signature is not ("TES4" or "CELL" or "ACHR")))
            throw new InvalidDataException("Interior placement contains an unadmitted record signature or duplicate major record.");
        if (roots.Any(node => node.IsGroup && node.Type != 0) || cellGroup.Children.Any(node => node.IsGroup && node.Type != 2) || block.Children.Any(node => node.IsGroup && node.Type != 3))
            throw new InvalidDataException("Interior placement contains an unadmitted top-level group.");

        return new(tes4Count, cellCount, achrCount, HasOnlyEdid(data, cell), HasPersistent(actor));
    }

    private static bool HasOnlyEdid(byte[] bytes, Node cell) =>
        ReadSubrecords(bytes, cell).Select(row => row.Signature).SequenceEqual(["EDID"]);

    private static bool HasPersistent(Node actor) => (actor.Flags & PersistentFlag) == PersistentFlag;

    private static void ValidateTes4(byte[] bytes, Node tes4)
    {
        if ((tes4.Flags & 0x200) == 0 || tes4.FormVersion != 44)
            throw new InvalidDataException("TES4 does not carry the admitted ESL/FormVersion header.");
        Subrecord? hedr = ReadSubrecords(bytes, tes4).SingleOrDefault(row => row.Signature == "HEDR");
        if (hedr is null || hedr.Payload.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(hedr.Payload.AsSpan(4, 4)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(hedr.Payload.AsSpan(8, 4)) != 0x801)
            throw new InvalidDataException("TES4 HEDR count/NextObjectID do not match the fixed contract.");
    }

    private static void ValidateCell(byte[] bytes, Node cell)
    {
        if (cell.Flags != 0 || !ReadSubrecords(bytes, cell).Select(row => row.Signature).SequenceEqual(["EDID"]))
            throw new InvalidDataException("CELL must contain exactly one EDID and no DATA/LTMP payload.");
    }

    private static void ValidateActor(byte[] bytes, Node actor, uint expectedNpc, uint? expectedLocation)
    {
        if ((actor.Flags & PersistentFlag) != PersistentFlag)
            throw new InvalidDataException("ACHR is not persistent.");
        string[] expected = expectedLocation is null ? ["NAME", "DATA"] : ["NAME", "DATA", "XLCN"];
        Subrecord[] rows = ReadSubrecords(bytes, actor).ToArray();
        if (!rows.Select(row => row.Signature).SequenceEqual(expected) || rows[0].Payload.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(rows[0].Payload) != expectedNpc || rows[1].Payload.Length != 24)
            throw new InvalidDataException("ACHR NAME/DATA/XLCN shape does not match the fixed contract.");
        for (int index = 0; index < 6; index++)
            if (!float.IsFinite(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(rows[1].Payload.AsSpan(index * 4, 4)))))
                throw new InvalidDataException("ACHR DATA contains a non-finite transform.");
        if (expectedLocation is { } location && (rows[2].Payload.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(rows[2].Payload) != location))
            throw new InvalidDataException("ACHR XLCN does not match the selected location.");
    }

    private static IEnumerable<Node> Flatten(IEnumerable<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            yield return node;
            if (node.IsGroup)
                foreach (Node child in Flatten(node.Children))
                    yield return child;
        }
    }

    private static Node Require(IEnumerable<Node> nodes, Func<Node, bool> predicate, string label)
    {
        Node[] matches = nodes.Where(predicate).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Expected exactly one {label}; found {matches.Length}.");
    }

    private static List<Node> ParseRange(ReadOnlySpan<byte> bytes, int start, int end)
    {
        List<Node> result = [];
        int offset = start;
        while (offset < end)
        {
            if (end - offset < HeaderSize)
                throw new InvalidDataException("Interior placement node header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (signature == "GRUP")
            {
                if (size < HeaderSize || size > int.MaxValue || (long)offset + size > end)
                    throw new InvalidDataException("Interior placement group size is invalid.");
                Node group = new(true, signature, BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 12, 4)), 0, 0, 0, offset, (int)size, ParseRange(bytes, offset + HeaderSize, checked(offset + (int)size)));
                result.Add(group);
                offset += checked((int)size);
            }
            else
            {
                long recordEnd = (long)offset + HeaderSize + size;
                if (size > int.MaxValue || recordEnd > end)
                    throw new InvalidDataException("Interior placement record size is invalid.");
                result.Add(new(false, signature, 0, 0, BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 12, 4)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 20, 2)), offset, checked((int)(HeaderSize + size)), []));
                offset = checked((int)recordEnd);
            }
        }
        if (offset != end)
            throw new InvalidDataException("Interior placement container has trailing bytes.");
        return result;
    }

    private static List<Subrecord> ReadSubrecords(byte[] bytes, Node record)
    {
        List<Subrecord> rows = [];
        int start = record.Offset + HeaderSize;
        int end = record.Offset + record.Size;
        while (start < end)
        {
            if (end - start < 6)
                throw new InvalidDataException("Interior placement subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes.AsSpan(start, 4));
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + 4, 2));
            int payloadStart = start + 6;
            if (payloadStart + size > end)
                throw new InvalidDataException("Interior placement subrecord exceeds its record.");
            rows.Add(new(signature, bytes.AsSpan(payloadStart, size).ToArray()));
            start = payloadStart + size;
        }
        if (start != end)
            throw new InvalidDataException("Interior placement record has trailing subrecord bytes.");
        return rows;
    }

    private sealed record Node(bool IsGroup, string Signature, uint Label, uint Type, uint FormId, uint Flags, uint FormVersion, int Offset, int Size, List<Node> Children);

    private sealed record Subrecord(string Signature, byte[] Payload);
}
