namespace NpcManager.Domain;

#pragma warning disable CA1711

public enum NpcLevelMode
{
    Fixed,
    Multiplier
}

/// <summary>Typed representation of the ACBS level/level-multiplier union.</summary>
public readonly record struct NpcLevelValue(NpcLevelMode Mode, decimal Value)
{
    public bool TryGetRaw(out ushort raw)
    {
        raw = 0;
        if (Value < 0) return false;
        if (Mode == NpcLevelMode.Fixed)
        {
            if (Value > ushort.MaxValue || decimal.Truncate(Value) != Value) return false;
            raw = (ushort)Value;
            return true;
        }

        if (Mode != NpcLevelMode.Multiplier) return false;

        if (Value > 65.535m || decimal.Round(Value, 3) != Value) return false;
        raw = (ushort)decimal.Round(Value * 1000m, 0, MidpointRounding.AwayFromZero);
        return true;
    }

    public override string ToString() => Mode == NpcLevelMode.Fixed
        ? decimal.Truncate(Value).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Surface-level ACBS flags with explicit game-specific names.</summary>
public enum NpcFlag
{
    Female,
    Essential,
    IsCharGenFacePreset,
    Respawn,
    AutoCalcStats,
    Unique,
    DoesntAffectStealthMeter,
    Fallout4CalcForEachTemplate,
    SkyrimUseTemplate,
    Protected,
    Summonable,
    DoesNotBleed,
    BleedoutOverride,
    OppositeGenderAnims,
    SimpleActor,
    Fallout4NoActivationOrHellos,
    Fallout4DiffuseAlphaTest,
    SkyrimLoopedScript,
    SkyrimLoopedAudio,
    IsGhost,
    Invulnerable
}

public enum NpcSkill
{
    OneHanded,
    TwoHanded,
    Archery,
    Block,
    Smithing,
    HeavyArmor,
    LightArmor,
    Pickpocket,
    Lockpicking,
    Sneak,
    Alchemy,
    Speech,
    Alteration,
    Conjuration,
    Destruction,
    Illusion,
    Restoration,
    Enchanting
}

public static class NpcSkillExtensions
{
    public static bool TryParseWireName(string value, out NpcSkill skill)
    {
        skill = default;
        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<NpcSkill>())
        {
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                skill = candidate;
                return true;
            }
        }

        return false;
    }

    public static string ToWireName(this NpcSkill skill) => skill.ToString().ToLowerInvariant();
}

public static class NpcFlagExtensions
{
    public static bool TryGetMask(GameEdition edition, NpcFlag flag, out uint mask)
    {
        mask = flag switch
        {
            NpcFlag.Female => 0x00000001,
            NpcFlag.Essential => 0x00000002,
            NpcFlag.IsCharGenFacePreset => 0x00000004,
            NpcFlag.Respawn => 0x00000008,
            NpcFlag.AutoCalcStats => 0x00000010,
            NpcFlag.Unique => 0x00000020,
            NpcFlag.DoesntAffectStealthMeter => 0x00000040,
            NpcFlag.Protected => 0x00000800,
            NpcFlag.Summonable => 0x00004000,
            NpcFlag.DoesNotBleed => 0x00010000,
            NpcFlag.BleedoutOverride => 0x00020000,
            NpcFlag.OppositeGenderAnims => 0x00080000,
            NpcFlag.SimpleActor => 0x00100000,
            NpcFlag.IsGhost => 0x20000000,
            NpcFlag.Invulnerable => 0x80000000,
            NpcFlag.Fallout4CalcForEachTemplate => edition == GameEdition.Fallout4 ? 0x00000080u : 0u,
            NpcFlag.SkyrimUseTemplate => edition == GameEdition.SkyrimSpecialEdition ? 0x00000080u : 0u,
            NpcFlag.Fallout4NoActivationOrHellos => edition == GameEdition.Fallout4 ? 0x00800000u : 0u,
            NpcFlag.Fallout4DiffuseAlphaTest => edition == GameEdition.Fallout4 ? 0x01000000u : 0u,
            NpcFlag.SkyrimLoopedScript => edition == GameEdition.SkyrimSpecialEdition ? 0x00200000u : 0u,
            NpcFlag.SkyrimLoopedAudio => edition == GameEdition.SkyrimSpecialEdition ? 0x00400000u : 0u,
            _ => 0
        };
        return mask != 0;
    }

    public static bool TryParseWireName(string value, out NpcFlag flag)
    {
        flag = default;
        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<NpcFlag>())
        {
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                flag = candidate;
                return true;
            }
        }

        return false;
    }

    public static string ToWireName(this NpcFlag flag) => flag.ToString().ToLowerInvariant();
}
