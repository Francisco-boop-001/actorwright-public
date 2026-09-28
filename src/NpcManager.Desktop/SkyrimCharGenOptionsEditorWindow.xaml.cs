using System.Windows;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class SkyrimCharGenOptionsEditorWindow : Window
{
    private bool accepted;

    public SkyrimCharGenOptionsEditorWindow(
        SkyrimCharGenOptionsEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ??
            throw new ArgumentNullException(nameof(viewModel));
    }

    private SkyrimCharGenOptionsEditorViewModel ViewModel =>
        (SkyrimCharGenOptionsEditorViewModel)DataContext;

    private void ResetTexture_OnClick(
        object sender,
        RoutedEventArgs eventArgs) => ViewModel.ResetTexture();

    private void ResetConvention_OnClick(
        object sender,
        RoutedEventArgs eventArgs) => ViewModel.ResetConvention();

    private void ResetSort_OnClick(
        object sender,
        RoutedEventArgs eventArgs) => ViewModel.ResetSort();

    private void AddTintRule_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryAddSortRule(SkyrimCharGenSortList.Tint);

    private void RemoveTintRule_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryRemoveSortRule(SkyrimCharGenSortList.Tint);

    private void MoveTintRuleUp_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSortRule(SkyrimCharGenSortList.Tint, -1);

    private void MoveTintRuleDown_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSortRule(SkyrimCharGenSortList.Tint, 1);

    private void AddOverlayRule_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryAddSortRule(SkyrimCharGenSortList.Overlay);

    private void RemoveOverlayRule_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryRemoveSortRule(SkyrimCharGenSortList.Overlay);

    private void MoveOverlayRuleUp_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSortRule(SkyrimCharGenSortList.Overlay, -1);

    private void MoveOverlayRuleDown_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel.TryMoveSortRule(SkyrimCharGenSortList.Overlay, 1);

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
