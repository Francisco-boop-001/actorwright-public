using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class NativeFaceGenBatchViewModel :
    INotifyPropertyChanged,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private readonly IFaceGenBakeAllService service;
    private readonly IReviewedGameIntakeService? intakeService;
    private readonly DelegateCommand browseDataRootCommand;
    private readonly DelegateCommand browseOutputRootCommand;
    private readonly DelegateCommand cancelCommand;
    private readonly BoundedProgressCollection progressEvents = new(
        512, "... earlier progress entries omitted ...");
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();

    public NativeFaceGenBatchViewModel(
        IFaceGenBakeAllService service,
        IReviewedGameIntakeService? intakeService = null)
    {
        this.service = service;
        this.intakeService = intakeService;
        browseDataRootCommand = new DelegateCommand(
            _ => BrowseDataRoot(), _ => !IsBusy);
        browseOutputRootCommand = new DelegateCommand(
            _ => BrowseOutputRoot(), _ => !IsBusy);
        cancelCommand = new DelegateCommand(
            _ => RequestCancellation(), _ => IsBusy && !IsCancelling);
    }

    public event EventHandler<SkyrimMainWorkspaceArtifactHandoff>?
        ArtifactCommitted
    {
        add => artifactEmitter.ArtifactCommitted += value;
        remove => artifactEmitter.ArtifactCommitted -= value;
    }

    public void BindWorkspaceIdentity(
        SkyrimMainWorkspaceIdentity identity) =>
        artifactEmitter.Bind(identity);

    public BoundedProgressCollection ProgressEvents => progressEvents;
    public ObservableCollection<string> Outcomes { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    internal event Action? ReviewedOutputConsumed;
    public ICommand BrowseDataRootCommand => browseDataRootCommand;
    public ICommand BrowseOutputRootCommand => browseOutputRootCommand;
    public ICommand CancelCommand => cancelCommand;

    private string dataRoot = string.Empty;
    public string DataRoot
    {
        get => dataRoot;
        set => SetInput(ref dataRoot, value);
    }

    private string pluginOrderText = string.Empty;
    public string PluginOrderText
    {
        get => pluginOrderText;
        set => SetInput(ref pluginOrderText, value);
    }

    private string winningPlugin = string.Empty;
    public string WinningPlugin
    {
        get => winningPlugin;
        set => SetInput(ref winningPlugin, value);
    }

    private string outputRoot = string.Empty;
    public string OutputRoot
    {
        get => outputRoot;
        set => SetInput(ref outputRoot, value);
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CloseActionContent));
            OnPropertyChanged(nameof(CloseActionName));
            OnPropertyChanged(nameof(CanUseCloseAction));
            RaiseCommands();
        }
    }

    public bool IsNotBusy => !IsBusy;

    private bool isCancelling;
    public bool IsCancelling
    {
        get => isCancelling;
        private set
        {
            if (!Set(ref isCancelling, value)) return;
            OnPropertyChanged(nameof(CloseActionContent));
            OnPropertyChanged(nameof(CloseActionName));
            OnPropertyChanged(nameof(CanUseCloseAction));
            RaiseCommands();
        }
    }

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set => Set(ref hasCompleted, value);
    }

    public bool CanStart => !IsBusy &&
                            !string.IsNullOrWhiteSpace(DataRoot) &&
                            !string.IsNullOrWhiteSpace(PluginOrderText) &&
                            !string.IsNullOrWhiteSpace(OutputRoot) &&
                            (intakeService is null || reviewedIntake is not null);

    public string CloseActionContent => IsBusy
        ? IsCancelling ? "Cancelling..." : "_Cancel"
        : "_Close";

    public string CloseActionName => IsBusy
        ? IsCancelling
            ? "Cancelling native FaceGen batch"
            : "Cancel native FaceGen batch"
        : "Close native FaceGen batch results";

    public bool CanUseCloseAction => !IsCancelling;

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private bool isProgressIndeterminate;
    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        private set => Set(ref isProgressIndeterminate, value);
    }

    private string status = "Choose a copied Data folder and explicit plugin order.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string verdict = "Not started";
    public string Verdict
    {
        get => verdict;
        private set => Set(ref verdict, value);
    }

    private int discovered;
    public int Discovered
    {
        get => discovered;
        private set => Set(ref discovered, value);
    }

    private int baked;
    public int Baked
    {
        get => baked;
        private set => Set(ref baked, value);
    }

    private int skipped;
    public int Skipped
    {
        get => skipped;
        private set => Set(ref skipped, value);
    }

    private int failed;
    public int Failed
    {
        get => failed;
        private set => Set(ref failed, value);
    }

    public string RuntimeAuthority { get; } =
        "Static files only. In-game appearance authority remains false.";

    private ReviewedGameIntake? reviewedIntake;

    internal void ApplyReviewedIntake(ReviewedGameIntake intake)
    {
        if (IsBusy) return;
        reviewedIntake = null;
        DataRoot = intake.DataRoot.Value;
        PluginOrderText = string.Join(Environment.NewLine,
            intake.Plugins.OrderBy(plugin => plugin.Order).Select(plugin => plugin.Plugin.Value));
        OutputRoot = intake.OutputRoot.Value;
        WinningPlugin = string.Empty;
        reviewedIntake = intake;
        OnPropertyChanged(nameof(CanStart));
        Status = $"Ready with reviewed workspace {intake.IntakeFingerprint.Value[..12]}...";
    }

    internal void ClearReviewedIntake()
    {
        if (IsBusy) return;
        reviewedIntake = null;
        OnPropertyChanged(nameof(CanStart));
        Status = "Review the copied workspace again before baking.";
    }

    internal async Task RunAsync()
    {
        if (IsBusy) return;
        ResetRunState();
        if (!TryBuildRequest(out FaceGenBakeAllRequest? request,
                out string? validationError))
        {
            Verdict = "Input required";
            Status = validationError;
            Diagnostics.Add($"Error: gui-facegen-input: {validationError}");
            HasCompleted = true;
            return;
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = "Checking reviewed workspace";
        try
        {
            if (!await RevalidateReviewedIntakeAsync(source.Token)) return;
            ReviewedFaceGenOutputRootLease? outputLease = null;
            if (intakeService is not null)
            {
                if (!TryPrepareReviewedOutputRoot(request.OutputDataRoot,
                        out ReviewedFaceGenOutputRootLease prepared)) return;
                outputLease = prepared;
            }
            using (outputLease)
            {
                Verdict = "Baking";
                Status = "Discovering winning NPC records...";
                var progress = new ContextProgress<FaceGenBakeAllProgress>(
                    SynchronizationContext.Current, PresentProgress);
                FaceGenBakeAllResult result = await Task.Run(async () =>
                    await service.RunAsync(request, progress, source.Token)
                        .ConfigureAwait(false)).ConfigureAwait(true);
                if (result.Status != FaceGenBakeAllStatus.Fatal)
                {
                    outputLease?.Preserve();
                    ConsumeReviewedOutput();
                }
                PresentResult(result);
                if (result.Status is
                        FaceGenBakeAllStatus.Succeeded or
                        FaceGenBakeAllStatus.SomeFailed)
                    await CommitWorkspaceArtifactAsync(
                        request.OutputDataRoot,
                        result,
                        source.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Cancelled at a safe boundary. Verified earlier pairs, if any, were retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Verdict = "Fatal";
            Status = "The batch stopped before a trustworthy result was available.";
            Diagnostics.Add($"Error: gui-facegen-exception: {exception.Message}");
        }
        finally
        {
            HasCompleted = true;
            IsProgressIndeterminate = false;
            IsBusy = false;
            IsCancelling = false;
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            source.Dispose();
        }
    }

    private async Task<bool> RevalidateReviewedIntakeAsync(CancellationToken cancellationToken)
    {
        if (intakeService is null) return true;
        if (reviewedIntake is null)
        {
            Verdict = "Workspace review required";
            Status = "Open workspace and review the copied inputs before baking.";
            Diagnostics.Add("Error: gui-facegen-intake-required: No reviewed workspace is active.");
            HasCompleted = true;
            return false;
        }

        Status = "Revalidating the reviewed workspace before the bake...";
        var original = reviewedIntake;
        ReviewedGameIntakeResult validation;
        try
        {
            validation = await Task.Run(async () => await intakeService.ReviewAsync(
                new ReviewedGameIntakeRequest(
                    original.Edition,
                    original.WorkspaceRoot,
                    original.DataRoot,
                    original.LoadOrderPath,
                    original.OutputRoot,
                    original.Plugins.Select(plugin => plugin.Plugin).ToImmutableArray()),
                cancellationToken).ConfigureAwait(false)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Verdict = "Workspace needs attention";
            Status = "The reviewed workspace could not be revalidated.";
            Diagnostics.Add($"Error: gui-facegen-intake-revalidation: {exception.Message}");
            HasCompleted = true;
            return false;
        }

        foreach (var diagnostic in validation.Diagnostics)
            Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
        if (!validation.IsAccepted || validation.Intake is null ||
            validation.Intake.IntakeFingerprint != original.IntakeFingerprint)
        {
            reviewedIntake = null;
            OnPropertyChanged(nameof(CanStart));
            Verdict = "Workspace changed";
            Status = "Copied workspace bytes changed after review. Review the workspace again.";
            Diagnostics.Add("Error: gui-facegen-intake-stale: The reviewed intake fingerprint no longer matches.");
            HasCompleted = true;
            return false;
        }

        reviewedIntake = validation.Intake;
        return true;
    }

    private bool TryPrepareReviewedOutputRoot(
        WorkspacePath requestedRoot,
        out ReviewedFaceGenOutputRootLease lease)
    {
        lease = null!;
        string error;
        if (reviewedIntake is null)
        {
            error = "No reviewed workspace is active.";
        }
        else if (ReviewedFaceGenOutputRootLease.TryCreate(
                     reviewedIntake, requestedRoot,
                     out ReviewedFaceGenOutputRootLease? created,
                     out error) && created is not null)
        {
            lease = created;
            return true;
        }
        Verdict = "Output needs attention";
        Status = "The reviewed output folder could not be created safely.";
        Diagnostics.Add($"Error: gui-facegen-output-create: {error}");
        HasCompleted = true;
        return false;
    }

    private void ConsumeReviewedOutput()
    {
        if (reviewedIntake is null) return;
        reviewedIntake = null;
        OnPropertyChanged(nameof(CanStart));
        ReviewedOutputConsumed?.Invoke();
    }

    internal void RequestCancellation()
    {
        if (!IsBusy || IsCancelling || cancellation is null) return;
        IsCancelling = true;
        Status = "Cancelling at the next safe review or NPC boundary...";
        cancellation.Cancel();
    }

    private bool TryBuildRequest(
        out FaceGenBakeAllRequest request,
        out string message)
    {
        request = default!;
        try
        {
            ImmutableArray<PluginName> plugins = PluginOrderText.Split(
                    [',', ';', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(item => new PluginName(item))
                .ToImmutableArray();
            if (plugins.IsDefaultOrEmpty)
            {
                message = "Enter at least one plugin in ascending load order.";
                return false;
            }
            PluginName? winner = string.IsNullOrWhiteSpace(WinningPlugin)
                ? null
                : new PluginName(WinningPlugin.Trim());
            request = new FaceGenBakeAllRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(DataRoot),
                plugins,
                new WorkspacePath(OutputRoot),
                winner);
            message = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }
    }

    private void PresentProgress(FaceGenBakeAllProgress item)
    {
        IsProgressIndeterminate = item.Total <= 0 &&
                                  item.Phase == FaceGenBakeAllProgressPhase.Discovering;
        Status = item.Message;
        ProgressPercent = item.Total > 0
            ? Math.Clamp((int)Math.Round(
                item.Completed * 100D / item.Total), 0, 100)
            : item.Phase == FaceGenBakeAllProgressPhase.Completed ? 100 : 0;
        progressEvents.Add(
            $"{item.Sequence:000}  [{item.Completed}/{item.Total}]  {item.Phase}  {item.Message}");
    }

    private void PresentResult(FaceGenBakeAllResult result)
    {
        Discovered = result.Discovered;
        Baked = result.Baked;
        Skipped = result.Skipped;
        Failed = result.Failed;
        ProgressPercent = result.Status == FaceGenBakeAllStatus.Succeeded
            ? 100
            : ProgressPercent;
        Verdict = result.Status switch
        {
            FaceGenBakeAllStatus.Succeeded => "Static pass",
            FaceGenBakeAllStatus.SomeFailed => "Completed with failures",
            FaceGenBakeAllStatus.Cancelled => "Cancelled",
            _ => "Fatal refusal"
        };
        Status = result.Status switch
        {
            FaceGenBakeAllStatus.Succeeded =>
                "Every admitted NPC produced or already had a complete canonical pair.",
            FaceGenBakeAllStatus.SomeFailed =>
                "Some NPCs failed. Verified earlier pairs were retained; inspect the log.",
            FaceGenBakeAllStatus.Cancelled =>
                "Cancelled between NPCs. Verified earlier pairs were retained.",
            _ => "Preflight refused the batch before a trustworthy run."
        };
        foreach (FaceGenNpcBakeResult outcome in result.Outcomes)
        {
            Outcomes.Add(
                $"{outcome.Status,-7}  {outcome.Target.OriginatingPlugin.Value}|{outcome.Target.FormId}" +
                (outcome.Artifact is null
                    ? string.Empty
                    : $"  NIF {outcome.Artifact.FaceGeomNif.Value}  DDS {outcome.Artifact.FaceTintDds.Value}"));
        }
        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            Diagnostics.Add(
                $"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private async Task CommitWorkspaceArtifactAsync(
        WorkspacePath outputRoot,
        FaceGenBakeAllResult result,
        CancellationToken cancellationToken)
    {
        SkyrimMainWorkspaceIdentity? identity =
            artifactEmitter.BoundIdentity;
        if (identity is null)
            return;
        FaceGenNpcBakeResult? outcome = result.Outcomes
            .FirstOrDefault(item =>
                item.Target.FormId == identity.FormId &&
                item.Target.OriginatingPlugin ==
                    identity.OwnerPlugin &&
                item.Artifact is not null &&
                item.Status != FaceGenNpcBakeStatus.Failed);
        if (outcome?.Artifact is not { } artifact)
            return;
        string path = Path.Combine(
            outputRoot.Value,
            "npcmanager-facegen-batch-manifest.json");
        if (File.Exists(path))
            throw new IOException(
                "The retained FaceGen handoff manifest already exists.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            artifactKind = "native-facegen-batch-handoff",
            ownerPlugin = identity.OwnerPlugin.Value,
            winningProvider = identity.WinningProvider.Value,
            formId = identity.FormId.ToString(),
            faceGeom = artifact.FaceGeomNif.Value,
            faceGeomSha256 = artifact.FaceGeomSha256.Value,
            faceTint = artifact.FaceTintDds.Value,
            faceTintSha256 = artifact.FaceTintSha256.Value,
            runtimeAuthority = false
        });
        await File.WriteAllBytesAsync(
            path,
            bytes,
            cancellationToken);
        var observed = new Sha256Hash(
            Convert.ToHexString(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        path,
                        cancellationToken))));
        artifactEmitter.Commit(
            this,
            "facegen-batch",
            new WorkspacePath(path),
            observed,
            identity.FormId);
    }

    private void ResetRunState()
    {
        progressEvents.Clear();
        Outcomes.Clear();
        Diagnostics.Clear();
        Discovered = Baked = Skipped = Failed = 0;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        HasCompleted = false;
        IsCancelling = false;
    }

    private void BrowseDataRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Choose a copied Skyrim Data folder"
        };
        if (dialog.ShowDialog() == true) DataRoot = dialog.FolderName;
    }

    private void BrowseOutputRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "Choose an empty K-local output Data folder"
        };
        if (dialog.ShowDialog() == true) OutputRoot = dialog.FolderName;
    }

    private void SetInput(ref string field, string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName)) return;
        if (reviewedIntake is not null)
        {
            reviewedIntake = null;
            Status = "Review handoff changed. Return to Open workspace and review again.";
        }
        OnPropertyChanged(nameof(CanStart));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        browseDataRootCommand.RaiseCanExecuteChanged();
        browseOutputRootCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        CancellationTokenSource? source = cancellation;
        cancellation = null;
        if (source is null) return;
        source.Cancel();
        source.Dispose();
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
