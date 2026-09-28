using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimLeveledEntryEditorMode
{
    Add,
    Edit
}

public sealed record SkyrimLeveledEntryCandidate(
    FormReference Reference,
    SkyrimOutfitEditorItemKind Kind,
    string DisplayName);

public sealed record SkyrimLeveledEntryEditorResult(
    bool Accepted,
    LeveledListEntryProposal? Entry,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimLeveledListCycleEvidence(
    bool IsComplete,
    ImmutableDictionary<FormReference, ImmutableArray<FormReference>> Edges);

public sealed record SkyrimLeveledListDocumentEditResult(
    bool Accepted,
    SkyrimLeveledListEditorDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Validates the values returned by the small Skyrim LVLO add/edit dialog.
/// Document transitions and graph traversal live in focused collaborators.
/// </summary>
public static class SkyrimLeveledEntryEditorRules
{
    public const int MinimumLevel = 1;
    public const int MaximumLevel = short.MaxValue;
    public const int MinimumCount = 1;
    public const int MaximumCount = short.MaxValue;
    public const int MaximumEntries =
        SkyrimLeveledListEntryDocumentRules.MaximumEntries;

    public static SkyrimLeveledEntryEditorResult Apply(
        GameEdition edition,
        SkyrimLeveledEntryEditorMode mode,
        SkyrimLeveledEntryCandidate? candidate,
        int level,
        int count,
        int chanceNone)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("leveled-entry-edition-invalid",
                "The Skyrim leveled-entry editor accepts only Skyrim Special Edition."));
        if (!Enum.IsDefined(mode))
            diagnostics.Add(Error("leveled-entry-mode-invalid",
                "The leveled-entry editor mode must be Add or Edit."));
        if (candidate is null)
            diagnostics.Add(Error("leveled-entry-candidate-missing",
                "Choose one qualified ARMO or LVLI reference."));
        else
        {
            ValidateReference(candidate.Reference, diagnostics);
            if (!Enum.IsDefined(candidate.Kind))
                diagnostics.Add(Error("leveled-entry-kind-invalid",
                    "The leveled-entry reference must be typed as ARMO or LVLI."));
            if (string.IsNullOrWhiteSpace(candidate.DisplayName) ||
                candidate.DisplayName.Length > 512 ||
                candidate.DisplayName.Any(char.IsControl))
                diagnostics.Add(Error("leveled-entry-display-name-invalid",
                    "The leveled-entry label must contain 1 through 512 visible characters."));
        }
        if (level is < MinimumLevel or > MaximumLevel)
            diagnostics.Add(Error("leveled-entry-level-range",
                $"Level must be from {MinimumLevel} through {MaximumLevel}."));
        if (count is < MinimumCount or > MaximumCount)
            diagnostics.Add(Error("leveled-entry-count-range",
                $"Count must be from {MinimumCount} through {MaximumCount}."));
        if (chanceNone != 0)
            diagnostics.Add(Error("leveled-entry-chance-unsupported",
                "Skyrim has no supported per-entry Chance None field; use zero."));
        if (HasErrors(diagnostics) || candidate is null)
            return new(false, null, diagnostics.ToImmutable());

        return new(true,
            new LeveledListEntryProposal(
                candidate.Reference, (ushort)level, (ushort)count, 0),
            diagnostics.ToImmutable());
    }

    public static SkyrimLeveledEntryEditorResult Cancel() =>
        new(false, null, []);

    public static ImmutableArray<Diagnostic> ValidateEntry(
        LeveledListEntryProposal? entry)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (entry is null)
        {
            diagnostics.Add(Error("leveled-entry-missing",
                "A leveled-list entry is missing."));
            return diagnostics.ToImmutable();
        }
        ValidateReference(entry.Item, diagnostics);
        if (entry.Level is < MinimumLevel or > MaximumLevel)
            diagnostics.Add(Error("leveled-entry-level-range",
                $"Level must be from {MinimumLevel} through {MaximumLevel}."));
        if (entry.Count is < MinimumCount or > MaximumCount)
            diagnostics.Add(Error("leveled-entry-count-range",
                $"Count must be from {MinimumCount} through {MaximumCount}."));
        if (entry.ChanceNone != 0)
            diagnostics.Add(Error("leveled-entry-chance-unsupported",
                "Skyrim has no supported per-entry Chance None field; use zero."));
        return diagnostics.ToImmutable();
    }

    public static SkyrimLeveledListDocumentEditResult Add(
        SkyrimLeveledListEditorDocument? document,
        FormReference targetList,
        SkyrimOutfitEditorItemKind candidateKind,
        LeveledListEntryProposal? entry,
        SkyrimLeveledListCycleEvidence? cycleEvidence) =>
        SkyrimLeveledListEntryDocumentRules.Add(
            document, targetList, candidateKind, entry, cycleEvidence);

    public static SkyrimLeveledListDocumentEditResult Replace(
        SkyrimLeveledListEditorDocument? document,
        int index,
        FormReference targetList,
        SkyrimOutfitEditorItemKind candidateKind,
        LeveledListEntryProposal? entry,
        SkyrimLeveledListCycleEvidence? cycleEvidence) =>
        SkyrimLeveledListEntryDocumentRules.Replace(
            document, index, targetList, candidateKind, entry, cycleEvidence);

    public static SkyrimLeveledListDocumentEditResult Remove(
        SkyrimLeveledListEditorDocument? document,
        int index) =>
        SkyrimLeveledListEntryDocumentRules.Remove(document, index);

    internal static void ValidateReference(
        FormReference reference,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role = "entry")
    {
        if (reference.FormId.Value is > 0 and <= 0x00FF_FFFF &&
            !string.IsNullOrWhiteSpace(reference.Plugin.Value)) return;
        diagnostics.Add(Error("leveled-entry-reference-invalid",
            $"The {role} reference must have a provider and a nonzero plugin-local 24-bit FormID."));
    }

    internal static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    internal static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
