using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record InteriorPlacementTopologyTransform(
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ);

public sealed record InteriorPlacementTopologyInput(
    PluginName Patch,
    ImmutableArray<PluginName> Masters,
    uint RawCellFormId,
    string CellEditorId,
    uint InteriorBlock,
    uint InteriorSubBlock,
    uint RawNpcFormId,
    InteriorPlacementTopologyTransform Transform,
    uint? RawLocationFormId);

/// <summary>
/// Produces the smallest supported interior placement topology.  Mutagen is
/// used for the initial record scaffold; the post-pass is deliberately raw and
/// bounded because CELL DATA/LTMP are not part of the admitted placement
/// surface.
/// </summary>
public sealed class BethesdaSkyrimInteriorPlacementTopologyWriter
{
    private const ushort RecordFormVersion = 44;
    private const uint SmallMasterFlag = 0x0000_0200;
    private const uint PersistentFlag = 0x0000_0400;
    private const uint NextFormId = 0x0000_0801;
    private const uint ActorFormId = 0x0000_0800;
    private const int RecordHeaderSize = 24;
    private const int GroupHeaderSize = 24;
    private const int SubrecordHeaderSize = 6;
    private static readonly ModKey Skyrim =
        ModKey.FromNameAndExtension("Skyrim.esm");

    public byte[] Write(InteriorPlacementTopologyInput input)
    {
        _ = GetType();
        ValidateInput(input);

        string scratchRoot = ActorwrightWorkspace.WorkRoot(
            ActorwrightWorkspace.ResolveRoot(),
            "interior-placement").Value;
        Directory.CreateDirectory(scratchRoot);
        string scratch = Path.Combine(
            scratchRoot,
            "npcmanager-preview227-" + Guid.NewGuid().ToString("N") + ".esl");
        try
        {
            BuildMutagenScaffold(input, scratch);
            byte[] scaffold = File.ReadAllBytes(scratch);
            return SanitizeScaffold(scaffold, input);
        }
        finally
        {
            try
            {
                if (File.Exists(scratch)) File.Delete(scratch);
            }
            catch (IOException)
            {
                // The caller receives no path to the ephemeral scaffold.  A
                // locked scratch file is not allowed to change the result.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IOException boundary above.
            }
        }
    }

