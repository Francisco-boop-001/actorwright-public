using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Product-owned orchestration from accepted RaceMenu intent to a complete,
/// independently verified FaceGeom NIF. External authoring tools are not part
/// of this transaction.
/// </summary>
public sealed partial class RaceMenuNpcFaceGeomBuildService : IRaceMenuNpcFaceGeomBuildService
{
    private readonly ISkyrimFaceBakeAuthorityLoader _authorityLoader;
    private readonly ISkyrimFaceRecordRouteResolver _recordRouteResolver;
    private readonly ISseSelectedHeadpartNifGeometryReader _geometryReader;
    private readonly ISkyrimRaceMenuFaceBakeService _faceBakeService;
    private readonly IRaceMenuCharGenFaceGeomMergeService _mergeService;

    public RaceMenuNpcFaceGeomBuildService(
        ISkyrimFaceBakeAuthorityLoader authorityLoader,
        ISkyrimFaceRecordRouteResolver recordRouteResolver,
        ISseSelectedHeadpartNifGeometryReader geometryReader,
        ISkyrimRaceMenuFaceBakeService faceBakeService,
        IRaceMenuCharGenFaceGeomMergeService mergeService)
    {
        _authorityLoader = authorityLoader ?? throw new ArgumentNullException(nameof(authorityLoader));
        _recordRouteResolver = recordRouteResolver ?? throw new ArgumentNullException(nameof(recordRouteResolver));
        _geometryReader = geometryReader ?? throw new ArgumentNullException(nameof(geometryReader));
        _faceBakeService = faceBakeService ?? throw new ArgumentNullException(nameof(faceBakeService));
        _mergeService = mergeService ?? throw new ArgumentNullException(nameof(mergeService));
    }

    public async ValueTask<RaceMenuNpcFaceGeomBuildResult> BuildAsync(
        RaceMenuNpcFaceGeomBuildRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        OwnedFaceGeomWrites? ownedWrites = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Preflight? preflight = await ValidatePreflightAsync(
                request, diagnostics, cancellationToken).ConfigureAwait(false);
            if (preflight is null || HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }

            SkyrimFaceBakeAuthorityLoadResult authorityLoad = await _authorityLoader.LoadAsync(
                new SkyrimFaceBakeAuthorityLoadRequest(
                    request.AllowedRoot,
                    request.FaceBakeAuthorityManifest,
                    request.ExpectedFaceBakeAuthorityManifestSha256),
                cancellationToken).ConfigureAwait(false);
            Append(diagnostics, authorityLoad.Diagnostics);
            if (!authorityLoad.Loaded || authorityLoad.Authority is null)
            {
                AddMissingAcceptedPayloadDiagnostic(authorityLoad.Loaded, authorityLoad.Authority,
                    "facegeom-authority-payload", diagnostics);
                return Refused(diagnostics);
            }

            SkyrimFaceBakeAuthority authority = authorityLoad.Authority;
            ImmutableArray<FormReference> selectedRoots = DeriveGeometrySelectedRoots(
                request.AppearancePlan, authority, diagnostics);
            if (HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }

            ImmutableArray<SkyrimFaceRecordHeadPartSelection> recordSelections =
                BuildRecordSelections(authority, selectedRoots, diagnostics);
            if (HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }

            SkyrimFaceRecordRouteResult routeResult = await _recordRouteResolver.ResolveAsync(
                new SkyrimFaceRecordRouteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.AppearancePlan.RaceBinding.Reference,
                    request.AppearancePlan.Request.Traits.Sex,
                    recordSelections,
                    authority.RecordPluginAuthorities), cancellationToken).ConfigureAwait(false);
            Append(diagnostics, routeResult.Diagnostics);
            if (!routeResult.Accepted || routeResult.Route is null)
            {
                AddMissingAcceptedPayloadDiagnostic(routeResult.Accepted, routeResult.Route,
                    "facegeom-record-route-payload", diagnostics);
                return Refused(diagnostics);
            }

            SkyrimFaceRecordRoute route = routeResult.Route;
            ValidateAuthorityAgainstRecordRoute(authority, route,
                request.AppearancePlan.RaceBinding.Reference, selectedRoots, diagnostics);
            if (HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }

            SkyrimRaceMenuFaceBakeRequest? bakeRequest = BuildBakeRequest(
                request, authority, route, diagnostics);
            if (bakeRequest is null || HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }

            SkyrimRaceMenuFaceBakeResult baked = _faceBakeService.Bake(bakeRequest);
            Append(diagnostics, baked.Diagnostics);
            ValidateBakedClosure(authority, baked, diagnostics);
            if (!baked.Accepted || HasErrors(diagnostics))
            {
                return Refused(diagnostics);
            }

            ownedWrites = new OwnedFaceGeomWrites(preflight.GeneratedXyzDirectory,
                request.OutputNif);
            StagedGeneratedXyz staged = await StageGeneratedXyzAsync(
                baked.Shapes, ownedWrites, cancellationToken).ConfigureAwait(false);

