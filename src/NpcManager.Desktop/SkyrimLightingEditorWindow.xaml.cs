using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace NpcManager.Desktop;

public partial class SkyrimLightingEditorWindow : Window
{
    private bool completed;

    public SkyrimLightingEditorWindow(SkyrimLightingEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += (_, _) => RigList.Focus();
    }

    public SkyrimLightingEditorViewModel ViewModel =>
        (SkyrimLightingEditorViewModel)DataContext;

    private void AddLight_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryAddLight();

    private void RemoveLight_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryRemoveSelectedLight();

    private void MoveUp_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSelectedLight(-1);

    private void MoveDown_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSelectedLight(1);

    private void Reset_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.Reset();

    private async void Apply_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!await ViewModel.ApplyAsync()) return;
        completed = true;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs eventArgs) =>
        CancelAndClose();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape) return;
        eventArgs.Handled = true;
        CancelAndClose();
    }

    private void CancelAndClose()
    {
        ViewModel.Cancel();
        if (ViewModel.IsBusy) return;
        completed = true;
        DialogResult = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!completed)
        {
            ViewModel.Cancel();
            if (ViewModel.IsBusy)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }
}
