using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class RaceMenuNpcBuildPanel : UserControl
{
    public RaceMenuNpcBuildPanel() => InitializeComponent();

    private void RunButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RaceMenuNpcBuildViewModel viewModel || !viewModel.CanRun)
            return;

        var dialog = new RaceMenuNpcBuildProgressWindow(viewModel)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private async void ChoosePreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RaceMenuNpcBuildViewModel viewModel ||
            viewModel.CreatePresetLoader() is not { } loader)
            return;

        using var dialog = new RaceMenuPresetLoaderWindow(loader)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && dialog.Selection is { } selection)
            await viewModel.ApplyPresetSelectionAsync(selection);
    }
}
