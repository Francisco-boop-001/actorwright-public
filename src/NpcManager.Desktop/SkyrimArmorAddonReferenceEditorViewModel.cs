using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public delegate SkyrimArmorAddonDeepEditResult? SkyrimArmorAddonDeepEditor(
    SkyrimArmorAddonReferenceRow current);

public sealed class SkyrimArmorAddonCandidateItem
{
    public SkyrimArmorAddonCandidateItem(
        SkyrimArmorAddonReferenceCandidate candidate)
    {
        Candidate = candidate;
        DisplayName = candidate.DisplayName;
        ReferenceText = candidate.Reference.ToString();
        CompatibilityText = SameReference(
            candidate.Compatibility.OwningArmorRace,
            candidate.Compatibility.PrimaryRace)
            ? "Compatible through primary race"
            : "Compatible through additional race";
        AutomationName =
            $"{DisplayName}; {ReferenceText}; {CompatibilityText}";
    }

    public SkyrimArmorAddonReferenceCandidate Candidate { get; }
    public string DisplayName { get; }
    public string ReferenceText { get; }
    public string CompatibilityText { get; }
    public string AutomationName { get; }

    private static bool SameReference(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);
}

public sealed class SkyrimArmorAddonReferenceEditorViewModel : NotifyViewModel
{
    private readonly FormReference owningArmorRace;
    private readonly SkyrimArmorAddonReferenceRow? openingRow;
    private readonly ImmutableArray<SkyrimArmorAddonReferenceCandidate> catalog;
    private readonly SkyrimArmorAddonDeepEditor deepEditor;
    private readonly string authorityNotice =
        "This returns one in-memory Skyrim ARMA reference. It does not write, equip, render, or prove a plugin or game result.";

    public SkyrimArmorAddonReferenceEditorViewModel(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow? openingRow,
        ImmutableArray<SkyrimArmorAddonReferenceCandidate> catalog,
        SkyrimArmorAddonDeepEditor deepEditor)
    {
        this.owningArmorRace = owningArmorRace;
        this.openingRow = openingRow;
        this.catalog = catalog.IsDefault ? [] : catalog;
        this.deepEditor = deepEditor ?? throw new ArgumentNullException(nameof(deepEditor));
        workingRow = openingRow;
        RefreshCatalog();
        RefreshWorkingRow();
    }

    public ObservableCollection<SkyrimArmorAddonCandidateItem> Candidates { get; } = [];
    public SkyrimArmorAddonReferenceRow? AcceptedRow { get; private set; }
    public bool IsAccepted { get; private set; }
    public bool WasAutoAccepted { get; private set; }
    public string OwningRaceText => owningArmorRace.ToString();
    public string AuthorityNotice => authorityNotice;

