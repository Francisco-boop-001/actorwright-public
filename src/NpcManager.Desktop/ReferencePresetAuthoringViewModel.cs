using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

public enum ReferencePresetDesktopStep
{
    Empty,
    IntakeRequired,
    Defining,
    Analyzing,
    ReviewRequired,
    ResourceChoice,
    Comparing,
    ProposalReady,
    Writing,
    Cancelled,
    RecoverableError,
    Stale,
    Completed,
    DownstreamHandoff
}

public sealed record ReferencePresetAnchorReviewDraft(
    ReferenceSemanticAnchor Anchor,
    bool DecisionRecorded);

public sealed record ReferencePresetViewReviewDraft(
    string ImageId,
    ReferenceImageViewRole ViewRole,
    Sha256Hash InferenceSha256,
    double DetectorScore,
    double ReviewedYawDegrees,
    bool ReviewAccepted,
    ImmutableArray<ReferencePresetAnchorReviewDraft> Anchors);

public sealed record ReferencePresetNpcDesktopHandoff(
    VerifiedReferencePreset Preset,
    ReviewedGameIntake ReviewedIntake);

public sealed record ReferencePresetDesktopRecovery(
    ReviewedGameIntake ReviewedIntake,
    WorkspacePath IntakePath,
    Sha256Hash IntakeSha256,
    WorkspacePath InferenceProposalPath,
    Sha256Hash InferenceProposalSha256);

public sealed record ReferencePresetImageInputDraft(
    string ImageId,
    WorkspacePath SourcePath,
    ReferenceImageViewRole ViewRole);

public sealed record ReferencePresetBaselinePreview(
    ReferenceImageViewRole ViewRole,
    WorkspacePath ImagePath,
    Sha256Hash PngSha256,
    Sha256Hash RenderInputSha256);

public sealed record ReferencePresetComparisonView(
    string Label,
    WorkspacePath ImagePath,
    Sha256Hash ContentSha256,
    Sha256Hash? SourceReferenceSha256);

