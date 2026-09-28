using System.Windows;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimArmorAddonEditorWindow : Window
{
    private bool accepted;

    public SkyrimArmorAddonEditorWindow(
        SkyrimArmorAddonEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ??
            throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimArmorAddonEditorViewModel ViewModel =>
        (SkyrimArmorAddonEditorViewModel)DataContext;

    private void Blank_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TrySwitchIntent(SkyrimArmorAddonEditorIntent.BlankNew);
    private void Template_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TrySwitchIntent(SkyrimArmorAddonEditorIntent.NewFromTemplate);
    private void Override_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TrySwitchIntent(SkyrimArmorAddonEditorIntent.OverrideExisting);
    private void EditAuthored_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TrySwitchIntent(SkyrimArmorAddonEditorIntent.EditAuthored);
    private void Delete_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryDeleteOrRevert();
    private void AddRace_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryAddAdditionalRace();
    private void RemoveRace_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryRemoveAdditionalRace();
    private void MoveRaceUp_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveAdditionalRace(-1);
    private void MoveRaceDown_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveAdditionalRace(1);
    private void Preview_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryPreview();
    private void PickMaleThirdPersonModel_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryPickModel(
            SkyrimMeshTargetField.ArmorAddonMaleThirdPerson);
    private void PickFemaleThirdPersonModel_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryPickModel(
            SkyrimMeshTargetField.ArmorAddonFemaleThirdPerson);
    private void PickMaleFirstPersonModel_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryPickModel(
            SkyrimMeshTargetField.ArmorAddonMaleFirstPerson);
    private void PickFemaleFirstPersonModel_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryPickModel(
            SkyrimMeshTargetField.ArmorAddonFemaleFirstPerson);

    private void PickRace_OnClick(object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.PrimaryRace);
    private void PickMaleTexture_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.MaleSkinTexture);
    private void PickFemaleTexture_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.FemaleSkinTexture);
    private void PickMaleSwapList_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.MaleSkinTextureSwapList);
    private void PickFemaleSwapList_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.FemaleSkinTextureSwapList);
    private void PickFootstep_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.FootstepSet);
    private void PickArtObject_OnClick(
        object sender, RoutedEventArgs eventArgs) =>
        PickReference(SkyrimArmorAddonReferenceField.ArtObject);

    private void PickReference(SkyrimArmorAddonReferenceField field)
    {
        if (!ViewModel.HasTypedReferenceCatalog)
        {
            ViewModel.TryPickReference(field);
            return;
        }
        TypedFormIdPickerViewModel picker =
            ViewModel.CreateReferencePicker(field);
        var dialog = new TypedFormIdPickerWindow(picker) { Owner = this };
        if (dialog.ShowDialog() == true)
            ViewModel.ApplyReferencePicker(field, picker);
        else
            ViewModel.ReportReferencePickerCancelled();
    }

    private void Save_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TrySave()) return;
        accepted = true;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs eventArgs)
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
