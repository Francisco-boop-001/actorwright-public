using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.ReferencePreset.Tests;

internal static partial class ReferencePresetTransactionTests
{
    private static readonly string LabRoot = Path.GetFullPath(Environment.CurrentDirectory);

    public static async Task TestHashBoundTransactionalWorkflow()
    {
        string parent = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests");
        string token = Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N");
        string authorityRoot = Path.Combine(
            parent, "scratch-reference-authorities-" + token);
        string designRoot = Path.Combine(
            parent, "scratch-reference-design-" + token);
        string proposalRoot = Path.Combine(
            parent, "scratch-reference-proposal-" + token);
        string applyRoot = Path.Combine(
            parent, "scratch-reference-apply-" + token);
        string npcRoot = Path.Combine(
            parent, "scratch-reference-npc-" + token);
        Directory.CreateDirectory(authorityRoot);

        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(
            policy, new WorkspacePath(LabRoot));
        Sha256Hash runtimeManifest = Hash('8');
        AuthoritySet authorities =
            await WriteAuthoritiesAsync(
                sessions, authorityRoot, runtimeManifest);
        var comparison = new ControlledComparisonService();
        var writer = new ControlledPresetWriter();
        var downstream = new ControlledNpcBuildService();
        var transaction = new ReferencePresetAuthoringTransaction(
            policy,
            new WorkspacePath(LabRoot),
            runtimeManifest,
            sessions,
            new ControlledImageDecoder(),
            new ControlledInferenceService(),
            new ControlledDescriptionInterpreter(),
            new ControlledProjector(),
            new ControlledRenderInputBuilder(),
            new ControlledResponseMatrixBuilder(),
            new ControlledSolver(),
            comparison,
            writer,
            downstream);

        try
        {
            ReferencePresetDesignProposalResult design =
                await transaction.ProposeDesignAsync(
                    new ReferencePresetDesignProposalRequest(
                        authorities.Intake,
                        authorities.IntakeSha256,
                        new WorkspacePath(designRoot)),
                    null,
                    CancellationToken.None);
            Require(design.Completed &&
                    design.Proposal is not null &&
                    design.ProposalSha256 is not null &&
                    design.ResourceSnapshot is null &&
                    design.ResourceSnapshotSha256 is null &&
                    File.Exists(Path.Combine(
                        designRoot, "authoring-intake.json")) &&
                    File.Exists(Path.Combine(
                        designRoot, "landmark-proposal.json")),
                "design transaction did not preserve the proposal-only authority boundary");

            ReferencePresetWriteRequest proposalRequest =
                authorities.Request(
                    proposalRoot,
                    apply: false,
                    acceptedProposalSha256: null);
            ReferencePresetWriteResult proposed =
                await transaction.WritePresetAsync(
                    proposalRequest,
                    null,
                    CancellationToken.None);
            Require(proposed.Completed &&
                    proposed.Proposal is not null &&
                    proposed.ProposalSha256 is not null &&
                    proposed.VerifiedPreset is null &&
                    Directory.Exists(proposalRoot) &&
                    !Directory.Exists(Path.Combine(
                        proposalRoot, "preset")),
                $"proposal-only transaction failed: {Codes(proposed.Diagnostics)}");
            Sha256Hash acceptedProposal =
                proposed.ProposalSha256 ??
                throw new InvalidOperationException(
                    "proposal hash was absent");

            ReferencePresetWriteResult rejectedApply =
                await transaction.WritePresetAsync(
                    authorities.Request(
                        applyRoot,
                        apply: true,
                        acceptedProposalSha256: Hash('f')),
                    null,
                    CancellationToken.None);
            Require(!rejectedApply.Completed &&
                    !Directory.Exists(applyRoot) &&
                    rejectedApply.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-transaction-accepted-proposal"),
                "apply accepted a stale proposal hash or retained output");

            ReferencePresetWriteResult applied =
                await transaction.WritePresetAsync(
                    authorities.Request(
                        applyRoot,
                        apply: true,
                        acceptedProposalSha256: acceptedProposal),
                    null,
                    CancellationToken.None);
            Require(applied.Completed &&
                    applied.ProposalSha256 == acceptedProposal &&
                    applied.VerifiedPreset is not null &&
                    File.Exists(applied.VerifiedPreset.PresetPath.Value) &&
                    writer.WriteCount == 1 &&
                    writer.LastRequest is not null &&
                    writer.LastRequest.Proposal.Losses.All(
                        item => item.Acknowledged),
                $"accepted apply did not write and verify the exact proposal: {Codes(applied.Diagnostics)}");

            var currentBuildRequest =
                new RaceMenuNpcExecutionRequest(null!, null!);
            ReferencePresetNpcBuildResult npc =
                await transaction.WritePresetAndBuildNpcAsync(
                    new ReferencePresetNpcBuildRequest(
                        authorities.Request(
                            npcRoot,
                            apply: true,
                            acceptedProposalSha256:
                                acceptedProposal),
                        new RaceMenuJslotNpcBuildRequest(
                            currentBuildRequest,
                            new WorkspacePath(
                                Path.Combine(authorityRoot,
                                    "wrong.jslot")),
                            Hash('0'),
                            new WorkspacePath(authorityRoot),
                            [],
                            new WorkspacePath(authorityRoot))),
                    null,
                    CancellationToken.None);
            Require(npc.Completed &&
                    npc.Handoff is not null &&
                    downstream.LastRequest is not null &&
                    downstream.LastRequest.Preset ==
                        npc.Handoff.PresetPath &&
                    downstream.LastRequest.ExpectedPresetSha256 ==
                        npc.Handoff.PresetSha256 &&
                    ReferenceEquals(
                        downstream.LastRequest.CurrentRequest,
                        currentBuildRequest) &&
                    downstream.LastRequest.CompanionRoot.Value ==
                        Path.Combine(npcRoot, "npc") &&
                    npc.Handoff.Race ==
                        authorities.Intake.Race &&
                    npc.Handoff.Sex ==
                        authorities.Intake.Sex &&
                    npc.Handoff.Weight ==
                        authorities.Intake.Weight,
                $"verified preset was not handed to the existing NPC transaction exactly: {Codes(npc.Diagnostics)}");

            ReferencePresetWriteResult collision =
                await transaction.WritePresetAsync(
                    proposalRequest,
                    null,
                    CancellationToken.None);
            Require(!collision.Completed &&
                    collision.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-transaction-output-exists"),
                "transaction overwrote an existing output root");
        }
        finally
        {
            DeleteOwnedRoot(authorityRoot);
            DeleteOwnedRoot(designRoot);
            DeleteOwnedRoot(proposalRoot);
            DeleteOwnedRoot(applyRoot);
            DeleteOwnedRoot(npcRoot);
        }
    }

