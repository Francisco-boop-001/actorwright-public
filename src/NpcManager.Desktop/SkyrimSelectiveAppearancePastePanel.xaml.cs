using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimSelectiveAppearancePastePanel : UserControl
{
    public SkyrimSelectiveAppearancePastePanel() => InitializeComponent();

    private void OpenSelector_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not SkyrimSelectiveAppearancePasteWorkspaceViewModel viewModel ||
            !viewModel.CanChooseCategories)
        {
            return;
        }
        SkyrimSelectiveAppearancePasteViewModel selector = viewModel.CreateSelector();
        var dialog = new SkyrimSelectiveAppearancePasteWindow(selector)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) viewModel.ApplySelector(selector);
    }
}
