using System.Windows;

namespace NpcManager.Desktop;

public partial class ExistingNpcStatsWindow : Window
{
    public ExistingNpcStatsWindow(ExistingNpcStatsEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private ExistingNpcStatsEditorViewModel ViewModel =>
        (ExistingNpcStatsEditorViewModel)DataContext;

    private void Accept_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.TryAccept()) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) =>
        DialogResult = false;
}
