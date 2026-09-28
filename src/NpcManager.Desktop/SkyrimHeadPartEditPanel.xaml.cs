using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class SkyrimHeadPartEditPanel : UserControl
{
    public SkyrimHeadPartEditPanel() => InitializeComponent();

    private void ChooseHeadPart_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not SkyrimHeadPartEditViewModel viewModel ||
            !viewModel.CanOpenPicker)
            return;
        using SkyrimHeadPartPickerViewModel picker = viewModel.CreatePicker();
        var dialog = new SkyrimHeadPartPickerWindow(picker)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) viewModel.ApplyPicker(picker);
    }
}
