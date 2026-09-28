using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed partial class MutationCommandHandler
{
    internal static bool TryArchetype(ParsedCommand command, out NpcArchetypePatch? archetype, out string errorMessage)
    {
        archetype = null;
        if (!TryReferenceOption(command, "race", out var race, out errorMessage) ||
            !TryReferenceOption(command, "voice", out var voice, out errorMessage) ||
            !TryReferenceOption(command, "class", out var npcClass, out errorMessage) ||
            !TryReferenceOption(command, "combat-style", out var combatStyle, out errorMessage))
            return false;
        if (race.IsSpecified || voice.IsSpecified || npcClass.IsSpecified || combatStyle.IsSpecified)
            archetype = new NpcArchetypePatch(race, voice, npcClass, combatStyle);
        errorMessage = string.Empty;
        return true;
    }

    internal static bool TryKeywordPatch(ParsedCommand command, GameEdition edition, out NpcKeywordPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryReferenceList(command, "keywords", out var replace, out errorMessage) ||
            !TryReferenceList(command, "add-keyword", out var add, out errorMessage) ||
            !TryReferenceList(command, "remove-keyword", out var remove, out errorMessage) ||
            !TryReferenceList(command, "appr", out var apprReplace, out errorMessage) ||
            !TryReferenceList(command, "add-appr", out var apprAdd, out errorMessage) ||
            !TryReferenceList(command, "remove-appr", out var apprRemove, out errorMessage)) return false;
        var keywords = new NpcKeywordListPatch(replace, add ?? ImmutableArray<FormReference>.Empty, remove ?? ImmutableArray<FormReference>.Empty);
        var hasAppr = apprReplace is not null || !(apprAdd ?? ImmutableArray<FormReference>.Empty).IsDefaultOrEmpty || !(apprRemove ?? ImmutableArray<FormReference>.Empty).IsDefaultOrEmpty;
        if (keywords.IsEmpty && !hasAppr) return true;
        patch = new NpcKeywordPatch(keywords, hasAppr ? new NpcKeywordListPatch(apprReplace, apprAdd ?? ImmutableArray<FormReference>.Empty, apprRemove ?? ImmutableArray<FormReference>.Empty) : null);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryReferenceList(ParsedCommand command, string option, out ImmutableArray<FormReference>? replace,
        out string errorMessage)
    {
        replace = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FormReference.TryParse(value, out var reference)) { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX references separated by commas."; return false; }
            if (builder.Contains(reference)) { errorMessage = $"--{option} contains duplicate reference {value}."; return false; }
            builder.Add(reference);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one reference."; return false; }
        replace = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddKeywordExpectations(ImmutableArray<MutationChange>.Builder changes, NpcKeywordPatch? patch)
    {
        if (patch is null) return;
        if (!patch.Keywords.IsEmpty) changes.Add(new MutationChange("Keywords", null, FormatKeywordList(patch.Keywords)));
        if (patch.AttachParentSlots is { } appr && !appr.IsEmpty) changes.Add(new MutationChange("AttachParentSlots", null, FormatKeywordList(appr)));
    }

    internal static bool TryFactionPatch(ParsedCommand command, out NpcFactionPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryFactionEntries(command, "factions", out var replace, out errorMessage) ||
            !TryFactionEntries(command, "add-faction", out var add, out errorMessage) ||
            !TryFactionEntries(command, "update-faction", out var update, out errorMessage) ||
            !TryFactionReferences(command, "remove-faction", out var remove, out errorMessage)) return false;
        var adds = add ?? ImmutableArray<NpcFactionEntry>.Empty;
        var updates = update ?? ImmutableArray<NpcFactionEntry>.Empty;
        var removals = remove ?? ImmutableArray<FormReference>.Empty;
        if (replace is null && adds.IsDefaultOrEmpty && updates.IsDefaultOrEmpty && removals.IsDefaultOrEmpty)
        { errorMessage = string.Empty; return true; }
        patch = new NpcFactionPatch(replace, adds, updates, removals);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryFactionEntries(ParsedCommand command, string option, out ImmutableArray<NpcFactionEntry>? entries,
        out string errorMessage)
    {
        entries = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<NpcFactionEntry>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.LastIndexOf('=');
            if (separator <= 0 || separator == item.Length - 1 || !FormReference.TryParse(item[..separator], out var faction) ||
                !sbyte.TryParse(item[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
            { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX=rank entries with signed-byte ranks."; return false; }
            var entry = new NpcFactionEntry(faction, rank);
            if (builder.Any(existing => existing.Faction == faction)) { errorMessage = $"--{option} contains duplicate faction {faction}."; return false; }
            builder.Add(entry);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one faction=rank entry."; return false; }
        entries = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static bool TryFactionReferences(ParsedCommand command, string option, out ImmutableArray<FormReference>? references,
        out string errorMessage)
    {
        references = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FormReference.TryParse(value, out var reference)) { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX references separated by commas."; return false; }
            if (builder.Contains(reference)) { errorMessage = $"--{option} contains duplicate faction {value}."; return false; }
            builder.Add(reference);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one faction reference."; return false; }
        references = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddFactionExpectations(ImmutableArray<MutationChange>.Builder changes, NpcFactionPatch? patch)
    {
        if (patch is null) return;
        if (patch.Replace is { } replace)
        {
            changes.Add(new MutationChange("Factions", null, FormatFactionEntries(replace)));
            return;
        }
        if (patch.Add.IsDefaultOrEmpty && patch.Update.IsDefaultOrEmpty && patch.Remove.IsDefaultOrEmpty) return;
        changes.Add(new MutationChange("Factions", null,
            $"add:{FormatFactionEntries(patch.Add)};update:{FormatFactionEntries(patch.Update)};remove:{string.Join(",", patch.Remove)}"));
    }

    internal static bool TryInventoryPatch(ParsedCommand command, out NpcInventoryPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryInventoryEntries(command, "inventory", out var replace, out errorMessage) ||
            !TryInventoryEntries(command, "add-inventory", out var add, out errorMessage) ||
            !TryInventoryEntries(command, "update-inventory", out var update, out errorMessage) ||
            !TryInventoryReferences(command, "remove-inventory", out var remove, out errorMessage)) return false;
        var adds = add ?? ImmutableArray<NpcInventoryEntry>.Empty;
        var updates = update ?? ImmutableArray<NpcInventoryEntry>.Empty;
        var removals = remove ?? ImmutableArray<FormReference>.Empty;
        if (replace is null && adds.IsDefaultOrEmpty && updates.IsDefaultOrEmpty && removals.IsDefaultOrEmpty)
        { errorMessage = string.Empty; return true; }
        patch = new NpcInventoryPatch(replace, adds, updates, removals);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryInventoryEntries(ParsedCommand command, string option, out ImmutableArray<NpcInventoryEntry>? entries,
        out string errorMessage)
    {
        entries = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<NpcInventoryEntry>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.LastIndexOf('=');
            if (separator <= 0 || separator == item.Length - 1 || !FormReference.TryParse(item[..separator], out var form) ||
                !int.TryParse(item[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX=count entries with signed 32-bit counts."; return false; }
            builder.Add(new NpcInventoryEntry(form, count));
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one item=count entry."; return false; }
        entries = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static bool TryInventoryReferences(ParsedCommand command, string option, out ImmutableArray<FormReference>? references,
        out string errorMessage)
    {
        references = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FormReference.TryParse(value, out var reference)) { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX references separated by commas."; return false; }
            if (builder.Contains(reference)) { errorMessage = $"--{option} contains duplicate item {value}."; return false; }
            builder.Add(reference);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one item reference."; return false; }
        references = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddInventoryExpectations(ImmutableArray<MutationChange>.Builder changes, NpcInventoryPatch? patch)
    {
        if (patch is null) return;
        if (patch.Replace is { } replace)
        {
            changes.Add(new MutationChange("Inventory", null, FormatInventoryEntries(replace)));
            return;
        }
        if (patch.Add.IsDefaultOrEmpty && patch.Update.IsDefaultOrEmpty && patch.Remove.IsDefaultOrEmpty) return;
        changes.Add(new MutationChange("Inventory", null,
            $"add:{FormatInventoryEntries(patch.Add)};update:{FormatInventoryEntries(patch.Update)};remove:{string.Join(",", patch.Remove)}"));
    }

    private static string FormatInventoryEntries(IEnumerable<NpcInventoryEntry> entries) =>
        string.Join(",", entries.Select(entry => $"{entry.Item}={entry.Count.ToString(CultureInfo.InvariantCulture)}"));

    internal static bool TryOutfitPatch(ParsedCommand command, out NpcOutfitPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryReferenceOption(command, "default-outfit", out var defaultOutfit, out errorMessage) ||
            !TryReferenceOption(command, "sleep-outfit", out var sleepingOutfit, out errorMessage)) return false;
        if (!defaultOutfit.IsSpecified && !sleepingOutfit.IsSpecified)
        {
            errorMessage = string.Empty;
            return true;
        }
        patch = new NpcOutfitPatch(defaultOutfit, sleepingOutfit);
        errorMessage = string.Empty;
        return true;
    }

    private static void AddOutfitExpectations(ImmutableArray<MutationChange>.Builder changes, NpcOutfitPatch? patch)
    {
        if (patch is null) return;
        AddReferenceExpectation(changes, "DefaultOutfit", patch.DefaultOutfit);
        AddReferenceExpectation(changes, "SleepingOutfit", patch.SleepingOutfit);
    }

    internal static bool TryPerkPatch(ParsedCommand command, out NpcPerkPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryPerkEntries(command, "perks", out var replace, out errorMessage) ||
            !TryPerkEntries(command, "add-perk", out var add, out errorMessage) ||
            !TryPerkEntries(command, "update-perk", out var update, out errorMessage) ||
            !TryPerkReferences(command, "remove-perk", out var remove, out errorMessage)) return false;
        var adds = add ?? ImmutableArray<NpcPerkEntry>.Empty;
        var updates = update ?? ImmutableArray<NpcPerkEntry>.Empty;
        var removals = remove ?? ImmutableArray<FormReference>.Empty;
        if (replace is null && adds.IsDefaultOrEmpty && updates.IsDefaultOrEmpty && removals.IsDefaultOrEmpty)
        {
            errorMessage = string.Empty;
            return true;
        }
        patch = new NpcPerkPatch(replace, adds, updates, removals);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryPerkEntries(ParsedCommand command, string option, out ImmutableArray<NpcPerkEntry>? entries,
        out string errorMessage)
    {
        entries = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<NpcPerkEntry>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.LastIndexOf('=');
            if (separator <= 0 || separator == item.Length - 1 || !FormReference.TryParse(item[..separator], out var perk) ||
                !byte.TryParse(item[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
            { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX=rank entries with unsigned-byte ranks."; return false; }
            builder.Add(new NpcPerkEntry(perk, rank));
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one perk=rank entry."; return false; }
        entries = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static bool TryPerkReferences(ParsedCommand command, string option, out ImmutableArray<FormReference>? references,
        out string errorMessage)
    {
        references = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FormReference.TryParse(value, out var reference)) { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX references separated by commas."; return false; }
            builder.Add(reference);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one perk reference."; return false; }
        references = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddPerkExpectations(ImmutableArray<MutationChange>.Builder changes, NpcPerkPatch? patch)
    {
        if (patch is null) return;
        if (patch.Replace is { } replace)
        {
            changes.Add(new MutationChange("Perks", null, FormatPerkEntries(replace)));
            return;
        }
        if (patch.Add.IsDefaultOrEmpty && patch.Update.IsDefaultOrEmpty && patch.Remove.IsDefaultOrEmpty) return;
        changes.Add(new MutationChange("Perks", null,
            $"add:{FormatPerkEntries(patch.Add)};update:{FormatPerkEntries(patch.Update)};remove:{string.Join(",", patch.Remove)}"));
    }

    private static string FormatPerkEntries(IEnumerable<NpcPerkEntry> entries) =>
        string.Join(",", entries.Select(entry => $"{entry.Perk}={entry.Rank.ToString(CultureInfo.InvariantCulture)}"));

    internal static bool TryActorEffectPatch(ParsedCommand command, out NpcActorEffectPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryActorEffectReferences(command, "actor-effects", out var replace, out errorMessage) ||
            !TryActorEffectReferences(command, "add-actor-effect", out var add, out errorMessage) ||
            !TryActorEffectReferences(command, "remove-actor-effect", out var remove, out errorMessage)) return false;
        var adds = add ?? ImmutableArray<FormReference>.Empty;
        var removals = remove ?? ImmutableArray<FormReference>.Empty;
        if (replace is null && adds.IsDefaultOrEmpty && removals.IsDefaultOrEmpty)
        {
            errorMessage = string.Empty;
            return true;
        }
        patch = new NpcActorEffectPatch(replace, adds, removals);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryActorEffectReferences(ParsedCommand command, string option, out ImmutableArray<FormReference>? references,
        out string errorMessage)
    {
        references = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FormReference.TryParse(value, out var reference))
            { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX references separated by commas."; return false; }
            if (builder.Contains(reference))
            { errorMessage = $"--{option} contains duplicate reference {value}."; return false; }
            builder.Add(reference);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one FormID reference."; return false; }
        references = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddActorEffectExpectations(ImmutableArray<MutationChange>.Builder changes, NpcActorEffectPatch? patch)
    {
        if (patch is null) return;
        if (patch.Replace is { } replace)
        {
            changes.Add(new MutationChange("ActorEffects", null, string.Join(",", replace)));
            return;
        }
        if (patch.Add.IsDefaultOrEmpty && patch.Remove.IsDefaultOrEmpty) return;
        changes.Add(new MutationChange("ActorEffects", null,
            $"add:{string.Join(",", patch.Add)};remove:{string.Join(",", patch.Remove)}"));
    }

    private static bool TryPropertyPatch(ParsedCommand command, out NpcPropertyPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!TryPropertyEntries(command, "properties", out var replace, out errorMessage) ||
            !TryPropertyEntries(command, "add-property", out var add, out errorMessage) ||
            !TryPropertyEntries(command, "update-property", out var update, out errorMessage) ||
            !TryPropertyReferences(command, "remove-property", out var remove, out errorMessage)) return false;
        var adds = add ?? ImmutableArray<NpcPropertyEntry>.Empty;
        var updates = update ?? ImmutableArray<NpcPropertyEntry>.Empty;
        var removals = remove ?? ImmutableArray<FormReference>.Empty;
        if (replace is null && adds.IsDefaultOrEmpty && updates.IsDefaultOrEmpty && removals.IsDefaultOrEmpty)
        {
            errorMessage = string.Empty;
            return true;
        }
        patch = new NpcPropertyPatch(replace, adds, updates, removals);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryPropertyEntries(ParsedCommand command, string option, out ImmutableArray<NpcPropertyEntry>? entries,
        out string errorMessage)
    {
        entries = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<NpcPropertyEntry>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.LastIndexOf('=');
            if (separator <= 0 || separator == item.Length - 1 || !FormReference.TryParse(item[..separator], out var actorValue) ||
                !float.TryParse(item[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX=float entries separated by commas."; return false; }
            if (builder.Any(entry => entry.ActorValue == actorValue))
            { errorMessage = $"--{option} contains duplicate actor-value reference {actorValue}."; return false; }
            builder.Add(new NpcPropertyEntry(actorValue, value));
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one actor-value=float entry."; return false; }
        entries = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static bool TryPropertyReferences(ParsedCommand command, string option, out ImmutableArray<FormReference>? references,
        out string errorMessage)
    {
        references = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FormReference.TryParse(value, out var reference))
            { errorMessage = $"--{option} uses Plugin.esp|0xXXXXXXXX references separated by commas."; return false; }
            if (builder.Contains(reference))
            { errorMessage = $"--{option} contains duplicate reference {value}."; return false; }
            builder.Add(reference);
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one FormID reference."; return false; }
        references = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddPropertyExpectations(ImmutableArray<MutationChange>.Builder changes, NpcPropertyPatch? patch)
    {
        if (patch is null) return;
        if (patch.Replace is { } replace)
        {
            changes.Add(new MutationChange("Properties", null, FormatPropertyEntries(replace)));
            return;
        }
        if (patch.Add.IsDefaultOrEmpty && patch.Update.IsDefaultOrEmpty && patch.Remove.IsDefaultOrEmpty) return;
        changes.Add(new MutationChange("Properties", null,
            $"add:{FormatPropertyEntries(patch.Add)};update:{FormatPropertyEntries(patch.Update)};remove:{string.Join(",", patch.Remove)}"));
    }

    private static string FormatPropertyEntries(IEnumerable<NpcPropertyEntry> entries) =>
        string.Join(",", entries.Select(entry => $"{entry.ActorValue}={entry.Value.ToString("R", CultureInfo.InvariantCulture)}"));

    private static string FormatFactionEntries(IEnumerable<NpcFactionEntry> entries) =>
        string.Join(",", entries.Select(entry => $"{entry.Faction}={entry.Rank.ToString(CultureInfo.InvariantCulture)}"));

    private static string FormatKeywordList(NpcKeywordListPatch patch)
    {
        if (patch.Replace is { } replace) return string.Join(",", replace.Select(reference => reference.ToString()));
        var adds = string.Join(",", patch.Add.Select(reference => reference.ToString()));
        var removes = string.Join(",", patch.Remove.Select(reference => reference.ToString()));
        return $"add:{adds};remove:{removes}";
    }

}
