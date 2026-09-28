using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production parent for SKY-GUI-015. It binds the owner-modal editor to one
/// reviewed load order and owns proposal review, binary write, and readback.
/// </summary>
public sealed class SkyrimOutfitProductionWorkspaceViewModel :
    NotifyViewModel, IDisposable
{
    private const string EmptyResult = "Not available";
    private readonly ISkyrimOutfitProductionLoadService loadService;
    private readonly ISkyrimOutfitProductionTransactionService transactionService;
    private readonly ISkyrimOutfitItemCatalogReader itemCatalogReader;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private ReviewedGameIntake? intake;
    private SkyrimOutfitProductionState? loaded;
    private SkyrimOutfitEditorCommitResult? editorResult;
    private SkyrimOutfitProductionProposal? reviewedProposal;

    public SkyrimOutfitProductionWorkspaceViewModel(
        ISkyrimOutfitProductionLoadService loadService,
        ISkyrimOutfitProductionTransactionService transactionService,
        ISkyrimOutfitItemCatalogReader itemCatalogReader,
        WorkspacePath labRoot)
    {
        this.loadService = loadService;
        this.transactionService = transactionService;
        this.itemCatalogReader = itemCatalogReader;
        this.labRoot = labRoot;
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(
            ReviewAsync,
            () => IsLoaded && HasAuthoredProposal && !IsBusy);
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
    public bool IsLoaded => loaded is not null;
    public bool CanOpenEditor => IsLoaded && !IsBusy;
    public bool HasAcceptedSelection => editorResult is { Accepted: true };
    public bool HasAuthoredProposal => editorResult?.Proposal is not null;
    public string CatalogSummary => loaded is null
        ? "No reviewed outfit catalog is loaded."
        : $"{loaded.Outfits.Length} winning outfit choices and " +
          $"{loaded.Items.Count(item => item.Kind == SkyrimOutfitEditorItemKind.Armor)} ARMO / " +
          $"{loaded.Items.Count(item => item.Kind == SkyrimOutfitEditorItemKind.LeveledList)} LVLI items.";
    public string SelectionSummary => editorResult switch
    {
        { Proposal: { } proposal } =>
            $"{proposal.Mode} OTFT staged with {proposal.Items.Length} ordered item(s).",
        { Choice.Kind: var kind } => $"{kind} outfit choice accepted; no OTFT write is staged.",
        _ => "No accepted outfit choice or authored transaction."
    };

    private string templatePlugin = string.Empty;
    public string TemplatePlugin
    {
        get => templatePlugin;
        set { if (Set(ref templatePlugin, value ?? string.Empty)) ClearLoaded(); }
    }

    private string templateFormId = "0x00000900";
    public string TemplateFormId
    {
        get => templateFormId;
        set { if (Set(ref templateFormId, value ?? string.Empty)) ClearLoaded(); }
    }

    private string newTargetFormId = "0x00000A00";
    public string NewTargetFormId
    {
        get => newTargetFormId;
        set { if (Set(ref newTargetFormId, value ?? string.Empty)) ClearLoaded(); }
    }

    private string previewSeed = "11";
    public string PreviewSeed
    {
        get => previewSeed;
        set { if (Set(ref previewSeed, value ?? string.Empty)) ClearLoaded(); }
    }

    private string proposalPath = string.Empty;
    public string ProposalPath
    {
        get => proposalPath;
        set
        {
            if (!Set(ref proposalPath, value ?? string.Empty)) return;
            ClearAcceptedSelection();
        }
    }

    private string outputPlugin = string.Empty;
    public string OutputPlugin
    {
        get => outputPlugin;
        set { if (Set(ref outputPlugin, value ?? string.Empty)) InvalidateReview(); }
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

    private string status =
        "Review a copied Skyrim workspace before loading outfits.";
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

    public void ApplyReviewedIntake(ReviewedGameIntake reviewedIntake)
    {
        ArgumentNullException.ThrowIfNull(reviewedIntake);
        intake = reviewedIntake;
        templatePlugin = reviewedIntake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded)
            .OrderByDescending(item => item.Requested)
            .ThenByDescending(item => item.Order)
            .Select(item => item.Plugin.Value)
            .FirstOrDefault() ?? string.Empty;
        Raise(nameof(TemplatePlugin));
        ClearLoaded();
        Status = "Reviewed workspace accepted. Choose a winning OTFT template and load its catalog.";
    }

    public void ClearReviewedIntake()
    {
        intake = null;
        templatePlugin = string.Empty;
        Raise(nameof(TemplatePlugin));
        ClearLoaded();
        Status = "Workspace authority is stale. Review the copied workspace again.";
    }

    public async Task LoadAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildLoadRequest(out SkyrimOutfitProductionLoadRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Loading",
            "Reading winning OTFT, ARMO, and LVLI records from the reviewed copied closure.");
        try
        {
            SkyrimOutfitProductionLoadResult result = await loadService.LoadAsync(
                request!, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.State is null)
            {
                Verdict = "Load refused";
                Status = "No outfit authoring transaction was created.";
                return;
            }
            loaded = result.State;
            ClearAcceptedSelection();
            Verdict = "Catalog loaded";
            Status = "Reviewed authority loaded. Open the outfit workbench to choose or author.";
            RaiseLoadedState();
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Catalog load cancelled; no transaction was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Load failed";
            Status = "No outfit authoring transaction was retained.";
        }
        finally { Release(source); }
    }

    public SkyrimOutfitEditorViewModel CreateEditor(
        SkyrimOutfitChildEditor childEditor)
    {
        if (!CanOpenEditor || loaded is null)
            throw new InvalidOperationException("Load the reviewed outfit catalog first.");
        if (!TryWorkspacePath(ProposalPath, out WorkspacePath proposal))
            throw new InvalidOperationException(
                "Enter a K-only .outfit-proposal.json path before opening the workbench.");
        return new SkyrimOutfitEditorViewModel(
            loaded.Outfits,
            loaded.Items,
            loaded.Intake.DataRoot,
            loaded.TemplatePluginPath,
            loaded.TemplateFormId,
            loaded.NewTargetFormId,
            proposal,
            (reference, seed) => itemCatalogReader.Resolve(
                new SkyrimOutfitPreviewResolveRequest(
                    loaded.Intake.DataRoot,
                    loaded.PluginOrder,
                    reference,
                    seed)),
            childEditor);
    }

    internal bool TryGetNewLeveledListChildContext(
        out SkyrimLeveledListOutfitChildContext? context)
    {
        context = null;
        if (loaded is null ||
            !TryWorkspacePath(OutputPlugin, out WorkspacePath output) ||
            !FormId.TryParse(NewTargetFormId, out FormId outfitTarget) ||
            outfitTarget.Value is 0 or >= 0x00FF_FFFF)
            return false;
        SkyrimOutfitEditorItem? entryCandidate = loaded.Items.FirstOrDefault(item =>
            item.Kind == SkyrimOutfitEditorItemKind.Armor);
        if (entryCandidate is null) return false;
        try
        {
            ImmutableArray<EditorId> existingIds = loaded.Items
                .Where(item => item.Kind == SkyrimOutfitEditorItemKind.LeveledList)
                .Select(item =>
                {
                    try { return (EditorId?)new EditorId(item.DisplayName); }
                    catch (ArgumentException) { return null; }
                })
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToImmutableArray();
            context = new SkyrimLeveledListOutfitChildContext(
                new PluginName(Path.GetFileName(output.Value)),
                new FormId(outfitTarget.Value + 1),
                existingIds,
                entryCandidate);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public bool ApplyEditor(SkyrimOutfitEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.IsAccepted || editor.AcceptedResult is not { Accepted: true } accepted)
            return false;
        editorResult = accepted;
        InvalidateReview();
        Verdict = accepted.Proposal is null ? "Choice staged" : "Authoring staged";
        Status = accepted.Proposal is null
            ? "The typed outfit choice is ready for an NPC caller; no plugin write is staged."
            : "The complete ordered OTFT is staged. Review writes its proposal only.";
        RaiseAcceptedState();
        return true;
    }

    public void ReportEditorCancelled()
    {
        Status = "Outfit workbench cancelled or closed; the previously accepted state is unchanged.";
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildTransactionRequest(
                out SkyrimOutfitProductionTransactionRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Reviewing",
            "Persisting one hash-bound OTFT proposal; the output ESP remains absent.");
        try
        {
            SkyrimOutfitProductionProposal proposal = await transactionService
                .AnalyzeAsync(request!, source.Token);
            AddDiagnostics(proposal.Diagnostics);
            if (!proposal.IsApplicable || proposal.ProposalSha256 is null ||
                !File.Exists(request!.OutfitProposal.OutputProposal.Value) ||
                File.Exists(request.OutputPlugin.Value))
            {
                Verdict = "Review refused";
                Status = "No output plugin was written. Correct the diagnostics and review again.";
                return;
            }
            reviewedProposal = proposal;
            ProposalSha256 = proposal.ProposalSha256.Value.Value;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = "Proposal retained and output ESP absent. Apply will use this exact reviewed request.";
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
        if (!IsReviewed || reviewedProposal is null)
        {
            PresentInputError("Review the exact authored outfit before writing.");
            return;
        }
        if (!TryBuildTransactionRequest(
                out SkyrimOutfitProductionTransactionRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Writing",
            "Writing one fresh ESP and reopening its exact record surface.");
        ProgressPercent = 20;
        try
        {
            SkyrimOutfitProductionResult applied = await transactionService.ApplyAsync(
                request!, reviewedProposal, source.Token);
            AddDiagnostics(applied.Diagnostics);
            if (!applied.Applied || applied.Verification is not { IsValid: true })
            {
                Verdict = "Write refused";
                Status = "The OTFT output did not pass its first readback and is not authoritative.";
                return;
            }
            ProgressPercent = 75;
            SkyrimOutfitProductionVerification verified = await transactionService.VerifyAsync(
                request!, reviewedProposal, source.Token);
            AddDiagnostics(verified.Diagnostics);
            if (!verified.IsValid || !verified.OwnerMatches ||
                !verified.EditorIdMatches || !verified.OrderedItemsMatch ||
                !verified.MasterSetMatches || verified.RecordCount != 1 ||
                verified.OutputSha256 is null || !File.Exists(request!.OutputPlugin.Value))
            {
                Verdict = "Verification failed";
                Status = "The explicit second reopen did not prove the exact OTFT output.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = verified.OutputPlugin.Value;
            ResultSha256 = verified.OutputSha256.Value.Value;
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = "Fresh ESP written and independently reopened. Preview, equipment, runtime, and visual authority remain false.";
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
        TryPluginName(TemplatePlugin, out _) &&
        FormId.TryParse(TemplateFormId, out _) &&
        FormId.TryParse(NewTargetFormId, out _) &&
        long.TryParse(PreviewSeed, out _);

    private bool TryBuildLoadRequest(
        out SkyrimOutfitProductionLoadRequest? request,
        out string error)
    {
        request = null;
        if (intake is null)
        {
            error = "Review a copied Skyrim workspace first.";
            return false;
        }
        if (!TryPluginName(TemplatePlugin, out PluginName plugin) ||
            !FormId.TryParse(TemplateFormId, out FormId template) ||
            !FormId.TryParse(NewTargetFormId, out FormId target) ||
            !long.TryParse(PreviewSeed, out long seed))
        {
            error = "Enter a reviewed plugin, two nonzero hexadecimal local FormIDs, and a signed preview seed.";
            return false;
        }
        request = new SkyrimOutfitProductionLoadRequest(
            intake,
            plugin,
            template,
            target,
            seed);
        error = string.Empty;
        return true;
    }

    private bool TryBuildTransactionRequest(
        out SkyrimOutfitProductionTransactionRequest? request,
        out string error)
    {
        request = null;
        if (editorResult?.Proposal is not { } proposal)
        {
            error = "Open the workbench and save a new or override OTFT first.";
            return false;
        }
        if (!TryWorkspacePath(OutputPlugin, out WorkspacePath output) ||
            !output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
        {
            error = "Enter a fresh K-only .esp output path.";
            return false;
        }
        request = new SkyrimOutfitProductionTransactionRequest(proposal, output);
        error = string.Empty;
        return true;
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

    private static bool TryPluginName(string value, out PluginName plugin)
    {
        try { plugin = new PluginName(value); return true; }
        catch (ArgumentException) { plugin = default; return false; }
    }

    private void ClearLoaded()
    {
        loaded = null;
        ClearAcceptedSelection();
        Verdict = "Not loaded";
        RaiseLoadedState();
    }

    private void ClearAcceptedSelection()
    {
        editorResult = null;
        InvalidateReview();
        RaiseAcceptedState();
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
        Raise(nameof(CatalogSummary));
        RaiseCommandState();
    }

    private void RaiseAcceptedState()
    {
        Raise(nameof(HasAcceptedSelection));
        Raise(nameof(HasAuthoredProposal));
        Raise(nameof(SelectionSummary));
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
        public bool CanExecute(object? parameter) =>
            canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed record SkyrimLeveledListOutfitChildContext(
    PluginName Provider,
    FormId ProvisionalFormId,
    ImmutableArray<EditorId> ExistingEditorIds,
    SkyrimOutfitEditorItem EntryCandidate);
