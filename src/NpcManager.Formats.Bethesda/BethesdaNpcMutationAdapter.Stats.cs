using System.Collections.Immutable;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcMutationAdapter
{
    private static NpcStatsSnapshot ReadSkyrimStats(Mutagen.Bethesda.Skyrim.INpcGetter npc)
    {
        var config = npc.Configuration;
        var level = ReadLevel(config.Level, config.Level is Mutagen.Bethesda.Skyrim.PcLevelMult);
        var skills = npc.PlayerSkills;
        var playerSkills = skills is null
            ? null
            : new NpcPlayerSkillsSnapshot(skills.Health, skills.Magicka, skills.Stamina,
                ReadSkills<Mutagen.Bethesda.Skyrim.Skill>(skills.SkillValues),
                ReadSkills<Mutagen.Bethesda.Skyrim.Skill>(skills.SkillOffsets),
                skills.FarAwayModelDistance,
                skills.GearedUpWeapons);
        return new NpcStatsSnapshot(level, 0, config.MagickaOffset, config.StaminaOffset, config.HealthOffset,
            unchecked((ushort)config.CalcMinLevel), unchecked((ushort)config.CalcMaxLevel), config.SpeedMultiplier,
            config.DispositionBase, config.BleedoutOverride, npc.Height, playerSkills,
            ReadFlags(GameEdition.SkyrimSpecialEdition, (uint)config.Flags));
    }

    private static NpcStatsSnapshot ReadFallout4Stats(Mutagen.Bethesda.Fallout4.INpcGetter npc)
    {
        var level = ReadLevel(npc.Level, npc.Level is Mutagen.Bethesda.Fallout4.PcLevelMult);
        return new NpcStatsSnapshot(level, npc.XpValueOffset, 0, 0, 0,
            unchecked((ushort)npc.CalcMinLevel), unchecked((ushort)npc.CalcMaxLevel), 0,
            npc.DispositionBase, npc.BleedoutOverride, null, null,
            ReadFlags(GameEdition.Fallout4, (uint)npc.Flags));
    }

    private static NpcLevelValue ReadLevel(Mutagen.Bethesda.Skyrim.IANpcLevelGetter? level, bool multiplier)
    {
        var raw = level switch
        {
            Mutagen.Bethesda.Skyrim.NpcLevel concrete => unchecked((ushort)concrete.Level),
            Mutagen.Bethesda.Skyrim.PcLevelMult multiplierLevel => unchecked((ushort)Math.Round(multiplierLevel.LevelMult * 1000f)),
            _ => (ushort)0
        };
        return new NpcLevelValue(multiplier ? NpcLevelMode.Multiplier : NpcLevelMode.Fixed,
            multiplier ? raw / 1000m : raw);
    }

    private static NpcLevelValue ReadLevel(Mutagen.Bethesda.Fallout4.IANpcLevelGetter? level, bool multiplier)
    {
        var raw = level switch
        {
            Mutagen.Bethesda.Fallout4.NpcLevel concrete => unchecked((ushort)concrete.Level),
            Mutagen.Bethesda.Fallout4.PcLevelMult multiplierLevel => unchecked((ushort)Math.Round(multiplierLevel.LevelMult * 1000f)),
            _ => (ushort)0
        };
        return new NpcLevelValue(multiplier ? NpcLevelMode.Multiplier : NpcLevelMode.Fixed,
            multiplier ? raw / 1000m : raw);
    }

    private static ImmutableDictionary<NpcSkill, byte> ReadSkills<T>(IReadOnlyDictionary<T, byte> values)
        where T : struct, Enum
    {
        var builder = ImmutableDictionary.CreateBuilder<NpcSkill, byte>();
        foreach (var (key, value) in values)
            if (Enum.TryParse<NpcSkill>(key.ToString(), out var skill)) builder[skill] = value;
        return builder.ToImmutable();
    }

    private static ImmutableHashSet<NpcFlag> ReadFlags(GameEdition edition, uint raw)
    {
        var builder = ImmutableHashSet.CreateBuilder<NpcFlag>();
        foreach (var flag in Enum.GetValues<NpcFlag>())
            if (NpcFlagExtensions.TryGetMask(edition, flag, out var mask) && (raw & mask) != 0) builder.Add(flag);
        return builder.ToImmutable();
    }

    private static void ApplySkyrimStats(Mutagen.Bethesda.Skyrim.Npc npc, NpcStatsPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var config = npc.Configuration;
        var rawFlags = (uint)config.Flags;
        ApplyFlagPatch(GameEdition.SkyrimSpecialEdition, ref rawFlags, patch.Flags);
        if (patch.Level is { } level)
        {
            if (!level.TryGetRaw(out var raw)) throw new InvalidOperationException("Invalid Skyrim level value.");
            rawFlags = level.Mode == NpcLevelMode.Multiplier ? rawFlags | 0x80u : rawFlags & ~0x80u;
            config.Level = level.Mode == NpcLevelMode.Multiplier
                ? new Mutagen.Bethesda.Skyrim.PcLevelMult { LevelMult = (float)level.Value }
                : new Mutagen.Bethesda.Skyrim.NpcLevel { Level = unchecked((short)raw) };
        }
        else if ((patch.Flags is { } flagPatch && flagPatch.Set.Contains(NpcFlag.SkyrimUseTemplate)) ||
                 (patch.Flags is { } clearPatch && clearPatch.Clear.Contains(NpcFlag.SkyrimUseTemplate)))
        {
            var current = ReadLevel(config.Level, config.Level is Mutagen.Bethesda.Skyrim.PcLevelMult);
            if (patch.Flags!.Set.Contains(NpcFlag.SkyrimUseTemplate))
                config.Level = new Mutagen.Bethesda.Skyrim.PcLevelMult { LevelMult = (float)(current.Mode == NpcLevelMode.Multiplier ? current.Value : current.Value / 1000m) };
            else if (current.TryGetRaw(out var currentRaw))
                config.Level = new Mutagen.Bethesda.Skyrim.NpcLevel { Level = unchecked((short)currentRaw) };
        }
        config.Flags = (Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag)rawFlags;
        if (patch.MagickaOffset is { } magicka) config.MagickaOffset = magicka;
        if (patch.StaminaOffset is { } stamina) config.StaminaOffset = stamina;
        if (patch.HealthOffset is { } health) config.HealthOffset = health;
        if (patch.CalcMinLevel is { } min) config.CalcMinLevel = unchecked((short)min);
        if (patch.CalcMaxLevel is { } max) config.CalcMaxLevel = unchecked((short)max);
        if (patch.SpeedMultiplier is { } speed) config.SpeedMultiplier = speed;
        if (patch.DispositionBase is { } disposition) config.DispositionBase = disposition;
        if (patch.BleedoutOverride is { } bleedout) config.BleedoutOverride = bleedout;
        if (patch.Height is { } height) npc.Height = height;
        ApplyPlayerSkills(npc, patch.PlayerSkills);
    }

    private static void ApplyFallout4Stats(Mutagen.Bethesda.Fallout4.Npc npc, NpcStatsPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var rawFlags = (uint)npc.Flags;
        ApplyFlagPatch(GameEdition.Fallout4, ref rawFlags, patch.Flags);
        if (patch.Level is { } level)
        {
            if (!level.TryGetRaw(out var raw)) throw new InvalidOperationException("Invalid Fallout 4 level value.");
            rawFlags = level.Mode == NpcLevelMode.Multiplier ? rawFlags | 0x80u : rawFlags & ~0x80u;
            npc.Level = level.Mode == NpcLevelMode.Multiplier
                ? new Mutagen.Bethesda.Fallout4.PcLevelMult { LevelMult = (float)level.Value }
                : new Mutagen.Bethesda.Fallout4.NpcLevel { Level = unchecked((short)raw) };
        }
        else if ((patch.Flags is { } flagPatch && flagPatch.Set.Contains(NpcFlag.Fallout4CalcForEachTemplate)) ||
                 (patch.Flags is { } clearPatch && clearPatch.Clear.Contains(NpcFlag.Fallout4CalcForEachTemplate)))
        {
            var current = ReadLevel(npc.Level, npc.Level is Mutagen.Bethesda.Fallout4.PcLevelMult);
            if (patch.Flags!.Set.Contains(NpcFlag.Fallout4CalcForEachTemplate))
                npc.Level = new Mutagen.Bethesda.Fallout4.PcLevelMult { LevelMult = (float)(current.Mode == NpcLevelMode.Multiplier ? current.Value : current.Value / 1000m) };
            else if (current.TryGetRaw(out var currentRaw))
                npc.Level = new Mutagen.Bethesda.Fallout4.NpcLevel { Level = unchecked((short)currentRaw) };
        }
        npc.Flags = (Mutagen.Bethesda.Fallout4.Npc.Flag)rawFlags;
        if (patch.XpValueOffset is { } xp) npc.XpValueOffset = xp;
        if (patch.CalcMinLevel is { } min) npc.CalcMinLevel = unchecked((short)min);
        if (patch.CalcMaxLevel is { } max) npc.CalcMaxLevel = unchecked((short)max);
        if (patch.DispositionBase is { } disposition) npc.DispositionBase = disposition;
        if (patch.BleedoutOverride is { } bleedout) npc.BleedoutOverride = bleedout;
    }

    private static void ApplyFlagPatch(GameEdition edition, ref uint raw, NpcFlagPatch? patch)
    {
        if (patch is null) return;
        foreach (var flag in patch.Set)
            if (NpcFlagExtensions.TryGetMask(edition, flag, out var mask)) raw |= mask;
        foreach (var flag in patch.Clear)
            if (NpcFlagExtensions.TryGetMask(edition, flag, out var mask)) raw &= ~mask;
    }

    private static void ApplyPlayerSkills(Mutagen.Bethesda.Skyrim.Npc npc, NpcPlayerSkillsPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var skills = npc.PlayerSkills ??= new Mutagen.Bethesda.Skyrim.PlayerSkills();
        if (patch.Health is { } health) skills.Health = health;
        if (patch.Magicka is { } magicka) skills.Magicka = magicka;
        if (patch.Stamina is { } stamina) skills.Stamina = stamina;
        if (patch.Values is not null)
            foreach (var (skill, value) in patch.Values)
                skills.SkillValues[Enum.Parse<Mutagen.Bethesda.Skyrim.Skill>(skill.ToString())] = value;
        if (patch.Offsets is not null)
            foreach (var (skill, value) in patch.Offsets)
                skills.SkillOffsets[Enum.Parse<Mutagen.Bethesda.Skyrim.Skill>(skill.ToString())] = value;
        if (patch.FarAwayModelDistance is { } farAwayModelDistance)
            skills.FarAwayModelDistance = farAwayModelDistance;
        if (patch.GearedUpWeapons is { } gearedUpWeapons)
            skills.GearedUpWeapons = gearedUpWeapons;
    }

    private static Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag SetFemaleFlag(
        Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag flags, bool female) =>
        female ? flags | Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Female
            : flags & ~Mutagen.Bethesda.Skyrim.NpcConfiguration.Flag.Female;

    private static Mutagen.Bethesda.Fallout4.Npc.Flag SetFemaleFlag(
        Mutagen.Bethesda.Fallout4.Npc.Flag flags, bool female) =>
        female ? flags | Mutagen.Bethesda.Fallout4.Npc.Flag.Female
            : flags & ~Mutagen.Bethesda.Fallout4.Npc.Flag.Female;
}
