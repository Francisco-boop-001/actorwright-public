using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaNpcTemplateSnapshot(
    ImmutableHashSet<NpcTemplateCategory> InheritedCategories,
    ImmutableDictionary<NpcTemplateCategory, FormReference?> DirectSources,
    FormReference? BaseTemplate);

/// <summary>Typed dual-game bridge for TES4 template flags and category-specific source links.</summary>
public static class BethesdaNpcTemplateAdapter
{
    public static BethesdaNpcTemplateSnapshot Read(GameEdition edition, WorkspacePath pluginPath, FormId formId)
    {
        var path = pluginPath.Value;
        var modKey = ToModKey(path);
        return edition switch
        {
            GameEdition.Fallout4 => ReadFallout4(path, modKey, formId),
            GameEdition.SkyrimSpecialEdition => ReadSkyrim(path, modKey, formId),
            _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unsupported game edition.")
        };
    }

    public static void Write(
        NpcTemplateMaterializationRequest request,
        ImmutableHashSet<NpcTemplateCategory> categories,
        NpcMutationRequest mutation,
        WorkspacePath destination)
    {
        var sourcePath = request.InputPlugin.Value;
        var modKey = ToModKey(sourcePath);
        switch (request.Edition)
        {
            case GameEdition.Fallout4:
                WriteFallout4(sourcePath, destination.Value, modKey, categories, mutation);
                break;
            case GameEdition.SkyrimSpecialEdition:
                WriteSkyrim(sourcePath, destination.Value, modKey, categories, mutation);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Edition, "Unsupported game edition.");
        }
    }

    private static BethesdaNpcTemplateSnapshot ReadFallout4(string path, ModKey modKey, FormId formId)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var npc = overlay.Npcs[new FormKey(modKey, formId.Value)];
        var flags = (uint)npc.UseTemplateActors;
        var inherited = Enum.GetValues<NpcTemplateCategory>()
            .Where(category => (flags & (1u << (int)category)) != 0)
            .ToImmutableHashSet();
        var actors = npc.TemplateActors;
        var defaultSource = ToReference(npc.DefaultTemplate.FormKeyNullable);
        var directSources = Enum.GetValues<NpcTemplateCategory>().ToImmutableDictionary(
            category => category,
            category => GetFallout4Source(actors, category) ?? defaultSource);
        return new BethesdaNpcTemplateSnapshot(inherited, directSources, defaultSource);
    }

    private static BethesdaNpcTemplateSnapshot ReadSkyrim(string path, ModKey modKey, FormId formId)
    {
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
        var npc = overlay.Npcs[new FormKey(modKey, formId.Value)];
        var flags = (uint)npc.Configuration.TemplateFlags;
        var inherited = Enum.GetValues<NpcTemplateCategory>()
            .Where(category => (flags & (1u << (int)category)) != 0)
            .ToImmutableHashSet();
        var template = ToReference(npc.Template.FormKeyNullable);
        return new BethesdaNpcTemplateSnapshot(inherited,
            Enum.GetValues<NpcTemplateCategory>().ToImmutableDictionary(category => category, _ => template), template);
    }

    private static FormReference? GetFallout4Source(Fo4.ITemplateActorsGetter? actors, NpcTemplateCategory category) => actors is null ? null : category switch
    {
        NpcTemplateCategory.Traits => ToReference(actors.TraitTemplate.FormKey),
        NpcTemplateCategory.Stats => ToReference(actors.StatsTemplate.FormKey),
        NpcTemplateCategory.Factions => ToReference(actors.FactionsTemplate.FormKey),
        NpcTemplateCategory.SpellList => ToReference(actors.SpellListTemplate.FormKey),
        NpcTemplateCategory.AiData => ToReference(actors.AiDataTemplate.FormKey),
        NpcTemplateCategory.AiPackages => ToReference(actors.AiPackagesTemplate.FormKey),
        NpcTemplateCategory.ModelAnimation => ToReference(actors.ModelOrAnimationTemplate.FormKey),
        NpcTemplateCategory.BaseData => ToReference(actors.BaseDataTemplate.FormKey),
        NpcTemplateCategory.Inventory => ToReference(actors.InventoryTemplate.FormKey),
        NpcTemplateCategory.Script => ToReference(actors.ScriptTemplate.FormKey),
        NpcTemplateCategory.DefaultPackageList => ToReference(actors.DefPackListTemplate.FormKey),
        NpcTemplateCategory.AttackData => ToReference(actors.AttackDataTemplate.FormKey),
        NpcTemplateCategory.Keywords => ToReference(actors.KeywordsTemplate.FormKey),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown NPC template category.")
    };

    private static void WriteFallout4(
        string sourcePath,
        string destinationPath,
        ModKey modKey,
        ImmutableHashSet<NpcTemplateCategory> categories,
        NpcMutationRequest mutation)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        var mutable = (Fo4.Fallout4Mod)overlay.DeepCopy();
        var npc = mutable.Npcs[new FormKey(modKey, mutation.TargetFormId.Value)];
        ApplyFallout4Mutation(npc, mutation);
        var flags = (uint)npc.UseTemplateActors;
        foreach (var category in categories) flags &= ~(1u << (int)category);
        npc.UseTemplateActors = (Fo4.Npc.TemplateActorType)flags;
        WriteMod(mutable, destinationPath);
    }

    private static void WriteSkyrim(
        string sourcePath,
        string destinationPath,
        ModKey modKey,
        ImmutableHashSet<NpcTemplateCategory> categories,
        NpcMutationRequest mutation)
    {
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(sourcePath)), Sse.SkyrimRelease.SkyrimSE);
        var mutable = (Sse.SkyrimMod)overlay.DeepCopy();
        var npc = mutable.Npcs[new FormKey(modKey, mutation.TargetFormId.Value)];
        ApplySkyrimMutation(npc, mutation);
        var flags = (uint)npc.Configuration.TemplateFlags;
        foreach (var category in categories) flags &= ~(1u << (int)category);
        npc.Configuration.TemplateFlags = (Sse.NpcConfiguration.TemplateFlag)flags;
        WriteMod(mutable, destinationPath);
    }

    private static void ApplyFallout4Mutation(Fo4.Npc npc, NpcMutationRequest request)
    {
        if (request.Sex is { } sex) npc.Flags = sex == NpcSex.Female ? npc.Flags | Fo4.Npc.Flag.Female : npc.Flags & ~Fo4.Npc.Flag.Female;
        if (request.Archetype is { } archetype)
        {
            if (archetype.Race.Value is { } race) npc.Race = new FormLink<Fo4.IRaceGetter>(ToFormKey(race));
            if (archetype.Voice.Value is { } voice) npc.Voice = new FormLinkNullable<Fo4.IVoiceTypeGetter>(ToFormKey(voice));
            if (archetype.Class.Value is { } npcClass) npc.Class = new FormLinkNullable<Fo4.IClassGetter>(ToFormKey(npcClass));
            if (archetype.CombatStyle.Value is { } style) npc.CombatStyle = new FormLinkNullable<Fo4.ICombatStyleGetter>(ToFormKey(style));
        }
        if (request.Stats is { } stats) ApplyFallout4StatsForTemplate(npc, stats);
        if (request.KeywordPatch is { } keywords) ApplyFallout4KeywordsForTemplate(npc, keywords);
        if (request.FactionPatch is { } factions) ApplyFallout4FactionsForTemplate(npc, factions);
        if (request.ActorEffectPatch is { } effects) ApplyFallout4EffectsForTemplate(npc, effects);
    }

    private static void ApplySkyrimMutation(Sse.Npc npc, NpcMutationRequest request)
    {
        if (request.Sex is { } sex) npc.Configuration.Flags = sex == NpcSex.Female ? npc.Configuration.Flags | Sse.NpcConfiguration.Flag.Female : npc.Configuration.Flags & ~Sse.NpcConfiguration.Flag.Female;
        if (request.Archetype is { } archetype)
        {
            if (archetype.Race.Value is { } race) npc.Race = new FormLink<Sse.IRaceGetter>(ToFormKey(race));
            if (archetype.Voice.Value is { } voice) npc.Voice = new FormLinkNullable<Sse.IVoiceTypeGetter>(ToFormKey(voice));
            if (archetype.Class.Value is { } npcClass) npc.Class = new FormLink<Sse.IClassGetter>(ToFormKey(npcClass));
            if (archetype.CombatStyle.Value is { } style) npc.CombatStyle = new FormLinkNullable<Sse.ICombatStyleGetter>(ToFormKey(style));
        }
        if (request.Stats is { } stats) ApplySkyrimStatsForTemplate(npc, stats);
        if (request.KeywordPatch is { } keywords) ApplySkyrimKeywordsForTemplate(npc, keywords);
        if (request.FactionPatch is { } factions) ApplySkyrimFactionsForTemplate(npc, factions);
        if (request.ActorEffectPatch is { } effects) ApplySkyrimEffectsForTemplate(npc, effects);
    }

    private static void ApplyFallout4StatsForTemplate(Fo4.Npc npc, NpcStatsPatch patch)
    {
        if (patch.Level is { } level)
        {
            npc.Flags = level.Mode == NpcLevelMode.Multiplier ? npc.Flags | (Fo4.Npc.Flag)0x80u : npc.Flags & ~(Fo4.Npc.Flag)0x80u;
            npc.Level = level.Mode == NpcLevelMode.Multiplier ? new Fo4.PcLevelMult { LevelMult = (float)level.Value } : new Fo4.NpcLevel { Level = checked((short)level.Value) };
        }
        if (patch.XpValueOffset is { } xp) npc.XpValueOffset = xp;
        if (patch.CalcMinLevel is { } min) npc.CalcMinLevel = checked((short)min);
        if (patch.CalcMaxLevel is { } max) npc.CalcMaxLevel = checked((short)max);
        if (patch.DispositionBase is { } disposition) npc.DispositionBase = disposition;
        if (patch.BleedoutOverride is { } bleedout) npc.BleedoutOverride = bleedout;
    }

    private static void ApplySkyrimStatsForTemplate(Sse.Npc npc, NpcStatsPatch patch)
    {
        if (patch.Level is { } level)
        {
            npc.Configuration.Flags = level.Mode == NpcLevelMode.Multiplier ? npc.Configuration.Flags | (Sse.NpcConfiguration.Flag)0x80u : npc.Configuration.Flags & ~(Sse.NpcConfiguration.Flag)0x80u;
            npc.Configuration.Level = level.Mode == NpcLevelMode.Multiplier ? new Sse.PcLevelMult { LevelMult = (float)level.Value } : new Sse.NpcLevel { Level = checked((short)level.Value) };
        }
        if (patch.MagickaOffset is { } magicka) npc.Configuration.MagickaOffset = magicka;
        if (patch.StaminaOffset is { } stamina) npc.Configuration.StaminaOffset = stamina;
        if (patch.HealthOffset is { } health) npc.Configuration.HealthOffset = health;
        if (patch.CalcMinLevel is { } min) npc.Configuration.CalcMinLevel = checked((short)min);
        if (patch.CalcMaxLevel is { } max) npc.Configuration.CalcMaxLevel = checked((short)max);
        if (patch.SpeedMultiplier is { } speed) npc.Configuration.SpeedMultiplier = speed;
        if (patch.DispositionBase is { } disposition) npc.Configuration.DispositionBase = disposition;
        if (patch.BleedoutOverride is { } bleedout) npc.Configuration.BleedoutOverride = bleedout;
        if (patch.Height is { } height) npc.Height = height;
        if (patch.PlayerSkills is { } playerSkills)
        {
            var skills = npc.PlayerSkills ??= new Sse.PlayerSkills();
            if (playerSkills.Health is { } playerHealth) skills.Health = playerHealth;
            if (playerSkills.Magicka is { } playerMagicka) skills.Magicka = playerMagicka;
            if (playerSkills.Stamina is { } playerStamina) skills.Stamina = playerStamina;
            if (playerSkills.Values is not null)
                foreach (var (skill, value) in playerSkills.Values) skills.SkillValues[Enum.Parse<Sse.Skill>(skill.ToString())] = value;
            if (playerSkills.Offsets is not null)
                foreach (var (skill, value) in playerSkills.Offsets) skills.SkillOffsets[Enum.Parse<Sse.Skill>(skill.ToString())] = value;
        }
    }

    private static void ApplyFallout4KeywordsForTemplate(Fo4.Npc npc, NpcKeywordPatch patch)
    {
        if (patch.Keywords.Replace is { } replace) { npc.Keywords ??= []; npc.Keywords.Clear(); foreach (var reference in replace) npc.Keywords.Add(new FormLink<Fo4.IKeywordGetter>(ToFormKey(reference))); }
    }

    private static void ApplySkyrimKeywordsForTemplate(Sse.Npc npc, NpcKeywordPatch patch)
    {
        if (patch.Keywords.Replace is { } replace) { npc.Keywords ??= []; npc.Keywords.Clear(); foreach (var reference in replace) npc.Keywords.Add(new FormLink<Sse.IKeywordGetter>(ToFormKey(reference))); }
    }

    private static void ApplyFallout4FactionsForTemplate(Fo4.Npc npc, NpcFactionPatch patch)
    {
        if (patch.Replace is null) return;
        npc.Factions.Clear(); foreach (var entry in patch.Replace) npc.Factions.Add(new Fo4.RankPlacement { Faction = new FormLink<Fo4.IFactionGetter>(ToFormKey(entry.Faction)), Rank = entry.Rank });
    }

    private static void ApplySkyrimFactionsForTemplate(Sse.Npc npc, NpcFactionPatch patch)
    {
        if (patch.Replace is null) return;
        npc.Factions.Clear(); foreach (var entry in patch.Replace) npc.Factions.Add(new Sse.RankPlacement { Faction = new FormLink<Sse.IFactionGetter>(ToFormKey(entry.Faction)), Rank = entry.Rank });
    }

    private static void ApplyFallout4EffectsForTemplate(Fo4.Npc npc, NpcActorEffectPatch patch)
    {
        if (patch.Replace is { } replace) { npc.ActorEffect ??= []; npc.ActorEffect.Clear(); foreach (var reference in replace) npc.ActorEffect.Add(new FormLink<Fo4.ISpellRecordGetter>(ToFormKey(reference))); }
    }

    private static void ApplySkyrimEffectsForTemplate(Sse.Npc npc, NpcActorEffectPatch patch)
    {
        if (patch.Replace is { } replace) { npc.ActorEffect ??= []; npc.ActorEffect.Clear(); foreach (var reference in replace) npc.ActorEffect.Add(new FormLink<Sse.ISpellRecordGetter>(ToFormKey(reference))); }
    }

    private static void WriteMod(IModGetter mod, string destinationPath) =>
        mod.WriteToBinary(new FilePath(destinationPath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck
        });

    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);

    private static FormReference? ToReference(FormKey? formKey) => formKey is { } key && !key.IsNull
        ? new FormReference(new PluginName(key.ModKey.ToString()), new FormId(key.ID)) : null;

    private static FormKey ToFormKey(FormReference reference) => new(
        new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin), reference.FormId.Value);
}
