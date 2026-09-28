using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static void PrepareFinishOutfitRaceFixture(WorkspacePath copiedMaster, FormKey actorRace)
    {
        var mod = SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromNameAndExtension("Skyrim.esm"),
            new FilePath(copiedMaster.Value)), SkyrimRelease.SkyrimSE);
        var armature = mod.ArmorAddons.AddNew(new FormKey(mod.ModKey, 0xA10));
        armature.EditorID = "SyntheticExcludedArmature";
        var otherRace = new FormKey(mod.ModKey, actorRace.ID == 0x13746 ? 0x13740u : 0x13746u);
        if (!mod.Races.Any(row => row.FormKey == otherRace)) mod.Races.AddNew(otherRace).EditorID = "SyntheticOtherRace";
        armature.Race.SetTo(otherRace);
        var armor = mod.Armors.Single(row => row.FormKey == new FormKey(mod.ModKey, 0x900));
        armor.Armature.Clear();
        armor.Armature.Add(armature.FormKey);
        var outfit = mod.Outfits.AddNew(new FormKey(mod.ModKey, 0xA11));
        outfit.EditorID = "SyntheticExcludedOutfit";
        outfit.Items = [new FormLink<IOutfitTargetGetter>(armor.FormKey)];
        mod.WriteToBinary(new FilePath(copiedMaster.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static async Task<SkyrimNpcFinishCoreRequest> CheckFinishOutfitRaceRefusals(
        WorkspacePath root, WorkspacePath copiedMaster, SkyrimNpcFinishCoreRequest request)
    {
        var requestPath = Child(root, "excluded-finish-request.json");
        var proposalPath = Child(root, "excluded-finish-proposal.json");
        File.WriteAllBytes(requestPath.Value, SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root));
        var refused = await RunCliAsync(root, "npc", "finish", "analyze", "--json", "--request", requestPath.Value,
            "--request-sha256", HashFile(requestPath), "--proposal", proposalPath.Value);
        Require(refused.ExitCode != 0 && refused.Root.ToString().Contains("finish-core-outfit-armature-race-excluded", StringComparison.Ordinal) &&
            !File.Exists(proposalPath.Value), "Actual Finish analyze must refuse excluded armature before producing a proposal: " + refused.Root);
        var schema = await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", "outfit propose");
        Require(schema.ExitCode == 0 && schema.Root.ToString().Contains("actor-race", StringComparison.Ordinal),
            "Exact outfit propose schema must publish actor-race.");
        using var source = SkyrimMod.CreateFromBinaryOverlay(new ModPath(ModKey.FromNameAndExtension(request.Source.Plugin!.Value.Value),
            new FilePath(request.Source.PluginPath!.Value.Value)), SkyrimRelease.SkyrimSE);
        FormKey race = source.Npcs.Single().Race.FormKey;
        var actorRace = new FormReference(new PluginName(race.ModKey.ToString()), new FormId(race.ID));
        var outfitProposal = Child(root, "excluded.outfit-proposal.json");
        var outfitRefused = await RunCliAsync(root, "outfit", "propose", "--json", "--edition", "skyrimse",
            "--plugin", copiedMaster.Value, "--source", "0x00000A11", "--items", "Skyrim.esm|0x00000900",
            "--mode", "override", "--output", outfitProposal.Value, "--actor-race", actorRace.ToString());
        Require(outfitRefused.ExitCode != 0 && outfitRefused.Root.ToString().Contains("finish-core-outfit-armature-race-excluded", StringComparison.Ordinal) &&
            !File.Exists(outfitProposal.Value), "Actual outfit propose must enforce actor race: " + outfitRefused.Root);
        return request with { OutfitRacePolicy = SkyrimNpcFinishCoreOutfitRacePolicy.Clone };
    }

    private static void AssertFinishOutfitRaceOutput(WorkspacePath path, FormKey race)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(ModKey.FromNameAndExtension("PackagedNpc.esp"),
            new FilePath(path.Value)), SkyrimRelease.SkyrimSE);
        var outfit = mod.Outfits.Single(row => row.FormKey == mod.Npcs.Single().DefaultOutfit.FormKey);
        var armor = mod.Armors.Single(row => row.FormKey == outfit.Items!.Single().FormKey);
        var armature = mod.ArmorAddons.Single(row => row.FormKey == armor.Armature.Single().FormKey);
        Require(armor.FormKey.ModKey == mod.ModKey && armature.FormKey.ModKey == mod.ModKey &&
            armature.AdditionalRaces.Any(row => row.FormKey == race),
            "Actual CLI output outfit must reach an output-owned ARMO and ARMA admitting the actor race.");
    }
}
