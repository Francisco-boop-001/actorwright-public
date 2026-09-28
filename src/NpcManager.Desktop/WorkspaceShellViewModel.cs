using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

public sealed class WorkspaceShellViewModel : INotifyPropertyChanged, IDisposable
{
    private const string CloseBlockedMessage =
        "A task is active. Complete or cancel it before closing NPC Studio.";

    public WorkspaceShellViewModel(
        ReviewedGameIntakeViewModel preflight,
        IBlankNpcBuildService blankNpcBuildService,
        BlankNpcBuildRequest initialCreateRequest,
        RaceMenuNpcBuildViewModel raceMenuNpc,
        ExistingNpcEditViewModel existingNpcEdit,
        SkyrimHeadPartEditViewModel headPartEdit,
        SkyrimFaceEditWorkspaceViewModel faceEdit,
        SkyrimBodyEditWorkspaceViewModel bodyEdit,
        SkyrimSelectiveAppearancePasteWorkspaceViewModel selectiveAppearancePaste,
        SkyrimOutfitProductionWorkspaceViewModel outfitProduction,
        SkyrimLeveledListProductionWorkspaceViewModel leveledListProduction,
        SkyrimArmorProductionWorkspaceViewModel armorProduction,
        SkyrimCharGenOptionsProductionWorkspaceViewModel charGenOptionsProduction,
        NativeFaceGenBatchViewModel faceGenBatch,
        SkyrimSavePackageViewModel savePackage,
        SkyrimLightingWorkspaceViewModel lighting,
        IPreviewAnimationPickerService? animationPickerService = null,
        SkyrimMainWorkspaceViewModel? mainWorkspace = null,
        ReferencePresetAuthoringViewModel? referencePreset = null,
        SkyrimNpcFinishWizardViewModel? finishCore = null,
        DesktopWorkflowReviewViewModel? workflowReview = null,
        SkyrimNpcVoiceViewModel? voice = null,
        BlankNpcProviderService? blankNpcProviderSelector = null)
    {
        Preflight = preflight;
        Preflight.AcceptedIntakeChanged += OnAcceptedIntakeChanged;
        Preflight.PropertyChanged += OnPreflightPropertyChanged;
        faceGenBatch.ReviewedOutputConsumed += OnFaceGenReviewedOutputConsumed;
        Commands = new ObservableCollection<CommandCatalogItemViewModel>(
            CommandCatalog.All.Select(command => new CommandCatalogItemViewModel(command)));
        AnimationPicker = new AnimationPickerViewModel(animationPickerService);
        CreateNpc = new CreateNpcViewModel(
            blankNpcBuildService, initialCreateRequest, blankNpcProviderSelector);
        RaceMenuNpc = raceMenuNpc;
        ExistingNpcEdit = existingNpcEdit;
        HeadPartEdit = headPartEdit;
        FaceEdit = faceEdit;
        BodyEdit = bodyEdit;
        SelectiveAppearancePaste = selectiveAppearancePaste;
        OutfitProduction = outfitProduction;
        LeveledListProduction = leveledListProduction;
        ArmorProduction = armorProduction;
        CharGenOptionsProduction = charGenOptionsProduction;
        FaceGenBatch = faceGenBatch;
        SavePackage = savePackage;
        Lighting = lighting;
        MainWorkspace = mainWorkspace;
        ReferencePreset = referencePreset;
        FinishCore = finishCore;
        WorkflowReview = workflowReview;
        Voice = voice;
        if (MainWorkspace is not null)
        {
            MainWorkspace.NavigationRequested +=
                OnMainWorkspaceNavigationRequested;
            MainWorkspace.PropertyChanged +=
                OnMainWorkspacePropertyChanged;
        }
        artifactOwners =
        [
            RaceMenuNpc,
            ExistingNpcEdit,
            HeadPartEdit,
            FaceEdit,
            BodyEdit,
            SelectiveAppearancePaste,
            FaceGenBatch,
            SavePackage
        ];
        foreach (ISkyrimMainWorkspaceArtifactOwner owner in artifactOwners)
            owner.ArtifactCommitted += OnChildArtifactCommitted;
        _selectedCommand = Commands.FirstOrDefault();
        Message = Preflight.Status;
        ShowCapabilitiesCommand = new DelegateCommand(() =>
            Message = $"{Commands.Count} command families are available through the CLI.");
        ClearSelectionCommand = new DelegateCommand(() =>
        {
            SelectedCommand = null;
            Message = "Selection cleared. Choose a command family to inspect its contract.";
        });
    }

