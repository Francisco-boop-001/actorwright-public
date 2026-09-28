using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// The sole orchestration boundary for reference-image proposal, deterministic
/// preset authoring, and the existing JSlot-to-NPC workflow.
/// </summary>
public sealed class ReferencePresetAuthoringTransaction(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Sha256Hash runtimeManifestSha256,
    IReferencePresetSessionService sessions,
    IReferenceImageDecoder imageDecoder,
    IReferenceFaceInferenceService inference,
    IReferenceDescriptionInterpreter descriptions,
    IReferenceSemanticLandmarkProjector projector,
    IReferencePresetRenderInputBuilder renderInputBuilder,
    IRaceMenuTriResponseMatrixBuilder responseMatrixBuilder,
    IReferenceRaceMenuPresetSolver solver,
    IReferencePresetComparisonService comparisons,
    IReferenceRaceMenuPresetWriter presetWriter,
    IRaceMenuJslotNpcBuildService jslotNpcBuildService)
    : IReferencePresetAuthoringTransaction
{
    public async ValueTask<ReferencePresetDesignProposalResult>
        ProposeDesignAsync(
            ReferencePresetDesignProposalRequest request,
            IProgress<ReferencePresetProgress>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string? staging = BeginTransaction(
            request.OutputRoot, diagnostics);
        if (staging is null)
            return new ReferencePresetDesignProposalResult(
                false, null, null, null, null,
                diagnostics.ToImmutable());
        try
        {
            Report(progress,
                ReferencePresetProgressStage.Validate,
                0, 1, "Validating authoring intake.");
            ReferencePresetIntake intake = request.Intake;
            diagnostics.AddRange(
                ReferencePresetAuthoringRules.ValidateIntake(
                    intake));
            if (HasErrors(diagnostics))
                return RefuseDesign(staging, diagnostics);
            WorkspacePath stagingRoot = new(staging);
            ReferencePresetSessionWriteResult intakeWrite =
                await WriteSessionAsync(
                    stagingRoot,
                    "authoring-intake.json",
                    new ReferencePresetSessionDocument(
                        ReferencePresetSessionDocumentKind.Intake,
                        Intake: intake),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(intakeWrite.Diagnostics);
            if (!intakeWrite.Written ||
                intakeWrite.ContentSha256 is null ||
                intakeWrite.ContentSha256.Value !=
                    request.ExpectedIntakeSha256)
            {
                diagnostics.Add(Error(
                    "reference-transaction-intake-hash",
                    "The supplied intake does not match its expected canonical session hash."));
                return RefuseDesign(staging, diagnostics);
            }

            var imageResults =
                ImmutableArray.CreateBuilder<ReferenceImageInference>(
                    intake.Images.Length);
            long remaining =
                ReferencePresetAuthoringRules.MaximumDecodedBytes;
            int completed = 0;
            foreach (ReferenceImageAuthority image in intake.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress,
                    ReferencePresetProgressStage.DecodeImages,
                    completed, intake.Images.Length,
                    $"Decoding {image.ImageId}.");
                ReferenceImageDecodeResult decoded =
                    await imageDecoder.DecodeAsync(
                        new ReferenceImageDecodeRequest(
                            image, remaining),
                        cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(decoded.Diagnostics);
                if (!decoded.Accepted ||
                    decoded.Image is null)
                    return RefuseDesign(staging, diagnostics);
                remaining -= decoded.Image.CanonicalRgba.Length;

                Report(progress,
                    ReferencePresetProgressStage.InferLandmarks,
                    completed, intake.Images.Length,
                    $"Analyzing {image.ImageId}.");
                ReferenceFaceInferenceResult inferred =
                    await inference.InferAsync(
                        new ReferenceFaceInferenceRequest(
                            decoded.Image,
                            runtimeManifestSha256),
                        cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(inferred.Diagnostics);
                if (!inferred.Accepted ||
                    inferred.Inference is null)
                    return RefuseDesign(staging, diagnostics);
                Report(progress,
                    ReferencePresetProgressStage.ProjectAnchors,
                    completed, intake.Images.Length,
                    $"Projecting semantic anchors for {image.ImageId}.");
                ReferenceSemanticLandmarkProjectionResult projected =
                    await projector.ProjectAsync(
                        new ReferenceSemanticLandmarkProjectionRequest(
                            inferred.Inference),
                        cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(projected.Diagnostics);
                if (HasErrors(projected.Diagnostics) ||
                    projected.Anchors.IsDefaultOrEmpty)
                    return RefuseDesign(staging, diagnostics);
                imageResults.Add(inferred.Inference);
                completed++;
            }

            Report(progress,
                ReferencePresetProgressStage.InterpretDescription,
                0, 1, "Interpreting description.");
            ReferenceDescriptionInterpretResult interpreted =
                await descriptions.InterpretAsync(
                    new ReferenceDescriptionInterpretRequest(
                        intake.Description,
                        request.ExpectedIntakeSha256),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(interpreted.Diagnostics);
            if (HasErrors(diagnostics))
                return RefuseDesign(staging, diagnostics);
            var proposal = new LandmarkInferenceProposal(
                1,
                ReferencePresetAuthorityKind.InferenceProposal,
                request.ExpectedIntakeSha256,
                runtimeManifestSha256,
                imageResults.ToImmutable(),
                interpreted.Traits,
                interpreted.Unknowns);
            diagnostics.AddRange(
                ReferencePresetAuthoringRules
                    .ValidateInferenceProposal(
                        proposal,
                        request.ExpectedIntakeSha256,
                        runtimeManifestSha256));
            if (HasErrors(diagnostics))
                return RefuseDesign(staging, diagnostics);

            Report(progress,
                ReferencePresetProgressStage.WriteProposal,
                0, 1, "Writing canonical inference proposal.");
            ReferencePresetSessionWriteResult proposalWrite =
                await WriteSessionAsync(
                    stagingRoot,
                    "landmark-proposal.json",
                    new ReferencePresetSessionDocument(
                        ReferencePresetSessionDocumentKind
                            .InferenceProposal,
                        InferenceProposal: proposal),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(proposalWrite.Diagnostics);
            if (!intakeWrite.Written ||
                !proposalWrite.Written ||
                proposalWrite.ContentSha256 is null ||
                HasErrors(diagnostics))
                return RefuseDesign(staging, diagnostics);

            VerifyDeclaredFiles(
                staging,
                [
                    "authoring-intake.json",
                    "landmark-proposal.json"
                ],
                diagnostics);
            if (HasErrors(diagnostics))
                return RefuseDesign(staging, diagnostics);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, request.OutputRoot.Value);
            Report(progress,
                ReferencePresetProgressStage.Completed,
                1, 1, "Design proposal completed.");
            return new ReferencePresetDesignProposalResult(
                true,
                proposal,
                proposalWrite.ContentSha256,
                null,
                null,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            DeleteTransactionRoot(staging);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            OverflowException)
        {
            DeleteTransactionRoot(staging);
            diagnostics.Add(Error(
                "reference-transaction-propose-failed",
                exception.Message));
            return new ReferencePresetDesignProposalResult(
                false, null, null, null, null,
                diagnostics.ToImmutable());
        }
    }

    public async ValueTask<ReferencePresetWriteResult>
        WritePresetAsync(
            ReferencePresetWriteRequest request,
            IProgress<ReferencePresetProgress>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await WritePresetCoreAsync(
            request, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ReferencePresetNpcBuildResult>
        WritePresetAndBuildNpcAsync(
            ReferencePresetNpcBuildRequest request,
            IProgress<ReferencePresetProgress>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReferencePresetWriteRequest authoring =
            request.Authoring with { Apply = true };
        ReferencePresetWriteResult written =
            await WritePresetCoreAsync(
                authoring, progress, cancellationToken)
                .ConfigureAwait(false);
        if (!written.Completed ||
            written.VerifiedPreset is null)
        {
            return new ReferencePresetNpcBuildResult(
                false, null, null, written.Diagnostics);
        }

        VerifiedReferencePreset verified =
            written.VerifiedPreset;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress,
                ReferencePresetProgressStage.BuildNpc,
                0, 1, "Building NPC through the existing JSlot route.");
            WorkspacePath npcRoot = new(Path.Combine(
                authoring.OutputRoot.Value, "npc"));
            var downstream = request.JslotBuildRequest with
            {
                Preset = verified.PresetPath,
                ExpectedPresetSha256 =
                    verified.PresetSha256,
                CompanionRoot = npcRoot
            };
            RaceMenuJslotNpcBuildResult build =
                await jslotNpcBuildService.ExecuteAsync(
                    downstream, null, cancellationToken)
                    .ConfigureAwait(false);
            ImmutableArray<Diagnostic> diagnostics =
                written.Diagnostics.AddRange(build.Diagnostics);
            if (!build.Completed ||
                build.Diagnostics.Any(item =>
                    item.Severity ==
                    DiagnosticSeverity.Error))
            {
                DeleteTransactionRoot(
                    authoring.OutputRoot.Value);
                return new ReferencePresetNpcBuildResult(
                    false, null, build, diagnostics);
            }
            Sha256Hash verifiedDocumentHash =
                HashVerifiedPreset(verified);
            var handoff = new VerifiedReferenceNpcHandoff(
                1,
                ReferencePresetAuthorityKind.VerifiedNpcHandoff,
                verified.ProjectId,
                verifiedDocumentHash,
                verified.PresetPath,
                verified.PresetSha256,
                verified.Race,
                verified.Sex,
                verified.Weight,
                diagnostics);
            Report(progress,
                ReferencePresetProgressStage.Completed,
                1, 1, "NPC handoff completed.");
            return new ReferencePresetNpcBuildResult(
                true, handoff, build, diagnostics);
        }
        catch (OperationCanceledException)
        {
            DeleteTransactionRoot(
                authoring.OutputRoot.Value);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            DeleteTransactionRoot(
                authoring.OutputRoot.Value);
            return new ReferencePresetNpcBuildResult(
                false,
                null,
                null,
                written.Diagnostics.Add(Error(
                    "reference-transaction-npc-failed",
                    exception.Message)));
        }
    }

    private async ValueTask<ReferencePresetWriteResult>
        WritePresetCoreAsync(
            ReferencePresetWriteRequest request,
            IProgress<ReferencePresetProgress>? progress,
            CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string? staging = BeginTransaction(
            request.OutputRoot, diagnostics);
        if (staging is null)
            return RefusedWrite(diagnostics);
        try
        {
            Report(progress,
                ReferencePresetProgressStage.Validate,
                0, 4, "Reopening authoring authorities.");
            ReferencePresetSessionReadResult intakeRead =
                await ReadSessionAsync(
                    request.IntakePath,
                    request.IntakeSha256,
                    ReferencePresetSessionDocumentKind.Intake,
                    cancellationToken).ConfigureAwait(false);
            ReferencePresetSessionReadResult inferenceRead =
                await ReadSessionAsync(
                    request.InferenceProposalPath,
                    request.InferenceProposalSha256,
                    ReferencePresetSessionDocumentKind
                        .InferenceProposal,
                    cancellationToken).ConfigureAwait(false);
            ReferencePresetSessionReadResult reviewRead =
                await ReadSessionAsync(
                    request.ReviewedDesignPath,
                    request.ReviewedDesignSha256,
                    ReferencePresetSessionDocumentKind
                        .ReviewedDesign,
                    cancellationToken).ConfigureAwait(false);
            ReferencePresetSessionReadResult resourceRead =
                await ReadSessionAsync(
                    request.ResourceSnapshotPath,
                    request.ResourceSnapshotSha256,
                    ReferencePresetSessionDocumentKind
                        .ResourceSnapshot,
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(intakeRead.Diagnostics);
            diagnostics.AddRange(inferenceRead.Diagnostics);
            diagnostics.AddRange(reviewRead.Diagnostics);
            diagnostics.AddRange(resourceRead.Diagnostics);
            ReferencePresetIntake? intake =
                intakeRead.Document?.Intake;
            LandmarkInferenceProposal? inferenceProposal =
                inferenceRead.Document?.InferenceProposal;
            ReviewedReferencePresetDesign? review =
                reviewRead.Document?.ReviewedDesign;
            ReferencePresetResourceSnapshot? snapshot =
                resourceRead.Document?.ResourceSnapshot;
            if (intake is null ||
                inferenceProposal is null ||
                review is null ||
                snapshot is null)
                return RefuseWrite(staging, diagnostics);

            request = request with
            {
                IntakeSha256 = intakeRead.ContentSha256!.Value,
                InferenceProposalSha256 = inferenceRead.ContentSha256!.Value,
                ReviewedDesignSha256 = reviewRead.ContentSha256!.Value,
                ResourceSnapshotSha256 = resourceRead.ContentSha256!.Value
            };

            diagnostics.AddRange(
                ReferencePresetAuthoringRules.ValidateIntake(
                    intake));
            diagnostics.AddRange(
                ReferencePresetAuthoringRules
                    .ValidateInferenceProposal(
                        inferenceProposal,
                        request.IntakeSha256,
                        runtimeManifestSha256));
            diagnostics.AddRange(
                ReferencePresetAuthoringRules
                    .ValidateReviewedDesign(
                        review,
                        request.InferenceProposalSha256));
            if (snapshot.ReviewedDesignSha256 !=
                    request.ReviewedDesignSha256 ||
                snapshot.ProjectId != intake.ProjectId ||
                snapshot.BaselineJslotSha256 !=
                    intake.BaselineJslotSha256)
            {
                diagnostics.Add(Error(
                    "reference-transaction-resource-binding",
                    "The resource snapshot is stale against the reviewed design or intake baseline."));
            }
            if (HasErrors(diagnostics))
                return RefuseWrite(staging, diagnostics);

            Report(progress,
                ReferencePresetProgressStage.ResolveResources,
                1, 4, "Building deterministic render input.");
            ReferencePresetRenderInputResult render =
                await renderInputBuilder.BuildAsync(
                    new ReferencePresetRenderInputRequest(
                        snapshot, review),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(render.Diagnostics);
            if (render.Input is null ||
                HasErrors(diagnostics))
                return RefuseWrite(staging, diagnostics);

            Report(progress,
                ReferencePresetProgressStage.BuildResponseMatrix,
                2, 5, "Building the reviewed morph response matrix.");
            RaceMenuTriResponseMatrixBuildResult matrix =
                await responseMatrixBuilder.BuildAsync(
                    new RaceMenuTriResponseMatrixBuildRequest(
                        review, snapshot, render.Input)
                    {
                        MorphBases = snapshot.MorphBases,
                        ReviewedDesignSha256 =
                            request.ReviewedDesignSha256
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(matrix.Diagnostics);
            if (!matrix.Accepted)
                return RefuseWrite(staging, diagnostics);
            Report(progress,
                ReferencePresetProgressStage.Solve,
                3, 5, "Solving reviewed face controls.");
            ReferenceRaceMenuPresetSolverResult solved =
                await solver.SolveAsync(
                    new ReferenceRaceMenuPresetSolverRequest(
                        review, snapshot, matrix)
                    {
                        RenderInput = render.Input,
                        ReviewedDesignSha256 =
                            request.ReviewedDesignSha256,
                        ProtectedNeckRingVertexIndices =
                            snapshot
                                .ProtectedNeckRingVertexIndices
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(solved.Diagnostics);
            if (!solved.Accepted)
                return RefuseWrite(staging, diagnostics);

            WorkspacePath stagingRoot = new(staging);
            Directory.CreateDirectory(Path.Combine(
                staging, "evidence"));
            Report(progress,
                ReferencePresetProgressStage.Render,
                4, 5, "Rendering comparison evidence.");
            ReferencePresetComparisonResult comparison =
                await comparisons.RenderAsync(
                    new ReferencePresetComparisonRequest(
                        render.Input,
                        review,
                        solved,
                        new WorkspacePath(Path.Combine(
                            staging, "renders")))
                    {
                        Snapshot = snapshot,
                        Intake = intake
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(comparison.Diagnostics);
            if (HasErrors(diagnostics))
                return RefuseWrite(staging, diagnostics);
            var proposal = new ReferencePresetAuthoringProposal(
                1,
                ReferencePresetAuthorityKind.AuthoringProposal,
                intake.ProjectId,
                intake.Race,
                intake.Sex,
                intake.Weight,
                request.ReviewedDesignSha256,
                request.ResourceSnapshotSha256,
                solved,
                comparison,
                solved.Losses,
                diagnostics.Where(item =>
                        item.Severity !=
                        DiagnosticSeverity.Error)
                    .ToImmutableArray());

            Report(progress,
                ReferencePresetProgressStage.WriteProposal,
                0, 1, "Writing canonical authoring proposal.");
            await WriteCoreDocumentsAsync(
                stagingRoot,
                intake,
                inferenceProposal,
                review,
                snapshot,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            ReferencePresetSessionWriteResult proposalWrite =
                await WriteSessionAsync(
                    stagingRoot,
                    "authoring-proposal.json",
                    new ReferencePresetSessionDocument(
                        ReferencePresetSessionDocumentKind
                            .AuthoringProposal,
                        AuthoringProposal: proposal),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(proposalWrite.Diagnostics);
            if (!proposalWrite.Written ||
                proposalWrite.ContentSha256 is null ||
                HasErrors(diagnostics))
                return RefuseWrite(staging, diagnostics);
            Sha256Hash proposalHash =
                proposalWrite.ContentSha256.Value;

            await WriteProposalEvidenceAsync(
                staging,
                runtimeManifestSha256,
                intake,
                request,
                inferenceProposal,
                matrix,
                solved,
                cancellationToken).ConfigureAwait(false);

            VerifiedReferencePreset? verified = null;
            if (request.Apply)
            {
                if (request.AcceptedAuthoringProposalSha256 is null ||
                    request.AcceptedAuthoringProposalSha256.Value !=
                    proposalHash)
                {
                    diagnostics.Add(Error(
                        "reference-transaction-accepted-proposal",
                        "Apply requires the exact hash of the deterministic proposal-only document."));
                    return RefuseWrite(staging, diagnostics);
                }
                Directory.CreateDirectory(Path.Combine(
                    staging, "preset"));
                string fileName =
                    SafeFileName(intake.TargetName) + ".jslot";
                WorkspacePath stagedPreset = new(Path.Combine(
                    staging, "preset", fileName));
                ReferencePresetAuthoringProposal accepted =
                    proposal with
                    {
                        Losses = proposal.Losses
                            .Select(item => item with
                            {
                                Acknowledged = true
                            })
                            .ToImmutableArray()
                    };
                Report(progress,
                    ReferencePresetProgressStage.WritePreset,
                    0, 1, "Writing accepted RaceMenu preset.");
                ReferencePresetWriteArtifact artifact =
                    await presetWriter.WriteAsync(
                        new ReferenceRaceMenuPresetWriteRequest(
                            snapshot.Baseline,
                            accepted,
                            proposalHash,
                            stagedPreset)
                        {
                            Snapshot = snapshot,
                            ResourceSnapshotDocumentSha256 =
                                request.ResourceSnapshotSha256
                        },
                        cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(artifact.Diagnostics);
                if (HasErrors(artifact.Diagnostics))
                    return RefuseWrite(staging, diagnostics);
                Report(progress,
                    ReferencePresetProgressStage.VerifyPreset,
                    0, 1, "Recording verified RaceMenu preset readback.");
                cancellationToken.ThrowIfCancellationRequested();
                await WriteEvidenceAsync(
                    Path.Combine(staging, "evidence",
                        "preset-readback.json"),
                    "preset-readback",
                    [
                        ("presetSha256",
                            artifact.PresetSha256.Value),
                        ("readbackEvidenceSha256",
                            artifact.ReadbackEvidenceSha256.Value)
                    ],
                    cancellationToken).ConfigureAwait(false);
                WorkspacePath finalPreset = new(Path.Combine(
                    request.OutputRoot.Value,
                    "preset", fileName));
                verified = new VerifiedReferencePreset(
                    1,
                    ReferencePresetAuthorityKind.VerifiedPreset,
                    intake.ProjectId,
                    intake.Race,
                    intake.Sex,
                    intake.Weight,
                    proposalHash,
                    finalPreset,
                    artifact.PresetSha256,
                    artifact.ReadbackEvidenceSha256,
                    diagnostics.ToImmutable());
            }

            HashSet<string> declared =
                BuildDeclaredFiles(
                    comparison, request.Apply,
                    verified?.PresetPath.Value is null
                        ? null
                        : Path.GetFileName(
                            verified.PresetPath.Value));
            VerifyDeclaredFiles(
                staging, declared, diagnostics);
            if (HasErrors(diagnostics))
                return RefuseWrite(staging, diagnostics);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, request.OutputRoot.Value);
            Report(progress,
                ReferencePresetProgressStage.Completed,
                5, 5,
                request.Apply
                    ? "Verified preset completed."
                    : "Authoring proposal completed.");
            return new ReferencePresetWriteResult(
                true,
                proposal,
                proposalHash,
                verified,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            DeleteTransactionRoot(staging);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            OverflowException)
        {
            DeleteTransactionRoot(staging);
            diagnostics.Add(Error(
                "reference-transaction-write-failed",
                exception.Message));
            return RefusedWrite(diagnostics);
        }
    }

    private string? BeginTransaction(
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.Evaluate(
            labRoot, outputRoot));
        if (File.Exists(outputRoot.Value) ||
            Directory.Exists(outputRoot.Value))
        {
            diagnostics.Add(Error(
                "reference-transaction-output-exists",
                "Transaction output must be an absent path."));
        }
        string? parent = Path.GetDirectoryName(
            outputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error(
                "reference-transaction-parent",
                "Transaction output parent must already exist."));
        }
        if (HasErrors(diagnostics))
            return null;
        string staging = outputRoot.Value + ".tmp-" +
                         Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private async ValueTask<
        ReferencePresetSessionReadResult> ReadSessionAsync(
            WorkspacePath path,
            Sha256Hash hash,
            ReferencePresetSessionDocumentKind kind,
            CancellationToken cancellationToken) =>
        await sessions.ReadAsync(
            new ReferencePresetSessionReadRequest(
                path, hash, kind),
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<
        ReferencePresetSessionWriteResult> WriteSessionAsync(
            WorkspacePath root,
            string relative,
            ReferencePresetSessionDocument document,
            CancellationToken cancellationToken) =>
        await sessions.WriteAsync(
            new ReferencePresetSessionWriteRequest(
                new WorkspacePath(Path.Combine(
                    root.Value, relative)),
                document),
            cancellationToken).ConfigureAwait(false);

    private async Task WriteCoreDocumentsAsync(
        WorkspacePath root,
        ReferencePresetIntake intake,
        LandmarkInferenceProposal inferenceProposal,
        ReviewedReferencePresetDesign review,
        ReferencePresetResourceSnapshot snapshot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        ReferencePresetSessionWriteResult[] writes =
        [
            await WriteSessionAsync(
                root, "authoring-intake.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind.Intake,
                    Intake: intake),
                cancellationToken).ConfigureAwait(false),
            await WriteSessionAsync(
                root, "landmark-proposal.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind
                        .InferenceProposal,
                    InferenceProposal: inferenceProposal),
                cancellationToken).ConfigureAwait(false),
            await WriteSessionAsync(
                root, "reviewed-design.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind
                        .ReviewedDesign,
                    ReviewedDesign: review),
                cancellationToken).ConfigureAwait(false),
            await WriteSessionAsync(
                root, "resource-snapshot.json",
                new ReferencePresetSessionDocument(
                    ReferencePresetSessionDocumentKind
                        .ResourceSnapshot,
                    ResourceSnapshot: snapshot),
                cancellationToken).ConfigureAwait(false)
        ];
        foreach (ReferencePresetSessionWriteResult write in writes)
            diagnostics.AddRange(write.Diagnostics);
        if (writes.Any(item => !item.Written))
            diagnostics.Add(Error(
                "reference-transaction-core-write",
                "One or more canonical authority copies were refused."));
    }

    private static async Task WriteProposalEvidenceAsync(
        string staging,
        Sha256Hash runtimeManifest,
        ReferencePresetIntake intake,
        ReferencePresetWriteRequest request,
        LandmarkInferenceProposal inferenceProposal,
        RaceMenuTriResponseMatrixBuildResult matrix,
        ReferenceRaceMenuPresetSolverResult solved,
        CancellationToken cancellationToken)
    {
        string evidence = Path.Combine(staging, "evidence");
        await WriteEvidenceAsync(
            Path.Combine(evidence, "model-authority.json"),
            "model-authority",
            [("runtimeManifestSha256",
                runtimeManifest.Value)],
            cancellationToken).ConfigureAwait(false);
        await WriteEvidenceAsync(
            Path.Combine(evidence, "source-images.json"),
            "source-images",
            [("imageSetSha256",
                HashStrings(intake.Images
                    .OrderBy(item => item.ImageId,
                        StringComparer.Ordinal)
                    .Select(item =>
                        item.SourceSha256.Value)).Value)],
            cancellationToken).ConfigureAwait(false);
        await WriteEvidenceAsync(
            Path.Combine(evidence,
                "description-interpretation.json"),
            "description-interpretation",
            [("inferenceProposalSha256",
                request.InferenceProposalSha256.Value)],
            cancellationToken).ConfigureAwait(false);
        await WriteEvidenceAsync(
            Path.Combine(evidence,
                "landmark-comparison.json"),
            "landmark-comparison",
            [("reviewedDesignSha256",
                request.ReviewedDesignSha256.Value)],
            cancellationToken).ConfigureAwait(false);
        await WriteEvidenceAsync(
            Path.Combine(evidence,
                "morph-response-matrix.json"),
            "morph-response-matrix",
            [("matrixSha256",
                matrix.MatrixSha256!.Value.Value)],
            cancellationToken).ConfigureAwait(false);
        await WriteEvidenceAsync(
            Path.Combine(evidence,
                "residuals-and-losses.json"),
            "residuals-and-losses",
            [
                ("solverResultSha256",
                    solved.ResultSha256!.Value.Value),
                ("residualCount",
                    solved.Residuals.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                ("lossCount",
                    solved.Losses.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
            ],
            cancellationToken).ConfigureAwait(false);
        _ = inferenceProposal;
    }

    private static async Task WriteEvidenceAsync(
        string path,
        string kind,
        IEnumerable<(string Name, string Value)> values,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   memory,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteNumber("schemaVersion", 1);
            foreach ((string name, string value) in
                     values.OrderBy(item => item.Name,
                         StringComparer.Ordinal))
                writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        byte[] bytes = memory.ToArray();
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous |
            FileOptions.WriteThrough);
        await stream.WriteAsync(
            bytes, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static HashSet<string> BuildDeclaredFiles(
        ReferencePresetComparisonResult comparison,
        bool apply,
        string? presetFileName)
    {
        HashSet<string> result =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "authoring-intake.json",
                "landmark-proposal.json",
                "reviewed-design.json",
                "resource-snapshot.json",
                "authoring-proposal.json",
                "evidence/model-authority.json",
                "evidence/source-images.json",
                "evidence/description-interpretation.json",
                "evidence/landmark-comparison.json",
                "evidence/morph-response-matrix.json",
                "evidence/residuals-and-losses.json"
            };
        foreach (ReferencePresetComparisonArtifact artifact in
                 comparison.Artifacts)
            result.Add(artifact.Path.Value);
        if (apply)
        {
            result.Add("evidence/preset-readback.json");
            result.Add("preset/" + presetFileName);
        }
        return result;
    }

    private static void VerifyDeclaredFiles(
        string root,
        IEnumerable<string> declared,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        HashSet<string> expected = declared
            .Select(NormalizeRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = Directory
            .EnumerateFiles(
                root, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelative(
                Path.GetRelativePath(root, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string missing in expected.Except(
                     actual, StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "reference-transaction-file-missing",
                $"Declared transaction file '{missing}' is absent."));
        foreach (string extra in actual.Except(
                     expected, StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "reference-transaction-file-undeclared",
                $"Undeclared transaction file '{extra}' was produced."));
    }

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static ReferencePresetDesignProposalResult RefuseDesign(
        string staging,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        DeleteTransactionRoot(staging);
        return new ReferencePresetDesignProposalResult(
            false, null, null, null, null,
            diagnostics.ToImmutable());
    }

    private static ReferencePresetWriteResult RefuseWrite(
        string staging,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        DeleteTransactionRoot(staging);
        return RefusedWrite(diagnostics);
    }

    private static ReferencePresetWriteResult RefusedWrite(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, null,
            diagnostics.ToImmutable());

    private static void DeleteTransactionRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            // A transaction-owned staging/final root remains non-authoritative
            // after a reported refusal; no pre-existing root is ever removed.
        }
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string normalized = new(value
            .Select(character =>
                invalid.Contains(character) ||
                char.IsControl(character)
                    ? '-'
                    : character)
            .ToArray());
        normalized = normalized.Trim().Trim('.');
        if (normalized.Length == 0)
            normalized = "controlled-reference";
        return normalized.Length <= 80
            ? normalized
            : normalized[..80];
    }

    private static Sha256Hash HashVerifiedPreset(
        VerifiedReferencePreset preset) =>
        HashStrings(
        [
            preset.SchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            preset.Authority.ToWireName(),
            preset.ProjectId,
            preset.Race.Plugin.Value,
            preset.Race.FormId.Value.ToString(
                "X8",
                System.Globalization.CultureInfo.InvariantCulture),
            preset.Sex.ToString(),
            preset.Weight.ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture),
            preset.AuthoringProposalSha256.Value,
            preset.PresetPath.Value,
            preset.PresetSha256.Value,
            preset.ReadbackEvidenceSha256.Value
        ]);

    private static Sha256Hash HashStrings(
        IEnumerable<string> values)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private static void Report(
        IProgress<ReferencePresetProgress>? progress,
        ReferencePresetProgressStage stage,
        int completed,
        int total,
        string message) =>
        progress?.Report(new ReferencePresetProgress(
            stage, completed, total, message));

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
