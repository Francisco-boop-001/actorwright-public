using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimFaceEditorWindow : Window
{
    private bool accepted;

    public SkyrimFaceEditorWindow(SkyrimFaceEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private SkyrimFaceEditorViewModel ViewModel =>
        (SkyrimFaceEditorViewModel)DataContext;

    private void Tabs_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.Source, FaceTabs)) return;
        ViewModel.SelectedSection = (SkyrimFaceEditorSection)Math.Clamp(
            FaceTabs.SelectedIndex, 0, Enum.GetValues<SkyrimFaceEditorSection>().Length - 1);
    }

    private void ChooseHeadPart_Click(object sender, RoutedEventArgs eventArgs)
    {
        using SkyrimHeadPartPickerViewModel picker =
            ViewModel.CreateHeadPartPicker(ViewModel.Parts.SelectedAddType.Value);
        var window = new SkyrimHeadPartPickerWindow(picker) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ApplyHeadPartPicker(picker);
    }

    private void RemoveHeadPart_Click(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.Parts.RemoveSelected();

    private void HeadParts_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete || !ViewModel.Parts.CanRemove) return;
        ViewModel.Parts.RemoveSelected();
        eventArgs.Handled = true;
    }

    private void ChooseHairColor_Click(object sender, RoutedEventArgs eventArgs)
    {
        TypedFormIdPickerViewModel picker = ViewModel.CreateHairColorPicker();
        var window = new TypedFormIdPickerWindow(picker) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ApplyHairColorPicker(picker);
    }

    private void PreserveHairColor_Click(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.PreserveHairColor();

    private void ChooseHeadTexture_Click(object sender, RoutedEventArgs eventArgs)
    {
        TypedFormIdPickerViewModel picker = ViewModel.CreateHeadTexturePicker();
        var window = new TypedFormIdPickerWindow(picker) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ApplyHeadTexturePicker(picker);
    }

    private void ChooseTintMask_Click(object sender, RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.DataContext is not SkyrimFaceTintRowViewModel row) return;
        SkyrimRaceMenuPaintPickerViewModel picker = ViewModel.CreateTintMaskPicker(row.Index);
        var window = new SkyrimRaceMenuPaintPickerWindow(picker) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ApplyTintMaskPicker(row.Index, picker);
    }

    private void AddFacePaint_Click(object sender, RoutedEventArgs eventArgs)
    {
        SkyrimRaceMenuPaintPickerViewModel picker = ViewModel.CreateFacePaintPicker();
        var window = new SkyrimRaceMenuPaintPickerWindow(picker) { Owner = this };
        if (window.ShowDialog() == true) ViewModel.ApplyFacePaintPicker(picker);
    }

    private void RemoveFacePaint_Click(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.FaceOverlays.RemoveSelected();

    private void FaceOverlays_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Delete || !ViewModel.FaceOverlays.CanRemove) return;
        ViewModel.FaceOverlays.RemoveSelected();
        eventArgs.Handled = true;
    }

    private void MoveFacePaintUp_Click(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.FaceOverlays.MoveSelectedUp();

    private void MoveFacePaintDown_Click(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.FaceOverlays.MoveSelectedDown();

    private void ResetSection_Click(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.ResetSelectedSection();

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
