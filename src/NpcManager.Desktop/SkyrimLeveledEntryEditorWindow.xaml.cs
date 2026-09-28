using System.Windows;

namespace NpcManager.Desktop;

public partial class SkyrimLeveledEntryEditorWindow : Window
{
    private bool accepted;

    public SkyrimLeveledEntryEditorWindow(
        SkyrimLeveledEntryEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimLeveledEntryEditorViewModel ViewModel =>
        (SkyrimLeveledEntryEditorViewModel)DataContext;

    private void Accept_OnClick(object sender, RoutedEventArgs eventArgs)
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
