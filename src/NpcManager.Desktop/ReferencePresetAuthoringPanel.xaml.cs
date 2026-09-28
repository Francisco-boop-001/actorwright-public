using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public partial class ReferencePresetAuthoringPanel
    : UserControl
{
    private bool correctingStep;

    public ReferencePresetAuthoringPanel()
    {
        InitializeComponent();
    }

    private ReferencePresetAuthoringViewModel? ViewModel =>
        DataContext as ReferencePresetAuthoringViewModel;

    private void AuthoringStepsSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (correctingStep ||
            ViewModel is not { } viewModel ||
            AuthoringSteps.SelectedIndex ==
                viewModel.VisibleStepIndex)
        {
            return;
        }
        correctingStep = true;
        AuthoringSteps.SelectedIndex =
            viewModel.VisibleStepIndex;
        correctingStep = false;
    }

    private void ChooseBaselineClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } viewModel)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose baseline RaceMenu JSlot",
            Filter = "RaceMenu preset (*.jslot)|*.jslot",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
            viewModel.BaselineJslotPath = dialog.FileName;
    }

    private void AddImageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } viewModel)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Choose one reference image",
            Filter =
                "Reference images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            viewModel.AddReferenceImage(
                new WorkspacePath(dialog.FileName),
                viewModel.NewImageViewRole);
        }
        catch (ArgumentException)
        {
            // The view model reports authoritative input failures at commit.
        }
    }

    private void RemoveImageClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is { IsBusy: false } viewModel &&
            ReferenceImageList.SelectedItem is
                ReferencePresetImageInputDraft image)
        {
            viewModel.RemoveReferenceImage(image);
        }
    }

    private void AcceptAnchorsClick(
        object sender,
        RoutedEventArgs e) =>
        ViewModel?.AcceptAllProposedAnchors();

    private void AcceptViewsClick(
        object sender,
        RoutedEventArgs e) =>
        ViewModel?.AcceptAllViews();

    private void AcceptTraitsClick(
        object sender,
        RoutedEventArgs e) =>
        ViewModel?.AcceptAllTraits();

    private void AcknowledgeUnknownsClick(
        object sender,
        RoutedEventArgs e) =>
        ViewModel?.AcknowledgeAllUnknowns();

    private void CommitResourcesClick(
        object sender,
        RoutedEventArgs e) =>
        ViewModel?.CommitResourceSelection();

    private void ReferenceImageMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ViewModel is not
            {
                SelectedReviewView: { } view,
                SelectedReviewAnchor: { } anchor
            } viewModel ||
            sender is not Image image ||
            image.ActualWidth <= 0 ||
            image.ActualHeight <= 0)
        {
            return;
        }
        Point point = e.GetPosition(image);
        viewModel.CorrectAnchorFromDisplay(
            view.ViewRole,
            anchor.Anchor.Anchor,
            point.X,
            point.Y,
            0,
            0,
            image.ActualWidth,
            image.ActualHeight);
    }

    private void AnchorReviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (ViewModel is not
            {
                SelectedReviewView: { } view,
                SelectedReviewAnchor: { } anchor
            } viewModel)
        {
            return;
        }
        const double step = 0.001;
        (double X, double Y)? delta = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => null
        };
        if (delta is null)
            return;
        e.Handled = viewModel.CorrectAnchorFromDisplay(
            view.ViewRole,
            anchor.Anchor.Anchor,
            Math.Clamp(
                anchor.Anchor.X + delta.Value.X,
                0,
                1),
            Math.Clamp(
                anchor.Anchor.Y + delta.Value.Y,
                0,
                1),
            0,
            0,
            1,
            1);
    }

    private void BaselineImageMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ViewModel is not
            {
                SelectedReviewView: { } view,
                SelectedReviewAnchor: { } anchor,
                RenderInput: { } renderInput
            } viewModel ||
            sender is not Image image ||
            image.ActualWidth <= 0 ||
            image.ActualHeight <= 0)
        {
            return;
        }
        ReferenceRenderShape? head =
            renderInput.Shapes.FirstOrDefault();
        if (head is null)
            return;
        Point point = e.GetPosition(image);
        viewModel.BindBaselineAnchor(
            view.ViewRole,
            anchor.Anchor.Anchor,
            head.NifIdentity,
            head.ShapeIdentity,
            point.X / image.ActualWidth,
            point.Y / image.ActualHeight);
    }

    private void ContinueToNpcClick(
        object sender,
        RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        viewModel.ContinueToNpc();
        if (viewModel.CurrentStep ==
            ReferencePresetDesktopStep.DownstreamHandoff)
        {
            (Window.GetWindow(this) as MainWindow)?
                .OpenPresetToNpcTask();
        }
    }
}
