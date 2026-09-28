using System.Windows;

namespace NpcManager.Desktop;

public partial class SkyrimArmorAddonReferenceEditorWindow : Window
{
    private bool accepted;

    public SkyrimArmorAddonReferenceEditorWindow(
        SkyrimArmorAddonReferenceEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimArmorAddonReferenceEditorViewModel ViewModel =>
        (SkyrimArmorAddonReferenceEditorViewModel)DataContext;

    private void Choose_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryChoose();

    private void Edit_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TryDeepEdit()) return;
        accepted = true;
        DialogResult = true;
    }

    private void Use_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TryUse()) return;
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
