using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Compares override intent by value. Record equality is insufficient because
/// immutable arrays and dictionaries otherwise retain collection-instance
/// identity after a proposal is reconstructed from persisted data.
/// </summary>
internal static class NpcOverridePatchEquality
{
    public static bool Equals(NpcOverridePatch left, NpcOverridePatch right) =>
        left.EditorId == right.EditorId &&
        left.Name == right.Name &&
        left.Names == right.Names &&
        left.Archetype == right.Archetype &&
        left.Weight == right.Weight &&
        StatsEqual(left.Stats, right.Stats) &&
        KeywordPatchEqual(left.Keywords, right.Keywords) &&
        FactionPatchEqual(left.Factions, right.Factions) &&
        InventoryPatchEqual(left.Inventory, right.Inventory) &&
        left.Outfits == right.Outfits &&
        PerkPatchEqual(left.Perks, right.Perks) &&
        ActorEffectPatchEqual(left.ActorEffects, right.ActorEffects);

    private static bool StatsEqual(NpcStatsPatch? left, NpcStatsPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Level == right.Level &&
               left.XpValueOffset == right.XpValueOffset &&
               left.MagickaOffset == right.MagickaOffset &&
               left.StaminaOffset == right.StaminaOffset &&
               left.HealthOffset == right.HealthOffset &&
               left.CalcMinLevel == right.CalcMinLevel &&
               left.CalcMaxLevel == right.CalcMaxLevel &&
               left.SpeedMultiplier == right.SpeedMultiplier &&
               left.DispositionBase == right.DispositionBase &&
               left.BleedoutOverride == right.BleedoutOverride &&
               left.Height == right.Height &&
               PlayerSkillsEqual(left.PlayerSkills, right.PlayerSkills) &&
               FlagPatchEqual(left.Flags, right.Flags);
    }

    private static bool PlayerSkillsEqual(
        NpcPlayerSkillsPatch? left,
        NpcPlayerSkillsPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Health == right.Health &&
               left.Magicka == right.Magicka &&
               left.Stamina == right.Stamina &&
               DictionaryEqual(left.Values, right.Values) &&
               DictionaryEqual(left.Offsets, right.Offsets) &&
               left.FarAwayModelDistance == right.FarAwayModelDistance &&
               left.GearedUpWeapons == right.GearedUpWeapons;
    }

    private static bool FlagPatchEqual(NpcFlagPatch? left, NpcFlagPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return ArrayEqual(left.Set, right.Set) && ArrayEqual(left.Clear, right.Clear);
    }

    private static bool KeywordPatchEqual(NpcKeywordPatch? left, NpcKeywordPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return KeywordListEqual(left.Keywords, right.Keywords) &&
               KeywordListEqual(left.AttachParentSlots, right.AttachParentSlots);
    }

    private static bool KeywordListEqual(NpcKeywordListPatch? left, NpcKeywordListPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return NullableArrayEqual(left.Replace, right.Replace) &&
               ArrayEqual(left.Add, right.Add) &&
               ArrayEqual(left.Remove, right.Remove);
    }

    private static bool FactionPatchEqual(NpcFactionPatch? left, NpcFactionPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return NullableArrayEqual(left.Replace, right.Replace) &&
               ArrayEqual(left.Add, right.Add) &&
               ArrayEqual(left.Update, right.Update) &&
               ArrayEqual(left.Remove, right.Remove);
    }

    private static bool InventoryPatchEqual(NpcInventoryPatch? left, NpcInventoryPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return NullableArrayEqual(left.Replace, right.Replace) &&
               ArrayEqual(left.Add, right.Add) &&
               ArrayEqual(left.Update, right.Update) &&
               ArrayEqual(left.Remove, right.Remove);
    }

    private static bool PerkPatchEqual(NpcPerkPatch? left, NpcPerkPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return NullableArrayEqual(left.Replace, right.Replace) &&
               ArrayEqual(left.Add, right.Add) &&
               ArrayEqual(left.Update, right.Update) &&
               ArrayEqual(left.Remove, right.Remove);
    }

    private static bool ActorEffectPatchEqual(
        NpcActorEffectPatch? left,
        NpcActorEffectPatch? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return NullableArrayEqual(left.Replace, right.Replace) &&
               ArrayEqual(left.Add, right.Add) &&
               ArrayEqual(left.Remove, right.Remove);
    }

    private static bool DictionaryEqual<TKey, TValue>(
        ImmutableDictionary<TKey, TValue>? left,
        ImmutableDictionary<TKey, TValue>? right)
        where TKey : notnull
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Count == right.Count &&
               left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                                EqualityComparer<TValue>.Default.Equals(pair.Value, value));
    }

    private static bool NullableArrayEqual<T>(
        ImmutableArray<T>? left,
        ImmutableArray<T>? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue || ArrayEqual(left.Value, right!.Value));

    private static bool ArrayEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefaultOrEmpty || right.IsDefaultOrEmpty
            ? left.IsDefaultOrEmpty && right.IsDefaultOrEmpty
            : left.SequenceEqual(right);
}
