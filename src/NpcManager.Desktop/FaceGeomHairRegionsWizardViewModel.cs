using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal sealed record FaceGeomHairRegionsWizardLaunchContext(
    SkyrimMainWorkspaceIdentity? Identity,
    WorkspacePath? SuggestedSource,
    string DisplayName)
{
    public static FaceGeomHairRegionsWizardLaunchContext Standalone
        { get; } = new(
            null,
            null,
            "Standalone K-local FaceGeom");
}

internal sealed record FaceGeomHairRegionsWizardProductionContext(
    FaceGeomHairRegionsWizardDesktopComposition Composition,
    WorkspacePath LabRoot,
    ReviewedGameIntake? ReviewedIntake,
    SkyrimMainWorkspaceIdentity? Identity,
    string DisplayName);

internal sealed record FaceGeomHairRegionsWizardAnalysisState(
    StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
        AnalysisDocument,
    FaceGeomHairRegionsRequest AssignmentTemplate,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed record FaceGeomHairRegionsWizardProposalState(
    StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
        RequestDocument,
    StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
        ProposalDocument,
    FaceGeomHairRegionsProposalMaterialization Materialization,
    ImmutableArray<Diagnostic> Diagnostics);

internal interface IFaceGeomHairRegionsWizardTransaction
{
    ValueTask<FaceGeomHairRegionsWizardAnalysisState>
        AnalyzeAsync(
            WorkspacePath source,
            WorkspacePath output,
            WorkspacePath manifest,
            CancellationToken cancellationToken);

    ValueTask<FaceGeomHairRegionsWizardProposalState>
        ProposeAsync(
            StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
                analysis,
            FaceGeomHairRegionsRequest request,
            CancellationToken cancellationToken);

    ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewAsync(
            FaceGeomHairRegionsWizardProposalState proposal,
            ReviewedGameIntake intake,
            WorkspacePath outputRoot,
            CancellationToken cancellationToken);

    ValueTask<FaceGeomHairRegionsApplyResult>
        ApplyAsync(
            FaceGeomHairRegionsWizardProposalState proposal,
            CancellationToken cancellationToken);

    ValueTask<FaceGeomHairRegionsPreviewCacheAuthority>
        ResolvePreviewCacheAuthorityAsync(
            FaceGeomHairRegionsWizardProposalState proposal,
            ReviewedGameIntake intake,
            CancellationToken cancellationToken);
}

internal interface IFaceGeomHairRegionsPreviewOutputCleanup
{
    ImmutableArray<Diagnostic> Cleanup(
        WorkspacePath outputRoot);
}

internal sealed class
    FileSystemFaceGeomHairRegionsPreviewOutputCleanup :
        IFaceGeomHairRegionsPreviewOutputCleanup
{
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath admittedParent;

    public FileSystemFaceGeomHairRegionsPreviewOutputCleanup(
        WorkspacePath labRoot)
    {
        this.labRoot =
            FaceGeomHairRegionsDesktopPathBoundary
                .RequireExactLabRoot(labRoot);
        admittedParent =
            FaceGeomHairRegionsDesktopPathBoundary
                .PreviewRoot(this.labRoot);
    }

    public ImmutableArray<Diagnostic> Cleanup(
        WorkspacePath outputRoot)
    {
        try
        {
            FaceGeomHairRegionsDesktopPathBoundary
                .RequirePreviewOutput(
                    labRoot,
                    admittedParent,
                    outputRoot);
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                  IOException or
                  UnauthorizedAccessException or
                  InvalidDataException)
        {
            return
            [
                Failure(
                    outputRoot,
                    exception.Message)
            ];
        }

        if (File.Exists(outputRoot.Value))
        {
            return
            [
                Failure(
                    outputRoot,
                    "The preview output authority is a file, not an owned directory.")
            ];
        }

        if (!Directory.Exists(outputRoot.Value))
        {
            return [];
        }

        try
        {
            RefuseReparseTree(outputRoot.Value);
            Directory.Delete(
                outputRoot.Value,
                recursive: true);
            return [];
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidDataException)
        {
            return
            [
                Failure(
                    outputRoot,
                    exception.Message)
            ];
        }
    }

    private static void RefuseReparseTree(
        string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            FileAttributes directoryAttributes =
                File.GetAttributes(directory);
            if ((directoryAttributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Reparse directory cannot be cleaned recursively: {directory}");
            }

            foreach (string entry in
                     Directory.EnumerateFileSystemEntries(
                         directory))
            {
                FileAttributes attributes =
                    File.GetAttributes(entry);
                if ((attributes &
                     FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Reparse cache content cannot be cleaned recursively: {entry}");
                }

                if ((attributes &
                     FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static Diagnostic Failure(
        WorkspacePath outputRoot,
        string reason) =>
        new(
            "hair-regions-preview-discard-cleanup-failed",
            DiagnosticSeverity.Error,
            $"Discarded preview survived at {outputRoot.Value}: {reason}");
}

internal sealed record FaceGeomHairRegionsWizardStepViewModel(
    int Index,
    string Title)
{
    public int Number => Index + 1;

    public string AutomationName =>
        $"HairTint wizard step {Number}";
}

internal sealed record FaceGeomHairRegionBytePlanViewModel(
    string SharedShaderGroupId,
    string StructuralIdsText,
    string Role,
    string ByteEnvelopeText,
    string OldFloatBitsText,
    string NewFloatBitsText,
    string ChangedByteOffsetsText);

internal sealed class FaceGeomHairRegionsWizardViewModel :
    NotifyViewModel,
    IDisposable
{
    internal const string RuntimeAuthorityLabel =
        "Off-engine HairTint preview \u2014 Skyrim runtime remains authoritative";

    private readonly IFaceGeomHairRegionsWizardTransaction transaction;
    private readonly FaceGeomHairRegionsPreviewCache? cache;
    private readonly WorkspacePath labRoot;
    private readonly ReviewedGameIntake? reviewedIntake;
    private readonly FaceGeomHairRegionsWizardLaunchContext launch;
    private readonly IFaceGeomHairRegionsPreviewOutputCleanup
        previewOutputCleanup;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private FaceGeomHairRegionsWizardAnalysisState? analysis;
    private FaceGeomHairRegionsWizardProposalState? proposal;
    private FaceGeomHairRegionsPreviewResult? preview;
    private FaceGeomHairRegionsApplyResult? applyResult;
    private ImmutableArray<Diagnostic> diagnostics = [];
    private ImmutableArray<WorkspacePath>
        survivingPreviewOutputs = [];
    private ImmutableArray<WorkspacePath>
        survivingApplyArtifacts = [];
    private ImmutableArray<
        FaceGeomHairRegionBytePlanViewModel>
        bytePlanEnvelopes = [];
    private WorkspacePath? ownedPreviewOutputRoot;
    private CancellationTokenSource? analyzeCancellation;
    private CancellationTokenSource? proposeCancellation;
    private CancellationTokenSource? previewCancellation;
    private CancellationTokenSource? applyCancellation;
    private string sourcePath;
    private string outputPath = string.Empty;
    private string manifestPath = string.Empty;
    private string primaryColor = "#000000";
    private string accentColor = "#FFFFFF";
    private int currentStepIndex;
    private long revision;
    private long analyzeGeneration;
    private long proposeGeneration;
    private long previewGeneration;
    private long applyGeneration;
    private long proposalRevision = -1;
    private long previewRevision = -1;
    private long applyResultRevision = -1;
    private bool proposalAccepted;
    private bool isAnalyzing;
    private bool isProposing;
    private bool isRenderingPreview;
    private bool isApplying;
    private bool disposed;

    public FaceGeomHairRegionsWizardViewModel(
        IFaceGeomHairRegionsWizardTransaction transaction,
        FaceGeomHairRegionsPreviewCache? cache,
        WorkspacePath labRoot,
        ReviewedGameIntake? reviewedIntake,
        FaceGeomHairRegionsWizardLaunchContext launch,
        IFaceGeomHairRegionsPreviewOutputCleanup?
            previewOutputCleanup = null)
    {
        this.transaction = transaction ??
            throw new ArgumentNullException(nameof(transaction));
        this.cache = cache;
        this.labRoot =
            FaceGeomHairRegionsDesktopPathBoundary
                .RequireExactLabRoot(labRoot);
        this.reviewedIntake = reviewedIntake;
        this.launch = launch ??
            throw new ArgumentNullException(nameof(launch));
        this.previewOutputCleanup =
            previewOutputCleanup ??
            new FileSystemFaceGeomHairRegionsPreviewOutputCleanup(
                labRoot);
        diagnostics =
            cache?.StartupDiagnostics ??
            [];
        sourcePath = launch.SuggestedSource?.Value ?? string.Empty;
        Steps =
        [
            new(0, "Source"),
            new(1, "Regions"),
            new(2, "Colors and Preview"),
            new(3, "Byte-plan Review"),
            new(4, "Write and Verify")
        ];
        Regions = [];
    }

    public ReadOnlyCollection<FaceGeomHairRegionsWizardStepViewModel>
        Steps { get; }

    public ObservableCollection<FaceGeomHairRegionCardViewModel>
        Regions { get; }

    public int CurrentStepIndex
    {
        get => currentStepIndex;
        private set
        {
            if (!Set(ref currentStepIndex, value))
            {
                return;
            }

            Raise(nameof(CurrentStep));
            Raise(nameof(CanWrite));
        }
    }

    public FaceGeomHairRegionsWizardStepViewModel CurrentStep =>
        Steps[CurrentStepIndex];

    public string SourcePath
    {
        get => sourcePath;
        set
        {
            ThrowIfDisposed();
            if (Set(ref sourcePath, value ?? string.Empty))
            {
                InvalidateAnalysis();
            }
        }
    }

    public string OutputPath
    {
        get => outputPath;
        set
        {
            ThrowIfDisposed();
            if (Set(ref outputPath, value ?? string.Empty))
            {
                InvalidateProposal();
            }
        }
    }

    public string ManifestPath
    {
        get => manifestPath;
        set
        {
            ThrowIfDisposed();
            if (Set(ref manifestPath, value ?? string.Empty))
            {
                InvalidateProposal();
            }
        }
    }

    public string PrimaryColor
    {
        get => primaryColor;
        set
        {
            ThrowIfDisposed();
            if (Set(ref primaryColor, value ?? string.Empty))
            {
                UpdateRegionTargetColors();
                InvalidateProposal();
            }
        }
    }

    public string AccentColor
    {
        get => accentColor;
        set
        {
            ThrowIfDisposed();
            if (Set(ref accentColor, value ?? string.Empty))
            {
                UpdateRegionTargetColors();
                InvalidateProposal();
            }
        }
    }

    public string SelectedNpcSummary => launch.DisplayName;

    public bool IsStandaloneSource => launch.Identity is null;

    public string AuthorityLabel { get; } = RuntimeAuthorityLabel;

    public bool HasCurrentAnalysis => analysis is not null;

    public bool HasCurrentProposal =>
        proposal is not null &&
        proposalRevision == revision;

    public bool IsPreviewCurrent =>
        preview?.Succeeded == true &&
        previewRevision == revision &&
        HasCurrentProposal;

    public bool IsProposalAccepted =>
        proposalAccepted &&
        HasCurrentProposal &&
        IsPreviewCurrent;

    public bool IsApplyResultCurrent =>
        applyResult is not null &&
        applyResultRevision == revision;

    public bool HasVerifiedApplyResult =>
        IsApplyResultCurrent &&
        applyResult is not null &&
        survivingApplyArtifacts.IsEmpty &&
        IsVerifiedApplyResult(
            applyResult);

    public bool CanWrite =>
        CurrentStepIndex == 4 &&
        IsProposalAccepted &&
        !IsApplying &&
        survivingApplyArtifacts.IsEmpty &&
        !HasVerifiedApplyResult;

    public FaceGeomHairRegionsPreviewResult? Preview =>
        IsPreviewCurrent
            ? preview
            : null;

    public FaceGeomHairRegionsApplyResult? ApplyResult =>
        applyResult;

    public ImmutableArray<Diagnostic> Diagnostics =>
        diagnostics;

    public ImmutableArray<WorkspacePath>
        SurvivingPreviewOutputs =>
            survivingPreviewOutputs;

    public ImmutableArray<WorkspacePath>
        SurvivingApplyArtifacts =>
            survivingApplyArtifacts;

    public ImmutableArray<
        FaceGeomHairRegionBytePlanViewModel>
        BytePlanEnvelopes =>
            bytePlanEnvelopes;

    public string BytePlanProposalSha256 =>
        HasCurrentProposal
            ? proposal!.ProposalDocument.Sha256.Value
            : "—";

    public string BytePlanExpectedOutputPath =>
        HasCurrentProposal
            ? proposal!.ProposalDocument.Value
                .ExpectedOutput.Path.Value
            : "—";

    public string BytePlanExpectedOutputSha256 =>
        HasCurrentProposal
            ? proposal!.ProposalDocument.Value
                .ExpectedOutput.Sha256.Value
            : "—";

    public string BytePlanExpectedOutputLength =>
        HasCurrentProposal
            ? proposal!.ProposalDocument.Value
                .ExpectedOutput.ByteLength
                .ToString(
                    CultureInfo.InvariantCulture)
            : "—";

    public string BytePlanManifestPath =>
        HasCurrentProposal
            ? proposal!.ProposalDocument.Value
                .Manifest.Value
            : "—";

    public string BytePlanChangedByteOffsetsText =>
        HasCurrentProposal
            ? string.Join(
                ", ",
                proposal!.ProposalDocument.Value
                    .PredictedChangedByteOffsets)
            : "—";

    public bool IsAnalyzing
    {
        get => isAnalyzing;
        private set => Set(ref isAnalyzing, value);
    }

    public bool IsProposing
    {
        get => isProposing;
        private set => Set(ref isProposing, value);
    }

    public bool IsRenderingPreview
    {
        get => isRenderingPreview;
        private set
        {
            if (Set(
                    ref isRenderingPreview,
                    value))
            {
                Raise(nameof(CanWrite));
            }
        }
    }

    public bool IsApplying
    {
        get => isApplying;
        private set
        {
            if (Set(ref isApplying, value))
            {
                Raise(nameof(CanWrite));
            }
        }
    }

    public async ValueTask AnalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        WorkspacePath source = RequiredPath(
            SourcePath,
            nameof(SourcePath));
        WorkspacePath output = RequiredPath(
            OutputPath,
            nameof(OutputPath));
        WorkspacePath manifest = RequiredPath(
            ManifestPath,
            nameof(ManifestPath));

        InvalidateAnalysis();
        CancellationTokenSource operation =
            BeginOperation(
                ref analyzeCancellation,
                ref analyzeGeneration,
                cancellationToken);
        long operationGeneration =
            analyzeGeneration;
        long operationRevision =
            revision;
        IsAnalyzing = true;
        try
        {
            FaceGeomHairRegionsWizardAnalysisState result =
                await transaction.AnalyzeAsync(
                        source,
                        output,
                        manifest,
                        operation.Token)
                    .ConfigureAwait(true);
            if (!IsCurrent(
                    operationGeneration,
                    analyzeGeneration,
                    operationRevision,
                    operation,
                    cancellationToken))
            {
                return;
            }

            AppendDiagnostics(
                result.Diagnostics);
            analysis = result;
            Regions.Clear();
            foreach (FaceGeomHairRegionsRegion region in
                     result.AnalysisDocument.Value.Regions)
            {
                Regions.Add(
                    new FaceGeomHairRegionCardViewModel(
                        region,
                        SetLinkedRole));
            }

            SetAnalysisTemplateValue(
                ref primaryColor,
                result.AssignmentTemplate.PrimaryColor,
                nameof(PrimaryColor));
            SetAnalysisTemplateValue(
                ref accentColor,
                result.AssignmentTemplate.AccentColor,
                nameof(AccentColor));
            UpdateRegionTargetColors();
            CurrentStepIndex = 1;
            RaiseState();
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            FinishOperation(
                ref analyzeCancellation,
                operation,
                operationGeneration,
                analyzeGeneration,
                () => IsAnalyzing = false);
        }
    }

    public async ValueTask ProposeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        FaceGeomHairRegionsWizardAnalysisState currentAnalysis =
            analysis ??
            throw new InvalidOperationException(
                "Analyze the exact FaceGeom before creating a byte plan.");
        FaceGeomHairRegionsRequest request =
            currentAnalysis.AssignmentTemplate with
            {
                PrimaryColor = PrimaryColor,
                AccentColor = AccentColor,
                Assignments = Regions
                    .Select(region =>
                        new FaceGeomHairRegionAssignment(
                            region.StructuralId,
                            region.Role))
                    .ToImmutableArray(),
                Output = RequiredPath(
                    OutputPath,
                    nameof(OutputPath)),
                Manifest = RequiredPath(
                    ManifestPath,
                    nameof(ManifestPath))
            };

        InvalidateProposal();
        CancellationTokenSource operation =
            BeginOperation(
                ref proposeCancellation,
                ref proposeGeneration,
                cancellationToken);
        long operationGeneration =
            proposeGeneration;
        long operationRevision =
            revision;
        IsProposing = true;
        try
        {
            FaceGeomHairRegionsWizardProposalState result =
                await transaction.ProposeAsync(
                        currentAnalysis.AnalysisDocument,
                        request,
                        operation.Token)
                    .ConfigureAwait(true);
            if (!IsCurrent(
                    operationGeneration,
                    proposeGeneration,
                    operationRevision,
                    operation,
                    cancellationToken))
            {
                return;
            }

            AppendDiagnostics(
                result.Diagnostics);
            proposal = result;
            proposalRevision = revision;
            bytePlanEnvelopes =
                BuildBytePlanEnvelopes(
                    result.ProposalDocument.Value);
            RaiseState();
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            FinishOperation(
                ref proposeCancellation,
                operation,
                operationGeneration,
                proposeGeneration,
                () => IsProposing = false);
        }
    }

    public async ValueTask RenderPreviewAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        FaceGeomHairRegionsWizardProposalState currentProposal =
            CurrentProposal();
        ReviewedGameIntake intake =
            reviewedIntake ??
            throw new InvalidOperationException(
                "A reviewed game intake is required to render the exact resolved HairTint preview.");

        CancelOperation(
            ref previewCancellation,
            ref previewGeneration,
            () => IsRenderingPreview = false);
        CancelOperation(
            ref applyCancellation,
            ref applyGeneration,
            () => IsApplying = false);
        RetrySurvivingPreviewOutputs();
        ReleaseOwnedPreviewOutput();
        ClearPreviewState();
        if (!survivingPreviewOutputs.IsEmpty)
        {
            AppendDiagnostics(
            [
                new Diagnostic(
                    "hair-regions-preview-surviving-output-blocked",
                    DiagnosticSeverity.Error,
                    $"A new preview is blocked until surviving output is removed: {string.Join(", ", survivingPreviewOutputs.Select(path => path.Value))}")
            ]);
            return;
        }

        CancellationTokenSource operation =
            BeginOperation(
                ref previewCancellation,
                ref previewGeneration,
                cancellationToken);
        long operationGeneration =
            previewGeneration;
        long operationRevision =
            revision;
        IsRenderingPreview = true;
        WorkspacePath? freshOutputRoot = null;
        try
        {
            FaceGeomHairRegionsPreviewCacheAuthority authority =
                await transaction.ResolvePreviewCacheAuthorityAsync(
                        currentProposal,
                        intake,
                        operation.Token)
                    .ConfigureAwait(true);
            if (!IsCurrent(
                    operationGeneration,
                    previewGeneration,
                    operationRevision,
                    operation,
                    cancellationToken))
            {
                return;
            }

            if (!TryValidatePreviewAuthority(
                    authority,
                    currentProposal,
                    intake,
                    out string? authorityFailure))
            {
                AppendPreviewAuthorityMismatch(
                    authorityFailure);
                return;
            }

            if (cache is not null)
            {
                FaceGeomHairRegionsPreviewCacheLookupResult
                    lookup = cache.Lookup(authority.Key);
                AppendDiagnostics(
                    lookup.Diagnostics);
                if (lookup.Hit &&
                    lookup.Result is not null)
                {
                    if (!IsCurrent(
                            operationGeneration,
                            previewGeneration,
                            operationRevision,
                            operation,
                            cancellationToken))
                    {
                        return;
                    }

                    if (!TryValidatePreviewResult(
                            authority,
                            lookup.Result,
                            out string? cacheFailure))
                    {
                        AppendPreviewAuthorityMismatch(
                            $"Cached preview was refused: {cacheFailure}");
                        return;
                    }

                    PublishPreview(lookup.Result);
                    return;
                }
            }

            freshOutputRoot =
                NewPreviewOutputRoot(
                    operationGeneration);
            FaceGeomHairRegionsPreviewResult result =
                await transaction.PreviewAsync(
                        currentProposal,
                        intake,
                        freshOutputRoot.Value,
                        operation.Token)
                    .ConfigureAwait(true);
            if (!IsCurrent(
                    operationGeneration,
                    previewGeneration,
                    operationRevision,
                    operation,
                    cancellationToken))
            {
                return;
            }

            AppendDiagnostics(
                result.Diagnostics);
            if (!result.Succeeded)
            {
                PublishPreview(result);
                return;
            }

            if (!TryValidatePreviewResult(
                    authority,
                    result,
                    out string? resultFailure))
            {
                AppendPreviewAuthorityMismatch(
                    $"Fresh preview was refused: {resultFailure}");
                return;
            }

            if (cache is not null)
            {
                FaceGeomHairRegionsPreviewCacheStoreResult
                    stored =
                        cache.Store(
                            authority.Key,
                            result);
                AppendDiagnostics(
                    stored.Diagnostics);
                if (!IsCurrent(
                        operationGeneration,
                        previewGeneration,
                        operationRevision,
                        operation,
                        cancellationToken))
                {
                    return;
                }

                if (stored.Stored)
                {
                    if (stored.CachedResult is null)
                    {
                        AppendPreviewAuthorityMismatch(
                            "The cache reported success without returning the admitted cached result.");
                        return;
                    }

                    if (!TryValidatePreviewResult(
                            authority,
                            stored.CachedResult,
                            out string? storedFailure))
                    {
                        AppendPreviewAuthorityMismatch(
                            $"Stored preview was refused: {storedFailure}");
                        return;
                    }

                    PublishPreview(
                        stored.CachedResult);
                    return;
                }
            }

            PublishPreview(result);
            ownedPreviewOutputRoot =
                freshOutputRoot;
            freshOutputRoot = null;
        }
        catch (FaceGeomHairRegionsOperationCanceledException
               exception)
        {
            RegisterPreviewSurvivors(
                exception.SurvivingArtifacts);
            AppendDiagnostics(
            [
                new Diagnostic(
                    "hair-regions-preview-operation-canceled",
                    DiagnosticSeverity.Error,
                    $"Preview cancellation left artifacts: {exception.Message}")
            ]);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (FaceGeomHairRegionsOperationalException exception)
        {
            RegisterPreviewSurvivors(
                exception.SurvivingArtifacts);
            AppendDiagnostics(
            [
                new Diagnostic(
                    "hair-regions-preview-operation-failed",
                    DiagnosticSeverity.Error,
                    $"Preview operation failed and may have left artifacts: {exception.Message}")
            ]);
        }
        finally
        {
            if (freshOutputRoot is not null)
            {
                CleanupPreviewOutput(
                    freshOutputRoot.Value);
            }

            FinishOperation(
                ref previewCancellation,
                operation,
                operationGeneration,
                previewGeneration,
                () =>
                    IsRenderingPreview = false);
        }
    }

    public async ValueTask ApplyAndVerifyAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!CanWrite)
        {
            throw new InvalidOperationException(
                "Writing requires step five, a current successful preview, and explicit acceptance of the current byte plan.");
        }

        FaceGeomHairRegionsWizardProposalState currentProposal =
            CurrentProposal();
        CancellationTokenSource operation =
            BeginOperation(
                ref applyCancellation,
                ref applyGeneration,
                cancellationToken);
        long operationGeneration =
            applyGeneration;
        long operationRevision =
            revision;
        applyResult = null;
        applyResultRevision = -1;
        IsApplying = true;
        RaiseState();
        try
        {
            FaceGeomHairRegionsApplyResult result =
                await transaction.ApplyAsync(
                        currentProposal,
                        operation.Token)
                    .ConfigureAwait(true);
            RecordApplyResultSurvivors(result);
            if (!IsCurrent(
                    operationGeneration,
                    applyGeneration,
                    operationRevision,
                    operation,
                    cancellationToken))
            {
                if (MayHaveCommittedArtifacts(result))
                {
                    RegisterCommittedApplyArtifacts(
                        currentProposal,
                        result,
                        "hair-regions-apply-stale-committed-artifacts",
                        "A non-current or caller-cancelled Apply may already have committed output");
                }

                return;
            }

            applyResult = result;
            applyResultRevision =
                revision;
            AppendDiagnostics(
                result.Diagnostics);
            if (result.Verification is not null)
            {
                AppendDiagnostics(
                    result.Verification.Diagnostics);
            }

            if (MayHaveCommittedArtifacts(result) &&
                !IsVerifiedApplyResult(result))
            {
                RegisterCommittedApplyArtifacts(
                    currentProposal,
                    result,
                    "hair-regions-apply-unverified-committed-artifacts",
                    "Apply reported committed output that did not satisfy the current verification authority");
            }

            RaiseState();
        }
        catch (FaceGeomHairRegionsOperationCanceledException
               exception)
        {
            RegisterApplySurvivors(
                exception.SurvivingArtifacts);
            AppendDiagnostics(
            [
                new Diagnostic(
                    "hair-regions-apply-operation-canceled",
                    DiagnosticSeverity.Error,
                    $"Apply cancellation left artifacts: {exception.Message}")
            ]);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (FaceGeomHairRegionsOperationalException exception)
        {
            RegisterApplySurvivors(
                exception.SurvivingArtifacts);
            AppendDiagnostics(
            [
                new Diagnostic(
                    "hair-regions-apply-operation-failed",
                    DiagnosticSeverity.Error,
                    $"Apply failed and may have left artifacts: {exception.Message}")
            ]);
        }
        finally
        {
            FinishOperation(
                ref applyCancellation,
                operation,
                operationGeneration,
                applyGeneration,
                () => IsApplying = false);
            RaiseState();
        }
    }

    public bool TryAcceptProposal()
    {
        ThrowIfDisposed();
        if (CurrentStepIndex != 3 ||
            !HasCurrentProposal ||
            !IsPreviewCurrent)
        {
            return false;
        }

        if (!proposalAccepted)
        {
            proposalAccepted = true;
            RaiseState();
        }

        return true;
    }

    public bool MoveNext()
    {
        ThrowIfDisposed();
        bool permitted = CurrentStepIndex switch
        {
            0 => HasCurrentAnalysis,
            1 => HasCurrentAnalysis && Regions.Count > 0,
            2 => HasCurrentProposal && IsPreviewCurrent,
            3 => IsProposalAccepted,
            _ => false
        };
        if (!permitted)
        {
            return false;
        }

        CurrentStepIndex++;
        return true;
    }

    public bool MoveBack()
    {
        ThrowIfDisposed();
        if (CurrentStepIndex == 0)
        {
            return false;
        }

        CurrentStepIndex--;
        if (CurrentStepIndex == 0)
        {
            InvalidateAnalysis();
        }
        else
        {
            InvalidateProposal();
        }

        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CancelAllOperations(
            raiseState: false);
        RetrySurvivingPreviewOutputs();
        ReleaseOwnedPreviewOutput();
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        disposed = true;
    }

    internal void CancelActiveOperationsForClose()
    {
        if (disposed)
        {
            return;
        }

        CancelAllOperations(
            raiseState: true);
    }

    private FaceGeomHairRegionsWizardProposalState CurrentProposal()
    {
        if (!HasCurrentProposal)
        {
            throw new InvalidOperationException(
                "Create a byte plan for the current region and color revision before rendering.");
        }

        return proposal!;
    }

    private bool IsVerifiedApplyResult(
        FaceGeomHairRegionsApplyResult result)
    {
        FaceGeomHairRegionsManifest? manifest =
            result.Manifest;
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsManifest>?
            manifestDocument =
                result.ManifestDocument;
        FaceGeomHairRegionsVerification? verification =
            result.Verification;
        if (!result.Succeeded ||
            !HasCurrentProposal ||
            manifest is null ||
            manifestDocument is null ||
            verification is null ||
            !verification.Succeeded ||
            verification.ObservedOutput is null ||
            !result.SurvivingArtifacts.IsEmpty ||
            result.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error) ||
            verification.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
        {
            return false;
        }

        FaceGeomHairRegionsProposal current =
            proposal!.ProposalDocument.Value;
        return
            manifest.ProposalSha256 ==
                proposal.ProposalDocument.Sha256 &&
            manifest.Source ==
                current.Source &&
            manifest.Output ==
                current.ExpectedOutput &&
            manifestDocument.Value ==
                manifest &&
            verification.ObservedOutput ==
                current.ExpectedOutput;
    }

    private void PublishPreview(
        FaceGeomHairRegionsPreviewResult result)
    {
        preview = result;
        previewRevision = result.Succeeded
            ? revision
            : -1;
        proposalAccepted = false;
        foreach (FaceGeomHairRegionCardViewModel region in
                 Regions)
        {
            FaceGeomHairRegionsPreviewArtifact? thumbnail =
                result.Artifacts.FirstOrDefault(item =>
                    item.Kind ==
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionThumbnail &&
                    string.Equals(
                        item.StructuralId,
                        region.StructuralId,
                        StringComparison.Ordinal));
            FaceGeomHairRegionsPreviewArtifact? mask =
                result.Artifacts.FirstOrDefault(item =>
                    item.Kind ==
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionMask &&
                    string.Equals(
                        item.StructuralId,
                        region.StructuralId,
                        StringComparison.Ordinal));
            region.SetPreviewArtifacts(
                thumbnail?.Path,
                mask?.Path);
        }
        RaiseState();
    }

    private void SetLinkedRole(
        FaceGeomHairRegionCardViewModel changed,
        FaceGeomHairRegionRole role)
    {
        bool anyChanged = false;
        foreach (FaceGeomHairRegionCardViewModel region in Regions)
        {
            if (string.Equals(
                    region.SharedShaderGroupId,
                    changed.SharedShaderGroupId,
                    StringComparison.Ordinal))
            {
                anyChanged |=
                    region.ApplyLinkedRole(role);
            }
        }

        if (anyChanged)
        {
            UpdateRegionTargetColors();
            InvalidateProposal();
        }
    }

    private void InvalidateAnalysis()
    {
        revision++;
        CancelAllOperations(
            raiseState: true);
        RetrySurvivingPreviewOutputs();
        ReleaseOwnedPreviewOutput();
        analysis = null;
        Regions.Clear();
        if (CurrentStepIndex > 0)
        {
            CurrentStepIndex = 0;
        }

        ClearProposalState();
        RaiseState();
    }

    private void InvalidateProposal()
    {
        revision++;
        CancelAllOperations(
            raiseState: true);
        RetrySurvivingPreviewOutputs();
        ReleaseOwnedPreviewOutput();
        ClearProposalState();
        RaiseState();
    }

    private void ClearProposalState()
    {
        proposal = null;
        proposalRevision = -1;
        bytePlanEnvelopes = [];
        applyResult = null;
        applyResultRevision = -1;
        ClearPreviewState();
    }

    private void ClearPreviewState()
    {
        preview = null;
        previewRevision = -1;
        proposalAccepted = false;
        foreach (FaceGeomHairRegionCardViewModel region in
                 Regions)
        {
            region.SetPreviewArtifacts(
                null,
                null);
        }
        RaiseState();
    }

    private void UpdateRegionTargetColors()
    {
        foreach (FaceGeomHairRegionCardViewModel region in
                 Regions)
        {
            region.UpdateTargetColor(
                PrimaryColor,
                AccentColor);
        }
    }

    private void AppendDiagnostics(
        ImmutableArray<Diagnostic> additions)
    {
        if (additions.IsDefaultOrEmpty)
        {
            return;
        }

        diagnostics =
            diagnostics.AddRange(additions);
        if (!disposed)
        {
            Raise(nameof(Diagnostics));
        }
    }

    private void RaiseState()
    {
        if (disposed)
        {
            return;
        }

        Raise(nameof(HasCurrentAnalysis));
        Raise(nameof(HasCurrentProposal));
        Raise(nameof(IsPreviewCurrent));
        Raise(nameof(IsProposalAccepted));
        Raise(nameof(IsApplyResultCurrent));
        Raise(nameof(HasVerifiedApplyResult));
        Raise(nameof(CanWrite));
        Raise(nameof(Preview));
        Raise(nameof(ApplyResult));
        Raise(nameof(BytePlanEnvelopes));
        Raise(nameof(BytePlanProposalSha256));
        Raise(nameof(BytePlanExpectedOutputPath));
        Raise(nameof(BytePlanExpectedOutputSha256));
        Raise(nameof(BytePlanExpectedOutputLength));
        Raise(nameof(BytePlanManifestPath));
        Raise(nameof(BytePlanChangedByteOffsetsText));
    }

    private static ImmutableArray<
        FaceGeomHairRegionBytePlanViewModel>
        BuildBytePlanEnvelopes(
            FaceGeomHairRegionsProposal value) =>
        value.AuthorizedEnvelopes
            .OrderBy(
                envelope =>
                    envelope.ByteOffset)
            .Select(envelope =>
                new FaceGeomHairRegionBytePlanViewModel(
                    envelope.SharedShaderGroupId,
                    string.Join(
                        ", ",
                        envelope.StructuralIds),
                    envelope.Role.ToString(),
                    $"offset {envelope.ByteOffset} · {envelope.ByteLength} bytes · [{envelope.ByteOffset}, {envelope.ByteOffset + envelope.ByteLength})",
                    string.Join(
                        " · ",
                        envelope.OldFloatBits.Select(
                            bits =>
                                $"0x{bits:X8}")),
                    string.Join(
                        " · ",
                        envelope.NewFloatBits.Select(
                            bits =>
                                $"0x{bits:X8}")),
                    string.Join(
                        ", ",
                        value.PredictedChangedByteOffsets
                            .Where(offset =>
                                offset >=
                                    envelope.ByteOffset &&
                                offset <
                                    envelope.ByteOffset +
                                    envelope.ByteLength))))
            .ToImmutableArray();

    private void SetAnalysisTemplateValue(
        ref string field,
        string value,
        string propertyName)
    {
        if (!string.Equals(
                field,
                value,
                StringComparison.Ordinal))
        {
            field = value;
            Raise(propertyName);
        }
    }

    private WorkspacePath NewPreviewOutputRoot(
        long generation)
    {
        WorkspacePath admittedRoot =
            FaceGeomHairRegionsDesktopPathBoundary
                .PreviewRoot(labRoot);
        string path = Path.Combine(
            admittedRoot.Value,
            $"preview-{generation:D8}-{Guid.NewGuid():N}");
        var outputRoot =
            new WorkspacePath(path);
        FaceGeomHairRegionsDesktopPathBoundary
            .RequirePreviewOutput(
                labRoot,
                admittedRoot,
                outputRoot);
        return outputRoot;
    }

    private void CleanupPreviewOutput(
        WorkspacePath outputRoot)
    {
        ImmutableArray<Diagnostic> cleanupDiagnostics;
        try
        {
            cleanupDiagnostics =
                previewOutputCleanup.Cleanup(
                    outputRoot);
        }
        catch (Exception exception)
            when (exception is IOException or
                  UnauthorizedAccessException or
                  InvalidDataException or
                  ArgumentException)
        {
            cleanupDiagnostics =
            [
                new Diagnostic(
                    "hair-regions-preview-discard-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"Discarded preview survived at {outputRoot.Value}: {exception.Message}")
            ];
        }

        AppendDiagnostics(
            cleanupDiagnostics);
        if (cleanupDiagnostics.IsDefaultOrEmpty)
        {
            RemovePreviewSurvivor(
                outputRoot);
            return;
        }

        RegisterPreviewSurvivors(
            [outputRoot]);
    }

    private void ReleaseOwnedPreviewOutput()
    {
        WorkspacePath? owned =
            ownedPreviewOutputRoot;
        ownedPreviewOutputRoot = null;
        if (owned is not null)
        {
            CleanupPreviewOutput(
                owned.Value);
        }
    }

    private void RetrySurvivingPreviewOutputs()
    {
        ImmutableArray<WorkspacePath> pending =
            survivingPreviewOutputs;
        foreach (WorkspacePath path in pending)
        {
            CleanupPreviewOutput(path);
        }
    }

    private void RegisterPreviewSurvivors(
        ImmutableArray<WorkspacePath> paths)
    {
        if (paths.IsDefaultOrEmpty)
        {
            return;
        }

        ImmutableArray<WorkspacePath>.Builder builder =
            survivingPreviewOutputs.ToBuilder();
        foreach (WorkspacePath path in paths)
        {
            if (!builder.Any(existing =>
                    string.Equals(
                        existing.Value,
                        path.Value,
                        StringComparison.OrdinalIgnoreCase)))
            {
                builder.Add(path);
            }
        }

        ImmutableArray<WorkspacePath> updated =
            builder.ToImmutable();
        if (updated.Length ==
            survivingPreviewOutputs.Length)
        {
            return;
        }

        survivingPreviewOutputs = updated;
        if (!disposed)
        {
            Raise(nameof(SurvivingPreviewOutputs));
        }
    }

    private void RemovePreviewSurvivor(
        WorkspacePath path)
    {
        ImmutableArray<WorkspacePath> updated =
            survivingPreviewOutputs
                .Where(existing =>
                    !string.Equals(
                        existing.Value,
                        path.Value,
                        StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
        if (updated.Length ==
            survivingPreviewOutputs.Length)
        {
            return;
        }

        survivingPreviewOutputs = updated;
        if (!disposed)
        {
            Raise(nameof(SurvivingPreviewOutputs));
        }
    }

    private void RegisterApplySurvivors(
        ImmutableArray<WorkspacePath> paths)
    {
        if (paths.IsDefaultOrEmpty)
        {
            return;
        }

        ImmutableArray<WorkspacePath>.Builder builder =
            survivingApplyArtifacts.ToBuilder();
        foreach (WorkspacePath path in paths)
        {
            if (!builder.Any(existing =>
                    string.Equals(
                        existing.Value,
                        path.Value,
                        StringComparison.OrdinalIgnoreCase)))
            {
                builder.Add(path);
            }
        }

        ImmutableArray<WorkspacePath> updated =
            builder.ToImmutable();
        if (updated.Length ==
            survivingApplyArtifacts.Length)
        {
            return;
        }

        survivingApplyArtifacts = updated;
        if (!disposed)
        {
            Raise(nameof(SurvivingApplyArtifacts));
            Raise(nameof(HasVerifiedApplyResult));
            Raise(nameof(CanWrite));
        }
    }

    private void RecordApplyResultSurvivors(
        FaceGeomHairRegionsApplyResult result)
    {
        if (result.SurvivingArtifacts.IsDefaultOrEmpty)
        {
            return;
        }

        RegisterApplySurvivors(
            result.SurvivingArtifacts);
        AppendDiagnostics(
        [
            new Diagnostic(
                "hair-regions-apply-surviving-artifacts",
                DiagnosticSeverity.Error,
                $"Apply left artifacts that require inspection: {string.Join(", ", result.SurvivingArtifacts.Select(path => path.Value))}")
        ]);
    }

    private void RegisterCommittedApplyArtifacts(
        FaceGeomHairRegionsWizardProposalState appliedProposal,
        FaceGeomHairRegionsApplyResult result,
        string diagnosticCode,
        string diagnosticSummary)
    {
        ImmutableArray<WorkspacePath>.Builder paths =
            ImmutableArray.CreateBuilder<WorkspacePath>();
        AddDistinct(
            appliedProposal.ProposalDocument.Value.Output);
        AddDistinct(
            appliedProposal.ProposalDocument.Value.Manifest);
        if (result.Manifest is not null)
        {
            AddDistinct(
                result.Manifest.Output.Path);
        }

        if (result.Verification?.ObservedOutput is not null)
        {
            AddDistinct(
                result.Verification.ObservedOutput.Path);
        }

        ImmutableArray<WorkspacePath> committed =
            paths.ToImmutable();
        RegisterApplySurvivors(committed);
        AppendDiagnostics(
        [
            new Diagnostic(
                diagnosticCode,
                DiagnosticSeverity.Error,
                $"{diagnosticSummary}: {string.Join(", ", committed.Select(path => path.Value))}")
        ]);
        return;

        void AddDistinct(
            WorkspacePath path)
        {
            if (!paths.Any(existing =>
                    string.Equals(
                        existing.Value,
                        path.Value,
                        StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(path);
            }
        }
    }

    private static bool MayHaveCommittedArtifacts(
        FaceGeomHairRegionsApplyResult result) =>
        result.Succeeded ||
        result.Manifest is not null ||
        result.ManifestDocument is not null ||
        result.Verification?.ObservedOutput is not null;

    private static bool TryValidatePreviewAuthority(
        FaceGeomHairRegionsPreviewCacheAuthority authority,
        FaceGeomHairRegionsWizardProposalState proposal,
        ReviewedGameIntake intake,
        out string? failure)
    {
        FaceGeomHairRegionsPreviewCacheKey key =
            authority.Key;
        FaceGeomHairRegionsPreviewCacheKey recomputed =
            FaceGeomHairRegionsPreviewCacheKey.Create(
                key.SourceSha256,
                key.RequestSha256,
                key.ProposalSha256,
                key.IntakeSha256,
                key.RendererAuthoritySha256,
                key.RendererScriptSha256,
                key.TextureCatalogSha256,
                key.ResolvedTextureAuthoritySha256);
        if (authority.IntakeFingerprint !=
            intake.IntakeFingerprint)
        {
            failure =
                "The resolved intake fingerprint does not match the current reviewed intake.";
            return false;
        }

        if (key.SourceSha256 !=
            proposal.Materialization.Candidate.Sha256)
        {
            failure =
                "The cache source hash does not match the exact proposed FaceGeom candidate.";
            return false;
        }

        if (key.RequestSha256 !=
            proposal.RequestDocument.Sha256)
        {
            failure =
                "The cache request hash does not match the current request document.";
            return false;
        }

        if (key.ProposalSha256 !=
            proposal.ProposalDocument.Sha256)
        {
            failure =
                "The cache proposal hash does not match the current proposal document.";
            return false;
        }

        if (key.Fingerprint !=
            recomputed.Fingerprint)
        {
            failure =
                "The cache fingerprint does not match its complete authority tuple.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryValidatePreviewResult(
        FaceGeomHairRegionsPreviewCacheAuthority authority,
        FaceGeomHairRegionsPreviewResult result,
        out string? failure)
    {
        FaceGeomHairRegionsPreviewEvidence? evidence =
            result.Evidence;
        FaceGeomHairRegionsPreviewCacheKey key =
            authority.Key;
        if (!result.Succeeded ||
            evidence is null)
        {
            failure =
                "The preview is not successful or has no evidence authority.";
            return false;
        }

        if (evidence.StagedFaceGeomSha256 !=
                key.SourceSha256 ||
            evidence.ProposalSha256 !=
                key.ProposalSha256 ||
            evidence.IntakeSha256 !=
                key.IntakeSha256 ||
            evidence.RendererSha256 !=
                key.RendererScriptSha256 ||
            evidence.TextureFingerprintSha256 !=
                key.ResolvedTextureAuthoritySha256 ||
            evidence.RenderAuthority
                    .RendererScriptSha256 !=
                key.RendererScriptSha256 ||
            evidence.RenderAuthority
                    .TextureSourceFingerprintSha256 !=
                key.ResolvedTextureAuthoritySha256)
        {
            failure =
                "The preview evidence does not match the current source, proposal, intake, renderer, or texture authority.";
            return false;
        }

        if (evidence.RenderAuthority.RendererScriptSha256 !=
            evidence.RendererSha256)
        {
            failure =
                "The preview renderer evidence disagrees with its renderer-script authority.";
            return false;
        }

        if (result.Artifacts.IsDefaultOrEmpty)
        {
            failure =
                "A successful preview did not declare any rendered artifacts.";
            return false;
        }

        FaceGeomHairRegionsPreviewArtifact[] combinedFaces =
            result.Artifacts
                .Where(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace)
                .ToArray();
        if (combinedFaces.Length != 1 ||
            combinedFaces[0].NonEmptyPixelCount is not
                > 0 ||
            evidence.DetectedFaceCount != 1 ||
            evidence.LandmarkCount <= 0 ||
            evidence.SemanticAnchorCount != 31)
        {
            failure =
                "The preview did not prove one nonempty recognizable face with the admitted landmark and 31-anchor evidence.";
            return false;
        }

        if (result.VisualAuthority ||
            result.RuntimeAuthority)
        {
            failure =
                "The off-engine preview incorrectly claimed visual or runtime authority.";
            return false;
        }

        failure = null;
        return true;
    }

    private void AppendPreviewAuthorityMismatch(
        string? reason) =>
        AppendDiagnostics(
        [
            new Diagnostic(
                "hair-regions-preview-authority-mismatch",
                DiagnosticSeverity.Error,
                reason ??
                "Preview authority did not match the current request and proposal.")
        ]);

    private CancellationTokenSource BeginOperation(
        ref CancellationTokenSource? slot,
        ref long generation,
        CancellationToken callerCancellation)
    {
        CancelOperation(
            ref slot,
            ref generation,
            afterCancel: null);
        generation++;
        CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                lifetimeCancellation.Token);
        slot = operation;
        return operation;
    }

    private static void FinishOperation(
        ref CancellationTokenSource? slot,
        CancellationTokenSource operation,
        long operationGeneration,
        long currentGeneration,
        Action finishCurrent)
    {
        if (ReferenceEquals(slot, operation))
        {
            slot = null;
        }

        operation.Dispose();
        if (operationGeneration ==
            currentGeneration)
        {
            finishCurrent();
        }
    }

    private void CancelAllOperations(
        bool raiseState)
    {
        CancelOperation(
            ref analyzeCancellation,
            ref analyzeGeneration,
            () => isAnalyzing = false);
        CancelOperation(
            ref proposeCancellation,
            ref proposeGeneration,
            () => isProposing = false);
        CancelOperation(
            ref previewCancellation,
            ref previewGeneration,
            () => isRenderingPreview = false);
        CancelOperation(
            ref applyCancellation,
            ref applyGeneration,
            () => isApplying = false);
        if (raiseState &&
            !disposed)
        {
            Raise(nameof(IsAnalyzing));
            Raise(nameof(IsProposing));
            Raise(nameof(IsRenderingPreview));
            Raise(nameof(IsApplying));
            Raise(nameof(CanWrite));
        }
    }

    private static void CancelOperation(
        ref CancellationTokenSource? slot,
        ref long generation,
        Action? afterCancel)
    {
        generation++;
        CancellationTokenSource? active =
            slot;
        slot = null;
        active?.Cancel();
        afterCancel?.Invoke();
    }

    private bool IsCurrent(
        long operationGeneration,
        long currentGeneration,
        long operationRevision,
        CancellationTokenSource operation,
        CancellationToken callerCancellation) =>
        !disposed &&
        !operation.IsCancellationRequested &&
        !callerCancellation.IsCancellationRequested &&
        operationGeneration ==
            currentGeneration &&
        operationRevision ==
            revision;

    private static WorkspacePath RequiredPath(
        string value,
        string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{propertyName} must identify a new K-local path.");
        }

        return new WorkspacePath(value);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
