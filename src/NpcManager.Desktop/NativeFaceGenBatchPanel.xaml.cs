using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class NativeFaceGenBatchPanel : UserControl
{
    public NativeFaceGenBatchPanel() => InitializeComponent();

    private void StartBatchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NativeFaceGenBatchViewModel viewModel ||
            !viewModel.CanStart)
            return;
        var dialog = new NativeFaceGenBatchProgressWindow(viewModel)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
