using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class AnimationPickerWindow : Window, IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private bool loaded;
    private bool accepted;
    private bool disposed;

    public AnimationPickerWindow(AnimationPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        ContentRendered += (_, _) => FilterBox.Focus();
    }

    public PreviewAnimationSelection? Selection { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded || DataContext is not AnimationPickerViewModel viewModel) return;
        loaded = true;
        try
        {
            await viewModel.LoadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing the modal owns cancellation; caller state is unchanged.
        }
    }

    private void AnimationTree_OnSelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is AnimationPickerViewModel viewModel)
            viewModel.Select(e.NewValue as AnimationTreeItemViewModel);
    }

    private void AnimationTree_OnMouseDoubleClick(
        object sender,
        MouseButtonEventArgs e) => Accept();

    private void AnimationTree_OnPreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        bool wasAccepted = Accept();
        e.Handled = wasAccepted;
    }

    private void Accept_OnClick(object sender, RoutedEventArgs e) => Accept();

    private bool Accept()
    {
        if (DataContext is not AnimationPickerViewModel viewModel ||
            !viewModel.CanAccept ||
            viewModel.AcceptSelection() is not { } selection)
        {
            return false;
        }

        Selection = selection;
        accepted = true;
        DialogResult = true;
        return true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    protected override void OnClosing(CancelEventArgs e)
    {
        cancellation.Cancel();
        if (!accepted && DataContext is AnimationPickerViewModel viewModel)
            viewModel.CancelPendingSelection();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (disposed) return;
        cancellation.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
