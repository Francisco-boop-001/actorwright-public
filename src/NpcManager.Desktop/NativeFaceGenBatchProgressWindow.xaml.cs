using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace NpcManager.Desktop;

public partial class NativeFaceGenBatchProgressWindow : Window
{
    private bool workStarted;
    private bool allowClose;
    private DispatcherOperation? pendingScroll;
    private readonly NativeFaceGenBatchViewModel viewModel;

    public NativeFaceGenBatchProgressWindow(
        NativeFaceGenBatchViewModel viewModel)
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.ProgressEvents.CollectionChanged += ProgressEventsChanged;
        viewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (workStarted) return;
        workStarted = true;
        if (DataContext is NativeFaceGenBatchViewModel viewModel)
            await viewModel.RunAsync();
    }

    private void CancelOrCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NativeFaceGenBatchViewModel viewModel) return;
        if (viewModel.IsBusy)
        {
            viewModel.RequestCancellation();
            return;
        }
        allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose &&
            DataContext is NativeFaceGenBatchViewModel { IsBusy: true } viewModel)
        {
            e.Cancel = true;
            viewModel.RequestCancellation();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.ProgressEvents.CollectionChanged -= ProgressEventsChanged;
        viewModel.PropertyChanged -= ViewModelPropertyChanged;
        pendingScroll?.Abort();
        pendingScroll = null;
        base.OnClosed(e);
    }

    private void ProgressEventsChanged(object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add ||
            pendingScroll is { Status: DispatcherOperationStatus.Pending })
            return;
        pendingScroll = Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () =>
            {
                pendingScroll = null;
                if (ProgressLog.Items.Count > 0)
                    ProgressLog.ScrollIntoView(ProgressLog.Items[^1]);
            });
    }

    private void ViewModelPropertyChanged(object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NativeFaceGenBatchViewModel.IsBusy) ||
            viewModel.IsBusy || !workStarted)
            return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input,
            CloseActionButton.Focus);
    }
}
