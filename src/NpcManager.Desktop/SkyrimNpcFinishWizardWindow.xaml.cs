using System.Windows;

namespace NpcManager.Desktop;

public partial class SkyrimNpcFinishWizardWindow : Window
{
    public SkyrimNpcFinishWizardWindow(SkyrimNpcFinishWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
