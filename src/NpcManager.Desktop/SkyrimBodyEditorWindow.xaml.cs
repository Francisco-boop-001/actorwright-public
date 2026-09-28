using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimBodyEditorWindow : Window
{
    private bool accepted;

    public SkyrimBodyEditorWindow(SkyrimBodyEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SkyrimBodyEditorViewModel ViewModel => (SkyrimBodyEditorViewModel)DataContext;

    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.Source, BodyTabs)) return;
        ViewModel.SelectedSection = (SkyrimBodyEditorSection)Math.Clamp(
            BodyTabs.SelectedIndex, 0, Enum.GetValues<SkyrimBodyEditorSection>().Length - 1);
    }

    private void ApplyTransform_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.Transforms.Apply();
    private void ResetTransform_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.Transforms.ResetSelected();

    private void Transforms_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete || !ViewModel.Transforms.CanReset) return;
        ViewModel.Transforms.ResetSelected();
        eventArgs.Handled = true;
    }

    private void AddSkinOverride_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.SkinOverrides.Add();
    private void ApplySkinOverride_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.SkinOverrides.Apply();
    private void RemoveSkinOverride_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.SkinOverrides.RemoveSelected();

    private void SkinOverrides_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete || !ViewModel.SkinOverrides.CanEdit) return;
        ViewModel.SkinOverrides.RemoveSelected();
        eventArgs.Handled = true;
    }

    private void AddBodyPaint_Click(object sender, RoutedEventArgs eventArgs)
    {
        BodyOverlayTarget target = ViewModel.BodyOverlays.SelectedTarget.Value;
        SkyrimRaceMenuPaintPickerViewModel picker = ViewModel.CreateBodyPaintPicker(target);
        var window = new SkyrimRaceMenuPaintPickerWindow(picker) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ApplyBodyPaintPicker(target, picker);
    }

    private void RemoveBodyPaint_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.BodyOverlays.RemoveSelected();
    private void MoveBodyPaintUp_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.BodyOverlays.MoveSelectedUp();
    private void MoveBodyPaintDown_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.BodyOverlays.MoveSelectedDown();

    private void BodyOverlays_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete || !ViewModel.BodyOverlays.CanRemove) return;
        ViewModel.BodyOverlays.RemoveSelected();
        eventArgs.Handled = true;
    }

    private void ResetSection_Click(object sender, RoutedEventArgs eventArgs) => ViewModel.ResetSelectedSection();

    private void Accept_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TryAccept()) return;
        accepted = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs)
    {
        ViewModel.Cancel();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!accepted) ViewModel.Cancel();
        base.OnClosed(e);
    }
}
