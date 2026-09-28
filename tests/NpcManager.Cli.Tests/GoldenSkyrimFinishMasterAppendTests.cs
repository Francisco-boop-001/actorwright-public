using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static SkyrimNpcFinishCoreAdditionalMasterBinding PrepareFinishMasterAppendFixture(
        WorkspacePath root, WorkspacePath pluginPath, WorkspacePath manifestPath)
    {
        var key = ModKey.FromNameAndExtension("PackagedNpc.esp");
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        SkyrimMod source = SkyrimMod.CreateFromBinary(
            new ModPath(key, new FilePath(pluginPath.Value)), SkyrimRelease.SkyrimSE);
        Require(source.ModHeader.MasterReferences.Count <= 7 &&
                source.ModHeader.MasterReferences.Any(row => row.Master == skyrim),
            "The master-append fixture requires a generated source prefix of at most seven masters including Skyrim.esm.");
        for (int index = source.ModHeader.MasterReferences.Count; index < 7; index++)
            source.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension($"SourceMaster{index}.esm")
            });
        var keyword = new Keyword(new FormKey(key, 0xE00), SkyrimRelease.SkyrimSE) { EditorID = "RetainedKeyword" };
        var texture = new TextureSet(new FormKey(key, 0xE01), SkyrimRelease.SkyrimSE)
        {
            EditorID = "RetainedTexture", Diffuse = "actors/character/female/femalehead.dds"
        };
        var outfit = new Outfit(new FormKey(key, 0xE02), SkyrimRelease.SkyrimSE) { EditorID = "RetainedSleepingOutfit", Items = [] };
        outfit.Items.Add(new FormLink<IOutfitTargetGetter>(new FormKey(skyrim, 0x900)));
        source.Keywords.Add(keyword);
        source.TextureSets.Add(texture);
        source.Outfits.Add(outfit);
        Npc npc = source.Npcs.Single();
        npc.Keywords ??= [];
        npc.Keywords.Add(new FormLink<IKeywordGetter>(keyword.FormKey));
        npc.HeadTexture.SetTo(texture.FormKey);
        npc.SleepingOutfit.SetTo(outfit.FormKey);
        source.ModHeader.Stats.NextFormID = 0xE03;
        WriteMasterAppendFixture(source, pluginPath.Value);

        // This is an owned synthetic Finish source. Rebind its ordinary package
        // inventory before composing the reviewed Finish request.
        JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath.Value))!.AsObject();
        JsonNode pluginRow = manifest["artifacts"]!.AsArray().Single(row =>
            row!["relativePath"]!.GetValue<string>().EndsWith("PackagedNpc.esp", StringComparison.OrdinalIgnoreCase))!;
        pluginRow["sha256"] = HashFile(pluginPath);
        pluginRow["byteLength"] = new FileInfo(pluginPath.Value).Length;
        File.WriteAllBytes(manifestPath.Value, JsonSerializer.SerializeToUtf8Bytes(manifest));

        var providerKey = ModKey.FromNameAndExtension("EighthOutfit.esp");
        var provider = new SkyrimMod(providerKey, SkyrimRelease.SkyrimSE);
        provider.ModHeader.MasterReferences.Add(new MasterReference { Master = skyrim });
        var selected = new Outfit(new FormKey(providerKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "SelectedEighthOutfit", Items = [] };
        selected.Items.Add(new FormLink<IOutfitTargetGetter>(new FormKey(skyrim, 0x900)));
        provider.Outfits.Add(selected);
        var path = Child(root, "authority", providerKey.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path.Value)!);
        WriteMasterAppendFixture(provider, path.Value);
        return new(new PluginName(providerKey.ToString()), path, new Sha256Hash(HashFile(path)),
            new FileInfo(path.Value).Length, 8);
    }

    private static void AssertFinishMasterAppendOutput(WorkspacePath sourcePath, WorkspacePath outputPath)
    {
        var key = ModKey.FromNameAndExtension("PackagedNpc.esp");
        using var source = SkyrimMod.CreateFromBinaryOverlay(new ModPath(key, new FilePath(sourcePath.Value)), SkyrimRelease.SkyrimSE);
        using var output = SkyrimMod.CreateFromBinaryOverlay(new ModPath(key, new FilePath(outputPath.Value)), SkyrimRelease.SkyrimSE);
        Require(source.ModHeader.MasterReferences.Count == 7 && output.ModHeader.MasterReferences.Count == 8 &&
                output.ModHeader.MasterReferences.Take(7).Select(row => row.Master).SequenceEqual(source.ModHeader.MasterReferences.Select(row => row.Master)),
            "Finish did not preserve the seven source masters and append the eighth outfit owner.");
        var actual = output.EnumerateMajorRecords().ToDictionary(row => row.FormKey);
        foreach (IMajorRecordGetter record in source.EnumerateMajorRecords())
        {
            Require(actual.TryGetValue(record.FormKey, out var retained), "Master append lost record ownership: " + record.FormKey);
            if (record is not INpcGetter)
                Require(record.EnumerateFormLinks().Select(link => link.FormKey).SequenceEqual(
                        retained!.EnumerateFormLinks().Select(link => link.FormKey)),
                    "Master append changed a retained record FormLink: " + record.FormKey);
        }
        INpcGetter npc = output.Npcs.Single();
        Require(npc.Keywords!.Any(link => link.FormKey == new FormKey(key, 0xE00)) &&
                npc.HeadTexture.FormKey == new FormKey(key, 0xE01) &&
                npc.SleepingOutfit.FormKey == new FormKey(key, 0xE02) &&
                npc.DefaultOutfit.FormKey == new FormKey(ModKey.FromNameAndExtension("EighthOutfit.esp"), 0x800),
            "Master append did not retain protected NPC links and the reviewed eighth-master outfit.");
    }

    private static void WriteMasterAppendFixture(SkyrimMod mod, string path) => mod.WriteToBinary(new FilePath(path),
        new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });
}
