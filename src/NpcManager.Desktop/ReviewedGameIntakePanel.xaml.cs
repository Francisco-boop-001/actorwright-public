using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NpcManager.Desktop;

public partial class ReviewedGameIntakePanel : UserControl
{
    private const double SingleColumnBreakpoint = 1040;

    public ReviewedGameIntakePanel() => InitializeComponent();

    private void PanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var singleColumn = e.NewSize.Width < SingleColumnBreakpoint;
        PrimaryColumn.MinWidth = singleColumn ? 0 : 590;
        PrimaryColumn.Width = new GridLength(3, GridUnitType.Star);
        GutterColumn.Width = new GridLength(singleColumn ? 0 : 22);
        ReviewColumn.MinWidth = singleColumn ? 0 : 330;
        ReviewColumn.Width = singleColumn
            ? new GridLength(0)
            : new GridLength(2, GridUnitType.Star);
        Grid.SetColumn(ReviewPanel, singleColumn ? 0 : 2);
        Grid.SetRow(ReviewPanel, singleColumn ? 1 : 0);
        ReviewPanel.Margin = singleColumn
            ? new Thickness(0, 14, 0, 0)
            : new Thickness(0);
    }

    private async void ReviewClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ReviewedGameIntakeViewModel viewModel)
        {
            await viewModel.ReviewAsync();
            if (!viewModel.HasAcceptedIntake && DiagnosticsList.Items.Count > 0)
            {
                DiagnosticsList.UpdateLayout();
                if (DiagnosticsList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
                    item.Focus();
                else
                    DiagnosticsList.Focus();
            }
        }
    }

    private void PanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PluginList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape ||
            DataContext is not ReviewedGameIntakeViewModel { IsBusy: true } viewModel ||
            !viewModel.CancelCommand.CanExecute(null)) return;
        viewModel.CancelCommand.Execute(null);
        e.Handled = true;
    }
}
