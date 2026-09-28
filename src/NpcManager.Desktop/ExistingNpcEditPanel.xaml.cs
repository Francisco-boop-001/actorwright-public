using System.Windows;
using System.Windows.Controls;

namespace NpcManager.Desktop;

public partial class ExistingNpcEditPanel : UserControl
{
    public ExistingNpcEditPanel() => InitializeComponent();

    private async void EditIdentity_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ExistingNpcEditViewModel viewModel ||
            !viewModel.CanEditIdentity)
            return;

        var editor = await viewModel.CreateIdentityEditorAsync();
        if (editor is null) return;
        var dialog = new ExistingNpcIdentityWindow(editor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            viewModel.Identity.Commit(editor);
    }

    private void EditStats_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ExistingNpcEditViewModel viewModel ||
            !viewModel.CanEditStats)
            return;

        var editor = viewModel.Stats.CreateEditor();
        var dialog = new ExistingNpcStatsWindow(editor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            viewModel.Stats.Commit(editor);
    }

    private void EditCollections_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ExistingNpcEditViewModel viewModel ||
            !viewModel.CanEditCollections)
            return;

        var editor = viewModel.Collections.CreateEditor();
        var dialog = new ExistingNpcCollectionsWindow(editor)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            viewModel.Collections.Commit(editor);
    }
}
