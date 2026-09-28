using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class DesktopWorkflowReviewViewModel :
    INotifyPropertyChanged
{
    private readonly IDesktopWorkflowReviewService service;
    private DesktopWorkflowLaunchBinding? binding;
    private DesktopWorkflowReviewSnapshot? snapshot;
    private bool isBusy;
    private string receiptOutput = string.Empty;
    private string? reviewerNote;
    private string status = "No workflow bundle selected.";
    private string receiptOutcome = "No review decision has been recorded.";
    private ImageSource? previewImage;

    public DesktopWorkflowReviewViewModel(
        IDesktopWorkflowReviewService service)
    {
        this.service = service ??
            throw new ArgumentNullException(nameof(service));
        AcceptCommand = new AsyncCommand(
            () => CreateReceiptAsync(ReviewOutcome.Accepted), CanCreateReceipt);
        RejectCommand = new AsyncCommand(
            () => CreateReceiptAsync(ReviewOutcome.Rejected), CanCreateReceipt);
        RequestRevisionCommand = new AsyncCommand(
            () => CreateReceiptAsync(ReviewOutcome.RevisionRequested),
            CanCreateReceipt);
    }

    public DesktopWorkflowReviewSnapshot? Snapshot
    {
        get => snapshot;
        private set
        {
            if (ReferenceEquals(snapshot, value))
                return;
            snapshot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(Artifacts));
            OnPropertyChanged(nameof(Authority));
            OnPropertyChanged(nameof(Diagnostics));
            OnPropertyChanged(nameof(NextActions));
            OnPropertyChanged(nameof(ProposalChanges));
            OnPropertyChanged(nameof(PreviewViews));
            RaiseCommands();
        }
    }

    public bool IsLoaded => Snapshot is not null;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (isBusy == value)
                return;
            isBusy = value;
            OnPropertyChanged();
            RaiseCommands();
        }
    }

    public ImmutableArray<DesktopWorkflowArtifactPresentation> Artifacts =>
        Snapshot?.Artifacts ?? [];

    public ImmutableArray<WorkflowAuthorityEvidence> Authority =>
        Snapshot?.Authority ?? [];

    public ImmutableArray<ProtocolDiagnostic> Diagnostics =>
        Snapshot?.Diagnostics ?? [];

    public ImmutableArray<ProtocolNextAction> NextActions =>
        Snapshot?.NextActions ?? [];

    public ImmutableArray<string> ProposalChanges =>
        Snapshot?.Proposal.Changes ?? [];

    public ImmutableArray<DesktopWorkflowPreviewViewPresentation> PreviewViews =>
        Snapshot?.Preview.Views ?? [];

    public ImageSource? PreviewImage
    {
        get => previewImage;
        private set
        {
            if (ReferenceEquals(previewImage, value))
                return;
            previewImage = value;
            OnPropertyChanged();
        }
    }

    public string ReceiptOutput
    {
        get => receiptOutput;
        set
        {
            if (string.Equals(receiptOutput, value, StringComparison.Ordinal))
                return;
            receiptOutput = value;
            OnPropertyChanged();
            RaiseCommands();
        }
    }

    public string? ReviewerNote
    {
        get => reviewerNote;
        set
        {
            if (string.Equals(reviewerNote, value, StringComparison.Ordinal))
                return;
            reviewerNote = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => status;
        private set
        {
            if (string.Equals(status, value, StringComparison.Ordinal))
                return;
            status = value;
            OnPropertyChanged();
        }
    }

    public string ReceiptOutcome
    {
        get => receiptOutcome;
        private set
        {
            if (string.Equals(receiptOutcome, value, StringComparison.Ordinal))
                return;
            receiptOutcome = value;
            OnPropertyChanged();
        }
    }

    public ICommand AcceptCommand { get; }

    public ICommand RejectCommand { get; }

    public ICommand RequestRevisionCommand { get; }

    public async Task LoadAsync(
        DesktopWorkflowLaunchBinding launchBinding,
        CancellationToken cancellationToken = default)
    {
        binding = launchBinding ??
            throw new ArgumentNullException(nameof(launchBinding));
        IsBusy = true;
        Status = "Validating the exact workflow review bundle…";
        try
        {
            DesktopWorkflowReviewLoadResult result = await service.LoadAsync(
                launchBinding, cancellationToken);
            Snapshot = result.Snapshot;
            try
            {
                PreviewImage = result.Snapshot is null
                    ? null
                    : OnLoadImageSource.Load(
                        result.Snapshot.Preview.ContactSheetBytes);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or
                    NotSupportedException or FormatException)
            {
                Snapshot = null;
                PreviewImage = null;
                Status =
                    $"Verified preview bytes could not be decoded: {exception.Message}";
                return;
            }
            Status = result.Loaded && result.Snapshot is not null
                ? $"Review ready for {result.Snapshot.NpcEditorId}; runtime and promotion remain separate."
                : Describe(result.Diagnostics, "Workflow review was refused.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void PresentStartupError(string message)
    {
        Snapshot = null;
        PreviewImage = null;
        Status = message;
    }

    private bool CanCreateReceipt() =>
        !IsBusy && binding is not null && Snapshot is not null &&
        !string.IsNullOrWhiteSpace(ReceiptOutput);

    private async Task CreateReceiptAsync(ReviewOutcome outcome)
    {
        if (!CanCreateReceipt() || binding is null)
            return;
        WorkspacePath output;
        try
        {
            output = new WorkspacePath(ReceiptOutput);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                NotSupportedException)
        {
            Status = exception.Message;
            return;
        }

        IsBusy = true;
        Status = "Revalidating exact review evidence before receipt creation…";
        try
        {
            DesktopWorkflowReviewReceiptResult result =
                await service.CreateReceiptAsync(
                    binding, outcome, ReviewerNote, output,
                    CancellationToken.None);
            if (!result.Created)
            {
                Status = Describe(
                    result.Diagnostics, "Review receipt was refused.");
                return;
            }
            ReceiptOutcome =
                $"{result.Outcome}: operator-attested receipt {result.Sha256}.";
            Status =
                "Receipt created. Human visual acceptance, game runtime verification, and promotion approval remain Required.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommands()
    {
        ((AsyncCommand)AcceptCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)RejectCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)RequestRevisionCommand).RaiseCanExecuteChanged();
    }

    private static string Describe(
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        string fallback) => diagnostics.IsDefaultOrEmpty
            ? fallback
            : string.Join(" ", diagnostics.Select(item =>
                item.Recovery is null
                    ? $"{item.Code}: {item.Message}"
                    : $"{item.Code}: {item.Message} Recovery: {item.Recovery.Constraint}"));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(
        Func<Task> execute,
        Func<bool> canExecute) : ICommand
    {
        private bool executing;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            !executing && canExecute();

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;
            executing = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            finally
            {
                executing = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