    public static async Task TestUndeclaredAndCancelledRollback()
    {
        string parent = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests");
        string token = Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N");
        string authorityRoot = Path.Combine(
            parent, "scratch-reference-negative-authorities-" + token);
        string undeclaredRoot = Path.Combine(
            parent, "scratch-reference-undeclared-" + token);
        string cancelledRoot = Path.Combine(
            parent, "scratch-reference-cancelled-" + token);
        string acceptedRoot = Path.Combine(
            parent, "scratch-reference-negative-proposal-" + token);
        string writerFailureRoot = Path.Combine(
            parent, "scratch-reference-writer-failure-" + token);
        string staleRoot = Path.Combine(
            parent, "scratch-reference-stale-" + token);
        string unacceptedRoot = Path.Combine(
            parent, "scratch-reference-unaccepted-" + token);
        Directory.CreateDirectory(authorityRoot);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(
            policy, new WorkspacePath(LabRoot));
        Sha256Hash runtimeManifest = Hash('8');
        AuthoritySet authorities =
            await WriteAuthoritiesAsync(
                sessions, authorityRoot, runtimeManifest);
        string unacceptedAuthorityRoot =
            Path.Combine(authorityRoot, "unaccepted");
        Directory.CreateDirectory(unacceptedAuthorityRoot);
        AuthoritySet unacceptedAuthorities =
            await WriteAuthoritiesAsync(
                sessions,
                unacceptedAuthorityRoot,
                runtimeManifest,
                reviewAccepted: false);

        try
        {
            var authorityTransaction =
                new ReferencePresetAuthoringTransaction(
                    policy,
                    new WorkspacePath(LabRoot),
                    runtimeManifest,
                    sessions,
                    new ControlledImageDecoder(),
                    new ControlledInferenceService(),
                    new ControlledDescriptionInterpreter(),
                    new ControlledProjector(),
                    new ControlledRenderInputBuilder(),
                    new ControlledResponseMatrixBuilder(),
                    new ControlledSolver(),
                    new ControlledComparisonService(),
                    new ControlledPresetWriter(),
                    new ControlledNpcBuildService());
            ReferencePresetWriteResult stale =
                await authorityTransaction.WritePresetAsync(
                    authorities.Request(
                        staleRoot,
                        apply: false,
                        acceptedProposalSha256: null) with
                    {
                        InferenceProposalSha256 = Hash('e')
                    },
                    null,
                    CancellationToken.None);
            Require(!stale.Completed &&
                    !Directory.Exists(staleRoot) &&
                    stale.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-session-hash-mismatch"),
                "stale inference parent hash was accepted");
            ReferencePresetWriteResult unaccepted =
                await authorityTransaction.WritePresetAsync(
                    unacceptedAuthorities.Request(
                        unacceptedRoot,
                        apply: false,
                        acceptedProposalSha256: null),
                    null,
                    CancellationToken.None);
            Require(!unaccepted.Completed &&
                    !Directory.Exists(unacceptedRoot) &&
                    unaccepted.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-review-not-accepted"),
                "unaccepted reviewed design entered the solve transaction");

            var undeclared = new ReferencePresetAuthoringTransaction(
                policy,
                new WorkspacePath(LabRoot),
                runtimeManifest,
                sessions,
                new ControlledImageDecoder(),
                new ControlledInferenceService(),
                new ControlledDescriptionInterpreter(),
                new ControlledProjector(),
                new ControlledRenderInputBuilder(),
                new ControlledResponseMatrixBuilder(),
                new ControlledSolver(),
                new ControlledComparisonService(
                    injectUndeclaredFile: true),
                new ControlledPresetWriter(),
                new ControlledNpcBuildService());
            ReferencePresetWriteResult refused =
                await undeclared.WritePresetAsync(
                    authorities.Request(
                        undeclaredRoot,
                        apply: false,
                        acceptedProposalSha256: null),
                    null,
                    CancellationToken.None);
            Require(!refused.Completed &&
                    !Directory.Exists(undeclaredRoot) &&
                    refused.Diagnostics.Any(item =>
                        item.Code ==
                        "reference-transaction-file-undeclared"),
                "undeclared comparison output was not rejected and rolled back");

            var acceptedTransaction =
                new ReferencePresetAuthoringTransaction(
                    policy,
                    new WorkspacePath(LabRoot),
                    runtimeManifest,
                    sessions,
                    new ControlledImageDecoder(),
                    new ControlledInferenceService(),
                    new ControlledDescriptionInterpreter(),
                    new ControlledProjector(),
                    new ControlledRenderInputBuilder(),
                    new ControlledResponseMatrixBuilder(),
                    new ControlledSolver(),
                    new ControlledComparisonService(),
                    new ControlledPresetWriter(),
                    new ControlledNpcBuildService());
            ReferencePresetWriteResult accepted =
                await acceptedTransaction.WritePresetAsync(
                    authorities.Request(
                        acceptedRoot,
                        apply: false,
                        acceptedProposalSha256: null),
                    null,
                    CancellationToken.None);
            Require(accepted.Completed &&
                    accepted.ProposalSha256 is not null,
                $"negative fixture proposal failed: {Codes(accepted.Diagnostics)}");
            var failingWriterTransaction =
                new ReferencePresetAuthoringTransaction(
                    policy,
                    new WorkspacePath(LabRoot),
                    runtimeManifest,
                    sessions,
                    new ControlledImageDecoder(),
                    new ControlledInferenceService(),
                    new ControlledDescriptionInterpreter(),
                    new ControlledProjector(),
                    new ControlledRenderInputBuilder(),
                    new ControlledResponseMatrixBuilder(),
                    new ControlledSolver(),
                    new ControlledComparisonService(),
                    new FailingPresetWriter(),
                    new ControlledNpcBuildService());
            ReferencePresetWriteResult writerFailure =
                await failingWriterTransaction.WritePresetAsync(
                    authorities.Request(
                        writerFailureRoot,
                        apply: true,
                        acceptedProposalSha256:
                            accepted.ProposalSha256),
                    null,
                    CancellationToken.None);
            Require(!writerFailure.Completed &&
                    !Directory.Exists(writerFailureRoot) &&
                    writerFailure.Diagnostics.Any(item =>
                        item.Code ==
                        "controlled-preset-write-failure"),
                "injected preset write failure retained transaction output");

            var cancelled = new ReferencePresetAuthoringTransaction(
                policy,
                new WorkspacePath(LabRoot),
                runtimeManifest,
                sessions,
                new ControlledImageDecoder(),
                new ControlledInferenceService(),
                new ControlledDescriptionInterpreter(),
                new ControlledProjector(),
                new ControlledRenderInputBuilder(),
                new ControlledResponseMatrixBuilder(),
                new CancellingSolver(),
                new ControlledComparisonService(),
                new ControlledPresetWriter(),
                new ControlledNpcBuildService());
            bool cancellationObserved = false;
            try
            {
                _ = await cancelled.WritePresetAsync(
                    authorities.Request(
                        cancelledRoot,
                        apply: false,
                        acceptedProposalSha256: null),
                    null,
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            Require(cancellationObserved &&
                    !Directory.Exists(cancelledRoot) &&
                    !Directory.EnumerateDirectories(
                        parent,
                        Path.GetFileName(cancelledRoot) +
                        ".tmp-*",
                        SearchOption.TopDirectoryOnly).Any(),
                "cancellation did not remove its transaction-owned staging root");
        }
        finally
        {
            DeleteOwnedRoot(authorityRoot);
            DeleteOwnedRoot(undeclaredRoot);
            DeleteOwnedRoot(cancelledRoot);
            DeleteOwnedRoot(acceptedRoot);
            DeleteOwnedRoot(writerFailureRoot);
            DeleteOwnedRoot(staleRoot);
            DeleteOwnedRoot(unacceptedRoot);
        }
    }

    private static async Task<AuthoritySet>
        WriteAuthoritiesAsync(
            IReferencePresetSessionService sessions,
            string root,
            Sha256Hash runtimeManifest,
            bool reviewAccepted = true)
    {
        (PresetDocument baseline,
            ReferencePresetResourceSnapshot fixtureSnapshot,
            _, _) = ReferencePresetWriterTests.Fixture();
        ReferencePresetIntake seed = ReferencePresetRulesTests.ValidIntake();
        ReferencePresetIntake intake =
            seed with
            {
                BaselineJslot = new WorkspacePath(Path.Combine(root, "baseline.jslot")),
                BaselineJslotSha256 = baseline.SourceHash,
                Images = seed.Images.Select(image => image with
                {
                    SourcePath = new WorkspacePath(Path.Combine(root, image.ImageId + ".png"))
                }).ToImmutableArray(),
                Target = seed.Target with { DataRoot = new WorkspacePath(Path.Combine(root, "Data")) }
            };
        (WorkspacePath intakePath, Sha256Hash intakeHash) =
            await WriteAsync(
                sessions,
                root,
                "intake.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind.Intake,
                    Intake: intake));

        LandmarkInferenceProposal inference =
            ReferencePresetRulesTests.ValidInferenceProposal()
            with
            {
                IntakeSha256 = intakeHash,
                RuntimeManifestSha256 = runtimeManifest
            };
        (WorkspacePath inferencePath, Sha256Hash inferenceHash) =
            await WriteAsync(
                sessions,
                root,
                "inference.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind
                        .InferenceProposal,
                    InferenceProposal: inference));

        ReviewedReferencePresetDesign review =
            ReferencePresetRulesTests.ValidReviewedDesign()
            with
            {
                ProposalSha256 = inferenceHash,
                ReviewAccepted = reviewAccepted
            };
        (WorkspacePath reviewPath, Sha256Hash reviewHash) =
            await WriteAsync(
                sessions,
                root,
                "review.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind
                        .ReviewedDesign,
                    ReviewedDesign: review));

