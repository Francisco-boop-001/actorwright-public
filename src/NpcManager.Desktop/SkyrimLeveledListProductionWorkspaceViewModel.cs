using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production owner for the Gate 016 header and Gate 017 entry modals. The
/// dialogs only return immutable values; this type owns review, write, and two
/// independent readbacks against one reviewed copied plugin closure.
/// </summary>
public sealed class SkyrimLeveledListProductionWorkspaceViewModel :
    NotifyViewModel, IDisposable
{
    private const string EmptyResult = "Not available";
    private readonly ISkyrimLeveledListProductionLoadService loadService;
    private readonly ISkyrimLeveledListProductionTransactionService transactionService;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private ReviewedGameIntake? intake;
    private SkyrimLeveledListProductionState? loaded;
    private SkyrimLeveledListEditorDocument? authoredDocument;
    private SkyrimLeveledListProductionProposal? reviewedProposal;

    public SkyrimLeveledListProductionWorkspaceViewModel(
        ISkyrimLeveledListProductionLoadService loadService,
        ISkyrimLeveledListProductionTransactionService transactionService,
        WorkspacePath labRoot)
    {
        this.loadService = loadService;
        this.transactionService = transactionService;
        this.labRoot = labRoot;
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(
            ReviewAsync, () => HasAuthoredDocument && !IsBusy);
        executeCommand = new AsyncCommand(
            ExecuteAsync, () => IsReviewed && !HasCompleted && !IsBusy);
        cancelCommand = new DelegateCommand(
            _ => cancellation?.Cancel(), _ => IsBusy);
    }

    public ObservableCollection<SkyrimLeveledListCandidateViewModel> Candidates { get; } = [];
    public ObservableCollection<SkyrimLeveledListAuthoredEntryViewModel> AuthoredEntries { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public ICommand LoadCommand => loadCommand;
    public ICommand ReviewCommand => reviewCommand;
    public ICommand ExecuteCommand => executeCommand;
    public ICommand CancelCommand => cancelCommand;
    public bool IsLoaded => loaded is not null;
    public bool HasAuthoredDocument => authoredDocument is not null;
    public bool CanAddEntry => authoredDocument is not null &&
                               SelectedCandidate is not null &&
                               HasValidDestination && !IsBusy;
    public bool CanEditEntry => authoredDocument is not null &&
                                SelectedAuthoredEntry is not null &&
                                HasValidDestination && !IsBusy;
    public bool CanRemoveEntry => authoredDocument is not null &&
                                  SelectedAuthoredEntry is not null && !IsBusy;
    public bool CanOpenEditor => IsLoaded && SelectedCandidate is not null &&
                                 HasValidDestination && !IsBusy;
    public string CatalogSummary => loaded is null
        ? "No reviewed leveled-list entry catalog is loaded."
        : $"{Candidates.Count} typed candidates: " +
          $"{Candidates.Count(item => item.Item.Kind == SkyrimOutfitEditorItemKind.Armor)} ARMO and " +
          $"{Candidates.Count(item => item.Item.Kind == SkyrimOutfitEditorItemKind.LeveledList)} LVLI.";
    public string DocumentSummary => authoredDocument is null
        ? "No accepted header and LVLO row are staged."
        : $"{authoredDocument.EditorId.Value} / LVLF 0x{authoredDocument.PackedFlags:X2} / " +
          $"Chance None {authoredDocument.ChanceNone}% / {authoredDocument.Entries.Length} row(s).";

    private string previewSeed = "17";
    public string PreviewSeed
    {
        get => previewSeed;
        set { if (Set(ref previewSeed, value ?? string.Empty)) ClearLoaded(); }
    }

    private string targetFormId = "0x00000A10";
    public string TargetFormId
    {
        get => targetFormId;
        set
        {
            if (!Set(ref targetFormId, value ?? string.Empty)) return;
            ClearAuthoredDocument();
            Raise(nameof(CanOpenEditor));
        }
    }

    private string proposalPath = string.Empty;
    public string ProposalPath
    {
        get => proposalPath;
        set
        {
            if (!Set(ref proposalPath, value ?? string.Empty)) return;
            InvalidateReview();
        }
    }

    private string outputPlugin = string.Empty;
    public string OutputPlugin
    {
        get => outputPlugin;
        set
        {
            if (!Set(ref outputPlugin, value ?? string.Empty)) return;
            ClearAuthoredDocument();
            Raise(nameof(CanOpenEditor));
        }
    }

    private SkyrimLeveledListCandidateViewModel? selectedCandidate;
    public SkyrimLeveledListCandidateViewModel? SelectedCandidate
    {
        get => selectedCandidate;
        set
        {
            if (!Set(ref selectedCandidate, value)) return;
            Raise(nameof(CanOpenEditor));
            Raise(nameof(CanAddEntry));
        }
    }

    private SkyrimLeveledListAuthoredEntryViewModel? selectedAuthoredEntry;
    public SkyrimLeveledListAuthoredEntryViewModel? SelectedAuthoredEntry
    {
        get => selectedAuthoredEntry;
        set
        {
            if (!Set(ref selectedAuthoredEntry, value)) return;
            Raise(nameof(CanEditEntry));
            Raise(nameof(CanRemoveEntry));
        }
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            Raise(nameof(IsNotBusy));
            Raise(nameof(CanOpenEditor));
            Raise(nameof(CanAddEntry));
            Raise(nameof(CanEditEntry));
            Raise(nameof(CanRemoveEntry));
            RaiseCommandState();
        }
    }
    public bool IsNotBusy => !IsBusy;

    private bool isReviewed;
    public bool IsReviewed
    {
        get => isReviewed;
        private set { if (Set(ref isReviewed, value)) RaiseCommandState(); }
    }

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set { if (Set(ref hasCompleted, value)) RaiseCommandState(); }
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private string status = "Review a copied Skyrim workspace before loading LVLI candidates.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string verdict = "Not loaded";
    public string Verdict
    {
        get => verdict;
        private set => Set(ref verdict, value);
    }

    private string proposalSha256 = EmptyResult;
    public string ProposalSha256
    {
        get => proposalSha256;
        private set => Set(ref proposalSha256, value);
    }

    private string resultPlugin = EmptyResult;
    public string ResultPlugin
    {
        get => resultPlugin;
        private set => Set(ref resultPlugin, value);
    }

    private string resultSha256 = EmptyResult;
    public string ResultSha256
    {
        get => resultSha256;
        private set => Set(ref resultSha256, value);
    }

    private bool HasValidDestination =>
        TryWorkspacePath(OutputPlugin, out WorkspacePath output) &&
        output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
        FormId.TryParse(TargetFormId, out FormId target) && target.Value is > 0 and <= 0x00FF_FFFF;

    public void ApplyReviewedIntake(ReviewedGameIntake reviewedIntake)
    {
        ArgumentNullException.ThrowIfNull(reviewedIntake);
        intake = reviewedIntake;
        ClearLoaded();
        Status = "Reviewed workspace accepted. Load its typed ARMO and LVLI catalog.";
    }

    public void ClearReviewedIntake()
    {
        intake = null;
        ClearLoaded();
        Status = "Workspace authority is stale. Review the copied workspace again.";
    }

    public async Task LoadAsync()
    {
        Diagnostics.Clear();
        if (intake is null || !long.TryParse(PreviewSeed.Trim(), out long seed))
        {
            PresentInputError("Review a copied Skyrim workspace and enter a signed preview seed.");
            return;
        }
        CancellationTokenSource source = Begin(
            "Loading", "Rehashing reviewed inputs and reading typed ARMO/LVLI candidates.");
        try
        {
            SkyrimLeveledListProductionLoadResult result = await loadService.LoadAsync(
                new SkyrimLeveledListProductionLoadRequest(intake, seed), source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.State is null)
            {
                Verdict = "Load refused";
                Status = "No LVLI production state was retained.";
                return;
            }
            loaded = result.State;
            Candidates.Clear();
            foreach (SkyrimOutfitEditorItem item in loaded.EntryCandidates)
                Candidates.Add(new SkyrimLeveledListCandidateViewModel(item));
            selectedCandidate = Candidates.FirstOrDefault(item =>
                item.Item.Kind == SkyrimOutfitEditorItemKind.Armor) ?? Candidates.FirstOrDefault();
            Raise(nameof(SelectedCandidate));
            ClearAuthoredDocument();
            Verdict = "Catalog loaded";
            Status = "Choose a typed entry and open the header and row editors.";
            RaiseLoadedState();
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Catalog load cancelled; no production state was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Load failed";
            Status = "No LVLI production state was retained.";
        }
        finally { Release(source); }
    }

    public SkyrimLeveledListEditorViewModel CreateHeaderEditor()
    {
        if (!CanOpenEditor || loaded is null)
            throw new InvalidOperationException("Load a reviewed catalog and enter a valid fresh destination first.");
        return new SkyrimLeveledListEditorViewModel(loaded.ExistingEditorIds);
    }

    public SkyrimLeveledEntryEditorViewModel CreateEntryEditor()
    {
        if (!CanOpenEditor || SelectedCandidate is null)
            throw new InvalidOperationException("Choose one reviewed typed entry first.");
        SkyrimOutfitEditorItem item = SelectedCandidate.Item;
        return new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Add,
            new SkyrimLeveledEntryCandidate(item.Reference, item.Kind, item.DisplayName));
    }

    public SkyrimLeveledEntryEditorViewModel CreateAdditionalEntryEditor()
    {
        if (!CanAddEntry || SelectedCandidate is null)
            throw new InvalidOperationException("Choose a reviewed row candidate first.");
        SkyrimOutfitEditorItem item = SelectedCandidate.Item;
        return new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Add,
            new SkyrimLeveledEntryCandidate(
                item.Reference, item.Kind, item.DisplayName));
    }

    public SkyrimLeveledEntryEditorViewModel CreateSelectedEntryEditor()
    {
        if (!CanEditEntry || SelectedAuthoredEntry is null || loaded is null)
            throw new InvalidOperationException("Choose one authored row first.");
        LeveledListEntryProposal existing = SelectedAuthoredEntry.Entry;
        SkyrimOutfitEditorItem item = loaded.EntryCandidates.FirstOrDefault(candidate =>
            SameReference(candidate.Reference, existing.Item)) ??
            throw new InvalidOperationException(
                "The selected row no longer belongs to the reviewed typed catalog.");
        return new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Edit,
            new SkyrimLeveledEntryCandidate(
                item.Reference, item.Kind, item.DisplayName),
            existing);
    }

    public bool ApplyAddedEntry(SkyrimLeveledEntryEditorViewModel entryEditor)
    {
        ArgumentNullException.ThrowIfNull(entryEditor);
        if (authoredDocument is null || SelectedCandidate is null ||
            !entryEditor.IsAccepted || entryEditor.AcceptedEntry is null ||
            !TryDestinationReference(out FormReference target)) return false;
        SkyrimLeveledListDocumentEditResult result =
            SkyrimLeveledEntryEditorRules.Add(
                authoredDocument,
                target,
                SelectedCandidate.Item.Kind,
                entryEditor.AcceptedEntry,
                null);
        int selectedIndex = result.Document?.Entries.Length - 1 ?? -1;
        return CommitDocumentEdit(
            result,
            selectedIndex,
            "Row added",
            "The new ordered LVLO row is staged. Review authority was invalidated.");
    }

    public bool ApplyEditedEntry(SkyrimLeveledEntryEditorViewModel entryEditor)
    {
        ArgumentNullException.ThrowIfNull(entryEditor);
        if (authoredDocument is null || SelectedAuthoredEntry is null || loaded is null ||
            !entryEditor.IsAccepted || entryEditor.AcceptedEntry is null ||
            !TryDestinationReference(out FormReference target)) return false;
        int selectedIndex = SelectedAuthoredEntry.Index;
        SkyrimOutfitEditorItem item = loaded.EntryCandidates.FirstOrDefault(candidate =>
            SameReference(candidate.Reference, SelectedAuthoredEntry.Entry.Item)) ??
            throw new InvalidOperationException(
                "The selected row no longer belongs to the reviewed typed catalog.");
        SkyrimLeveledListDocumentEditResult result =
            SkyrimLeveledEntryEditorRules.Replace(
                authoredDocument,
                selectedIndex,
                target,
                item.Kind,
                entryEditor.AcceptedEntry,
                null);
        return CommitDocumentEdit(
            result,
            selectedIndex,
            "Row edited",
            "Only the selected ordered LVLO row was replaced. Review authority was invalidated.");
    }

    public bool RemoveSelectedEntry()
    {
        if (authoredDocument is null || SelectedAuthoredEntry is null) return false;
        int selectedIndex = SelectedAuthoredEntry.Index;
        SkyrimLeveledListDocumentEditResult result =
            SkyrimLeveledEntryEditorRules.Remove(authoredDocument, selectedIndex);
        int nextIndex = result.Document is null || result.Document.Entries.Length == 0
            ? -1
            : Math.Min(selectedIndex, result.Document.Entries.Length - 1);
        return CommitDocumentEdit(
            result,
            nextIndex,
            "Row removed",
            "Exactly one ordered LVLO row was removed. Review authority was invalidated.");
    }

    public bool ApplyEditors(
        SkyrimLeveledListEditorViewModel headerEditor,
        SkyrimLeveledEntryEditorViewModel entryEditor)
    {
        ArgumentNullException.ThrowIfNull(headerEditor);
        ArgumentNullException.ThrowIfNull(entryEditor);
        if (loaded is null || SelectedCandidate is null ||
            !headerEditor.IsAccepted || headerEditor.AcceptedDocument is null ||
            !entryEditor.IsAccepted || entryEditor.AcceptedEntry is null ||
            !TryDestinationReference(out FormReference target))
            return false;

        SkyrimLeveledListDocumentEditResult result = SkyrimLeveledEntryEditorRules.Add(
            headerEditor.AcceptedDocument,
            target,
            SelectedCandidate.Item.Kind,
            entryEditor.AcceptedEntry,
            null);
        AddDiagnostics(result.Diagnostics);
        if (!result.Accepted || result.Document is null)
        {
            Verdict = "Authoring refused";
            Status = "The header and entry were not retained.";
            return false;
        }
        return CommitDocumentEdit(
            result,
            0,
            "Authoring staged",
            "One complete immutable LVLI is staged. Review writes JSON only.");
    }

    public void ReportEditorCancelled() =>
        Status = "Header or row editor cancelled; the previously accepted document is unchanged.";

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildRequest(out SkyrimLeveledListProductionRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Reviewing", "Persisting one hash-bound LVLI proposal; the output ESP must remain absent.");
        try
        {
            SkyrimLeveledListProductionProposal proposal = await transactionService.AnalyzeAsync(
                request!, source.Token);
            AddDiagnostics(proposal.Diagnostics);
            if (!proposal.IsApplicable || proposal.ProposalSha256 is null ||
                !File.Exists(request!.OutputProposal.Value) || File.Exists(request.OutputPlugin.Value))
            {
                Verdict = "Review refused";
                Status = "No output plugin was written. Correct the diagnostics and use fresh paths.";
                return;
            }
            reviewedProposal = proposal;
            ProposalSha256 = proposal.ProposalSha256.Value.Value;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = "Proposal retained and output ESP absent. Apply is bound to this exact request.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Review cancelled; no output plugin was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "No output plugin was written.";
        }
        finally { Release(source); }
    }

    public async Task ExecuteAsync()
    {
        Diagnostics.Clear();
        if (reviewedProposal is null || !IsReviewed)
        {
            PresentInputError("Review the exact LVLI request before writing.");
            return;
        }
        if (!TryBuildRequest(out SkyrimLeveledListProductionRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Writing", "Writing one fresh LVLI ESP and reopening its exact record surface.");
        ProgressPercent = 20;
        try
        {
            SkyrimLeveledListProductionResult applied = await transactionService.ApplyAsync(
                request!, reviewedProposal, source.Token);
            AddDiagnostics(applied.Diagnostics);
            if (!applied.Applied || applied.Verification is not { IsValid: true })
            {
                Verdict = "Write refused";
                Status = "The new LVLI failed its immediate readback and is not authoritative.";
                return;
            }
            ProgressPercent = 75;
            SkyrimLeveledListProductionVerification verified = await transactionService.VerifyAsync(
                request!, reviewedProposal, source.Token);
            AddDiagnostics(verified.Diagnostics);
            if (!verified.IsValid || verified.LeveledListRecordCount != 1 ||
                verified.OtherRecordCount != 0 || !verified.SelfOwned ||
                !verified.HeaderMatches || !verified.EntriesMatch ||
                !verified.MasterSetMatches || verified.OutputSha256 is null ||
                !File.Exists(request!.OutputPlugin.Value))
            {
                Verdict = "Verification failed";
                Status = "The explicit second reopen did not prove the exact new LVLI.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = verified.OutputPlugin.Value;
            ResultSha256 = verified.OutputSha256.Value.Value;
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = "Fresh LVLI written and reopened twice. Equipment, runtime, and visual authority remain false.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Write cancelled; no partial output is authoritative.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Write failed";
            Status = "No unverified output is authoritative.";
        }
        finally { Release(source); }
    }

    public void Dispose()
    {
        CancellationTokenSource? source = cancellation;
        cancellation = null;
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    private bool CanLoad() => intake is not null && !IsBusy &&
                              long.TryParse(PreviewSeed.Trim(), out _);

    private bool TryBuildRequest(
        out SkyrimLeveledListProductionRequest? request,
        out string error)
    {
        request = null;
        if (loaded is null || authoredDocument is null)
        {
            error = "Load a reviewed catalog and accept the header and entry editors first.";
            return false;
        }
        if (!FormId.TryParse(TargetFormId, out FormId target) || target.Value is 0 or > 0x00FF_FFFF ||
            !TryWorkspacePath(ProposalPath, out WorkspacePath proposal) ||
            !TryWorkspacePath(OutputPlugin, out WorkspacePath output) ||
            !output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
        {
            error = "Enter a nonzero local 24-bit FormID, a K-only proposal path, and a fresh K-only .esp path.";
            return false;
        }
        request = new SkyrimLeveledListProductionRequest(
            loaded, target, authoredDocument, proposal, output);
        error = string.Empty;
        return true;
    }

    private bool TryDestinationReference(out FormReference reference)
    {
        reference = default;
        if (!FormId.TryParse(TargetFormId, out FormId target) ||
            !TryWorkspacePath(OutputPlugin, out WorkspacePath output)) return false;
        try
        {
            reference = new FormReference(new PluginName(Path.GetFileName(output.Value)), target);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private bool TryWorkspacePath(string value, out WorkspacePath path)
    {
        try
        {
            path = new WorkspacePath(value);
            return path.IsUnder(labRoot);
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    private void ClearLoaded()
    {
        loaded = null;
        Candidates.Clear();
        selectedCandidate = null;
        Raise(nameof(SelectedCandidate));
        ClearAuthoredDocument();
        Verdict = "Not loaded";
        RaiseLoadedState();
    }

    private void ClearAuthoredDocument()
    {
        authoredDocument = null;
        InvalidateReview();
        RaiseDocumentState();
    }

    private void InvalidateReview()
    {
        reviewedProposal = null;
        IsReviewed = false;
        HasCompleted = false;
        ProgressPercent = 0;
        ProposalSha256 = EmptyResult;
        ResultPlugin = EmptyResult;
        ResultSha256 = EmptyResult;
        RaiseCommandState();
    }

    private void RaiseLoadedState()
    {
        Raise(nameof(IsLoaded));
        Raise(nameof(CanOpenEditor));
        Raise(nameof(CanAddEntry));
        Raise(nameof(CanEditEntry));
        Raise(nameof(CanRemoveEntry));
        Raise(nameof(CatalogSummary));
        RaiseCommandState();
    }

    private bool CommitDocumentEdit(
        SkyrimLeveledListDocumentEditResult result,
        int selectedIndex,
        string verdict,
        string status)
    {
        AddDiagnostics(result.Diagnostics);
        if (!result.Accepted || result.Document is null)
        {
            Verdict = "Row edit refused";
            Status = "The accepted LVLI document is unchanged.";
            return false;
        }
        authoredDocument = result.Document;
        InvalidateReview();
        Verdict = verdict;
        Status = status;
        RaiseDocumentState(selectedIndex);
        return true;
    }

    private void RaiseDocumentState(int selectedIndex = -1)
    {
        SyncAuthoredEntries(selectedIndex);
        Raise(nameof(HasAuthoredDocument));
        Raise(nameof(DocumentSummary));
        Raise(nameof(CanAddEntry));
        Raise(nameof(CanEditEntry));
        Raise(nameof(CanRemoveEntry));
        RaiseCommandState();
    }

    private void SyncAuthoredEntries(int selectedIndex = -1)
    {
        int previousIndex = selectedIndex >= 0
            ? selectedIndex
            : SelectedAuthoredEntry?.Index ?? 0;
        AuthoredEntries.Clear();
        if (authoredDocument is not null)
        {
            for (int index = 0; index < authoredDocument.Entries.Length; index++)
            {
                LeveledListEntryProposal entry = authoredDocument.Entries[index];
                string displayName = loaded?.EntryCandidates.FirstOrDefault(item =>
                    SameReference(item.Reference, entry.Item))?.DisplayName ??
                    "Reviewed leveled-list item";
                AuthoredEntries.Add(new SkyrimLeveledListAuthoredEntryViewModel(
                    index, entry, displayName));
            }
        }
        SelectedAuthoredEntry = AuthoredEntries.Count == 0
            ? null
            : AuthoredEntries[Math.Clamp(previousIndex, 0, AuthoredEntries.Count - 1)];
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value,
            right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private CancellationTokenSource Begin(string verdictValue, string statusValue)
    {
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = verdictValue;
        Status = statusValue;
        return source;
    }

    private void Release(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellation, source)) cancellation = null;
        source.Dispose();
        IsBusy = false;
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
            Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
    }

    private void PresentInputError(string message)
    {
        Diagnostics.Add(message);
        Verdict = "Input required";
        Status = "Nothing was written.";
    }

    private void RaiseCommandState()
    {
        loadCommand.RaiseCanExecuteChanged();
        reviewCommand.RaiseCanExecuteChanged();
        executeCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
    }

    public sealed class SkyrimLeveledListCandidateViewModel(
        SkyrimOutfitEditorItem item)
    {
        public SkyrimOutfitEditorItem Item { get; } = item;
        public string Label { get; } =
            $"{item.Signature.Value}  {item.DisplayName}  [{item.Reference}]";
    }

    public sealed class SkyrimLeveledListAuthoredEntryViewModel(
        int index,
        LeveledListEntryProposal entry,
        string displayName)
    {
        public int Index { get; } = index;
        public LeveledListEntryProposal Entry { get; } = entry;
        public string DisplayName { get; } = displayName;
        public string Label { get; } =
            $"{index + 1}. {displayName} [{entry.Item}] - Level {entry.Level}, Count {entry.Count}";
    }

    private sealed class DelegateCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
