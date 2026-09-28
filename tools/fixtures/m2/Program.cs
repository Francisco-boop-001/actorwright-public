using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using System.Drawing;

if (args.Length == 3 && string.Equals(args[0], "--archetype", StringComparison.Ordinal))
{
    GenerateArchetypeFallout4(args[1]);
    GenerateArchetypeSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--stats", StringComparison.Ordinal))
{
    GenerateStatsFallout4(args[1]);
    GenerateStatsSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--keywords", StringComparison.Ordinal))
{
    GenerateKeywordsFallout4(args[1]);
    GenerateKeywordsSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--factions", StringComparison.Ordinal))
{
    GenerateFactionsFallout4(args[1]);
    GenerateFactionsSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--inventory", StringComparison.Ordinal))
{
    GenerateInventoryFallout4(args[1]);
    GenerateInventorySkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--outfits", StringComparison.Ordinal))
{
    GenerateOutfitsFallout4(args[1]);
    GenerateOutfitsSkyrim(args[2]);
    return 0;
}

if (args.Length == 6 && string.Equals(args[0], "--verify-outfit", StringComparison.Ordinal))
{
    return VerifyOutfit(args[1], args[2], args[3], args[4], args[5]);
}

if (args.Length == 2 && string.Equals(args[0], "--leveled", StringComparison.Ordinal))
{
    GenerateLeveledFallout4(args[1]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--leveled-dual", StringComparison.Ordinal))
{
    GenerateLeveledFallout4(args[1]);
    GenerateLeveledSkyrim(args[2]);
    return 0;
}

if (args.Length == 14 && string.Equals(args[0], "--verify-leveled", StringComparison.Ordinal))
{
    return VerifyLeveled(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]);
}

if (args.Length == 9 && string.Equals(args[0], "--verify-armor", StringComparison.Ordinal))
{
    return VerifyArmor(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]);
}

if (args.Length == 14 && string.Equals(args[0], "--verify-arma", StringComparison.Ordinal))
{
    return VerifyArma(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]);
}

if (args.Length == 12 && string.Equals(args[0], "--verify-mswp", StringComparison.Ordinal))
{
    return VerifyMaterialSwap(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]);
}

if (args.Length == 3 && string.Equals(args[0], "--armor", StringComparison.Ordinal))
{
    GenerateArmorFallout4(args[1]);
    GenerateArmorSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--arma", StringComparison.Ordinal))
{
    GenerateArmorAddonFallout4(args[1]);
    GenerateArmorAddonSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--mswp", StringComparison.Ordinal))
{
    GenerateMaterialSwapFallout4(args[1]);
    GenerateMaterialSwapSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--perks", StringComparison.Ordinal))
{
    GeneratePerksFallout4(args[1]);
    GeneratePerksSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--actor-effects", StringComparison.Ordinal))
{
    GenerateActorEffectsFallout4(args[1]);
    GenerateActorEffectsSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--properties", StringComparison.Ordinal))
{
    GeneratePropertiesFallout4(args[1]);
    GeneratePropertiesSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--templates", StringComparison.Ordinal))
{
    GenerateTemplatesFallout4(args[1]);
    GenerateTemplatesSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--templates-unsupported", StringComparison.Ordinal))
{
    GenerateTemplatesFallout4(args[1], includeUnsupported: true);
    GenerateTemplatesSkyrim(args[2], includeUnsupported: true);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--templates-cycle", StringComparison.Ordinal))
{
    GenerateTemplateCycleFallout4(args[1]);
    GenerateTemplateCycleSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--templates-missing", StringComparison.Ordinal))
{
    GenerateTemplateMissingFallout4(args[1]);
    GenerateTemplateMissingSkyrim(args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--headparts", StringComparison.Ordinal))
{
    GenerateHeadpartsFallout4(args[1]);
    GenerateHeadpartsSkyrim(args[2]);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--face-tints", StringComparison.Ordinal))
{
    GenerateFaceTintsFallout4(args[1]);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--face-tint-provider", StringComparison.Ordinal))
{
    GenerateFaceTintProviderFallout4(args[1]);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--sse-morphs", StringComparison.Ordinal))
{
    GenerateSseMorphs(args[1]);
    return 0;
}

if (args.Length == 2 && string.Equals(args[0], "--sse-tints", StringComparison.Ordinal))
{
    GenerateSseTints(args[1]);
    return 0;
}

if (args.Length == 2 && string.Equals(
        args[0],
        "--sky-gui-011-012-face-edit",
        StringComparison.Ordinal))
{
    try
    {
        GenerateCompleteFaceEditSkyrim(args[1]);
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

if (args.Length == 2 && string.Equals(
        args[0],
        "--sky-gui-014-selective-paste",
        StringComparison.Ordinal))
{
    try
    {
        GenerateSelectivePasteSkyrim(args[1]);
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

if (args.Length == 3 && string.Equals(args[0], "--sky-gui-025-partial", StringComparison.Ordinal))
{
    GenerateSkyGui025Partial(args[1], args[2]);
    return 0;
}

if (args.Length == 3 && string.Equals(args[0], "--sky-gui-025-cancellation", StringComparison.Ordinal))
{
    GenerateSkyGui025Cancellation(args[1], args[2]);
    return 0;
}

if (args.Length != 2) return 2;
Directory.CreateDirectory(Path.GetDirectoryName(args[0])!);
Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);

var sse = new SkyrimMod(new ModKey("M2FixtureSSE", ModType.Plugin), SkyrimRelease.SkyrimSE);
sse.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(sse.ModKey, 0x800), SkyrimRelease.SkyrimSE)
{
    EditorID = "M2FixtureSseNpc",
    Name = "M2 SSE Fixture",
    Weight = 50
});
sse.WriteToBinary(new FilePath(args[0]));

var fo4 = new Fallout4Mod(new ModKey("M2FixtureFO4", ModType.Plugin), Fallout4Release.Fallout4);
fo4.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(fo4.ModKey, 0x800), Fallout4Release.Fallout4)
{
    EditorID = "M2FixtureFo4Npc",
    Name = "M2 FO4 Fixture"
});
fo4.WriteToBinary(new FilePath(args[1]));
return 0;

static void GenerateArchetypeFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey("M3ArchetypeFO4", ModType.Plugin), Fallout4Release.Fallout4);
    var raceKey = new FormKey(mod.ModKey, 0x801);
    var voiceKey = new FormKey(mod.ModKey, 0x802);
    var classKey = new FormKey(mod.ModKey, 0x803);
    var combatStyleKey = new FormKey(mod.ModKey, 0x804);
    mod.Races.Add(new Mutagen.Bethesda.Fallout4.Race(raceKey, Fallout4Release.Fallout4) { EditorID = "M3RaceFO4" });
    mod.VoiceTypes.Add(new Mutagen.Bethesda.Fallout4.VoiceType(voiceKey, Fallout4Release.Fallout4) { EditorID = "M3VoiceFO4" });
    mod.Classes.Add(new Mutagen.Bethesda.Fallout4.Class(classKey, Fallout4Release.Fallout4) { EditorID = "M3ClassFO4" });
    mod.CombatStyles.Add(new Mutagen.Bethesda.Fallout4.CombatStyle(combatStyleKey, Fallout4Release.Fallout4) { EditorID = "M3CombatFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3ArchetypeNpcFO4",
        Name = "M3 Archetype FO4",
        Race = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IRaceGetter>(raceKey),
        Voice = new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IVoiceTypeGetter>(voiceKey),
        Class = new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IClassGetter>(classKey),
        CombatStyle = new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.ICombatStyleGetter>(combatStyleKey)
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateSkyGui025Partial(string inputPath, string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    ModKey modKey = ModKey.FromNameAndExtension(Path.GetFileName(inputPath));
    using var overlay = SkyrimMod.CreateFromBinaryOverlay(
        new ModPath(modKey, new FilePath(inputPath)), SkyrimRelease.SkyrimSE);
    var mod = (SkyrimMod)overlay.DeepCopy();
    for (uint index = 0; index < 12; index++)
    {
        var failing = new Mutagen.Bethesda.Skyrim.Npc(
            new FormKey(modKey, 0x805 + index), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"NativeTintMissingHeadPartNpc{index + 1}",
            Name = $"Native Tint Missing HeadPart {index + 1}",
            Configuration = new Mutagen.Bethesda.Skyrim.NpcConfiguration
            {
                Flags = Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Female
            },
            Race = new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(
                new FormKey(modKey, 0x801))
        };
        failing.HeadParts.Add(
            new FormLink<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(
                new FormKey(modKey, 0x900 + index)));
        mod.Npcs.Add(failing);
    }
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateSkyGui025Cancellation(string inputPath, string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    ModKey modKey = ModKey.FromNameAndExtension(Path.GetFileName(inputPath));
    using var overlay = SkyrimMod.CreateFromBinaryOverlay(
        new ModPath(modKey, new FilePath(inputPath)), SkyrimRelease.SkyrimSE);
    var mod = (SkyrimMod)overlay.DeepCopy();
    // Keep the real production bake active long enough for packaged-GUI
    // cancellation and title-bar close acceptance checks to observe it. A
    // 256-NPC fixture completed during the accessibility observation-to-click
    // handoff, so this acceptance fixture deliberately provides a wider window.
    for (uint index = 0; index < 4095; index++)
    {
        var npc = new Mutagen.Bethesda.Skyrim.Npc(
            new FormKey(modKey, 0x805 + index), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"NativeTintCancellationNpc{index + 1}",
            Name = $"Native Tint Cancellation {index + 1}",
            Configuration = new Mutagen.Bethesda.Skyrim.NpcConfiguration
            {
                Flags = Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Female
            },
            Race = new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(
                new FormKey(modKey, 0x801))
        };
        npc.HeadParts.Add(new FormLink<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(
            new FormKey(modKey, 0x804)));
        npc.TintLayers.Add(new Mutagen.Bethesda.Skyrim.TintLayer
        {
            Index = 1,
            Color = Color.FromArgb(255, 0, 0, 255),
            InterpolationValue = 1F,
            Preset = 0
        });
        mod.Npcs.Add(npc);
    }
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateArchetypeSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey("M3ArchetypeSSE", ModType.Plugin), SkyrimRelease.SkyrimSE);
    var raceKey = new FormKey(mod.ModKey, 0x801);
    var voiceKey = new FormKey(mod.ModKey, 0x802);
    var classKey = new FormKey(mod.ModKey, 0x803);
    var combatStyleKey = new FormKey(mod.ModKey, 0x804);
    mod.Races.Add(new Mutagen.Bethesda.Skyrim.Race(raceKey, SkyrimRelease.SkyrimSE) { EditorID = "M3RaceSSE" });
    mod.VoiceTypes.Add(new Mutagen.Bethesda.Skyrim.VoiceType(voiceKey, SkyrimRelease.SkyrimSE) { EditorID = "M3VoiceSSE" });
    mod.Classes.Add(new Mutagen.Bethesda.Skyrim.Class(classKey, SkyrimRelease.SkyrimSE) { EditorID = "M3ClassSSE" });
    mod.CombatStyles.Add(new Mutagen.Bethesda.Skyrim.CombatStyle(combatStyleKey, SkyrimRelease.SkyrimSE) { EditorID = "M3CombatSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3ArchetypeNpcSSE",
        Name = "M3 Archetype SSE",
        Race = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(raceKey),
        Voice = new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IVoiceTypeGetter>(voiceKey),
        Class = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IClassGetter>(classKey),
        CombatStyle = new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.ICombatStyleGetter>(combatStyleKey)
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateStatsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var npc = new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3StatsNpcFO4",
        Name = "M3 Stats FO4",
        Flags = Mutagen.Bethesda.Fallout4.Npc.Flag.Female | Mutagen.Bethesda.Fallout4.Npc.Flag.Unique |
            (Mutagen.Bethesda.Fallout4.Npc.Flag)0x40000000,
        Level = new Mutagen.Bethesda.Fallout4.NpcLevel { Level = 12 },
        XpValueOffset = 3,
        CalcMinLevel = 2,
        CalcMaxLevel = 40,
        DispositionBase = 5,
        BleedoutOverride = 7
    };
    mod.Npcs.Add(npc);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateStatsSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var npc = new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3StatsNpcSSE",
        Name = "M3 Stats SSE",
        Weight = 50,
        Height = 1.0f,
        Configuration = new Mutagen.Bethesda.Skyrim.NpcConfiguration
        {
            Flags = Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Unique |
                (Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag)0x40000000,
            Level = new Mutagen.Bethesda.Skyrim.NpcLevel { Level = 14 },
            MagickaOffset = 1,
            StaminaOffset = 2,
            HealthOffset = 3,
            CalcMinLevel = 2,
            CalcMaxLevel = 35,
            SpeedMultiplier = 100,
            DispositionBase = 4,
            BleedoutOverride = 6
        },
        PlayerSkills = new Mutagen.Bethesda.Skyrim.PlayerSkills { Health = 100, Magicka = 80, Stamina = 90 }
    };
    npc.PlayerSkills.SkillValues[Mutagen.Bethesda.Skyrim.Skill.OneHanded] = 15;
    npc.PlayerSkills.SkillOffsets[Mutagen.Bethesda.Skyrim.Skill.OneHanded] = 1;
    mod.Npcs.Add(npc);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateKeywordsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var keyword1 = new FormKey(mod.ModKey, 0x801); var keyword2 = new FormKey(mod.ModKey, 0x802); var attach = new FormKey(mod.ModKey, 0x803);
    mod.Keywords.Add(new Mutagen.Bethesda.Fallout4.Keyword(keyword1, Fallout4Release.Fallout4) { EditorID = "M3KeywordOneFO4" });
    mod.Keywords.Add(new Mutagen.Bethesda.Fallout4.Keyword(keyword2, Fallout4Release.Fallout4) { EditorID = "M3KeywordTwoFO4" });
    mod.Keywords.Add(new Mutagen.Bethesda.Fallout4.Keyword(attach, Fallout4Release.Fallout4) { EditorID = "M3AttachFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3KeywordsNpcFO4",
        Name = "M3 Keywords FO4",
        Keywords = [new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IKeywordGetter>(keyword1)],
        AttachParentSlots = [new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IKeywordGetter>(attach)]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateKeywordsSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var keyword1 = new FormKey(mod.ModKey, 0x801); var keyword2 = new FormKey(mod.ModKey, 0x802);
    mod.Keywords.Add(new Mutagen.Bethesda.Skyrim.Keyword(keyword1, SkyrimRelease.SkyrimSE) { EditorID = "M3KeywordOneSSE" });
    mod.Keywords.Add(new Mutagen.Bethesda.Skyrim.Keyword(keyword2, SkyrimRelease.SkyrimSE) { EditorID = "M3KeywordTwoSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3KeywordsNpcSSE",
        Name = "M3 Keywords SSE",
        Keywords = [new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IKeywordGetter>(keyword1)]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateFactionsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var faction1 = new FormKey(mod.ModKey, 0x801); var faction2 = new FormKey(mod.ModKey, 0x802); var faction3 = new FormKey(mod.ModKey, 0x803);
    mod.Factions.Add(new Mutagen.Bethesda.Fallout4.Faction(faction1, Fallout4Release.Fallout4) { EditorID = "M3FactionOneFO4" });
    mod.Factions.Add(new Mutagen.Bethesda.Fallout4.Faction(faction2, Fallout4Release.Fallout4) { EditorID = "M3FactionTwoFO4" });
    mod.Factions.Add(new Mutagen.Bethesda.Fallout4.Faction(faction3, Fallout4Release.Fallout4) { EditorID = "M3FactionThreeFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3FactionsNpcFO4",
        Name = "M3 Factions FO4",
        Factions = [
            new Mutagen.Bethesda.Fallout4.RankPlacement { Faction = new FormLink<Mutagen.Bethesda.Fallout4.IFactionGetter>(faction1), Rank = 1 },
            new Mutagen.Bethesda.Fallout4.RankPlacement { Faction = new FormLink<Mutagen.Bethesda.Fallout4.IFactionGetter>(faction2), Rank = -2 }
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateFactionsSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var faction1 = new FormKey(mod.ModKey, 0x801); var faction2 = new FormKey(mod.ModKey, 0x802); var faction3 = new FormKey(mod.ModKey, 0x803);
    mod.Factions.Add(new Mutagen.Bethesda.Skyrim.Faction(faction1, SkyrimRelease.SkyrimSE) { EditorID = "M3FactionOneSSE" });
    mod.Factions.Add(new Mutagen.Bethesda.Skyrim.Faction(faction2, SkyrimRelease.SkyrimSE) { EditorID = "M3FactionTwoSSE" });
    mod.Factions.Add(new Mutagen.Bethesda.Skyrim.Faction(faction3, SkyrimRelease.SkyrimSE) { EditorID = "M3FactionThreeSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3FactionsNpcSSE",
        Name = "M3 Factions SSE",
        Factions = [
            new Mutagen.Bethesda.Skyrim.RankPlacement { Faction = new FormLink<Mutagen.Bethesda.Skyrim.IFactionGetter>(faction1), Rank = 1 },
            new Mutagen.Bethesda.Skyrim.RankPlacement { Faction = new FormLink<Mutagen.Bethesda.Skyrim.IFactionGetter>(faction2), Rank = -2 }
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateInventoryFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var item1 = new FormKey(mod.ModKey, 0x801);
    var item2 = new FormKey(mod.ModKey, 0x802);
    var npc = new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3InventoryNpcFO4",
        Name = "M3 Inventory FO4",
        Items =
        [
            new Mutagen.Bethesda.Fallout4.ContainerEntry
            {
                Item = new Mutagen.Bethesda.Fallout4.ContainerItem
                {
                    Item = new FormLink<Mutagen.Bethesda.Fallout4.IItemGetter>(item1),
                    Count = 2
                }
            },
            new Mutagen.Bethesda.Fallout4.ContainerEntry
            {
                Item = new Mutagen.Bethesda.Fallout4.ContainerItem
                {
                    Item = new FormLink<Mutagen.Bethesda.Fallout4.IItemGetter>(item2),
                    Count = -1
                }
            }
        ]
    };
    mod.Npcs.Add(npc);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateInventorySkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var item1 = new FormKey(mod.ModKey, 0x801);
    var item2 = new FormKey(mod.ModKey, 0x802);
    var npc = new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3InventoryNpcSSE",
        Name = "M3 Inventory SSE",
        Items =
        [
            new Mutagen.Bethesda.Skyrim.ContainerEntry
            {
                Item = new Mutagen.Bethesda.Skyrim.ContainerItem
                {
                    Item = new FormLink<Mutagen.Bethesda.Skyrim.IItemGetter>(item1),
                    Count = 2
                }
            },
            new Mutagen.Bethesda.Skyrim.ContainerEntry
            {
                Item = new Mutagen.Bethesda.Skyrim.ContainerItem
                {
                    Item = new FormLink<Mutagen.Bethesda.Skyrim.IItemGetter>(item2),
                    Count = -1
                }
            }
        ]
    };
    mod.Npcs.Add(npc);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateOutfitsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var defaultOutfit = new FormKey(mod.ModKey, 0x801);
    var sleepOutfit = new FormKey(mod.ModKey, 0x802);
    var armor = new FormKey(mod.ModKey, 0x803);
    mod.Armors.Add(new Mutagen.Bethesda.Fallout4.Armor(armor, Fallout4Release.Fallout4) { EditorID = "M3OutfitArmorFO4" });
    mod.Outfits.Add(new Mutagen.Bethesda.Fallout4.Outfit(defaultOutfit, Fallout4Release.Fallout4)
    {
        EditorID = "M3DefaultOutfitFO4",
        Items = [new FormLink<Mutagen.Bethesda.Fallout4.IOutfitTargetGetter>(armor)]
    });
    mod.Outfits.Add(new Mutagen.Bethesda.Fallout4.Outfit(sleepOutfit, Fallout4Release.Fallout4) { EditorID = "M3SleepOutfitFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3OutfitsNpcFO4",
        Name = "M3 Outfits FO4",
        DefaultOutfit = new FormLinkNullable<Mutagen.Bethesda.Fallout4.IOutfitGetter>(defaultOutfit),
        SleepingOutfit = new FormLinkNullable<Mutagen.Bethesda.Fallout4.IOutfitGetter>(sleepOutfit)
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateOutfitsSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var defaultOutfit = new FormKey(mod.ModKey, 0x801);
    var sleepOutfit = new FormKey(mod.ModKey, 0x802);
    var armor = new FormKey(mod.ModKey, 0x803);
    mod.Armors.Add(new Mutagen.Bethesda.Skyrim.Armor(armor, SkyrimRelease.SkyrimSE) { EditorID = "M3OutfitArmorSSE" });
    mod.Outfits.Add(new Mutagen.Bethesda.Skyrim.Outfit(defaultOutfit, SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3DefaultOutfitSSE",
        Items = [new FormLink<Mutagen.Bethesda.Skyrim.IOutfitTargetGetter>(armor)]
    });
    mod.Outfits.Add(new Mutagen.Bethesda.Skyrim.Outfit(sleepOutfit, SkyrimRelease.SkyrimSE) { EditorID = "M3SleepOutfitSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3OutfitsNpcSSE",
        Name = "M3 Outfits SSE",
        DefaultOutfit = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>(defaultOutfit),
        SleepingOutfit = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>(sleepOutfit)
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static int VerifyOutfit(string edition, string path, string formIdText, string editorId,
    string itemFormIdText)
{
    if (!uint.TryParse(formIdText.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out var formId) ||
        !uint.TryParse(itemFormIdText.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out var itemFormId))
    {
        Console.Error.WriteLine("Invalid FormID argument.");
        return 2;
    }

    var modKey = new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    if (string.Equals(edition, "fallout4", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(path)), Fallout4Release.Fallout4);
        var outfits = mod.Outfits.ToArray();
        var outfit = outfits.SingleOrDefault(item => item.FormKey.ID == formId);
        if (outfits.Length != 1 || outfit is null || !string.Equals(outfit.EditorID, editorId, StringComparison.Ordinal) ||
            outfit.Items is null || outfit.Items.Count != 1 || outfit.Items[0].FormKey.ID != itemFormId)
        {
            Console.Error.WriteLine("FO4 OTFT binary verification failed.");
            return 1;
        }
    }
    else if (string.Equals(edition, "skyrimse", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(path)), SkyrimRelease.SkyrimSE);
        var outfits = mod.Outfits.ToArray();
        var outfit = outfits.SingleOrDefault(item => item.FormKey.ID == formId);
        if (outfits.Length != 1 || outfit is null || !string.Equals(outfit.EditorID, editorId, StringComparison.Ordinal) ||
            outfit.Items is null || outfit.Items.Count != 1 || outfit.Items[0].FormKey.ID != itemFormId)
        {
            Console.Error.WriteLine("SSE OTFT binary verification failed.");
            return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"Unsupported edition '{edition}'.");
        return 2;
    }

    Console.WriteLine($"OUTFIT BINARY INDEPENDENT PASS {path}");
    return 0;
}

static void GenerateLeveledFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    mod.LeveledItems.Add(new Mutagen.Bethesda.Fallout4.LeveledItem(new FormKey(mod.ModKey, 0x801), Fallout4Release.Fallout4)
    {
        EditorID = "M6SourceLeveledFO4",
        MaxCount = 2
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateLeveledSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    mod.LeveledItems.Add(new Mutagen.Bethesda.Skyrim.LeveledItem(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M6SourceLeveledSSE"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static int VerifyLeveled(string edition, string path, string listFormIdText, string editorId,
    string itemFormIdText, string levelText, string countText, string entryChanceText,
    string listChanceText, string maxCountText, string calcAllText, string calcEachText, string useAllText)
{
    uint listFormId = 0, itemFormId = 0;
    short level = 0, count = 0;
    byte entryChance = 0, listChance = 0, maxCount = 0;
    bool calcAll = false, calcEach = false, useAll = false;
    if (!TryParseFormId(listFormIdText, out listFormId) || !TryParseFormId(itemFormIdText, out itemFormId) ||
        !short.TryParse(levelText, out level) || !short.TryParse(countText, out count) ||
        !byte.TryParse(entryChanceText, out entryChance) || !byte.TryParse(listChanceText, out listChance) ||
        !byte.TryParse(maxCountText, out maxCount) || !bool.TryParse(calcAllText, out calcAll) ||
        !bool.TryParse(calcEachText, out calcEach) || !bool.TryParse(useAllText, out useAll)) return 2;
    if (string.Equals(edition, "fallout4", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), Fallout4Release.Fallout4);
        var records = mod.LeveledItems.ToArray();
        var record = records.FirstOrDefault(item => item.FormKey.ID == listFormId);
        var data = record?.Entries?.SingleOrDefault()?.Data;
        var expectedFlags = (calcAll ? Mutagen.Bethesda.Fallout4.LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) |
            (calcEach ? Mutagen.Bethesda.Fallout4.LeveledItem.Flag.CalculateForEachItemInCount : 0) |
            (useAll ? Mutagen.Bethesda.Fallout4.LeveledItem.Flag.UseAll : 0);
        if (records.Length != 1 || record is null || record.EditorID != editorId || record.MaxCount != maxCount ||
            record.ChanceNone != new Percent(listChance / 100d) || record.Flags != expectedFlags ||
            data is null || data.Reference.FormKey.ID != itemFormId || data.Level != level || data.Count != count || data.ChanceNone != new Percent(entryChance / 100d))
            return 1;
    }
    else if (string.Equals(edition, "skyrimse", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), SkyrimRelease.SkyrimSE);
        var records = mod.LeveledItems.ToArray();
        var record = records.FirstOrDefault(item => item.FormKey.ID == listFormId);
        var data = record?.Entries?.SingleOrDefault()?.Data;
        var expectedFlags = (calcAll ? Mutagen.Bethesda.Skyrim.LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) |
            (calcEach ? Mutagen.Bethesda.Skyrim.LeveledItem.Flag.CalculateForEachItemInCount : 0) |
            (useAll ? Mutagen.Bethesda.Skyrim.LeveledItem.Flag.UseAll : 0);
        if (records.Length != 1 || record is null || record.EditorID != editorId ||
            record.ChanceNone != new Percent(listChance / 100d) || record.Flags != expectedFlags ||
            data is null || data.Reference.FormKey.ID != itemFormId || data.Level != level || data.Count != count)
            return 1;
    }
    else return 2;
    Console.WriteLine($"LEVELED BINARY INDEPENDENT PASS {path}");
    return 0;
}

static int VerifyArmor(string edition, string path, string formIdText, string editorId,
    string valueText, string weightText, string ratingText, string healthText)
{
    if (!TryParseFormId(formIdText, out var formId) || !int.TryParse(valueText, out var value) ||
        !float.TryParse(weightText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var weight) ||
        !float.TryParse(ratingText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rating) ||
        !uint.TryParse(healthText, out var health)) return 2;
    if (string.Equals(edition, "fallout4", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), Fallout4Release.Fallout4);
        var records = mod.Armors.ToArray();
        var armor = records.SingleOrDefault(item => item.FormKey.ID == formId);
        if (records.Length != 1 || armor is null || armor.EditorID != editorId || armor.Value != value ||
            Math.Abs(armor.Weight - weight) >= 0.0001f || armor.ArmorRating != rating || armor.Health != health) return 1;
    }
    else if (string.Equals(edition, "skyrimse", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), SkyrimRelease.SkyrimSE);
        var records = mod.Armors.ToArray();
        var armor = records.SingleOrDefault(item => item.FormKey.ID == formId);
        if (records.Length != 1 || armor is null || armor.EditorID != editorId || armor.Value != (uint)value ||
            Math.Abs(armor.Weight - weight) >= 0.0001f || Math.Abs(armor.ArmorRating - rating) >= 0.0001f) return 1;
    }
    else return 2;
    Console.WriteLine($"ARMOR BINARY INDEPENDENT PASS {path}");
    return 0;
}

static int VerifyArma(string edition, string path, string formIdText, string editorId, string slotText,
    string detectionText, string weaponText, string? maleModel, string? femaleModel, string? maleFirstPerson,
    string? femaleFirstPerson, string malePriorityText, string femalePriorityText)
{
    maleModel = NullSentinel(maleModel);
    femaleModel = NullSentinel(femaleModel);
    maleFirstPerson = NullSentinel(maleFirstPerson);
    femaleFirstPerson = NullSentinel(femaleFirstPerson);
    if (!TryParseFormId(formIdText, out var formId) || !TryParseOptionalUInt(slotText, out var slot) ||
        !TryParseOptionalByte(detectionText, out var detection) ||
        !TryParseOptionalFloat(weaponText, out var weapon) ||
        !TryParseOptionalByte(malePriorityText, out var malePriority) || !TryParseOptionalByte(femalePriorityText, out var femalePriority)) return 2;
    if (string.Equals(edition, "fallout4", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), Fallout4Release.Fallout4);
        var records = mod.ArmorAddons.ToArray();
        var addon = records.SingleOrDefault(item => item.FormKey.ID == formId);
        if (records.Length != 1 || addon is null || addon.EditorID != editorId || (slot is not null && addon.BodyTemplate?.FirstPersonFlags != (Mutagen.Bethesda.Fallout4.BipedObjectFlag)slot.Value) ||
            (detection is not null && addon.DetectionSoundValue != detection.Value) || (weapon is not null && Math.Abs(addon.WeaponAdjust - weapon.Value) >= 0.0001f) || addon.WorldModel?.Male?.File != maleModel ||
            addon.WorldModel?.Female?.File != femaleModel || addon.FirstPersonModel?.Male?.File != maleFirstPerson || addon.FirstPersonModel?.Female?.File != femaleFirstPerson ||
            (malePriority is not null && addon.Priority?.Male != malePriority.Value) || (femalePriority is not null && addon.Priority?.Female != femalePriority.Value)) return 1;
    }
    else if (string.Equals(edition, "skyrimse", StringComparison.OrdinalIgnoreCase))
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), SkyrimRelease.SkyrimSE);
        var records = mod.ArmorAddons.ToArray();
        var addon = records.SingleOrDefault(item => item.FormKey.ID == formId);
        if (records.Length != 1 || addon is null || addon.EditorID != editorId || (slot is not null && addon.BodyTemplate?.FirstPersonFlags != (Mutagen.Bethesda.Skyrim.BipedObjectFlag)slot.Value) ||
            (detection is not null && addon.DetectionSoundValue != detection.Value) || (weapon is not null && Math.Abs(addon.WeaponAdjust - weapon.Value) >= 0.0001f) || addon.WorldModel?.Male?.File != maleModel ||
            addon.WorldModel?.Female?.File != femaleModel || addon.FirstPersonModel?.Male?.File != maleFirstPerson || addon.FirstPersonModel?.Female?.File != femaleFirstPerson ||
            (malePriority is not null && addon.Priority?.Male != malePriority.Value) || (femalePriority is not null && addon.Priority?.Female != femalePriority.Value)) return 1;
    }
    else return 2;
    Console.WriteLine($"ARMOR ADDON BINARY INDEPENDENT PASS {path}");
    return 0;
}

static int VerifyMaterialSwap(string edition, string path, string formIdText, string editorId, string? treeFolder,
    string originalOne, string replacementOne, string colorOne, string originalTwo, string replacementTwo, string colorTwo)
{
    treeFolder = NullSentinel(treeFolder);
    if (!TryParseFormId(formIdText, out var formId) || !TryParseOptionalFloat(colorOne, out var firstColor) || !TryParseOptionalFloat(colorTwo, out var secondColor)) return 2;
    if (!string.Equals(edition, "fallout4", StringComparison.OrdinalIgnoreCase)) return 2;
    using var mod = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin), new FilePath(path)), Fallout4Release.Fallout4);
    var records = mod.MaterialSwaps.ToArray();
    var swap = records.SingleOrDefault(item => item.FormKey.ID == formId);
    var expected = originalTwo is "-" && replacementTwo is "-"
        ? new[] { (originalOne, replacementOne, firstColor) }
        : new[] { (originalOne, replacementOne, firstColor), (originalTwo, replacementTwo, secondColor) };
    var offset = string.IsNullOrEmpty(treeFolder) ? 0 : 1;
    if (records.Length != 1 || swap is null || swap.EditorID != editorId || swap.TreeFolder != treeFolder || swap.Substitutions.Count != expected.Length + offset ||
        (offset == 1 && (swap.Substitutions[0].OriginalMaterial is not null || swap.Substitutions[0].ReplacementMaterial is not null || swap.Substitutions[0].ColorRemappingIndex is not null)) ||
        !swap.Substitutions.Skip(offset).Zip(expected).All(pair => pair.First.OriginalMaterial == pair.Second.Item1 && pair.First.ReplacementMaterial == pair.Second.Item2 &&
            (pair.Second.Item3 is null ? pair.First.ColorRemappingIndex is null : pair.First.ColorRemappingIndex is { } color && Math.Abs(color - pair.Second.Item3.Value) < 0.0001f)))
    {
        return 1;
    }
    Console.WriteLine($"MATERIAL SWAP BINARY INDEPENDENT PASS {path}");
    return 0;
}

static string? NullSentinel(string? value) => value is null or "-" or "null" ? null : value;
static bool TryParseOptionalUInt(string value, out uint? result) { if (value is "-") { result = null; return true; } if (uint.TryParse(value, out var parsed)) { result = parsed; return true; } result = null; return false; }
static bool TryParseOptionalByte(string value, out byte? result) { if (value is "-") { result = null; return true; } if (byte.TryParse(value, out var parsed)) { result = parsed; return true; } result = null; return false; }
static bool TryParseOptionalFloat(string value, out float? result) { if (value is "-") { result = null; return true; } if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) { result = parsed; return true; } result = null; return false; }

static bool TryParseFormId(string value, out uint formId) =>
    uint.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
        System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out formId);

static void GenerateArmorFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    mod.Armors.Add(new Mutagen.Bethesda.Fallout4.Armor(new FormKey(mod.ModKey, 0x801), Fallout4Release.Fallout4)
    {
        EditorID = "M6SourceArmorFO4"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateArmorSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    mod.Armors.Add(new Mutagen.Bethesda.Skyrim.Armor(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M6SourceArmorSSE"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateArmorAddonFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    mod.ArmorAddons.Add(new Mutagen.Bethesda.Fallout4.ArmorAddon(new FormKey(mod.ModKey, 0x801), Fallout4Release.Fallout4)
    {
        EditorID = "M6SourceArmorAddonFO4"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateArmorAddonSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    mod.ArmorAddons.Add(new Mutagen.Bethesda.Skyrim.ArmorAddon(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M6SourceArmorAddonSSE"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateMaterialSwapFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    mod.MaterialSwaps.Add(new Mutagen.Bethesda.Fallout4.MaterialSwap(new FormKey(mod.ModKey, 0x801), Fallout4Release.Fallout4)
    {
        EditorID = "M6SourceMaterialSwapFO4"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateMaterialSwapSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GeneratePerksFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var perk1 = new FormKey(mod.ModKey, 0x801);
    var perk2 = new FormKey(mod.ModKey, 0x802);
    var perk3 = new FormKey(mod.ModKey, 0x803);
    mod.Perks.Add(new Mutagen.Bethesda.Fallout4.Perk(perk1, Fallout4Release.Fallout4) { EditorID = "M3PerkOneFO4" });
    mod.Perks.Add(new Mutagen.Bethesda.Fallout4.Perk(perk2, Fallout4Release.Fallout4) { EditorID = "M3PerkTwoFO4" });
    mod.Perks.Add(new Mutagen.Bethesda.Fallout4.Perk(perk3, Fallout4Release.Fallout4) { EditorID = "M3PerkThreeFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3PerksNpcFO4",
        Name = "M3 Perks FO4",
        Perks =
        [
            new Mutagen.Bethesda.Fallout4.PerkPlacement
            {
                Perk = new FormLink<Mutagen.Bethesda.Fallout4.IPerkGetter>(perk1),
                Rank = 1
            },
            new Mutagen.Bethesda.Fallout4.PerkPlacement
            {
                Perk = new FormLink<Mutagen.Bethesda.Fallout4.IPerkGetter>(perk2),
                Rank = 255
            }
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GeneratePerksSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var perk1 = new FormKey(mod.ModKey, 0x801);
    var perk2 = new FormKey(mod.ModKey, 0x802);
    var perk3 = new FormKey(mod.ModKey, 0x803);
    mod.Perks.Add(new Mutagen.Bethesda.Skyrim.Perk(perk1, SkyrimRelease.SkyrimSE) { EditorID = "M3PerkOneSSE" });
    mod.Perks.Add(new Mutagen.Bethesda.Skyrim.Perk(perk2, SkyrimRelease.SkyrimSE) { EditorID = "M3PerkTwoSSE" });
    mod.Perks.Add(new Mutagen.Bethesda.Skyrim.Perk(perk3, SkyrimRelease.SkyrimSE) { EditorID = "M3PerkThreeSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3PerksNpcSSE",
        Name = "M3 Perks SSE",
        Perks =
        [
            new Mutagen.Bethesda.Skyrim.PerkPlacement
            {
                Perk = new FormLink<Mutagen.Bethesda.Skyrim.IPerkGetter>(perk1),
                Rank = 1
            },
            new Mutagen.Bethesda.Skyrim.PerkPlacement
            {
                Perk = new FormLink<Mutagen.Bethesda.Skyrim.IPerkGetter>(perk2),
                Rank = 255
            }
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateActorEffectsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var spell1 = new FormKey(mod.ModKey, 0x801);
    var spell2 = new FormKey(mod.ModKey, 0x802);
    var spell3 = new FormKey(mod.ModKey, 0x803);
    mod.Spells.Add(new Mutagen.Bethesda.Fallout4.Spell(spell1, Fallout4Release.Fallout4) { EditorID = "M3SpellOneFO4" });
    mod.Spells.Add(new Mutagen.Bethesda.Fallout4.Spell(spell2, Fallout4Release.Fallout4) { EditorID = "M3SpellTwoFO4" });
    mod.Spells.Add(new Mutagen.Bethesda.Fallout4.Spell(spell3, Fallout4Release.Fallout4) { EditorID = "M3SpellThreeFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3ActorEffectsNpcFO4",
        Name = "M3 Actor Effects FO4",
        ActorEffect =
        [
            new FormLink<Mutagen.Bethesda.Fallout4.ISpellRecordGetter>(spell1),
            new FormLink<Mutagen.Bethesda.Fallout4.ISpellRecordGetter>(spell2)
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateActorEffectsSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var spell1 = new FormKey(mod.ModKey, 0x801);
    var spell2 = new FormKey(mod.ModKey, 0x802);
    var spell3 = new FormKey(mod.ModKey, 0x803);
    mod.Spells.Add(new Mutagen.Bethesda.Skyrim.Spell(spell1, SkyrimRelease.SkyrimSE) { EditorID = "M3SpellOneSSE" });
    mod.Spells.Add(new Mutagen.Bethesda.Skyrim.Spell(spell2, SkyrimRelease.SkyrimSE) { EditorID = "M3SpellTwoSSE" });
    mod.Spells.Add(new Mutagen.Bethesda.Skyrim.Spell(spell3, SkyrimRelease.SkyrimSE) { EditorID = "M3SpellThreeSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3ActorEffectsNpcSSE",
        Name = "M3 Actor Effects SSE",
        ActorEffect =
        [
            new FormLink<Mutagen.Bethesda.Skyrim.ISpellRecordGetter>(spell1),
            new FormLink<Mutagen.Bethesda.Skyrim.ISpellRecordGetter>(spell2)
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GeneratePropertiesFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var av1 = new FormKey(mod.ModKey, 0x801);
    var av2 = new FormKey(mod.ModKey, 0x802);
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3PropertiesNpcFO4",
        Name = "M3 Properties FO4",
        Properties =
        [
            new Mutagen.Bethesda.Fallout4.ObjectProperty
            {
                ActorValue = new FormLink<Mutagen.Bethesda.Fallout4.IActorValueInformationGetter>(av1),
                Value = 1.25f
            },
            new Mutagen.Bethesda.Fallout4.ObjectProperty
            {
                ActorValue = new FormLink<Mutagen.Bethesda.Fallout4.IActorValueInformationGetter>(av2),
                Value = -2.5f
            }
        ]
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GeneratePropertiesSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3PropertiesNpcSSE",
        Name = "M3 Properties SSE"
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateTemplatesFallout4(string outputPath, bool includeUnsupported = false)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var sourceKey = new FormKey(mod.ModKey, 0x801);
    var factionKey = new FormKey(mod.ModKey, 0x810);
    var keywordKey = new FormKey(mod.ModKey, 0x811);
    var spellKey = new FormKey(mod.ModKey, 0x812);
    mod.Factions.Add(new Mutagen.Bethesda.Fallout4.Faction(factionKey, Fallout4Release.Fallout4) { EditorID = "M3TemplateFactionFO4" });
    mod.Keywords.Add(new Mutagen.Bethesda.Fallout4.Keyword(keywordKey, Fallout4Release.Fallout4) { EditorID = "M3TemplateKeywordFO4" });
    mod.Spells.Add(new Mutagen.Bethesda.Fallout4.Spell(spellKey, Fallout4Release.Fallout4) { EditorID = "M3TemplateSpellFO4" });
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(sourceKey, Fallout4Release.Fallout4)
    {
        EditorID = "M3TemplateSourceFO4",
        Level = new Mutagen.Bethesda.Fallout4.NpcLevel { Level = 19 },
        CalcMinLevel = 4,
        CalcMaxLevel = 55,
        Factions = [new Mutagen.Bethesda.Fallout4.RankPlacement { Faction = new FormLink<Mutagen.Bethesda.Fallout4.IFactionGetter>(factionKey), Rank = 2 }],
        ActorEffect = [new FormLink<Mutagen.Bethesda.Fallout4.ISpellRecordGetter>(spellKey)],
        Keywords = [new FormLink<Mutagen.Bethesda.Fallout4.IKeywordGetter>(keywordKey)]
    });
    var templateLink = new FormLink<Mutagen.Bethesda.Fallout4.INpcSpawnGetter>(sourceKey);
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "M3TemplateTargetFO4",
        UseTemplateActors = Mutagen.Bethesda.Fallout4.Npc.TemplateActorType.Stats |
            Mutagen.Bethesda.Fallout4.Npc.TemplateActorType.Factions |
            Mutagen.Bethesda.Fallout4.Npc.TemplateActorType.SpellList |
            Mutagen.Bethesda.Fallout4.Npc.TemplateActorType.Keywords |
            (includeUnsupported ? Mutagen.Bethesda.Fallout4.Npc.TemplateActorType.Traits : 0),
        TemplateActors = new Mutagen.Bethesda.Fallout4.TemplateActors
        {
            StatsTemplate = templateLink,
            FactionsTemplate = templateLink,
            SpellListTemplate = templateLink,
            KeywordsTemplate = templateLink,
            TraitTemplate = templateLink
        }
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateTemplatesSkyrim(string outputPath, bool includeUnsupported = false)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var sourceKey = new FormKey(mod.ModKey, 0x801);
    var factionKey = new FormKey(mod.ModKey, 0x810);
    var keywordKey = new FormKey(mod.ModKey, 0x811);
    var spellKey = new FormKey(mod.ModKey, 0x812);
    mod.Factions.Add(new Mutagen.Bethesda.Skyrim.Faction(factionKey, SkyrimRelease.SkyrimSE) { EditorID = "M3TemplateFactionSSE" });
    mod.Keywords.Add(new Mutagen.Bethesda.Skyrim.Keyword(keywordKey, SkyrimRelease.SkyrimSE) { EditorID = "M3TemplateKeywordSSE" });
    mod.Spells.Add(new Mutagen.Bethesda.Skyrim.Spell(spellKey, SkyrimRelease.SkyrimSE) { EditorID = "M3TemplateSpellSSE" });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(sourceKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3TemplateSourceSSE",
        Weight = 42,
        Configuration = new Mutagen.Bethesda.Skyrim.NpcConfiguration
        {
            Level = new Mutagen.Bethesda.Skyrim.NpcLevel { Level = 21 },
            CalcMinLevel = 5,
            CalcMaxLevel = 60
        },
        Factions = [new Mutagen.Bethesda.Skyrim.RankPlacement { Faction = new FormLink<Mutagen.Bethesda.Skyrim.IFactionGetter>(factionKey), Rank = 3 }],
        ActorEffect = [new FormLink<Mutagen.Bethesda.Skyrim.ISpellRecordGetter>(spellKey)],
        Keywords = [new FormLink<Mutagen.Bethesda.Skyrim.IKeywordGetter>(keywordKey)]
    });
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "M3TemplateTargetSSE",
        Template = new FormLinkNullable<Mutagen.Bethesda.Skyrim.INpcSpawnGetter>(sourceKey),
        Configuration = new Mutagen.Bethesda.Skyrim.NpcConfiguration
        {
            TemplateFlags = Mutagen.Bethesda.Skyrim.NpcConfiguration.TemplateFlag.Stats |
                Mutagen.Bethesda.Skyrim.NpcConfiguration.TemplateFlag.Factions |
                Mutagen.Bethesda.Skyrim.NpcConfiguration.TemplateFlag.SpellList |
                Mutagen.Bethesda.Skyrim.NpcConfiguration.TemplateFlag.Keywords |
                (includeUnsupported ? Mutagen.Bethesda.Skyrim.NpcConfiguration.TemplateFlag.Traits : 0)
        }
    });
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateTemplateCycleFallout4(string outputPath)
{
    GenerateTemplatesFallout4(outputPath);
    Fallout4Mod mod;
    using (var overlay = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), new FilePath(outputPath)), Fallout4Release.Fallout4))
        mod = (Fallout4Mod)overlay.DeepCopy();
    var source = mod.Npcs[new FormKey(mod.ModKey, 0x801)];
    var link = new FormLink<Mutagen.Bethesda.Fallout4.INpcSpawnGetter>(new FormKey(mod.ModKey, 0x800));
    source.UseTemplateActors = Mutagen.Bethesda.Fallout4.Npc.TemplateActorType.Stats;
    source.TemplateActors = new Mutagen.Bethesda.Fallout4.TemplateActors { StatsTemplate = link };
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateTemplateCycleSkyrim(string outputPath)
{
    GenerateTemplatesSkyrim(outputPath);
    SkyrimMod mod;
    using (var overlay = SkyrimMod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), new FilePath(outputPath)), SkyrimRelease.SkyrimSE))
        mod = (SkyrimMod)overlay.DeepCopy();
    var source = mod.Npcs[new FormKey(mod.ModKey, 0x801)];
    source.Configuration.TemplateFlags = Mutagen.Bethesda.Skyrim.NpcConfiguration.TemplateFlag.Stats;
    source.Template = new FormLinkNullable<Mutagen.Bethesda.Skyrim.INpcSpawnGetter>(new FormKey(mod.ModKey, 0x800));
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateTemplateMissingFallout4(string outputPath)
{
    GenerateTemplatesFallout4(outputPath);
    Fallout4Mod mod;
    using (var overlay = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), new FilePath(outputPath)), Fallout4Release.Fallout4))
        mod = (Fallout4Mod)overlay.DeepCopy();
    var target = mod.Npcs[new FormKey(mod.ModKey, 0x800)];
    target.TemplateActors = new Mutagen.Bethesda.Fallout4.TemplateActors();
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateTemplateMissingSkyrim(string outputPath)
{
    GenerateTemplatesSkyrim(outputPath);
    SkyrimMod mod;
    using (var overlay = SkyrimMod.CreateFromBinaryOverlay(new ModPath(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), new FilePath(outputPath)), SkyrimRelease.SkyrimSE))
        mod = (SkyrimMod)overlay.DeepCopy();
    var target = mod.Npcs[new FormKey(mod.ModKey, 0x800)];
    target.Template = new FormLinkNullable<Mutagen.Bethesda.Skyrim.INpcSpawnGetter>();
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateHeadpartsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var race = new FormKey(mod.ModKey, 0x801);
    var color = new FormKey(mod.ModKey, 0x802);
    var validRaces = new FormKey(mod.ModKey, 0x803);
    mod.Races.Add(new Mutagen.Bethesda.Fallout4.Race(race, Fallout4Release.Fallout4) { EditorID = "P04RaceFO4" });
    mod.Colors.Add(new Mutagen.Bethesda.Fallout4.ColorRecord(color, Fallout4Release.Fallout4) { EditorID = "P04HairColorFO4" });
    mod.FormLists.Add(new Mutagen.Bethesda.Fallout4.FormList(validRaces, Fallout4Release.Fallout4) { EditorID = "P04ValidRacesFO4", Items = [new FormLink<Mutagen.Bethesda.Fallout4.IRaceGetter>(race)] });
    var parts = new List<FormKey>();
    for (var type = 1; type <= 9; type++)
    {
        var key = new FormKey(mod.ModKey, (uint)(0x810 + type));
        parts.Add(key);
        var part = new Mutagen.Bethesda.Fallout4.HeadPart(key, Fallout4Release.Fallout4) { EditorID = $"P04Type{type}FO4", ValidRaces = new FormLinkNullable<Mutagen.Bethesda.Fallout4.IFormListGetter>(validRaces) };
        AddHeadPartModel(part, type, $"meshes\\p04\\{type}.nif");
        SetEnum(part, "Type", type); SetEnum(part, "Flags", 4); mod.HeadParts.Add(part);
        var assetPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "data", "meshes", "p04");
        Directory.CreateDirectory(assetPath);
        File.WriteAllBytes(Path.Combine(assetPath, $"{type}.nif"), [0x4E, 0x49, 0x46]);
    }
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "P04NpcFO4",
        Race = new FormLink<Mutagen.Bethesda.Fallout4.IRaceGetter>(race),
        HairColor = new FormLinkNullable<Mutagen.Bethesda.Fallout4.IColorRecordGetter>(color)
    });
    var npc = mod.Npcs[new FormKey(mod.ModKey, 0x800)]; SetEnum(npc, "Flags", 1); foreach (var key in parts) npc.HeadParts.Add(new FormLink<Mutagen.Bethesda.Fallout4.IHeadPartGetter>(key));
    mod.WriteToBinary(new FilePath(outputPath));
    InjectFo4Models(outputPath);
}

static void GenerateHeadpartsSkyrim(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    var race = new FormKey(mod.ModKey, 0x801);
    var color = new FormKey(mod.ModKey, 0x802);
    var validRaces = new FormKey(mod.ModKey, 0x803);
    mod.Races.Add(new Mutagen.Bethesda.Skyrim.Race(race, SkyrimRelease.SkyrimSE) { EditorID = "P04RaceSSE" });
    mod.Colors.Add(new Mutagen.Bethesda.Skyrim.ColorRecord(color, SkyrimRelease.SkyrimSE) { EditorID = "P04HairColorSSE" });
    mod.FormLists.Add(new Mutagen.Bethesda.Skyrim.FormList(validRaces, SkyrimRelease.SkyrimSE) { EditorID = "P04ValidRacesSSE", Items = [new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(race)] });
    var parts = new List<FormKey>();
    for (var type = 1; type <= 9; type++)
    {
        var key = new FormKey(mod.ModKey, (uint)(0x810 + type));
        parts.Add(key);
        var part = new Mutagen.Bethesda.Skyrim.HeadPart(key, SkyrimRelease.SkyrimSE) { EditorID = $"P04Type{type}SSE", ValidRaces = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IFormListGetter>(validRaces), Model = new Mutagen.Bethesda.Skyrim.Model { File = $"meshes\\p04\\{type}.nif" } };
        SetEnum(part, "Type", type); SetEnum(part, "Flags", 4); mod.HeadParts.Add(part);
        var assetPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "data", "meshes", "p04");
        Directory.CreateDirectory(assetPath);
        File.WriteAllBytes(Path.Combine(assetPath, $"{type}.nif"), [0x4E, 0x49, 0x46]);
    }
    var alternateHair = new Mutagen.Bethesda.Skyrim.HeadPart(
        new FormKey(mod.ModKey, 0x823), SkyrimRelease.SkyrimSE)
    {
        EditorID = "P04AlternateHairSSE",
        ValidRaces = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IFormListGetter>(validRaces),
        Model = new Mutagen.Bethesda.Skyrim.Model { File = "meshes\\p04\\hair-alt.nif" }
    };
    SetEnum(alternateHair, "Type", 3);
    SetEnum(alternateHair, "Flags", 4);
    mod.HeadParts.Add(alternateHair);
    File.WriteAllBytes(
        Path.Combine(Path.GetDirectoryName(outputPath)!, "data", "meshes", "p04", "hair-alt.nif"),
        [0x4E, 0x49, 0x46]);
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
    {
        EditorID = "P04NpcSSE",
        Race = new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(race),
        HairColor = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IColorRecordGetter>(color),
        PlayerSkills = new Mutagen.Bethesda.Skyrim.PlayerSkills { Health = 100, Magicka = 80, Stamina = 90 }
    });
    var npc = mod.Npcs[new FormKey(mod.ModKey, 0x800)]; SetEnum(npc.Configuration, "Flags", 1); foreach (var key in parts) npc.HeadParts.Add(new FormLink<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(key));
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateCompleteFaceEditSkyrim(string outputPath)
{
    if (File.Exists(outputPath))
        throw new IOException("The complete face-edit fixture never overwrites an existing output.");
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(
        new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin),
        SkyrimRelease.SkyrimSE);
    var npcKey = new FormKey(mod.ModKey, 0x800);
    var raceKey = new FormKey(mod.ModKey, 0x801);
    var colorKey = new FormKey(mod.ModKey, 0x802);
    var validRacesKey = new FormKey(mod.ModKey, 0x803);
    var textureSetKey = new FormKey(mod.ModKey, 0x804);

    mod.Races.Add(new Mutagen.Bethesda.Skyrim.Race(
        raceKey,
        SkyrimRelease.SkyrimSE)
    {
        EditorID = "FaceEditFixtureRaceSSE"
    });
    mod.Colors.Add(new Mutagen.Bethesda.Skyrim.ColorRecord(
        colorKey,
        SkyrimRelease.SkyrimSE)
    {
        EditorID = "FaceEditFixtureHairColorSSE",
        Color = Color.FromArgb(255, 40, 30, 20),
        Playable = true
    });
    mod.FormLists.Add(new Mutagen.Bethesda.Skyrim.FormList(
        validRacesKey,
        SkyrimRelease.SkyrimSE)
    {
        EditorID = "FaceEditFixtureValidRacesSSE",
        Items = [new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(raceKey)]
    });
    mod.TextureSets.Add(new Mutagen.Bethesda.Skyrim.TextureSet(
        textureSetKey,
        SkyrimRelease.SkyrimSE)
    {
        EditorID = "FaceEditFixtureHeadTextureSSE",
        Flags = Mutagen.Bethesda.Skyrim.TextureSet.Flag.FaceGenTextures |
                Mutagen.Bethesda.Skyrim.TextureSet.Flag.HasModelSpaceNormalMap,
        Diffuse = "textures\\face-edit-fixture\\femalehead.dds",
        NormalOrGloss = "textures\\face-edit-fixture\\femalehead_msn.dds",
        GlowOrDetailMap = "textures\\face-edit-fixture\\femalehead_sk.dds",
        Height = "textures\\actors\\character\\male\\blankdetailmap.dds",
        BacklightMaskOrSpecular = "textures\\face-edit-fixture\\femalehead_s.dds"
    });

    var orderedHeadParts = new List<FormKey>();
    for (var type = 1; type <= 9; type++)
    {
        var key = new FormKey(mod.ModKey, (uint)(0x810 + type));
        orderedHeadParts.Add(key);
        var part = new Mutagen.Bethesda.Skyrim.HeadPart(
            key,
            SkyrimRelease.SkyrimSE)
        {
            EditorID = $"FaceEditFixtureType{type}SSE",
            ValidRaces = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IFormListGetter>(
                validRacesKey),
            Model = new Mutagen.Bethesda.Skyrim.Model
            {
                File = $"meshes\\face-edit-fixture\\type-{type}.nif"
            }
        };
        SetEnum(part, "Type", type);
        SetEnum(part, "Flags", 4);
        mod.HeadParts.Add(part);
    }
    var alternateHair = new Mutagen.Bethesda.Skyrim.HeadPart(
        new FormKey(mod.ModKey, 0x823),
        SkyrimRelease.SkyrimSE)
    {
        EditorID = "FaceEditFixtureAlternateHairSSE",
        ValidRaces = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IFormListGetter>(
            validRacesKey),
        Model = new Mutagen.Bethesda.Skyrim.Model
        {
            File = "meshes\\face-edit-fixture\\hair-alternate.nif"
        }
    };
    SetEnum(alternateHair, "Type", 3);
    SetEnum(alternateHair, "Flags", 4);
    mod.HeadParts.Add(alternateHair);

    var npc = new Mutagen.Bethesda.Skyrim.Npc(npcKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "FaceEditFixtureNpcSSE",
        Name = "Face Edit Fixture",
        Race = new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(raceKey),
        HairColor = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IColorRecordGetter>(
            colorKey),
        HeadTexture = new FormLinkNullable<Mutagen.Bethesda.Skyrim.ITextureSetGetter>(
            textureSetKey),
        Weight = 55F,
        FaceMorph = new Mutagen.Bethesda.Skyrim.NpcFaceMorph
        {
            NoseLongVsShort = 0F,
            NoseUpVsDown = 0F,
            JawUpVsDown = 0F,
            JawNarrowVsWide = 0F,
            JawForwardVsBack = 0F,
            CheeksUpVsDown = 0F,
            CheeksForwardVsBack = 0F,
            EyesUpVsDown = 0F,
            EyesInVsOut = 0F,
            BrowsUpVsDown = 0F,
            BrowsInVsOut = 0F,
            BrowsForwardVsBack = 0F,
            LipsUpVsDown = 0F,
            LipsInVsOut = 0F,
            ChinNarrowVsWide = 0F,
            ChinUpVsDown = 0F,
            ChinUnderbiteVsOverbite = 0F,
            EyesForwardVsBack = 0F,
            Unknown = 0.125F
        },
        FaceParts = new Mutagen.Bethesda.Skyrim.NpcFaceParts
        {
            Nose = uint.MaxValue,
            Unknown = uint.MaxValue,
            Eyes = uint.MaxValue,
            Mouth = uint.MaxValue
        },
        TextureLighting = Color.FromArgb(255, 32, 96, 160),
        PlayerSkills = new Mutagen.Bethesda.Skyrim.PlayerSkills
        {
            Health = 100,
            Magicka = 80,
            Stamina = 90
        }
    };
    SetEnum(npc.Configuration, "Flags", 1);
    foreach (FormKey key in orderedHeadParts)
        npc.HeadParts.Add(new FormLink<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(key));
    npc.TintLayers.Add(new Mutagen.Bethesda.Skyrim.TintLayer
    {
        Index = 1,
        Color = Color.FromArgb(255, 10, 20, 30),
        InterpolationValue = 0.75F,
        Preset = 2
    });
    mod.Npcs.Add(npc);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void GenerateSelectivePasteSkyrim(string outputPath)
{
    if (File.Exists(outputPath))
        throw new IOException("The selective-paste fixture never overwrites an existing output.");
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(
        new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin),
        SkyrimRelease.SkyrimSE);
    var raceKey = new FormKey(mod.ModKey, 0x801);
    var targetColorKey = new FormKey(mod.ModKey, 0x802);
    var sourceColorKey = new FormKey(mod.ModKey, 0x803);
    var targetTextureKey = new FormKey(mod.ModKey, 0x804);
    var sourceTextureKey = new FormKey(mod.ModKey, 0x805);
    var validRacesKey = new FormKey(mod.ModKey, 0x806);
    var armorKey = new FormKey(mod.ModKey, 0x807);
    var targetDefaultOutfitKey = new FormKey(mod.ModKey, 0x808);
    var targetSleepingOutfitKey = new FormKey(mod.ModKey, 0x809);
    var sourceDefaultOutfitKey = new FormKey(mod.ModKey, 0x80A);
    var sourceSleepingOutfitKey = new FormKey(mod.ModKey, 0x80B);

    mod.Races.Add(new Mutagen.Bethesda.Skyrim.Race(
        raceKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "SelectivePasteRaceSSE"
    });
    mod.Colors.Add(new Mutagen.Bethesda.Skyrim.ColorRecord(
        targetColorKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "SelectivePasteTargetHairColorSSE",
        Color = Color.FromArgb(255, 20, 40, 80),
        Playable = true
    });
    mod.Colors.Add(new Mutagen.Bethesda.Skyrim.ColorRecord(
        sourceColorKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "SelectivePasteSourceHairColorSSE",
        Color = Color.FromArgb(255, 90, 45, 15),
        Playable = true
    });
    mod.FormLists.Add(new Mutagen.Bethesda.Skyrim.FormList(
        validRacesKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "SelectivePasteValidRacesSSE",
        Items = [new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(raceKey)]
    });
    mod.TextureSets.Add(CreateTextureSet(
        targetTextureKey,
        "Target",
        "target"));
    mod.TextureSets.Add(CreateTextureSet(
        sourceTextureKey,
        "Source",
        "source"));
    mod.Armors.Add(new Mutagen.Bethesda.Skyrim.Armor(
        armorKey, SkyrimRelease.SkyrimSE)
    {
        EditorID = "SelectivePasteArmorSSE",
        Name = "Selective Paste Fixture Armor"
    });
    AddOutfit(targetDefaultOutfitKey, "TargetDefault", true);
    AddOutfit(targetSleepingOutfitKey, "TargetSleeping", false);
    AddOutfit(sourceDefaultOutfitKey, "SourceDefault", true);
    AddOutfit(sourceSleepingOutfitKey, "SourceSleeping", false);

    List<FormKey> targetParts = AddHeadParts(0x810, "Target");
    List<FormKey> sourceParts = AddHeadParts(0x820, "Source");
    mod.Npcs.Add(CreateNpc(
        new FormKey(mod.ModKey, 0x800),
        "SelectivePasteTargetNpcSSE",
        "Selective Paste Target",
        targetColorKey,
        targetTextureKey,
        targetParts,
        targetDefaultOutfitKey,
        targetSleepingOutfitKey,
        31F,
        false,
        Color.FromArgb(255, 30, 70, 120),
        Color.FromArgb(255, 12, 24, 36),
        0.15F));
    mod.Npcs.Add(CreateNpc(
        new FormKey(mod.ModKey, 0x900),
        "SelectivePasteSourceNpcSSE",
        "Selective Paste Source",
        sourceColorKey,
        sourceTextureKey,
        sourceParts,
        sourceDefaultOutfitKey,
        sourceSleepingOutfitKey,
        72F,
        true,
        Color.FromArgb(255, 170, 80, 40),
        Color.FromArgb(255, 80, 40, 20),
        0.65F));
    mod.WriteToBinary(new FilePath(outputPath));

    Mutagen.Bethesda.Skyrim.TextureSet CreateTextureSet(
        FormKey key,
        string role,
        string folder) => new(key, SkyrimRelease.SkyrimSE)
    {
        EditorID = $"SelectivePaste{role}HeadTextureSSE",
        Flags = Mutagen.Bethesda.Skyrim.TextureSet.Flag.FaceGenTextures |
                Mutagen.Bethesda.Skyrim.TextureSet.Flag.HasModelSpaceNormalMap,
        Diffuse = $"textures\\selective-paste\\{folder}\\femalehead.dds",
        NormalOrGloss = $"textures\\selective-paste\\{folder}\\femalehead_msn.dds",
        GlowOrDetailMap = $"textures\\selective-paste\\{folder}\\femalehead_sk.dds",
        Height = "textures\\actors\\character\\male\\blankdetailmap.dds",
        BacklightMaskOrSpecular =
            $"textures\\selective-paste\\{folder}\\femalehead_s.dds"
    };

    void AddOutfit(FormKey key, string role, bool withArmor)
    {
        var outfit = new Mutagen.Bethesda.Skyrim.Outfit(
            key, SkyrimRelease.SkyrimSE)
        {
            EditorID = $"SelectivePaste{role}OutfitSSE",
            Items = withArmor
                ? [new FormLink<Mutagen.Bethesda.Skyrim.IOutfitTargetGetter>(armorKey)]
                : []
        };
        mod.Outfits.Add(outfit);
    }

    List<FormKey> AddHeadParts(uint baseId, string role)
    {
        var keys = new List<FormKey>();
        for (var type = 1; type <= 9; type++)
        {
            var key = new FormKey(mod.ModKey, baseId + (uint)type);
            keys.Add(key);
            var part = new Mutagen.Bethesda.Skyrim.HeadPart(
                key, SkyrimRelease.SkyrimSE)
            {
                EditorID = $"SelectivePaste{role}Type{type}SSE",
                ValidRaces = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IFormListGetter>(
                    validRacesKey),
                Model = new Mutagen.Bethesda.Skyrim.Model
                {
                    File = $"meshes\\selective-paste\\{role.ToLowerInvariant()}\\type-{type}.nif"
                }
            };
            SetEnum(part, "Type", type);
            SetEnum(part, "Flags", 4);
            mod.HeadParts.Add(part);
        }
        return keys;
    }

    Mutagen.Bethesda.Skyrim.Npc CreateNpc(
        FormKey key,
        string editorId,
        string name,
        FormKey color,
        FormKey texture,
        IReadOnlyList<FormKey> parts,
        FormKey defaultOutfit,
        FormKey sleepingOutfit,
        float weight,
        bool charGen,
        Color lighting,
        Color tint,
        float morphValue)
    {
        var npc = new Mutagen.Bethesda.Skyrim.Npc(
            key, SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId,
            Name = name,
            Race = new FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(raceKey),
            HairColor = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IColorRecordGetter>(color),
            HeadTexture = new FormLinkNullable<Mutagen.Bethesda.Skyrim.ITextureSetGetter>(texture),
            DefaultOutfit = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>(defaultOutfit),
            SleepingOutfit = new FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>(sleepingOutfit),
            Weight = weight,
            FaceMorph = new Mutagen.Bethesda.Skyrim.NpcFaceMorph
            {
                NoseLongVsShort = morphValue,
                NoseUpVsDown = morphValue / 2F,
                JawUpVsDown = 0F,
                JawNarrowVsWide = 0F,
                JawForwardVsBack = 0F,
                CheeksUpVsDown = 0F,
                CheeksForwardVsBack = 0F,
                EyesUpVsDown = 0F,
                EyesInVsOut = 0F,
                BrowsUpVsDown = 0F,
                BrowsInVsOut = 0F,
                BrowsForwardVsBack = 0F,
                LipsUpVsDown = 0F,
                LipsInVsOut = 0F,
                ChinNarrowVsWide = 0F,
                ChinUpVsDown = 0F,
                ChinUnderbiteVsOverbite = 0F,
                EyesForwardVsBack = 0F,
                Unknown = morphValue / 4F
            },
            FaceParts = new Mutagen.Bethesda.Skyrim.NpcFaceParts
            {
                Nose = charGen ? 1u : uint.MaxValue,
                Unknown = uint.MaxValue,
                Eyes = charGen ? 2u : uint.MaxValue,
                Mouth = uint.MaxValue
            },
            TextureLighting = lighting,
            PlayerSkills = new Mutagen.Bethesda.Skyrim.PlayerSkills
            {
                Health = 100,
                Magicka = 80,
                Stamina = 90
            }
        };
        SetEnum(npc.Configuration, "Flags", charGen ? 5 : 1);
        foreach (FormKey part in parts)
            npc.HeadParts.Add(
                new FormLink<Mutagen.Bethesda.Skyrim.IHeadPartGetter>(part));
        npc.TintLayers.Add(new Mutagen.Bethesda.Skyrim.TintLayer
        {
            Index = 1,
            Color = tint,
            InterpolationValue = charGen ? 0.8F : 0.35F,
            Preset = (short)(charGen ? 3 : 1)
        });
        return npc;
    }
}

static void GenerateFaceTintsFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    mod.Npcs.Add(new Mutagen.Bethesda.Fallout4.Npc(new FormKey(mod.ModKey, 0x800), Fallout4Release.Fallout4)
    {
        EditorID = "P04TintNpcFO4",
        Name = "P04 Tint Fixture"
    });
    mod.WriteToBinary(new FilePath(outputPath));
    InjectFaceTintBytes(outputPath, [
        (1u, 0x30u, new byte[] { 60, 10, 20, 30, 0, 255, 255 }),
        (2u, 0x31u, new byte[] { 40 }),
        (1u, 0x32u, new byte[] { 80, 80, 90, 100, 7 }),
        (1u, 0x33u, new byte[] { 20, 200, 210, 220, 0, 2, 0 })
    ]);
}

static void GenerateFaceTintProviderFallout4(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new Fallout4Mod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), Fallout4Release.Fallout4);
    var npcKey = new FormKey(mod.ModKey, 0x800);
    var raceKey = new FormKey(mod.ModKey, 0x801);
    var colorKey = new FormKey(mod.ModKey, 0x802);
    mod.Colors.Add(new Mutagen.Bethesda.Fallout4.ColorRecord(colorKey, Fallout4Release.Fallout4)
    {
        EditorID = "P18SkinToneColor",
        Data = new ColorData { Color = Color.FromArgb(255, 64, 32, 16) }
    });
    var femaleHead = new Mutagen.Bethesda.Fallout4.HeadData
    {
        TintLayers =
        [
            new TintGroup
            {
                CategoryIndex = 12,
                Options =
                [
                    new TintTemplateOption
                    {
                        Index = 42,
                        Slot = TintTemplateOption.TintSlot.SkinTone,
                        Default = 0.75F,
                        TemplateColors =
                        [
                            new TintTemplateColor
                            {
                                TemplateIndex = 3,
                                Alpha = 0.8F,
                                BlendOperation = BlendOperation.Multiply,
                                Color = new FormLink<Mutagen.Bethesda.Fallout4.IColorRecordGetter>(colorKey)
                            }
                        ]
                    }
                ]
            }
        ]
    };
    var race = new Mutagen.Bethesda.Fallout4.Race(raceKey, Fallout4Release.Fallout4)
    {
        EditorID = "P18TintRace",
        NumberOfTintsInList = 1,
        HeadData = new GenderedItem<Mutagen.Bethesda.Fallout4.HeadData?>(new Mutagen.Bethesda.Fallout4.HeadData(), femaleHead)
    };
    mod.Races.Add(race);
    var npc = new Mutagen.Bethesda.Fallout4.Npc(npcKey, Fallout4Release.Fallout4)
    {
        EditorID = "P18TintNpc",
        Race = new FormLink<Mutagen.Bethesda.Fallout4.IRaceGetter>(raceKey)
    };
    mod.Npcs.Add(npc);
    SetEnum(npc, "Flags", 1);
    mod.WriteToBinary(new FilePath(outputPath));
}

static void InjectFaceTintBytes(string outputPath, IReadOnlyList<(uint Type, uint Index, byte[] Tend)> layers)
{
    var source = File.ReadAllBytes(outputPath);
    var found = false;
    using var output = new MemoryStream(source.Length + 256);
    RewriteFaceTintRange(source, 0, source.Length, layers, output, ref found);
    if (!found) throw new InvalidDataException("Fixture NPC 0x800 was not found for face-tint injection.");
    File.WriteAllBytes(outputPath, output.ToArray());
}

static void RewriteFaceTintRange(byte[] source, int start, int end, IReadOnlyList<(uint Type, uint Index, byte[] Tend)> layers,
    MemoryStream output, ref bool found)
{
    var position = start;
    while (position < end)
    {
        var signature = System.Text.Encoding.ASCII.GetString(source, position, 4);
        var size = BitConverter.ToUInt32(source, position + 4);
        var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (recordEnd > end) throw new InvalidDataException("Fixture TES4 record exceeds container.");
        if (signature == "GRUP")
        {
            var headerPosition = output.Position; output.Write(source, position, 24);
            using var children = new MemoryStream((int)size);
            RewriteFaceTintRange(source, position + 24, recordEnd, layers, children, ref found);
            var groupSize = checked((uint)(24 + children.Length)); output.Position = headerPosition + 4; output.Write(BitConverter.GetBytes(groupSize)); output.Position = output.Length;
            children.Position = 0; children.CopyTo(output);
        }
        else if (signature == "NPC_" && position + 16 <= recordEnd && BitConverter.ToUInt32(source, position + 12) == 0x800)
        {
            found = true;
            using var record = new MemoryStream(recordEnd - position + 256); record.Write(source, position, recordEnd - position);
            foreach (var layer in layers)
            {
                record.Write(System.Text.Encoding.ASCII.GetBytes("TETI")); record.Write(BitConverter.GetBytes((ushort)4));
                record.Write(BitConverter.GetBytes((ushort)layer.Type)); record.Write(BitConverter.GetBytes((ushort)layer.Index));
                record.Write(System.Text.Encoding.ASCII.GetBytes("TEND")); record.Write(BitConverter.GetBytes((ushort)layer.Tend.Length)); record.Write(layer.Tend);
            }
            var bytes = record.ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 24)).CopyTo(bytes, 4); output.Write(bytes);
        }
        else output.Write(source, position, recordEnd - position);
        position = recordEnd;
    }
}

static void GenerateSseMorphs(string outputPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outputPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
    mod.Npcs.Add(new Mutagen.Bethesda.Skyrim.Npc(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "P04SseMorphNpc", Name = "P04 SSE Morph Fixture" });
    mod.WriteToBinary(new FilePath(outputPath));
    var nam9 = new byte[76]; for (var i = 0; i < 18; i++) BitConverter.GetBytes((float)(i - 9) / 10F).CopyTo(nam9, i * 4); BitConverter.GetBytes(float.MaxValue).CopyTo(nam9, 72);
    var nama = new byte[16]; BitConverter.GetBytes(uint.MaxValue).CopyTo(nama, 0); BitConverter.GetBytes((uint)2).CopyTo(nama, 4); BitConverter.GetBytes(uint.MaxValue).CopyTo(nama, 8); BitConverter.GetBytes((uint)4).CopyTo(nama, 12);
    InjectSseMorphBytes(outputPath, nam9, nama);
}

static void GenerateSseTints(string outputPath)
{
    GenerateSseMorphs(outputPath);
    var layers = new[]
    {
        (Index: (ushort)4, Red: (byte)1, Green: (byte)2, Blue: (byte)3, Alpha: (byte)255, Coverage: (uint)10, Preset: (short)1),
        (Index: (ushort)24, Red: (byte)9, Green: (byte)8, Blue: (byte)7, Alpha: (byte)64, Coverage: (uint)50, Preset: (short)-1)
    };
    var tintBytes = new MemoryStream();
    foreach (var layer in layers)
    {
        tintBytes.Write(System.Text.Encoding.ASCII.GetBytes("TINI")); tintBytes.Write(BitConverter.GetBytes((ushort)2)); tintBytes.Write(BitConverter.GetBytes(layer.Index));
        tintBytes.Write(System.Text.Encoding.ASCII.GetBytes("TINC")); tintBytes.Write(BitConverter.GetBytes((ushort)4)); tintBytes.Write(new[] { layer.Red, layer.Green, layer.Blue, layer.Alpha });
        tintBytes.Write(System.Text.Encoding.ASCII.GetBytes("TINV")); tintBytes.Write(BitConverter.GetBytes((ushort)4)); tintBytes.Write(BitConverter.GetBytes(layer.Coverage));
        tintBytes.Write(System.Text.Encoding.ASCII.GetBytes("TIAS")); tintBytes.Write(BitConverter.GetBytes((ushort)2)); tintBytes.Write(BitConverter.GetBytes(layer.Preset));
    }
    InjectSseTintBytes(outputPath, tintBytes.ToArray());
    AppendNonTargetSseNpc(outputPath);
}

static void AppendNonTargetSseNpc(string outputPath)
{
    var body = BuildSubrecord("EDID", System.Text.Encoding.ASCII.GetBytes("P04SseTintOtherNpc\0"));
    var header = new byte[24];
    System.Text.Encoding.ASCII.GetBytes("NPC_").CopyTo(header, 0);
    BitConverter.GetBytes((uint)body.Length).CopyTo(header, 4);
    BitConverter.GetBytes((uint)0x801).CopyTo(header, 12);
    using var output = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
    output.Write(header);
    output.Write(body);
}

static byte[] BuildSubrecord(string signature, byte[] data)
{
    var result = new byte[6 + data.Length];
    System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(result, 0);
    BitConverter.GetBytes((ushort)data.Length).CopyTo(result, 4);
    data.CopyTo(result, 6);
    return result;
}

static void InjectSseTintBytes(string outputPath, byte[] tintBytes)
{
    var source = File.ReadAllBytes(outputPath); var found = false; using var output = new MemoryStream(source.Length + tintBytes.Length);
    RewriteSseTintRange(source, 0, source.Length, tintBytes, output, ref found);
    if (!found) throw new InvalidDataException("Fixture NPC 0x800 was not found for SSE tint injection.");
    File.WriteAllBytes(outputPath, output.ToArray());
}

static void RewriteSseTintRange(byte[] source, int start, int end, byte[] tintBytes, MemoryStream output, ref bool found)
{
    var position = start;
    while (position < end)
    {
        var signature = System.Text.Encoding.ASCII.GetString(source, position, 4); var size = BitConverter.ToUInt32(source, position + 4); var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (recordEnd > end) throw new InvalidDataException("Fixture TES4 record exceeds container.");
        if (signature == "GRUP")
        {
            var headerPosition = output.Position; output.Write(source, position, 24); using var children = new MemoryStream((int)size); RewriteSseTintRange(source, position + 24, recordEnd, tintBytes, children, ref found);
            var groupSize = checked((uint)(24 + children.Length)); output.Position = headerPosition + 4; output.Write(BitConverter.GetBytes(groupSize)); output.Position = output.Length; children.Position = 0; children.CopyTo(output);
        }
        else if (signature == "NPC_" && position + 16 <= recordEnd && BitConverter.ToUInt32(source, position + 12) == 0x800)
        {
            found = true; output.Write(source, position, recordEnd - position); output.Write(tintBytes);
            var current = output.Position; output.Position = current - (recordEnd - position) - tintBytes.Length + 4; output.Write(BitConverter.GetBytes((uint)(recordEnd - position - 24 + tintBytes.Length))); output.Position = current;
        }
        else output.Write(source, position, recordEnd - position);
        position = recordEnd;
    }
}

static void InjectSseMorphBytes(string outputPath, byte[] nam9, byte[] nama)
{
    var source = File.ReadAllBytes(outputPath); var found = false; using var output = new MemoryStream(source.Length + 128);
    RewriteSseMorphRange(source, 0, source.Length, nam9, nama, output, ref found);
    if (!found) throw new InvalidDataException("Fixture NPC 0x800 was not found for SSE morph injection.");
    File.WriteAllBytes(outputPath, output.ToArray());
}

static void RewriteSseMorphRange(byte[] source, int start, int end, byte[] nam9, byte[] nama, MemoryStream output, ref bool found)
{
    var position = start;
    while (position < end)
    {
        var signature = System.Text.Encoding.ASCII.GetString(source, position, 4); var size = BitConverter.ToUInt32(source, position + 4); var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (recordEnd > end) throw new InvalidDataException("Fixture TES4 record exceeds container.");
        if (signature == "GRUP")
        {
            var headerPosition = output.Position; output.Write(source, position, 24); using var children = new MemoryStream((int)size); RewriteSseMorphRange(source, position + 24, recordEnd, nam9, nama, children, ref found);
            var groupSize = checked((uint)(24 + children.Length)); output.Position = headerPosition + 4; output.Write(BitConverter.GetBytes(groupSize)); output.Position = output.Length; children.Position = 0; children.CopyTo(output);
        }
        else if (signature == "NPC_" && position + 16 <= recordEnd && BitConverter.ToUInt32(source, position + 12) == 0x800)
        {
            found = true; using var record = new MemoryStream(recordEnd - position + 128); record.Write(source, position, recordEnd - position);
            record.Write(System.Text.Encoding.ASCII.GetBytes("NAM9")); record.Write(BitConverter.GetBytes((ushort)nam9.Length)); record.Write(nam9);
            record.Write(System.Text.Encoding.ASCII.GetBytes("NAMA")); record.Write(BitConverter.GetBytes((ushort)nama.Length)); record.Write(nama);
            var bytes = record.ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 24)).CopyTo(bytes, 4); output.Write(bytes);
        }
        else output.Write(source, position, recordEnd - position);
        position = recordEnd;
    }
}

static void SetEnum(object target, string propertyName, int value)
{
    var property = target.GetType().GetProperty(propertyName) ?? throw new InvalidOperationException($"Missing {propertyName} on {target.GetType().Name}.");
    property.SetValue(target, Enum.ToObject(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, value));
}

static void AddHeadPartModel(object headPart, int partType, string path)
{
    var collectionProperty = headPart.GetType().GetProperty("Parts") ?? throw new InvalidOperationException("Headpart Parts property missing.");
    var collection = (System.Collections.IList)(collectionProperty.GetValue(headPart) ?? throw new InvalidOperationException("Headpart Parts collection missing."));
    var elementType = collectionProperty.PropertyType.GetGenericArguments().Single();
    var element = System.Activator.CreateInstance(elementType) ?? throw new InvalidOperationException("Headpart part element could not be created.");
    SetEnum(element, "PartType", partType);
    var file = elementType.GetProperty("FileName") ?? throw new InvalidOperationException("Headpart part FileName property missing.");
    if (file.PropertyType == typeof(string)) file.SetValue(element, path);
    else
    {
        var constructor = file.PropertyType.GetConstructor([typeof(string)]);
        var value = constructor is not null ? constructor.Invoke([path]) : System.Activator.CreateInstance(file.PropertyType) ?? throw new InvalidOperationException("Headpart file name could not be created.");
        var givenPath = file.PropertyType.GetProperty("GivenPath");
        if (constructor is null && givenPath is null) throw new InvalidOperationException("Headpart file name GivenPath property missing.");
        givenPath?.SetValue(value, path);
        file.SetValue(element, value);
    }
    collection.Add(element);
}

static void InjectFo4Models(string path)
{
    var source = File.ReadAllBytes(path); using var output = new MemoryStream(source.Length + 512);
    RewriteFo4Range(source, 0, source.Length, output);
    File.WriteAllBytes(path, output.ToArray());
}

static void RewriteFo4Range(byte[] source, int start, int end, MemoryStream output)
{
    var position = start;
    while (position < end)
    {
        if (position + 8 > end) throw new InvalidDataException("Fixture TES4 header truncated.");
        var signature = System.Text.Encoding.ASCII.GetString(source, position, 4);
        var size = BitConverter.ToUInt32(source, position + 4);
        var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (recordEnd > end) throw new InvalidDataException("Fixture TES4 record exceeds container.");
        if (signature == "GRUP")
        {
            var headerPosition = output.Position; output.Write(source, position, 24);
            using var children = new MemoryStream((int)size); RewriteFo4Range(source, position + 24, recordEnd, children);
            var groupSize = checked((uint)(24 + children.Length)); output.Position = headerPosition + 4; output.Write(BitConverter.GetBytes(groupSize)); output.Position = output.Length;
            children.Position = 0; children.CopyTo(output);
        }
        else if (signature == "HDPT" && position + 16 <= recordEnd)
        {
            var id = BitConverter.ToUInt32(source, position + 12);
            if (id is >= 0x811 and <= 0x819) PatchFo4HeadPart(source, position, recordEnd, output, id - 0x810);
            else output.Write(source, position, recordEnd - position);
        }
        else output.Write(source, position, recordEnd - position);
        position = recordEnd;
    }
}

static void PatchFo4HeadPart(byte[] source, int start, int end, MemoryStream output, uint type)
{
    using var record = new MemoryStream(end - start + 64); record.Write(source, start, 24); record.Write(source, start + 24, end - start - 24);
    var pathBytes = System.Text.Encoding.UTF8.GetBytes($"meshes\\p04\\{type}.nif\0");
    record.Write(System.Text.Encoding.ASCII.GetBytes("MODL")); record.Write(BitConverter.GetBytes((ushort)pathBytes.Length)); record.Write(pathBytes);
    var bytes = record.ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 24)).CopyTo(bytes, 4); output.Write(bytes);
}
