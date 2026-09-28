using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static int EmitArmorProductionFixture(
        string destination,
        bool includeArmorAddonProvider)
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        var projectWork = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work"));
        WorkspacePath root;
        try { root = new WorkspacePath(destination); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        if (!root.IsUnder(projectWork) || Directory.Exists(root.Value) ||
            File.Exists(root.Value))
        {
            Console.Error.WriteLine(
                "The Armor fixture destination must be a fresh path under the project 03-builds/work root.");
            return 1;
        }

        try
        {
            string phase1 = Path.Combine(root.Value, "phase1");
            string phase1Data = Path.Combine(phase1, "Data");
            string phase2 = Path.Combine(root.Value, "phase2");
            string phase2Data = Path.Combine(phase2, "Data");
            Directory.CreateDirectory(phase1Data);
            Directory.CreateDirectory(phase2Data);

            string phase1Base = Path.Combine(phase1Data, "OutfitBase.esm");
            string phase1Source = Path.Combine(phase1Data, "Source.esp");
            string phase1Provider = Path.Combine(phase1Data, "OutfitProvider.esp");
            string phase1ArmorAddonProvider = Path.Combine(
                phase1Data, "Provider.esp");
            CreateOutfitProductionBase(phase1Base);
            WriteArmorSource(phase1Source);
            CreateOutfitProductionOverride(phase1Provider);
            if (includeArmorAddonProvider)
                WriteArmorAddonReferenceProvider(phase1ArmorAddonProvider);

            string phase2Base = Path.Combine(phase2Data, "OutfitBase.esm");
            string phase2Source = Path.Combine(phase2Data, "Source.esp");
            string phase2Provider = Path.Combine(
                phase2Data,
                "OutfitProviderAfter.esp");
            string phase2ArmorAddonProvider = Path.Combine(
                phase2Data, "Provider.esp");
            File.Copy(phase1Base, phase2Base);
            File.Copy(phase1Source, phase2Source);
            if (includeArmorAddonProvider)
                File.Copy(
                    phase1ArmorAddonProvider,
                    phase2ArmorAddonProvider);
            CreateOutfitProductionAfterGeneratedArmor(phase2Provider);

            WriteFixtureLoadOrder(
                Path.Combine(phase1, "load-order.json"),
                includeArmorAddonProvider
                    ? ["OutfitBase.esm", "Source.esp", "OutfitProvider.esp",
                        "Provider.esp"]
                    : ["OutfitBase.esm", "Source.esp", "OutfitProvider.esp"]);
            WriteFixtureLoadOrder(
                Path.Combine(phase2, "load-order.json"),
                includeArmorAddonProvider
                    ? ["OutfitBase.esm", "Source.esp", "Provider.esp",
                        "GeneratedArmor.esp", "OutfitProviderAfter.esp"]
                    : ["OutfitBase.esm", "Source.esp", "GeneratedArmor.esp",
                        "OutfitProviderAfter.esp"]);

            var expected = new
            {
                sourcePlugin = "Source.esp",
                sourceFormId = "0x00000A00",
                targetFormId = "0x00000B00",
                generatedPlugin = "GeneratedArmor.esp",
                generatedEditorId = "npcm_ARMO_SourceArmor",
                generatedName = "Generated travel armor",
                firstArmorAddon = "Source.esp|0x00000907",
                secondArmorAddon = "Source.esp|0x00000908",
                replacementArmorAddon = "Source.esp|0x0000090A",
                sourceMaleSkinTexture = "Source.esp|0x00000910",
                sourceFemaleSkinTexture = "Source.esp|0x00000911",
                sourceMaleSkinSwapList = "Source.esp|0x00000912",
                sourceFemaleSkinSwapList = "Source.esp|0x00000913",
                sourceFootstepSet = "Source.esp|0x00000914",
                sourceArtObject = "Source.esp|0x00000915",
                thirdRace = "Source.esp|0x00000916",
                firstKeyword = "Source.esp|0x00000906",
                secondKeyword = "Source.esp|0x00000909",
                outfitTemplatePlugin = "OutfitProviderAfter.esp",
                outfitTemplateFormId = "0x00000900",
                outfitTargetFormId = "0x00000A20",
                outfitEditorId = "npcm_OTFT_GeneratedArmor",
                phase1BaseSha256 = HashOutfitFile(phase1Base).Value,
                phase1SourceSha256 = HashArmorProductionFile(phase1Source).Value,
                phase1ProviderSha256 = HashOutfitFile(phase1Provider).Value,
                phase1ArmorAddonProviderSha256 = includeArmorAddonProvider
                    ? HashArmorProductionFile(phase1ArmorAddonProvider).Value
                    : null,
                phase2BaseSha256 = HashOutfitFile(phase2Base).Value,
                phase2SourceSha256 = HashArmorProductionFile(phase2Source).Value,
                phase2ArmorAddonProviderSha256 = includeArmorAddonProvider
                    ? HashArmorProductionFile(phase2ArmorAddonProvider).Value
                    : null,
                phase2ProviderSha256 = HashOutfitFile(phase2Provider).Value
            };
            File.WriteAllText(
                Path.Combine(root.Value, "expected.json"),
                JsonSerializer.Serialize(expected, OutfitFixtureJsonOptions));
            Console.WriteLine(root.Value);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void CreateOutfitProductionAfterGeneratedArmor(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        ModKey owner = ModKey.FromNameAndExtension("OutfitBase.esm");
        ModKey generatedArmor = ModKey.FromNameAndExtension("GeneratedArmor.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = owner });
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = generatedArmor
        });
        mod.Outfits.Add(new Outfit(new FormKey(owner, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProductionBaseOutfit",
            Items = []
        });
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }
}
