using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimLeveledListProductionPanel : UserControl
{
    public SkyrimLeveledListProductionPanel()
    {
        InitializeComponent();
    }

    private void OpenEditors_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SkyrimLeveledListProductionWorkspaceViewModel workspace ||
            !workspace.CanOpenEditor) return;
        SkyrimLeveledListProductionDialogCoordinator.TryAuthor(
            Window.GetWindow(this), workspace);
    }

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SkyrimLeveledListProductionWorkspaceViewModel workspace ||
            !workspace.CanAddEntry) return;
        SkyrimLeveledListProductionDialogCoordinator.TryAddEntry(
            Window.GetWindow(this), workspace);
    }

    private void EditEntry_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SkyrimLeveledListProductionWorkspaceViewModel workspace ||
            !workspace.CanEditEntry) return;
        SkyrimLeveledListProductionDialogCoordinator.TryEditEntry(
            Window.GetWindow(this), workspace);
    }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SkyrimLeveledListProductionWorkspaceViewModel workspace &&
            workspace.CanRemoveEntry)
            workspace.RemoveSelectedEntry();
    }
}
