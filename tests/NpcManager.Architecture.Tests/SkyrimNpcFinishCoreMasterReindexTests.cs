using System.Buffers.Binary;
using System.Collections.Immutable;
using Mutagen.Bethesda;
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
    private static async Task TestFinishCoreMasterReindex()
    {
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        var writer = new BethesdaSkyrimNpcFinishCoreWriter();
        byte[] control = writer.Write(fixture.SourcePlugin, fixture.Proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(control)) ==
                "505DE9DBDB3F344AEE5D79E52E80393507DDE802CF417EED68721D113F1B57D9",
            "The unchanged-master Skyrim outfit control is not byte-identical to the pre-fix output.");
        var key = fixture.PluginKey;
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        SkyrimMod source = SkyrimMod.CreateFromBinary(new ModPath(key, new FilePath(fixture.SourcePlugin.Value)), SkyrimRelease.SkyrimSE);
        for (int index = 1; index <= 6; index++)
            source.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension($"SourceMaster{index}.esm") });
        var keyword = new Keyword(new FormKey(key, 0xA00), SkyrimRelease.SkyrimSE) { EditorID = "RetainedKeyword" };
        var texture = new TextureSet(new FormKey(key, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "RetainedTexture" };
        var outfit = new Outfit(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE) { EditorID = "RetainedOutfit", Items = [] };
        outfit.Items.Add(new FormLink<IOutfitTargetGetter>(new FormKey(skyrim, 0x800)));
        source.Keywords.Add(keyword);
        source.TextureSets.Add(texture);
        source.Outfits.Add(outfit);
        var topic = new DialogTopic(new FormKey(key, 0x980), SkyrimRelease.SkyrimSE) { EditorID = "RetainedTopic" };
        topic.Responses.Add(new DialogResponses(new FormKey(key, 0x981), SkyrimRelease.SkyrimSE));
        source.DialogTopics.Add(topic);
        Npc npc = source.Npcs.Single();
        npc.Keywords ??= [];
        npc.Keywords.Add(new FormLink<IKeywordGetter>(keyword.FormKey));
        npc.HeadTexture.SetTo(texture.FormKey);
        npc.SleepingOutfit.SetTo(outfit.FormKey);
        source.ModHeader.Stats.NextFormID = 0xA03;
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        AddMasterIndexSentinel(fixture.SourcePlugin.Value);
        var eighth = new PluginName("EighthOutfit.esp");
        SkyrimNpcFinishCoreRequest request = fixture.Proposal.Request! with
        {
            Source = fixture.Proposal.Request!.Source with { PluginSha256 = CombatHash(fixture.SourcePlugin.Value) },
            OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                ExistingOutfit = new FormReference(eighth, new FormId(0x800)) }
        };
        ImmutableArray<string> reindexedChanges = fixture.Proposal.ExistingRecordChanges
            .Where(row => !row.StartsWith("OTFT 0x00000806:", StringComparison.Ordinal))
            .Select(row => row
                .Replace("CSTY 0x00000805:", "CSTY 0x00000A03:", StringComparison.Ordinal)
                .Replace("PACK 0x00000807:", "PACK 0x00000A04:", StringComparison.Ordinal))
            .ToImmutableArray();
        Assert(reindexedChanges.Count(row => row.StartsWith("CSTY 0x00000A03:", StringComparison.Ordinal)) == 1 &&
                reindexedChanges.Count(row => row.StartsWith("PACK 0x00000A04:", StringComparison.Ordinal)) == 1 &&
                !reindexedChanges.Any(row => row.StartsWith("CSTY 0x00000805:", StringComparison.Ordinal) ||
                    row.StartsWith("OTFT 0x00000806:", StringComparison.Ordinal) ||
                    row.StartsWith("PACK 0x00000807:", StringComparison.Ordinal)),
            "The handcrafted master-reindex proposal retained stale allocated-record identities.");
        SkyrimNpcFinishCoreProposal proposal = fixture.Proposal with
        {
            Request = request,
            MasterOrder = source.ModHeader.MasterReferences.Select(row => row.Master.ToString()).Append(eighth.Value).ToImmutableArray(),
            AppendedMasters = [eighth.Value],
            ExistingRecordChanges = reindexedChanges,
            NewRecords = ["CSTY 0x00000A03", "PACK 0x00000A04"], NextFormId = new FormId(0xA05)
        };
        byte[] written = writer.Write(fixture.SourcePlugin, proposal, fixture.CopiedMaster, CancellationToken.None);
        var outputPath = new WorkspacePath(Path.Combine(fixture.Root, "reindexed.esp"));
        File.WriteAllBytes(outputPath.Value, written);
        var verifier = new BethesdaSkyrimNpcFinishCoreVerifier();
        var verified = verifier.Verify(fixture.SourcePlugin, outputPath, proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(verified.Verified, "Seven source masters plus an eighth outfit master failed independent verification: " +
            string.Join(" | ", verified.Diagnostics.Select(row => row.Code + ": " + row.Message)));
        SkyrimMod output = SkyrimMod.CreateFromBinary(new ModPath(key, new FilePath(outputPath.Value)), SkyrimRelease.SkyrimSE);
        var retained = output.EnumerateMajorRecords().ToDictionary(row => row.FormKey);
        foreach (IMajorRecordGetter record in source.EnumerateMajorRecords())
        {
            Assert(retained.TryGetValue(record.FormKey, out var actual), "A pre-existing record lost source ownership: " + record.FormKey);
            if (record is not INpcGetter)
                Assert(record.EnumerateFormLinks().Select(link => link.FormKey).SequenceEqual(actual!.EnumerateFormLinks().Select(link => link.FormKey)),
                    "A retained record FormLink changed: " + record.FormKey);
        }
        Npc outputNpc = output.Npcs.Single();
        Assert(outputNpc.HeadTexture.FormKey == texture.FormKey && outputNpc.SleepingOutfit.FormKey == outfit.FormKey &&
                outputNpc.Keywords!.Any(link => link.FormKey == keyword.FormKey),
            "Protected NPC FTST/SOFT/KWDA links retained the old owner prefix.");
        var rawKeyword = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(written).NonTes4Records.Single(row => row.Signature == "KYWD");
        Assert(rawKeyword.RawFormId == 0x08000A00 && BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(written, rawKeyword)
                .Single(row => row.Signature == "ZZZZ").Bytes.AsSpan(6).SequenceEqual(new byte[] { 0, 8, 0, 7 }),
            "Reindexing changed unrelated bytes that happen to resemble an old self FormID.");
        Assert(BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(written).Groups.Any(group => group.Type == 7 &&
                BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(group.Offset + 8, 4)) == 0x08000980),
            "The retained DIAL children GRUP label did not follow its full owner identity.");
        var topicGroup = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(written).Groups.Single(group => group.Type == 7);
        byte[] mislabeled = written.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(mislabeled.AsSpan(topicGroup.Offset + 8, 4), 0x08000981);
        File.WriteAllBytes(outputPath.Value, mislabeled);
        var labelVerification = verifier.Verify(fixture.SourcePlugin, outputPath, proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(!labelVerification.Verified && labelVerification.Diagnostics.Any(row => row.Code == "finish-core-verify-record-closure"),
            "Independent verification accepted a different numeric DIAL child-group label with the same ASCII replacement text.");
        File.WriteAllBytes(outputPath.Value, written);
        string unalignedRoot = Path.Combine(fixture.Root, "unaligned");
        Directory.CreateDirectory(unalignedRoot);
        var unalignedPath = new WorkspacePath(Path.Combine(unalignedRoot, key.ToString()));
        File.WriteAllBytes(unalignedPath.Value, PadRetainedTextureLink(File.ReadAllBytes(fixture.SourcePlugin.Value)));
        var unalignedProposal = proposal with { Request = request with
        {
            Source = request.Source with { PluginPath = unalignedPath, PluginSha256 = CombatHash(unalignedPath.Value) }
        } };
        bool alignmentRefused = false;
        try { writer.Write(unalignedPath, unalignedProposal, fixture.CopiedMaster, CancellationToken.None); }
        catch (InvalidDataException exception) when (exception.Message.StartsWith("finish-core-master-reindex:", StringComparison.Ordinal))
        {
            alignmentRefused = exception.Message.Contains("cannot be aligned", StringComparison.Ordinal);
        }
        Assert(alignmentRefused, "The writer guessed a FormID offset in a retained subrecord without exact typed alignment.");
        var unalignedVerification = verifier.Verify(unalignedPath, outputPath, unalignedProposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(!unalignedVerification.Verified && unalignedVerification.Diagnostics.Any(row => row.Code == "finish-core-master-reindex"),
            "Unalignable relocation did not expose a typed verification refusal.");
        outputNpc.HeadTexture.SetTo(new FormKey(ModKey.FromNameAndExtension(eighth.Value), 0xA01));
        WriteCombatFixture(output, outputPath.Value);
        var tampered = verifier.Verify(fixture.SourcePlugin, outputPath, proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(!tampered.Verified && tampered.Diagnostics.Any(row => row.Code == "finish-core-verify-form-links"),
            "Independent typed verification did not identify a retained NPC link with the wrong owner.");

        var plan = SkyrimNpcFinishCoreMasterPlanner.Plan(new(new PluginName(key.ToString()), new PluginName(key.ToString()),
            source.ModHeader.MasterReferences.Select(row => new PluginName(row.Master.ToString())).ToImmutableArray(), [eighth],
            [new(eighth, new WorkspacePath(Path.Combine(fixture.Root, eighth.Value)), new Sha256Hash(new string('A', 64)), 1, 3, [new PluginName("Skyrim.esm")])]));
        Assert(plan.Admitted && plan.MasterOrder.Select(row => row.Value).SequenceEqual(proposal.MasterOrder),
            "The planner confused an additional global load-order index with a source TES4 ordinal: " + string.Join(" | ", plan.Diagnostics));
    }

    private static void AddMasterIndexSentinel(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var record = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(bytes).NonTes4Records.Single(row => row.Signature == "KYWD");
        (int groupStart, int groupLength) = FindTopLevelGroup(bytes, "KYWD");
        byte[] sentinel = [.. "ZZZZ"u8.ToArray(), 4, 0, 0, 8, 0, 7];
        int end = record.Offset + record.Length;
        byte[] result = [.. bytes.AsSpan(0, end).ToArray(), .. sentinel, .. bytes.AsSpan(end).ToArray()];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(record.Offset + 4, 4), checked((uint)(record.PayloadLength + sentinel.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(groupStart + 4, 4), checked((uint)(groupLength + sentinel.Length)));
        File.WriteAllBytes(path, result);
    }

    private static byte[] PadRetainedTextureLink(byte[] bytes)
    {
        var npc = BethesdaSkyrimNpcFinishCoreRaw.Read(bytes).Target!;
        var texture = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, npc).Single(row => row.Signature == "FTST");
        (int groupStart, int groupLength) = FindTopLevelGroup(bytes, "NPC_");
        int end = texture.Offset + texture.Length;
        byte[] result = [.. bytes.AsSpan(0, end).ToArray(), 0, 0, 0, 0, .. bytes.AsSpan(end).ToArray()];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(texture.Offset + 4, 2), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(npc.Offset + 4, 4), checked((uint)npc.PayloadLength + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(groupStart + 4, 4), checked((uint)groupLength + 4));
        return result;
    }
}
