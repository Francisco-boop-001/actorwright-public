using System.Windows;

namespace NpcManager.Desktop;

public partial class SkyrimLeveledListEditorWindow : Window
{
    private bool accepted;

    public SkyrimLeveledListEditorWindow(
        SkyrimLeveledListEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimLeveledListEditorViewModel ViewModel =>
        (SkyrimLeveledListEditorViewModel)DataContext;

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
