using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcMutationService
{
    private static void ValidateFactions(NpcMutationRequest request, NpcFactionPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        if (patch.Replace is not null && (!patch.Add.IsDefaultOrEmpty || !patch.Update.IsDefaultOrEmpty || !patch.Remove.IsDefaultOrEmpty))
            diagnostics.Add(new Diagnostic("faction-replace-conflict", DiagnosticSeverity.Error,
                "A faction replacement cannot be combined with add, update, or remove operations."));
        ValidateFactionEntries(patch.Replace ?? [], "factions", diagnostics, referencePlugins);
        ValidateFactionEntries(patch.Add, "add-faction", diagnostics, referencePlugins);
        ValidateFactionEntries(patch.Update, "update-faction", diagnostics, referencePlugins);
        ValidateFactionReferences(patch.Remove, "remove-faction", diagnostics, referencePlugins);
        if (patch.Add.Select(entry => entry.Faction).Intersect(patch.Remove).Any() ||
            patch.Update.Select(entry => entry.Faction).Intersect(patch.Remove).Any() ||
            patch.Add.Select(entry => entry.Faction).Intersect(patch.Update.Select(entry => entry.Faction)).Any())
            diagnostics.Add(new Diagnostic("faction-conflict", DiagnosticSeverity.Error,
                "A faction cannot be added, updated, and removed in the same mutation."));
    }

    private static void ValidateFactionEntries(IEnumerable<NpcFactionEntry> entries, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = entries.ToArray();
        foreach (var entry in values)
            ValidateFactionReference(entry.Faction, field, diagnostics, referencePlugins);
        if (values.Select(entry => entry.Faction).Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("faction-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate faction references."));
    }

    private static void ValidateFactionReferences(IEnumerable<FormReference> references, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = references.ToArray();
        foreach (var reference in values) ValidateFactionReference(reference, field, diagnostics, referencePlugins);
        if (values.Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("faction-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate faction references."));
    }

    private static void ValidateFactionReference(FormReference reference, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (reference.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("faction-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
        if (!referencePlugins.Contains(reference.Plugin.Value))
            diagnostics.Add(new Diagnostic("faction-plugin-unresolved", DiagnosticSeverity.Error,
                $"{field} reference {reference} is not in the source plugin or its declared masters."));
    }

    private static void AddFactionChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcFactionPatch? patch, NpcFactionSnapshot? current)
    {
        if (patch is null) return;
        var before = current?.Factions ?? ImmutableArray<NpcFactionEntry>.Empty;
        var after = ApplyFactionOperations(before, patch);
        if (!before.SequenceEqual(after))
            changes.Add(new MutationChange("Factions", FormatFactions(before), FormatFactions(after)));
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

    private static string FormatFactions(IEnumerable<NpcFactionEntry> factions) =>
        string.Join(",", factions.Select(entry => $"{entry.Faction}={entry.Rank.ToString(CultureInfo.InvariantCulture)}"));

    private static void ValidateInventory(NpcMutationRequest request, NpcInventoryPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        if (patch.Replace is not null && (!patch.Add.IsDefaultOrEmpty || !patch.Update.IsDefaultOrEmpty || !patch.Remove.IsDefaultOrEmpty))
            diagnostics.Add(new Diagnostic("inventory-replace-conflict", DiagnosticSeverity.Error,
                "An inventory replacement cannot be combined with add, update, or remove operations."));
        ValidateInventoryEntries(patch.Replace ?? [], "inventory", diagnostics,
            allowDuplicates: true, referencePlugins);
        ValidateInventoryEntries(patch.Add, "add-inventory", diagnostics,
            allowDuplicates: true, referencePlugins);
        ValidateInventoryEntries(patch.Update, "update-inventory", diagnostics,
            allowDuplicates: false, referencePlugins);
        ValidateInventoryReferences(patch.Remove, "remove-inventory", diagnostics, referencePlugins);
        if (patch.Add.Select(entry => entry.Item).Intersect(patch.Remove).Any() ||
            patch.Update.Select(entry => entry.Item).Intersect(patch.Remove).Any() ||
            patch.Add.Select(entry => entry.Item).Intersect(patch.Update.Select(entry => entry.Item)).Any())
            diagnostics.Add(new Diagnostic("inventory-conflict", DiagnosticSeverity.Error,
                "An inventory item cannot be added, updated, and removed in the same mutation."));
    }

    private static void ValidateInventoryEntries(IEnumerable<NpcInventoryEntry> entries, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, bool allowDuplicates,
        ImmutableHashSet<string> referencePlugins)
    {
        var values = entries.ToArray();
        foreach (var entry in values)
            ValidateInventoryReference(entry.Item, field, diagnostics, referencePlugins);
        if (!allowDuplicates && values.Select(entry => entry.Item).Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("inventory-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate item references."));
        var sums = new Dictionary<FormReference, long>();
        foreach (var entry in values)
        {
            sums.TryGetValue(entry.Item, out var current);
            current += entry.Count;
            sums[entry.Item] = current;
            if (current is < int.MinValue or > int.MaxValue)
                diagnostics.Add(new Diagnostic("inventory-count-overflow", DiagnosticSeverity.Error,
                    $"{field} count merging exceeds the signed 32-bit CNTO range for {entry.Item}."));
        }
    }

    private static void ValidateInventoryReferences(IEnumerable<FormReference> references, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = references.ToArray();
        foreach (var reference in values)
            ValidateInventoryReference(reference, field, diagnostics, referencePlugins);
        if (values.Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("inventory-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate item references."));
    }

    private static void ValidateInventoryReference(FormReference reference, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (reference.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("inventory-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
        if (!referencePlugins.Contains(reference.Plugin.Value))
            diagnostics.Add(new Diagnostic("inventory-plugin-unresolved", DiagnosticSeverity.Error,
                $"{field} reference {reference} is not in the source plugin or its declared masters."));
    }

    private static void AddInventoryChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcInventoryPatch? patch, NpcInventorySnapshot? current)
    {
        if (patch is null) return;
        var before = current?.Items ?? ImmutableArray<NpcInventoryEntry>.Empty;
        var after = ApplyInventoryOperations(before, patch);
        if (!before.SequenceEqual(after))
            changes.Add(new MutationChange("Inventory", FormatInventory(before), FormatInventory(after)));
    }

    private static ImmutableArray<NpcInventoryEntry> ApplyInventoryOperations(
        ImmutableArray<NpcInventoryEntry> current, NpcInventoryPatch patch)
    {
        var values = new List<NpcInventoryEntry>();
        foreach (var entry in patch.Replace ?? current) AddInventoryEntry(values, entry);
        if (patch.Replace is null)
        {
            foreach (var item in patch.Remove) values.RemoveAll(entry => entry.Item == item);
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

    private static string FormatInventory(IEnumerable<NpcInventoryEntry> entries) =>
        string.Join(",", entries.Select(entry => $"{entry.Item}={entry.Count.ToString(CultureInfo.InvariantCulture)}"));

    private static void ValidateOutfits(NpcMutationRequest request, NpcOutfitPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        ValidateOutfitReference(patch.DefaultOutfit, "default-outfit", diagnostics, referencePlugins);
        ValidateOutfitReference(patch.SleepingOutfit, "sleep-outfit", diagnostics, referencePlugins);
    }

    private static void ValidateOutfitReference(OptionalFormReference reference, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (!reference.IsSpecified || reference.Value is not { } value) return;
        if (value.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("outfit-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
        if (!referencePlugins.Contains(value.Plugin.Value))
            diagnostics.Add(new Diagnostic("outfit-plugin-unresolved", DiagnosticSeverity.Error,
                $"{field} reference {value} is not in the source plugin or its declared masters."));
    }

    private static void AddOutfitChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcOutfitPatch? patch, NpcOutfitSnapshot? current)
    {
        if (patch is null || current is null) return;
        AddOptionalReferenceChange(changes, "DefaultOutfit", patch.DefaultOutfit, current.DefaultOutfit);
        AddOptionalReferenceChange(changes, "SleepingOutfit", patch.SleepingOutfit, current.SleepingOutfit);
    }

    private static void AddOptionalReferenceChange(ImmutableArray<MutationChange>.Builder changes, string field,
        OptionalFormReference requested, FormReference? current)
    {
        if (!requested.IsSpecified || requested.Value == current) return;
        changes.Add(new MutationChange(field, current?.ToString() ?? "none", requested.Value?.ToString() ?? "none"));
    }

    private static void ValidatePerks(NpcMutationRequest request, NpcPerkPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        if (patch.Replace is not null && (!patch.Add.IsDefaultOrEmpty || !patch.Update.IsDefaultOrEmpty || !patch.Remove.IsDefaultOrEmpty))
            diagnostics.Add(new Diagnostic("perk-replace-conflict", DiagnosticSeverity.Error,
                "A perk replacement cannot be combined with add, update, or remove operations."));
        ValidatePerkEntries(patch.Replace ?? [], "perks", diagnostics, referencePlugins);
        ValidatePerkEntries(patch.Add, "add-perk", diagnostics, referencePlugins);
        ValidatePerkEntries(patch.Update, "update-perk", diagnostics, referencePlugins);
        ValidatePerkReferences(patch.Remove, "remove-perk", diagnostics, referencePlugins);
        if (patch.Add.Select(entry => entry.Perk).Intersect(patch.Remove).Any() ||
            patch.Update.Select(entry => entry.Perk).Intersect(patch.Remove).Any() ||
            patch.Add.Select(entry => entry.Perk).Intersect(patch.Update.Select(entry => entry.Perk)).Any())
            diagnostics.Add(new Diagnostic("perk-conflict", DiagnosticSeverity.Error,
                "A perk cannot be added, updated, and removed in the same mutation."));
    }

    private static void ValidatePerkEntries(IEnumerable<NpcPerkEntry> entries, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = entries.ToArray();
        foreach (var entry in values)
            ValidatePerkReference(entry.Perk, field, diagnostics, referencePlugins);
        if (values.Select(entry => entry.Perk).Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("perk-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate perk references."));
    }

    private static void ValidatePerkReferences(IEnumerable<FormReference> references, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = references.ToArray();
        foreach (var reference in values)
            ValidatePerkReference(reference, field, diagnostics, referencePlugins);
        if (values.Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("perk-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate perk references."));
    }

    private static void ValidatePerkReference(FormReference reference, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (reference.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("perk-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
        if (!referencePlugins.Contains(reference.Plugin.Value))
            diagnostics.Add(new Diagnostic("perk-plugin-unresolved", DiagnosticSeverity.Error,
                $"{field} reference {reference} is not in the source plugin or its declared masters."));
    }

    private static void AddPerkChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcPerkPatch? patch, NpcPerkSnapshot? current)
    {
        if (patch is null || current is null) return;
        var before = current.Perks;
        var after = ApplyPerkOperations(before, patch);
        if (!before.SequenceEqual(after))
            changes.Add(new MutationChange("Perks", FormatPerks(before), FormatPerks(after)));
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

    private static string FormatPerks(IEnumerable<NpcPerkEntry> perks) =>
        string.Join(",", perks.Select(entry => $"{entry.Perk}={entry.Rank.ToString(CultureInfo.InvariantCulture)}"));

    private static void ValidateActorEffects(NpcMutationRequest request, NpcActorEffectPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        if (patch.Replace is not null && (!patch.Add.IsDefaultOrEmpty || !patch.Remove.IsDefaultOrEmpty))
            diagnostics.Add(new Diagnostic("actor-effect-replace-conflict", DiagnosticSeverity.Error,
                "An actor-effect replacement cannot be combined with add or remove operations."));
        ValidateActorEffectReferences(patch.Replace ?? [], "actor-effects", diagnostics, referencePlugins);
        ValidateActorEffectReferences(patch.Add, "add-actor-effect", diagnostics, referencePlugins);
        ValidateActorEffectReferences(patch.Remove, "remove-actor-effect", diagnostics, referencePlugins);
        if (patch.Add.Intersect(patch.Remove).Any())
            diagnostics.Add(new Diagnostic("actor-effect-conflict", DiagnosticSeverity.Error,
                "An actor effect cannot be added and removed in the same mutation."));
    }

    private static void ValidateActorEffectReferences(IEnumerable<FormReference> references, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = references.ToArray();
        foreach (var reference in values)
        {
            if (reference.FormId.Value == 0)
                diagnostics.Add(new Diagnostic("actor-effect-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
            if (!referencePlugins.Contains(reference.Plugin.Value))
                diagnostics.Add(new Diagnostic("actor-effect-plugin-unresolved", DiagnosticSeverity.Error,
                    $"{field} reference {reference} is not in the source plugin or its declared masters."));
        }
        if (values.Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("actor-effect-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate spell references."));
    }

    private static void AddActorEffectChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcActorEffectPatch? patch, NpcActorEffectSnapshot? current)
    {
        if (patch is null || current is null) return;
        var before = current.ActorEffects;
        var after = ApplyActorEffectOperations(before, patch);
        if (!before.SequenceEqual(after))
            changes.Add(new MutationChange("ActorEffects", string.Join(",", before), string.Join(",", after)));
    }

    private static ImmutableArray<FormReference> ApplyActorEffectOperations(
        ImmutableArray<FormReference> current, NpcActorEffectPatch patch)
    {
        var values = (patch.Replace ?? current).ToBuilder();
        foreach (var reference in patch.Remove)
            for (var index = values.Count - 1; index >= 0; index--)
                if (values[index] == reference) values.RemoveAt(index);
        foreach (var reference in patch.Add)
            if (!values.Contains(reference)) values.Add(reference);
        return values.ToImmutable();
    }

    private static void ValidateProperties(NpcMutationRequest request, NpcPropertyPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        if (request.Edition != GameEdition.Fallout4)
            diagnostics.Add(new Diagnostic("skyrim-properties-unsupported", DiagnosticSeverity.Error,
                "NPC PRPS properties are Fallout 4-only in the pinned upstream editor and record model."));
        if (patch.Replace is not null && (!patch.Add.IsDefaultOrEmpty || !patch.Update.IsDefaultOrEmpty || !patch.Remove.IsDefaultOrEmpty))
            diagnostics.Add(new Diagnostic("property-replace-conflict", DiagnosticSeverity.Error,
                "A property replacement cannot be combined with add, update, or remove operations."));
        ValidatePropertyEntries(patch.Replace ?? [], "properties", diagnostics, referencePlugins);
        ValidatePropertyEntries(patch.Add, "add-property", diagnostics, referencePlugins);
        ValidatePropertyEntries(patch.Update, "update-property", diagnostics, referencePlugins);
        ValidatePropertyReferences(patch.Remove, "remove-property", diagnostics, referencePlugins);
        if (patch.Add.Select(entry => entry.ActorValue).Intersect(patch.Remove).Any() ||
            patch.Update.Select(entry => entry.ActorValue).Intersect(patch.Remove).Any() ||
            patch.Add.Select(entry => entry.ActorValue).Intersect(patch.Update.Select(entry => entry.ActorValue)).Any())
            diagnostics.Add(new Diagnostic("property-conflict", DiagnosticSeverity.Error,
                "An actor-value property cannot be added, updated, and removed in the same mutation."));
    }

    private static void ValidatePropertyEntries(IEnumerable<NpcPropertyEntry> entries, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = entries.ToArray();
        foreach (var entry in values)
        {
            ValidatePropertyReference(entry.ActorValue, field, diagnostics, referencePlugins);
            if (float.IsNaN(entry.Value) || float.IsInfinity(entry.Value))
                diagnostics.Add(new Diagnostic("property-value-invalid", DiagnosticSeverity.Error,
                    $"{field} contains a non-finite IEEE-754 single value."));
        }
        if (values.Select(entry => entry.ActorValue).Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("property-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate actor-value references."));
    }

    private static void ValidatePropertyReferences(IEnumerable<FormReference> references, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var values = references.ToArray();
        foreach (var reference in values)
            ValidatePropertyReference(reference, field, diagnostics, referencePlugins);
        if (values.Distinct().Count() != values.Length)
            diagnostics.Add(new Diagnostic("property-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate actor-value references."));
    }

    private static void ValidatePropertyReference(FormReference reference, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (reference.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("property-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
        if (!referencePlugins.Contains(reference.Plugin.Value))
            diagnostics.Add(new Diagnostic("property-plugin-unresolved", DiagnosticSeverity.Error,
                $"{field} reference {reference} is not in the source plugin or its declared masters."));
    }

    private static void AddPropertyChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcPropertyPatch? patch, NpcPropertySnapshot? current)
    {
        if (patch is null || current is null) return;
        var before = current.Properties;
        var after = ApplyPropertyOperations(before, patch);
        if (!before.SequenceEqual(after))
            changes.Add(new MutationChange("Properties", FormatProperties(before), FormatProperties(after)));
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

    private static string FormatProperties(IEnumerable<NpcPropertyEntry> properties) =>
        string.Join(",", properties.Select(entry => $"{entry.ActorValue}={entry.Value.ToString("R", CultureInfo.InvariantCulture)}"));

    private static void ValidateKeywords(NpcMutationRequest request, NpcKeywordPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        if (patch is null || patch.IsEmpty) return;
        ValidateKeywordList(patch.Keywords, "keywords", diagnostics, referencePlugins);
        if (patch.AttachParentSlots is not null && request.Edition != GameEdition.Fallout4)
            diagnostics.Add(new Diagnostic("skyrim-appr-unsupported", DiagnosticSeverity.Error, "Attach-parent-slot APPR is Fallout 4-only."));
        if (patch.AttachParentSlots is { } appr)
            ValidateKeywordList(appr, "attach-parent-slots", diagnostics, referencePlugins);
    }

    private static void ValidateKeywordList(NpcKeywordListPatch patch, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableHashSet<string> referencePlugins)
    {
        var all = (patch.Replace ?? []).Concat(patch.Add).Concat(patch.Remove).ToArray();
        foreach (var reference in all)
        {
            if (reference.FormId.Value == 0)
                diagnostics.Add(new Diagnostic("keyword-form-id-invalid", DiagnosticSeverity.Error, $"{field} contains the null FormID."));
            if (!referencePlugins.Contains(reference.Plugin.Value))
                diagnostics.Add(new Diagnostic("keyword-plugin-unresolved", DiagnosticSeverity.Error,
                    $"{field} reference {reference} is not in the source plugin or its declared masters."));
        }
        foreach (var values in new[] { patch.Replace ?? [], patch.Add, patch.Remove })
            if (values.Distinct().Count() != values.Length)
                diagnostics.Add(new Diagnostic("keyword-duplicate", DiagnosticSeverity.Error, $"{field} contains duplicate references."));
        if (patch.Add.Intersect(patch.Remove).Any())
            diagnostics.Add(new Diagnostic("keyword-conflict", DiagnosticSeverity.Error, $"{field} cannot add and remove the same reference."));
    }

    private static void AddKeywordChanges(ImmutableArray<MutationChange>.Builder changes, NpcKeywordPatch? patch, NpcKeywordSnapshot? current)
    {
        if (patch is null) return;
        var beforeKeywords = current?.Keywords ?? ImmutableArray<FormReference>.Empty;
        var afterKeywords = ApplyKeywordOperations(beforeKeywords, patch.Keywords);
        if (!beforeKeywords.SequenceEqual(afterKeywords))
            changes.Add(new MutationChange("Keywords", FormatReferences(beforeKeywords), FormatReferences(afterKeywords)));
        if (patch.AttachParentSlots is { } appr)
        {
            var beforeAppr = current?.AttachParentSlots ?? ImmutableArray<FormReference>.Empty;
            var afterAppr = ApplyKeywordOperations(beforeAppr, appr);
            if (!beforeAppr.SequenceEqual(afterAppr)) changes.Add(new MutationChange("AttachParentSlots", FormatReferences(beforeAppr), FormatReferences(afterAppr)));
        }
    }

    private static ImmutableArray<FormReference> ApplyKeywordOperations(ImmutableArray<FormReference> current, NpcKeywordListPatch patch)
    {
        var values = (patch.Replace ?? current).ToBuilder();
        foreach (var reference in patch.Remove) values.Remove(reference);
        foreach (var reference in patch.Add) if (!values.Contains(reference)) values.Add(reference);
        return values.ToImmutable();
    }

    private static string FormatReferences(IEnumerable<FormReference> references) =>
        string.Join(",", references.Select(reference => reference.ToString()));

}