/// <summary>
/// Cancellable presentation state over the same hash-bound transaction used
/// by the CLI. Mutable WPF state never becomes authoring authority: every
/// transition commits a new immutable application document.
/// </summary>
public sealed class ReferencePresetAuthoringViewModel
    : INotifyPropertyChanged, IDisposable
{
    private readonly IReferencePresetAuthoringTransaction transaction;
    private readonly IReferencePresetSessionService sessions;
    private readonly IReferencePresetResourceSnapshotService resources;
    private readonly IReferencePresetRenderInputBuilder renderInputs;
    private readonly IReferencePresetMeshAnchorBinder meshBinder;
    private readonly IReferenceSemanticLandmarkProjector projector;
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath sessionParent;
    private readonly Func<ReferencePresetNpcDesktopHandoff, bool>?
        continueToNpc;
    private readonly IDisposable? serviceLifetime;
    private readonly IReferencePresetCpuRenderer? baselineRenderer;
    private readonly AsyncCommand analyzeCommand;
    private readonly AsyncCommand advanceCommand;
    private readonly DelegateCommand backCommand;
    private readonly AsyncCommand applyCommand;
    private readonly DelegateCommand continueToNpcCommand;
    private readonly DelegateCommand cancelCommand;
    private readonly DelegateCommand defineCommand;
    private CancellationTokenSource? cancellation;
    private long generation;
    private long operationOrdinal;
    private long activeOperation;
    private bool disposed;
    private WorkspacePath? runRoot;
    private WorkspacePath? intakePath;
    private WorkspacePath? inferenceProposalPath;
    private WorkspacePath? reviewedDesignPath;
    private WorkspacePath? resourceSnapshotPath;
    private WorkspacePath? proposalOutputRoot;
    private WorkspacePath? acceptedOutputRoot;
    private Sha256Hash? intakeSha256;
    private Sha256Hash? inferenceProposalSha256;
    private Sha256Hash? reviewedDesignSha256;
    private Sha256Hash? resourceSnapshotSha256;

    public ReferencePresetAuthoringViewModel(
        IReferencePresetAuthoringTransaction transaction,
        IReferencePresetSessionService sessions,
        IReferencePresetResourceSnapshotService resources,
        IReferencePresetRenderInputBuilder renderInputs,
        IReferencePresetMeshAnchorBinder meshBinder,
        IReferenceSemanticLandmarkProjector projector,
        WorkspacePath labRoot,
        WorkspacePath sessionParent,
        Func<ReferencePresetNpcDesktopHandoff, bool>?
            continueToNpc = null,
        IDisposable? serviceLifetime = null,
        IReferencePresetCpuRenderer? baselineRenderer = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(renderInputs);
        ArgumentNullException.ThrowIfNull(meshBinder);
        ArgumentNullException.ThrowIfNull(projector);
        if (!sessionParent.IsUnder(labRoot) ||
            sessionParent == labRoot)
        {
            throw new ArgumentException(
                "Reference-preset desktop sessions must remain below the admitted lab root.",
                nameof(sessionParent));
        }

        this.transaction = transaction;
        this.sessions = sessions;
        this.resources = resources;
        this.renderInputs = renderInputs;
        this.meshBinder = meshBinder;
        this.projector = projector;
        this.labRoot = labRoot;
        this.sessionParent = sessionParent;
        this.continueToNpc = continueToNpc;
        this.serviceLifetime = serviceLifetime;
        this.baselineRenderer = baselineRenderer;
        analyzeCommand = new AsyncCommand(
            AnalyzeAsync,
            () => CanAnalyze);
        advanceCommand = new AsyncCommand(
            AdvanceAsync,
            () => CanAdvance);
        backCommand = new DelegateCommand(
            Back,
            () => CanGoBack);
        applyCommand = new AsyncCommand(
            ApplyAsync,
            () => CanApply);
        continueToNpcCommand = new DelegateCommand(
            ContinueToNpc,
            () => CanContinueToNpc);
        cancelCommand = new DelegateCommand(
            Cancel,
            () => IsBusy && cancellation is not null);
        defineCommand = new DelegateCommand(
            CommitDefinition,
            () => ReviewedIntake is not null && !IsBusy);
    }

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ObservableCollection<ReferencePresetImageInputDraft>
        ReferenceImages { get; } = [];

    public ImmutableArray<ReferenceImageViewRole> ImageViewRoles
        { get; } =
        Enum.GetValues<ReferenceImageViewRole>()
            .ToImmutableArray();

    public ReferenceImageViewRole NewImageViewRole
    {
        get => newImageViewRole;
        set => Set(ref newImageViewRole, value);
    }
    private ReferenceImageViewRole newImageViewRole =
        ReferenceImageViewRole.Front;

    public ImmutableArray<NpcSex> SexOptions { get; } =
        [NpcSex.Female, NpcSex.Male];

    public string ProjectId
    {
        get => projectId;
        set => Set(ref projectId, value);
    }
    private string projectId = "reference-preset";

    public string TargetName
    {
        get => targetName;
        set => Set(ref targetName, value);
    }
    private string targetName = string.Empty;

    public string RaceReference
    {
        get => raceReference;
        set => Set(ref raceReference, value);
    }
    private string raceReference =
        "Skyrim.esm|0x00013746";

    public NpcSex TargetSex
    {
        get => targetSex;
        set => Set(ref targetSex, value);
    }
    private NpcSex targetSex = NpcSex.Female;

    public float TargetWeight
    {
        get => targetWeight;
        set => Set(ref targetWeight, value);
    }
    private float targetWeight = 50.0F;

    public string HeadSystemId
    {
        get => headSystemId;
        set => Set(ref headSystemId, value);
    }
    private string headSystemId = "high-poly-head";

    public string BaselineJslotPath
    {
        get => baselineJslotPath;
        set => Set(ref baselineJslotPath, value);
    }
    private string baselineJslotPath = string.Empty;

    public string Description
    {
        get => description;
        set => Set(ref description, value);
    }
    private string description = string.Empty;

    public string FaceReference
    {
        get => faceReference;
        set => Set(ref faceReference, value);
    }
    private string faceReference = string.Empty;

    public string MouthReference
    {
        get => mouthReference;
        set => Set(ref mouthReference, value);
    }
    private string mouthReference = string.Empty;

    public string EyesReference
    {
        get => eyesReference;
        set => Set(ref eyesReference, value);
    }
    private string eyesReference = string.Empty;

    public string BrowsReference
    {
        get => browsReference;
        set => Set(ref browsReference, value);
    }
    private string browsReference = string.Empty;

    public string HairReference
    {
        get => hairReference;
        set => Set(ref hairReference, value);
    }
    private string hairReference = string.Empty;

    public ReferencePresetDesktopStep CurrentStep
    {
        get => currentStep;
        private set
        {
            if (!Set(ref currentStep, value))
                return;
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanAdvance));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanContinueToNpc));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(StepNumber));
            OnPropertyChanged(nameof(VisibleStepIndex));
            RaiseCommands();
        }
    }
    private ReferencePresetDesktopStep currentStep =
        ReferencePresetDesktopStep.Empty;

    public int StepNumber => CurrentStep switch
    {
        ReferencePresetDesktopStep.Empty or
        ReferencePresetDesktopStep.IntakeRequired or
        ReferencePresetDesktopStep.Defining or
        ReferencePresetDesktopStep.Stale => 1,
        ReferencePresetDesktopStep.Analyzing => 2,
        ReferencePresetDesktopStep.ReviewRequired => 3,
        ReferencePresetDesktopStep.ResourceChoice => 4,
        ReferencePresetDesktopStep.Comparing or
        ReferencePresetDesktopStep.ProposalReady => 5,
        ReferencePresetDesktopStep.Writing or
        ReferencePresetDesktopStep.Completed or
        ReferencePresetDesktopStep.DownstreamHandoff => 6,
        _ => 1
    };

    public int VisibleStepIndex => StepNumber - 1;

    public ReviewedGameIntake? ReviewedIntake { get; private set; }

    public ReferencePresetIntake? Intake { get; private set; }

    public LandmarkInferenceProposal? InferenceProposal
    {
        get;
        private set;
    }

    public Sha256Hash? InferenceProposalSha256 =>
        inferenceProposalSha256;

    public ReviewedReferencePresetDesign? ReviewedDesign
    {
        get;
        private set;
    }

    public ReferencePresetResourceSnapshot? ResourceSnapshot
    {
        get;
        private set;
    }

    public ReferencePresetRenderInput? RenderInput { get; private set; }

    public ReferencePresetAuthoringProposal? AuthoringProposal
    {
        get;
        private set;
    }

    public Sha256Hash? AuthoringProposalSha256
    {
        get;
        private set;
    }

    public VerifiedReferencePreset? VerifiedPreset
    {
        get;
        private set;
    }

    public ImmutableArray<ReferencePresetViewReviewDraft> ReviewViews
    {
        get;
        private set;
    } = [];

    public ImmutableArray<ReferenceDescriptionTrait> ReviewTraits
    {
        get;
        private set;
    } = [];

    public ImmutableArray<ReferenceUnknown> ReviewUnknowns
    {
        get;
        private set;
    } = [];

    public ReferencePresetCatalogSelection? CatalogSelection
    {
        get;
        private set;
    }

    public ReferencePresetViewReviewDraft? SelectedReviewView
    {
        get => selectedReviewView;
        set
        {
            if (!Set(ref selectedReviewView, value))
                return;
            SelectedReviewAnchor =
                value?.Anchors.FirstOrDefault();
            OnPropertyChanged(
                nameof(SelectedReferenceImagePath));
            SelectMatchingBaselinePreview();
        }
    }
    private ReferencePresetViewReviewDraft? selectedReviewView;

    public string SelectedReferenceImagePath =>
        Intake?.Images.SingleOrDefault(item =>
                item.ViewRole ==
                    SelectedReviewView?.ViewRole)
            ?.SourcePath.Value ??
        string.Empty;

    public ReferencePresetAnchorReviewDraft? SelectedReviewAnchor
    {
        get => selectedReviewAnchor;
        set => Set(ref selectedReviewAnchor, value);
    }
    private ReferencePresetAnchorReviewDraft?
        selectedReviewAnchor;

    public ImmutableArray<ReferencePresetBaselinePreview>
        BaselinePreviews
    {
        get;
        private set;
    } = [];

    public ReferencePresetBaselinePreview? SelectedBaselinePreview
    {
        get => selectedBaselinePreview;
        private set
        {
            if (!Set(ref selectedBaselinePreview, value))
                return;
            OnPropertyChanged(
                nameof(SelectedBaselinePreviewPath));
        }
    }
    private ReferencePresetBaselinePreview?
        selectedBaselinePreview;

    public string SelectedBaselinePreviewPath =>
        SelectedBaselinePreview?.ImagePath.Value ??
        string.Empty;

    public ImmutableArray<ReferencePresetComparisonView>
        ComparisonViews
    {
        get;
        private set;
    } = [];

    public string ComparisonSummary =>
        AuthoringProposal is null
            ? "No deterministic comparison has been produced."
            : $"{AuthoringProposal.SolverResult.Residuals.Length} residuals, " +
              $"{AuthoringProposal.Losses.Length} explicit losses, " +
              $"objective {AuthoringProposal.SolverResult.Objective:R}.";

    public string VerifiedPresetPath =>
        VerifiedPreset?.PresetPath.Value ?? string.Empty;

    public string VerifiedPresetHash =>
        VerifiedPreset?.PresetSha256.Value ?? string.Empty;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value))
                return;
            OnPropertyChanged(nameof(BlocksClose));
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanAdvance));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanContinueToNpc));
            OnPropertyChanged(nameof(CanGoBack));
            RaiseCommands();
        }
    }
    private bool isBusy;

    public bool BlocksClose => IsBusy;

    public string ProgressText
    {
        get => progressText;
        private set => Set(ref progressText, value);
    }
    private string progressText =
        "Open and review a copied Skyrim workspace to begin.";

    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }
    private int progressPercent;

    public string FocusTarget
    {
        get => focusTarget;
        private set => Set(ref focusTarget, value);
    }
    private string focusTarget = "Open copied Skyrim workspace";

    public bool CanAnalyze =>
        !IsBusy &&
        ReviewedIntake is not null &&
        Intake is not null &&
        CurrentStep is
            ReferencePresetDesktopStep.Defining or
            ReferencePresetDesktopStep.Stale or
            ReferencePresetDesktopStep.Cancelled or
            ReferencePresetDesktopStep.RecoverableError;

    public bool CanAdvance
    {
        get
        {
            if (IsBusy ||
                InferenceProposalSha256 is null)
            {
                return false;
            }
            if (CurrentStep ==
                    ReferencePresetDesktopStep.ReviewRequired &&
                ReviewedDesign is null)
            {
                return ReviewDecisionsComplete;
            }
            if (ReviewedDesign is null)
                return false;
            ImmutableArray<Diagnostic> diagnostics =
                CurrentStep ==
                    ReferencePresetDesktopStep.ResourceChoice
                    ? ResourceSnapshot is null
                        ? ReferencePresetAuthoringRules
                            .ValidateReviewedDesignForResourceSnapshot(
                                ReviewedDesign,
                                InferenceProposalSha256.Value)
                        : ReferencePresetAuthoringRules.CanSolve(
                            ReviewedDesign,
                            InferenceProposalSha256.Value)
                    : ReferencePresetAuthoringRules
                        .ValidateReviewedDesignForResourceSnapshot(
                            ReviewedDesign,
                            InferenceProposalSha256.Value);
            return CurrentStep is
                    ReferencePresetDesktopStep.ReviewRequired or
                    ReferencePresetDesktopStep.ResourceChoice &&
                !HasErrors(diagnostics);
        }
    }

    public bool CanApply =>
        !IsBusy &&
        CurrentStep ==
            ReferencePresetDesktopStep.ProposalReady &&
        AuthoringProposal is not null &&
        AuthoringProposalSha256 is not null;

    public bool CanContinueToNpc =>
        !IsBusy &&
        CurrentStep ==
            ReferencePresetDesktopStep.Completed &&
        VerifiedPreset is not null &&
        ReviewedIntake is not null;

    public bool CanGoBack =>
        !IsBusy &&
        CurrentStep is
            ReferencePresetDesktopStep.ReviewRequired or
            ReferencePresetDesktopStep.ResourceChoice or
            ReferencePresetDesktopStep.ProposalReady or
            ReferencePresetDesktopStep.Completed or
            ReferencePresetDesktopStep.RecoverableError or
            ReferencePresetDesktopStep.Cancelled;

    public ICommand AnalyzeCommand => analyzeCommand;
    public ICommand AdvanceCommand => advanceCommand;
    public ICommand BackCommand => backCommand;
    public ICommand ApplyCommand => applyCommand;
    public ICommand ContinueToNpcCommand => continueToNpcCommand;
    public ICommand CancelCommand => cancelCommand;
    public ICommand DefineCommand => defineCommand;

    public bool ReviewDecisionsComplete =>
        !ReviewViews.IsDefaultOrEmpty &&
        ReviewViews.All(view =>
            view.ReviewAccepted &&
            view.Anchors.All(anchor =>
                anchor.DecisionRecorded)) &&
        ReviewTraits.All(trait =>
            trait.ReviewState !=
                ReferenceTraitReviewState.Proposed) &&
        ReviewUnknowns.All(unknown =>
            unknown.ReviewState !=
                ReferenceUnknownReviewState.Unreviewed);

    public string BaselineBindingProgressText
    {
        get
        {
            ReviewedReferencePresetDesign? design =
                ReviewedDesign;
            if (design is null)
                return "0 of 0 required anchors are bound.";
            int required = design.Views
                .SelectMany(item => item.Anchors)
                .Count(item => item.Required);
            int bound = design.MeshBindings
                .Count(binding =>
                    design.Views.Any(view =>
                        view.ViewRole == binding.ViewRole &&
                        view.Anchors.Any(anchor =>
                            anchor.Anchor == binding.Anchor &&
                            anchor.Required)));
            return $"{bound} of {required} required anchors are bound.";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool AddReferenceImage(
        WorkspacePath sourcePath,
        ReferenceImageViewRole viewRole)
    {
        ThrowIfDisposed();
        if (!sourcePath.IsUnder(labRoot) ||
            !File.Exists(sourcePath.Value) ||
            ReferenceImages.Count >=
                ReferencePresetAuthoringRules.MaximumImageCount ||
            ReferenceImages.Any(item =>
                item.SourcePath == sourcePath ||
                item.ViewRole == viewRole))
        {
            return false;
        }
        string imageId = viewRole.ToWireName();
        ReferenceImages.Add(
            new ReferencePresetImageInputDraft(
                imageId,
                sourcePath,
                viewRole));
        return true;
    }

    public bool RemoveReferenceImage(
        ReferencePresetImageInputDraft image)
    {
        ThrowIfDisposed();
        return ReferenceImages.Remove(image);
    }

    public void CommitDefinition()
    {
        ThrowIfDisposed();
        Diagnostics.Clear();
        if (ReviewedIntake is null)
        {
            AddDiagnostic(
                "definition-intake: Review the copied Skyrim workspace first.");
            return;
        }
        if (!FormReference.TryParse(
                RaceReference.Trim(),
                out FormReference race))
        {
            AddDiagnostic(
                "definition-race: Use Plugin|0xFormID for the target race.");
            return;
        }
        WorkspacePath baseline;
        try
        {
            baseline = new WorkspacePath(
                BaselineJslotPath.Trim());
        }
        catch (ArgumentException exception)
        {
            AddDiagnostic(
                $"definition-baseline: {exception.Message}");
            return;
        }
        if (!baseline.IsUnder(labRoot) ||
            !File.Exists(baseline.Value) ||
            ReferenceImages.Count is < 1 or >
                ReferencePresetAuthoringRules.MaximumImageCount)
        {
            AddDiagnostic(
                "definition-inputs: Choose one existing K-local baseline JSlot and one through four distinct reference images.");
            return;
        }
        RaceMenuPresetTargetBuildResult target =
            RaceMenuPresetTargetFactory.Create(
                ReviewedIntake,
                race,
                TargetSex);
        AddDiagnostics(target.Diagnostics);
        if (!target.Accepted ||
            target.Target is null)
        {
            return;
        }
        try
        {
            ImmutableArray<ReferenceImageAuthority> images =
                ReferenceImages.Select(image =>
                {
                    var info = new FileInfo(
                        image.SourcePath.Value);
                    return new ReferenceImageAuthority(
                        image.ImageId,
                        image.SourcePath,
                        HashFile(image.SourcePath),
                        info.Length,
                        image.ViewRole);
                }).ToImmutableArray();
            DefineTarget(new ReferencePresetIntake(
                1,
                ProjectId.Trim(),
                TargetName.Trim(),
                race,
                TargetSex,
                TargetWeight,
                HeadSystemId.Trim(),
                baseline,
                HashFile(baseline),
                Description.Normalize(
                    System.Text.NormalizationForm.FormC),
                images,
                target.Target));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            AddDiagnostic(
                $"definition-read: {exception.Message}");
        }
    }

    public void AcceptAllProposedAnchors()
    {
        ThrowIfDisposed();
        ReviewViews = ReviewViews
            .Select(view => view with
            {
                Anchors = view.Anchors
                    .Select(anchor =>
                        new ReferencePresetAnchorReviewDraft(
                            anchor.Anchor with
                            {
                                ReviewState =
                                    ReferenceAnchorReviewState
                                        .Accepted
                            },
                            true))
                    .ToImmutableArray()
            })
            .ToImmutableArray();
        OnPropertyChanged(nameof(ReviewViews));
        SelectedReviewView =
            ReviewViews.FirstOrDefault();
        InvalidateEditedReview();
    }

    public void AcceptAllViews()
    {
        ThrowIfDisposed();
        ReviewViews = ReviewViews
            .Select(view => view with
            {
                ReviewAccepted = true
            })
            .ToImmutableArray();
        OnPropertyChanged(nameof(ReviewViews));
        SelectedReviewView =
            ReviewViews.FirstOrDefault();
        InvalidateEditedReview();
    }

    public void AcceptAllTraits()
    {
        ThrowIfDisposed();
        ReviewTraits = ReviewTraits
            .Select(trait => trait with
            {
                ReviewState =
                    ReferenceTraitReviewState.Accepted,
                ConflictAcknowledged = true
            })
            .ToImmutableArray();
        OnPropertyChanged(nameof(ReviewTraits));
        InvalidateEditedReview();
    }

    public void AcknowledgeAllUnknowns()
    {
        ThrowIfDisposed();
        ReviewUnknowns = ReviewUnknowns
            .Select(unknown => unknown with
            {
                ReviewState =
                    ReferenceUnknownReviewState.Acknowledged
            })
            .ToImmutableArray();
        OnPropertyChanged(nameof(ReviewUnknowns));
        InvalidateEditedReview();
    }

    public bool CommitResourceSelection()
    {
        ThrowIfDisposed();
        string[] values =
        [
            FaceReference,
            MouthReference,
            EyesReference,
            BrowsReference,
            HairReference
        ];
        var parsed = new FormReference[values.Length];
        for (var index = 0;
             index < values.Length;
             index++)
        {
            if (!FormReference.TryParse(
                    values[index].Trim(),
                    out parsed[index]))
            {
                AddDiagnostic(
                    "resource-reference: Face, mouth, eyes, brows, and hair each require Plugin|0xFormID.");
                return false;
            }
        }
        SetCatalogSelection(
            new ReferencePresetCatalogSelection(
                true,
                parsed[0],
                parsed[1],
                parsed[2],
                parsed[3],
                parsed[4],
                []));
        bool accepted = AcceptReview();
        if (accepted)
        {
            CurrentStep =
                ReferencePresetDesktopStep.ResourceChoice;
            FocusTarget = "Close selected Skyrim resources";
            ProgressText =
                "Resource choices are reviewed. Close their exact copied providers and baseline render.";
        }
        return accepted;
    }

    public void ApplyReviewedIntake(ReviewedGameIntake? intake)
    {
        ThrowIfDisposed();
        if (IsBusy)
            InvalidateActiveOperation();
        ReviewedIntake = intake;
        OnPropertyChanged(nameof(ReviewedIntake));
        ClearAllAuthoringState();
        if (intake is null)
        {
            CurrentStep =
                ReferencePresetDesktopStep.IntakeRequired;
            FocusTarget = "Open copied Skyrim workspace";
            ProgressText =
                "Review the copied Data root and plugin order before authoring a preset.";
        }
        else
        {
            CurrentStep = ReferencePresetDesktopStep.Defining;
            FocusTarget = "Reference target name";
            ProgressText =
                "Define the target race, sex, weight, baseline JSlot, description, and reference images.";
        }
        RaiseStateProperties();
    }

    public void DefineTarget(ReferencePresetIntake intake)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(intake);
        bool stale = IsBusy ||
            InferenceProposal is not null ||
            AuthoringProposal is not null ||
            VerifiedPreset is not null;
        InvalidateActiveOperation();
        Intake = intake;
        runRoot = NewRunRoot();
        ClearDerivedState();
        Diagnostics.Clear();
        AddDiagnostics(
            ReferencePresetAuthoringRules.ValidateIntake(intake));
        CurrentStep = stale
            ? ReferencePresetDesktopStep.Stale
            : ReferencePresetDesktopStep.Defining;
        FocusTarget = "Reference target name";
        ProgressText = stale
            ? "Target or source inputs changed. Earlier analysis is stale; analyze again."
            : "Target definition is ready for local analysis.";
        RaiseStateProperties();
    }

    public async Task AnalyzeAsync()
    {
        ThrowIfDisposed();
        if (!CanAnalyze ||
            Intake is null)
        {
            return;
        }

        runRoot = NewRunRoot();
        OperationScope operation = BeginOperation(
            ReferencePresetDesktopStep.Analyzing,
            "Analyzing reference images locally.",
            "Analysis progress");
        try
        {
            Directory.CreateDirectory(runRoot.Value.Value);
            WorkspacePath inputDirectory = new(Path.Combine(
                runRoot.Value.Value, "input"));
            Directory.CreateDirectory(inputDirectory.Value);
            WorkspacePath candidateIntakePath = new(Path.Combine(
                inputDirectory.Value, "authoring-intake.json"));
            ReferencePresetSessionWriteResult intakeWrite =
                await sessions.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        candidateIntakePath,
                        new ReferencePresetSessionDocument(
                            ReferencePresetSessionDocumentKind.Intake,
                            Intake: Intake)),
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            AddDiagnostics(intakeWrite.Diagnostics);
            if (!intakeWrite.Written ||
                intakeWrite.ContentSha256 is null)
            {
                PresentRecoverableError(
                    "The target definition could not be saved for recovery.");
                return;
            }

            WorkspacePath designRoot = new(Path.Combine(
                runRoot.Value.Value, "design"));
            var progress = new InlineProgress<ReferencePresetProgress>(
                item => ReportProgress(operation, item));
            ReferencePresetDesignProposalResult result =
                await transaction.ProposeDesignAsync(
                    new ReferencePresetDesignProposalRequest(
                        Intake,
                        intakeWrite.ContentSha256.Value,
                        designRoot),
                    progress,
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            AddDiagnostics(result.Diagnostics);
            if (!result.Completed ||
                result.Proposal is null ||
                result.ProposalSha256 is null)
            {
                PresentRecoverableError(
                    "Reference analysis was refused. Correct the reported inputs and analyze again.");
                return;
            }

            ImmutableArray<ReferencePresetViewReviewDraft>
                reviewViews =
                    await BuildReviewDraftsAsync(
                        result.Proposal,
                        operation.Token)
                        .ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            intakePath = candidateIntakePath;
            intakeSha256 = intakeWrite.ContentSha256.Value;
            inferenceProposalPath = new WorkspacePath(
                Path.Combine(
                    designRoot.Value,
                    "landmark-proposal.json"));
            inferenceProposalSha256 =
                result.ProposalSha256.Value;
            InferenceProposal = result.Proposal;
            ReviewViews = reviewViews;
            SelectedReviewView =
                reviewViews.FirstOrDefault();
            ReviewTraits = result.Proposal.Traits;
            ReviewUnknowns = result.Proposal.Unknowns;
            CatalogSelection = null;
            ReviewedDesign = null;
            ResourceSnapshot = null;
            RenderInput = null;
            CurrentStep =
                ReferencePresetDesktopStep.ReviewRequired;
            FocusTarget = "Review semantic anchors";
            ProgressPercent = 100;
            ProgressText =
                "Analysis complete. Review every anchor, description trait, uncertainty, and view.";
            RaiseStateProperties();
        }
        catch (OperationCanceledException)
        {
            if (operation.Generation == generation)
                PresentCancelled();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ArgumentException)
        {
            if (IsCurrent(operation))
            {
                AddDiagnostic(
                    $"analysis: {exception.Message}");
                PresentRecoverableError(
                    "Reference analysis stopped without retaining a later authority.");
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void ApplyReviewedDesign(
        ReviewedReferencePresetDesign design)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(design);
        if (InferenceProposalSha256 is null)
            return;
        Diagnostics.Clear();
        ImmutableArray<Diagnostic> validation =
            ReferencePresetAuthoringRules
                .ValidateReviewedDesignForResourceSnapshot(
                    design,
                    InferenceProposalSha256.Value);
        AddDiagnostics(validation);
        if (HasErrors(validation))
        {
            CurrentStep =
                ReferencePresetDesktopStep.ReviewRequired;
            ProgressText =
                "Review remains incomplete. Resolve every reported decision.";
            return;
        }
        ReviewedDesign = design;
        ReviewViews = design.Views.Select(view =>
                new ReferencePresetViewReviewDraft(
                    view.ImageId,
                    view.ViewRole,
                    view.InferenceSha256,
                    view.DetectorScore,
                    view.ReviewedYawDegrees,
                    view.ReviewAccepted,
                    view.Anchors.Select(anchor =>
                            new ReferencePresetAnchorReviewDraft(
                                anchor, true))
                        .ToImmutableArray()))
            .ToImmutableArray();
        SelectedReviewView =
            ReviewViews.FirstOrDefault();
        ReviewTraits = design.Traits;
        ReviewUnknowns = design.Unknowns;
        CatalogSelection = design.CatalogSelection;
        InvalidateSolveArtifacts();
        CurrentStep =
            ReferencePresetDesktopStep.ReviewRequired;
        FocusTarget = "Review semantic anchors";
        ProgressText =
            "Reviewed choices are complete. Continue to close exact Skyrim resources.";
        RaiseStateProperties();
    }

    public bool AcceptReview()
    {
        ThrowIfDisposed();
        if (InferenceProposalSha256 is null ||
            CatalogSelection is null ||
            ReviewViews.IsDefaultOrEmpty ||
            ReviewViews.Any(view =>
                !view.ReviewAccepted ||
                view.Anchors.Any(anchor =>
                    !anchor.DecisionRecorded)) ||
            ReviewTraits.Any(trait =>
                trait.ReviewState ==
                    ReferenceTraitReviewState.Proposed) ||
            ReviewUnknowns.Any(unknown =>
                unknown.ReviewState ==
                    ReferenceUnknownReviewState.Unreviewed))
        {
            ProgressText =
                "Every view, anchor, trait, unknown, and resource choice needs an explicit decision.";
            return false;
        }

        var design = new ReviewedReferencePresetDesign(
            1,
            ReferencePresetAuthorityKind.ReviewedDesign,
            InferenceProposalSha256.Value,
            true,
            ReviewViews.Select(view =>
                    new ReviewedReferenceView(
                        view.ImageId,
                        view.ViewRole,
                        view.InferenceSha256,
                        view.DetectorScore,
                        view.ReviewedYawDegrees,
                        view.ReviewAccepted,
                        view.Anchors
                            .Select(anchor => anchor.Anchor)
                            .ToImmutableArray()))
                .ToImmutableArray(),
            ReviewTraits,
            ReviewUnknowns,
            CatalogSelection,
            ReviewedDesign?.MeshBindings ?? []);
        ApplyReviewedDesign(design);
        return ReviewedDesign is not null;
    }

    public bool CorrectAnchorFromDisplay(
        ReferenceImageViewRole viewRole,
        ReferenceSemanticAnchorKind anchorKind,
        double displayX,
        double displayY,
        double contentLeft,
        double contentTop,
        double contentWidth,
        double contentHeight)
    {
        ThrowIfDisposed();
        if (!TryNormalizeDisplayCoordinate(
                displayX,
                displayY,
                contentLeft,
                contentTop,
                contentWidth,
                contentHeight,
                out double normalizedX,
                out double normalizedY))
        {
            AddDiagnostic(
                "anchor-coordinate: The displayed image coordinate is outside the rendered content.");
            return false;
        }
        return UpdateAnchor(
            viewRole,
            anchorKind,
            source => new ReferencePresetAnchorReviewDraft(
                source.Anchor with
                {
                    X = normalizedX,
                    Y = normalizedY,
                    ReviewState =
                        ReferenceAnchorReviewState.Corrected
                },
                true));
    }

    public bool SetAnchorDecision(
        ReferenceImageViewRole viewRole,
        ReferenceSemanticAnchorKind anchorKind,
        ReferenceAnchorReviewState state) =>
        UpdateAnchor(
            viewRole,
            anchorKind,
            source => new ReferencePresetAnchorReviewDraft(
                source.Anchor with { ReviewState = state },
                true));

    public bool SetViewReview(
        ReferenceImageViewRole viewRole,
        double reviewedYawDegrees,
        bool accepted)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(reviewedYawDegrees) ||
            reviewedYawDegrees is < -90.0 or > 90.0)
        {
            return false;
        }
        int index = FindIndex(ReviewViews, item =>
            item.ViewRole == viewRole);
        if (index < 0)
            return false;
        ReferencePresetViewReviewDraft current =
            ReviewViews[index];
        ReviewViews = ReviewViews.SetItem(
            index,
            current with
            {
                ReviewedYawDegrees = reviewedYawDegrees,
                ReviewAccepted = accepted
            });
        OnPropertyChanged(nameof(ReviewViews));
        InvalidateEditedReview();
        return true;
    }

    public bool SetTraitDecision(
        int index,
        ReferenceTraitReviewState state,
        double strength,
        bool conflictAcknowledged)
    {
        ThrowIfDisposed();
        if (index < 0 ||
            index >= ReviewTraits.Length ||
            !Enum.IsDefined(state) ||
            !double.IsFinite(strength) ||
            strength is < -1.0 or > 1.0)
        {
            return false;
        }
        ReviewTraits = ReviewTraits.SetItem(
            index,
            ReviewTraits[index] with
            {
                ReviewState = state,
                Strength = strength,
                ConflictAcknowledged =
                    conflictAcknowledged
            });
        OnPropertyChanged(nameof(ReviewTraits));
        InvalidateEditedReview();
        return true;
    }

    public bool SetUnknownDecision(
        int index,
        ReferenceUnknownReviewState state)
    {
        ThrowIfDisposed();
        if (index < 0 ||
            index >= ReviewUnknowns.Length ||
            state == ReferenceUnknownReviewState.Unreviewed)
        {
            return false;
        }
        ReviewUnknowns = ReviewUnknowns.SetItem(
            index,
            ReviewUnknowns[index] with
            {
                ReviewState = state
            });
        OnPropertyChanged(nameof(ReviewUnknowns));
        InvalidateEditedReview();
        return true;
    }

    public void SetCatalogSelection(
        ReferencePresetCatalogSelection selection)
    {
        ThrowIfDisposed();
        CatalogSelection = selection;
        OnPropertyChanged(nameof(CatalogSelection));
        InvalidateEditedReview();
    }

    public ReferenceMeshAnchorBindResult BindBaselineAnchor(
        ReferenceImageViewRole viewRole,
        ReferenceSemanticAnchorKind anchorKind,
        string headNifIdentity,
        string headShapeIdentity,
        double normalizedRenderX,
        double normalizedRenderY)
    {
        ThrowIfDisposed();
        if (ReviewedDesign is null ||
            RenderInput is null)
        {
            return BindRefusal(
                "reference-desktop-bind-state",
                "Close compatible resources before binding baseline head points.");
        }
        ReviewedReferenceView? view =
            ReviewedDesign.Views.SingleOrDefault(item =>
                item.ViewRole == viewRole);
        ReferenceSemanticAnchor? anchor =
            view?.Anchors.SingleOrDefault(item =>
                item.Anchor == anchorKind);
        ReferenceRenderShape? shape =
            RenderInput.Shapes.SingleOrDefault(item =>
                string.Equals(
                    item.NifIdentity,
                    headNifIdentity,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.ShapeIdentity,
                    headShapeIdentity,
                    StringComparison.Ordinal));
        ReferenceOrthographicCamera? camera =
            RenderInput.Cameras.SingleOrDefault(item =>
                item.ViewRole == viewRole);
        if (view is null ||
            anchor is null ||
            shape is null ||
            camera is null)
        {
            return BindRefusal(
                "reference-desktop-bind-target",
                "The selected reviewed view, anchor, head shape, or camera is not uniquely available.");
        }
        ReferenceMeshAnchorBindResult result = meshBinder.Bind(
            new ReferenceMeshAnchorBindRequest(
                view,
                anchor,
                RenderInput,
                headNifIdentity,
                headShapeIdentity,
                shape.GeometrySha256,
                camera.CameraSha256,
                RenderInput.InputSha256,
                normalizedRenderX,
                normalizedRenderY)
            {
                ExpectedNifSha256 = shape.NifSha256,
                ExpectedTopologySha256 =
                    shape.TopologySha256,
                ExpectedRestPositionsSha256 =
                    shape.RestPositionsSha256
            });
        AddDiagnostics(result.Diagnostics);
        if (!result.Accepted ||
            result.Binding is null)
        {
            return result;
        }

        ImmutableArray<ReferenceMeshAnchorBinding> bindings =
            ReviewedDesign.MeshBindings
                .Where(item =>
                    item.ViewRole != viewRole ||
                    item.Anchor != anchorKind)
                .Append(result.Binding)
                .OrderBy(item => item.ViewRole)
                .ThenBy(item => item.Anchor)
                .ToImmutableArray();
        ReviewedDesign = ReviewedDesign with
        {
            MeshBindings = bindings
        };
        OnPropertyChanged(nameof(ReviewedDesign));
        InvalidateSolveArtifacts(keepResources: true);
        CurrentStep =
            ReferencePresetDesktopStep.ResourceChoice;
        ProgressText =
            "Baseline anchor binding stored against the exact head topology and camera.";
        RaiseStateProperties();
        return result;
    }

    public async Task AdvanceAsync()
    {
        ThrowIfDisposed();
        if (!CanAdvance ||
            Intake is null ||
            InferenceProposalSha256 is null ||
            runRoot is null)
        {
            return;
        }
        if (CurrentStep ==
            ReferencePresetDesktopStep.ReviewRequired)
        {
            if (ReviewedDesign is null)
            {
                CurrentStep =
                    ReferencePresetDesktopStep.ResourceChoice;
                FocusTarget =
                    "Choose compatible head resources";
                ProgressText =
                    "Review decisions are complete. Choose the exact face, mouth, eyes, brows, and hair records.";
                return;
            }
            await ClosePreviewResourcesAsync().ConfigureAwait(true);
            return;
        }
        if (CurrentStep ==
            ReferencePresetDesktopStep.ResourceChoice)
        {
            if (ReviewedDesign is null)
                return;
            if (ResourceSnapshot is null)
            {
                await ClosePreviewResourcesAsync()
                    .ConfigureAwait(true);
            }
            else
            {
                await BuildProposalAsync()
                    .ConfigureAwait(true);
            }
        }
    }

    public async Task ApplyAsync()
    {
        ThrowIfDisposed();
        if (!CanApply ||
            AuthoringProposalSha256 is null)
        {
            return;
        }
        OperationScope operation = BeginOperation(
            ReferencePresetDesktopStep.Writing,
            "Writing and independently reopening the accepted JSlot.",
            "Preset write progress");
        try
        {
            ReferencePresetWriteRequest? request =
                BuildWriteRequest(
                    apply: true,
                    AuthoringProposalSha256.Value);
            if (request is null)
            {
                PresentRecoverableError(
                    "The accepted proposal is missing one exact session authority.");
                return;
            }
            var progress = new InlineProgress<ReferencePresetProgress>(
                item => ReportProgress(operation, item));
            ReferencePresetWriteResult result =
                await transaction.WritePresetAsync(
                    request,
                    progress,
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            AddDiagnostics(result.Diagnostics);
            if (!result.Completed ||
                result.VerifiedPreset is null ||
                !PresetStillMatches(
                    result.VerifiedPreset))
            {
                PresentRecoverableError(
                    "The accepted preset did not survive exact write/readback verification.");
                return;
            }
            VerifiedPreset = result.VerifiedPreset;
            OnPropertyChanged(nameof(VerifiedPresetPath));
            OnPropertyChanged(nameof(VerifiedPresetHash));
            CurrentStep =
                ReferencePresetDesktopStep.Completed;
            FocusTarget = "Continue to NPC";
            ProgressPercent = 100;
            ProgressText =
                "Static preset complete and independently reopened. Skyrim runtime proof is still required.";
            RaiseStateProperties();
        }
        catch (OperationCanceledException)
        {
            if (operation.Generation == generation)
                PresentCancelled();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            ArgumentException)
        {
            if (IsCurrent(operation))
            {
                AddDiagnostic(
                    $"write: {exception.Message}");
                PresentRecoverableError(
                    "Preset writing stopped without retaining a verified JSlot.");
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void ContinueToNpc()
    {
        ThrowIfDisposed();
        if (!CanContinueToNpc ||
            VerifiedPreset is null ||
            ReviewedIntake is null)
        {
            return;
        }
        if (!PresetStillMatches(VerifiedPreset))
        {
            VerifiedPreset = null;
            CurrentStep = ReferencePresetDesktopStep.Stale;
            ProgressText =
                "The verified JSlot changed on disk. Write and verify it again before continuing.";
            RaiseStateProperties();
            return;
        }
        var handoff = new ReferencePresetNpcDesktopHandoff(
            VerifiedPreset,
            ReviewedIntake);
        if (continueToNpc is not null &&
            !continueToNpc(handoff))
        {
            CurrentStep =
                ReferencePresetDesktopStep.RecoverableError;
            ProgressText =
                "The preset remains verified, but the NPC task needs a compatible reviewed new-NPC request.";
            RaiseStateProperties();
            return;
        }
        CurrentStep =
            ReferencePresetDesktopStep.DownstreamHandoff;
        FocusTarget = "Preset to NPC task";
        ProgressText =
            "The exact JSlot path, hash, target, Data root, and plugin order were handed to Preset to NPC.";
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        if (!IsBusy ||
            cancellation is null)
        {
            return;
        }
        ProgressText =
            "Cancelling after the active safe checkpoint.";
        cancellation.Cancel();
    }

    public async Task RecoverAsync(
        ReferencePresetDesktopRecovery recovery)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(recovery);
        if (IsBusy)
            return;
        ApplyReviewedIntake(recovery.ReviewedIntake);
        OperationScope operation = BeginOperation(
            ReferencePresetDesktopStep.Analyzing,
            "Reopening exact recoverable session documents.",
            "Session recovery");
        try
        {
            ReferencePresetSessionReadResult intakeRead =
                await sessions.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        recovery.IntakePath,
                        recovery.IntakeSha256,
                        ReferencePresetSessionDocumentKind.Intake),
                    operation.Token).ConfigureAwait(true);
            ReferencePresetSessionReadResult proposalRead =
                await sessions.ReadAsync(
                    new ReferencePresetSessionReadRequest(
                        recovery.InferenceProposalPath,
                        recovery.InferenceProposalSha256,
                        ReferencePresetSessionDocumentKind
                            .InferenceProposal),
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            AddDiagnostics(intakeRead.Diagnostics);
            AddDiagnostics(proposalRead.Diagnostics);
            ReferencePresetIntake? recoveredIntake =
                intakeRead.Document?.Intake;
            LandmarkInferenceProposal? recoveredProposal =
                proposalRead.Document?.InferenceProposal;
            if (recoveredIntake is null ||
                recoveredProposal is null ||
                recoveredProposal.IntakeSha256 !=
                    recovery.IntakeSha256)
            {
                PresentRecoverableError(
                    "The recoverable session is incomplete or stale.");
                return;
            }
            ImmutableArray<ReferencePresetViewReviewDraft>
                drafts = await BuildReviewDraftsAsync(
                    recoveredProposal,
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            Intake = recoveredIntake;
            InferenceProposal = recoveredProposal;
            intakePath = recovery.IntakePath;
            intakeSha256 = recovery.IntakeSha256;
            inferenceProposalPath =
                recovery.InferenceProposalPath;
            inferenceProposalSha256 =
                recovery.InferenceProposalSha256;
            runRoot = new WorkspacePath(
                Path.GetDirectoryName(
                    recovery.IntakePath.Value) ??
                sessionParent.Value);
            ReviewViews = drafts;
            ReviewTraits = recoveredProposal.Traits;
            ReviewUnknowns = recoveredProposal.Unknowns;
            CurrentStep =
                ReferencePresetDesktopStep.ReviewRequired;
            FocusTarget = "Review semantic anchors";
            ProgressText =
                "Recovered exact intake and inference sessions. Review decisions remain required.";
            RaiseStateProperties();
        }
        catch (OperationCanceledException)
        {
            if (operation.Generation == generation)
                PresentCancelled();
        }
        finally
        {
            EndOperation(operation);
        }
    }

    public void Back()
    {
        ThrowIfDisposed();
        if (!CanGoBack)
            return;
        switch (CurrentStep)
        {
            case ReferencePresetDesktopStep.ResourceChoice:
            case ReferencePresetDesktopStep.ProposalReady:
            case ReferencePresetDesktopStep.Completed:
                InvalidateSolveArtifacts();
                CurrentStep =
                    ReferencePresetDesktopStep.ReviewRequired;
                FocusTarget = "Review semantic anchors";
                ProgressText =
                    "Returned to review. Later proposal and preset authority were invalidated.";
                break;
            case ReferencePresetDesktopStep.ReviewRequired:
            case ReferencePresetDesktopStep.RecoverableError:
            case ReferencePresetDesktopStep.Cancelled:
                ClearDerivedState();
                CurrentStep =
                    ReferencePresetDesktopStep.Defining;
                FocusTarget = "Reference target name";
                ProgressText =
                    "Returned to target definition.";
                break;
        }
    }

    private async Task ClosePreviewResourcesAsync()
    {
        if (Intake is null ||
            ReviewedDesign is null ||
            runRoot is null)
        {
            return;
        }
        OperationScope operation = BeginOperation(
            ReferencePresetDesktopStep.ResourceChoice,
            "Closing compatible copied Skyrim resources.",
            "Resource closure");
        try
        {
            WorkspacePath authorityRoot = new(Path.Combine(
                runRoot.Value.Value, "authority"));
            Directory.CreateDirectory(authorityRoot.Value);
            WorkspacePath previewReviewPath = new(Path.Combine(
                authorityRoot.Value,
                "review-preview.json"));
            ReferencePresetSessionWriteResult reviewWrite =
                await sessions.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        previewReviewPath,
                        new ReferencePresetSessionDocument(
                            ReferencePresetSessionDocumentKind
                                .ReviewedDesign,
                            ReviewedDesign: ReviewedDesign)),
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            AddDiagnostics(reviewWrite.Diagnostics);
            if (!reviewWrite.Written ||
                reviewWrite.ContentSha256 is null)
            {
                PresentRecoverableError(
                    "The reviewed choices could not be hash-bound.");
                return;
            }
            ReferencePresetResourceSnapshotResult closed =
                await resources.CreateAsync(
                    new ReferencePresetResourceSnapshotRequest(
                        Intake,
                        ReviewedDesign,
                        reviewWrite.ContentSha256.Value),
                    operation.Token).ConfigureAwait(true);
            AddDiagnostics(closed.Diagnostics);
            if (!closed.Accepted ||
                closed.Snapshot is null)
            {
                PresentRecoverableError(
                    "The reviewed copied Skyrim resources are incomplete or incompatible.");
                return;
            }
            ReferencePresetRenderInputResult render =
                await renderInputs.BuildAsync(
                    new ReferencePresetRenderInputRequest(
                        closed.Snapshot,
                        ReviewedDesign),
                    operation.Token).ConfigureAwait(true);
            AddDiagnostics(render.Diagnostics);
            if (render.Input is null ||
                HasErrors(render.Diagnostics))
            {
                PresentRecoverableError(
                    "The selected head resources could not produce a deterministic baseline.");
                return;
            }
            if (!IsCurrent(operation))
                return;
            ResourceSnapshot = closed.Snapshot;
            RenderInput = render.Input;
            if (baselineRenderer is not null)
            {
                ImmutableArray<ReferencePresetBaselinePreview>
                    previews = await RenderBaselinePreviewsAsync(
                        render.Input,
                        operation.Token).ConfigureAwait(true);
                if (!IsCurrent(operation))
                    return;
                if (previews.IsDefaultOrEmpty)
                {
                    PresentRecoverableError(
                        "The exact baseline head could not be rendered for reviewed mesh binding.");
                    return;
                }
                BaselinePreviews = previews;
                SelectMatchingBaselinePreview();
            }
            CurrentStep =
                ReferencePresetDesktopStep.ResourceChoice;
            FocusTarget = "Choose compatible head resources";
            ProgressPercent = 100;
            ProgressText =
                "Compatible resources are closed. Bind each required reviewed anchor on the exact baseline head.";
            RaiseStateProperties();
        }
        catch (OperationCanceledException)
        {
            if (operation.Generation == generation)
                PresentCancelled();
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async ValueTask<ImmutableArray<
        ReferencePresetBaselinePreview>> RenderBaselinePreviewsAsync(
            ReferencePresetRenderInput input,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (baselineRenderer is null ||
            runRoot is null)
        {
            return [];
        }
        WorkspacePath previewRoot = new(Path.Combine(
            runRoot.Value.Value,
            "baseline-previews"));
        if (Directory.Exists(previewRoot.Value) ||
            File.Exists(previewRoot.Value))
        {
            AddDiagnostic(
                "baseline-preview-output: The baseline preview root already exists.");
            return [];
        }
        await Task.Run(
            () => Directory.CreateDirectory(previewRoot.Value),
            cancellationToken).ConfigureAwait(true);
        var previews = ImmutableArray.CreateBuilder<
            ReferencePresetBaselinePreview>(
            input.Cameras.Length);
        foreach (ReferenceOrthographicCamera camera in
                 input.Cameras)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReferencePresetCpuRenderResult rendered =
                await Task.Run(
                    () => baselineRenderer.Render(
                        new ReferencePresetCpuRenderRequest(
                            input.Shapes,
                            input.Textures,
                            camera,
                            900,
                            900)),
                    cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            AddDiagnostics(rendered.Diagnostics);
            if (!rendered.Accepted ||
                rendered.PngSha256 is null ||
                rendered.PngBytes.IsDefaultOrEmpty)
            {
                return [];
            }
            WorkspacePath path = new(Path.Combine(
                previewRoot.Value,
                camera.ViewRole.ToWireName() +
                ".png"));
            await File.WriteAllBytesAsync(
                path.Value,
                rendered.PngBytes.ToArray(),
                cancellationToken).ConfigureAwait(true);
            if (await HashFileAsync(
                    path,
                    cancellationToken).ConfigureAwait(true) !=
                rendered.PngSha256.Value)
            {
                AddDiagnostic(
                    "baseline-preview-readback: The preview PNG changed during write.");
                return [];
            }
            previews.Add(new ReferencePresetBaselinePreview(
                camera.ViewRole,
                path,
                rendered.PngSha256.Value,
                input.InputSha256));
        }
        return previews.ToImmutable();
    }

    private void SelectMatchingBaselinePreview()
    {
        ReferenceImageViewRole? viewRole =
            SelectedReviewView?.ViewRole;
        SelectedBaselinePreview =
            viewRole is null
                ? BaselinePreviews.FirstOrDefault()
                : BaselinePreviews.FirstOrDefault(item =>
                    item.ViewRole == viewRole);
    }

    private async Task BuildProposalAsync()
    {
        if (Intake is null ||
            ReviewedDesign is null ||
            runRoot is null)
        {
            return;
        }
        OperationScope operation = BeginOperation(
            ReferencePresetDesktopStep.Comparing,
            "Solving and rendering deterministic comparisons.",
            "Comparison progress");
        try
        {
            WorkspacePath authorityRoot = new(Path.Combine(
                runRoot.Value.Value, "authority"));
            Directory.CreateDirectory(authorityRoot.Value);
            WorkspacePath candidateReviewPath = new(Path.Combine(
                authorityRoot.Value, "reviewed-design.json"));
            ReferencePresetSessionWriteResult reviewWrite =
                await sessions.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        candidateReviewPath,
                        new ReferencePresetSessionDocument(
                            ReferencePresetSessionDocumentKind
                                .ReviewedDesign,
                            ReviewedDesign: ReviewedDesign)),
                    operation.Token).ConfigureAwait(true);
            AddDiagnostics(reviewWrite.Diagnostics);
            if (!reviewWrite.Written ||
                reviewWrite.ContentSha256 is null)
            {
                PresentRecoverableError(
                    "The final reviewed design could not be reopened.");
                return;
            }

            ReferencePresetResourceSnapshotResult closed =
                await resources.CreateAsync(
                    new ReferencePresetResourceSnapshotRequest(
                        Intake,
                        ReviewedDesign,
                        reviewWrite.ContentSha256.Value),
                    operation.Token).ConfigureAwait(true);
            AddDiagnostics(closed.Diagnostics);
            if (!closed.Accepted ||
                closed.Snapshot is null)
            {
                PresentRecoverableError(
                    "The final resource snapshot is stale against the reviewed design.");
                return;
            }
            WorkspacePath candidateResourcePath = new(
                Path.Combine(
                    authorityRoot.Value,
                    "resource-snapshot.json"));
            ReferencePresetSessionWriteResult resourceWrite =
                await sessions.WriteAsync(
                    new ReferencePresetSessionWriteRequest(
                        candidateResourcePath,
                        new ReferencePresetSessionDocument(
                            ReferencePresetSessionDocumentKind
                                .ResourceSnapshot,
                            ResourceSnapshot:
                                closed.Snapshot)),
                    operation.Token).ConfigureAwait(true);
            AddDiagnostics(resourceWrite.Diagnostics);
            if (!resourceWrite.Written ||
                resourceWrite.ContentSha256 is null)
            {
                PresentRecoverableError(
                    "The final resource snapshot could not be reopened.");
                return;
            }

            reviewedDesignPath = candidateReviewPath;
            reviewedDesignSha256 =
                reviewWrite.ContentSha256.Value;
            resourceSnapshotPath =
                candidateResourcePath;
            resourceSnapshotSha256 =
                resourceWrite.ContentSha256.Value;
            ResourceSnapshot = closed.Snapshot;
            proposalOutputRoot = new WorkspacePath(Path.Combine(
                runRoot.Value.Value, "preset-draft"));
            ReferencePresetWriteRequest? request =
                BuildWriteRequest(
                    apply: false,
                    acceptedProposal: null);
            if (request is null)
            {
                PresentRecoverableError(
                    "The comparison request lacks one exact authority.");
                return;
            }
            var progress = new InlineProgress<ReferencePresetProgress>(
                item => ReportProgress(operation, item));
            ReferencePresetWriteResult result =
                await transaction.WritePresetAsync(
                    request,
                    progress,
                    operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation))
                return;
            AddDiagnostics(result.Diagnostics);
            if (!result.Completed ||
                result.Proposal is null ||
                result.ProposalSha256 is null ||
                result.VerifiedPreset is not null)
            {
                PresentRecoverableError(
                    "The deterministic comparison proposal was refused.");
                return;
            }
            AuthoringProposal = result.Proposal;
            AuthoringProposalSha256 =
                result.ProposalSha256.Value;
            ComparisonViews = proposalOutputRoot is null
                ? []
                : result.Proposal.Comparison.Artifacts
                    .Where(item =>
                        item.Path.Value.EndsWith(
                            ".png",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(item =>
                        new ReferencePresetComparisonView(
                            item.ViewRole?.ToWireName() ??
                                "comparison",
                            new WorkspacePath(Path.Combine(
                                proposalOutputRoot.Value.Value,
                                item.Path.Value)),
                            item.ContentSha256,
                            item.SourceReferenceSha256))
                    .ToImmutableArray();
            OnPropertyChanged(nameof(ComparisonViews));
            OnPropertyChanged(nameof(ComparisonSummary));
            CurrentStep =
                ReferencePresetDesktopStep.ProposalReady;
            FocusTarget = "Review comparison losses";
            ProgressPercent = 100;
            ProgressText =
                "Comparison proposal ready. Review residuals and losses before Apply.";
            RaiseStateProperties();
        }
        catch (OperationCanceledException)
        {
            if (operation.Generation == generation)
                PresentCancelled();
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private ReferencePresetWriteRequest? BuildWriteRequest(
        bool apply,
        Sha256Hash? acceptedProposal)
    {
        if (runRoot is null ||
            intakePath is null ||
            intakeSha256 is null ||
            inferenceProposalPath is null ||
            inferenceProposalSha256 is null ||
            reviewedDesignPath is null ||
            reviewedDesignSha256 is null ||
            resourceSnapshotPath is null ||
            resourceSnapshotSha256 is null)
        {
            return null;
        }
        WorkspacePath output;
        if (apply)
        {
            acceptedOutputRoot ??= new WorkspacePath(Path.Combine(
                runRoot.Value.Value, "preset-accepted"));
            output = acceptedOutputRoot.Value;
        }
        else
        {
            proposalOutputRoot ??= new WorkspacePath(Path.Combine(
                runRoot.Value.Value, "preset-draft"));
            output = proposalOutputRoot.Value;
        }
        return new ReferencePresetWriteRequest(
            intakePath.Value,
            intakeSha256.Value,
            inferenceProposalPath.Value,
            inferenceProposalSha256.Value,
            reviewedDesignPath.Value,
            reviewedDesignSha256.Value,
            resourceSnapshotPath.Value,
            resourceSnapshotSha256.Value,
            output,
            apply,
            acceptedProposal);
    }

    private async ValueTask<
        ImmutableArray<ReferencePresetViewReviewDraft>>
        BuildReviewDraftsAsync(
            LandmarkInferenceProposal proposal,
            CancellationToken cancellationToken)
    {
        var drafts = ImmutableArray.CreateBuilder<
            ReferencePresetViewReviewDraft>(
            proposal.Images.Length);
        foreach (ReferenceImageInference image in proposal.Images)
        {
            ReferenceSemanticLandmarkProjectionResult projected =
                await projector.ProjectAsync(
                    new ReferenceSemanticLandmarkProjectionRequest(
                        image),
                    cancellationToken).ConfigureAwait(false);
            AddDiagnostics(projected.Diagnostics);
            if (HasErrors(projected.Diagnostics))
            {
                throw new InvalidDataException(
                    $"Semantic anchor projection failed for '{image.ImageId}'.");
            }
            ImmutableArray<ReferencePresetAnchorReviewDraft> anchors =
                projected.Anchors.Select(item =>
                        new ReferencePresetAnchorReviewDraft(
                            new ReferenceSemanticAnchor(
                                item.Anchor,
                                item.SourceLandmarkIndex,
                                item.X,
                                item.Y,
                                item.Confidence,
                                item.AdvisoryVisible,
                                item.ManualConfirmationRequired
                                    ? ReferenceAnchorReviewState
                                        .UnknownLowConfidence
                                    : ReferenceAnchorReviewState
                                        .Accepted),
                            false))
                    .ToImmutableArray();
            drafts.Add(new ReferencePresetViewReviewDraft(
                image.ImageId,
                image.ViewRole,
                HashInference(image),
                image.DetectorScore,
                image.AdvisoryYawDegrees,
                false,
                anchors));
        }
        return drafts.ToImmutable();
    }

    private bool UpdateAnchor(
        ReferenceImageViewRole viewRole,
        ReferenceSemanticAnchorKind anchorKind,
        Func<ReferencePresetAnchorReviewDraft,
            ReferencePresetAnchorReviewDraft> update)
    {
        ThrowIfDisposed();
        int viewIndex = FindIndex(ReviewViews, item =>
            item.ViewRole == viewRole);
        if (viewIndex < 0)
            return false;
        ReferencePresetViewReviewDraft view =
            ReviewViews[viewIndex];
        int anchorIndex = FindIndex(view.Anchors, item =>
            item.Anchor.Anchor == anchorKind);
        if (anchorIndex < 0)
            return false;
        ImmutableArray<ReferencePresetAnchorReviewDraft> anchors =
            view.Anchors.SetItem(
                anchorIndex,
                update(view.Anchors[anchorIndex]));
        ReviewViews = ReviewViews.SetItem(
            viewIndex,
            view with { Anchors = anchors });
        OnPropertyChanged(nameof(ReviewViews));
        InvalidateEditedReview();
        return true;
    }

    private void InvalidateEditedReview()
    {
        generation++;
        ReviewedDesign = null;
        InvalidateSolveArtifacts();
        CurrentStep =
            ReferencePresetDesktopStep.ReviewRequired;
        FocusTarget = "Review semantic anchors";
        ProgressText =
            "Review changed. Accept the complete immutable review before continuing.";
        RaiseStateProperties();
    }

    private void InvalidateSolveArtifacts(
        bool keepResources = false)
    {
        if (!keepResources)
        {
            ResourceSnapshot = null;
            RenderInput = null;
            BaselinePreviews = [];
            SelectedBaselinePreview = null;
        }
        AuthoringProposal = null;
        AuthoringProposalSha256 = null;
        VerifiedPreset = null;
        ComparisonViews = [];
        reviewedDesignPath = null;
        reviewedDesignSha256 = null;
        resourceSnapshotPath = null;
        resourceSnapshotSha256 = null;
        proposalOutputRoot = null;
        acceptedOutputRoot = null;
        OnPropertyChanged(nameof(ResourceSnapshot));
        OnPropertyChanged(nameof(RenderInput));
        OnPropertyChanged(nameof(BaselinePreviews));
        OnPropertyChanged(nameof(AuthoringProposal));
        OnPropertyChanged(nameof(AuthoringProposalSha256));
        OnPropertyChanged(nameof(VerifiedPreset));
        OnPropertyChanged(nameof(ComparisonViews));
        OnPropertyChanged(nameof(ComparisonSummary));
        OnPropertyChanged(nameof(BaselineBindingProgressText));
        OnPropertyChanged(nameof(VerifiedPresetPath));
        OnPropertyChanged(nameof(VerifiedPresetHash));
    }

    private void InvalidateActiveOperation()
    {
        generation++;
        cancellation?.Cancel();
    }

    private void ClearAllAuthoringState()
    {
        Intake = null;
        runRoot = null;
        ClearDerivedState();
        OnPropertyChanged(nameof(Intake));
    }

    private void ClearDerivedState()
    {
        InferenceProposal = null;
        ReviewedDesign = null;
        ReviewViews = [];
        ReviewTraits = [];
        ReviewUnknowns = [];
        CatalogSelection = null;
        SelectedReviewView = null;
        SelectedReviewAnchor = null;
        intakePath = null;
        intakeSha256 = null;
        inferenceProposalPath = null;
        inferenceProposalSha256 = null;
        InvalidateSolveArtifacts();
        RaiseStateProperties();
    }

    private OperationScope BeginOperation(
        ReferencePresetDesktopStep step,
        string progress,
        string focus)
    {
        cancellation?.Dispose();
        var source = new CancellationTokenSource();
        cancellation = source;
        long operation = ++operationOrdinal;
        activeOperation = operation;
        IsBusy = true;
        CurrentStep = step;
        ProgressText = progress;
        FocusTarget = focus;
        ProgressPercent = 0;
        return new OperationScope(
            operation,
            generation,
            source.Token);
    }

    private void EndOperation(OperationScope operation)
    {
        if (activeOperation != operation.Operation)
            return;
        activeOperation = 0;
        CancellationTokenSource? source = cancellation;
        cancellation = null;
        source?.Dispose();
        IsBusy = false;
    }

    private bool IsCurrent(OperationScope operation) =>
        operation.Operation == activeOperation &&
        operation.Generation == generation &&
        !operation.Token.IsCancellationRequested;

    private void ReportProgress(
        OperationScope operation,
        ReferencePresetProgress progress)
    {
        if (!IsCurrent(operation))
            return;
        ProgressText = progress.Message;
        ProgressPercent = progress.TotalUnits <= 0
            ? 0
            : Math.Clamp(
                (int)Math.Round(
                    progress.CompletedUnits * 100.0 /
                    progress.TotalUnits,
                    MidpointRounding.AwayFromZero),
                0,
                100);
    }

    private void PresentCancelled()
    {
        CurrentStep = ReferencePresetDesktopStep.Cancelled;
        FocusTarget = "Analyze reference images";
        ProgressText =
            "Cancelled safely. No verified preset or NPC artifact was retained.";
    }

    private void PresentRecoverableError(string message)
    {
        CurrentStep =
            ReferencePresetDesktopStep.RecoverableError;
        FocusTarget = "Technical details";
        ProgressText = message;
    }

    private WorkspacePath NewRunRoot() =>
        new(Path.Combine(
            sessionParent.Value,
            "reference-preset-" +
            Guid.NewGuid().ToString("N")));

    private bool PresetStillMatches(
        VerifiedReferencePreset preset)
    {
        try
        {
            return preset.PresetPath.IsUnder(labRoot) &&
                File.Exists(preset.PresetPath.Value) &&
                HashFile(preset.PresetPath) ==
                    preset.PresetSha256;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            AddDiagnostic(
                $"preset-handoff: {exception.Message}");
            return false;
        }
    }

    private static bool TryNormalizeDisplayCoordinate(
        double displayX,
        double displayY,
        double contentLeft,
        double contentTop,
        double contentWidth,
        double contentHeight,
        out double normalizedX,
        out double normalizedY)
    {
        normalizedX = 0;
        normalizedY = 0;
        if (!double.IsFinite(displayX) ||
            !double.IsFinite(displayY) ||
            !double.IsFinite(contentLeft) ||
            !double.IsFinite(contentTop) ||
            !double.IsFinite(contentWidth) ||
            !double.IsFinite(contentHeight) ||
            contentWidth <= 0 ||
            contentHeight <= 0)
        {
            return false;
        }
        normalizedX =
            (displayX - contentLeft) / contentWidth;
        normalizedY =
            (displayY - contentTop) / contentHeight;
        return normalizedX is >= 0 and <= 1 &&
            normalizedY is >= 0 and <= 1;
    }

    private static Sha256Hash HashInference(
        ReferenceImageInference inference)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        Append(hash, inference.ImageId);
        Append(hash, inference.ViewRole.ToWireName());
        Append(hash, inference.SourceSha256.Value);
        Append(hash, inference.CanonicalRgbaSha256.Value);
        Append(hash, inference.DetectorScore.ToString(
            "R",
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (ReferenceFaceLandmark landmark in
                 inference.Landmarks)
        {
            Append(hash, landmark.Index.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, landmark.X.ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, landmark.Y.ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture));
        }
        return new Sha256Hash(
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void Append(
        IncrementalHash hash,
        string value)
    {
        hash.AppendData(
            System.Text.Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static Sha256Hash HashFile(
        WorkspacePath path)
    {
        using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.SequentialScan);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static int FindIndex<T>(
        ImmutableArray<T> items,
        Func<T, bool> predicate)
    {
        for (var index = 0; index < items.Length; index++)
        {
            if (predicate(items[index]))
                return index;
        }
        return -1;
    }

    private void AddDiagnostics(
        IEnumerable<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
        {
            AddDiagnostic(
                $"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private void AddDiagnostic(string diagnostic)
    {
        if (!Diagnostics.Contains(diagnostic))
            Diagnostics.Add(diagnostic);
    }

    private static ReferenceMeshAnchorBindResult BindRefusal(
        string code,
        string message) =>
        new(
            null,
            [
                new Diagnostic(
                    code,
                    DiagnosticSeverity.Error,
                    message)
            ]);

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(Intake));
        OnPropertyChanged(nameof(InferenceProposal));
        OnPropertyChanged(nameof(ReviewedDesign));
        OnPropertyChanged(nameof(ResourceSnapshot));
        OnPropertyChanged(nameof(RenderInput));
        OnPropertyChanged(nameof(AuthoringProposal));
        OnPropertyChanged(nameof(AuthoringProposalSha256));
        OnPropertyChanged(nameof(VerifiedPreset));
        OnPropertyChanged(nameof(ReviewViews));
        OnPropertyChanged(nameof(ReviewTraits));
        OnPropertyChanged(nameof(ReviewUnknowns));
        OnPropertyChanged(nameof(CatalogSelection));
        OnPropertyChanged(nameof(ReviewDecisionsComplete));
        OnPropertyChanged(nameof(SelectedReviewView));
        OnPropertyChanged(nameof(SelectedReviewAnchor));
        OnPropertyChanged(nameof(SelectedReferenceImagePath));
        OnPropertyChanged(nameof(BaselinePreviews));
        OnPropertyChanged(nameof(SelectedBaselinePreview));
        OnPropertyChanged(nameof(SelectedBaselinePreviewPath));
        OnPropertyChanged(nameof(ComparisonViews));
        OnPropertyChanged(nameof(ComparisonSummary));
        OnPropertyChanged(nameof(VerifiedPresetPath));
        OnPropertyChanged(nameof(VerifiedPresetHash));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanContinueToNpc));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        analyzeCommand?.RaiseCanExecuteChanged();
        advanceCommand?.RaiseCanExecuteChanged();
        backCommand?.RaiseCanExecuteChanged();
        applyCommand?.RaiseCanExecuteChanged();
        continueToNpcCommand?.RaiseCanExecuteChanged();
        cancelCommand?.RaiseCanExecuteChanged();
        defineCommand?.RaiseCanExecuteChanged();
    }

    private bool Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(
                field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        serviceLifetime?.Dispose();
    }

    private readonly record struct OperationScope(
        long Operation,
        long Generation,
        CancellationToken Token);

    private sealed class InlineProgress<T>(
        Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class DelegateCommand(
        Action execute,
        Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) =>
            canExecute();
        public void Execute(object? parameter) => execute();
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(
                this, EventArgs.Empty);
    }
}
