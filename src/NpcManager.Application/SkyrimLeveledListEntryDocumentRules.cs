using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Immutable ordered add, replace, and remove operations for LVLO rows.</summary>
public static class SkyrimLeveledListEntryDocumentRules
{
    public const int MaximumEntries = byte.MaxValue;

    public static SkyrimLeveledListDocumentEditResult Add(
        SkyrimLeveledListEditorDocument? document,
        FormReference targetList,
        SkyrimOutfitEditorItemKind candidateKind,
        LeveledListEntryProposal? entry,
        SkyrimLeveledListCycleEvidence? cycleEvidence)
    {
        var diagnostics = ValidateTransition(
            document, targetList, candidateKind, entry, cycleEvidence);
        if (document is not null && !document.Entries.IsDefault &&
            document.Entries.Length >= MaximumEntries)
            diagnostics.Add(SkyrimLeveledEntryEditorRules.Error(
                "leveled-entry-list-capacity",
                $"A Skyrim authored leveled list may contain at most {MaximumEntries} entries."));
        if (SkyrimLeveledEntryEditorRules.HasErrors(diagnostics) ||
            document is null || entry is null)
            return Refused(diagnostics);
        return new(true,
            document with { Entries = document.Entries.Add(entry) },
            diagnostics.ToImmutable());
    }

    public static SkyrimLeveledListDocumentEditResult Replace(
        SkyrimLeveledListEditorDocument? document,
        int index,
        FormReference targetList,
        SkyrimOutfitEditorItemKind candidateKind,
        LeveledListEntryProposal? entry,
        SkyrimLeveledListCycleEvidence? cycleEvidence)
    {
        var diagnostics = ValidateTransition(
            document, targetList, candidateKind, entry, cycleEvidence);
        if (document is null || document.Entries.IsDefault ||
            index < 0 || index >= document.Entries.Length)
            diagnostics.Add(SkyrimLeveledEntryEditorRules.Error(
                "leveled-entry-index-invalid",
                "The selected leveled-list entry does not exist."));
        if (SkyrimLeveledEntryEditorRules.HasErrors(diagnostics) ||
            document is null || entry is null)
            return Refused(diagnostics);
        return new(true,
            document with { Entries = document.Entries.SetItem(index, entry) },
            diagnostics.ToImmutable());
    }

    public static SkyrimLeveledListDocumentEditResult Remove(
        SkyrimLeveledListEditorDocument? document,
        int index)
    {
        var diagnostics = SkyrimLeveledListEditorRules
            .ValidateDocument(document).ToBuilder();
        if (document is null || document.Entries.IsDefault ||
            index < 0 || index >= document.Entries.Length)
            diagnostics.Add(SkyrimLeveledEntryEditorRules.Error(
                "leveled-entry-index-invalid",
                "The selected leveled-list entry does not exist."));
        if (SkyrimLeveledEntryEditorRules.HasErrors(diagnostics) ||
            document is null)
            return Refused(diagnostics);
        return new(true,
            document with { Entries = document.Entries.RemoveAt(index) },
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<Diagnostic>.Builder ValidateTransition(
        SkyrimLeveledListEditorDocument? document,
        FormReference targetList,
        SkyrimOutfitEditorItemKind candidateKind,
        LeveledListEntryProposal? entry,
        SkyrimLeveledListCycleEvidence? cycleEvidence)
    {
        var diagnostics = SkyrimLeveledListEditorRules
            .ValidateDocument(document).ToBuilder();
        SkyrimLeveledEntryEditorRules.ValidateReference(
            targetList, diagnostics, "target leveled list");
        diagnostics.AddRange(
            SkyrimLeveledEntryEditorRules.ValidateEntry(entry));
        if (candidateKind == SkyrimOutfitEditorItemKind.LeveledList &&
            entry is not null)
            diagnostics.AddRange(SkyrimLeveledListCycleValidator.Validate(
                targetList, entry.Item, cycleEvidence));
        else if (candidateKind != SkyrimOutfitEditorItemKind.Armor)
            diagnostics.Add(SkyrimLeveledEntryEditorRules.Error(
                "leveled-entry-kind-invalid",
                "The leveled-entry reference must be typed as ARMO or LVLI."));
        return diagnostics;
    }

    private static SkyrimLeveledListDocumentEditResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
