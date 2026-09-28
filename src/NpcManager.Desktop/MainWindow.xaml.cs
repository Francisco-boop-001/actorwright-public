using System.ComponentModel;
using System.IO;
using System.Windows;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

public partial class MainWindow : Window
{
    private bool initialized;
    private readonly string? startupRequestError;
    private readonly bool startupWorkflowModeRequested;
    private readonly DesktopWorkflowLaunchBinding? startupWorkflowReview;
    private readonly StartupInitializationCloseGate startupCloseGate = new();

    public MainWindow()
        : this(
            ActorwrightWorkspace.ResolveAdmittedRoot(),
            Environment.GetCommandLineArgs().Skip(1))
    {
    }

    internal MainWindow(
        WorkspacePath labRoot,
        IEnumerable<string> startupArguments)
    {
        ArgumentNullException.ThrowIfNull(startupArguments);
        string[] startupTokens = startupArguments.ToArray();
        InitializeComponent();
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            ActorwrightWorkspace.ResolveProtectedRoot(labRoot));
        var reviewedIntake = ReviewedGameIntakeDesktopComposition.Create(policy, labRoot);
        var blankNpc = BlankNpcDesktopComposition.Create(policy, labRoot);
        var raceMenuNpc = RaceMenuNpcDesktopComposition.Create(
            policy, labRoot, blankNpc.FaceGeomCarrierService);
        var startupRequest = DesktopStartupRequestOptions.Resolve(
            startupTokens, labRoot);
        startupRequestError = startupRequest.Error;
        startupWorkflowReview = startupRequest.WorkflowReview;
        startupWorkflowModeRequested = startupTokens.Any(token =>
            token.Equals(
                "--workflow-bundle",
                StringComparison.OrdinalIgnoreCase) ||
            token.Equals(
                "--workflow-bundle-sha256",
                StringComparison.OrdinalIgnoreCase));
        var workflowReview = new DesktopWorkflowReviewViewModel(
            new DesktopWorkflowReviewService(policy, labRoot));
        var existingNpcEdit = ExistingNpcEditDesktopComposition.Create(policy, labRoot);
        var lighting = new SkyrimLightingWorkspaceViewModel(
            new SkyrimLightingSettingsService(
                policy,
                labRoot,
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    ".actorwright",
                    "work",
                    "npc-studio-settings",
                    "preview-lighting.json"))));
        var raceMenuNpcViewModel = new RaceMenuNpcBuildViewModel(
            raceMenuNpc.RequestLoader,
            raceMenuNpc.BuildService,
            raceMenuNpc.PresetCatalogService,
            raceMenuNpc.PresetCatalogLifetime,
            labRoot,
            startupRequest.RequestFile ?? raceMenuNpc.InitialRequestFile,
            startupRequest.RequestSha256 ?? raceMenuNpc.InitialRequestSha256,
            raceMenuNpc.OutputParent,
            raceMenuNpc.PresetPreviewService,
            raceMenuNpc.PresetSelectionTransactionService,
            raceMenuNpc.JslotBuildService,
            raceMenuNpc.PreflightService,
            raceMenuNpc.PreviewComposer);
        ReferencePresetAuthoringViewModel
            referencePresetViewModel =
                ReferencePresetDesktopComposition.Create(
                    policy,
                    labRoot,
                    raceMenuNpc.JslotBuildService,
                    raceMenuNpcViewModel
                        .TryAcceptReferencePresetHandoff);
        var finishCore = SkyrimNpcFinishWizardDesktopComposition.Create(
            policy,
            labRoot,
            raceMenuNpcViewModel.TryResolveTargetRace);
        var voice = SkyrimNpcVoiceDesktopComposition.Create(labRoot);
        DataContext = new WorkspaceShellViewModel(
            new ReviewedGameIntakeViewModel(reviewedIntake),
            blankNpc.Service,
            blankNpc.InitialRequest,
            raceMenuNpcViewModel,
            new ExistingNpcEditViewModel(
                existingNpcEdit.Service,
                existingNpcEdit.FormChoices,
                labRoot,
                existingNpcEdit.InitialRequest),
            SkyrimHeadPartEditDesktopComposition.Create(policy, labRoot),
            SkyrimFaceEditDesktopComposition.Create(policy, labRoot),
            SkyrimBodyEditDesktopComposition.Create(policy, labRoot),
            SkyrimSelectiveAppearancePasteDesktopComposition.Create(policy, labRoot),
            SkyrimOutfitProductionDesktopComposition.Create(policy, labRoot),
            SkyrimLeveledListProductionDesktopComposition.Create(policy, labRoot),
            SkyrimArmorProductionDesktopComposition.Create(policy, labRoot),
            SkyrimCharGenOptionsProductionDesktopComposition.Create(policy, labRoot),
            new NativeFaceGenBatchViewModel(
                NativeFaceGenBatchDesktopComposition.Create(policy, labRoot),
                reviewedIntake.Service),
            SkyrimSavePackageDesktopComposition.Create(policy, labRoot),
            lighting,
            PreviewAnimationComposition.CreatePickerService(policy, labRoot),
            SkyrimMainWorkspaceDesktopComposition.Create(
                policy,
                labRoot),
            referencePresetViewModel,
            finishCore.ViewModel,
            workflowReview,
            voice,
            blankNpc.ProviderSelector);
        ((WorkspaceShellViewModel)DataContext)
            .WorkspaceNavigationRequested +=
            OnWorkspaceNavigationRequested;
        Loaded += InitializeWorkspaceAsync;
    }

    private async void InitializeWorkspaceAsync(
        object sender,
        RoutedEventArgs e) =>
        await InitializeWorkspaceCoreAsync();

    internal async Task InitializeWorkspaceCoreAsync()
    {
        if (initialized)
            return;
        initialized = true;
        if (DataContext is not WorkspaceShellViewModel viewModel)
            return;
        startupCloseGate.Begin();
        try
        {
            await DesktopStartupFailureBoundary.RunInitializationAsync(
                async () =>
                {
                    await viewModel.Lighting.InitializeAsync();
                    if (startupRequestError is not null)
                    {
                        if (startupWorkflowModeRequested &&
                            viewModel.WorkflowReview is not null)
                        {
                            viewModel.WorkflowReview.PresentStartupError(
                                startupRequestError);
                            MainTasks.SelectedItem = ReviewHandoffTab;
                        }
                        else
                        {
                            viewModel.RaceMenuNpc.PresentStartupError(
                                startupRequestError);
                        }
                        return;
                    }

                    if (startupWorkflowReview is not null &&
                        viewModel.WorkflowReview is not null)
                    {
                        await viewModel.WorkflowReview.LoadAsync(
                            startupWorkflowReview);
                        MainTasks.SelectedItem = ReviewHandoffTab;
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(
                            viewModel.RaceMenuNpc.RequestSha256))
                    {
                        await viewModel.RaceMenuNpc.ReviewAsync();
                    }
                },
                viewModel.RaceMenuNpc.PresentStartupError,
                exception => (System.Windows.Application.Current as App)?
                    .WriteStartupFailure(exception));
        }
        finally
        {
            if (startupCloseGate.Complete() &&
                !Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void OpenAnimationPickerClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceShellViewModel viewModel ||
            !viewModel.AnimationPicker.CanOpenPicker)
        {
            return;
        }

        using var dialog = new AnimationPickerWindow(viewModel.AnimationPicker)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Selection is { } selection)
            viewModel.AnimationPicker.CommitSelection(selection);
    }

    private void OpenLightingEditorClick(object sender, RoutedEventArgs e)
        => OpenLightingEditor();

    private void OpenLightingEditor()
    {
        if (DataContext is not WorkspaceShellViewModel viewModel ||
            viewModel.Lighting.IsBusy)
        {
            return;
        }

        using SkyrimLightingEditorViewModel editor =
            viewModel.Lighting.CreateEditor();
        var dialog = new SkyrimLightingEditorWindow(editor)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && editor.AcceptedPreset is not null)
            viewModel.Lighting.Commit(editor);
    }

    internal void OpenPresetToNpcTask() =>
        MainTasks.SelectedItem = PresetToNpcTab;

    internal void ReportFailureEvidenceUnavailable()
    {
        if (DataContext is WorkspaceShellViewModel viewModel)
            viewModel.ReportFailureEvidenceUnavailable();
    }

    private void OnWorkspaceNavigationRequested(
        object? sender,
        SkyrimWorkspaceNavigationRequest request)
    {
        if (request.Route == SkyrimMainWorkspaceRoute.Lighting)
        {
            OpenLightingEditor();
            return;
        }
        if (request.Route == SkyrimMainWorkspaceRoute.Animation)
        {
            MainTasks.SelectedItem = AnimationsTab;
            return;
        }
        MainTasks.SelectedItem = request.Route switch
        {
            SkyrimMainWorkspaceRoute.EditNpc => EditNpcTab,
            SkyrimMainWorkspaceRoute.EditHeadParts => HeadPartsTab,
            SkyrimMainWorkspaceRoute.EditFace => FaceTab,
            SkyrimMainWorkspaceRoute.EditBody => BodyTab,
            SkyrimMainWorkspaceRoute.EditOutfit => OutfitsTab,
            SkyrimMainWorkspaceRoute.LoadRaceMenuPreset => PresetToNpcTab,
            SkyrimMainWorkspaceRoute.PasteAppearance => PasteAppearanceTab,
            SkyrimMainWorkspaceRoute.CharGenOptions => CharGenOptionsTab,
            SkyrimMainWorkspaceRoute.BuildCharGen => FaceGenTab,
            SkyrimMainWorkspaceRoute.SavePackage => SavePackageTab,
            _ => NpcWorkspaceTab
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (startupCloseGate.TryDeferClose())
        {
            e.Cancel = true;
            return;
        }

        if (DataContext is WorkspaceShellViewModel { HasActiveOperation: true } viewModel)
        {
            e.Cancel = true;
            viewModel.ReportCloseBlocked();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is WorkspaceShellViewModel viewModel)
        {
            viewModel.WorkspaceNavigationRequested -=
                OnWorkspaceNavigationRequested;
            viewModel.Dispose();
        }
        base.OnClosed(e);
    }
}
