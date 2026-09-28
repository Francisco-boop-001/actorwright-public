using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Actorwright.PublicFixtures;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimInteriorPlacementFormatSpike()
    {
        var cases = new[]
        {
            CreateTopologyCase(
                "SkyrimOwner",
                ["Skyrim.esm", "CoreXY.esm"],
                0x00001485,
                0x01000800),
            CreateTopologyCase(
                "OrdinaryOwner",
                ["Skyrim.esm", "OrdinaryCellOwner.esp", "CoreNpc.esp"],
                0x01001234,
                0x02000800),
            CreateTopologyCase(
                "LightOwner",
                ["Skyrim.esm", "OrdinaryCellOwner.esp", "LightCellOwner.esl", "CoreNpc.esp"],
                0x02000D67,
                0x03000800)
        };

        string root = ActorwrightWorkspace.WorkRoot(
            ActorwrightWorkspace.ResolveRoot(),
            "preview227-format-spike-" + Guid.NewGuid().ToString("N")).Value;
        Directory.CreateDirectory(root);
        byte[]? baselineBytes = null;
        InteriorPlacementTopologyInput? baselineInput = null;
        TopologyCase? baselineCase = null;
        try
        {
            foreach (TopologyCase testCase in cases)
            {
                var input = new InteriorPlacementTopologyInput(
                    new PluginName($"{testCase.Name}.esl"),
                    testCase.Masters.Select(name => new PluginName(name)).ToImmutableArray(),
                    testCase.RawCellFormId,
                    $"{testCase.Name}_Cell",
                    0x00000010,
                    0x00000020,
                    testCase.RawNpcFormId,
                    new InteriorPlacementTopologyTransform(
                        1.25f, -2.5f, 3.75f, 0.125f, -0.25f, 0.5f),
                    null);
                byte[] bytes = new BethesdaSkyrimInteriorPlacementTopologyWriter()
                    .Write(input);
                byte[] repeat = new BethesdaSkyrimInteriorPlacementTopologyWriter()
                    .Write(input);
                Assert(bytes.AsSpan().SequenceEqual(repeat),
                    $"{testCase.Name}: repeated topology writes were not byte-identical.");
                Console.WriteLine(
                    $"{testCase.Name} SHA256 {Convert.ToHexString(SHA256.HashData(bytes))}");
                if (baselineBytes is null)
                {
                    baselineBytes = bytes;
                    baselineInput = input;
                    baselineCase = testCase;
                }
                Assert(bytes.Length > 0,
                    $"{testCase.Name}: writer returned no bytes.");
                ValidateRawOutput(bytes, input, testCase);

                string outputPath = Path.Combine(root, input.Patch.Value);
                File.WriteAllBytes(outputPath, bytes);
                BethesdaSkyrimRawTopologyPreflight.Validate(
                    outputPath,
                    CancellationToken.None);

                SkyrimMod overlay = SkyrimMod.CreateFromBinary(
                    new ModPath(
                        ModKey.FromNameAndExtension(input.Patch.Value),
                        new FilePath(outputPath)),
                    SkyrimRelease.SkyrimSE);
                Assert(overlay.IsSmallMaster,
                    $"{testCase.Name}: Mutagen did not reopen the ESL flag.");
                Assert(overlay.ModHeader.Stats.NextFormID == 0x801,
                    $"{testCase.Name}: NextObjectID changed.");
                Assert(overlay.ModHeader.MasterReferences
                           .Select(master => master.Master.FileName.String)
                           .SequenceEqual(testCase.Masters),
                    $"{testCase.Name}: master order changed.");

                ICellGetter cell = overlay.Cells
                    .SelectMany(block => block.SubBlocks)
                    .SelectMany(subBlock => subBlock.Cells)
                    .Single();
                Assert(cell.FormKey.ID == (input.RawCellFormId & 0x00FF_FFFFu),
                    $"{testCase.Name}: typed CELL local FormID changed.");
                Assert(cell.FormKey.ModKey.FileName.String == testCase.CellOwner,
                    $"{testCase.Name}: typed CELL owner index was not resolved.");
                Assert(cell.EditorID == input.CellEditorId,
                    $"{testCase.Name}: typed CELL EDID changed.");
                IPlacedNpcGetter actor = cell.Persistent
                    .OfType<IPlacedNpcGetter>()
                    .Single();
                Assert(actor.FormKey.ModKey == overlay.ModKey &&
                       actor.FormKey.ID == 0x800,
                    $"{testCase.Name}: typed ACHR identity changed (actual={actor.FormKey}, overlay={overlay.ModKey}).");
                Assert(actor.Base.FormKey.ModKey.FileName.String == testCase.NpcOwner &&
                       actor.Base.FormKey.ID == (input.RawNpcFormId & 0x00FF_FFFFu),
                    $"{testCase.Name}: typed ACHR NAME owner index was not resolved.");
                Assert(TransformMatches(actor.Placement!, input.Transform),
                    $"{testCase.Name}: typed transform changed.");
            }

            Assert(baselineBytes is not null && baselineInput is not null && baselineCase is not null,
                "The three-owner spike did not retain a baseline output.");
            TestSyntheticTopologyFixtures(root);
            TestHostileMutations(baselineBytes!, baselineInput!, baselineCase!);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }

    private static void TestSyntheticTopologyFixtures(string temporaryRoot)
    {
        string fixtureRoot = Path.Combine(
            ActorwrightWorkspace.ResolveRoot().Value, "tests", "fixtures", "skyrim-plugin-topology");
        string repaired = Path.Combine(fixtureRoot, SyntheticSkyrimPluginTopology.RepairedFileName);
        string broken = Path.Combine(fixtureRoot, SyntheticSkyrimPluginTopology.OrphanFileName);
        string generatedRoot = Path.Combine(temporaryRoot, "generated-topology-fixtures");
        (string generatedRepaired, string generatedBroken) =
            SyntheticSkyrimPluginTopology.Generate(generatedRoot);
        Assert(File.ReadAllBytes(generatedRepaired).AsSpan()
                   .SequenceEqual(File.ReadAllBytes(repaired)),
            "The committed repaired topology fixture differs from its generator output.");
        Assert(File.ReadAllBytes(generatedBroken).AsSpan()
                   .SequenceEqual(File.ReadAllBytes(broken)),
            "The committed orphan topology fixture differs from its generator output.");

        byte[] repairedBytes = File.ReadAllBytes(repaired);
        SyntheticTopologyLayout layout = SyntheticSkyrimPluginTopology.LocateTopology(repairedBytes);
        SyntheticTopologyLayout orphanLayout = SyntheticSkyrimPluginTopology.LocateTopology(
            File.ReadAllBytes(broken));
        BethesdaSkyrimRawTopologyPreflight.Validate(repaired, CancellationToken.None);
        PluginReadDiagnosticException? orphan = null;
        try
        {
            BethesdaSkyrimRawTopologyPreflight.Validate(broken, CancellationToken.None);
        }
        catch (PluginReadDiagnosticException exception)
        {
            orphan = exception;
        }
        Assert(orphan is not null && orphan.Code == "orphan-cell-children" &&
               orphan.Message.Contains($"0x{orphanLayout.CellChildrenGroupOffset:X8}", StringComparison.Ordinal) &&
               orphan.Message.Contains($"0x{layout.CellFormId:X8}", StringComparison.Ordinal) &&
               orphan.Message.Contains("type=6", StringComparison.Ordinal),
            "The synthetic orphan-cell fixture lost its coded topology failure.");
    }

    private static void TestHostileMutations(
        byte[] baseline,
        InteriorPlacementTopologyInput input,
        TopologyCase testCase)
    {
        AssertRawMutationRejected("removed CELL", baseline, input, testCase,
            bytes =>
            {
                var (records, _) = ParsePlacementNodes(bytes);
                PlacementRawRecord cell = records.Single(record => record.Signature == "CELL");
                "ARMO"u8.CopyTo(bytes.AsSpan(cell.Offset, 4));
                return bytes;
            });
        AssertRawMutationRejected("duplicate CELL", baseline, input, testCase,
            bytes =>
            {
                var (records, groups) = ParsePlacementNodes(bytes);
                PlacementRawRecord cell = records.Single(record => record.Signature == "CELL");
                RawGroup subBlock = groups.Single(group => group.Type == 3);
                return AppendToGroup(bytes, subBlock, RecordBytes(bytes, cell), groups);
            });
        AssertRawMutationRejected("wrong type-2 label", baseline, input, testCase,
            bytes => MutateGroupLabel(bytes, 2, input.InteriorBlock ^ 1));
        AssertRawMutationRejected("wrong type-3 label", baseline, input, testCase,
            bytes => MutateGroupLabel(bytes, 3, input.InteriorSubBlock ^ 1));
        AssertRawMutationRejected("wrong type-6 label", baseline, input, testCase,
            bytes => MutateGroupLabel(bytes, 6, input.RawCellFormId ^ 1));
        AssertRawMutationRejected("wrong type-8 label", baseline, input, testCase,
            bytes => MutateGroupLabel(bytes, 8, input.RawCellFormId ^ 1));
        AssertRawMutationRejected("wrong group size", baseline, input, testCase,
            bytes =>
            {
                var (_, groups) = ParsePlacementNodes(bytes);
                RawGroup subBlock = groups.Single(group => group.Type == 3);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(subBlock.Offset + 4, 4), subBlock.Size + 1);
                return bytes;
            });
        AssertRawMutationRejected("missing EDID", baseline, input, testCase,
            bytes =>
            {
                var (records, _) = ParsePlacementNodes(bytes);
                PlacementRawRecord cell = records.Single(record => record.Signature == "CELL");
                int offset = FindSubrecordHeader(bytes, cell, "EDID");
                "DATA"u8.CopyTo(bytes.AsSpan(offset, 4));
                return bytes;
            });
        AssertRawMutationRejected("duplicate EDID", baseline, input, testCase,
            bytes =>
            {
                var (records, groups) = ParsePlacementNodes(bytes);
                PlacementRawRecord cell = records.Single(record => record.Signature == "CELL");
                byte[] edid = ReadSubrecordBytes(bytes, cell, "EDID");
                return AppendToRecord(bytes, cell, edid, groups);
            });
        AssertRawMutationRejected("injected DATA", baseline, input, testCase,
            bytes =>
            {
                var (records, groups) = ParsePlacementNodes(bytes);
                PlacementRawRecord cell = records.Single(record => record.Signature == "CELL");
                return AppendToRecord(bytes, cell, EncodeSubrecord("DATA", [0, 0, 0, 0]), groups);
            });
        AssertRawMutationRejected("injected LTMP", baseline, input, testCase,
            bytes =>
            {
                var (records, groups) = ParsePlacementNodes(bytes);
                PlacementRawRecord cell = records.Single(record => record.Signature == "CELL");
                return AppendToRecord(bytes, cell, EncodeSubrecord("LTMP", [0, 0, 0, 0]), groups);
            });
        AssertRawMutationRejected("wrong NAME owner", baseline, input, testCase,
            bytes =>
            {
                var (records, _) = ParsePlacementNodes(bytes);
                PlacementRawRecord actor = records.Single(record => record.Signature == "ACHR");
                int offset = FindSubrecordPayload(bytes, actor, "NAME");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(offset, 4), input.RawNpcFormId ^ 1);
                return bytes;
            });
        AssertRawMutationRejected("nonpersistent ACHR", baseline, input, testCase,
            bytes =>
            {
                var (records, _) = ParsePlacementNodes(bytes);
                PlacementRawRecord actor = records.Single(record => record.Signature == "ACHR");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(actor.Offset + 8, 4), 0);
                return bytes;
            });
        AssertRawMutationRejected("duplicate ACHR", baseline, input, testCase,
            bytes =>
            {
                var (records, groups) = ParsePlacementNodes(bytes);
                PlacementRawRecord actor = records.Single(record => record.Signature == "ACHR");
                RawGroup persistent = groups.Single(group => group.Type == 8);
                return AppendToGroup(bytes, persistent, RecordBytes(bytes, actor), groups);
            });
        AssertRawMutationRejected("NaN transform", baseline, input, testCase,
            bytes =>
            {
                var (records, _) = ParsePlacementNodes(bytes);
                PlacementRawRecord actor = records.Single(record => record.Signature == "ACHR");
                int offset = FindSubrecordPayload(bytes, actor, "DATA");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(offset, 4), 0x7FC0_0000);
                return bytes;
            });
        AssertRawMutationRejected("infinite transform", baseline, input, testCase,
            bytes =>
            {
                var (records, _) = ParsePlacementNodes(bytes);
                PlacementRawRecord actor = records.Single(record => record.Signature == "ACHR");
                int offset = FindSubrecordPayload(bytes, actor, "DATA");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(offset + 4, 4), 0x7F80_0000);
                return bytes;
            });
        AssertRawMutationRejected("wrong HEDR count", baseline, input, testCase,
            bytes =>
            {
                int offset = FindTes4SubrecordPayload(bytes, "HEDR");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(offset + 4, 4), 3);
                return bytes;
            });
        AssertRawMutationRejected("wrong NextObjectID", baseline, input, testCase,
            bytes =>
            {
                int offset = FindTes4SubrecordPayload(bytes, "HEDR");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(offset + 8, 4), 0x802);
                return bytes;
            });

        // The baseline uses equal-width master names so a true byte-level
        // reorder does not require changing any enclosing sizes.
        AssertRawMutationRejected("master reorder", baseline, input, testCase,
            bytes =>
            {
                int first = FindTes4SubrecordPayload(bytes, "MAST");
                int second = FindTes4SubrecordPayload(
                    bytes,
                    "MAST",
                    first + input.Masters[0].Value.Length + 1 + 6);
                byte[] firstMaster = bytes.AsSpan(first, input.Masters[0].Value.Length + 1).ToArray();
                bytes.AsSpan(second, firstMaster.Length).CopyTo(bytes.AsSpan(first));
                firstMaster.CopyTo(bytes.AsSpan(second));
                return bytes;
            });

        var ambiguousLight = new InteriorPlacementTopologyInput(
            new PluginName("AmbiguousLight.esl"),
            [new PluginName("Skyrim.esm"), new PluginName("A.esl"), new PluginName("B.esl")],
            0xFE12_3456,
            "Ambiguous_Cell",
            0x10,
            0x20,
            0x0000_0800,
            new InteriorPlacementTopologyTransform(0, 0, 0, 0, 0, 0),
            null);
        AssertThrowsWithMessage<InvalidDataException>(
            () => new BethesdaSkyrimInteriorPlacementTopologyWriter().Write(ambiguousLight),
            "light-owner-ambiguous");
    }

    private static void AssertRawMutationRejected(
        string name,
        byte[] baseline,
        InteriorPlacementTopologyInput input,
        TopologyCase testCase,
        Func<byte[], byte[]> mutate)
    {
        try
        {
            ValidateRawOutput(mutate(baseline.ToArray()), input, testCase);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }
        catch (OverflowException)
        {
            return;
        }
        throw new InvalidOperationException($"Hostile mutation '{name}' was accepted.");
    }

    private static void AssertThrowsWithMessage<TException>(
        Action action,
        string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            Assert(exception.Message.Contains(message, StringComparison.Ordinal),
                $"Expected diagnostic fragment '{message}', got '{exception.Message}'.");
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} containing '{message}'.");
    }

    private static (List<PlacementRawRecord> Records, List<RawGroup> Groups)
        ParsePlacementNodes(byte[] bytes)
    {
        uint tes4Size = ReadUInt32(bytes, 4);
        var records = new List<PlacementRawRecord>();
        var groups = new List<RawGroup>();
        Walk(bytes, checked(24 + (int)tes4Size), bytes.Length, records, groups);
        return (records, groups);
    }

    private static byte[] MutateGroupLabel(byte[] bytes, uint type, uint label)
    {
        var (_, groups) = ParsePlacementNodes(bytes);
        RawGroup group = groups.Single(item => item.Type == type);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(group.Offset + 8, 4), label);
        return bytes;
    }

    private static byte[] RecordBytes(byte[] bytes, PlacementRawRecord record) =>
        bytes.AsSpan(record.Offset, checked(24 + record.Body.Length)).ToArray();

    private static byte[] AppendToGroup(
        byte[] bytes,
        RawGroup parent,
        byte[] appended,
        List<RawGroup> groups)
    {
        int insert = checked(parent.Offset + (int)parent.Size);
        byte[] output = new byte[checked(bytes.Length + appended.Length)];
        Buffer.BlockCopy(bytes, 0, output, 0, insert);
        Buffer.BlockCopy(appended, 0, output, insert, appended.Length);
        Buffer.BlockCopy(bytes, insert, output, insert + appended.Length,
            bytes.Length - insert);
        foreach (RawGroup group in groups.Where(group =>
                     group.Offset <= insert && insert <= group.Offset + group.Size))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(group.Offset + 4, 4), group.Size + checked((uint)appended.Length));
        }
        return output;
    }

    private static byte[] AppendToRecord(
        byte[] bytes,
        PlacementRawRecord record,
        byte[] appended,
        List<RawGroup> groups)
    {
        int insert = checked(record.Offset + 24 + record.Body.Length);
        byte[] output = new byte[checked(bytes.Length + appended.Length)];
        Buffer.BlockCopy(bytes, 0, output, 0, insert);
        Buffer.BlockCopy(appended, 0, output, insert, appended.Length);
        Buffer.BlockCopy(bytes, insert, output, insert + appended.Length,
            bytes.Length - insert);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(record.Offset + 4, 4), checked((uint)record.Body.Length + (uint)appended.Length));
        foreach (RawGroup group in groups.Where(group =>
                     group.Offset <= insert && insert <= group.Offset + group.Size))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(group.Offset + 4, 4), group.Size + checked((uint)appended.Length));
        }
        return output;
    }

    private static byte[] EncodeSubrecord(string signature, byte[] payload)
    {
        byte[] output = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(output, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(4, 2), checked((ushort)payload.Length));
        payload.CopyTo(output, 6);
        return output;
    }

    private static int FindSubrecordHeader(
        byte[] bytes,
        PlacementRawRecord record,
        string signature) =>
        FindSubrecord(bytes, record, signature).HeaderOffset;

    private static int FindSubrecordPayload(
        byte[] bytes,
        PlacementRawRecord record,
        string signature) =>
        FindSubrecord(bytes, record, signature).PayloadOffset;

    private static (int HeaderOffset, int PayloadOffset) FindSubrecord(
        byte[] bytes,
        PlacementRawRecord record,
        string signature)
    {
        int position = record.Offset + 24;
        int end = checked(position + record.Body.Length);
        while (position < end)
        {
            string actual = Encoding.ASCII.GetString(bytes, position, 4);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (actual == signature)
                return (position, position + 6);
            position = checked(position + 6 + length);
        }
        throw new InvalidOperationException($"Missing test subrecord {signature}.");
    }

    private static byte[] ReadSubrecordBytes(
        byte[] bytes,
        PlacementRawRecord record,
        string signature)
    {
        var (header, payload) = FindSubrecord(bytes, record, signature);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(header + 4, 2));
        return bytes.AsSpan(header, 6 + length).ToArray();
    }

    private static int FindTes4SubrecordPayload(
        byte[] bytes,
        string signature,
        int start = 24)
    {
        int position = start;
        int end = checked(24 + (int)ReadUInt32(bytes, 4));
        while (position < end)
        {
            string actual = Encoding.ASCII.GetString(bytes, position, 4);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (actual == signature)
                return position + 6;
            position = checked(position + 6 + length);
        }
        throw new InvalidOperationException($"Missing TES4 test subrecord {signature}.");
    }

    private static TopologyCase CreateTopologyCase(
        string name,
        string[] masters,
        uint rawCellFormId,
        uint rawNpcFormId)
    {
        string owner = ResolveOwner(masters, rawCellFormId);
        string npcOwner = ResolveOwner(masters, rawNpcFormId);
        return new TopologyCase(name, masters, rawCellFormId, rawNpcFormId, owner, npcOwner);
    }

    private static string ResolveOwner(string[] masters, uint rawFormId)
    {
        int index = checked((int)(rawFormId >> 24));
        Assert(index < masters.Length,
            $"Synthetic raw FormID 0x{rawFormId:X8} has no declared master.");
        return masters[index];
    }

    private static void ValidateRawOutput(
        byte[] bytes,
        InteriorPlacementTopologyInput input,
        TopologyCase testCase)
    {
        Assert(Encoding.ASCII.GetString(bytes, 0, 4) == "TES4",
            $"{testCase.Name}: missing TES4 header.");
        uint tes4Size = ReadUInt32(bytes, 4);
        uint tes4Flags = ReadUInt32(bytes, 8);
        Assert((tes4Flags & 0x200) != 0 && (tes4Flags & 0x1) == 0,
            $"{testCase.Name}: TES4 flags are not ESL-only.");
        Assert(ReadUInt16(bytes, 20) == 44,
            $"{testCase.Name}: TES4 FormVersion is not 44.");
        var tes4Subrecords = ReadSubrecords(
            bytes.AsSpan(24, checked((int)tes4Size)));
        byte[] hedr = tes4Subrecords.Single(row => row.Signature == "HEDR").Payload;
        Assert(hedr.Length == 12 &&
               Math.Abs(BitConverter.ToSingle(hedr, 0) - 1.7f) < 0.0001f &&
               BinaryPrimitives.ReadUInt32LittleEndian(hedr.AsSpan(4)) == 2 &&
               BinaryPrimitives.ReadUInt32LittleEndian(hedr.AsSpan(8)) == 0x801,
            $"{testCase.Name}: HEDR contract changed.");
        string[] masters = tes4Subrecords
            .Where(row => row.Signature == "MAST")
            .Select(row => Encoding.ASCII.GetString(row.Payload).TrimEnd('\0'))
            .ToArray();
        Assert(masters.SequenceEqual(testCase.Masters),
            $"{testCase.Name}: raw master order changed.");

        var records = new List<PlacementRawRecord>();
        var groups = new List<RawGroup>();
        Walk(bytes, checked(24 + (int)tes4Size), bytes.Length, records, groups);
        Assert(records.Count == 2 && records.Count(row => row.Signature == "CELL") == 1 &&
               records.Count(row => row.Signature == "ACHR") == 1,
            $"{testCase.Name}: raw record census is not one CELL plus one ACHR.");
        Assert(groups.Select(group => group.Type).SequenceEqual([0u, 2u, 3u, 6u, 8u]),
            $"{testCase.Name}: raw group hierarchy changed.");
        Assert(groups.Single(group => group.Type == 2).Label == input.InteriorBlock &&
               groups.Single(group => group.Type == 3).Label == input.InteriorSubBlock,
            $"{testCase.Name}: observed interior block/sub-block labels changed.");

        PlacementRawRecord cell = records.Single(row => row.Signature == "CELL");
        Assert(cell.FormId == input.RawCellFormId && cell.Flags == 0 &&
               ReadSubrecords(cell.Body).Select(row => row.Signature)
                   .SequenceEqual(["EDID"]),
            $"{testCase.Name}: CELL record surface changed.");
        Assert(Encoding.ASCII.GetString(
                   ReadSubrecords(cell.Body).Single().Payload).TrimEnd('\0') == input.CellEditorId,
            $"{testCase.Name}: CELL EDID changed.");

        PlacementRawRecord actor = records.Single(row => row.Signature == "ACHR");
        Assert((actor.FormId & 0x00FF_FFFFu) == 0x800 && actor.Flags == 0x400,
            $"{testCase.Name}: ACHR identity or persistence changed.");
        var actorFields = ReadSubrecords(actor.Body);
        Assert(actorFields.Select(row => row.Signature).SequenceEqual(["NAME", "DATA"]),
            $"{testCase.Name}: ACHR record surface changed.");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(actorFields[0].Payload) == input.RawNpcFormId,
            $"{testCase.Name}: ACHR NAME link changed.");
        float[] transform = Enumerable.Range(0, 6)
            .Select(index => BitConverter.ToSingle(actorFields[1].Payload, index * 4))
            .ToArray();
        Assert(transform.All(float.IsFinite) &&
               transform.SequenceEqual(
                   [input.Transform.X, input.Transform.Y, input.Transform.Z,
                    input.Transform.RotationX, input.Transform.RotationY,
                    input.Transform.RotationZ]),
            $"{testCase.Name}: ACHR transform changed.");

        RawGroup children = groups.Single(group => group.Type == 6);
        RawGroup persistent = groups.Single(group => group.Type == 8);
        Assert(children.Label == input.RawCellFormId &&
               persistent.Label == input.RawCellFormId,
            $"{testCase.Name}: child group labels do not match the CELL.");
    }

    private static void Walk(
        byte[] bytes,
        int start,
        int end,
        List<PlacementRawRecord> records,
        List<RawGroup> groups)
    {
        int position = start;
        while (position < end)
        {
            Assert(end - position >= 24, "Raw topology header is truncated.");
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            uint size = ReadUInt32(bytes, position + 4);
            if (signature == "GRUP")
            {
                Assert(size >= 24 && position + size <= end,
                    "Raw topology group exceeds its parent.");
                groups.Add(new RawGroup(
                    position,
                    size,
                    ReadUInt32(bytes, position + 8),
                    ReadUInt32(bytes, position + 12)));
                Walk(bytes, position + 24, checked(position + (int)size), records, groups);
                position += checked((int)size);
                continue;
            }

            int recordEnd = checked(position + 24 + (int)size);
            Assert(recordEnd <= end, "Raw topology record exceeds its parent.");
            records.Add(new PlacementRawRecord(
                position,
                signature,
                ReadUInt32(bytes, position + 12),
                ReadUInt32(bytes, position + 8),
                bytes.AsSpan(position + 24, checked((int)size)).ToArray()));
            position = recordEnd;
        }
        Assert(position == end, "Raw topology did not end on its parent boundary.");
    }

    private static ImmutableArray<RawSubrecord> ReadSubrecords(ReadOnlySpan<byte> body)
    {
        var rows = ImmutableArray.CreateBuilder<RawSubrecord>();
        int position = 0;
        while (position < body.Length)
        {
            Assert(body.Length - position >= 6, "Raw subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(body.Slice(position, 4));
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
                body.Slice(position + 4, 2));
            int end = checked(position + 6 + length);
            Assert(end <= body.Length, "Raw subrecord exceeds its parent.");
            rows.Add(new RawSubrecord(signature,
                body.Slice(position + 6, length).ToArray()));
            position = end;
        }
        return rows.ToImmutable();
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static bool TransformMatches(
        IPlacementGetter placement,
        InteriorPlacementTopologyTransform expected)
    {
        float[] actual =
        [
            placement.Position.X, placement.Position.Y, placement.Position.Z,
            placement.Rotation.X, placement.Rotation.Y, placement.Rotation.Z
        ];
        float[] target =
        [
            expected.X, expected.Y, expected.Z,
            expected.RotationX, expected.RotationY, expected.RotationZ
        ];
        return actual.SequenceEqual(target);
    }

    private sealed record TopologyCase(
        string Name,
        string[] Masters,
        uint RawCellFormId,
        uint RawNpcFormId,
        string CellOwner,
        string NpcOwner);

    private sealed record RawGroup(int Offset, uint Size, uint Label, uint Type);

    private sealed record PlacementRawRecord(
        int Offset,
        string Signature,
        uint FormId,
        uint Flags,
        byte[] Body);

    private sealed record RawSubrecord(string Signature, byte[] Payload);
}
