using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production parent transaction for SKY-GUI-010. It stages only PNAM, delegates
/// every choice to the reusable typed picker, and writes only after review.
/// </summary>
public sealed class SkyrimHeadPartEditViewModel :
    NotifyViewModel,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyResult = "Not available";
    private readonly ISkyrimHeadPartEditLoadService loadService;
    private readonly INpcFacePatchService patchService;
    private readonly ISkyrimHeadPartPreviewService? previewService;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();
    private ReviewedGameIntake? intake;
    private SkyrimHeadPartEditLoadResult? loaded;
    private NpcFacePatchProposal? reviewedProposal;
    private ImmutableArray<NpcHeadPartSelection> baseline = [];
    private ImmutableArray<NpcHeadPartSelection> staged = [];

    public SkyrimHeadPartEditViewModel(
        ISkyrimHeadPartEditLoadService loadService,
        INpcFacePatchService patchService,
        ISkyrimHeadPartPreviewService? previewService,
        WorkspacePath labRoot)
    {
        this.loadService = loadService;
        this.patchService = patchService;
        this.previewService = previewService;
        this.labRoot = labRoot;
        TypeOptions = Enum.GetValues<NpcHeadPartType>()
            .Select(type => new SkyrimHeadPartTypeOption(type, type.ToWireName()))
            .ToArray();
        selectedType = TypeOptions.Single(item => item.Value == NpcHeadPartType.Hair);
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(ReviewAsync,
            () => IsLoaded && HasStagedChange && !IsBusy);
        executeCommand = new AsyncCommand(ExecuteAsync,
            () => IsReviewed && !HasCompleted && !IsBusy);
        cancelCommand = new DelegateCommand(_ => cancellation?.Cancel(), _ => IsBusy);
    }

    public IReadOnlyList<SkyrimHeadPartTypeOption> TypeOptions { get; }
    public ObservableCollection<SkyrimHeadPartEditRowViewModel> HeadParts { get; } = [];
    public ObservableCollection<ExistingNpcEditChangeViewModel> Changes { get; } = [];
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

    public ImmutableArray<NpcHeadPartSelection> StagedHeadParts => staged;
    public bool IsLoaded => loaded?.Accepted == true && loaded.Snapshot is not null;
    public bool HasStagedChange => IsLoaded && !staged.SequenceEqual(baseline);
    public bool CanOpenPicker => IsLoaded && !IsBusy &&
                                 loaded!.Catalogs.TryGetValue(SelectedType.Value, out SkyrimHeadPartChoiceResult? result) &&
                                 result.Accepted;
    public string Context => IsLoaded
        ? $"{loaded!.Snapshot!.Sex.ToString().ToLowerInvariant()} / race {loaded.Snapshot.Race} / {staged.Length} ordered PNAM row(s)"
        : "Review a copied Skyrim workspace, then load one exact NPC.";
    public string ReadinessText => !IsLoaded
        ? "Load one hash-bound source NPC before choosing a head part."
        : !HasStagedChange
            ? "Choose one compatible typed head part; loading never stages a change."
            : IsReviewed
                ? "Review is current. The fresh output plugin can be written."
                : "The PNAM change is staged. Review the exact before/after value.";

    private string sourcePlugin = string.Empty;
    public string SourcePlugin
    {
        get => sourcePlugin;
        set
        {
            if (!Set(ref sourcePlugin, value ?? string.Empty)) return;
            ClearLoaded();
        }
    }

    private string npcFormId = "0x00000800";
    public string NpcFormId
    {
        get => npcFormId;
        set
        {
            if (!Set(ref npcFormId, value ?? string.Empty)) return;
            ClearLoaded();
        }
    }

    private string outputPlugin = string.Empty;
    public string OutputPlugin
    {
        get => outputPlugin;
        set
        {
            if (!Set(ref outputPlugin, value ?? string.Empty)) return;
            InvalidateReview();
        }
    }

    private SkyrimHeadPartTypeOption selectedType;
    public SkyrimHeadPartTypeOption SelectedType
    {
        get => selectedType;
        set
        {
            if (!Set(ref selectedType, value)) return;
            Raise(nameof(CanOpenPicker));
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
            Raise(nameof(CanOpenPicker));
            RaiseCommandState();
        }
    }
    public bool IsNotBusy => !IsBusy;

    private bool isReviewed;
    public bool IsReviewed
    {
        get => isReviewed;
        private set
        {
            if (!Set(ref isReviewed, value)) return;
            Raise(nameof(ReadinessText));
            RaiseCommandState();
        }
    }

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set
        {
            if (!Set(ref hasCompleted, value)) return;
            RaiseCommandState();
        }
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private string status = "Review a copied workspace before editing PNAM.";
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

    private string resultPlugin = EmptyResult;
    public string ResultPlugin
    {
        get => resultPlugin;
        private set => Set(ref resultPlugin, value);
    }

    private string resultPluginSha256 = EmptyResult;
    public string ResultPluginSha256
    {
        get => resultPluginSha256;
        private set => Set(ref resultPluginSha256, value);
    }

    public void ApplyReviewedIntake(ReviewedGameIntake reviewedIntake)
    {
        ArgumentNullException.ThrowIfNull(reviewedIntake);
        intake = reviewedIntake;
        PluginClosureReviewEntry? source = reviewedIntake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded)
            .OrderByDescending(item => item.Requested)
            .ThenByDescending(item => item.Order)
            .FirstOrDefault();
        sourcePlugin = source?.Plugin.Value ?? string.Empty;
        Raise(nameof(SourcePlugin));
        ClearLoaded();
        Status = "Reviewed workspace accepted. Choose its source NPC and load PNAM.";
    }

    public void ClearReviewedIntake()
    {
        intake = null;
        sourcePlugin = string.Empty;
        Raise(nameof(SourcePlugin));
        ClearLoaded();
        Status = "Workspace authority is stale. Review the copied workspace again.";
    }

    public async Task LoadAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildLoadRequest(out SkyrimHeadPartEditLoadRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin("Loading", "Reading the exact PNAM baseline and compatible HDPT catalogs.");
        try
        {
            SkyrimHeadPartEditLoadResult result = await loadService.LoadAsync(request!, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.Snapshot is null)
            {
                Verdict = "Load refused";
                Status = "No editable head-part transaction was created.";
                return;
            }
            loaded = result;
            baseline = result.Snapshot.HeadParts;
            staged = baseline;
            RefreshRows();
            InvalidateReview();
            Verdict = "Loaded without changes";
            Status = $"Loaded {baseline.Length} exact PNAM row(s); no mutation is staged.";
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

    public SkyrimHeadPartPickerViewModel CreatePicker()
    {
        if (!CanOpenPicker || intake is null || loaded?.Snapshot?.Race is not { } race ||
            !loaded.Catalogs.TryGetValue(SelectedType.Value, out SkyrimHeadPartChoiceResult? catalog))
            throw new InvalidOperationException("Load a reviewed NPC and select an admitted head-part type first.");
        return new SkyrimHeadPartPickerViewModel(
            catalog,
            intake.DataRoot,
            race,
            loaded.Snapshot.Sex,
            SelectedType.Value,
            previewService);
    }

    public bool ApplyPicker(SkyrimHeadPartPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted || picker.AcceptedReference is not { } accepted)
            return false;
        var selection = new NpcHeadPartSelection(accepted, picker.Type);
        ImmutableArray<NpcHeadPartSelection>.Builder rows = staged.ToBuilder();
        if (picker.Type == NpcHeadPartType.Misc)
        {
            if (rows.Any(item => item.Reference == accepted)) return false;
            rows.Add(selection);
        }
        else
        {
            int index = -1;
            for (int candidate = 0; candidate < rows.Count; candidate++)
            {
                if (rows[candidate].Type != picker.Type) continue;
                index = candidate;
                break;
            }
            if (index < 0)
            {
                Diagnostics.Add(
                    $"The loaded PNAM baseline has no classified {picker.Type.ToWireName()} row; no change was staged.");
                Status = "The typed selection was refused because its PNAM bucket is absent.";
                return false;
            }
            rows[index] = selection;
        }
        ImmutableArray<NpcHeadPartSelection> next = rows.ToImmutable();
        if (next.SequenceEqual(staged)) return false;
        staged = next;
        RefreshRows();
        InvalidateReview();
        Verdict = "Change staged";
        Status = $"Staged {picker.Type.ToWireName()} as {accepted}. Review before writing.";
        RaiseLoadedState();
        return true;
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        Changes.Clear();
        if (!TryBuildPatchRequest(expectedHash: loaded?.SourcePluginSha256,
                out NpcFacePatchRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin("Reviewing", "Analyzing the exact PNAM change; nothing is written during review.");
        try
        {
            NpcFacePatchProposal proposal = await patchService.AnalyzeAsync(request!, source.Token);
            AddDiagnostics(proposal.Diagnostics);
            foreach (MutationChange change in proposal.Changes)
                Changes.Add(ExistingNpcEditChangeViewModel.From(change));
            if (!proposal.IsApplicable)
            {
                Verdict = "Review refused";
                Status = "Nothing will be written. Correct the diagnostic and review again.";
                return;
            }
            reviewedProposal = proposal;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = $"{Changes.Count} exact PNAM change(s) reviewed; no other face section is authorized.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Review cancelled; nothing was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "Nothing was written.";
        }
        finally { Release(source); }
    }

    public async Task ExecuteAsync()
    {
        Diagnostics.Clear();
        if (!IsReviewed || reviewedProposal is null)
        {
            PresentInputError("Review the exact PNAM change before writing.");
            return;
        }
        if (!TryBuildPatchRequest(reviewedProposal.InputHash,
                out NpcFacePatchRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin("Writing", "Writing one fresh plugin and reopening exact PNAM.");
        ProgressPercent = 20;
        try
        {
            NpcFacePatchRequest appliedRequest = request!;
            NpcFacePatchResult result = await patchService.ApplyAsync(
                appliedRequest, reviewedProposal, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Applied || result.OutputHash is null)
            {
                Verdict = "Write refused";
                Status = "No output plugin was retained.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = appliedRequest.OutputPlugin.Value;
            ResultPluginSha256 = result.OutputHash.Value.Value;
            artifactEmitter.Commit(
                this,
                "headpart-plugin",
                appliedRequest.OutputPlugin,
                result.OutputHash.Value,
                appliedRequest.TargetFormId);
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = "Fresh plugin written and exact PNAM independently reopened.";
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
            Status = "No verified output was retained.";
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
                              FormId.TryParse(NpcFormId, out _);

    private bool TryBuildLoadRequest(
        out SkyrimHeadPartEditLoadRequest? request,
        out string error)
    {
        request = null;
        if (intake is null)
        {
            error = "Review a copied Skyrim workspace first.";
            return false;
        }
        if (!TryPluginName(SourcePlugin, out PluginName plugin) ||
            !FormId.TryParse(NpcFormId, out FormId formId))
        {
            error = "Enter one reviewed plugin filename and hexadecimal NPC FormID.";
            return false;
        }
        request = new SkyrimHeadPartEditLoadRequest(intake, plugin, formId);
        error = string.Empty;
        return true;
    }

    private bool TryBuildPatchRequest(
        Sha256Hash? expectedHash,
        out NpcFacePatchRequest? request,
        out string error)
    {
        request = null;
        if (!IsLoaded || loaded is null || intake is null || !HasStagedChange)
        {
            error = "Load one NPC and stage one typed head-part change first.";
            return false;
        }
        if (!FormId.TryParse(NpcFormId, out FormId formId))
        {
            error = "Enter one hexadecimal NPC FormID.";
            return false;
        }
        try
        {
            var output = new WorkspacePath(OutputPlugin);
            if (!output.IsUnder(labRoot))
            {
                error = "The output plugin must remain under the K-only workspace.";
                return false;
            }
            request = new NpcFacePatchRequest(
                GameEdition.SkyrimSpecialEdition,
                loaded.SourcePluginPath,
                output,
                formId,
                new NpcFacePatch(staged, default),
                intake.DataRoot,
                expectedHash,
                false,
                null);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void RefreshRows()
    {
        HeadParts.Clear();
        foreach (NpcHeadPartSelection item in staged)
            HeadParts.Add(new SkyrimHeadPartEditRowViewModel(
                item.Type.ToWireName(item.RawPnamType),
                item.Reference.Plugin.Value,
                item.Reference.FormId.ToString()));
    }

    private void ClearLoaded()
    {
        loaded = null;
        baseline = [];
        staged = [];
        HeadParts.Clear();
        InvalidateReview();
        Verdict = "Not loaded";
        RaiseLoadedState();
    }

    private void InvalidateReview()
    {
        reviewedProposal = null;
        IsReviewed = false;
        HasCompleted = false;
        Changes.Clear();
        ProgressPercent = 0;
        ResultPlugin = EmptyResult;
        ResultPluginSha256 = EmptyResult;
        RaiseLoadedState();
    }

    private void RaiseLoadedState()
    {
        Raise(nameof(IsLoaded));
        Raise(nameof(HasStagedChange));
        Raise(nameof(CanOpenPicker));
        Raise(nameof(Context));
        Raise(nameof(ReadinessText));
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

    private static bool TryPluginName(string value, out PluginName plugin)
    {
        try
        {
            plugin = new PluginName(value);
            return true;
        }
        catch (ArgumentException)
        {
            plugin = default;
            return false;
        }
    }

    private sealed class DelegateCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record SkyrimHeadPartEditRowViewModel(
    string Type,
    string Plugin,
    string FormId);
