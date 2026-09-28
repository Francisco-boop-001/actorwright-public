using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Owns one reviewed opening options artifact, one immutable modal result, and
/// one exact-hash proposal/apply transaction. The modal itself performs no I/O.
/// </summary>
public sealed class SkyrimCharGenOptionsProductionWorkspaceViewModel :
    NotifyViewModel, IDisposable
{
    private const string EmptyResult = "Not available";
    private readonly IFaceGenOptionsService optionsReader;
    private readonly ISkyrimCharGenOptionsProductionService productionService;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand applyCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private FaceGenOptionsResult? loadedSource;
    private CharGenOptions? acceptedOptions;
    private SkyrimCharGenOptionsProductionReviewResult? reviewed;

    public SkyrimCharGenOptionsProductionWorkspaceViewModel(
        IFaceGenOptionsService optionsReader,
        ISkyrimCharGenOptionsProductionService productionService)
    {
        this.optionsReader = optionsReader ??
            throw new ArgumentNullException(nameof(optionsReader));
        this.productionService = productionService ??
            throw new ArgumentNullException(nameof(productionService));
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(
            ReviewAsync,
            () => HasAcceptedOptions && !IsBusy);
        applyCommand = new AsyncCommand(
            ApplyAsync,
            () => IsReviewed && !HasCompleted && !IsBusy);
        cancelCommand = new DelegateCommand(
            _ => cancellation?.Cancel(),
            _ => IsBusy);
    }

    public ObservableCollection<string> Diagnostics { get; } = [];
    public IReadOnlyList<NpcSex> SexOptions { get; } =
        [NpcSex.Female, NpcSex.Male];
    public ICommand LoadCommand => loadCommand;
    public ICommand ReviewCommand => reviewCommand;
    public ICommand ApplyCommand => applyCommand;
    public ICommand CancelCommand => cancelCommand;
    public bool IsLoaded => loadedSource?.Options is not null;
    public bool CanOpenEditor => IsLoaded && !IsBusy;
    public bool HasAcceptedOptions => acceptedOptions is not null;
    public bool IsNotBusy => !IsBusy;
    public string SourceSummary => loadedSource is null
        ? "No exact opening options artifact is reviewed."
        : $"Skyrim SE schema {loadedSource.Options?.SchemaVersion} · " +
          $"SHA-256 {loadedSource.InputSha256?.Value ?? EmptyResult}";
    public string DocumentSummary => acceptedOptions is null
        ? "No complete modal document is accepted."
        : $"{acceptedOptions.DiffuseResolution} / " +
          $"{acceptedOptions.DiffuseCompression} · overlays " +
          $"{(acceptedOptions.BakeSseRaceMenuOverlays ? "on" : "off")} · " +
          $"{acceptedOptions.TintSort.TintRules.Length} tint rule(s)";

    private string sourceOptions = string.Empty;
    public string SourceOptions
    {
        get => sourceOptions;
        set
        {
            if (!Set(ref sourceOptions, value ?? string.Empty)) return;
            ClearLoadedSource();
        }
    }

    private string sourceOptionsSha256 = string.Empty;
    public string SourceOptionsSha256
    {
        get => sourceOptionsSha256;
        set
        {
            if (!Set(ref sourceOptionsSha256, value ?? string.Empty)) return;
            ClearLoadedSource();
        }
    }

    private string dataRoot = string.Empty;
    public string DataRoot
    {
        get => dataRoot;
        set
        {
            if (Set(ref dataRoot, value ?? string.Empty)) InvalidateReview();
        }
    }

    private string pluginOrder = string.Empty;
    public string PluginOrder
    {
        get => pluginOrder;
        set
        {
            if (Set(ref pluginOrder, value ?? string.Empty)) InvalidateReview();
        }
    }

    private string npcReference = string.Empty;
    public string NpcReference
    {
        get => npcReference;
        set
        {
            if (Set(ref npcReference, value ?? string.Empty)) InvalidateReview();
        }
    }

    private string expectedRace = string.Empty;
    public string ExpectedRace
    {
        get => expectedRace;
        set
        {
            if (Set(ref expectedRace, value ?? string.Empty)) InvalidateReview();
        }
    }

    private NpcSex expectedSex = NpcSex.Female;
    public NpcSex ExpectedSex
    {
        get => expectedSex;
        set
        {
            if (Set(ref expectedSex, value)) InvalidateReview();
        }
    }

    private string proposalPath = string.Empty;
    public string ProposalPath
    {
        get => proposalPath;
        set
        {
            if (Set(ref proposalPath, value ?? string.Empty)) InvalidateReview();
        }
    }

    private string outputRoot = string.Empty;
    public string OutputRoot
    {
        get => outputRoot;
        set
        {
            if (Set(ref outputRoot, value ?? string.Empty)) InvalidateReview();
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

    private bool isReviewed;
    public bool IsReviewed
    {
        get => isReviewed;
        private set
        {
            if (Set(ref isReviewed, value)) RaiseCommandState();
        }
    }

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set
        {
            if (Set(ref hasCompleted, value)) RaiseCommandState();
        }
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private string status =
        "Review one exact K-local Skyrim CharGen options artifact.";
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

    private string optionsArtifact = EmptyResult;
    public string OptionsArtifact
    {
        get => optionsArtifact;
        private set => Set(ref optionsArtifact, value);
    }

    private string optionsSha256 = EmptyResult;
    public string OptionsSha256
    {
        get => optionsSha256;
        private set => Set(ref optionsSha256, value);
    }

    private string faceTintArtifact = EmptyResult;
    public string FaceTintArtifact
    {
        get => faceTintArtifact;
        private set => Set(ref faceTintArtifact, value);
    }

    private string faceTintSha256 = EmptyResult;
    public string FaceTintSha256
    {
        get => faceTintSha256;
        private set => Set(ref faceTintSha256, value);
    }

    private string receiptSha256 = EmptyResult;
    public string ReceiptSha256
    {
        get => receiptSha256;
        private set => Set(ref receiptSha256, value);
    }

    public async Task LoadAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildSourceRequest(out FaceGenOptionsRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }

        CancellationTokenSource source = Begin(
            "Reviewing source",
            "Hash-reviewing the exact opening Skyrim options artifact.");
        try
        {
            FaceGenOptionsResult result = await optionsReader.ValidateAsync(
                request!, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.IsValid || result.Options is null ||
                result.InputSha256 is null)
            {
                Verdict = "Source refused";
                Status = "No editable CharGen transaction was created.";
                return;
            }

            loadedSource = result;
            acceptedOptions = null;
            InvalidateReview();
            Verdict = "Source reviewed";
            Status =
                "Exact opening options accepted. Open the editor; it performs no file I/O.";
            RaiseDocumentState();
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Source review cancelled; no transaction was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add(
                $"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Source review failed";
            Status = "No editable CharGen transaction was retained.";
        }
        finally
        {
            Release(source);
        }
    }

    public SkyrimCharGenOptionsEditorViewModel CreateEditor()
    {
        if (!CanOpenEditor || loadedSource?.Options is not { } opening)
            throw new InvalidOperationException(
                "Review one exact opening options artifact before editing.");
        return new SkyrimCharGenOptionsEditorViewModel(
            acceptedOptions ?? opening);
    }

    public bool TryCommitEditor(SkyrimCharGenOptionsEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.IsAccepted || editor.AcceptedOptions is null) return false;
        acceptedOptions = editor.AcceptedOptions;
        InvalidateReview();
        Verdict = "Options accepted";
        Status =
            "One complete in-memory document is accepted. Review writes only the proposal.";
        RaiseDocumentState();
        return true;
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildReviewRequest(
                out SkyrimCharGenOptionsProductionReviewRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }

        CancellationTokenSource source = Begin(
            "Reviewing proposal",
            "Validating native consumption and writing proposal JSON only.");
        try
        {
            SkyrimCharGenOptionsProductionReviewResult result =
                await productionService.ReviewAsync(request!, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Reviewed || result.ProposalPath is null ||
                result.ProposalSha256 is null || result.Proposal is null)
            {
                Verdict = "Proposal refused";
                Status = "No production output root was created.";
                return;
            }

            reviewed = result;
            IsReviewed = true;
            ProposalSha256 = result.ProposalSha256.Value.Value;
            ProgressPercent = 45;
            Verdict = "Proposal reviewed";
            Status =
                "Proposal JSON retained; accepted options and FaceTint outputs do not exist yet.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Proposal review cancelled; no Apply transaction ran.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add(
                $"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Proposal review failed";
            Status = "No Apply transaction ran.";
        }
        finally
        {
            Release(source);
        }
    }

    public async Task ApplyAsync()
    {
        Diagnostics.Clear();
        if (reviewed?.ProposalPath is not { } path ||
            reviewed.ProposalSha256 is not { } hash)
        {
            PresentInputError(
                "Review the exact proposal before applying it.");
            return;
        }

        CancellationTokenSource source = Begin(
            "Writing and reopening",
            "Persisting canonical options, running native FaceTint, and reopening evidence.");
        try
        {
            SkyrimCharGenOptionsProductionApplyResult result =
                await productionService.ApplyAsync(
                    new SkyrimCharGenOptionsProductionApplyRequest(path, hash),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Applied || result.OptionsWrite is null ||
                !result.OptionsWrite.Written ||
                !result.OptionsWrite.ReadbackVerified ||
                result.OptionsWrite.OutputSha256 is null ||
                result.FaceTintBake is null ||
                !result.FaceTintBake.Written ||
                result.FaceTintBake.ReceiptSha256 is null)
            {
                Verdict = "Apply refused";
                Status =
                    "The fresh owned output root was not accepted; inspect diagnostics.";
                return;
            }

            OptionsArtifact = result.OptionsWrite.Output.Value;
            OptionsSha256 = result.OptionsWrite.OutputSha256.Value.Value;
            ReceiptSha256 = result.FaceTintBake.ReceiptSha256.Value.Value;
            if (result.FaceTintBake.Receipt?.NativeFaceTint is { } artifact)
            {
                FaceTintArtifact = reviewed.Proposal is { } acceptedProposal
                    ? Path.Combine(acceptedProposal.OutputRoot, "facetint.dds")
                    : EmptyResult;
                FaceTintSha256 = artifact.OutputSha256.Value;
            }
            else if (reviewed.Proposal is { } proposal)
            {
                FaceTintArtifact = Path.Combine(
                    proposal.OutputRoot, "facetint.dds");
            }
            HasCompleted = true;
            ProgressPercent = 100;
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status =
                "Canonical options and native FaceTint were retained and reopened. Skyrim runtime rendering is not proven.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status =
                "Apply was cancelled; the production service owns rollback of its fresh root.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add(
                $"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Apply failed";
            Status = "No production success is claimed.";
        }
        finally
        {
            Release(source);
        }
    }

    private bool CanLoad() => !IsBusy &&
                              !string.IsNullOrWhiteSpace(SourceOptions) &&
                              !string.IsNullOrWhiteSpace(SourceOptionsSha256);

    private bool TryBuildSourceRequest(
        out FaceGenOptionsRequest? request,
        out string error)
    {
        request = null;
        error = string.Empty;
        try
        {
            var path = new WorkspacePath(SourceOptions);
            var hash = new Sha256Hash(SourceOptionsSha256);
            request = new FaceGenOptionsRequest(
                GameEdition.SkyrimSpecialEdition,
                path,
                null,
                hash,
                false);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TryBuildReviewRequest(
        out SkyrimCharGenOptionsProductionReviewRequest? request,
        out string error)
    {
        request = null;
        error = string.Empty;
        if (loadedSource?.Options is null ||
            loadedSource.InputSha256 is not { } sourceHash ||
            acceptedOptions is null)
        {
            error =
                "Review a source artifact and accept one complete modal document first.";
            return false;
        }

        try
        {
            ImmutableArray<PluginName> plugins = PluginOrder
                .Split(['\r', '\n', ',', ';'],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(value => new PluginName(value))
                .ToImmutableArray();
            if (plugins.IsDefaultOrEmpty)
            {
                error = "Enter the copied plugin order, one plugin per line.";
                return false;
            }
            if (!FormReference.TryParse(NpcReference, out FormReference npc))
            {
                error = "NPC must use Plugin.ext|0x00000000.";
                return false;
            }
            if (!FormReference.TryParse(ExpectedRace,
                    out FormReference race))
            {
                error = "Race must use Plugin.ext|0x00000000.";
                return false;
            }
            request = new SkyrimCharGenOptionsProductionReviewRequest(
                loadedSource.Input,
                sourceHash,
                acceptedOptions,
                new WorkspacePath(DataRoot),
                plugins,
                npc,
                ExpectedSex,
                race,
                new WorkspacePath(OutputRoot),
                new WorkspacePath(ProposalPath));
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void ClearLoadedSource()
    {
        loadedSource = null;
        acceptedOptions = null;
        InvalidateReview();
        Verdict = "Not loaded";
        Status = "Review one exact K-local Skyrim CharGen options artifact.";
        RaiseDocumentState();
    }

    private void InvalidateReview()
    {
        reviewed = null;
        IsReviewed = false;
        HasCompleted = false;
        ProgressPercent = 0;
        ProposalSha256 = EmptyResult;
        OptionsArtifact = EmptyResult;
        OptionsSha256 = EmptyResult;
        FaceTintArtifact = EmptyResult;
        FaceTintSha256 = EmptyResult;
        ReceiptSha256 = EmptyResult;
        RaiseCommandState();
    }

    private void RaiseDocumentState()
    {
        Raise(nameof(IsLoaded));
        Raise(nameof(CanOpenEditor));
        Raise(nameof(HasAcceptedOptions));
        Raise(nameof(SourceSummary));
        Raise(nameof(DocumentSummary));
        RaiseCommandState();
    }

    private CancellationTokenSource Begin(
        string verdictValue,
        string statusValue)
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
            Diagnostics.Add(
                $"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
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
        applyCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private sealed class DelegateCommand(
        Action<object?> execute,
        Predicate<object?> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute(parameter);
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
