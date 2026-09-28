using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace NpcManager.Desktop;

public partial class RaceMenuPresetLoaderWindow : Window, IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private bool loaded;
    private bool disposed;

    public RaceMenuPresetLoaderWindow(RaceMenuPresetLoaderViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        ContentRendered += (_, _) => FilterBox.Focus();
    }

    public RaceMenuPresetSelection? Selection { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded || DataContext is not RaceMenuPresetLoaderViewModel viewModel) return;
        loaded = true;
        try
        {
            await viewModel.LoadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing the modal owns cancellation; no caller state has changed.
        }
    }

    private void Accept_OnClick(object sender, RoutedEventArgs e) => Accept();

    private void PresetList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (DataContext is not RaceMenuPresetLoaderViewModel viewModel ||
            !viewModel.CanAccept || viewModel.AcceptSelection() is not { } selection)
            return;
        Selection = selection;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosing(CancelEventArgs e)
    {
        cancellation.Cancel();
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
        if (DataContext is IDisposable viewModel) viewModel.Dispose();
        cancellation.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }
}