            var analyzeRequest = new RaceMenuCharGenFaceGeomMergeAnalyzeRequest(
                GameEdition.SkyrimSpecialEdition,
                request.SourceCharGenNif,
                preflight.SourceCharGenSha256,
                request.CompleteCarrierNif,
                preflight.CarrierSha256,
                request.OutputNif,
                staged.Authorities);
            RaceMenuCharGenFaceGeomMergeAnalysisResult analysis =
                await _mergeService.AnalyzeAsync(analyzeRequest, cancellationToken)
                    .ConfigureAwait(false);
            Append(diagnostics, analysis.Diagnostics);
            if (!analysis.Accepted || analysis.Proposal is null)
            {
                AddMissingAcceptedPayloadDiagnostic(analysis.Accepted, analysis.Proposal,
                    "facegeom-merge-proposal-payload", diagnostics);
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
                return Refused(diagnostics);
            }

            RaceMenuCharGenFaceGeomMergeResult applied = await _mergeService.ApplyAsync(
                analysis.Proposal, cancellationToken).ConfigureAwait(false);
            Append(diagnostics, applied.Diagnostics);
            bool appliedArtifactMatches = applied.Artifact is not null &&
                                          MergeArtifactMatches(analysis.Proposal,
                                              applied.Artifact);
            if (applied.Written && applied.Artifact is not null &&
                CanClaimOutputOwnership(analysis.Proposal, applied.Artifact))
            {
                ownedWrites.MarkOutputWritten(applied.Artifact.OutputSha256,
                    applied.Artifact.OutputByteLength);
            }
            else if (applied.Written)
            {
                diagnostics.Add(Error("facegeom-output-ownership-unproven",
                    "The merge reported a write without an exact output path/hash/length ownership envelope; the facade will not delete that path."));
            }

            bool appliedVerificationMatches = applied.Verification is not null &&
                                               VerificationMatches(analysis.Proposal,
                                                   applied.Verification);
            if (!applied.Written || !applied.Verified || applied.Artifact is null ||
                !appliedArtifactMatches || !appliedVerificationMatches)
            {
                AddMissingAcceptedPayloadDiagnostic(applied.Written && applied.Verified,
                    applied.Artifact, "facegeom-merge-artifact-payload", diagnostics);
                if (applied.Artifact is not null && !appliedArtifactMatches)
                {
                    diagnostics.Add(Error("facegeom-merge-artifact-drift",
                        "The written merge artifact did not retain the exact analyzed proposal and output evidence."));
                }
                if (applied.Verification is null || !appliedVerificationMatches)
                {
                    diagnostics.Add(Error("facegeom-merge-verification-drift",
                        "The apply result did not retain exact post-write identity, structure, ordered-shape, lane, and radius evidence."));
                }
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
                return Refused(diagnostics);
            }

            RaceMenuCharGenFaceGeomMergeVerificationResult independent =
                await _mergeService.VerifyAsync(analysis.Proposal, cancellationToken)
                    .ConfigureAwait(false);
            Append(diagnostics, independent.Diagnostics);
            if (!VerificationMatches(analysis.Proposal, independent) ||
                independent.OutputSha256 != applied.Artifact.OutputSha256)
            {
                diagnostics.Add(Error("facegeom-independent-verification",
                    "The independently reopened FaceGeom output did not verify the exact identity, structure, ordered-shape, lane, and radius closure."));
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
                return Refused(diagnostics);
            }

            ImmutableArray<RaceMenuNpcFaceGeomShapeEvidence> shapeEvidence =
                BuildShapeEvidence(authority, route, baked.Shapes, staged.Files, diagnostics);
            if (HasErrors(diagnostics))
            {
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
                return Refused(diagnostics);
            }

            ownedWrites.Commit();
            diagnostics.Add(new Diagnostic("facegeom-product-bake-complete",
                DiagnosticSeverity.Info,
                $"Compiled and independently verified {shapeEvidence.Length} generated carrier shapes without a finished-output oracle."));
            var artifact = new RaceMenuNpcFaceGeomBuildArtifact(
                "PRODUCT_FACEGEOM_GENERATED_XYZ_VERIFIED",
                authority.AuthorityId,
                authority.ManifestSha256,
                request.SourceCharGenNif,
                preflight.SourceCharGenSha256,
                request.CompleteCarrierNif,
                preflight.CarrierSha256,
                request.OutputNif,
                independent.OutputSha256.Value,
                selectedRoots,
                route,
                shapeEvidence,
                applied.Artifact,
                independent,
                RuntimeAuthority: false);
            return new RaceMenuNpcFaceGeomBuildResult(true, true, artifact,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (ownedWrites is not null)
            {
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or OverflowException or
                                          ArgumentException)
        {
            diagnostics.Add(Error("facegeom-build-failed", exception.Message));
            if (ownedWrites is not null)
            {
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
            }
            return Refused(diagnostics);
        }
        finally
        {
            if (ownedWrites is not null)
            {
                await ownedWrites.RollBackAsync(diagnostics).ConfigureAwait(false);
            }
        }
    }

