using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimBodyEditPanel : UserControl
{
    public SkyrimBodyEditPanel() => InitializeComponent();

    private void OpenBodyEditor_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not SkyrimBodyEditWorkspaceViewModel viewModel ||
            !viewModel.CanOpenEditor)
        {
            return;
        }
        SkyrimBodyEditorViewModel editor = viewModel.CreateEditor();
        var dialog = new SkyrimBodyEditorWindow(editor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) viewModel.ApplyEditor(editor);
    }
}
