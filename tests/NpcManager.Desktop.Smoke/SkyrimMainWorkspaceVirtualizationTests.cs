using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private const int MainWorkspaceVirtualizationRowCount = 5_000;
    private const int MainWorkspaceRealizedContainerLimit = 256;

    private static void RunSkyrimMainWorkspaceVirtualizationTest()
    {
        BrowserRowFixture[] rows = Enumerable
            .Range(0, MainWorkspaceVirtualizationRowCount)
            .Select(index => new BrowserRowFixture(
                $"VirtualNpc{index:D4}",
                $"Virtual NPC {index:D4}",
                $"Actors.esp | 0x{index + 0x800:X8}",
                "NPC",
                "Unchanged"))
            .ToArray();
        var panel = new SkyrimMainWorkspacePanel();
        var window = new Window
        {
            Title = "NPC workspace virtualization regression",
            Width = 980,
            Height = 680,
            MinWidth = 0,
            MinHeight = 0,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Content = panel
        };

        try
        {
            window.Show();
            ListBox records = FindVisualDescendant<ListBox>(
                    panel,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "NPC and leveled NPC record browser",
                        StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
                    "The production main-workspace record browser is missing.");
            BindingOperations.ClearBinding(
                records,
                ItemsControl.ItemsSourceProperty);
            records.ItemsSource = rows;

            AssertVirtualizedMainWorkspace(
                window,
                panel,
                records,
                rows,
                width: 980,
                height: 680);
            AssertVirtualizedMainWorkspace(
                window,
                panel,
                records,
                rows,
                width: 1240,
                height: 820);

            Console.WriteLine(
                "PASS main workspace virtualizes 5,000 records at minimum and default window sizes.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertVirtualizedMainWorkspace(
        Window window,
        SkyrimMainWorkspacePanel panel,
        ListBox records,
        BrowserRowFixture[] rows,
        double width,
        double height)
    {
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            DispatcherPriority.ApplicationIdle);

        int initiallyRealized = CountVisualDescendantsUntil<ListBoxItem>(
            records,
            MainWorkspaceRealizedContainerLimit + 1);
        bool lastInitiallyRealized =
            records.ItemContainerGenerator.ContainerFromIndex(
                rows.Length - 1) is not null;
        Assert(
            initiallyRealized is > 0 and <=
                MainWorkspaceRealizedContainerLimit &&
            !lastInitiallyRealized,
            "The main browser realized an unbounded record set before " +
            "scrolling. The outer workspace viewport has disabled WPF " +
            $"virtualization at {width:F0}x{height:F0}: " +
            $"realized={initiallyRealized}, " +
            $"lastInitiallyRealized={lastInitiallyRealized}.");

        FrameworkElement workspaceViewport =
            FindVisualDescendant<FrameworkElement>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace viewport",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The finite main-workspace viewport is missing.");
        VirtualizingStackPanel itemsHost =
            FindVisualDescendant<VirtualizingStackPanel>(
                records,
                _ => true) ??
            throw new InvalidOperationException(
                "The record browser has no VirtualizingStackPanel items host.");
        ScrollViewer recordViewport =
            FindVisualDescendant<ScrollViewer>(
                records,
                _ => true) ??
            throw new InvalidOperationException(
                "The record browser has no internal scrolling viewport.");
        Assert(
            workspaceViewport is Grid &&
            VirtualizingPanel.GetIsVirtualizing(records) &&
            VirtualizingPanel.GetVirtualizationMode(records) ==
                VirtualizationMode.Recycling &&
            ScrollViewer.GetCanContentScroll(records) &&
            itemsHost.IsItemsHost &&
            records.ActualHeight > 0 &&
            records.ActualHeight <= panel.ActualHeight + 1 &&
            double.IsFinite(recordViewport.ViewportHeight) &&
            recordViewport.ViewportHeight > 0,
            "The production record browser is not arranged as a finite, " +
            "logical-scrolling, recycling viewport.");

        records.ScrollIntoView(rows[^1]);
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            DispatcherPriority.ApplicationIdle);
        int finalRealized = CountVisualDescendantsUntil<ListBoxItem>(
            records,
            MainWorkspaceRealizedContainerLimit + 1);
        Assert(
            records.ItemContainerGenerator.ContainerFromIndex(
                rows.Length - 1) is ListBoxItem &&
            finalRealized is > 0 and <=
                MainWorkspaceRealizedContainerLimit,
            "Scrolling to the final browser row did not realize only a " +
            $"bounded viewport: realized={finalRealized}.");

        records.ScrollIntoView(rows[0]);
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            DispatcherPriority.ApplicationIdle);
        int firstRealized = CountVisualDescendantsUntil<ListBoxItem>(
            records,
            MainWorkspaceRealizedContainerLimit + 1);
        Assert(
            records.ItemContainerGenerator.ContainerFromIndex(0) is
                ListBoxItem &&
            firstRealized is > 0 and <=
                MainWorkspaceRealizedContainerLimit,
            "Scrolling back to the first browser row did not retain a " +
            $"bounded viewport: realized={firstRealized}.");

        ScrollViewer previewViewport =
            FindVisualDescendant<ScrollViewer>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace preview viewport",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The preview column has no independent vertical viewport.");
        ScrollViewer detailsViewport =
            FindVisualDescendant<ScrollViewer>(
                panel,
                item => string.Equals(
                    AutomationProperties.GetName(item),
                    "Main workspace details viewport",
                    StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                "The details column has no independent vertical viewport.");
        Assert(
            previewViewport.ActualHeight > 0 &&
            detailsViewport.ActualHeight > 0 &&
            double.IsFinite(previewViewport.ViewportHeight) &&
            double.IsFinite(detailsViewport.ViewportHeight) &&
            previewViewport.ViewportHeight > 0 &&
            detailsViewport.ViewportHeight > 0,
            "The preview or details column is not independently reachable " +
            "within the finite workspace.");
    }

    private static int CountVisualDescendantsUntil<T>(
        DependencyObject root,
        int stopAfter)
        where T : DependencyObject
    {
        var count = 0;

        void Visit(DependencyObject node)
        {
            for (var index = 0;
                 count < stopAfter &&
                 index < VisualTreeHelper.GetChildrenCount(node);
                 index++)
            {
                DependencyObject child =
                    VisualTreeHelper.GetChild(node, index);
                if (child is T)
                    count++;
                Visit(child);
            }
        }

        Visit(root);
        return count;
    }

    private sealed record BrowserRowFixture(
        string EditorId,
        string Name,
        string IdentityText,
        string KindText,
        string StateText);
}