    private string searchText = string.Empty;
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!Set(ref searchText, value ?? string.Empty)) return;
            RefreshCatalog();
        }
    }

    private SkyrimArmorAddonCandidateItem? selectedCandidate;
    public SkyrimArmorAddonCandidateItem? SelectedCandidate
    {
        get => selectedCandidate;
        set
        {
            if (!Set(ref selectedCandidate, value)) return;
            Raise(nameof(CanChoose));
        }
    }

    private SkyrimArmorAddonReferenceRow? workingRow;
    public string CurrentName => workingRow?.DisplayName ?? "No ARMA selected";
    public string CurrentReference => workingRow?.Reference.ToString() ?? "(none)";
    public string CompatibilitySummary => workingRow?.Compatibility is not { } evidence
        ? "Choose a reviewed ARMA compatible with the owning armor race."
        : SameReference(evidence.OwningArmorRace,
            evidence.PrimaryRace)
            ? $"Compatible with {OwningRaceText} through its primary race."
            : $"Compatible with {OwningRaceText} through an additional race.";
    public bool CanChoose => SelectedCandidate is not null;
    public bool CanEdit => workingRow is not null && CanUse;

    private bool canUse;
    public bool CanUse
    {
        get => canUse;
        private set => Set(ref canUse, value);
    }

    private string catalogSummary = string.Empty;
    public string CatalogSummary
    {
        get => catalogSummary;
        private set => Set(ref catalogSummary, value);
    }

    private string emptyMessage = string.Empty;
    public string EmptyMessage
    {
        get => emptyMessage;
        private set => Set(ref emptyMessage, value);
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

    public bool TryChoose()
    {
        SkyrimArmorAddonReferenceTransition transition =
            SkyrimArmorAddonReferenceEditorRules.Choose(
                GameEdition.SkyrimSpecialEdition,
                owningArmorRace,
                SelectedCandidate?.Candidate);
        if (!transition.Accepted || transition.Row is null)
            return Refuse(FirstError(transition.Diagnostics));
        workingRow = transition.Row;
        ClearAcceptance();
        RefreshWorkingRow();
        StatusMessage = "The compatible ARMA is selected locally. Use ARMA to return it.";
        return true;
    }

    public bool TryUse()
    {
        SkyrimArmorAddonReferenceTransition transition =
            SkyrimArmorAddonReferenceEditorRules.Accept(
                owningArmorRace, workingRow);
        if (!transition.Accepted || transition.Row is null)
            return Refuse(FirstError(transition.Diagnostics));
        Accept(transition.Row, autoAccepted: false);
        StatusMessage = "The ARMA row is ready for the outer Armor transaction.";
        return true;
    }

    public bool TryDeepEdit()
    {
        if (workingRow is null)
            return Refuse("Choose one reviewed race-compatible ARMA before deep editing it.");
        SkyrimArmorAddonDeepEditResult? child;
        try { child = deepEditor(workingRow); }
        catch (Exception exception)
        {
            return Refuse($"The ARMA deep editor failed: {exception.Message}");
        }
        SkyrimArmorAddonReferenceTransition transition =
            SkyrimArmorAddonReferenceEditorRules.ApplyDeepEdit(
                owningArmorRace, workingRow, child);
        if (!transition.Accepted || transition.Row is null)
        {
            ValidationMessage = child?.Committed == true
                ? FirstError(transition.Diagnostics)
                : string.Empty;
            StatusMessage = child?.Committed == true
                ? string.Empty
                : child is null
                    ? FirstError(transition.Diagnostics)
                    : "Deep edit was cancelled. The current ARMA selection is unchanged.";
            return false;
        }
        workingRow = transition.Row;
        Accept(transition.Row, autoAccepted: transition.AutoAccept);
        StatusMessage = "The committed ARMA edit is ready and was accepted automatically.";
        return true;
    }

    public void Cancel()
    {
        SkyrimArmorAddonReferenceEditorRules.Cancel();
        workingRow = openingRow;
        ClearAcceptance();
        RefreshWorkingRow();
        StatusMessage = string.Empty;
    }

    private void RefreshCatalog()
    {
        SkyrimArmorAddonReferenceCandidate? selected = SelectedCandidate?.Candidate;
        Candidates.Clear();
        int deleted = 0;
        int stale = 0;
        int incompatibleOrUnverified = 0;
        foreach (SkyrimArmorAddonReferenceCandidate candidate in catalog)
        {
            SkyrimArmorAddonReferenceTransition validation =
                SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningArmorRace,
                    candidate);
            if (!validation.Accepted)
            {
                if (candidate.IsDeleted) deleted++;
                else if (candidate.IsStale) stale++;
                else incompatibleOrUnverified++;
                continue;
            }
            if (!MatchesSearch(candidate, SearchText)) continue;
            Candidates.Add(new SkyrimArmorAddonCandidateItem(candidate));
        }
        SelectedCandidate = Candidates.FirstOrDefault(item =>
            selected is not null && item.Candidate == selected);
        CatalogSummary =
            $"{Candidates.Count} compatible ARMA candidate(s) shown. Excluded: {deleted} deleted, {stale} stale, {incompatibleOrUnverified} incompatible or unverified.";
        EmptyMessage = Candidates.Count == 0
            ? "No reviewed race-compatible ARMA matches this search."
            : string.Empty;
    }

    private void RefreshWorkingRow()
    {
        ImmutableArray<Diagnostic> diagnostics = workingRow is null
            ? []
            : SkyrimArmorAddonReferenceEditorRules.ValidateRow(
                owningArmorRace, workingRow);
        CanUse = workingRow is not null &&
            !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        ValidationMessage = workingRow is null || CanUse
            ? string.Empty
            : FirstError(diagnostics);
        Raise(nameof(CurrentName));
        Raise(nameof(CurrentReference));
        Raise(nameof(CompatibilitySummary));
        Raise(nameof(CanEdit));
    }

    private void Accept(
        SkyrimArmorAddonReferenceRow row,
        bool autoAccepted)
    {
        AcceptedRow = row;
        IsAccepted = true;
        WasAutoAccepted = autoAccepted;
        ValidationMessage = string.Empty;
        Raise(nameof(AcceptedRow));
        Raise(nameof(IsAccepted));
        Raise(nameof(WasAutoAccepted));
    }

    private void ClearAcceptance()
    {
        AcceptedRow = null;
        IsAccepted = false;
        WasAutoAccepted = false;
        Raise(nameof(AcceptedRow));
        Raise(nameof(IsAccepted));
        Raise(nameof(WasAutoAccepted));
    }

    private bool Refuse(string message)
    {
        ClearAcceptance();
        ValidationMessage = message;
        StatusMessage = string.Empty;
        return false;
    }

    private static bool MatchesSearch(
        SkyrimArmorAddonReferenceCandidate candidate,
        string query) =>
        string.IsNullOrWhiteSpace(query) ||
        candidate.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        candidate.Reference.ToString().Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameReference(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static string FirstError(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ??
        "The Armor-addon reference was refused.";
}