    private static bool CanClaimOutputOwnership(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        RaceMenuCharGenFaceGeomMergeArtifact artifact) =>
        MergeProposalMatches(proposal, artifact.Proposal) &&
        artifact.OutputSha256 == proposal.ExpectedOutputSha256 &&
        artifact.OutputByteLength == proposal.ExpectedOutputByteLength;

    private static bool MergeArtifactMatches(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        RaceMenuCharGenFaceGeomMergeArtifact artifact) =>
        MergeProposalMatches(proposal, artifact.Proposal) &&
        artifact.OutputSha256 == proposal.ExpectedOutputSha256 &&
        artifact.OutputByteLength == proposal.ExpectedOutputByteLength &&
        StructureMatches(artifact.Structure, proposal.CarrierStructure) &&
        artifact.RoutedShapeCount == proposal.ShapeDispositions.Length &&
        artifact.ChangedPositionShapeCount == proposal.ChangedPositionShapeCount &&
        artifact.ExpandedRadiusCount == proposal.ExpandedRadiusCount &&
        !artifact.CreationKitAuthority && !artifact.RuntimeAuthority;

    private static bool VerificationMatches(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        RaceMenuCharGenFaceGeomMergeVerificationResult verification)
    {
        if (!verification.Verified || verification.OutputNif != proposal.OutputNif ||
            verification.CharGenSha256 != proposal.CharGenSha256 ||
            verification.CarrierSha256 != proposal.CarrierSha256 ||
            verification.OutputSha256 != proposal.ExpectedOutputSha256 ||
            verification.OutputByteLength != proposal.ExpectedOutputByteLength ||
            verification.OutputStructure is null ||
            !StructureMatches(verification.OutputStructure, proposal.CarrierStructure) ||
            verification.VerifiedPositionLaneCount !=
            proposal.ShapeDispositions.Sum(item => item.VertexCount) ||
            verification.VerifiedRadiusCount != proposal.ExpandedRadiusCount)
        {
            return false;
        }

        // Verification order is the complete carrier's physical traversal order,
        // which the analyzed proposal preserves. Manifest carrier order is an
        // authority declaration order and need not match NIF graph order.
        ImmutableArray<string> expectedNames = proposal.ShapeDispositions
            .Select(item => item.CarrierShapeName).ToImmutableArray();
        return !verification.VerifiedShapeNames.IsDefault &&
               verification.VerifiedShapeNames.SequenceEqual(expectedNames,
                   StringComparer.Ordinal);
    }

    private static bool MergeProposalMatches(
        RaceMenuCharGenFaceGeomMergeProposal left,
        RaceMenuCharGenFaceGeomMergeProposal right) =>
        string.Equals(left.SchemaVersion, right.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(left.Operation, right.Operation, StringComparison.Ordinal) &&
        left.Edition == right.Edition &&
        left.CharGenNif == right.CharGenNif &&
        left.CharGenSha256 == right.CharGenSha256 &&
        left.CharGenByteLength == right.CharGenByteLength &&
        left.CharGenBlockCount == right.CharGenBlockCount &&
        left.CharGenDynamicShapeCount == right.CharGenDynamicShapeCount &&
        left.CarrierNif == right.CarrierNif &&
        left.CarrierSha256 == right.CarrierSha256 &&
        left.CarrierByteLength == right.CarrierByteLength &&
        StructureMatches(left.CarrierStructure, right.CarrierStructure) &&
        left.OutputNif == right.OutputNif &&
        left.ExpectedOutputSha256 == right.ExpectedOutputSha256 &&
        left.ExpectedOutputByteLength == right.ExpectedOutputByteLength &&
        left.ShapeDispositions.SequenceEqual(right.ShapeDispositions) &&
        left.ChangedPositionShapeCount == right.ChangedPositionShapeCount &&
        left.ExpandedRadiusCount == right.ExpandedRadiusCount &&
        !left.CreationKitAuthority && !left.RuntimeAuthority &&
        !right.CreationKitAuthority && !right.RuntimeAuthority;

    private static bool StructureMatches(
        QualifiedFaceGeomCarrierStructure left,
        QualifiedFaceGeomCarrierStructure right) =>
        left.BlockCount == right.BlockCount &&
        left.ReachableBlockCount == right.ReachableBlockCount &&
        left.RootCount == right.RootCount &&
        left.NiNodeCount == right.NiNodeCount &&
        left.FadeNodeCount == right.FadeNodeCount &&
        left.DynamicShapeCount == right.DynamicShapeCount &&
        left.NullChildReferenceCount == right.NullChildReferenceCount &&
        left.GraphSha256 == right.GraphSha256 &&
        left.ReachableShapeNames.SequenceEqual(right.ReachableShapeNames,
            StringComparer.Ordinal) &&
        left.ReachableCensus.SequenceEqual(right.ReachableCensus);

    private static void AddMissingAcceptedPayloadDiagnostic(
        bool accepted,
        object? payload,
        string code,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (accepted && payload is null)
        {
            diagnostics.Add(Error(code, "An accepted dependency result omitted its required payload."));
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void Append(ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source) => target.AddRange(source);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuNpcFaceGeomBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, diagnostics.ToImmutable());
}