    public ObservableCollection<CommandCatalogItemViewModel> Commands { get; }

    public ReviewedGameIntakeViewModel Preflight { get; }

    public AnimationPickerViewModel AnimationPicker { get; }

    public CreateNpcViewModel CreateNpc { get; }

    public RaceMenuNpcBuildViewModel RaceMenuNpc { get; }

    public ExistingNpcEditViewModel ExistingNpcEdit { get; }

    public SkyrimHeadPartEditViewModel HeadPartEdit { get; }

    public SkyrimFaceEditWorkspaceViewModel FaceEdit { get; }

    public SkyrimBodyEditWorkspaceViewModel BodyEdit { get; }

    public SkyrimSelectiveAppearancePasteWorkspaceViewModel SelectiveAppearancePaste { get; }

    public SkyrimOutfitProductionWorkspaceViewModel OutfitProduction { get; }

    public SkyrimLeveledListProductionWorkspaceViewModel LeveledListProduction { get; }

    public SkyrimArmorProductionWorkspaceViewModel ArmorProduction { get; }

    public SkyrimCharGenOptionsProductionWorkspaceViewModel CharGenOptionsProduction { get; }

    public NativeFaceGenBatchViewModel FaceGenBatch { get; }

    public SkyrimSavePackageViewModel SavePackage { get; }

    public SkyrimLightingWorkspaceViewModel Lighting { get; }

    public SkyrimMainWorkspaceViewModel? MainWorkspace { get; }

    public ReferencePresetAuthoringViewModel? ReferencePreset
    {
        get;
    }

    public SkyrimNpcFinishWizardViewModel? FinishCore { get; }

    public DesktopWorkflowReviewViewModel? WorkflowReview { get; }

    public SkyrimNpcVoiceViewModel? Voice { get; }

    public ReviewedGameIntake? ReviewedIntake { get; private set; }

    public bool HasReviewedIntake => ReviewedIntake is not null;

    public ICommand ShowCapabilitiesCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    internal bool HasActiveOperation =>
        Preflight.IsBusy || CreateNpc.IsBusy || RaceMenuNpc.IsBusy || ExistingNpcEdit.IsBusy ||
        HeadPartEdit.IsBusy ||
        FaceEdit.IsBusy ||
        BodyEdit.IsBusy ||
        SelectiveAppearancePaste.IsBusy ||
        OutfitProduction.IsBusy ||
        LeveledListProduction.IsBusy ||
        ArmorProduction.IsBusy ||
        CharGenOptionsProduction.IsBusy ||
        FaceGenBatch.IsBusy || SavePackage.IsBusy || Lighting.IsBusy ||
        MainWorkspace?.HasActiveOperation == true ||
        ReferencePreset?.IsBusy == true || FinishCore?.IsBusy == true ||
        WorkflowReview?.IsBusy == true || Voice?.IsBusy == true;

    public event EventHandler<SkyrimWorkspaceNavigationRequest>?
        WorkspaceNavigationRequested;

    private readonly IReadOnlyList<ISkyrimMainWorkspaceArtifactOwner>
        artifactOwners;

    internal void ReportCloseBlocked() =>
        Message = CloseBlockedMessage;

    internal void ReportFailureEvidenceUnavailable() =>
        Message = "Desktop failure evidence could not be safely saved.";

    private void OnMainWorkspacePropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (MainWorkspace is null)
            return;

        if (e.PropertyName ==
            nameof(SkyrimMainWorkspaceViewModel.Status))
        {
            Message = MainWorkspace.Status;
            return;
        }

        if (e.PropertyName !=
                nameof(SkyrimMainWorkspaceViewModel.HasActiveOperation) ||
            MainWorkspace.HasActiveOperation ||
            !string.Equals(
                Message,
                CloseBlockedMessage,
                StringComparison.Ordinal))
        {
            return;
        }

