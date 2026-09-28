using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NpcManager.Application;

namespace NpcManager.Desktop;

public partial class FaceGeomHairRegionsWizardWindow :
    Window
{
    private readonly ICollectionView regionView;
    private readonly HashSet<
        TaskCompletionSource<bool>> activeOperations =
            [];
    private bool closePending;
    private bool allowClose;
    private bool disposed;

    internal FaceGeomHairRegionsWizardWindow(
        FaceGeomHairRegionsWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ??
            throw new ArgumentNullException(
                nameof(viewModel));
        regionView =
            CollectionViewSource.GetDefaultView(
                ViewModel.Regions);
        regionView.Filter = RegionMatchesFilter;
        RegionsList.ItemsSource = regionView;
        ViewModel.PropertyChanged +=
            ViewModel_OnPropertyChanged;
        ViewModel.Regions.CollectionChanged +=
            Regions_OnCollectionChanged;
        Loaded += (_, _) =>
        {
            UpdateStepVisibility();
            SourcePathBox.Focus();
        };
    }

    internal FaceGeomHairRegionsWizardViewModel ViewModel =>
        (FaceGeomHairRegionsWizardViewModel)
            DataContext;

    private async void Analyze_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await RunAsync(
            () => ViewModel.AnalyzeAsync().AsTask());

    private async void Propose_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await RunAsync(
            () => ViewModel.ProposeAsync().AsTask());

    private async void Preview_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await RunAsync(
            () => ViewModel.RenderPreviewAsync().AsTask());

    private async void Write_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await RunAsync(
            () => ViewModel.ApplyAndVerifyAsync().AsTask());

    private void Previous_OnClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!ViewModel.MoveBack())
        {
            LocalStatus.Text =
                "There is no previous step.";
        }
    }

    private void Next_OnClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!ViewModel.MoveNext())
        {
            LocalStatus.Text =
                "Complete the current authority gate before continuing.";
        }
    }

    private void AcceptProposal_OnClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!ViewModel.TryAcceptProposal())
        {
            LocalStatus.Text =
                "A current successful preview is required before accepting the exact byte plan.";
        }
    }

    private void BrowseSource_OnClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title =
                "Select a K-local Skyrim FaceGeom NIF",
            Filter =
                "Skyrim NIF (*.nif)|*.nif",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.SourcePath =
                dialog.FileName;
        }
    }

    private void RegionFilter_OnTextChanged(
        object sender,
        TextChangedEventArgs eventArgs) =>
        regionView.Refresh();

    private bool RegionMatchesFilter(
        object item)
    {
        if (item is not
            FaceGeomHairRegionCardViewModel region)
        {
            return false;
        }
        string filter =
            RegionFilterBox.Text.Trim();
        return filter.Length == 0 ||
            region.Name.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase) ||
            region.StructuralId.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase) ||
            region.SharedShaderGroupId.Contains(
                filter,
                StringComparison.OrdinalIgnoreCase);
    }

    private void AssignPrimary_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        AssignSelected(
            FaceGeomHairRegionRole.Primary);

    private void AssignAccent_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        AssignSelected(
            FaceGeomHairRegionRole.Accent);

    private void AssignPreserve_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        AssignSelected(
            FaceGeomHairRegionRole.Preserve);

    private void CardPrimary_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        AssignCard(
            sender,
            FaceGeomHairRegionRole.Primary);

    private void CardAccent_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        AssignCard(
            sender,
            FaceGeomHairRegionRole.Accent);

    private void CardPreserve_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        AssignCard(
            sender,
            FaceGeomHairRegionRole.Preserve);

    private static void AssignCard(
        object sender,
        FaceGeomHairRegionRole role)
    {
        if (sender is FrameworkElement
            {
                DataContext:
                    FaceGeomHairRegionCardViewModel
                    region
            })
        {
            region.Role = role;
        }
    }

    private void AssignSelected(
        FaceGeomHairRegionRole role)
    {
        FaceGeomHairRegionCardViewModel[] selected =
            RegionsList.SelectedItems
                .Cast<
                    FaceGeomHairRegionCardViewModel>()
                .ToArray();
        foreach (
            FaceGeomHairRegionCardViewModel region
            in selected)
        {
            region.Role = role;
        }
    }

    private void Window_OnPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        FaceGeomHairRegionRole? role =
            eventArgs.Key switch
            {
                Key.P =>
                    FaceGeomHairRegionRole.Primary,
                Key.A =>
                    FaceGeomHairRegionRole.Accent,
                Key.D0 or Key.NumPad0 =>
                    FaceGeomHairRegionRole.Preserve,
                _ => null
            };
        if (role is not null &&
            ViewModel.CurrentStepIndex == 1)
        {
            AssignSelected(role.Value);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Close();
        }
    }

    private void PreviewZoom_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double>
            eventArgs)
    {
        if (CombinedPreviewImage is null ||
            ContactSheetImage is null)
        {
            return;
        }
        var transform =
            new ScaleTransform(
                eventArgs.NewValue,
                eventArgs.NewValue);
        CombinedPreviewImage.LayoutTransform =
            transform;
        ContactSheetImage.LayoutTransform =
            new ScaleTransform(
                eventArgs.NewValue,
                eventArgs.NewValue);
    }

    private void Cancel_OnClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        Close();

    private void ViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is
                nameof(
                    FaceGeomHairRegionsWizardViewModel
                        .CurrentStepIndex) or
                nameof(
                    FaceGeomHairRegionsWizardViewModel
                        .CurrentStep))
        {
            UpdateStepVisibility();
        }
        if (eventArgs.PropertyName is
            nameof(
                FaceGeomHairRegionsWizardViewModel
                    .Preview))
        {
            RefreshPreviewImages();
        }
    }

    private void Regions_OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs) =>
        regionView.Refresh();

    private void UpdateStepVisibility()
    {
        FrameworkElement[] panels =
        [
            SourceStep,
            RegionsStep,
            ColorsStep,
            ReviewStep,
            WriteStep
        ];
        for (int index = 0;
             index < panels.Length;
             index++)
        {
            panels[index].Visibility =
                index == ViewModel.CurrentStepIndex
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        LocalStatus.Text = string.Empty;
    }

    private void RefreshPreviewImages()
    {
        FaceGeomHairRegionsPreviewResult? result =
            ViewModel.Preview;
        CombinedPreviewImage.Source =
            LoadImage(
                result?.Artifacts.FirstOrDefault(
                    item =>
                        item.Kind ==
                        FaceGeomHairRegionsPreviewArtifactKind
                            .CombinedFace));
        ContactSheetImage.Source =
            LoadImage(
                result?.Artifacts.FirstOrDefault(
                    item =>
                        item.Kind ==
                        FaceGeomHairRegionsPreviewArtifactKind
                            .ContactSheet));
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading
                .DispatcherPriority.DataBind,
            new Action(
                RefreshVisibleRegionArtifactImages));
    }

    private static BitmapImage? LoadImage(
        FaceGeomHairRegionsPreviewArtifact? artifact) =>
        LoadImagePath(
            artifact?.Path.Value);

    private void RegionArtifactImage_OnLoaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is Image image)
        {
            image.Source =
                LoadImagePath(
                    image.Tag as string);
        }
    }

    private void RefreshVisibleRegionArtifactImages()
    {
        RegionPreviewGallery.UpdateLayout();
        RefreshVisibleRegionArtifactImages(
            RegionPreviewGallery);
    }

    private static void RefreshVisibleRegionArtifactImages(
        DependencyObject root)
    {
        int count =
            VisualTreeHelper.GetChildrenCount(
                root);
        for (int index = 0;
             index < count;
             index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(
                    root,
                    index);
            if (child is Image image &&
                (string.Equals(
                     AutomationProperties.GetName(
                         image),
                     "Isolated HairTint region thumbnail",
                     StringComparison.Ordinal) ||
                 string.Equals(
                     AutomationProperties.GetName(
                         image),
                     "HairTint region role mask",
                     StringComparison.Ordinal)))
            {
                image.Source =
                    LoadImagePath(
                        image.Tag as string);
            }
            RefreshVisibleRegionArtifactImages(
                child);
        }
    }

    private static BitmapImage? LoadImagePath(
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            return null;
        }

        byte[] bytes =
            File.ReadAllBytes(path);
        using var stream =
            new MemoryStream(bytes);
        var bitmap =
            new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption =
            BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task RunAsync(
        Func<Task> operation)
    {
        if (closePending)
        {
            return;
        }

        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        activeOperations.Add(
            completion);
        LocalStatus.Text = string.Empty;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            LocalStatus.Text =
                "Operation canceled.";
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException or
                InvalidOperationException)
        {
            LocalStatus.Text =
                $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            activeOperations.Remove(
                completion);
            completion.TrySetResult(
                true);
        }
    }

    protected override void OnClosing(
        CancelEventArgs e)
    {
        if (!allowClose &&
            closePending)
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        if (!allowClose &&
            activeOperations.Count > 0)
        {
            e.Cancel = true;
            closePending = true;
            IsEnabled = false;
            LocalStatus.Text =
                "Cancelling and draining the active HairTint operation...";
            ViewModel.CancelActiveOperationsForClose();
            Task[] drain =
                activeOperations
                    .Select(item =>
                        item.Task)
                    .ToArray();
            _ = DrainAndCloseAsync(
                drain);
            base.OnClosing(e);
            return;
        }

        DisposeViewModel();
        base.OnClosing(e);
    }

    private async Task DrainAndCloseAsync(
        Task[] drain)
    {
        Task completion =
            Task.WhenAll(drain);
        Task checkpoint =
            Task.Delay(
                TimeSpan.FromSeconds(30));
        if (await Task.WhenAny(
                completion,
                checkpoint) != completion)
        {
            LocalStatus.Text =
                "Cancellation exceeded 30 seconds. The wizard remains open and retains its source and service authority until cleanup exits.";
        }

        await completion;
        allowClose = true;
        if (!Dispatcher.HasShutdownStarted)
        {
            Close();
        }
    }

    private void DisposeViewModel()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ViewModel.PropertyChanged -=
            ViewModel_OnPropertyChanged;
        ViewModel.Regions.CollectionChanged -=
            Regions_OnCollectionChanged;
        ViewModel.Dispose();
    }

    protected override void OnClosed(
        EventArgs e)
    {
        DisposeViewModel();
        base.OnClosed(e);
    }
}
