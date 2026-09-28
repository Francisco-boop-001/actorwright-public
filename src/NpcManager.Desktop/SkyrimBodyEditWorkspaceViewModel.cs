using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production parent transaction for SKY-GUI-013. The child body editor stays
/// I/O-free; this coordinator binds a reviewed source hash to a proposal-first
/// source-owned weight override, fresh output, and explicit production
/// verification.
/// </summary>
public sealed class SkyrimBodyEditWorkspaceViewModel :
    NotifyViewModel,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyResult = "Not available";
    private readonly ISkyrimBodyEditLoadService loadService;
    private readonly INpcOverrideService overrideService;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();
    private ReviewedGameIntake? intake;
    private SkyrimBodyEditLoadedState? loaded;
    private SkyrimBodyEditorDocument? staged;
    private NpcOverrideProposal? reviewedProposal;

    public SkyrimBodyEditWorkspaceViewModel(
        ISkyrimBodyEditLoadService loadService,
        INpcOverrideService overrideService,
        WorkspacePath labRoot)
    {
        this.loadService = loadService;
        this.overrideService = overrideService;
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

    public ObservableCollection<SkyrimBodyEditChangeRowViewModel> Changes { get; } = [];
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
    public SkyrimBodyEditorDocument? StagedDocument => staged;
    public bool IsLoaded => loaded is not null && staged is not null;
    public bool HasStagedChange => IsLoaded &&
        !SkyrimBodyEditorDocumentRules.Equivalent(staged!, loaded!.Document);
    public bool CanOpenEditor => IsLoaded && !IsBusy;
    public string Context => loaded is null
        ? "Review a copied Skyrim workspace, then load one exact source NPC."
        : $"{loaded.SourceEditorId} / NAM7 {loaded.Document.Weight:0.##} / " +
          "sidecar baseline empty by design";
    public string ReadinessText => !IsLoaded
        ? "Load one hash-bound NPC with exact NAM7 authority."
        : !HasStagedChange
            ? "Open the five-section body editor. Only NAM7 has a verified plugin write route."
            : IsReviewed
                ? "The proposal is current. Write one fresh plugin and verify it."
                : "A writer-representable body document is staged. Review writes only proposal JSON.";

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

    private string status = "Review a copied workspace before editing one Skyrim body.";
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
        Status = "Reviewed workspace accepted. Choose one source NPC and load its body document.";
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
        if (!TryBuildLoadRequest(out SkyrimBodyEditLoadRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Loading",
            "Reading exact NAM7 authority and reviewed body-paint catalogs.");
        try
        {
            SkyrimBodyEditLoadResult result = await loadService.LoadAsync(
                request!,
                source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.State is null)
            {
                Verdict = "Load refused";
                Status = "No editable body transaction was created.";
                return;
            }
            loaded = result.State;
            staged = result.State.Document;
            InvalidateReview();
            Verdict = "Loaded without changes";
            Status = "Loaded one exact body document; no mutation is staged.";
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

    public SkyrimBodyEditorViewModel CreateEditor()
    {
        if (!CanOpenEditor || loaded is null || staged is null)
        {
            throw new InvalidOperationException(
                "Load one complete reviewed body document first.");
        }
        var catalogs = new SkyrimBodyEditorCatalogs(
            loaded.Catalogs.BodySlideNames,
            loaded.Catalogs.NodeNames,
            loaded.Catalogs.Paints,
            loaded.Catalogs.OverlaySlotLimits);
        return new SkyrimBodyEditorViewModel(
            staged,
            loaded.SourceEditorId.Value,
            catalogs);
    }

    public bool ApplyEditor(SkyrimBodyEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.IsAccepted || editor.AcceptedDocument is not { } accepted ||
            loaded is null || staged is null)
        {
            return false;
        }
        SkyrimBodyEditProjectionResult projection = SkyrimBodyEditProjector.Project(
            loaded.WriterBaseline,
            accepted);
        AddDiagnostics(projection.Diagnostics);
        if (!projection.Accepted || projection.WeightPatch is null)
        {
            Verdict = "Body edit refused";
            Status = "The accepted document changes a sidecar-only or unsupported field; no parent change was staged.";
            return false;
        }
        if (SkyrimBodyEditorDocumentRules.Equivalent(staged, accepted)) return false;
        staged = accepted;
        InvalidateReview();
        Verdict = HasStagedChange ? "Change staged" : "Loaded without changes";
        Status = HasStagedChange
            ? "Staged one complete writer-representable body document. Review before writing."
            : "The accepted editor document matches the loaded baseline.";
        RaiseLoadedState();
        return true;
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        Changes.Clear();
        if (!TryBuildOverrideRequest(out NpcOverrideRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Reviewing",
            "Persisting one hash-bound NAM7 proposal; the plugin is not written during review.");
        try
        {
            NpcOverrideRequest overrideRequest = request!;
            NpcOverrideProposal proposal = await overrideService.AnalyzeAsync(
                overrideRequest,
                source.Token);
            AddDiagnostics(proposal.Diagnostics);
            foreach (MutationChange change in proposal.Changes)
            {
                Changes.Add(new SkyrimBodyEditChangeRowViewModel(
                    change.Field,
                    change.Before ?? "absent",
                    change.After ?? "absent"));
            }
            WorkspacePath? persistedProposalPath = overrideRequest.ProposalPath;
            if (!proposal.IsApplicable || persistedProposalPath is null ||
                proposal.ProposalSha256 is null ||
                !File.Exists(persistedProposalPath.Value.Value) ||
                HashFile(persistedProposalPath.Value.Value) != proposal.ProposalSha256)
            {
                Verdict = "Review refused";
                Status = "No plugin will be written. Correct the diagnostic and review again.";
                return;
            }
            reviewedProposal = proposal;
            ProposalSha256 = proposal.ProposalSha256.Value.Value;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = $"Proposal retained with {Changes.Count} exact field change(s); output remains absent.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Review cancelled; no plugin was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "No plugin was written.";
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
            PresentInputError("Review the exact body document before writing.");
            return;
        }
        if (!TryBuildOverrideRequest(out NpcOverrideRequest? request, out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Writing",
            "Writing one fresh plugin, reopening it, and running explicit final verification.");
        ProgressPercent = 20;
        try
        {
            NpcOverrideRequest overrideRequest = request!;
            NpcOverrideResult applied = await overrideService.ApplyAsync(
                overrideRequest,
                reviewedProposal,
                source.Token);
            AddDiagnostics(applied.Diagnostics);
            if (!applied.Applied || applied.OutputSha256 is null ||
                !IsExactSourceOwnedVerification(applied.Verification))
            {
                Verdict = "Write refused";
                Status = "The output did not prove one source-owned NPC override; it must not be promoted.";
                return;
            }
            ProgressPercent = 75;
            NpcOverrideVerificationResult verified = await overrideService.VerifyAsync(
                overrideRequest,
                reviewedProposal,
                source.Token);
            AddDiagnostics(verified.Diagnostics);
            if (!IsExactSourceOwnedVerification(verified) ||
                !File.Exists(overrideRequest.OutputPlugin.Value))
            {
                Verdict = "Verification failed";
                Status = "The written plugin is not authoritative and must not be promoted.";
                return;
            }
            Sha256Hash observed = HashFile(overrideRequest.OutputPlugin.Value);
            if (observed != applied.OutputSha256 ||
                verified.OutputSha256 is null ||
                observed != verified.OutputSha256)
            {
                Diagnostics.Add(
                    "Error: body-edit-output-hash-mismatch: The output changed after production verification.");
                Verdict = "Verification failed";
                Status = "The written plugin is not authoritative and must not be promoted.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = overrideRequest.OutputPlugin.Value;
            OutputSha256 = observed.Value;
            artifactEmitter.Commit(
                this,
                "body-plugin",
                overrideRequest.OutputPlugin,
                observed,
                overrideRequest.TargetFormId);
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = "Fresh plugin written and independently reopened. BodySlide-build, mesh, texture-render, runtime, and visual authority remain false.";
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
        out SkyrimBodyEditLoadRequest? request,
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
        request = new SkyrimBodyEditLoadRequest(intake, plugin, formId);
        error = string.Empty;
        return true;
    }

    private bool TryBuildOverrideRequest(
        out NpcOverrideRequest? request,
        out string error)
    {
        request = null;
        if (loaded is null || staged is null || !HasStagedChange)
        {
            error = "Load one NPC and stage one writer-representable NAM7 change first.";
            return false;
        }
        SkyrimBodyEditProjectionResult projection = SkyrimBodyEditProjector.Project(
            loaded.WriterBaseline,
            staged);
        if (!projection.Accepted || projection.WeightPatch is null)
        {
            error = projection.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error)?.Message ??
                "The staged body document cannot be represented by the verified writer.";
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
            request = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                loaded.SourcePluginPath,
                loaded.SourcePluginSha256,
                loaded.TargetFormId,
                proposal,
                output,
                new NpcOverridePatch(
                    null,
                    null,
                    null,
                    Weight: projection.WeightPatch));
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
        {
            Diagnostics.Add(
                $"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
        }
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

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool IsExactSourceOwnedVerification(
        NpcOverrideVerificationResult? verification) =>
        verification is
        {
            IsValid: true,
            MajorRecordCount: 1,
            NpcRecordCount: 1,
            SourceOwnedTargetCount: 1,
            SelfOwnedTargetCount: 0,
            OutputSha256: not null
        };

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

public sealed record SkyrimBodyEditChangeRowViewModel(
    string Field,
    string Before,
    string After);
