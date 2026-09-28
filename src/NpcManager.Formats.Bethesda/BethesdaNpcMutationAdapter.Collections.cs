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
    private static NpcKeywordSnapshot ReadSkyrimKeywords(Mutagen.Bethesda.Skyrim.INpcGetter npc) =>
        new(ReadKeywordList(npc.Keywords ?? []), ImmutableArray<FormReference>.Empty);

    private static NpcKeywordSnapshot ReadFallout4Keywords(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new(ReadKeywordList(npc.Keywords ?? []), ReadKeywordList(npc.AttachParentSlots ?? []));

    private static ImmutableArray<FormReference> ReadKeywordList<T>(IEnumerable<Mutagen.Bethesda.Plugins.IFormLinkGetter<T>> links)
        where T : class, IMajorRecordGetter =>
        links.Select(link => ToReference(link.FormKey)).Where(reference => reference is not null).Select(reference => reference!.Value).ToImmutableArray();

    private static void ApplySkyrimKeywords(Mutagen.Bethesda.Skyrim.Npc npc, NpcKeywordPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Keywords ??= [];
        ApplyKeywordList(npc.Keywords, patch.Keywords);
    }

    private static void ApplyFallout4Keywords(Mutagen.Bethesda.Fallout4.Npc npc, NpcKeywordPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Keywords ??= [];
        ApplyKeywordList(npc.Keywords, patch.Keywords);
        if (patch.AttachParentSlots is { } appr)
        {
            npc.AttachParentSlots ??= [];
            ApplyKeywordList(npc.AttachParentSlots, appr);
        }
    }

    private static NpcFactionSnapshot ReadSkyrimFactions(Mutagen.Bethesda.Skyrim.INpcGetter npc) =>
        new((npc.Factions ?? []).Select(entry => new NpcFactionEntry(
            ToReference(entry.Faction.FormKey) ?? throw new InvalidDataException("Skyrim SNAM contains a null faction."), entry.Rank)).ToImmutableArray());

    private static NpcFactionSnapshot ReadFallout4Factions(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new((npc.Factions ?? []).Select(entry => new NpcFactionEntry(
            ToReference(entry.Faction.FormKey) ?? throw new InvalidDataException("Fallout 4 SNAM contains a null faction."), entry.Rank)).ToImmutableArray());

    private static void ApplySkyrimFactions(Mutagen.Bethesda.Skyrim.Npc npc, NpcFactionPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var updated = ApplyFactionOperations(ReadSkyrimFactions(npc).Factions, patch);
        npc.Factions.Clear();
        foreach (var entry in updated)
            npc.Factions.Add(new Mutagen.Bethesda.Skyrim.RankPlacement
            {
                Faction = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IFactionGetter>(ToFormKey(entry.Faction)),
                Rank = entry.Rank
            });
    }

    private static void ApplyFallout4Factions(Mutagen.Bethesda.Fallout4.Npc npc, NpcFactionPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var updated = ApplyFactionOperations(ReadFallout4Factions(npc).Factions, patch);
        npc.Factions.Clear();
        foreach (var entry in updated)
            npc.Factions.Add(new Mutagen.Bethesda.Fallout4.RankPlacement
            {
                Faction = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IFactionGetter>(ToFormKey(entry.Faction)),
                Rank = entry.Rank
            });
    }

    private static ImmutableArray<NpcFactionEntry> ApplyFactionOperations(
        ImmutableArray<NpcFactionEntry> current, NpcFactionPatch patch)
    {
        var values = (patch.Replace ?? current).ToBuilder();
        foreach (var faction in patch.Remove)
            for (var index = values.Count - 1; index >= 0; index--)
                if (values[index].Faction == faction) values.RemoveAt(index);
        foreach (var entry in patch.Update)
            for (var index = 0; index < values.Count; index++)
                if (values[index].Faction == entry.Faction) values[index] = entry;
        foreach (var entry in patch.Add)
            if (!values.Any(currentEntry => currentEntry.Faction == entry.Faction)) values.Add(entry);
        return values.ToImmutable();
    }

    private static NpcInventorySnapshot ReadSkyrimInventory(Mutagen.Bethesda.Skyrim.INpcGetter npc) =>
        new((npc.Items ?? []).Select(entry => new NpcInventoryEntry(
            ToReference(entry.Item.Item.FormKey) ?? throw new InvalidDataException("Skyrim CNTO contains a null item."),
            entry.Item.Count)).ToImmutableArray());

    private static NpcInventorySnapshot ReadFallout4Inventory(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new((npc.Items ?? []).Select(entry => new NpcInventoryEntry(
            ToReference(entry.Item.Item.FormKey) ?? throw new InvalidDataException("Fallout 4 CNTO contains a null item."),
            entry.Item.Count)).ToImmutableArray());

    private static void ApplySkyrimInventory(Mutagen.Bethesda.Skyrim.Npc npc, NpcInventoryPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Items ??= [];
        var current = npc.Items.GroupBy(entry => entry.Item.Item.FormKey).ToDictionary(group => group.Key, group => group.First());
        var updated = ApplyInventoryOperations(ReadSkyrimInventory(npc).Items, patch);
        npc.Items.Clear();
        foreach (var entry in updated)
        {
            var key = ToFormKey(entry.Item);
            if (current.TryGetValue(key, out var existing))
            {
                existing.Item.Count = entry.Count;
                npc.Items.Add(existing);
            }
            else
            {
                npc.Items.Add(new Mutagen.Bethesda.Skyrim.ContainerEntry
                {
                    Item = new Mutagen.Bethesda.Skyrim.ContainerItem
                    {
                        Item = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IItemGetter>(key),
                        Count = entry.Count
                    }
                });
            }
        }
    }

    private static void ApplyFallout4Inventory(Mutagen.Bethesda.Fallout4.Npc npc, NpcInventoryPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Items ??= [];
        var current = npc.Items.GroupBy(entry => entry.Item.Item.FormKey).ToDictionary(group => group.Key, group => group.First());
        var updated = ApplyInventoryOperations(ReadFallout4Inventory(npc).Items, patch);
        npc.Items.Clear();
        foreach (var entry in updated)
        {
            var key = ToFormKey(entry.Item);
            if (current.TryGetValue(key, out var existing))
            {
                existing.Item.Count = entry.Count;
                npc.Items.Add(existing);
            }
            else
            {
                npc.Items.Add(new Mutagen.Bethesda.Fallout4.ContainerEntry
                {
                    Item = new Mutagen.Bethesda.Fallout4.ContainerItem
                    {
                        Item = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IItemGetter>(key),
                        Count = entry.Count
                    }
                });
            }
        }
    }

    private static ImmutableArray<NpcInventoryEntry> ApplyInventoryOperations(
        ImmutableArray<NpcInventoryEntry> current, NpcInventoryPatch patch)
    {
        var values = new List<NpcInventoryEntry>();
        foreach (var entry in patch.Replace ?? current) AddInventoryEntry(values, entry);
        if (patch.Replace is null)
        {
            foreach (var entry in patch.Remove)
                values.RemoveAll(currentEntry => currentEntry.Item == entry);
            foreach (var entry in patch.Update)
            {
                var index = values.FindIndex(currentEntry => currentEntry.Item == entry.Item);
                if (index >= 0) values[index] = entry;
            }
            foreach (var entry in patch.Add) AddInventoryEntry(values, entry);
        }
        return values.ToImmutableArray();
    }

    private static void AddInventoryEntry(List<NpcInventoryEntry> values, NpcInventoryEntry entry)
    {
        var index = values.FindIndex(current => current.Item == entry.Item);
        if (index < 0) values.Add(entry);
        else values[index] = values[index] with { Count = checked(values[index].Count + entry.Count) };
    }

    private static NpcOutfitSnapshot ReadSkyrimOutfits(Mutagen.Bethesda.Skyrim.INpcGetter npc) =>
        new(ToReference(npc.DefaultOutfit.FormKeyNullable), ToReference(npc.SleepingOutfit.FormKeyNullable));

    private static NpcOutfitSnapshot ReadFallout4Outfits(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new(ToReference(npc.DefaultOutfit.FormKeyNullable), ToReference(npc.SleepingOutfit.FormKeyNullable));

    private static void ApplySkyrimOutfits(Mutagen.Bethesda.Skyrim.Npc npc, NpcOutfitPatch? patch)
    {
        if (patch is null) return;
        if (patch.DefaultOutfit.IsSpecified)
            npc.DefaultOutfit = patch.DefaultOutfit.Value is { } value
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>(ToFormKey(value))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>();
        if (patch.SleepingOutfit.IsSpecified)
            npc.SleepingOutfit = patch.SleepingOutfit.Value is { } value
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>(ToFormKey(value))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IOutfitGetter>();
    }

    private static void ApplyFallout4Outfits(Mutagen.Bethesda.Fallout4.Npc npc, NpcOutfitPatch? patch)
    {
        if (patch is null) return;
        if (patch.DefaultOutfit.IsSpecified)
            npc.DefaultOutfit = patch.DefaultOutfit.Value is { } value
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IOutfitGetter>(ToFormKey(value))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IOutfitGetter>();
        if (patch.SleepingOutfit.IsSpecified)
            npc.SleepingOutfit = patch.SleepingOutfit.Value is { } value
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IOutfitGetter>(ToFormKey(value))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IOutfitGetter>();
    }

    private static NpcPerkSnapshot ReadSkyrimPerks(Mutagen.Bethesda.Skyrim.INpcGetter npc) =>
        new((npc.Perks ?? []).Select(entry => new NpcPerkEntry(
            ToReference(entry.Perk.FormKey) ?? throw new InvalidDataException("Skyrim PRKR contains a null perk."),
            entry.Rank)).ToImmutableArray());

    private static NpcPerkSnapshot ReadFallout4Perks(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new((npc.Perks ?? []).Select(entry => new NpcPerkEntry(
            ToReference(entry.Perk.FormKey) ?? throw new InvalidDataException("Fallout 4 PRKR contains a null perk."),
            entry.Rank)).ToImmutableArray());

    private static void ApplySkyrimPerks(Mutagen.Bethesda.Skyrim.Npc npc, NpcPerkPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Perks ??= [];
        var updated = ApplyPerkOperations(ReadSkyrimPerks(npc).Perks, patch);
        npc.Perks.Clear();
        foreach (var entry in updated)
            npc.Perks.Add(new Mutagen.Bethesda.Skyrim.PerkPlacement
            {
                Perk = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IPerkGetter>(ToFormKey(entry.Perk)),
                Rank = entry.Rank
            });
    }

    private static void ApplyFallout4Perks(Mutagen.Bethesda.Fallout4.Npc npc, NpcPerkPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Perks ??= [];
        var updated = ApplyPerkOperations(ReadFallout4Perks(npc).Perks, patch);
        npc.Perks.Clear();
        foreach (var entry in updated)
            npc.Perks.Add(new Mutagen.Bethesda.Fallout4.PerkPlacement
            {
                Perk = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IPerkGetter>(ToFormKey(entry.Perk)),
                Rank = entry.Rank
            });
    }

    private static ImmutableArray<NpcPerkEntry> ApplyPerkOperations(
        ImmutableArray<NpcPerkEntry> current, NpcPerkPatch patch)
    {
        var values = (patch.Replace ?? current).ToBuilder();
        foreach (var perk in patch.Remove)
            for (var index = values.Count - 1; index >= 0; index--)
                if (values[index].Perk == perk) values.RemoveAt(index);
        foreach (var entry in patch.Update)
            for (var index = 0; index < values.Count; index++)
                if (values[index].Perk == entry.Perk) values[index] = entry;
        foreach (var entry in patch.Add)
            if (!values.Any(currentEntry => currentEntry.Perk == entry.Perk)) values.Add(entry);
        return values.ToImmutable();
    }

    private static NpcActorEffectSnapshot ReadSkyrimActorEffects(Mutagen.Bethesda.Skyrim.INpcGetter npc) =>
        new(ReadSpellReferences(npc.ActorEffect ?? []));

    private static NpcActorEffectSnapshot ReadFallout4ActorEffects(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new(ReadSpellReferences(npc.ActorEffect ?? []));

    private static NpcPropertySnapshot ReadFallout4Properties(Mutagen.Bethesda.Fallout4.INpcGetter npc) =>
        new((npc.Properties ?? []).Select(entry => new NpcPropertyEntry(
            ToReference(entry.ActorValue.FormKey) ?? throw new InvalidDataException("Fallout 4 PRPS contains a null actor-value reference."),
            entry.Value)).ToImmutableArray());

    private static ImmutableArray<FormReference> ReadSpellReferences<T>(IEnumerable<Mutagen.Bethesda.Plugins.IFormLinkGetter<T>> links)
        where T : class, IMajorRecordGetter =>
        links.Select(link => ToReference(link.FormKey) ?? throw new InvalidDataException("SPLO contains a null spell reference.")).ToImmutableArray();

    private static void ApplySkyrimActorEffects(Mutagen.Bethesda.Skyrim.Npc npc, NpcActorEffectPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.ActorEffect ??= [];
        ApplyActorEffectList(npc.ActorEffect, patch);
    }

    private static void ApplyFallout4ActorEffects(Mutagen.Bethesda.Fallout4.Npc npc, NpcActorEffectPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.ActorEffect ??= [];
        ApplyActorEffectList(npc.ActorEffect, patch);
    }

    private static void ApplyFallout4Properties(Mutagen.Bethesda.Fallout4.Npc npc, NpcPropertyPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        npc.Properties ??= [];
        var updated = ApplyPropertyOperations(ReadFallout4Properties(npc).Properties, patch);
        npc.Properties.Clear();
        foreach (var entry in updated)
            npc.Properties.Add(new Mutagen.Bethesda.Fallout4.ObjectProperty
            {
                ActorValue = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IActorValueInformationGetter>(ToFormKey(entry.ActorValue)),
                Value = entry.Value
            });
    }

    private static ImmutableArray<NpcPropertyEntry> ApplyPropertyOperations(
        ImmutableArray<NpcPropertyEntry> current, NpcPropertyPatch patch)
    {
        var values = (patch.Replace ?? current).ToBuilder();
        foreach (var property in patch.Remove)
            for (var index = values.Count - 1; index >= 0; index--)
                if (values[index].ActorValue == property) values.RemoveAt(index);
        foreach (var entry in patch.Update)
            for (var index = 0; index < values.Count; index++)
                if (values[index].ActorValue == entry.ActorValue) values[index] = entry;
        foreach (var entry in patch.Add)
            if (!values.Any(currentEntry => currentEntry.ActorValue == entry.ActorValue)) values.Add(entry);
        return values.ToImmutable();
    }

    private static void ApplyActorEffectList<T>(IList<Mutagen.Bethesda.Plugins.IFormLinkGetter<T>> target,
        NpcActorEffectPatch patch)
        where T : class, IMajorRecordGetter
    {
        if (patch.Replace is { } replacement)
        {
            target.Clear();
            foreach (var reference in replacement)
                target.Add(new Mutagen.Bethesda.Plugins.FormLink<T>(ToFormKey(reference)));
        }
        foreach (var reference in patch.Remove)
            target.RemoveWhere(link => link.FormKey == ToFormKey(reference));
        foreach (var reference in patch.Add)
            if (!target.Any(link => link.FormKey == ToFormKey(reference)))
                target.Add(new Mutagen.Bethesda.Plugins.FormLink<T>(ToFormKey(reference)));
    }

    private static void ApplyKeywordList<T>(IList<Mutagen.Bethesda.Plugins.IFormLinkGetter<T>> target, NpcKeywordListPatch patch)
        where T : class, IMajorRecordGetter
    {
        if (patch.Replace is { } replacement)
        {
            target.Clear();
            foreach (var reference in replacement) target.Add(new Mutagen.Bethesda.Plugins.FormLink<T>(ToFormKey(reference)));
        }
        foreach (var reference in patch.Remove)
            target.RemoveWhere(link => link.FormKey == ToFormKey(reference));
        foreach (var reference in patch.Add)
            if (!target.Any(link => link.FormKey == ToFormKey(reference))) target.Add(new Mutagen.Bethesda.Plugins.FormLink<T>(ToFormKey(reference)));
    }

}
