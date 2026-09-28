using System.Windows;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimArmorEditorWindow : Window
{
    public SkyrimArmorEditorWindow()
    {
        InitializeComponent();
    }

    private SkyrimArmorEditorViewModel ViewModel =>
        DataContext as SkyrimArmorEditorViewModel ??
        throw new InvalidOperationException(
            "The Armor editor requires SkyrimArmorEditorViewModel data context.");

    private void Blank_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TrySwitchIntent(SkyrimArmorEditorIntent.BlankNew);
    private void Template_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TrySwitchIntent(SkyrimArmorEditorIntent.NewFromTemplate);
    private void Override_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TrySwitchIntent(SkyrimArmorEditorIntent.OverrideExisting);
    private void EditAuthored_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TrySwitchIntent(SkyrimArmorEditorIntent.EditAuthored);
    private void Delete_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TryDeleteOrRevert();
    private void Recalculate_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TryRecalculateSlots();
    private void AddAddon_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryAddArmorAddon();
    private void ReplaceAddon_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryReplaceArmorAddon();
    private void RemoveAddon_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryRemoveArmorAddon();
    private void MoveAddonUp_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryMoveArmorAddon(-1);
    private void MoveAddonDown_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryMoveArmorAddon(1);
    private void AddKeyword_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryAddKeyword();
    private void RemoveKeyword_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryRemoveKeyword();
    private void Preview_OnClick(object sender, RoutedEventArgs e) => ViewModel.TryPreview();
    private void PickMaleWorldModel_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TryPickWorldModel(SkyrimMeshTargetField.ArmorMaleWorld);
    private void PickFemaleWorldModel_OnClick(object sender, RoutedEventArgs e) =>
        ViewModel.TryPickWorldModel(SkyrimMeshTargetField.ArmorFemaleWorld);

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TrySave()) return;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Cancel();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true && DataContext is SkyrimArmorEditorViewModel viewModel)
            viewModel.Cancel();
        base.OnClosed(e);
    }
}
