using System.Collections.Specialized;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public partial class SkyrimMainWorkspacePanel : UserControl
{
    private SkyrimMainWorkspaceViewModel? viewModel;
    private bool synchronizingSelection;
    private bool openingHairRegionsWizard;

    public SkyrimMainWorkspacePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Detach(viewModel);
        Attach(viewModel);
        QueueSelectionSync();
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        Detach(eventArgs.OldValue as SkyrimMainWorkspaceViewModel);
        viewModel = eventArgs.NewValue as SkyrimMainWorkspaceViewModel;
        Attach(viewModel);
        QueueSelectionSync();
    }

    private void Attach(SkyrimMainWorkspaceViewModel? value)
    {
        if (value is null)
            return;
        value.VisibleRows.CollectionChanged += OnRowsChanged;
        value.SelectedRows.CollectionChanged += OnRowsChanged;
    }

    private void Detach(SkyrimMainWorkspaceViewModel? value)
    {
        if (value is null)
            return;
        value.VisibleRows.CollectionChanged -= OnRowsChanged;
        value.SelectedRows.CollectionChanged -= OnRowsChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs eventArgs) =>
        Detach(viewModel);

    private void OnRowsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs) =>
        QueueSelectionSync();

    private void QueueSelectionSync() =>
        _ = Dispatcher.BeginInvoke(SyncSelectionFromViewModel);

    private void SyncSelectionFromViewModel()
    {
        if (viewModel is null)
            return;
        synchronizingSelection = true;
        try
        {
            RecordsList.SelectedItems.Clear();
            foreach (SkyrimMainWorkspaceRowViewModel row in
                     viewModel.SelectedRows)
            {
                if (RecordsList.Items.Contains(row))
                    RecordsList.SelectedItems.Add(row);
            }
        }
        finally
        {
            synchronizingSelection = false;
        }
    }

    private void RecordsSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (synchronizingSelection || viewModel is null)
            return;
        viewModel.ReplaceSelection(
            RecordsList.SelectedItems
                .Cast<SkyrimMainWorkspaceRowViewModel>()
                .Select(row => row.Identity)
                .ToImmutableArray());
    }

    private void RecordsPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (FindAncestor<ListBoxItem>(
                eventArgs.OriginalSource as DependencyObject) is not
                { } item ||
            item.IsSelected)
            return;
        RecordsList.SelectedItems.Clear();
        item.IsSelected = true;
    }

    private async void OpenNpcVisualPackageForPreview(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (viewModel is null ||
            viewModel.HasActiveOperation)
            return;
        var dialog = new OpenFileDialog
        {
            Title =
                "Open verified NPC Manager package for preview",
            Filter =
                "NPC Manager package (npcmanager-package.json)|npcmanager-package.json|JSON files (*.json)|*.json",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
            await viewModel.OpenNpcVisualPackageAsync(
                new NpcManager.Domain.WorkspacePath(
                    dialog.FileName));
    }

    private void ClearNpcVisualPackagePreview(
        object sender,
        RoutedEventArgs eventArgs) =>
        viewModel?.ClearNpcVisualPackagePreview();

    private async void OpenDualToneHairWizard(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (viewModel is null ||
            viewModel.HasActiveOperation ||
            openingHairRegionsWizard)
        {
            return;
        }

        if (!viewModel.TryGetHairRegionsWizardContext(
                out FaceGeomHairRegionsWizardProductionContext?
                    context) ||
            context is null)
        {
            ShowHairWizardMessage(
                "The production HairTint authoring services are not available in this desktop composition.",
                MessageBoxImage.Error);
            return;
        }

        openingHairRegionsWizard = true;
        FaceGeomHairRegionsSelectedSourceLease? sourceLease =
            null;
        string? resultRoot = null;
        try
        {
            WorkspacePath? source = null;
            if (context.Identity is not null)
            {
                if (context.ReviewedIntake is null)
                {
                    ShowHairWizardMessage(
                        "Load and accept a reviewed K-local game intake before resolving the selected NPC's exact FaceGeom.",
                        MessageBoxImage.Warning);
                    return;
                }

                sourceLease =
                    await context.Composition
                        .ResolveSelectedSourceAsync(
                            context.ReviewedIntake,
                            context.Identity,
                            CancellationToken.None);
                FaceGeomHairRegionsSelectedSourceResult
                    resolved = sourceLease.Result;
                if (!resolved.Resolved ||
                    resolved.Source is null ||
                    resolved.Diagnostics.Any(item =>
                        item.Severity ==
                        DiagnosticSeverity.Error))
                {
                    ShowHairWizardMessage(
                        "The selected NPC's exact winning FaceGeom could not be admitted.\n\n" +
                        FormatHairWizardDiagnostics(
                            resolved.Diagnostics),
                        MessageBoxImage.Warning);
                    return;
                }

                source =
                    resolved.Source.Source.Path;
            }

            string projectWork = Path.Combine(
                context.LabRoot.Value,
                ".actorwright",
                "work");
            var cacheRoot = new WorkspacePath(
                Path.Combine(
                    projectWork,
                    "desktop-hair-regions-cache",
                    "content-v1"));
            var cache =
                new FaceGeomHairRegionsPreviewCache(
                    context.LabRoot,
                    cacheRoot);

            resultRoot = Path.Combine(
                projectWork,
                "desktop-hair-regions-results",
                $"result-{Guid.NewGuid():N}");
            Directory.CreateDirectory(
                resultRoot);
            var output = new WorkspacePath(
                Path.Combine(
                    resultRoot,
                    "hair-regions-facegeom.nif"));
            var manifest = new WorkspacePath(
                Path.Combine(
                    resultRoot,
                    "hair-regions-manifest.json"));
            var launch =
                new FaceGeomHairRegionsWizardLaunchContext(
                    context.Identity,
                    source,
                    context.DisplayName);
            using var wizardViewModel =
                new FaceGeomHairRegionsWizardViewModel(
                    context.Composition.Transaction,
                    cache,
                    context.LabRoot,
                    context.ReviewedIntake,
                    launch)
                {
                    OutputPath = output.Value,
                    ManifestPath = manifest.Value
                };
            var window =
                new FaceGeomHairRegionsWizardWindow(
                    wizardViewModel)
                {
                    Owner = Window.GetWindow(this)
                };
            _ = window.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            ShowHairWizardMessage(
                "HairTint source resolution was canceled. No FaceGeom was written.",
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException)
        {
            ShowHairWizardMessage(
                $"{exception.GetType().Name}: {exception.Message}",
                MessageBoxImage.Error);
        }
        finally
        {
            string? sourceCleanupFailure = null;
            try
            {
                sourceLease?.Dispose();
            }
            catch (FaceGeomHairRegionsOperationalException
                   exception)
            {
                sourceCleanupFailure =
                    "Selected FaceGeom staging cleanup failed. " +
                    $"Surviving path(s): {string.Join(", ", exception.SurvivingArtifacts.Select(path => path.Value))}. " +
                    exception.Message;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                sourceCleanupFailure =
                    $"Selected FaceGeom staging cleanup failed: {exception.Message}";
            }
            string? cleanupFailure =
                TryRemoveEmptyHairWizardResultRoot(
                    resultRoot);
            openingHairRegionsWizard = false;
            string? combinedCleanupFailure =
                string.Join(
                    "\n",
                    new[]
                    {
                        sourceCleanupFailure,
                        cleanupFailure
                    }
                    .Where(value =>
                        value is not null));
            if (!string.IsNullOrWhiteSpace(
                    combinedCleanupFailure))
            {
                ShowHairWizardMessage(
                    combinedCleanupFailure,
                    MessageBoxImage.Warning);
            }
        }
    }

    private static string FormatHairWizardDiagnostics(
        ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.IsDefaultOrEmpty
            ? "No diagnostic detail was returned."
            : string.Join(
                "\n",
                diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}"));

    private void ShowHairWizardMessage(
        string message,
        MessageBoxImage icon) =>
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            "Dual-tone Hair / Accent Regions",
            MessageBoxButton.OK,
            icon);

    private static string?
        TryRemoveEmptyHairWizardResultRoot(
        string? resultRoot)
    {
        if (string.IsNullOrWhiteSpace(
                resultRoot))
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(
                    resultRoot) ||
                Directory.EnumerateFileSystemEntries(
                    resultRoot).Any())
            {
                return null;
            }
            Directory.Delete(
                resultRoot);
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return
                $"Empty HairTint result staging survived at {resultRoot}: {exception.Message}";
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (DependencyObject? current = source;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }
        return null;
    }
}