    private static void BuildMutagenScaffold(
        InteriorPlacementTopologyInput input,
        string destination)
    {
        ModKey patch = ModKey.FromNameAndExtension(input.Patch.Value);
        var output = new SkyrimMod(
            patch,
            SkyrimRelease.SkyrimSE,
            headerVersion: 1.7f,
            forceUseLowerFormIDRanges: false)
        {
            IsSmallMaster = true
        };
        ((IMod)output).NextFormID = NextFormId;
        foreach (PluginName master in input.Masters)
        {
            output.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master.Value)
            });
        }

        FormKey cellKey = ToFormKey(input.RawCellFormId, input.Masters);
        var cell = new Cell(cellKey, SkyrimRelease.SkyrimSE)
        {
            // IsInteriorCell causes Mutagen to emit a DATA row.  A temporary
            // lighting link causes it to emit LTMP.  Both are removed by the
            // bounded raw sanitizer below.
            Flags = Cell.Flag.IsInteriorCell,
            EditorID = input.CellEditorId,
            LightingTemplate = new FormLink<ILightingTemplateGetter>(
                new FormKey(Skyrim, 0x0000_0800)),
            FormVersion = RecordFormVersion
        };
        var actor = new PlacedNpc(
            new FormKey(patch, ActorFormId),
            SkyrimRelease.SkyrimSE)
        {
            Base = new FormLinkNullable<INpcGetter>(
                ToFormKey(input.RawNpcFormId, input.Masters)),
            Placement = new Placement
            {
                Position = new P3Float(input.Transform.X, input.Transform.Y, input.Transform.Z),
                Rotation = new P3Float(
                    input.Transform.RotationX,
                    input.Transform.RotationY,
                    input.Transform.RotationZ)
            },
            SkyrimMajorRecordFlags =
                (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
            FormVersion = RecordFormVersion
        };
        if (input.RawLocationFormId is { } location)
        {
            actor.LocationReference = new FormLinkNullable<ILocationGetter>(
                ToFormKey(location, input.Masters));
        }
        cell.Persistent.Add(actor);
        output.Cells.AddInteriorCell(cell);
        output.WriteToBinary(new FilePath(destination), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static byte[] SanitizeScaffold(
        byte[] scaffold,
        InteriorPlacementTopologyInput input)
    {
        if (scaffold.Length < RecordHeaderSize ||
            !scaffold.AsSpan(0, 4).SequenceEqual("TES4"u8))
            Refuse("interior-placement-scaffold-header", "Mutagen did not emit a TES4 header.");

        int tes4BodyLength = CheckedLength(ReadUInt32(scaffold, 4));
        int firstGroup = checked(RecordHeaderSize + tes4BodyLength);
        if (firstGroup > scaffold.Length)
            Refuse("interior-placement-scaffold-bounds", "TES4 exceeds the scaffold file.");
        CanonicalizeHedrRecordCount(scaffold, tes4BodyLength);
        ValidateHeader(scaffold, tes4BodyLength, input);

        var tree = ParseTopLevel(scaffold, firstGroup);
        RawGroup[] allGroups = tree.OfType<RawGroup>().ToArray();
        RawRecord[] allRecords = tree.OfType<RawRecord>().ToArray();
        if (allGroups.Length != 5 ||
            allGroups.Select(group => group.Type).OrderBy(type => type)
                .SequenceEqual([0u, 2u, 3u, 6u, 8u]) is false ||
            allRecords.Length != 2 ||
            allRecords.Count(record => record.Signature == "CELL") != 1 ||
            allRecords.Count(record => record.Signature == "ACHR") != 1)
            Refuse("interior-placement-scaffold-census",
                "Mutagen emitted records or groups outside the fixed topology scaffold.");
        RawGroup cellGroup = RequireSingleGroup(tree, 0, "CELL");
        RawGroup block = RequireSingleChildGroup(tree, cellGroup, 2, null);
        RawGroup subBlock = RequireSingleChildGroup(tree, block, 3, null);
        RawRecord cell = RequireSingleRecord(tree, subBlock, "CELL");
        if (cell.FormId != input.RawCellFormId || cell.Flags != 0)
            Refuse("interior-placement-cell-identity", "Mutagen changed the selected CELL identity or flags.");
        if (ReadSubrecords(cell.Body).Select(row => row.Signature)
                .SequenceEqual(["EDID", "DATA", "LTMP"]) is false)
            Refuse("interior-placement-cell-scaffold", "CELL scaffold was not exactly EDID/DATA/LTMP.");

        RawGroup children = RequireSingleChildGroup(tree, subBlock, 6, input.RawCellFormId);
        RawGroup persistent = RequireSingleChildGroup(tree, children, 8, input.RawCellFormId);
        RawRecord actor = RequireSingleRecord(tree, persistent, "ACHR");
        if ((actor.FormId & 0x00FF_FFFFu) != ActorFormId || actor.Flags != PersistentFlag)
            Refuse("interior-placement-achr-identity",
                $"Mutagen changed the fixed persistent ACHR identity (form=0x{actor.FormId:X8}, flags=0x{actor.Flags:X8}).");
        var actorFields = ReadSubrecords(actor.Body);
        string[] expectedActorFields = input.RawLocationFormId is null
            ? ["NAME", "DATA"]
            : ["NAME", "DATA", "XLCN"];
        if (!actorFields.Select(row => row.Signature).SequenceEqual(expectedActorFields))
            Refuse("interior-placement-achr-scaffold", "ACHR scaffold contains an unadmitted field.");
        if (actorFields[0].Payload.Length != sizeof(uint) ||
            ReadUInt32(actorFields[0].Payload, 0) != input.RawNpcFormId)
            Refuse("interior-placement-name-link", "Mutagen changed the raw NAME owner/index link.");
        if (actorFields[1].Payload.Length != 24 ||
            !TransformMatches(actorFields[1].Payload, input.Transform))
            Refuse("interior-placement-transform", "Mutagen changed the reviewed transform.");
        if (input.RawLocationFormId is { } location &&
            (actorFields[2].Payload.Length != sizeof(uint) ||
             ReadUInt32(actorFields[2].Payload, 0) != location))
            Refuse("interior-placement-location-link", "Mutagen changed the optional XLCN link.");

        byte[] cellBody = ReadSubrecords(cell.Body)
            .Where(row => row.Signature == "EDID")
            .SelectMany(row => EncodeSubrecord(row.Signature, row.Payload))
            .ToArray();
        if (cellBody.Length == 0)
            Refuse("interior-placement-edid", "The scaffold CELL has no EDID.");

        var output = scaffold.ToArray();
        // Replace only the CELL body, then decrement every containing group by
        // the removed DATA/LTMP bytes.  The raw tree remains otherwise intact.
        int removed = cell.Body.Length - cellBody.Length;
        if (removed <= 0)
            Refuse("interior-placement-cell-sanitizer", "The scaffold had no removable CELL payload.");
        int cellBodyOffset = cell.BodyOffset;
        Buffer.BlockCopy(cellBody, 0, output, cellBodyOffset, cellBody.Length);
        int tailStart = checked(cellBodyOffset + cell.Body.Length);
        Buffer.BlockCopy(
            scaffold,
            tailStart,
            output,
            checked(cellBodyOffset + cellBody.Length),
            scaffold.Length - tailStart);
        Array.Resize(ref output, checked(output.Length - removed));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(cell.Offset + 4, sizeof(uint)), checked((uint)cellBody.Length));
        foreach (RawGroup ancestor in cell.Ancestors)
        {
            uint size = ReadUInt32(output, ancestor.Offset + 4);
            if (size < removed + GroupHeaderSize)
                Refuse("interior-placement-group-underflow", "A group size underflowed during CELL sanitization.");
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(ancestor.Offset + 4, sizeof(uint)), size - checked((uint)removed));
        }

        // The scaffold's block labels are derived from the cell key.  The
        // provider-derived labels are part of the proposal and are therefore
        // written only after the scaffold shape has been independently checked.
        WriteUInt32(output, block.Offset + 8, input.InteriorBlock);
        WriteUInt32(output, subBlock.Offset + 8, input.InteriorSubBlock);
        WriteUInt32(output, ShiftedOffset(children, cellBodyOffset, cell.Body.Length, removed) + 8,
            input.RawCellFormId);
        WriteUInt32(output, ShiftedOffset(persistent, cellBodyOffset, cell.Body.Length, removed) + 8,
            input.RawCellFormId);
        return output;
    }

    private static int ShiftedOffset(
        RawNode node,
        int removedStart,
        int removedLength,
        int removed) =>
        node.Offset >= removedStart + removedLength
            ? checked(node.Offset - removed)
            : node.Offset;

    private static void ValidateInput(InteriorPlacementTopologyInput input)
    {
        if (!input.Patch.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) &&
            !input.Patch.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            Refuse("interior-placement-patch-type", "The topology patch must be a light .esp or .esl plugin.");
        if (input.Masters.IsDefaultOrEmpty || input.Masters.Length > byte.MaxValue)
            Refuse("interior-placement-masters", "The topology patch requires 1..255 masters.");
        if (input.Masters.Any(master => master == input.Patch) ||
            input.Masters.Distinct().Count() != input.Masters.Length)
            Refuse("interior-placement-master-order", "Masters must be unique and cannot contain the patch.");
        if (string.IsNullOrWhiteSpace(input.CellEditorId) ||
            input.CellEditorId.Any(ch => ch == '\0' || ch > 0x7F) ||
            Encoding.ASCII.GetByteCount(input.CellEditorId) > ushort.MaxValue - 1)
            Refuse("interior-placement-edid-input", "CELL EDID must be non-empty bounded ASCII.");
        if (input.RawCellFormId == 0 || input.RawNpcFormId == 0)
            Refuse("interior-placement-formid", "CELL and NPC FormIDs must be non-zero.");
        ValidateMasterIndex(input.RawCellFormId, input.Masters, "cell");
        ValidateMasterIndex(input.RawNpcFormId, input.Masters, "npc");
        if (input.RawLocationFormId is { } location)
            ValidateMasterIndex(location, input.Masters, "location");
        if (input.InteriorBlock == uint.MaxValue || input.InteriorSubBlock == uint.MaxValue)
            Refuse("interior-placement-group-label", "Interior block labels cannot be sentinel values.");
        float[] transform =
        [
            input.Transform.X, input.Transform.Y, input.Transform.Z,
            input.Transform.RotationX, input.Transform.RotationY, input.Transform.RotationZ
        ];
        if (transform.Any(value => !float.IsFinite(value)))
            Refuse("interior-placement-transform-input", "All transform values must be finite float32 values.");
    }

    private static void ValidateMasterIndex(
        uint rawFormId,
        ImmutableArray<PluginName> masters,
        string field)
    {
        byte high = checked((byte)(rawFormId >> 24));
        if (high == 0xFE)
        {
            // A compact light FormID cannot be mapped to a master-list ordinal
            // without the light-load-order map.  One and only one light master
            // is admitted by this generic spike; the provider-bound service
            // supplies the real relocation map later.
            if (masters.Count(master =>
                    master.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)) != 1)
                Refuse("interior-placement-light-owner-ambiguous",
                    $"The {field} compact light FormID cannot be resolved uniquely.");
            return;
        }
        if (high >= masters.Length)
            Refuse("interior-placement-owner-index",
                $"The {field} raw FormID owner index {high} is outside the declared master list.");
    }

    private static FormKey ToFormKey(
        uint rawFormId,
        ImmutableArray<PluginName> masters)
    {
        byte high = checked((byte)(rawFormId >> 24));
        if (high == 0xFE)
        {
            PluginName light = masters.Single(master =>
                master.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase));
            return new FormKey(
                ModKey.FromNameAndExtension(light.Value),
                rawFormId & 0x00FF_FFFFu);
        }
        return new FormKey(
            ModKey.FromNameAndExtension(masters[high].Value),
            rawFormId & 0x00FF_FFFFu);
    }

    private static ImmutableArray<RawNode> ParseTopLevel(
        byte[] bytes,
        int start)
    {
        var nodes = ImmutableArray.CreateBuilder<RawNode>();
        ParseRange(bytes, start, bytes.Length, [], nodes);
        return nodes.ToImmutable();
    }

    private static void ParseRange(
        byte[] bytes,
        int start,
        int end,
        ImmutableArray<RawGroup> ancestors,
        ImmutableArray<RawNode>.Builder nodes)
    {
        int position = start;
        while (position < end)
        {
            if (end - position < RecordHeaderSize)
                Refuse("interior-placement-scaffold-bounds", "A raw node header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            uint size = ReadUInt32(bytes, position + 4);
            if (signature == "GRUP")
            {
                int groupEnd = checked(position + CheckedLength(size));
                if (size < GroupHeaderSize || groupEnd > end)
                    Refuse("interior-placement-scaffold-bounds", "A raw group exceeds its parent.");
                var group = new RawGroup(
                    position,
                    ReadUInt32(bytes, position + 8),
                    ReadUInt32(bytes, position + 12),
                    ancestors);
                nodes.Add(group);
                ParseRange(bytes, position + GroupHeaderSize, groupEnd,
                    ancestors.Add(group), nodes);
                position = groupEnd;
            }
            else
            {
                int recordEnd = checked(position + RecordHeaderSize + CheckedLength(size));
                if (recordEnd > end)
                    Refuse("interior-placement-scaffold-bounds", "A raw record exceeds its parent.");
                nodes.Add(new RawRecord(
                    position,
                    signature,
                    ReadUInt32(bytes, position + 12),
                    ReadUInt32(bytes, position + 8),
                    position + RecordHeaderSize,
                    bytes.AsSpan(position + RecordHeaderSize, CheckedLength(size)).ToArray(),
                    ancestors));
                position = recordEnd;
            }
        }
        if (position != end)
            Refuse("interior-placement-scaffold-bounds", "A raw container has trailing bytes.");
    }

    private static RawGroup RequireSingleGroup(
        ImmutableArray<RawNode> nodes,
        uint type,
        string label)
    {
        RawGroup[] matches = nodes.OfType<RawGroup>()
            .Where(group => group.Type == type && group.LabelText == label)
            .ToArray();
        if (matches.Length != 1)
            Refuse("interior-placement-group-shape",
                $"Expected one {label} GRUP of type {type}, found {matches.Length}.");
        return matches[0];
    }

    private static RawGroup RequireSingleChildGroup(
        ImmutableArray<RawNode> nodes,
        RawGroup parent,
        uint type,
        uint? label)
    {
        // Group objects and records are flattened by ParseRange; ancestry gives
        // us a test-owned, independent way to select direct children.
        RawGroup[] matches = nodes
            .OfType<RawGroup>()
            .Where(group => group.Type == type &&
                            (label is null || group.Label == label.Value) &&
                            group.Ancestors.Length == parent.Ancestors.Length + 1 &&
                            group.Ancestors.LastOrDefault() == parent)
            .ToArray();
        if (matches.Length != 1)
            Refuse("interior-placement-group-shape",
                $"Expected one direct GRUP type {type}" +
                (label is null ? string.Empty : $" label 0x{label.Value:X8}") +
                $", found {matches.Length}.");
        return matches[0];
    }

    private static RawRecord RequireSingleRecord(
        ImmutableArray<RawNode> nodes,
        RawGroup parent,
        string signature)
    {
        RawRecord[] matches = nodes
            .OfType<RawRecord>()
            .Where(record => record.Signature == signature &&
                            record.Ancestors.Length == parent.Ancestors.Length + 1 &&
                            record.Ancestors.LastOrDefault() == parent)
            .ToArray();
        if (matches.Length != 1)
            Refuse("interior-placement-record-census",
                $"Expected one direct {signature} record, found {matches.Length}.");
        return matches[0];
    }

    private static void ValidateHeader(
        byte[] bytes,
        int tes4BodyLength,
        InteriorPlacementTopologyInput input)
    {
        uint flags = ReadUInt32(bytes, 8);
        if (flags != SmallMasterFlag || ReadUInt16(bytes, 20) != RecordFormVersion)
            Refuse("interior-placement-tes4-header", "TES4 flags or FormVersion are outside the fixed contract.");
        var fields = ReadSubrecords(bytes.AsSpan(RecordHeaderSize, tes4BodyLength));
        RawSubrecord hedr = fields.SingleOrDefault(field => field.Signature == "HEDR")
            ?? throw new InvalidDataException("interior-placement-tes4-hedr: HEDR is missing.");
        float headerVersion = hedr.Payload.Length >= 4
            ? BitConverter.ToSingle(hedr.Payload, 0)
            : float.NaN;
        uint headerCount = hedr.Payload.Length >= 8
            ? ReadUInt32(hedr.Payload, 4)
            : 0;
        uint headerNext = hedr.Payload.Length >= 12
            ? ReadUInt32(hedr.Payload, 8)
            : 0;
        if (hedr.Payload.Length != 12 ||
            Math.Abs(headerVersion - 1.7f) > 0.0001f ||
            headerCount != 2 ||
            headerNext != NextFormId)
            Refuse("interior-placement-tes4-hedr",
                $"HEDR version/count/NextObjectID differ from the contract (version={headerVersion}, count={headerCount}, next=0x{headerNext:X8}).");
        string[] masters = fields.Where(field => field.Signature == "MAST")
            .Select(field => Encoding.ASCII.GetString(field.Payload).TrimEnd('\0'))
            .ToArray();
        if (!masters.SequenceEqual(input.Masters.Select(master => master.Value)))
            Refuse("interior-placement-tes4-masters", "Mutagen changed the declared master order.");
    }

    private static void CanonicalizeHedrRecordCount(
        byte[] bytes,
        int tes4BodyLength)
    {
        ReadOnlySpan<byte> body = bytes.AsSpan(RecordHeaderSize, tes4BodyLength);
        int position = 0;
        while (position < body.Length)
        {
            if (body.Length - position < SubrecordHeaderSize)
                Refuse("interior-placement-tes4-hedr", "TES4 subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(body.Slice(position, 4));
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                body.Slice(position + 4, sizeof(ushort)));
            int end = checked(position + SubrecordHeaderSize + length);
            if (end > body.Length)
                Refuse("interior-placement-tes4-hedr", "TES4 subrecord exceeds the header.");
            if (signature == "HEDR")
            {
                if (length != 12)
                    Refuse("interior-placement-tes4-hedr", "HEDR does not contain 12 bytes.");
                uint scaffoldCount = ReadUInt32(bytes, RecordHeaderSize + position + 6 + 4);
                if (scaffoldCount != 7)
                    Refuse("interior-placement-tes4-hedr",
                        $"Unexpected Mutagen scaffold record count {scaffoldCount}; expected 7 before canonicalization.");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(RecordHeaderSize + position + 6 + 4, sizeof(uint)), 2);
                return;
            }
            position = end;
        }
        Refuse("interior-placement-tes4-hedr", "HEDR is missing from the scaffold.");
    }

    private static bool TransformMatches(
        byte[] payload,
        InteriorPlacementTopologyTransform expected)
    {
        float[] actual = Enumerable.Range(0, 6)
            .Select(index => BitConverter.ToSingle(payload, index * sizeof(float)))
            .ToArray();
        float[] target =
        [
            expected.X, expected.Y, expected.Z,
            expected.RotationX, expected.RotationY, expected.RotationZ
        ];
        return actual.SequenceEqual(target);
    }

    private static ImmutableArray<RawSubrecord> ReadSubrecords(ReadOnlySpan<byte> body)
    {
        var rows = ImmutableArray.CreateBuilder<RawSubrecord>();
        int position = 0;
        while (position < body.Length)
        {
            if (body.Length - position < SubrecordHeaderSize)
                Refuse("interior-placement-subrecord-bounds", "A subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(body.Slice(position, 4));
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                body.Slice(position + 4, sizeof(ushort)));
            int end = checked(position + SubrecordHeaderSize + length);
            if (end > body.Length)
                Refuse("interior-placement-subrecord-bounds", "A subrecord exceeds its record.");
            rows.Add(new RawSubrecord(
                signature,
                body.Slice(position + SubrecordHeaderSize, length).ToArray()));
            position = end;
        }
        return rows.ToImmutable();
    }

    private static byte[] EncodeSubrecord(string signature, byte[] payload)
    {
        if (signature.Length != 4 || payload.Length > ushort.MaxValue)
            throw new InvalidDataException("interior-placement-subrecord-encode: invalid subrecord.");
        byte[] output = new byte[SubrecordHeaderSize + payload.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(output, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(4, sizeof(ushort)), checked((ushort)payload.Length));
        payload.CopyTo(output, SubrecordHeaderSize);
        return output;
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));

    private static uint ReadUInt32(byte[] bytes, uint offset) =>
        ReadUInt32(bytes, checked((int)offset));

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(uint)), value);

    private static int CheckedLength(uint value) =>
        value > int.MaxValue ? throw new InvalidDataException(
            "interior-placement-bounds: a raw length exceeds the supported range.") : (int)value;

    private sealed record RawSubrecord(string Signature, byte[] Payload);

    private abstract record RawNode(int Offset, ImmutableArray<RawGroup> Ancestors);

    private sealed record RawGroup(
        int Offset,
        uint Label,
        uint Type,
        ImmutableArray<RawGroup> Ancestors)
        : RawNode(Offset, Ancestors)
    {
        internal string LabelText =>
            Type == 0 ? "CELL" : string.Empty;
    }

    private sealed record RawRecord(
        int Offset,
        string Signature,
        uint FormId,
        uint Flags,
        int BodyOffset,
        byte[] Body,
        ImmutableArray<RawGroup> Ancestors)
        : RawNode(Offset, Ancestors);

    private static void Refuse(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");
}
