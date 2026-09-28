using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class SkyrimTestPluginFactory
{
    internal static void CreateGoldenSkyrimMaster(WorkspacePath outputPath)
    {
        var outputKey = ModKey.FromNameAndExtension(
            Path.GetFileName(outputPath.Value));
        var source = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        var female = new HeadData();
        female.TintMasks.Add(new TintAssets
        {
            Index = 0,
            FileName = new AssetLink<SkyrimTextureAssetType>(
                "actors/character/character assets/tintmasks/femalehead.dds"),
            MaskType = TintAssets.TintMaskType.SkinTone
        });
        var raceKey = new FormKey(outputKey, 0x013746);
        var skinKey = new FormKey(outputKey, 0x000900);
        var bodyAddonKey = new FormKey(outputKey, 0x000901);
        var handsAddonKey = new FormKey(outputKey, 0x000902);
        var feetAddonKey = new FormKey(outputKey, 0x000903);
        var bodyTextureKey = new FormKey(outputKey, 0x000904);
        var handsTextureKey = new FormKey(outputKey, 0x000905);
        var feetTextureKey = new FormKey(outputKey, 0x000906);
        source.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NordRace",
            Skin = new FormLinkNullable<IArmorGetter>(skinKey),
            HeadData = new GenderedItem<HeadData?>(
                new HeadData(),
                female)
        });
        var skin = new Armor(skinKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightGoldenNakedSkin",
            Race = new FormLinkNullable<IRaceGetter>(raceKey),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)(0x04 | 0x08 | 0x80)
            }
        };
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(bodyAddonKey));
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(handsAddonKey));
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(feetAddonKey));
        source.Armors.Add(skin);
        source.ArmorAddons.Add(GoldenSkinAddon(
            bodyAddonKey, raceKey, bodyTextureKey, 0x04, "Body"));
        source.ArmorAddons.Add(GoldenSkinAddon(
            handsAddonKey, raceKey, handsTextureKey, 0x08, "Hands"));
        source.ArmorAddons.Add(GoldenSkinAddon(
            feetAddonKey, raceKey, feetTextureKey, 0x80, "Feet"));
        source.TextureSets.Add(GoldenSkinTextureSet(
            bodyTextureKey, "body"));
        source.TextureSets.Add(GoldenSkinTextureSet(
            handsTextureKey, "hands"));
        source.TextureSets.Add(GoldenSkinTextureSet(
            feetTextureKey, "feet"));
        source.VoiceTypes.Add(new VoiceType(
            new FormKey(outputKey, 0x013ADC),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "FemaleYoungEager"
        });
        source.Classes.Add(new Mutagen.Bethesda.Skyrim.Class(
            new FormKey(outputKey, 0x013181),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "TrainerLightArmorExpert"
        });
        source.CombatStyles.Add(new CombatStyle(
            new FormKey(outputKey, 0x03BE1D),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "csHumanMeleeLvl1"
        });
        source.WriteToBinary(
            new FilePath(outputPath.Value),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }

    private static ArmorAddon GoldenSkinAddon(
        FormKey key,
        FormKey race,
        FormKey textureSet,
        uint slotMask,
        string suffix) => new(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightGolden" + suffix + "Skin",
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)slotMask
            },
            Race = new FormLinkNullable<IRaceGetter>(race),
            SkinTexture = new GenderedItem<
                IFormLinkNullableGetter<ITextureSetGetter>>(
                new FormLinkNullable<ITextureSetGetter>(textureSet),
                new FormLinkNullable<ITextureSetGetter>(textureSet))
        };

    private static TextureSet GoldenSkinTextureSet(
        FormKey key,
        string stem) => new(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightGolden" + stem + "SkinTexture",
            Diffuse = $"Actorwright/Skin/{stem}_d.dds",
            NormalOrGloss = $"Actorwright/Skin/{stem}_n.dds",
            GlowOrDetailMap = $"Actorwright/Skin/{stem}_sk.dds",
            BacklightMaskOrSpecular = $"Actorwright/Skin/{stem}_s.dds"
        };

    internal static void CreateGoldenNpcProviderWithHeadTexture(
        WorkspacePath templatePath,
        WorkspacePath outputPath,
        bool includeChargenMorph = false)
    {
        var templateKey = ModKey.FromNameAndExtension(
            Path.GetFileName(templatePath.Value));
        using var template = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(templateKey, new FilePath(templatePath.Value)),
            SkyrimRelease.SkyrimSE);
        var templateNpc = template.Npcs.Single(item =>
            item.FormKey == new FormKey(templateKey, 0x000800));
        var outputKey = ModKey.FromNameAndExtension(
            Path.GetFileName(outputPath.Value));
        var source = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        source.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        source.Npcs.DuplicateInAsNewRecord(
            templateNpc,
            new FormKey(outputKey, 0x000800));
        source.TextureSets.Add(new TextureSet(
            new FormKey(outputKey, 0x000801),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightGoldenHeadTexture",
            Diffuse = @"Actors\Character\Actorwright\Head.dds",
            NormalOrGloss = @"Actors\Character\Actorwright\Head_msn.dds",
            GlowOrDetailMap = @"Actors\Character\Actorwright\Head_sk.dds",
            Height = @"Actors\Character\Male\BlankDetailmap.dds",
            BacklightMaskOrSpecular =
                @"Actors\Character\Actorwright\Head_s.dds"
        });
        var face = new HeadPart(
            new FormKey(outputKey, 0x000802),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightGoldenFemaleFace",
            Type = HeadPart.TypeEnum.Face,
            Flags = HeadPart.Flag.Playable | HeadPart.Flag.Female,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    "Actors/Character/Actorwright/Head.nif")
            }
        };
        face.Parts.Add(new Part
        {
            PartType = Part.PartTypeEnum.Tri,
            FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                "Actors/Character/Actorwright/Head.tri")
        });
        if (includeChargenMorph)
            face.Parts.Add(new Part
            {
                PartType = (Part.PartTypeEnum)NpcManager.Application.SkyrimHdptTriRole.CharGen,
                FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                    "Actors/Character/Actorwright/Head.tri")
            });
        source.HeadParts.Add(face);
        source.WriteToBinary(
            new FilePath(outputPath.Value),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }

    internal static void CreateSourceWithMaster(
        WorkspacePath templatePath,
        WorkspacePath outputPath)
    {
        var templateKey = ModKey.FromNameAndExtension(Path.GetFileName(templatePath.Value));
        using var template = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(templateKey, new FilePath(templatePath.Value)),
            SkyrimRelease.SkyrimSE);
        var templateNpc = template.Npcs.Single(item =>
            item.FormKey == new FormKey(templateKey, 0x000800));
        var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(outputPath.Value));
        var source = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        source.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        source.Npcs.DuplicateInAsNewRecord(
            templateNpc,
            new FormKey(outputKey, 0x000800));
        source.WriteToBinary(new FilePath(outputPath.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    internal static void CreateCollectionSource(
        WorkspacePath templatePath,
        WorkspacePath outputPath)
    {
        var templateKey = ModKey.FromNameAndExtension(Path.GetFileName(templatePath.Value));
        using var template = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(templateKey, new FilePath(templatePath.Value)),
            SkyrimRelease.SkyrimSE);
        var templateNpc = template.Npcs.Single(item =>
            item.FormKey == new FormKey(templateKey, 0x000800));
        var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(outputPath.Value));
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var source = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        source.ModHeader.MasterReferences.Add(new MasterReference { Master = skyrim });
        var npc = source.Npcs.DuplicateInAsNewRecord(
            templateNpc,
            new FormKey(outputKey, 0x000800));

        npc.Keywords =
        [
            new FormLink<IKeywordGetter>(new FormKey(skyrim, 0x00013794))
        ];
        npc.Factions.Clear();
        npc.Factions.Add(new RankPlacement
        {
            Faction = new FormLink<IFactionGetter>(new FormKey(skyrim, 0x0005A1A4)),
            Rank = -1
        });
        npc.Items =
        [
            new ContainerEntry
            {
                Item = new ContainerItem
                {
                    Item = new FormLink<IItemGetter>(new FormKey(skyrim, 0x0000000F)),
                    Count = 5
                }
            }
        ];
        npc.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(
            new FormKey(skyrim, 0x0001697B));
        npc.SleepingOutfit = new FormLinkNullable<IOutfitGetter>();
        npc.Perks =
        [
            new PerkPlacement
            {
                Perk = new FormLink<IPerkGetter>(new FormKey(skyrim, 0x00058F6A)),
                Rank = 1
            }
        ];
        npc.ActorEffect =
        [
            new FormLink<ISpellRecordGetter>(new FormKey(skyrim, 0x00012FCD))
        ];

        source.WriteToBinary(new FilePath(outputPath.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    internal static void CreateIdentitySource(
        WorkspacePath templatePath,
        WorkspacePath outputPath)
    {
        var templateKey = ModKey.FromNameAndExtension(Path.GetFileName(templatePath.Value));
        using var template = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(templateKey, new FilePath(templatePath.Value)),
            SkyrimRelease.SkyrimSE);
        var templateNpc = template.Npcs.Single(item =>
            item.FormKey == new FormKey(templateKey, 0x000800));
        var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(outputPath.Value));
        var source = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        var race = new Race(new FormKey(outputKey, 0x000900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IdentityRace"
        };
        var alternateRace = new Race(
            new FormKey(outputKey, 0x000901), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IdentityAlternateRace"
        };
        var voice = new VoiceType(
            new FormKey(outputKey, 0x000902), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IdentityVoice"
        };
        var npcClass = new Mutagen.Bethesda.Skyrim.Class(
            new FormKey(outputKey, 0x000903), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IdentityClass"
        };
        var combatStyle = new CombatStyle(
            new FormKey(outputKey, 0x000904), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IdentityCombatStyle"
        };
        source.Races.Add(race);
        source.Races.Add(alternateRace);
        source.VoiceTypes.Add(voice);
        source.Classes.Add(npcClass);
        source.CombatStyles.Add(combatStyle);
        var npc = source.Npcs.DuplicateInAsNewRecord(
            templateNpc,
            new FormKey(outputKey, 0x000800));
        npc.EditorID = "IdentitySourceNpc";
        npc.Name = "Identity Source NPC";
        npc.ShortName = "Source short";
        npc.Race = new FormLink<IRaceGetter>(race.FormKey);
        npc.Voice = new FormLinkNullable<IVoiceTypeGetter>(voice.FormKey);
        npc.Class = new FormLink<IClassGetter>(npcClass.FormKey);
        npc.CombatStyle = new FormLinkNullable<ICombatStyleGetter>();

        source.WriteToBinary(new FilePath(outputPath.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }
}