        Message = MainWorkspace.Status;
    }

    private CommandCatalogItemViewModel? _selectedCommand;
    public CommandCatalogItemViewModel? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            if (ReferenceEquals(_selectedCommand, value)) return;
            _selectedCommand = value;
            OnPropertyChanged();
            Message = value is null ? "Ready" : $"Selected {value.Name}.";
        }
    }

    private string _message = "Ready • choose a command family to inspect its contract.";
    public string Message
    {
        get => _message;
        private set
        {
            if (string.Equals(_message, value, StringComparison.Ordinal)) return;
            _message = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void OnAcceptedIntakeChanged(ReviewedGameIntake? intake)
    {
        await ApplyAcceptedIntakeAsync(intake);
    }

    internal async Task ApplyAcceptedIntakeAsync(
        ReviewedGameIntake? intake)
    {
        ReviewedIntake = intake;
        OnPropertyChanged(nameof(ReviewedIntake));
        OnPropertyChanged(nameof(HasReviewedIntake));
        if (intake is not null)
        {
            ReferencePreset?.ApplyReviewedIntake(intake);
            FaceGenBatch.ApplyReviewedIntake(intake);
            RaceMenuNpc.ApplyReviewedIntake(intake);
            FinishCore?.ApplyReviewedIntake(intake);
            HeadPartEdit.ApplyReviewedIntake(intake);
            FaceEdit.ApplyReviewedIntake(intake);
            BodyEdit.ApplyReviewedIntake(intake);
            SelectiveAppearancePaste.ApplyReviewedIntake(intake);
            OutfitProduction.ApplyReviewedIntake(intake);
            LeveledListProduction.ApplyReviewedIntake(intake);
            ArmorProduction.ApplyReviewedIntake(intake);
            Message = $"Reviewed workspace ready: {intake.Plugins.Length} plugins.";
            if (MainWorkspace is not null)
            {
                await MainWorkspace.ApplyReviewedIntakeAsync(intake);
                Message = MainWorkspace.Status;
            }
        }
        else
        {
            ReferencePreset?.ApplyReviewedIntake(null);
            MainWorkspace?.ClearReviewedIntake();
            FaceGenBatch.ClearReviewedIntake();
            RaceMenuNpc.ClearReviewedIntake();
            FinishCore?.ApplyReviewedIntake(null);
            HeadPartEdit.ClearReviewedIntake();
            FaceEdit.ClearReviewedIntake();
            BodyEdit.ClearReviewedIntake();
            SelectiveAppearancePaste.ClearReviewedIntake();
            OutfitProduction.ClearReviewedIntake();
            LeveledListProduction.ClearReviewedIntake();
            ArmorProduction.ClearReviewedIntake();
            Message = "Workspace review is stale. Review again before opening an NPC task.";
        }
    }

    private void OnFaceGenReviewedOutputConsumed()
    {
        Preflight.InvalidateConsumedOutput();
        Message = "FaceGen output retained. Choose a new future output and review the workspace before another write.";
    }

    private void OnPreflightPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (string.Equals(eventArgs.PropertyName, nameof(ReviewedGameIntakeViewModel.Status), StringComparison.Ordinal))
        {
            Message = Preflight.Status;
        }
    }

    private void OnMainWorkspaceNavigationRequested(
        object? sender,
        SkyrimWorkspaceNavigationRequest request)
    {
        if (MainWorkspace is null ||
            !MainWorkspace.TryGetRecord(
                request.Identity,
                out SkyrimMainWorkspaceRecord? record) ||
            record is null ||
            record.Kind != SkyrimMainWorkspaceRecordKind.Npc ||
            record.IsSourceDeleted)
        {
            Message =
                "Workspace route refused because the exact live NPC identity is stale.";
            return;
        }
        PluginClosureReviewEntry? provider =
            ReviewedIntake?.Plugins.SingleOrDefault(item =>
                item.Enabled &&
                item.Exists &&
                item.ReadSucceeded &&
                item.Plugin ==
                    request.Identity.WinningProvider);
        if (provider?.SourceHash is null ||
            !string.Equals(
                provider.Path.Value,
                request.SourcePlugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(provider.Path.Value) ||
            HashFile(provider.Path) != provider.SourceHash.Value)
        {
            Message =
                "Workspace route refused because the reviewed winning provider changed.";
            return;
        }

        switch (request.Route)
        {
            case SkyrimMainWorkspaceRoute.EditNpc:
                Bind(ExistingNpcEdit, request.Identity);
                ExistingNpcEdit.InputPlugin =
                    provider.Path.Value;
                ExistingNpcEdit.InputSha256 =
                    provider.SourceHash.Value.Value;
                ExistingNpcEdit.NpcFormId =
                    request.Identity.FormId.ToString();
                break;
            case SkyrimMainWorkspaceRoute.EditHeadParts:
                Bind(HeadPartEdit, request.Identity);
                HeadPartEdit.SourcePlugin =
                    provider.Plugin.Value;
                HeadPartEdit.NpcFormId =
                    request.Identity.FormId.ToString();
                break;
            case SkyrimMainWorkspaceRoute.EditFace:
                Bind(FaceEdit, request.Identity);
                FaceEdit.SourcePlugin =
                    provider.Plugin.Value;
                FaceEdit.NpcFormId =
                    request.Identity.FormId.ToString();
                break;
            case SkyrimMainWorkspaceRoute.EditBody:
                Bind(BodyEdit, request.Identity);
                BodyEdit.SourcePlugin =
                    provider.Plugin.Value;
                BodyEdit.NpcFormId =
                    request.Identity.FormId.ToString();
                break;
            case SkyrimMainWorkspaceRoute.LoadRaceMenuPreset:
                Bind(RaceMenuNpc, request.Identity);
                RaceMenuNpc.ApplyWorkspaceExistingNpcTarget(
                    provider.Path,
                    provider.SourceHash.Value,
                    request.Identity.FormId);
                break;
            case SkyrimMainWorkspaceRoute.PasteAppearance:
                Bind(SelectiveAppearancePaste, request.Identity);
                SelectiveAppearancePaste.TargetPlugin =
                    provider.Plugin.Value;
                SelectiveAppearancePaste.TargetNpcFormId =
                    request.Identity.FormId.ToString();
                PrefillAppearanceSource();
                break;
            case SkyrimMainWorkspaceRoute.CharGenOptions:
                CharGenOptionsProduction.DataRoot =
                    ReviewedIntake!.DataRoot.Value;
                CharGenOptionsProduction.PluginOrder =
                    string.Join(
                        Environment.NewLine,
                        ReviewedIntake.Plugins
                            .Where(item =>
                                item.Enabled &&
                                item.Exists &&
                                item.ReadSucceeded)
                            .OrderBy(item => item.Order)
                            .Select(item => item.Plugin.Value));
                CharGenOptionsProduction.NpcReference =
                    request.Identity.OwnerPlugin.Value + "|" +
                    request.Identity.FormId;
                break;
            case SkyrimMainWorkspaceRoute.BuildCharGen:
                Bind(FaceGenBatch, request.Identity);
                FaceGenBatch.DataRoot =
                    ReviewedIntake!.DataRoot.Value;
                FaceGenBatch.PluginOrderText =
                    string.Join(
                        Environment.NewLine,
                        ReviewedIntake.Plugins
                            .Where(item =>
                                item.Enabled &&
                                item.Exists &&
                                item.ReadSucceeded)
                            .OrderBy(item => item.Order)
                            .Select(item => item.Plugin.Value));
                FaceGenBatch.WinningPlugin =
                    request.Identity.WinningProvider.Value;
                break;
            case SkyrimMainWorkspaceRoute.SavePackage:
                Bind(SavePackage, request.Identity);
                if (request.Artifact is { } artifact)
                    SavePackage.SourceRoot =
                        ResolvePackageRoot(artifact.Path);
                break;
        }
        WorkspaceNavigationRequested?.Invoke(this, request);
        Message =
            $"Opened {request.Route} for {request.Identity.OwnerPlugin.Value}|" +
            request.Identity.FormId + ".";
    }

    private void PrefillAppearanceSource()
    {
        if (MainWorkspace?.CopiedAppearanceIdentity is not { } copied ||
            !MainWorkspace.TryGetRecord(
                copied,
                out SkyrimMainWorkspaceRecord? sourceRecord) ||
            sourceRecord is null ||
            !MainWorkspace.TryGetArtifact(
                copied,
                out SkyrimMainWorkspaceArtifactHandoff? artifact) ||
            artifact is null)
            return;
        WorkspacePath? preset =
            artifact.ProposalPath is { } proposal &&
            string.Equals(
                Path.GetExtension(proposal.Value),
                ".jslot",
                StringComparison.OrdinalIgnoreCase)
                ? proposal
                : string.Equals(
                    Path.GetExtension(artifact.Path.Value),
                    ".jslot",
                    StringComparison.OrdinalIgnoreCase)
                    ? artifact.Path
                    : null;
        if (preset is null)
            return;
        SelectiveAppearancePaste.SourcePlugin =
            sourceRecord.Identity.WinningProvider.Value;
        SelectiveAppearancePaste.SourceNpcFormId =
            sourceRecord.Identity.FormId.ToString();
        SelectiveAppearancePaste.SourcePreset =
            preset.Value.Value;
    }

    private async void OnChildArtifactCommitted(
        object? sender,
        SkyrimMainWorkspaceArtifactHandoff handoff)
    {
        if (MainWorkspace is null)
            return;
        try
        {
            bool accepted =
                await MainWorkspace.AcceptArtifactHandoffAsync(
                    handoff);
            Message = accepted
                ? $"Verified {handoff.Kind} returned to the NPC workspace."
                : $"Refused stale {handoff.Kind} child handoff.";
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            Message =
                $"Child handoff could not be rehashed: {exception.Message}";
        }
    }

    private static void Bind(
        ISkyrimMainWorkspaceArtifactOwner owner,
        SkyrimMainWorkspaceIdentity identity) =>
        owner.BindWorkspaceIdentity(identity);

    private static Sha256Hash HashFile(WorkspacePath path)
    {
        using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string ResolvePackageRoot(WorkspacePath path)
    {
        string? directory = Path.GetDirectoryName(path.Value);
        if (directory is null)
            return path.Value;
        if (string.Equals(
                Path.GetFileName(path.Value),
                "npcmanager-package.json",
                StringComparison.OrdinalIgnoreCase))
            return directory;
        if (string.Equals(
                Path.GetFileName(directory),
                "Data",
                StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(directory) ?? directory;
        return directory;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (MainWorkspace is not null)
        {
            MainWorkspace.NavigationRequested -=
                OnMainWorkspaceNavigationRequested;
            MainWorkspace.PropertyChanged -=
                OnMainWorkspacePropertyChanged;
            MainWorkspace.Dispose();
        }
        foreach (ISkyrimMainWorkspaceArtifactOwner owner in artifactOwners)
            owner.ArtifactCommitted -= OnChildArtifactCommitted;
        Preflight.AcceptedIntakeChanged -= OnAcceptedIntakeChanged;
        Preflight.PropertyChanged -= OnPreflightPropertyChanged;
        FaceGenBatch.ReviewedOutputConsumed -= OnFaceGenReviewedOutputConsumed;
        Preflight.Dispose();
        CreateNpc.Dispose();
        RaceMenuNpc.Dispose();
        ExistingNpcEdit.Dispose();
        HeadPartEdit.Dispose();
        FaceEdit.Dispose();
        BodyEdit.Dispose();
        SelectiveAppearancePaste.Dispose();
        OutfitProduction.Dispose();
        LeveledListProduction.Dispose();
        ArmorProduction.Dispose();
        CharGenOptionsProduction.Dispose();
        FaceGenBatch.Dispose();
        SavePackage.Dispose();
        ReferencePreset?.Dispose();
        FinishCore?.Dispose();
        Voice?.Dispose();
    }

    public sealed class CommandCatalogItemViewModel(CommandDescriptor descriptor)
    {
        public string Name { get; } = descriptor.Name;
        public string Description { get; } = descriptor.Description;
        public string Games { get; } = string.Join(" · ", descriptor.SupportedGames.Select(game => game.ToWireName()));
        public string MutationState { get; } = descriptor.Mutates ? "Writes artifacts" : "Read-only";
        public string MutationBadge { get; } = descriptor.Mutates ? "WRITE" : "READ";
        public string Limitation { get; } = descriptor.Limitations.Length == 0
            ? "No additional limitations declared."
            : string.Join(" ", descriptor.Limitations);
    }

    private sealed class DelegateCommand(Action action) : ICommand
    {
        event EventHandler? ICommand.CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
