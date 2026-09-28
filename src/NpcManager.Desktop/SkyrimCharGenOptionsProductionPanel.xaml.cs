using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimCharGenOptionsProductionPanel : UserControl
{
    public SkyrimCharGenOptionsProductionPanel()
    {
        InitializeComponent();
    }

    private void OpenOptionsEditor_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is not
                SkyrimCharGenOptionsProductionWorkspaceViewModel workspace ||
            !workspace.CanOpenEditor)
            return;
        var editor = workspace.CreateEditor();
        var dialog = new SkyrimCharGenOptionsEditorWindow(editor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            workspace.TryCommitEditor(editor);
    }
}
