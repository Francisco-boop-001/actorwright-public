using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimFaceEditPanel : UserControl
{
    public SkyrimFaceEditPanel() => InitializeComponent();

    private void OpenFaceEditor_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not SkyrimFaceEditWorkspaceViewModel viewModel ||
            !viewModel.CanOpenEditor)
        {
            return;
        }
        SkyrimFaceEditorViewModel editor = viewModel.CreateEditor();
        var dialog = new SkyrimFaceEditorWindow(editor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) viewModel.ApplyEditor(editor);
    }
}
