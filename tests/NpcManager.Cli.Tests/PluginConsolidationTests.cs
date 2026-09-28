using System.Buffers.Binary;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunEditPackagePreservationAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "task21-preservation", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        var input = Child(root, "Input.esp");
        Preview258ConsumerDefectTests.WriteFixture(input);
        byte[] original = File.ReadAllBytes(input.Value);
        var before = BethesdaRawPluginInventory.Read(input);
        Require(before.Count(row => row.Signature != "NPC_" && row.Signature != "TES4") >= 5,
            "Preservation fixture needs five owned non-NPC records.");
        await DiscoverEditPackageAsync(root);
        var outputRoot = Child(root, "package");
        var result = await RunCliAsync(root, "npc", "edit-package", "--edition", "skyrimse",
            "--input-plugin", input.Value, "--input-sha256", HashFile(input), "--npc", "0x800",
            "--output-root", outputRoot.Value, "--plugin", "Edited.esp",
            "--output-kind", "standalone-copy", "--editor-id", "EditedNpc", "--json");
        Require(result.ExitCode == 0, "Actual scalar edit failed: " + result.Root + result.StdErr);
        var after = BethesdaRawPluginInventory.Read(Child(outputRoot, "Data", "Edited.esp"));
        Require(before.Select(row => (row.GroupPath, row.Ordinal, row.Signature, row.RawFormId))
            .SequenceEqual(after.Select(row => (row.GroupPath, row.Ordinal, row.Signature, row.RawFormId))),
            "Scalar edit lost or changed record ownership.");
        Require(before.Where(row => row.Signature != "NPC_").Select(row => row.Sha256)
            .SequenceEqual(after.Where(row => row.Signature != "NPC_").Select(row => row.Sha256)),
            "Scalar edit changed a non-NPC record body.");
        Require(original.SequenceEqual(File.ReadAllBytes(input.Value)), "Scalar edit mutated source.");
    }

    internal static async Task RunPluginConsolidationAsync()
    {
        var root = new WorkspacePath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "task21-consolidation", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        var input = Child(root, "Input.esp");
        var providerPath = Child(root, "Provider.esp");
        Preview258ConsumerDefectTests.WriteFixture(input, duplicateLocalFormId: false);
        var providerKey = ModKey.FromNameAndExtension("Provider.esp");
        var main = SkyrimMod.CreateFromBinary(input.Value, SkyrimRelease.SkyrimSE);
        main.ModHeader.MasterReferences.Add(new MasterReference { Master = providerKey });
        var provider = new SkyrimMod(providerKey, SkyrimRelease.SkyrimSE);
        for (uint id = 0x800; id < 0x803; id++)
            provider.HeadParts.Add(new HeadPart(new FormKey(providerKey, id), SkyrimRelease.SkyrimSE)
            { EditorID = "ImportedPart" + id, Type = HeadPart.TypeEnum.Hair });
        provider.HeadParts[new FormKey(providerKey, 0x800)].ExtraParts.Add(
            new FormLink<IHeadPartGetter>(new FormKey(providerKey, 0x802)));
        main.Npcs.Single().HeadParts.Add(new FormLink<IHeadPartGetter>(new FormKey(providerKey, 0x800)));
        main.Npcs.Single().HeadParts.Add(new FormLink<IHeadPartGetter>(new FormKey(providerKey, 0x801)));
        provider.CombatStyles.Add(new CombatStyle(new FormKey(providerKey, 0x803), SkyrimRelease.SkyrimSE) { EditorID = "ImportedStyle" });
        provider.Outfits.Add(new Outfit(new FormKey(providerKey, 0x804), SkyrimRelease.SkyrimSE) { EditorID = "ImportedOutfit" });
        provider.Npcs.Add(new Npc(new FormKey(providerKey, 0x805), SkyrimRelease.SkyrimSE) { EditorID = "ImportedAliasActor" });
        main.Npcs.Single().CombatStyle.SetTo(new FormKey(providerKey, 0x803));
        main.Npcs.Single().DefaultOutfit.SetTo(new FormKey(providerKey, 0x804));
        main.Keywords.Add(new Keyword(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x805), SkyrimRelease.SkyrimSE)
            { EditorID = "RetainedForeignOwner" });
        var quest = new Quest(new FormKey(main.ModKey, 0x809), SkyrimRelease.SkyrimSE) { EditorID = "RetainedQuest" };
        quest.Aliases.Add(new QuestAlias { ID = 0, Name = "ImportedActorAlias",
            UniqueActor = new FormLinkNullable<INpcGetter>(new FormKey(providerKey, 0x805)) });
        main.Quests.Add(quest);
        ((IMod)main).NextFormID = 0x80A;
        WriteConsolidationFixture(main, input.Value);
        AppendTes4Subrecord(input.Value, "SNAM", Encoding.UTF8.GetBytes("Consolidation fixture\0"));
        WriteConsolidationFixture(provider, providerPath.Value);
        Directory.CreateDirectory(Child(root, "Seq").Value);
        byte[] seq = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seq, 0x02000809);
        File.WriteAllBytes(Child(root, "Seq", "Input.seq").Value, seq);
        string sourceHash = HashFile(input), providerHash = HashFile(providerPath);
        var output = Child(root, "Merged.esp");
        await DiscoverEditPackageAsync(root);
        var result = await RunCliAsync(root, "npc", "edit-package", "--edition", "skyrimse",
            "--input-plugin", input.Value, "--input-sha256", sourceHash,
            "--consolidate", providerPath.Value, "--esl-flag", "--output", output.Value, "--json");
        Require(result.ExitCode == 0, "Consolidation failed: " + result.Root + result.StdErr);
        using var read = SkyrimMod.CreateFromBinaryOverlay(output.Value, SkyrimRelease.SkyrimSE);
        Require(Tes4Signatures(File.ReadAllBytes(output.Value))
                .SequenceEqual(["HEDR", "MAST", "DATA", "SNAM"]),
            "Consolidation moved the TES4 master table behind a later retained subrecord.");
        Require(read.IsSmallMaster && read.ModHeader.MasterReferences.Count == 1 &&
            read.ModHeader.MasterReferences[0].Master.ToString() == "Skyrim.esm", "ESL/master removal failed.");
        Require(read.EnumerateMajorRecords().Count() == main.EnumerateMajorRecords().Count() + 6,
            "Consolidation lost main records or provider imports.");
        foreach (var old in main.EnumerateMajorRecords())
            Require(read.EnumerateMajorRecords().Any(row => row.FormKey.ID == old.FormKey.ID &&
                row.FormKey.ModKey == (old.FormKey.ModKey == main.ModKey ? read.ModKey : old.FormKey.ModKey)), "Existing local ID changed or disappeared.");
        var imported = read.HeadParts.Where(row => row.EditorID!.StartsWith("ImportedPart", StringComparison.Ordinal)).ToArray();
        Require(imported.Length == 3 && imported.All(row => row.FormKey.ID >= 0x80A && row.FormKey.ModKey == read.ModKey),
            "Imported IDs collide or retain provider ownership.");
        Require(read.Npcs[new FormKey(read.ModKey, 0x800)].HeadParts.All(link => link.FormKey.ModKey == read.ModKey) &&
            imported.Single(row => row.EditorID == "ImportedPart2048").ExtraParts.Single().FormKey ==
            imported.Single(row => row.EditorID == "ImportedPart2050").FormKey,
            "PNAM/HNAM links were not remapped.");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Child(root, "Seq", "Merged.seq").Value)) == 0x01000809,
            "SEQ was not rebased to the retained quest owner.");
        var targetNpc = read.Npcs[new FormKey(read.ModKey, 0x800)];
        Require(targetNpc.CombatStyle.FormKey == read.CombatStyles.Single().FormKey &&
            targetNpc.DefaultOutfit.FormKey == read.Outfits.Single().FormKey &&
            read.Quests.Single().Aliases.Single().UniqueActor.FormKey == read.Npcs.Single(row => row.EditorID == "ImportedAliasActor").FormKey,
            "DOFT/ZNAM/quest alias links were not remapped.");
        var comparable = Child(root, "comparison", "Input.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(comparable.Value)!);
        File.Copy(output.Value, comparable.Value);
        var reader = new BethesdaPluginReader();
        var before = await reader.ReadAsync(new PluginReadRequest(GameEdition.SkyrimSpecialEdition, input)
            { NormalizedMasterOrder = [new PluginName("Provider.esp"), new PluginName("Skyrim.esm")] }, CancellationToken.None);
        var after = await reader.ReadAsync(new PluginReadRequest(GameEdition.SkyrimSpecialEdition, comparable)
            { NormalizedMasterOrder = [new PluginName("Provider.esp"), new PluginName("Skyrim.esm")] }, CancellationToken.None);
        foreach (var record in before.Records.Where(row => row.Signature is not "NPC_" and not "QUST"))
            Require(after.Records.Single(row => row.Signature == record.Signature && row.FormId == record.FormId &&
                row.OwnerPlugin == record.OwnerPlugin).RawRecordSha256 == record.RawRecordSha256,
                "Normalized protected record body changed: " + record.Signature + " " + record.FormId);
        var flagOnly = Child(root, "FlagOnly.esp");
        var flagResult = await RunCliAsync(root, "npc", "edit-package", "--edition", "skyrimse",
            "--input-plugin", input.Value, "--input-sha256", sourceHash,
            "--esl-flag", "--output", flagOnly.Value, "--json");
        Require(flagResult.ExitCode == 0, "Independent ESL flag mode failed: " + flagResult.Root);
        using (var flagged = SkyrimMod.CreateFromBinaryOverlay(flagOnly.Value, SkyrimRelease.SkyrimSE))
            Require(flagged.IsSmallMaster && flagged.ModHeader.Stats.NextFormID == main.ModHeader.Stats.NextFormID,
                "ESL flag mode compacted or changed next ID.");
        Require(BethesdaRawPluginInventory.Read(input).Where(row => row.Signature != "TES4").Select(row => row.Sha256)
            .SequenceEqual(BethesdaRawPluginInventory.Read(flagOnly).Where(row => row.Signature != "TES4").Select(row => row.Sha256)),
            "ESL flag-only mode changed a raw record body or ID.");
        string retainedOutputHash = HashFile(output);
        var retry = await RunCliAsync(root, "npc", "edit-package", "--edition", "skyrimse",
            "--input-plugin", input.Value, "--input-sha256", sourceHash,
            "--consolidate", providerPath.Value, "--output", output.Value, "--json");
        Require(retry.ExitCode != 0 && HashFile(output) == retainedOutputHash, "Existing output was overwritten.");
        await ExpectRefusal("wrong-hash", input, new string('0', 64), ["--consolidate", providerPath.Value], "SHA-256");
        var oversized = Child(root, "Oversized.esp");
        byte[] highNext = File.ReadAllBytes(input.Value);
        Require(highNext.AsSpan(24, 4).SequenceEqual("HEDR"u8), "Fixture HEDR changed.");
        BinaryPrimitives.WriteUInt32LittleEndian(highNext.AsSpan(38, 4), 0x1000);
        File.WriteAllBytes(oversized.Value, highNext);
        var boundaryOutput = Child(root, "EslBoundary.esp");
        var boundary = await RunCliAsync(root, "npc", "edit-package", "--edition", "skyrimse",
            "--input-plugin", oversized.Value, "--input-sha256", HashFile(oversized),
            "--esl-flag", "--output", boundaryOutput.Value, "--json");
        Require(boundary.ExitCode == 0 && File.Exists(boundaryOutput.Value),
            "ESL flag mode refused the legal 0xFFF owned-ID boundary: " + boundary.Root + boundary.StdErr);
        using (var boundaryRead = SkyrimMod.CreateFromBinaryOverlay(boundaryOutput.Value, SkyrimRelease.SkyrimSE))
            Require(boundaryRead.IsSmallMaster && boundaryRead.ModHeader.Stats.NextFormID == 0x1000,
                "ESL boundary output did not retain next FormID 0x1000.");
        var inheritedOldForm = Child(root, "InheritedOldForm.esp");
        byte[] lightOldForm = File.ReadAllBytes(input.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(lightOldForm.AsSpan(8, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(lightOldForm.AsSpan(8, 4)) | 0x200);
        BinaryPrimitives.WriteUInt16LittleEndian(lightOldForm.AsSpan(20, 2), 43);
        File.WriteAllBytes(inheritedOldForm.Value, lightOldForm);
        await ExpectRefusal("inherited-esl-form", inheritedOldForm, HashFile(inheritedOldForm),
            ["--consolidate", providerPath.Value], "Form44");
        var inheritedLight = Child(root, "InheritedLight.esp");
        BinaryPrimitives.WriteUInt32LittleEndian(highNext.AsSpan(8, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(highNext.AsSpan(8, 4)) | 0x200);
        File.WriteAllBytes(inheritedLight.Value, highNext);
        await ExpectRefusal("inherited-esl-ineligible", inheritedLight, HashFile(inheritedLight),
            ["--consolidate", providerPath.Value], "compaction settings");
        File.WriteAllBytes(Child(root, "Seq", "Input.seq").Value, [1, 2, 3]);
        await ExpectRefusal("bad-seq", input, sourceHash, ["--consolidate", providerPath.Value], "SEQ");
        File.WriteAllBytes(Child(root, "Seq", "Input.seq").Value, seq);
        byte[] collisionProvider = File.ReadAllBytes(providerPath.Value);
        provider.ModHeader.MasterReferences.Add(new MasterReference
            { Master = ModKey.FromNameAndExtension("provider-master-collision.esp") });
        WriteConsolidationFixture(provider, providerPath.Value);
        await ExpectRefusal("provider-master-collision", input, sourceHash,
            ["--consolidate", providerPath.Value], "itself as a master");
        provider.ModHeader.MasterReferences.Clear();
        File.WriteAllBytes(providerPath.Value, collisionProvider);
        byte[] originalProvider = File.ReadAllBytes(providerPath.Value);
        provider.HeadParts.Remove(new FormKey(providerKey, 0x802));
        WriteConsolidationFixture(provider, providerPath.Value);
        await ExpectRefusal("missing-closure", input, sourceHash, ["--consolidate", providerPath.Value], "omitted provider");
        File.WriteAllBytes(providerPath.Value, originalProvider);
        Require(HashFile(input) == sourceHash && HashFile(providerPath) == providerHash, "Consolidation mutated an input.");

        async Task ExpectRefusal(string name, WorkspacePath source, string hash, string[] extra, string reason)
        {
            var fresh = Child(root, name + ".esp");
            string[] args = ["npc", "edit-package", "--edition", "skyrimse", "--input-plugin", source.Value,
                "--input-sha256", hash, "--output", fresh.Value, .. extra, "--json"];
            var refused = await RunCliAsync(root, args);
            Require(refused.ExitCode != 0 && refused.Root.GetRawText().Contains(reason, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(fresh.Value) && !File.Exists(fresh.Value + ".consolidation.json") &&
                !File.Exists(Child(root, "Seq", name + ".seq").Value),
                "Expected atomic refusal for " + name + ": " + refused.Root + refused.StdErr);
        }
    }

    private static void WriteConsolidationFixture(SkyrimMod mod, string path)
    {
        using var stream = new MemoryStream();
        mod.WriteToBinary(stream, new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static void AppendTes4Subrecord(string path, string signature, byte[] payload)
    {
        byte[] source = File.ReadAllBytes(path);
        int headerLength = 24 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4)));
        byte[] field = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(field, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(4), checked((ushort)payload.Length));
        payload.CopyTo(field, 6);
        byte[] result = [.. source.AsSpan(0, headerLength), .. field, .. source.AsSpan(headerLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),
            checked((uint)(headerLength - 24 + field.Length)));
        File.WriteAllBytes(path, result);
    }

    private static IEnumerable<string> Tes4Signatures(byte[] source)
    {
        int cursor = 24;
        int end = cursor + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4)));
        while (cursor < end)
        {
            yield return Encoding.ASCII.GetString(source, cursor, 4);
            cursor += 6 + BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(cursor + 4));
        }
    }

    private static async Task DiscoverEditPackageAsync(WorkspacePath root)
    {
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Discovery failed.");
        Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json",
            "--command", "npc edit-package")).ExitCode == 0, "Edit schema discovery failed.");
    }
}
