using System.Collections.ObjectModel;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production parent transaction for SKY-GUI-011/012. The child face editor
/// remains I/O-free; this coordinator binds one reviewed source hash to one
/// persisted proposal, fresh output, and explicit post-write verification.
/// </summary>
public sealed class SkyrimFaceEditWorkspaceViewModel :
    NotifyViewModel,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyResult = "Not available";
    private const int FaceOverlaySlotLimit = 16;
    private readonly ISkyrimFaceEditLoadService loadService;
    private readonly INpcAppearanceOverrideService overrideService;
    private readonly ISkyrimHeadPartPreviewService? previewService;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();
    private ReviewedGameIntake? intake;
    private SkyrimFaceEditLoadedState? loaded;
    private SkyrimFaceEditorDocument? staged;
    private NpcAppearanceOverrideProposal? reviewedProposal;

    public SkyrimFaceEditWorkspaceViewModel(
        ISkyrimFaceEditLoadService loadService,
        INpcAppearanceOverrideService overrideService,
        ISkyrimHeadPartPreviewService? previewService,
        WorkspacePath labRoot)
    {
        this.loadService = loadService;
        this.overrideService = overrideService;
        this.previewService = previewService;
        this.labRoot = labRoot;
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(
            ReviewAsync,
            () => IsLoaded && HasStagedChange && !IsBusy);
        executeCommand = new AsyncCommand(
            ExecuteAsync,
            () => IsReviewed && !HasCompleted && !IsBusy);
        cancelCommand = new DelegateCommand(
            _ => cancellation?.Cancel(),
            _ => IsBusy);
    }

    public ObservableCollection<SkyrimFaceEditChangeRowViewModel> Changes { get; } = [];
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
    public SkyrimFaceEditorDocument? StagedDocument => staged;
    public bool IsLoaded => loaded is not null && staged is not null;
    public bool HasStagedChange => IsLoaded &&
        !SkyrimFaceEditorDocumentRules.Equivalent(staged!, loaded!.Document);
    public bool CanOpenEditor => IsLoaded && !IsBusy;
    public string Context => loaded is null
        ? "Review a copied Skyrim workspace, then load one complete source NPC."
        : $"{loaded.SourceEditorId} / {loaded.RaceEditorId} / " +
          $"{loaded.Sex.ToString().ToLowerInvariant()} / {loaded.Document.Parts.OrderedHeadParts.Length} PNAM row(s)";
    public string ReadinessText => !IsLoaded
        ? "Load one hash-bound NPC with complete FTST, NAM9/NAMA, tint, and QNAM authority."
        : !HasStagedChange
            ? "Open the face editor. Paint selection, clear, and Cancel are distinct; reset any mask before plugin review."
            : IsReviewed
                ? "The proposal is current. Write one fresh ESP and verify it."
                : "A writer-representable face document is staged. Review writes only the proposal JSON.";

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
            InvalidateReview();
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

    private string status = "Review a copied workspace before editing one complete Skyrim face.";
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

    private string outputSha256 = EmptyResult;
    public string OutputSha256
    {
        get => outputSha256;
        private set => Set(ref outputSha256, value);
    }

    private string proposalSha256 = EmptyResult;
    public string ProposalSha256
    {
        get => proposalSha256;
        private set => Set(ref proposalSha256, value);
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
        Status = "Reviewed workspace accepted. Choose one complete source NPC and load its face document.";
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
        if (!TryBuildLoadRequest(out SkyrimFaceEditLoadRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Loading",
            "Reading the exact appearance baseline and all typed face catalogs.");
        try
        {
            SkyrimFaceEditLoadResult result = await loadService.LoadAsync(
                request!,
                source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.State is null)
            {
                Verdict = "Load refused";
                Status = "No editable face transaction was created.";
                return;
            }
            loaded = result.State;
            staged = result.State.Document;
            InvalidateReview();
            Verdict = "Loaded without changes";
            Status = "Loaded one exact complete face document; no mutation is staged.";
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
        finally
        {
            Release(source);
        }
    }

    public SkyrimFaceEditorViewModel CreateEditor()
    {
        if (!CanOpenEditor || loaded is null || staged is null || intake is null)
            throw new InvalidOperationException("Load one complete reviewed face document first.");
        var catalogs = new SkyrimFaceEditorCatalogs(
            loaded.Catalogs.TypedForms,
            loaded.Catalogs.HeadParts,
            loaded.Catalogs.Paints,
            loaded.Catalogs.SliderCatalog,
            loaded.Catalogs.RaceDefaultHeadParts);
        return new SkyrimFaceEditorViewModel(
            staged,
            intake.DataRoot,
            loaded.Race,
            loaded.RaceEditorId,
            loaded.Sex,
            catalogs,
            FaceOverlaySlotLimit,
            previewService);
    }

    public bool ApplyEditor(SkyrimFaceEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.IsAccepted || editor.AcceptedDocument is not { } accepted ||
            loaded is null || staged is null)
        {
            return false;
        }
        SkyrimFaceEditProjectionResult projection = SkyrimFaceEditProjector.Project(
            loaded.WriterBaseline,
            accepted);
        AddDiagnostics(projection.Diagnostics);
        if (!projection.Accepted || projection.Appearance is null)
        {
            Verdict = "Face edit refused";
            Status = "The accepted document changes a sidecar-only or unsupported field; no parent change was staged.";
            return false;
        }
        if (SkyrimFaceEditorDocumentRules.Equivalent(staged, accepted)) return false;
        staged = accepted;
        InvalidateReview();
        Verdict = HasStagedChange ? "Change staged" : "Loaded without changes";
        Status = HasStagedChange
            ? "Staged one complete writer-representable face document. Review before writing."
            : "The accepted editor document matches the loaded baseline.";
        RaiseLoadedState();
        return true;
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        Changes.Clear();
        if (!TryBuildOverrideRequest(out NpcAppearanceOverrideRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Reviewing",
            "Persisting one hash-bound appearance proposal; the ESP is not written during review.");
        try
        {
            NpcAppearanceOverrideProposal proposal = await overrideService.AnalyzeAsync(
                request!,
                source.Token);
            AddDiagnostics(proposal.Diagnostics);
            foreach (string subrecord in proposal.ChangedNpcSubrecords)
                Changes.Add(new SkyrimFaceEditChangeRowViewModel(
                    subrecord,
                    "source-owned",
                    "reviewed appearance value"));
            if (!proposal.IsApplicable || proposal.ProposalSha256 is null)
            {
                Verdict = "Review refused";
                Status = "No ESP will be written. Correct the diagnostic and review again.";
                return;
            }
            reviewedProposal = proposal;
            ProposalSha256 = proposal.ProposalSha256.Value.Value;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = $"Proposal retained with {Changes.Count} declared NPC subrecord surface(s); output remains absent.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Review cancelled; no ESP was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "No ESP was written.";
        }
        finally
        {
            Release(source);
        }
    }

    public async Task ExecuteAsync()
    {
        Diagnostics.Clear();
        if (!IsReviewed || reviewedProposal is null)
        {
            PresentInputError("Review the exact complete face document before writing.");
            return;
        }
        if (!TryBuildOverrideRequest(out NpcAppearanceOverrideRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Writing",
            "Writing one fresh ESP, reopening it, and running explicit final verification.");
        ProgressPercent = 20;
        try
        {
            NpcAppearanceOverrideResult applied = await overrideService.ApplyAsync(
                request!,
                reviewedProposal,
                source.Token);
            AddDiagnostics(applied.Diagnostics);
            if (!applied.Applied || applied.Verification is not { IsValid: true })
            {
                Verdict = "Write refused";
                Status = "No verified output plugin was retained.";
                return;
            }
            ProgressPercent = 75;
            NpcAppearanceOverrideVerificationResult verified =
                await overrideService.VerifyAsync(
                    request!,
                    reviewedProposal,
                    source.Token);
            AddDiagnostics(verified.Diagnostics);
            if (!verified.IsValid || verified.OutputSha256 is null)
            {
                Verdict = "Verification failed";
                Status = "The written ESP is not authoritative and must not be promoted.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = request!.OutputPlugin.Value;
            OutputSha256 = verified.OutputSha256.Value.Value;
            artifactEmitter.Commit(
                this,
                "face-plugin",
                request.OutputPlugin,
                verified.OutputSha256.Value,
                request.TargetFormId);
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = "Fresh ESP written and independently reopened. Runtime, FaceGen, texture-render, and visual authority remain false.";
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
        finally
        {
            Release(source);
        }
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
        out SkyrimFaceEditLoadRequest? request,
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
        request = new SkyrimFaceEditLoadRequest(intake, plugin, formId);
        error = string.Empty;
        return true;
    }

    private bool TryBuildOverrideRequest(
        out NpcAppearanceOverrideRequest? request,
        out string error)
    {
        request = null;
        if (loaded is null || staged is null || !HasStagedChange)
        {
            error = "Load one NPC and stage one writer-representable face change first.";
            return false;
        }
        SkyrimFaceEditProjectionResult projection = SkyrimFaceEditProjector.Project(
            loaded.WriterBaseline,
            staged);
        if (!projection.Accepted || projection.Appearance is null)
        {
            error = projection.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error)?.Message ??
                "The staged face document cannot be represented by the verified writer.";
            return false;
        }
        try
        {
            var proposal = new WorkspacePath(ProposalPath);
            var output = new WorkspacePath(OutputPlugin);
            if (!proposal.IsUnder(labRoot) || !output.IsUnder(labRoot))
            {
                error = "Proposal and output paths must remain under the K-only workspace.";
                return false;
            }
            request = new NpcAppearanceOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                loaded.SourcePluginPath,
                loaded.SourcePluginSha256,
                loaded.TargetFormId,
                proposal,
                output,
                loaded.Race,
                loaded.Sex,
                projection.Appearance,
                projection.RuntimeAppearance);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void ClearLoaded()
    {
        loaded = null;
        staged = null;
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
        OutputSha256 = EmptyResult;
        ProposalSha256 = EmptyResult;
        RaiseLoadedState();
    }

    private void RaiseLoadedState()
    {
        Raise(nameof(StagedDocument));
        Raise(nameof(IsLoaded));
        Raise(nameof(HasStagedChange));
        Raise(nameof(CanOpenEditor));
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
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record SkyrimFaceEditChangeRowViewModel(
    string Subrecord,
    string Before,
    string After);
