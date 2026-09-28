using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record NpcFlagPatch(
    ImmutableArray<NpcFlag> Set,
    ImmutableArray<NpcFlag> Clear)
{
    public bool IsEmpty => Set.IsDefaultOrEmpty && Clear.IsDefaultOrEmpty;
}

public sealed record NpcPlayerSkillsPatch(
    ushort? Health,
    ushort? Magicka,
    ushort? Stamina,
    ImmutableDictionary<NpcSkill, byte>? Values,
    ImmutableDictionary<NpcSkill, byte>? Offsets,
    float? FarAwayModelDistance = null,
    byte? GearedUpWeapons = null)
{
    public bool IsEmpty => Health is null && Magicka is null && Stamina is null &&
                           (Values is null || Values.Count == 0) &&
                           (Offsets is null || Offsets.Count == 0) &&
                           FarAwayModelDistance is null && GearedUpWeapons is null;
}

public sealed record NpcStatsPatch(
    NpcLevelValue? Level,
    short? XpValueOffset,
    short? MagickaOffset,
    short? StaminaOffset,
    short? HealthOffset,
    ushort? CalcMinLevel,
    ushort? CalcMaxLevel,
    short? SpeedMultiplier,
    short? DispositionBase,
    short? BleedoutOverride,
    float? Height,
    NpcPlayerSkillsPatch? PlayerSkills,
    NpcFlagPatch? Flags)
{
    public bool IsEmpty => Level is null && XpValueOffset is null && MagickaOffset is null &&
                           StaminaOffset is null && HealthOffset is null && CalcMinLevel is null &&
                           CalcMaxLevel is null && SpeedMultiplier is null && DispositionBase is null &&
                           BleedoutOverride is null && Height is null && (PlayerSkills is null || PlayerSkills.IsEmpty) &&
                           (Flags is null || Flags.IsEmpty);
}

public sealed record NpcPlayerSkillsSnapshot(
    ushort Health,
    ushort Magicka,
    ushort Stamina,
    ImmutableDictionary<NpcSkill, byte> Values,
    ImmutableDictionary<NpcSkill, byte> Offsets,
    float FarAwayModelDistance = 0,
    byte GearedUpWeapons = 0);

public sealed record NpcStatsSnapshot(
    NpcLevelValue Level,
    short XpValueOffset,
    short MagickaOffset,
    short StaminaOffset,
    short HealthOffset,
    ushort CalcMinLevel,
    ushort CalcMaxLevel,
    short SpeedMultiplier,
    short DispositionBase,
    short BleedoutOverride,
    float? Height,
    NpcPlayerSkillsSnapshot? PlayerSkills,
    ImmutableHashSet<NpcFlag> Flags);
