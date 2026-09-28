using System.Windows;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimOutfitEditorWindow : Window
{
    private bool accepted;

    public SkyrimOutfitEditorWindow(SkyrimOutfitEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimOutfitEditorViewModel ViewModel =>
        (SkyrimOutfitEditorViewModel)DataContext;

    private void BeginNew_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.TryBeginNew()) EditorTabs.SelectedIndex = 1;
    }

    private void BeginOverride_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel.TryBeginOverride()) EditorTabs.SelectedIndex = 1;
    }

    private void AddItem_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryAddSelectedItem();

    private void RemoveItem_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryRemoveSelectedItem();

    private void MoveUp_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSelectedItem(-1);

    private void MoveDown_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSelectedItem(1);

    private void Reroll_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryRerollSelected();

    private void Reset_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.Reset();

    private void NewArmor_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryCreateChildItem(SkyrimOutfitEditorItemKind.Armor);

    private void NewLeveledList_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryCreateChildItem(SkyrimOutfitEditorItemKind.LeveledList);

    private void EditItem_OnClick(object sender, RoutedEventArgs eventArgs) =>
        ViewModel.TryEditSelectedChildItem();

    private void WholePreview_OnChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is SkyrimOutfitEditorViewModel viewModel)
            viewModel.PreviewSelectedPiece = false;
    }

    private void Accept_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TryAccept()) return;
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
