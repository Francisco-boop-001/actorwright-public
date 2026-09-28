using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production parent for SKY-GUI-014. The modal chooses categories only; this
/// coordinator owns reviewed intake, typed proposal creation, the atomic ESP
/// plus jslot write, and a separate reopen/verification pass.
/// </summary>
public sealed class SkyrimSelectiveAppearancePasteWorkspaceViewModel :
    NotifyViewModel,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyResult = "Not available";
    private readonly ISkyrimSelectiveAppearancePasteLoadService loadService;
    private readonly ISkyrimSelectiveAppearancePasteTransactionService transactionService;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();
    private ReviewedGameIntake? intake;
    private SkyrimSelectiveAppearancePasteLoadedState? loaded;
    private SkyrimSelectiveAppearancePasteSelection? selection;
    private SkyrimSelectiveAppearancePasteProposal? reviewedProposal;

    public SkyrimSelectiveAppearancePasteWorkspaceViewModel(
        ISkyrimSelectiveAppearancePasteLoadService loadService,
        ISkyrimSelectiveAppearancePasteTransactionService transactionService,
        WorkspacePath labRoot)
    {
        this.loadService = loadService;
        this.transactionService = transactionService;
        this.labRoot = labRoot;
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(
            ReviewAsync,
            () => IsLoaded && HasSelection && !IsBusy);
        executeCommand = new AsyncCommand(
            ExecuteAsync,
            () => IsReviewed && !HasCompleted && !IsBusy);
        cancelCommand = new DelegateCommand(
            _ => cancellation?.Cancel(),
            _ => IsBusy);
    }

    public ObservableCollection<string> Diagnostics { get; } = [];
    public ICommand LoadCommand => loadCommand;
    public ICommand ReviewCommand => reviewCommand;
    public ICommand ExecuteCommand => executeCommand;
    public ICommand CancelCommand => cancelCommand;

    public event EventHandler<SkyrimMainWorkspaceArtifactHandoff>?
        ArtifactCommitted
    {
        add => artifactEmitter.ArtifactCommitted += value;
        remove => artifactEmitter.ArtifactCommitted -= value;
    }

    public void BindWorkspaceIdentity(
        SkyrimMainWorkspaceIdentity identity) =>
        artifactEmitter.Bind(identity);
    public bool IsLoaded => loaded is not null;
    public bool HasSelection => selection is { Categories.IsDefaultOrEmpty: false };
    public bool CanChooseCategories => IsLoaded && !IsBusy;
    public string SelectionSummary => selection is null
        ? "No accepted category selection."
        : selection.Categories.IsDefaultOrEmpty
            ? "Select None accepted: clean no-op; review and write remain disabled."
            : $"{selection.Categories.Length} category choices accepted for review.";
    public string Context => loaded is null
        ? "Load one exact source NPC/preset and one exact target NPC/preset."
        : $"{loaded.Source.EditorId} ({loaded.Source.NpcFormId}) to " +
          $"{loaded.Target.EditorId} ({loaded.Target.NpcFormId})";

    private string sourcePlugin = string.Empty;
    public string SourcePlugin
    {
        get => sourcePlugin;
        set { if (Set(ref sourcePlugin, value ?? string.Empty)) ClearLoaded(); }
    }

    private string sourceNpcFormId = "0x00000900";
    public string SourceNpcFormId
    {
        get => sourceNpcFormId;
        set { if (Set(ref sourceNpcFormId, value ?? string.Empty)) ClearLoaded(); }
    }

    private string sourcePreset = string.Empty;
    public string SourcePreset
    {
        get => sourcePreset;
        set { if (Set(ref sourcePreset, value ?? string.Empty)) ClearLoaded(); }
    }

    private string targetPlugin = string.Empty;
    public string TargetPlugin
    {
        get => targetPlugin;
        set { if (Set(ref targetPlugin, value ?? string.Empty)) ClearLoaded(); }
    }

    private string targetNpcFormId = "0x00000800";
    public string TargetNpcFormId
    {
        get => targetNpcFormId;
        set { if (Set(ref targetNpcFormId, value ?? string.Empty)) ClearLoaded(); }
    }

    private string targetPreset = string.Empty;
    public string TargetPreset
    {
        get => targetPreset;
        set { if (Set(ref targetPreset, value ?? string.Empty)) ClearLoaded(); }
    }

    private string transactionProposalPath = string.Empty;
    public string TransactionProposalPath
    {
        get => transactionProposalPath;
        set { if (Set(ref transactionProposalPath, value ?? string.Empty)) InvalidateReview(); }
    }

    private string pluginProposalPath = string.Empty;
    public string PluginProposalPath
    {
        get => pluginProposalPath;
        set { if (Set(ref pluginProposalPath, value ?? string.Empty)) InvalidateReview(); }
    }

    private string outputPlugin = string.Empty;
    public string OutputPlugin
    {
        get => outputPlugin;
        set { if (Set(ref outputPlugin, value ?? string.Empty)) InvalidateReview(); }
    }

    private string outputPreset = string.Empty;
    public string OutputPreset
    {
        get => outputPreset;
        set { if (Set(ref outputPreset, value ?? string.Empty)) InvalidateReview(); }
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            Raise(nameof(IsNotBusy));
            Raise(nameof(CanChooseCategories));
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

    private string status = "Review a copied workspace before loading selective appearance inputs.";
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

    private string resultPreset = EmptyResult;
    public string ResultPreset
    {
        get => resultPreset;
        private set => Set(ref resultPreset, value);
    }

    private string outputPresetSha256 = EmptyResult;
    public string OutputPresetSha256
    {
        get => outputPresetSha256;
        private set => Set(ref outputPresetSha256, value);
    }

    public void ApplyReviewedIntake(ReviewedGameIntake reviewedIntake)
    {
        ArgumentNullException.ThrowIfNull(reviewedIntake);
        intake = reviewedIntake;
        string initial = reviewedIntake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded)
            .OrderByDescending(item => item.Requested)
            .ThenByDescending(item => item.Order)
            .Select(item => item.Plugin.Value)
            .FirstOrDefault() ?? string.Empty;
        sourcePlugin = initial;
        targetPlugin = initial;
        Raise(nameof(SourcePlugin));
        Raise(nameof(TargetPlugin));
        ClearLoaded();
        Status = "Reviewed workspace accepted. Choose two NPC/preset endpoints and load them.";
    }

    public void ClearReviewedIntake()
    {
        intake = null;
        sourcePlugin = string.Empty;
        targetPlugin = string.Empty;
        Raise(nameof(SourcePlugin));
        Raise(nameof(TargetPlugin));
        ClearLoaded();
        Status = "Workspace authority is stale. Review the copied workspace again.";
    }

    public async Task LoadAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildLoadRequest(out var request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Loading", "Reading both hash-bound NPC and preset carriers.");
        try
        {
            SkyrimSelectiveAppearancePasteLoadResult result =
                await loadService.LoadAsync(request!, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.State is null)
            {
                Verdict = "Load refused";
                Status = "No selective-paste transaction was created.";
                return;
            }
            loaded = result.State;
            selection = null;
            InvalidateReview();
            Verdict = "Loaded without selection";
            Status = "Both exact carriers loaded. Choose the categories to paste.";
            RaiseLoadedState();
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Load cancelled; no transaction was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Load failed";
            Status = "No transaction was retained.";
        }
        finally { Release(source); }
    }

    public SkyrimSelectiveAppearancePasteViewModel CreateSelector()
    {
        if (!CanChooseCategories || loaded is null)
            throw new InvalidOperationException("Load both exact carriers first.");
        return new SkyrimSelectiveAppearancePasteViewModel(
            loaded.Source.Document,
            loaded.Target.Document,
            loaded.Source.EditorId.Value,
            loaded.Target.EditorId.Value);
    }

    public bool ApplySelector(SkyrimSelectiveAppearancePasteViewModel selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!selector.IsAccepted || selector.AcceptedSelection is null) return false;
        selection = selector.AcceptedSelection;
        InvalidateReview();
        Verdict = HasSelection ? "Selection staged" : "Clean no-op";
        Status = HasSelection
            ? "Category selection staged. Review creates proposals only."
            : "Select None accepted. No proposal or output can be written.";
        RaiseLoadedState();
        return true;
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildTransactionRequest(out var request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Reviewing", "Persisting two hash-bound proposals; outputs remain absent.");
        try
        {
            SkyrimSelectiveAppearancePasteProposal proposal =
                await transactionService.AnalyzeAsync(request!, source.Token);
            AddDiagnostics(proposal.Diagnostics);
            if (!proposal.IsApplicable || proposal.TransactionProposalSha256 is null ||
                !File.Exists(request!.TransactionProposalPath.Value) ||
                File.Exists(request.OutputPlugin.Value) ||
                File.Exists(request.OutputPreset.Value))
            {
                Verdict = "Review refused";
                Status = "No output was written. Correct the diagnostics and review again.";
                return;
            }
            reviewedProposal = proposal;
            ProposalSha256 = proposal.TransactionProposalSha256.Value.Value;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = "Both proposals are retained and both output carriers remain absent.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Review cancelled; no output was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "No output was written.";
        }
        finally { Release(source); }
    }

    public async Task ExecuteAsync()
    {
        Diagnostics.Clear();
        if (!IsReviewed || reviewedProposal is null)
        {
            PresentInputError("Review the exact selection before writing.");
            return;
        }
        if (!TryBuildTransactionRequest(out var request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Writing", "Writing both fresh carriers atomically, then reopening both again.");
        ProgressPercent = 20;
        try
        {
            SkyrimSelectiveAppearancePasteTransactionResult applied =
                await transactionService.ApplyAsync(
                    request!, reviewedProposal, source.Token);
            AddDiagnostics(applied.Diagnostics);
            if (!applied.Applied || applied.Verification is not { IsValid: true })
            {
                Verdict = "Write refused";
                Status = "The atomic transaction did not prove both carriers; no partial output is authoritative.";
                return;
            }
            ProgressPercent = 75;
            SkyrimSelectiveAppearancePasteVerificationResult verified =
                await transactionService.VerifyAsync(
                    request!, reviewedProposal, source.Token);
            AddDiagnostics(verified.Diagnostics);
            if (!verified.IsValid ||
                verified.PluginVerification is not
                {
                    IsValid: true,
                    RecordPatchMatches: true,
                    OutputSha256: not null,
                    SourceOwnedTargetCount: 1,
                    SelfOwnedTargetCount: 0
                } ||
                !verified.SelectedPresetSectionsMatchSource ||
                !verified.UncheckedPresetSectionsPreserveTarget ||
                verified.OutputPresetSha256 is null ||
                !File.Exists(request!.OutputPlugin.Value) ||
                !File.Exists(request.OutputPreset.Value))
            {
                Verdict = "Verification failed";
                Status = "The independently reopened pair is not authoritative.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = request.OutputPlugin.Value;
            ResultPreset = request.OutputPreset.Value;
            OutputPresetSha256 = verified.OutputPresetSha256.Value.Value;
            artifactEmitter.Commit(
                this,
                "appearance-plugin-jslot",
                request.OutputPlugin,
                verified.PluginVerification.OutputSha256!.Value,
                loaded!.Target.NpcFormId,
                request.OutputPreset,
                verified.OutputPresetSha256.Value);
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = "Fresh ESP and jslot written and independently reopened. FaceGen, BodySlide-build, runtime, and visual authority remain false.";
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
            Status = "No unverified pair is authoritative.";
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
        TryPluginName(SourcePlugin, out _) &&
        TryPluginName(TargetPlugin, out _) &&
        FormId.TryParse(SourceNpcFormId, out _) &&
        FormId.TryParse(TargetNpcFormId, out _) &&
        TryWorkspaceFile(SourcePreset, out _) &&
        TryWorkspaceFile(TargetPreset, out _);

    private bool TryBuildLoadRequest(
        out SkyrimSelectiveAppearancePasteLoadRequest? request,
        out string error)
    {
        request = null;
        if (intake is null)
        {
            error = "Review a copied Skyrim workspace first.";
            return false;
        }
        if (!TryPluginName(SourcePlugin, out PluginName sourcePluginValue) ||
            !TryPluginName(TargetPlugin, out PluginName targetPluginValue) ||
            !FormId.TryParse(SourceNpcFormId, out FormId sourceFormId) ||
            !FormId.TryParse(TargetNpcFormId, out FormId targetFormId) ||
            !TryWorkspaceFile(SourcePreset, out WorkspacePath sourcePresetValue) ||
            !TryWorkspaceFile(TargetPreset, out WorkspacePath targetPresetValue))
        {
            error = "Enter two reviewed plugin names, hexadecimal NPC FormIDs, and existing K-only preset files.";
            return false;
        }
        request = new SkyrimSelectiveAppearancePasteLoadRequest(
            intake,
            sourcePluginValue,
            sourceFormId,
            sourcePresetValue,
            targetPluginValue,
            targetFormId,
            targetPresetValue);
        error = string.Empty;
        return true;
    }

    private bool TryBuildTransactionRequest(
        out SkyrimSelectiveAppearancePasteTransactionRequest? request,
        out string error)
    {
        request = null;
        if (loaded is null || selection is null || selection.Categories.IsDefaultOrEmpty)
        {
            error = "Load both carriers and accept at least one appearance category.";
            return false;
        }
        try
        {
            var transactionProposal = new WorkspacePath(TransactionProposalPath);
            var pluginProposal = new WorkspacePath(PluginProposalPath);
            var outputPluginValue = new WorkspacePath(OutputPlugin);
            var outputPresetValue = new WorkspacePath(OutputPreset);
            if (!transactionProposal.IsUnder(labRoot) ||
                !pluginProposal.IsUnder(labRoot) ||
                !outputPluginValue.IsUnder(labRoot) ||
                !outputPresetValue.IsUnder(labRoot))
            {
                error = "All proposal and output paths must remain under the K-only workspace.";
                return false;
            }
            request = new SkyrimSelectiveAppearancePasteTransactionRequest(
                loaded,
                selection,
                transactionProposal,
                pluginProposal,
                outputPluginValue,
                outputPresetValue);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TryWorkspaceFile(string value, out WorkspacePath path)
    {
        try
        {
            path = new WorkspacePath(value);
            return path.IsUnder(labRoot) && File.Exists(path.Value);
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    private static bool TryPluginName(string value, out PluginName plugin)
    {
        try { plugin = new PluginName(value); return true; }
        catch (ArgumentException) { plugin = default; return false; }
    }

    private void ClearLoaded()
    {
        loaded = null;
        selection = null;
        InvalidateReview();
        Verdict = "Not loaded";
        RaiseLoadedState();
    }

    private void InvalidateReview()
    {
        reviewedProposal = null;
        IsReviewed = false;
        HasCompleted = false;
        ProgressPercent = 0;
        ProposalSha256 = EmptyResult;
        ResultPlugin = EmptyResult;
        ResultPreset = EmptyResult;
        OutputPresetSha256 = EmptyResult;
        RaiseCommandState();
    }

    private void RaiseLoadedState()
    {
        Raise(nameof(IsLoaded));
        Raise(nameof(HasSelection));
        Raise(nameof(CanChooseCategories));
        Raise(nameof(SelectionSummary));
        Raise(nameof(Context));
        RaiseCommandState();
    }

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
