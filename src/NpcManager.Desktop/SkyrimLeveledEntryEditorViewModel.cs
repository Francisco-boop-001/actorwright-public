using System.Collections.Immutable;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimLeveledEntryEditorViewModel : NotifyViewModel
{
    private readonly SkyrimLeveledEntryEditorMode mode;
    private readonly SkyrimLeveledEntryCandidate candidate;
    private readonly string? contextError;
    private readonly string authorityNotice =
        "This returns one in-memory Skyrim LVLO row only. It does not allocate, write, preview, or prove a plugin or game result.";
    private readonly string chanceNoneSummary =
        "0% - fixed for Skyrim entries. Use the parent list's Chance None for whole-list suppression.";

    public SkyrimLeveledEntryEditorViewModel(
        SkyrimLeveledEntryEditorMode mode,
        SkyrimLeveledEntryCandidate candidate,
        LeveledListEntryProposal? existingEntry = null)
    {
        this.mode = mode;
        this.candidate = candidate ??
            throw new ArgumentNullException(nameof(candidate));
        if (mode == SkyrimLeveledEntryEditorMode.Edit)
        {
            if (existingEntry is null)
                contextError = "Edit mode requires the selected existing LVLO row.";
            else if (!SameReference(existingEntry.Item, candidate.Reference))
                contextError =
                    "The selected entry reference does not match the immutable item context.";
            else if (existingEntry.ChanceNone != 0)
                contextError =
                    "The selected row carries unsupported per-entry Chance None data and cannot be edited as Skyrim without an explicit repair operation.";
            else
            {
                levelText = existingEntry.Level.ToString(CultureInfo.InvariantCulture);
                countText = existingEntry.Count.ToString(CultureInfo.InvariantCulture);
            }
        }
        else if (mode == SkyrimLeveledEntryEditorMode.Add)
        {
            if (existingEntry is not null)
                contextError = "Add mode may not replace an existing LVLO row.";
        }
        else
        {
            contextError = "The leveled-entry editor mode must be Add or Edit.";
        }
        RefreshValidation();
    }

    public LeveledListEntryProposal? AcceptedEntry { get; private set; }
    public bool IsAccepted { get; private set; }
    public string Title => mode == SkyrimLeveledEntryEditorMode.Edit
        ? "Edit leveled-list entry"
        : "Add leveled-list entry";
    public string ActionText => mode == SkyrimLeveledEntryEditorMode.Edit
        ? "_Apply changes"
        : "_Add entry";
    public string ItemTitle => candidate.DisplayName;
    public string ItemReference => candidate.Reference.ToString();
    public string ItemKind => candidate.Kind == SkyrimOutfitEditorItemKind.LeveledList
        ? "Nested leveled list (LVLI)"
        : "Armor (ARMO)";
    public string ChanceNoneSummary => chanceNoneSummary;
    public string AuthorityNotice => authorityNotice;

    private string levelText = "1";
    public string LevelText
    {
        get => levelText;
        set
        {
            if (!Set(ref levelText, value ?? string.Empty)) return;
            RefreshValidation();
        }
    }

    private string countText = "1";
    public string CountText
    {
        get => countText;
        set
        {
            if (!Set(ref countText, value ?? string.Empty)) return;
            RefreshValidation();
        }
    }

    private bool canAccept;
    public bool CanAccept
    {
        get => canAccept;
        private set => Set(ref canAccept, value);
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    private string statusMessage = string.Empty;
    public string StatusMessage
    {
        get => statusMessage;
        private set => Set(ref statusMessage, value);
    }

    public bool TryAccept()
    {
        SkyrimLeveledEntryEditorResult result = Evaluate();
        if (!result.Accepted || result.Entry is null)
        {
            ClearAcceptance();
            ValidationMessage = FirstError(result.Diagnostics);
            return false;
        }
        AcceptedEntry = result.Entry;
        IsAccepted = true;
        ValidationMessage = string.Empty;
        StatusMessage = mode == SkyrimLeveledEntryEditorMode.Edit
            ? "The replacement LVLO row is ready for the outer transaction."
            : "The new LVLO row is ready for the outer transaction.";
        Raise(nameof(AcceptedEntry));
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        SkyrimLeveledEntryEditorRules.Cancel();
        ClearAcceptance();
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    private SkyrimLeveledEntryEditorResult Evaluate()
    {
        if (contextError is not null)
            return new(false, null,
                [new Diagnostic("leveled-entry-context-invalid",
                    DiagnosticSeverity.Error, contextError)]);
        if (!TryParseInteger(LevelText, out int level))
            return InvalidNumber("leveled-entry-level-format",
                $"Level must be a whole number from {SkyrimLeveledEntryEditorRules.MinimumLevel} through {SkyrimLeveledEntryEditorRules.MaximumLevel}.");
        if (!TryParseInteger(CountText, out int count))
            return InvalidNumber("leveled-entry-count-format",
                $"Count must be a whole number from {SkyrimLeveledEntryEditorRules.MinimumCount} through {SkyrimLeveledEntryEditorRules.MaximumCount}.");
        return SkyrimLeveledEntryEditorRules.Apply(
            GameEdition.SkyrimSpecialEdition,
            mode,
            candidate,
            level,
            count,
            0);
    }

    private void RefreshValidation()
    {
        ClearAcceptance();
        SkyrimLeveledEntryEditorResult result = Evaluate();
        CanAccept = result.Accepted;
        ValidationMessage = result.Accepted
            ? string.Empty
            : FirstError(result.Diagnostics);
        StatusMessage = string.Empty;
    }

    private void ClearAcceptance()
    {
        AcceptedEntry = null;
        IsAccepted = false;
        Raise(nameof(AcceptedEntry));
        Raise(nameof(IsAccepted));
    }

    private static bool TryParseInteger(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out result);

    private static SkyrimLeveledEntryEditorResult InvalidNumber(
        string code,
        string message) =>
        new(false, null,
            [new Diagnostic(code, DiagnosticSeverity.Error, message)]);

    private static string FirstError(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ??
        "The leveled-list entry was refused.";

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);
}
