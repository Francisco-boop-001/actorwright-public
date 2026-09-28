using System.Windows;
using System.Windows.Input;

namespace NpcManager.Desktop;

public partial class SkyrimMeshPickerWindow : Window
{
    private bool accepted;

    public SkyrimMeshPickerWindow(SkyrimMeshPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ??
            throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimMeshPickerViewModel ViewModel =>
        (SkyrimMeshPickerViewModel)DataContext;

    private void Accept_OnClick(
        object sender,
        RoutedEventArgs eventArgs) => Accept();

    private void Cancel_OnClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        ViewModel.Cancel();
        DialogResult = false;
    }

    private void MeshList_OnMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (ViewModel.SelectedRow is not null) Accept();
    }

    private void MeshList_OnPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || ViewModel.SelectedRow is null) return;
        eventArgs.Handled = true;
        Accept();
    }

    private void Accept()
    {
        if (!ViewModel.TryAccept()) return;
        accepted = true;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!accepted) ViewModel.Cancel();
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
