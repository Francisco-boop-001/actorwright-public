using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimOutfitBinaryRoundTrip()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "outfit-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string basePath = Path.Combine(root, "Base.esm");
            string providerPath = Path.Combine(root, "Provider.esp");
            CreateBaseOutfit(basePath);
            CreateWinningOverride(providerPath);

            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath("F:\\ExampleGame"));
            var reader = new BethesdaPluginReader();
            var proposalService = new OutfitProposalService(reader, policy, labRoot);
            var writer = new BethesdaOutfitBinaryWriteService(policy, labRoot);
            FormReference armorItem = new(new PluginName("Base.esm"), new FormId(0x800));
            FormReference leveledItem = new(new PluginName("Base.esm"), new FormId(0x810));
            FormReference[] items = [armorItem, leveledItem];

            WorkspacePath newProposalPath = new(
                Path.Combine(root, "new.outfit-proposal.json"));
            OutfitProposalResult newProposal = await proposalService.ProposeAsync(
                new OutfitProposalRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(providerPath),
                    new FormId(0x900),
                    OutfitProposalMode.New,
                    new EditorId("NpcManager_NewOutfit"),
                    [.. items],
                    newProposalPath,
                    new FormId(0x901)),
                CancellationToken.None);
            Assert(newProposal.Written &&
                   newProposal.Artifact?.SourceOwnerPlugin == "Base.esm",
                "New outfit proposal did not retain the provider/owner distinction.");
            WorkspacePath newOutput = new(Path.Combine(root, "NewOutput.esp"));
            OutfitBinaryWriteResult newWrite = await writer.WriteAsync(
                new OutfitBinaryWriteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    newProposalPath,
                    newOutput),
                CancellationToken.None);
            Assert(newWrite.Written,
                "New Skyrim outfit binary write failed: " +
                string.Join("; ", newWrite.Diagnostics.Select(diagnostic => diagnostic.Message)));
            PluginRecordSummary newRecord = (await reader.ReadAsync(
                    new PluginReadRequest(GameEdition.SkyrimSpecialEdition, newOutput),
                    CancellationToken.None))
                .Records.Single(record => record.Signature == "OTFT");
            Assert(newRecord.OwnerPlugin == new PluginName("NewOutput.esp") &&
                   newRecord.FormId == new FormId(0x901) &&
                   newRecord.OutfitItemReferences.SequenceEqual(items),
                "New OTFT readback lost its owner, target FormID, or ordered ARMO/LVLI links.");

            WorkspacePath overrideProposalPath = new(
                Path.Combine(root, "override.outfit-proposal.json"));
            OutfitProposalResult overrideProposal = await proposalService.ProposeAsync(
                new OutfitProposalRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(providerPath),
                    new FormId(0x900),
                    OutfitProposalMode.Override,
                    null,
                    [.. items],
                    overrideProposalPath),
                CancellationToken.None);
            Assert(overrideProposal.Written &&
                   overrideProposal.Artifact?.SourceOwnerPlugin == "Base.esm",
                "Override proposal did not distinguish the winning provider from the Base.esm owner.");
            WorkspacePath overrideOutput = new(Path.Combine(root, "OverrideOutput.esp"));
            OutfitBinaryWriteResult overrideWrite = await writer.WriteAsync(
                new OutfitBinaryWriteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    overrideProposalPath,
                    overrideOutput),
                CancellationToken.None);
            Assert(overrideWrite.Written,
                "Override Skyrim outfit binary write failed: " +
                string.Join("; ", overrideWrite.Diagnostics.Select(diagnostic => diagnostic.Message)));
            PluginRecordSummary overrideRecord = (await reader.ReadAsync(
                    new PluginReadRequest(GameEdition.SkyrimSpecialEdition, overrideOutput),
                    CancellationToken.None))
                .Records.Single(record => record.Signature == "OTFT");
            Assert(overrideRecord.OwnerPlugin == new PluginName("Base.esm") &&
                   overrideRecord.FormId == new FormId(0x900) &&
                   overrideRecord.OutfitItemReferences.SequenceEqual(items),
                "Override OTFT readback lost the Base.esm owner or ordered ARMO/LVLI links.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void CreateBaseOutfit(string path)
        {
            ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var armor = new Armor(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
            {
                EditorID = "BaseArmor",
                Name = "Base armor"
            };
            mod.Armors.Add(armor);
            var leveled = new LeveledItem(
                new FormKey(key, 0x810), SkyrimRelease.SkyrimSE)
            {
                EditorID = "BaseLeveledArmor"
            };
            mod.LeveledItems.Add(leveled);
            mod.Outfits.Add(new Outfit(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
            {
                EditorID = "BaseOutfit",
                Items =
                [
                    new FormLink<IOutfitTargetGetter>(armor.FormKey),
                    new FormLink<IOutfitTargetGetter>(leveled.FormKey)
                ]
            });
            Write(mod, path);
        }

        static void CreateWinningOverride(string path)
        {
            ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
            ModKey owner = ModKey.FromNameAndExtension("Base.esm");
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = owner });
            mod.Outfits.Add(new Outfit(new FormKey(owner, 0x900), SkyrimRelease.SkyrimSE)
            {
                EditorID = "BaseOutfit",
                Items =
                [
                    new FormLink<IOutfitTargetGetter>(new FormKey(owner, 0x800)),
                    new FormLink<IOutfitTargetGetter>(new FormKey(owner, 0x810))
                ]
            });
            Write(mod, path);
        }

        static void Write(SkyrimMod mod, string path) =>
            mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }
}
