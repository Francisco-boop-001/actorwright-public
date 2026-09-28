using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimArmorProductionPanel : UserControl
{
    public SkyrimArmorProductionPanel()
    {
        InitializeComponent();
    }

    private void OpenArmorEditor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SkyrimArmorProductionWorkspaceViewModel workspace ||
            !workspace.CanOpenEditor) return;
        SkyrimArmorProductionDialogCoordinator.TryAuthor(
            Window.GetWindow(this),
            workspace);
    }
}
