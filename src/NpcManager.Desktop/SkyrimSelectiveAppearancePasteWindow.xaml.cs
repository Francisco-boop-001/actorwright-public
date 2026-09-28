using System.Windows;

namespace NpcManager.Desktop;

public partial class SkyrimSelectiveAppearancePasteWindow : Window
{
    private bool accepted;

    public SkyrimSelectiveAppearancePasteWindow(
        SkyrimSelectiveAppearancePasteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimSelectiveAppearancePasteViewModel ViewModel =>
        (SkyrimSelectiveAppearancePasteViewModel)DataContext;

    private void SelectAll_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.SelectAll();

    private void DeselectAll_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.DeselectAll();

    private void Paste_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TryAccept()) return;
        accepted = true;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ViewModel.Cancel();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!accepted) ViewModel.Cancel();
        base.OnClosed(e);
    }
}
