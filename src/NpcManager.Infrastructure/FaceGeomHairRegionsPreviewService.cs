using System.ComponentModel;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Owns the truthful preview transaction boundary. The exact proposal
/// materialization is handed to the renderer in memory; this service never
/// invokes Apply and never writes the proposal's final NIF or manifest.
/// </summary>
public sealed class FaceGeomHairRegionsPreviewService :
    IFaceGeomHairRegionsPreviewService,
    IDisposable
{
    private const long MaximumCandidateBytes =
        128L * 1024L * 1024L;
    private readonly IFaceGeomHairRegionsRenderer renderer;
    private readonly IFaceGeomHairRegionsPreviewSourceService?
        sourceService;
    private readonly FaceGeomHairRegionsWorkspaceBoundary?
        boundary;
    private readonly WorkspacePath? workRoot;
    private readonly INpcVisualPreviewVisualValidator?
        visualValidator;
    private readonly FaceGeomHairRegionsDocumentCodec?
        documents;
    private readonly FaceGeomHairRegionsPinnedDirectory?
        ownedWorkRoot;
    private bool disposed;

    public FaceGeomHairRegionsPreviewService(
        IFaceGeomHairRegionsRenderer renderer)
    {
        this.renderer = renderer ??
            throw new ArgumentNullException(nameof(renderer));
    }

    public FaceGeomHairRegionsPreviewService(
        WorkspacePath workspaceRoot,
        WorkspacePath workRoot,
        IFaceGeomHairRegionsPreviewSourceService sourceService,
        IFaceGeomHairRegionsRenderer renderer,
        bool createOwnedWorkRoot = false)
    {
        this.renderer = renderer ??
            throw new ArgumentNullException(nameof(renderer));
        this.sourceService = sourceService ??
            throw new ArgumentNullException(nameof(sourceService));
        boundary = new FaceGeomHairRegionsWorkspaceBoundary(
            workspaceRoot);
        if (createOwnedWorkRoot)
            ownedWorkRoot = boundary.CreateOwnedDirectory(
                workRoot,
                "hair-regions preview work root");
        else
            boundary.RequireExistingDirectory(
                workRoot,
                "hair-regions preview work root");
        this.workRoot = workRoot;
    }

    public FaceGeomHairRegionsPreviewService(
        WorkspacePath workspaceRoot,
        WorkspacePath workRoot,
        IFaceGeomHairRegionsPreviewSourceService sourceService,
        IFaceGeomHairRegionsRenderer renderer,
        INpcVisualPreviewVisualValidator visualValidator,
        FaceGeomHairRegionsDocumentCodec documents)
        : this(
            workspaceRoot,
            workRoot,
            sourceService,
            renderer)
    {
        this.visualValidator = visualValidator ??
            throw new ArgumentNullException(
                nameof(visualValidator));
        this.documents = documents ??
            throw new ArgumentNullException(nameof(documents));
        if (this.documents.WorkspaceBoundary.WorkspaceRoot !=
            workspaceRoot)
            throw new ArgumentException(
                "The document boundary does not match the preview workspace.",
                nameof(documents));
        boundary = this.documents.WorkspaceBoundary;
    }

    public FaceGeomHairRegionsPreviewService(
        WorkspacePath workspaceRoot,
        WorkspacePath workRoot,
        IFaceGeomHairRegionsPreviewSourceService sourceService,
        IFaceGeomHairRegionsRenderer renderer,
        INpcVisualPreviewVisualValidator visualValidator,
        FaceGeomHairRegionsDocumentCodec documents,
        bool createOwnedWorkRoot)
        : this(
            workspaceRoot,
            workRoot,
            sourceService,
            renderer,
            createOwnedWorkRoot)
    {
        this.visualValidator = visualValidator ??
            throw new ArgumentNullException(
                nameof(visualValidator));
        this.documents = documents ??
            throw new ArgumentNullException(nameof(documents));
        if (this.documents.WorkspaceBoundary.WorkspaceRoot !=
            workspaceRoot)
            throw new ArgumentException(
                "The document boundary does not match the preview workspace.",
                nameof(documents));
        boundary = this.documents.WorkspaceBoundary;
    }

    public async ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (boundary is null ||
            visualValidator is null ||
            documents is null)
            return await PreviewCoreAsync(
                request,
                null,
                cancellationToken);
        return await PreviewOwnedOutputAsync(
            request,
            cancellationToken);
    }

    internal ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            FaceGeomHairRegionsPinnedDirectory outputRoot,
            CancellationToken cancellationToken) =>
        PreviewCoreAsync(
            request,
            outputRoot,
            cancellationToken);

    private async ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewOwnedOutputAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken)
    {
        FaceGeomHairRegionsPinnedDirectory? privateOutputRoot =
            null;
        WorkspacePath? privateRoot = null;
        try
        {
            boundary!.RequireNewDirectory(
                request.OutputRoot,
                "public hair-regions preview bundle");
            string parent =
                Path.GetDirectoryName(
                    request.OutputRoot.Value) ??
                throw new InvalidDataException(
                    "The public hair-regions preview bundle has no parent directory.");
            string name =
                Path.GetFileName(request.OutputRoot.Value);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException(
                    "The public hair-regions preview bundle has no directory name.");
            privateRoot = new WorkspacePath(
                Path.Combine(
                    parent,
                    $".{name}-preview-{Guid.NewGuid():N}.tmp"));
            boundary.RequireNewDirectory(
                privateRoot.Value,
                "private hair-regions preview bundle");
            privateOutputRoot =
                boundary.CreateOwnedDirectory(
                    privateRoot.Value,
                    "private hair-regions preview bundle");
            FaceGeomHairRegionsPreviewRequest privateRequest =
                request with
                {
                    OutputRoot = privateRoot.Value
                };
            FaceGeomHairRegionsPreviewResult result =
                await PreviewCoreAsync(
                    privateRequest,
                    privateOutputRoot,
                    cancellationToken);
            if (!result.Succeeded)
            {
                ImmutableArray<WorkspacePath> survivors =
                    privateOutputRoot.DeleteTree();
                privateOutputRoot = null;
                if (!survivors.IsDefaultOrEmpty)
                    throw new FaceGeomHairRegionsOperationalException(
                        "The public hair-region preview was refused and sibling-private cleanup left exact survivors.",
                        survivors,
                        new IOException(
                            "The refused preview bundle could not be removed completely."));
                return PublicRollbackResult(
                    result,
                    survivors);
            }
            ImmutableArray<
                FaceGeomHairRegionsPreviewArtifact>
                publicArtifacts =
                    RemapPublicArtifacts(
                        result.Artifacts,
                        privateRoot.Value,
                        request.OutputRoot);
            privateOutputRoot.PromoteNoOverwrite(
                request.OutputRoot);
            privateOutputRoot.Dispose();
            privateOutputRoot = null;
            return result with
            {
                Artifacts = publicArtifacts
            };
        }
        catch (
            FaceGeomHairRegionsOperationCanceledException
                exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                MergeExactSurvivors(
                    privateRoot,
                    RollbackPrivateOutput(
                        privateOutputRoot),
                    exception.SurvivingArtifacts);
            privateOutputRoot = null;
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Public hair-region preview was canceled; the sibling-private output bundle was rolled back and exact survivors are attached.",
                survivors,
                exception,
                exception.SurvivingProcessIds,
                exception.ProcessTerminationFailure);
        }
        catch (OperationCanceledException exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                RollbackPrivateOutput(
                    privateOutputRoot);
            privateOutputRoot = null;
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Public hair-region preview was canceled; the sibling-private output bundle was rolled back and exact survivors are attached.",
                survivors,
                exception);
        }
        catch (Exception exception) when (
            exception is
                IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                InvalidDataException or
                Win32Exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                RollbackPrivateOutput(
                    privateOutputRoot);
            privateOutputRoot = null;
            FaceGeomHairRegionsOperationalException?
                operational =
                    exception as
                        FaceGeomHairRegionsOperationalException;
            survivors = MergeExactSurvivors(
                privateRoot,
                survivors,
                operational?.SurvivingArtifacts ?? []);
            throw new FaceGeomHairRegionsOperationalException(
                "Public hair-region preview failed during an expected operational boundary; the sibling-private output bundle was rolled back and exact survivors are attached. " +
                exception.Message,
                survivors,
                exception,
                operational?.SurvivingProcessIds ?? [],
                operational?.ProcessTerminationFailure);
        }
        catch (Exception exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                RollbackPrivateOutput(
                    privateOutputRoot);
            privateOutputRoot = null;
            if (!survivors.IsDefaultOrEmpty)
                throw new FaceGeomHairRegionsOperationalException(
                    "Public hair-region preview failed unexpectedly; the sibling-private output bundle rollback left exact survivors attached. " +
                    exception.Message,
                    survivors,
                    exception);
            throw;
        }
        finally
        {
            privateOutputRoot?.Dispose();
        }
    }

    private async ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewCoreAsync(
            FaceGeomHairRegionsPreviewRequest request,
            FaceGeomHairRegionsPinnedDirectory? outputRoot,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        FaceGeomHairRegionsProposal proposal =
            request.ProposalDocument.Value;
        FaceGeomHairRegionsProposalMaterialization materialization =
            request.Materialization;
        byte[] candidateBytes =
            materialization.CandidateBytes.ToArray();
        if (materialization.Candidate !=
                proposal.ExpectedOutput ||
            materialization.CandidateBytes.IsDefaultOrEmpty ||
            materialization.CandidateBytes.Length !=
                materialization.Candidate.ByteLength ||
            FaceGeomHairRegionsSupport.Hash(
                candidateBytes) !=
                materialization.Candidate.Sha256)
            return Refused(
                "facegeom-hair-regions-preview-candidate-invalid",
                "Preview rejected materialization that does not equal the exact proposal candidate.");

        FaceGeomHairRegionsAnalysis analysis;
        try
        {
            analysis = FaceGeomHairRegionsSupport.AnalyzeBytes(
                materialization.Candidate,
                candidateBytes,
                proposal.PluginColorContext);
        }
        catch (InvalidDataException exception)
        {
            return Refused(
                "facegeom-hair-regions-preview-candidate-invalid",
                exception.Message);
        }

        ImmutableArray<string> expectedIds =
            proposal.Assignments
                .Select(item => item.StructuralId)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
        ImmutableArray<string> observedIds =
            analysis.Regions
                .Select(item => item.StructuralId)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
        if (!expectedIds.SequenceEqual(
                observedIds,
                StringComparer.Ordinal))
            return Refused(
                "facegeom-hair-regions-preview-region-drift",
                "Preview candidate regions do not match the proposal assignments.");

        if (sourceService is null)
            return await RenderAndProveAsync(
                request,
                analysis,
                CandidateOnlySource(
                    materialization.Candidate),
                [],
                outputRoot,
                cancellationToken);
        return await PreviewWithOwnedSessionAsync(
            request,
            analysis,
            outputRoot,
            cancellationToken);
    }

    private async ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewWithOwnedSessionAsync(
            FaceGeomHairRegionsPreviewRequest request,
            FaceGeomHairRegionsAnalysis analysis,
            FaceGeomHairRegionsPinnedDirectory? outputRoot,
            CancellationToken cancellationToken)
    {
        (
            WorkspacePath sessionRoot,
            FaceGeomHairRegionsPinnedDirectory createdLease) =
                CreatePreviewSession();
        FaceGeomHairRegionsPinnedDirectory? sessionLease =
            createdLease;
        FaceGeomHairRegionsPreviewResult result;
        try
        {
            FaceGeomHairRegionsPreviewSourceResult composed =
                await ComposeSourceAsync(
                    request,
                    sessionRoot,
                    sessionLease,
                    cancellationToken);
            result = await PreviewComposedSourceAsync(
                request,
                analysis,
                composed,
                outputRoot,
                cancellationToken);
        }
        catch (
            FaceGeomHairRegionsOperationCanceledException
                exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                MergeExactSurvivors(
                    sessionRoot,
                    DeletePreviewSession(
                        ref sessionLease),
                    exception.SurvivingArtifacts);
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Hair-region preview was canceled; its per-render source session was rolled back and exact survivors are attached.",
                survivors,
                exception,
                exception.SurvivingProcessIds,
                exception.ProcessTerminationFailure);
        }
        catch (OperationCanceledException exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                DeletePreviewSession(
                    ref sessionLease);
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Hair-region preview was canceled; its per-render source session was rolled back and exact survivors are attached.",
                survivors,
                exception);
        }
        catch (
            FaceGeomHairRegionsOperationalException
                exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                MergeExactSurvivors(
                    sessionRoot,
                    DeletePreviewSession(
                        ref sessionLease),
                    exception.SurvivingArtifacts);
            throw new FaceGeomHairRegionsOperationalException(
                "Hair-region preview failed during an expected operational boundary; its per-render source session was rolled back and exact survivors are attached. " +
                exception.Message,
                survivors,
                exception,
                exception.SurvivingProcessIds,
                exception.ProcessTerminationFailure);
        }
        catch (Exception exception) when (
            exception is
                IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                InvalidDataException or
                Win32Exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                DeletePreviewSession(
                    ref sessionLease);
            throw new FaceGeomHairRegionsOperationalException(
                "Hair-region preview failed during an expected operational boundary; its per-render source session was rolled back and exact survivors are attached. " +
                exception.Message,
                survivors,
                exception);
        }
        catch (Exception exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                DeletePreviewSession(
                    ref sessionLease);
            if (!survivors.IsDefaultOrEmpty)
                throw new FaceGeomHairRegionsOperationalException(
                    "Hair-region preview failed unexpectedly and per-render source-session rollback left exact survivors attached. " +
                    exception.Message,
                    survivors,
                    exception);
            throw;
        }
        ImmutableArray<WorkspacePath> cleanupSurvivors =
            DeletePreviewSession(
                ref sessionLease);
        if (!cleanupSurvivors.IsDefaultOrEmpty)
            throw new FaceGeomHairRegionsOperationalException(
                "Hair-region preview completed, but per-render source-session cleanup left exact survivors.",
                cleanupSurvivors,
                new IOException(
                    "The per-render preview source session could not be removed completely."));
        return result;
    }

    private async ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewComposedSourceAsync(
            FaceGeomHairRegionsPreviewRequest request,
            FaceGeomHairRegionsAnalysis analysis,
            FaceGeomHairRegionsPreviewSourceResult composed,
            FaceGeomHairRegionsPinnedDirectory? outputRoot,
            CancellationToken cancellationToken)
    {
        if (composed.Diagnostics.IsDefault ||
            !composed.Composed ||
            composed.Source is null)
            return new FaceGeomHairRegionsPreviewResult(
                false,
                null,
                [],
                composed.Diagnostics.IsDefault
                    ? [
                        new Diagnostic(
                            "facegeom-hair-regions-source-invalid",
                            DiagnosticSeverity.Error,
                            "The source provider returned default diagnostics.")
                    ]
                    : composed.Diagnostics,
                VisualAuthority: false,
                RuntimeAuthority: false);
        ImmutableArray<Diagnostic> sourceDiagnostics =
            composed.Diagnostics;
        if (sourceDiagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
            return Refused(
                sourceDiagnostics,
                "facegeom-hair-regions-source-invalid",
                "A composed preview source may not carry an Error diagnostic.");
        FaceGeomHairRegionsPreviewSource source =
            composed.Source;
        FaceGeomHairRegionsProposalMaterialization
            materialization = request.Materialization;
        if (source.Candidate.Sha256 !=
                materialization.Candidate.Sha256 ||
            source.Candidate.Bytes !=
                materialization.Candidate.ByteLength ||
            source.Textures.IsDefault ||
            string.IsNullOrWhiteSpace(
                source.TextureFingerprintSha256.Value))
            return Refused(
                sourceDiagnostics,
                "facegeom-hair-regions-source-invalid",
                "The resolved preview source does not bind the exact candidate and sorted texture authority.");
        return await RenderAndProveAsync(
            request,
            analysis,
            source,
            sourceDiagnostics,
            outputRoot,
            cancellationToken);
    }

    private async ValueTask<FaceGeomHairRegionsPreviewResult>
        RenderAndProveAsync(
            FaceGeomHairRegionsPreviewRequest request,
            FaceGeomHairRegionsAnalysis analysis,
            FaceGeomHairRegionsPreviewSource source,
            ImmutableArray<Diagnostic> sourceDiagnostics,
            FaceGeomHairRegionsPinnedDirectory? outputRoot,
            CancellationToken cancellationToken)
    {
        FaceGeomHairRegionsRenderResult rendered =
            await renderer.RenderAsync(
                new FaceGeomHairRegionsRenderRequest(
                    source,
                    request.Materialization.CandidateBytes,
                    analysis.Regions,
                    request.OutputRoot),
                cancellationToken);
        ImmutableArray<Diagnostic> previewDiagnostics =
            rendered.Diagnostics.IsDefault
                ? sourceDiagnostics
                : sourceDiagnostics.AddRange(
                    rendered.Diagnostics);
        if (rendered.Diagnostics.IsDefault ||
            rendered.Artifacts.IsDefault)
            return Refused(
                previewDiagnostics,
                "facegeom-hair-regions-renderer-invalid",
                "The renderer returned default diagnostics or artifacts.");
        if (!rendered.Rendered)
            return new FaceGeomHairRegionsPreviewResult(
                false,
                null,
                rendered.Artifacts,
                previewDiagnostics,
                VisualAuthority: false,
                RuntimeAuthority: false);

        if (visualValidator is not null &&
            documents is not null &&
            boundary is not null)
            return await CloseProofAsync(
                request,
                source,
                rendered,
                previewDiagnostics,
                outputRoot,
                cancellationToken);

        return Refused(
            previewDiagnostics,
            "facegeom-hair-regions-preview-proof-incomplete",
            "The renderer completed, but closed output proof is not yet configured.");
    }

    private async ValueTask<FaceGeomHairRegionsPreviewResult>
        CloseProofAsync(
            FaceGeomHairRegionsPreviewRequest request,
            FaceGeomHairRegionsPreviewSource source,
            FaceGeomHairRegionsRenderResult rendered,
            ImmutableArray<Diagnostic> diagnostics,
            FaceGeomHairRegionsPinnedDirectory? outputRoot,
            CancellationToken cancellationToken)
    {
        const long maximumImageBytes =
            16L * 1024L * 1024L;
        const long maximumBundleBytes =
            256L * 1024L * 1024L;
        if (rendered.RendererSha256 is null ||
            rendered.TextureFingerprintSha256 is null ||
            rendered.Authority is null ||
            rendered.TextureFingerprintSha256 !=
                source.TextureFingerprintSha256 ||
            rendered.Authority.RendererScriptSha256 !=
                rendered.RendererSha256.Value ||
            rendered.Authority
                .TextureSourceFingerprintSha256 !=
                source.TextureFingerprintSha256 ||
            !RenderTextureAuthorityMatches(
                rendered.Authority,
                source.Textures) ||
            rendered.FaceGeomImportCount != 1 ||
            rendered.NifImportInvocationCount != 1 ||
            rendered.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            return Refused(
                diagnostics,
                "facegeom-hair-regions-renderer-invalid",
                "A successful render requires exact tool/texture hashes, one process import, and no Error diagnostic.");

        ImmutableArray<FaceGeomHairRegionAssignment>
            assignments =
                request.RequestDocument.Value.Assignments;
        if (rendered.Artifacts.Length !=
                2 + assignments.Length * 2 ||
            rendered.Artifacts.Any(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .EvidenceDocument) ||
            rendered.Artifacts.Count(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .CombinedFace) != 1 ||
            rendered.Artifacts.Count(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .ContactSheet) != 1)
            return Refused(
                diagnostics,
                "facegeom-hair-regions-renderer-invalid",
                "The renderer did not return the exact pre-evidence artifact set.");

        var assignmentIds = assignments.Select(item =>
                item.StructuralId)
            .ToHashSet(StringComparer.Ordinal);
        string[] thumbnailIds = rendered.Artifacts
            .Where(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .RegionThumbnail)
            .Select(item => item.StructuralId ?? "")
            .ToArray();
        string[] maskIds = rendered.Artifacts
            .Where(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .RegionMask)
            .Select(item => item.StructuralId ?? "")
            .ToArray();
        if (thumbnailIds.Length != assignments.Length ||
            maskIds.Length != assignments.Length ||
            thumbnailIds.Distinct(StringComparer.Ordinal)
                .Count() != assignments.Length ||
            maskIds.Distinct(StringComparer.Ordinal)
                .Count() != assignments.Length ||
            !thumbnailIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(assignmentIds) ||
            !maskIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(assignmentIds))
            return Refused(
                diagnostics,
                "facegeom-hair-regions-renderer-invalid",
                "Every assignment requires one unique thumbnail and mask.");

        var verified = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsPreviewArtifact>();
        var bytesByPath = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        var inspections = new Dictionary<
            string,
            FaceGeomHairRegionsPngInspection>(
            StringComparer.OrdinalIgnoreCase);
        var uniquePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        long cumulative = 0;
        try
        {
            foreach (FaceGeomHairRegionsPreviewArtifact artifact in
                     rendered.Artifacts)
            {
                bool isRegion = artifact.Kind is
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail or
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask;
                if (isRegion !=
                        !string.IsNullOrWhiteSpace(
                            artifact.StructuralId) ||
                    !uniquePaths.Add(artifact.Path.Value) ||
                    artifact.ByteLength is <= 0 or
                        > maximumImageBytes ||
                    artifact.Path ==
                        request.OutputRoot ||
                    !artifact.Path.IsUnder(
                        request.OutputRoot) ||
                    !Path.GetExtension(artifact.Path.Value)
                        .Equals(
                            ".png",
                            StringComparison.OrdinalIgnoreCase))
                    return Refused(
                        diagnostics,
                        "facegeom-hair-regions-renderer-invalid",
                        "A renderer artifact has an invalid role, path, or encoded length.");
                cumulative = checked(
                    cumulative + artifact.ByteLength);
                if (cumulative > maximumBundleBytes)
                    return Refused(
                        diagnostics,
                        "facegeom-hair-regions-renderer-invalid",
                        "The rendered artifact bundle exceeds its encoded byte limit.");
                if (artifact.Content.IsDefaultOrEmpty ||
                    artifact.Content.Length !=
                        artifact.ByteLength)
                    return Refused(
                        diagnostics,
                        "facegeom-hair-regions-renderer-invalid",
                        "Renderer did not return the explicit bounded artifact content handoff.");
                byte[] observed =
                    artifact.Content.ToArray();
                Sha256Hash observedHash = new(
                    Convert.ToHexString(
                        SHA256.HashData(observed)));
                if (observed.LongLength !=
                        artifact.ByteLength ||
                    observedHash != artifact.Sha256)
                    return Refused(
                        diagnostics,
                        "facegeom-hair-regions-renderer-invalid",
                        "A rendered artifact failed independent length/hash readback.");
                FaceGeomHairRegionsPngInspection inspection =
                    FaceGeomHairRegionsPreviewArtifactInspector
                        .InspectPng(observed);
                bool requiresFixedDimensions =
                    artifact.Kind !=
                    FaceGeomHairRegionsPreviewArtifactKind
                        .ContactSheet;
                if ((requiresFixedDimensions &&
                        (inspection.Width != 900 ||
                            inspection.Height != 900)) ||
                    inspection.Width <= 0 ||
                    inspection.Height <= 0 ||
                    inspection.NonEmptyPixelCount <= 0)
                    return Refused(
                        diagnostics,
                        "facegeom-hair-regions-renderer-invalid",
                        "Every rendered artifact must be nonempty; face and region artifacts must be 900x900 PNGs.");
                bytesByPath.Add(
                    artifact.Path.Value,
                    observed);
                inspections.Add(
                    artifact.Path.Value,
                    inspection);
                verified.Add(artifact with
                {
                    ByteLength = observed.LongLength,
                    Sha256 = observedHash,
                    NonEmptyPixelCount =
                        inspection.NonEmptyPixelCount,
                    Content = default
                });
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                OverflowException)
        {
            return Refused(
                diagnostics,
                "facegeom-hair-regions-renderer-invalid",
                exception.Message);
        }

        FaceGeomHairRegionsPreviewArtifact combined =
            verified.Single(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .CombinedFace);
        FaceGeomHairRegionsPreviewArtifact mask =
            verified.First(item =>
                item.Kind ==
                FaceGeomHairRegionsPreviewArtifactKind
                    .RegionMask);
        NpcVisualPreviewVisualEvidence visual =
            await visualValidator!.ValidateEncodedAsync(
                new NpcVisualPreviewView(
                    "face-front",
                    combined.Path,
                    combined.Sha256,
                    mask.Path,
                    mask.Sha256,
                    900,
                    900),
                bytesByPath[combined.Path.Value],
                cancellationToken);
        if (visual.Diagnostics.IsDefault ||
            visual.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error) ||
            visual.DetectedFaceCount != 1 ||
            visual.LandmarkCount != 478 ||
            visual.SemanticAnchorCount != 31 ||
            !visual.EyesNoseAndMouthBounded)
            return Refused(
                visual.Diagnostics.IsDefault
                    ? diagnostics
                    : diagnostics.AddRange(
                        visual.Diagnostics),
                "facegeom-hair-regions-visual-proof-invalid",
                "Combined-face bytes did not prove one face, 478 landmarks, 31 anchors, and bounded facial features.");

        try
        {
            foreach (FaceGeomHairRegionsPreviewArtifact artifact in
                     verified)
            {
                byte[] content =
                    bytesByPath[artifact.Path.Value];
                var temporary = new WorkspacePath(
                    Path.Combine(
                        request.OutputRoot.Value,
                        $".{Path.GetFileName(artifact.Path.Value)}-{Guid.NewGuid():N}.tmp"));
                await using FaceGeomHairRegionsOwnedFile file =
                    outputRoot is null
                        ? boundary!.CreateOwnedFile(
                            temporary,
                            artifact.Path,
                            "hair-regions preview image")
                        : outputRoot.CreateOwnedFile(
                            temporary,
                            artifact.Path,
                            "hair-regions preview image");
                byte[] readback =
                    await file.WriteAndReadbackAsync(
                        content,
                        maximumImageBytes,
                        cancellationToken);
                if (!readback.AsSpan().SequenceEqual(
                        content))
                    throw new InvalidDataException(
                        $"Preview image '{Path.GetFileName(artifact.Path.Value)}' failed exact pinned readback.");
                file.PromoteNoOverwrite();
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            return Refused(
                diagnostics,
                "facegeom-hair-regions-image-publication-invalid",
                exception.Message);
        }

        ImmutableArray<FaceGeomHairArtifactEvidence>
            artifactEvidence = verified
                .OrderBy(item => item.Kind switch
                {
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace => 0,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail => 1,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask => 2,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .ContactSheet => 3,
                    _ => 4
                })
                .ThenBy(
                    item => item.StructuralId ?? "",
                    StringComparer.Ordinal)
                .Select(item =>
                {
                    FaceGeomHairRegionsPngInspection inspection =
                        inspections[item.Path.Value];
                    return new FaceGeomHairArtifactEvidence(
                        item.Kind,
                        item.StructuralId,
                        Path.GetFileName(item.Path.Value),
                        item.Sha256,
                        item.ByteLength,
                        inspection.Width,
                        inspection.Height,
                        inspection.NonEmptyPixelCount);
                })
                .ToImmutableArray();

        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsPreviewEvidenceDocument>
            evidenceDocument = documents!.BindPreviewEvidence(
                new FaceGeomHairRegionsPreviewEvidenceDocument(
                    FaceGeomHairRegionSchemas.PreviewEvidence,
                    request.Materialization.Candidate.Sha256,
                    request.ProposalDocument.Sha256,
                    request.IntakeDocument.Sha256,
                    rendered.RendererSha256.Value,
                    source.TextureFingerprintSha256,
                    visual.DetectedFaceCount,
                    visual.LandmarkCount,
                    visual.SemanticAnchorCount,
                    VisualAuthority: false,
                    RuntimeAuthority: false,
                    rendered.Authority,
                    artifactEvidence));
        var evidencePath = new WorkspacePath(Path.Combine(
            request.OutputRoot.Value,
            "evidence.json"));
        var evidenceTemporary = new WorkspacePath(Path.Combine(
            request.OutputRoot.Value,
            $".evidence-{Guid.NewGuid():N}.tmp"));
        await using FaceGeomHairRegionsOwnedFile evidenceFile =
            outputRoot is null
                ? boundary!.CreateOwnedFile(
                    evidenceTemporary,
                    evidencePath,
                    "hair-regions preview evidence")
                : outputRoot.CreateOwnedFile(
                    evidenceTemporary,
                    evidencePath,
                    "hair-regions preview evidence");
        try
        {
            byte[] evidenceReadback =
                await evidenceFile.WriteAndReadbackAsync(
                    evidenceDocument.Utf8Json.AsMemory(),
                    1024L * 1024L,
                    cancellationToken);
            if (!evidenceReadback.AsSpan().SequenceEqual(
                    evidenceDocument.Utf8Json.AsSpan()))
                return Refused(
                    diagnostics,
                    "facegeom-hair-regions-evidence-invalid",
                    "Evidence JSON failed exact post-write readback.");
            evidenceFile.PromoteNoOverwrite();
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            bool removed =
                evidenceFile.TryDelete(
                    out string? cleanupFailure);
            return Refused(
                diagnostics,
                "facegeom-hair-regions-evidence-invalid",
                removed
                    ? exception.Message
                    : $"{exception.Message} Evidence cleanup failed: {cleanupFailure}");
        }

        verified.Add(
            new FaceGeomHairRegionsPreviewArtifact(
                FaceGeomHairRegionsPreviewArtifactKind
                    .EvidenceDocument,
                null,
                evidencePath,
                evidenceDocument.Utf8Json.Length,
                evidenceDocument.Sha256,
                null));
        var evidence =
            new FaceGeomHairRegionsPreviewEvidence(
                request.Materialization.Candidate.Sha256,
                request.ProposalDocument.Sha256,
                request.IntakeDocument.Sha256,
                rendered.RendererSha256.Value,
                source.TextureFingerprintSha256,
                evidenceDocument.Sha256,
                visual.DetectedFaceCount,
                visual.LandmarkCount,
                visual.SemanticAnchorCount,
                rendered.Authority);
        diagnostics = diagnostics.AddRange(
            visual.Diagnostics);
        return new FaceGeomHairRegionsPreviewResult(
            true,
            evidence,
            verified.ToImmutable(),
            diagnostics,
            VisualAuthority: false,
            RuntimeAuthority: false);
    }

    private static bool RenderTextureAuthorityMatches(
        FaceGeomHairRegionsRenderAuthority authority,
        ImmutableArray<FaceGeomHairTextureAuthority> source)
    {
        if (authority.PyniflyModuleCount <= 0 ||
            authority.OriginalProfileFileCount <= 0 ||
            authority.PyniflyModules.IsDefaultOrEmpty ||
            authority.PyniflyModules.Length !=
                authority.PyniflyModuleCount ||
            authority.Textures.IsDefault ||
            authority.Textures.Length != source.Length)
            return false;
        for (int index = 0;
             index < source.Length;
             index++)
        {
            FaceGeomHairTextureAuthority expected =
                source[index];
            FaceGeomHairTextureEvidence observed =
                authority.Textures[index];
            if (observed.AssetPath !=
                    expected.AssetPath ||
                observed.ProviderKind !=
                    expected.ProviderKind ||
                !string.Equals(
                    observed.Provider,
                    expected.Provider,
                    StringComparison.Ordinal) ||
                observed.SourceSha256 !=
                    expected.Sha256 ||
                observed.SourceBytes !=
                    expected.Bytes ||
                observed.DecodedPreviewBytes <= 0 ||
                observed.DecodeKind !=
                    "texconv-dds-to-png" ||
                observed.Bindings.IsDefaultOrEmpty)
                return false;
        }
        return true;
    }

    private async ValueTask<
        FaceGeomHairRegionsPreviewSourceResult>
        ComposeSourceAsync(
            FaceGeomHairRegionsPreviewRequest request,
            WorkspacePath sessionRoot,
            FaceGeomHairRegionsPinnedDirectory sessionLease,
            CancellationToken cancellationToken)
    {
        WorkspacePath textureRoot = sessionRoot;
        var stagedCandidate = new WorkspacePath(Path.Combine(
            sessionRoot.Value,
            "candidate.nif"));
        var stagedTemporary = new WorkspacePath(Path.Combine(
            sessionRoot.Value,
            $".candidate-{Guid.NewGuid():N}.tmp"));
        await using (FaceGeomHairRegionsOwnedFile owned =
                     sessionLease.CreateOwnedFile(
                         stagedTemporary,
                         stagedCandidate,
                         "hair-regions preview candidate"))
        {
            byte[] readback =
                await owned.WriteAndReadbackAsync(
                    request.Materialization.CandidateBytes
                        .AsMemory(),
                    MaximumCandidateBytes,
                    cancellationToken);
            if (!readback.AsSpan().SequenceEqual(
                    request.Materialization.CandidateBytes
                        .AsSpan()) ||
                FaceGeomHairRegionsSupport.Hash(readback) !=
                    request.Materialization.Candidate.Sha256)
                throw new InvalidDataException(
                    "The staged preview candidate failed exact byte readback.");
            owned.PromoteNoOverwrite();
        }

        var candidate = new FaceGeomHairRegionsFile(
            stagedCandidate,
            request.Materialization.Candidate.ByteLength,
            request.Materialization.Candidate.Sha256);
        return await sourceService!.ComposeAsync(
            new FaceGeomHairRegionsPreviewSourceRequest(
                candidate,
                request.Materialization.CandidateBytes,
                request.Intake,
                textureRoot),
            cancellationToken);
    }

    private (
        WorkspacePath Root,
        FaceGeomHairRegionsPinnedDirectory Lease)
        CreatePreviewSession()
    {
        FaceGeomHairRegionsWorkspaceBoundary exactBoundary =
            boundary ??
            throw new InvalidOperationException(
                "The real preview source boundary is missing.");
        WorkspacePath exactWorkRoot =
            workRoot ??
            throw new InvalidOperationException(
                "The real preview work root is missing.");
        var sessionRoot = new WorkspacePath(Path.Combine(
            exactWorkRoot.Value,
            $"preview-{Guid.NewGuid():N}"));
        FaceGeomHairRegionsPinnedDirectory sessionLease =
            ownedWorkRoot is null
                ? exactBoundary.CreateOwnedDirectory(
                    sessionRoot,
                    "hair-regions preview session")
                : ownedWorkRoot.CreateOwnedDirectory(
                    sessionRoot,
                    "hair-regions preview session");
        return (sessionRoot, sessionLease);
    }

    private static ImmutableArray<WorkspacePath>
        DeletePreviewSession(
            ref FaceGeomHairRegionsPinnedDirectory?
                sessionLease)
    {
        if (sessionLease is null)
            return [];
        ImmutableArray<WorkspacePath> survivors =
            sessionLease.DeleteTree();
        sessionLease = null;
        return survivors;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (ownedWorkRoot is null)
            return;
        ImmutableArray<WorkspacePath> survivors =
            ownedWorkRoot.DeleteTree();
        if (!survivors.IsDefaultOrEmpty)
            throw new IOException(
                "Hair-regions preview work cleanup left exact survivors: " +
                string.Join(
                    ", ",
                    survivors.Select(item => item.Value)));
    }

    private static FaceGeomHairRegionsPreviewResult
        PublicRollbackResult(
            FaceGeomHairRegionsPreviewResult result,
            ImmutableArray<WorkspacePath> survivors)
    {
        ImmutableArray<Diagnostic> diagnostics =
            result.Diagnostics.IsDefault
                ? [
                    new Diagnostic(
                        "facegeom-hair-regions-preview-result-invalid",
                        DiagnosticSeverity.Error,
                        "The preview returned default diagnostics.")
                ]
                : result.Diagnostics;
        if (!survivors.IsDefaultOrEmpty)
            diagnostics = diagnostics.Add(
                new Diagnostic(
                    "facegeom-hair-regions-preview-cleanup-failed",
                    DiagnosticSeverity.Error,
                    "The sibling-private preview bundle retained exact survivors: " +
                    string.Join(
                        ", ",
                        survivors.Select(item =>
                            item.Value))));
        return result with
        {
            Succeeded = false,
            Evidence = null,
            Artifacts = [],
            Diagnostics = diagnostics,
            VisualAuthority = false,
            RuntimeAuthority = false
        };
    }

    private static ImmutableArray<WorkspacePath>
        RollbackPrivateOutput(
            FaceGeomHairRegionsPinnedDirectory? outputRoot)
    {
        if (outputRoot is not null)
            return outputRoot.DeleteTree();
        return [];
    }

    private static ImmutableArray<WorkspacePath>
        MergeExactSurvivors(
            WorkspacePath? privateRoot,
            ImmutableArray<WorkspacePath> rollbackSurvivors,
            ImmutableArray<WorkspacePath> reportedSurvivors)
    {
        var exact = ImmutableArray.CreateBuilder<
            WorkspacePath>();
        exact.AddRange(
            rollbackSurvivors.IsDefault
                ? []
                : rollbackSurvivors);
        if (!reportedSurvivors.IsDefault)
            foreach (WorkspacePath survivor in
                     reportedSurvivors)
            {
                bool privateArtifact =
                    privateRoot is { } root &&
                    (string.Equals(
                         Path.GetFullPath(
                             survivor.Value),
                         Path.GetFullPath(root.Value),
                         StringComparison.OrdinalIgnoreCase) ||
                     survivor.IsUnder(root));
                if (!privateArtifact ||
                    Directory.Exists(survivor.Value) ||
                    File.Exists(survivor.Value))
                    exact.Add(survivor);
            }
        return exact
            .Distinct()
            .ToImmutableArray();
    }

    private static ImmutableArray<
        FaceGeomHairRegionsPreviewArtifact>
        RemapPublicArtifacts(
            ImmutableArray<
                FaceGeomHairRegionsPreviewArtifact> artifacts,
            WorkspacePath privateRoot,
            WorkspacePath publicRoot)
    {
        if (artifacts.IsDefault)
            throw new InvalidDataException(
                "A successful private preview returned default artifacts.");
        var remapped = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsPreviewArtifact>(
                artifacts.Length);
        foreach (FaceGeomHairRegionsPreviewArtifact artifact in
                 artifacts)
        {
            if (artifact is null ||
                !artifact.Path.IsUnder(privateRoot))
                throw new InvalidDataException(
                    "A successful private preview returned an artifact outside its exact bundle root.");
            string relative = Path.GetRelativePath(
                privateRoot.Value,
                artifact.Path.Value);
            if (string.IsNullOrWhiteSpace(relative) ||
                relative == "." ||
                Path.IsPathFullyQualified(relative) ||
                relative == ".." ||
                relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "A successful private preview returned a non-descendant artifact path.");
            remapped.Add(
                artifact with
                {
                    Path = new WorkspacePath(
                        Path.Combine(
                            publicRoot.Value,
                            relative))
                });
        }
        return remapped.ToImmutable();
    }

    private static FaceGeomHairRegionsPreviewResult Refused(
        string code,
        string message) =>
        Refused(
            [],
            code,
            message);

    private static FaceGeomHairRegionsPreviewResult Refused(
        ImmutableArray<Diagnostic> diagnostics,
        string code,
        string message) =>
        new(
            false,
            null,
            [],
            diagnostics.Add(
                new Diagnostic(
                    code,
                    DiagnosticSeverity.Error,
                    message)
            ),
            VisualAuthority: false,
            RuntimeAuthority: false);

    private static FaceGeomHairRegionsPreviewSource
        CandidateOnlySource(
            FaceGeomHairRegionsFile candidate)
    {
        var asset = new NpcVisualAsset(
            NpcVisualAssetRole.FaceGeom,
            new AssetPath(
                "meshes/npcmanager/hair-regions/candidate.nif"),
            $"exact-proposal:{candidate.Sha256.Value}",
            candidate.Sha256,
            candidate.ByteLength,
            candidate.Path,
            false,
            []);
        return new FaceGeomHairRegionsPreviewSource(
            asset,
            [],
            FaceGeomHairRegionsSupport.Hash([]));
    }
}
