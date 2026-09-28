using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace Actorwright.PublicFixtures;

public readonly record struct SyntheticTopologyLayout(
    int CellRecordOffset,
    int CellRecordSize,
    uint CellFormId,
    uint BlockLabel,
    uint SubBlockLabel,
    int CellChildrenGroupOffset,
    int CellChildrenGroupSize,
    uint CellChildrenLabel);

public static class SyntheticSkyrimPluginTopology
{
    public const string RepairedFileName = "synthetic-v1-repaired-cell-anchor.esp";
    public const string OrphanFileName = "synthetic-v1-orphan-cell-children.esp";
    public const uint BaseNpcLocalId = 0x00000800;
    public const uint CellLocalId = 0x00000901;
    public const uint PlacedReferenceLocalId = 0x00000909;
    private const uint CellGroupLabel = 0x4C4C4543; // "CELL" in little-endian GRUP labels.

    public static (string RepairedPath, string OrphanPath) Generate(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        string repairedPath = Path.Combine(root, RepairedFileName);
        string orphanPath = Path.Combine(root, OrphanFileName);

        var modKey = ModKey.FromNameAndExtension(RepairedFileName);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE)
        {
            IsSmallMaster = false
        };
        mod.ModHeader.Stats.NextFormID = PlacedReferenceLocalId + 1;
        var npc = new Npc(new FormKey(modKey, BaseNpcLocalId), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SyntheticTopologyBaseNpc",
            Name = "Synthetic topology base"
        };
        mod.Npcs.Add(npc);
        var cell = new Cell(new FormKey(modKey, CellLocalId), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SyntheticTopologyCell",
            Flags = Cell.Flag.IsInteriorCell
        };
        cell.Persistent.Add(new PlacedNpc(
            new FormKey(modKey, PlacedReferenceLocalId), SkyrimRelease.SkyrimSE)
        {
            Base = new FormLinkNullable<INpcGetter>(npc.FormKey),
            Placement = new Placement
            {
                Position = new P3Float(1, 2, 3),
                Rotation = new P3Float(0.1f, 0.2f, 0.3f)
            },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x00000400
        });
        mod.Cells.AddInteriorCell(cell);
        mod.WriteToBinary(new FilePath(repairedPath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });

        byte[] repaired = File.ReadAllBytes(repairedPath);
        SyntheticTopologyLayout layout = LocateTopology(repaired);
        if (layout.CellChildrenLabel != layout.CellFormId)
            throw new InvalidDataException("Mutagen emitted a cell-children label that does not match its CELL record.");
        byte[] orphan = MoveCellChildrenBeforeCell(repaired);
        File.WriteAllBytes(orphanPath, orphan);
        return (repairedPath, orphanPath);
    }

    public static SyntheticTopologyLayout LocateTopology(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("Synthetic topology plugin has no TES4 header.");
        uint tes4BodySize = ReadUInt32(bytes, 4);
        int firstGroup = checked(24 + (int)tes4BodySize);
        if (firstGroup > bytes.Length)
            throw new InvalidDataException("Synthetic topology TES4 header exceeds the file.");

        Node cellRoot = ReadNodes(bytes, firstGroup, bytes.Length)
            .Single(node => node.IsGroup && node.Type == 0 && node.Label == CellGroupLabel);
        Node block = ReadNodes(bytes, cellRoot.Offset + 24, cellRoot.End)
            .Single(node => node.IsGroup && node.Type == 2);
        Node subBlock = ReadNodes(bytes, block.Offset + 24, block.End)
            .Single(node => node.IsGroup && node.Type == 3);
        List<Node> siblings = ReadNodes(bytes, subBlock.Offset + 24, subBlock.End);
        if (siblings.Count != 2)
            throw new InvalidDataException("Synthetic topology sub-block must contain only CELL and type-6 nodes.");
        Node cellRecord = siblings.Single(node => !node.IsGroup && node.Signature == "CELL");
        Node children = siblings.Single(node => node.IsGroup && node.Type == 6);
        return new SyntheticTopologyLayout(
            cellRecord.Offset,
            cellRecord.Size,
            ReadUInt32(bytes, cellRecord.Offset + 12),
            block.Label,
            subBlock.Label,
            children.Offset,
            children.Size,
            children.Label);
    }

    private static byte[] MoveCellChildrenBeforeCell(byte[] repaired)
    {
        SyntheticTopologyLayout layout = LocateTopology(repaired);
        if (layout.CellRecordOffset + layout.CellRecordSize != layout.CellChildrenGroupOffset)
            throw new InvalidDataException("Synthetic topology CELL and type-6 group are not adjacent.");

        var orphan = new byte[repaired.Length];
        int prefix = layout.CellRecordOffset;
        repaired.AsSpan(0, prefix).CopyTo(orphan);
        repaired.AsSpan(layout.CellChildrenGroupOffset, layout.CellChildrenGroupSize)
            .CopyTo(orphan.AsSpan(prefix));
        int cellAfterGroup = checked(prefix + layout.CellChildrenGroupSize);
        repaired.AsSpan(layout.CellRecordOffset, layout.CellRecordSize)
            .CopyTo(orphan.AsSpan(cellAfterGroup));
        int suffixStart = checked(layout.CellChildrenGroupOffset + layout.CellChildrenGroupSize);
        int suffixDestination = checked(cellAfterGroup + layout.CellRecordSize);
        repaired.AsSpan(suffixStart).CopyTo(orphan.AsSpan(suffixDestination));
        return orphan;
    }

    private static List<Node> ReadNodes(byte[] bytes, int start, int end)
    {
        var nodes = new List<Node>();
        int offset = start;
        while (offset < end)
        {
            if (end - offset < 24)
                throw new InvalidDataException("Synthetic topology node header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes, offset, 4);
            uint bodySize = ReadUInt32(bytes, offset + 4);
            int size = signature == "GRUP"
                ? checked((int)bodySize)
                : checked(24 + (int)bodySize);
            if (size < 24 || size > end - offset)
                throw new InvalidDataException("Synthetic topology node exceeds its parent.");
            bool isGroup = signature == "GRUP";
            nodes.Add(new Node(
                offset,
                size,
                isGroup,
                signature,
                isGroup ? ReadUInt32(bytes, offset + 12) : 0,
                isGroup ? ReadUInt32(bytes, offset + 8) : 0));
            offset = checked(offset + size);
        }
        if (offset != end)
            throw new InvalidDataException("Synthetic topology parent ended off a node boundary.");
        return nodes;
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));

    private sealed record Node(
        int Offset,
        int Size,
        bool IsGroup,
        string Signature,
        uint Type,
        uint Label)
    {
        internal int End => checked(Offset + Size);
    }
}