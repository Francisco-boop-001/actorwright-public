using System.Windows;
using System.Windows.Input;

namespace NpcManager.Desktop;

public partial class SkyrimHeadPartPickerWindow : Window
{
    private bool accepted;

    public SkyrimHeadPartPickerWindow(SkyrimHeadPartPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SkyrimHeadPartPickerViewModel ViewModel =>
        (SkyrimHeadPartPickerViewModel)DataContext;

    private void Accept_OnClick(object sender, RoutedEventArgs eventArgs) => Accept();

    private void Cancel_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ViewModel.Cancel();
        DialogResult = false;
    }

    private void HeadPartList_OnMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (ViewModel.SelectedRow is not null) Accept();
    }

    private void HeadPartList_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
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