        ReferencePresetResourceSnapshot snapshot =
            fixtureSnapshot with
            {
                ProjectId = intake.ProjectId,
                ReviewedDesignSha256 = reviewHash,
                BaselineJslotSha256 =
                    intake.BaselineJslotSha256,
                Baseline = baseline,
                CatalogSelection =
                    review.CatalogSelection
            };
        (WorkspacePath snapshotPath, Sha256Hash snapshotHash) =
            await WriteAsync(
                sessions,
                root,
                "snapshot.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind
                        .ResourceSnapshot,
                    ResourceSnapshot: snapshot));
        return new AuthoritySet(
            intake,
            intakePath,
            intakeHash,
            inferencePath,
            inferenceHash,
            reviewPath,
            reviewHash,
            snapshotPath,
            snapshotHash);
    }

    private static async Task<(WorkspacePath Path, Sha256Hash Hash)>
        WriteAsync(
            IReferencePresetSessionService sessions,
            string root,
            string fileName,
            ReferencePresetSessionDocument document)
    {
        var path = new WorkspacePath(
            Path.Combine(root, fileName));
        ReferencePresetSessionWriteResult result =
            await sessions.WriteAsync(
                new ReferencePresetSessionWriteRequest(
                    path, document),
                CancellationToken.None);
        Require(result.Written &&
                result.ContentSha256 is not null,
            $"authority session write failed: {Codes(result.Diagnostics)}");
        return (path, result.ContentSha256!.Value);
    }

    private sealed record AuthoritySet(
        ReferencePresetIntake Intake,
        WorkspacePath IntakePath,
        Sha256Hash IntakeSha256,
        WorkspacePath InferencePath,
        Sha256Hash InferenceSha256,
        WorkspacePath ReviewPath,
        Sha256Hash ReviewSha256,
        WorkspacePath SnapshotPath,
        Sha256Hash SnapshotSha256)
    {
        public ReferencePresetWriteRequest Request(
            string output,
            bool apply,
            Sha256Hash? acceptedProposalSha256) =>
            new(
                IntakePath,
                IntakeSha256,
                InferencePath,
                InferenceSha256,
                ReviewPath,
                ReviewSha256,
                SnapshotPath,
                SnapshotSha256,
                new WorkspacePath(output),
                apply,
                acceptedProposalSha256);
    }

    private sealed class ControlledImageDecoder :
        IReferenceImageDecoder
    {
        public ValueTask<ReferenceImageDecodeResult> DecodeAsync(
            ReferenceImageDecodeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferenceImageDecodeResult(
                    new DecodedReferenceImage(
                        request.Image.ImageId,
                        request.Image.ViewRole,
                        request.Image.SourcePath,
                        request.Image.SourceSha256,
                        request.Image.EncodedLength,
                        ReferenceImageFormat.Png,
                        ReferenceImageOrientation.TopLeft,
                        1,
                        1,
                        4,
                        [0, 0, 0, 255],
                        Hash('1')),
                    []));
        }
    }

    private sealed class ControlledInferenceService :
        IReferenceFaceInferenceService
    {
        public ValueTask<ReferenceFaceInferenceResult> InferAsync(
            ReferenceFaceInferenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReferenceImageInference fixture =
                ReferencePresetRulesTests
                    .ValidInferenceProposal().Images[0];
            return ValueTask.FromResult(
                new ReferenceFaceInferenceResult(
                    fixture with
                    {
                        ImageId = request.Image.ImageId,
                        ViewRole = request.Image.ViewRole,
                        SourceSha256 =
                            request.Image.SourceSha256,
                        CanonicalRgbaSha256 =
                            request.Image.CanonicalRgbaSha256,
                        Width = request.Image.Width,
                        Height = request.Image.Height
                    },
                    []));
        }
    }

    private sealed class ControlledDescriptionInterpreter :
        IReferenceDescriptionInterpreter
    {
        public ValueTask<ReferenceDescriptionInterpretResult>
            InterpretAsync(
                ReferenceDescriptionInterpretRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LandmarkInferenceProposal fixture =
                ReferencePresetRulesTests
                    .ValidInferenceProposal();
            return ValueTask.FromResult(
                new ReferenceDescriptionInterpretResult(
                    fixture.Traits,
                    fixture.Unknowns,
                    []));
        }
    }

    private sealed class ControlledProjector :
        IReferenceSemanticLandmarkProjector
    {
        public ValueTask<
            ReferenceSemanticLandmarkProjectionResult> ProjectAsync(
                ReferenceSemanticLandmarkProjectionRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferenceSemanticLandmarkProjectionResult(
                    [
                        new ReferenceSemanticAnchorProposal(
                            ReferenceSemanticAnchorKind
                                .ForeheadCenter,
                            10,
                            0.5,
                            0.2,
                            0.9,
                            true,
                            false)
                    ],
                    []));
        }
    }

    private sealed class ControlledRenderInputBuilder :
        IReferencePresetRenderInputBuilder
    {
        public ValueTask<ReferencePresetRenderInputResult>
            BuildAsync(
                ReferencePresetRenderInputRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferencePresetRenderInputResult(
                    new ReferencePresetRenderInput(
                        [], [], [], Hash('2')),
                    []));
        }
    }

    private sealed class ControlledResponseMatrixBuilder :
        IRaceMenuTriResponseMatrixBuilder
    {
        public ValueTask<RaceMenuTriResponseMatrixBuildResult>
            BuildAsync(
                RaceMenuTriResponseMatrixBuildRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new RaceMenuTriResponseMatrixBuildResult(
                    [], Hash('3'), [])
                {
                    RenderInputSha256 =
                        request.RenderInput.InputSha256,
                    ReviewedDesignSha256 =
                        request.ReviewedDesignSha256
                });
        }
    }

    private class ControlledSolver :
        IReferenceRaceMenuPresetSolver
    {
        public virtual ValueTask<
            ReferenceRaceMenuPresetSolverResult> SolveAsync(
                ReferenceRaceMenuPresetSolverRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ReferenceRaceMenuPresetSolverResult(
                    ImmutableDictionary<string, double>
                        .Empty.Add("NAM9[0]", 0.25),
                    ImmutableDictionary<string, double>
                        .Empty,
                    [],
                    [],
                    [
                        new ReferencePresetLoss(
                            "controlled-loss",
                            ReferencePresetLossKind
                                .SculptUnavailable,
                            "Controlled acknowledged-at-apply loss.",
                            false)
                    ],
                    ReferencePresetAuthoringRules
                        .SolverIterations,
                    0.25,
                    Hash('4'),
                    []));
        }
    }

    private sealed class CancellingSolver :
        ControlledSolver
    {
        public override ValueTask<
            ReferenceRaceMenuPresetSolverResult> SolveAsync(
                ReferenceRaceMenuPresetSolverRequest request,
                CancellationToken cancellationToken) =>
            throw new OperationCanceledException(
                "controlled cancellation",
                cancellationToken);
    }

    private sealed class ControlledComparisonService(
        bool injectUndeclaredFile = false) :
        IReferencePresetComparisonService
    {
        public async ValueTask<
            ReferencePresetComparisonResult> RenderAsync(
                ReferencePresetComparisonRequest request,
                CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(
                request.OutputRoot.Value);
            string[] relative =
            [
                "renders/front.png",
                "renders/left-not-observed.json",
                "renders/right-not-observed.json",
                "renders/comparison-sheet.png"
            ];
            var artifacts =
                ImmutableArray.CreateBuilder<
                    ReferencePresetComparisonArtifact>();
            foreach (string path in relative)
            {
                byte[] content =
                    System.Text.Encoding.UTF8.GetBytes(path);
                string destination = Path.Combine(
                    Path.GetDirectoryName(
                        request.OutputRoot.Value)!,
                    path.Replace('/',
                        Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(
                    destination,
                    content,
                    cancellationToken);
                artifacts.Add(
                    new ReferencePresetComparisonArtifact(
                        path.Contains("front",
                            StringComparison.Ordinal)
                            ? ReferenceImageViewRole.Front
                            : null,
                        new AssetPath(path),
                        Digest(content),
                        Hash('5'),
                        Hash('6'),
                        Hash('7'),
                        Hash('8'),
                        Hash('9'),
                        null));
            }
            if (injectUndeclaredFile)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(
                        request.OutputRoot.Value,
                        "undeclared.txt"),
                    "must be rejected",
                    cancellationToken);
            }
            return new ReferencePresetComparisonResult(
                artifacts.ToImmutable(),
                request.SolverResult.Residuals,
                request.SolverResult.Losses,
                []);
        }
    }

    private sealed class ControlledPresetWriter :
        IReferenceRaceMenuPresetWriter
    {
        public int WriteCount { get; private set; }
        public ReferenceRaceMenuPresetWriteRequest? LastRequest
        {
            get;
            private set;
        }

        public async ValueTask<
            ReferencePresetWriteArtifact> WriteAsync(
                ReferenceRaceMenuPresetWriteRequest request,
                CancellationToken cancellationToken)
        {
            LastRequest = request;
            WriteCount++;
            byte[] content =
                System.Text.Encoding.UTF8.GetBytes(
                    "controlled-reference-preset");
            await File.WriteAllBytesAsync(
                request.DestinationPath.Value,
                content,
                cancellationToken);
            return new ReferencePresetWriteArtifact(
                request.DestinationPath,
                Digest(content),
                request.Baseline,
                Hash('a'),
                []);
        }
    }

    private sealed class FailingPresetWriter :
        IReferenceRaceMenuPresetWriter
    {
        public async ValueTask<
            ReferencePresetWriteArtifact> WriteAsync(
                ReferenceRaceMenuPresetWriteRequest request,
                CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(
                request.DestinationPath.Value,
                "partial",
                cancellationToken);
            return new ReferencePresetWriteArtifact(
                request.DestinationPath,
                Hash('b'),
                request.Baseline,
                Hash('c'),
                [
                    new Diagnostic(
                        "controlled-preset-write-failure",
                        DiagnosticSeverity.Error,
                        "Controlled failure after the preset write boundary.")
                ]);
        }
    }

    private sealed class ControlledNpcBuildService :
        IRaceMenuJslotNpcBuildService
    {
        public RaceMenuJslotNpcBuildRequest? LastRequest
        {
            get;
            private set;
        }

        public ValueTask<RaceMenuJslotNpcBuildResult>
            ExecuteAsync(
                RaceMenuJslotNpcBuildRequest request,
                IProgress<BlankNpcBuildProgress>? progress,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            Directory.CreateDirectory(
                request.CompanionRoot.Value);
            return ValueTask.FromResult(
                new RaceMenuJslotNpcBuildResult(
                    true,
                    null,
                    null,
                    null,
                    null,
                    null,
                    []));
        }
    }

    private static Sha256Hash Digest(byte[] content) =>
        new(Convert.ToHexString(
            SHA256.HashData(content)));

    private static Sha256Hash Hash(char value) =>
        ReferenceMeshAnchorBindingTests.Hash(value);

    private static string Codes(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(
            item => item.Code));

    private static void Require(
        bool condition,
        string message) =>
        ReferenceMeshAnchorBindingTests.Require(
            condition, message);

    private static void DeleteOwnedRoot(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
            return;
        foreach (string staging in
                 Directory.EnumerateDirectories(
                     parent,
                     Path.GetFileName(path) + ".tmp-*",
                     SearchOption.TopDirectoryOnly))
            Directory.Delete(staging, recursive: true);
    }
}
