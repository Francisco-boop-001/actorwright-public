using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class AgentReviewReceiptTests
{
    private const string HashA =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string ExactAnalyzeAuthorityNoticeSha256 =
        "360DD7F3B9D65A3B464B8CEDBEE0253F9681818E43B025E4EA3099B481F0E62F";
    private const string ExactApplyAuthorityNoticeSha256 =
        "CF3802F200721FB7DD5259824379AD81EA241F32DCD0EA81AFD3CA75C33D55C0";

    public static void Run()
    {
        string testOwner = Path.Combine(FindRepositoryRoot(), "artifacts", "tests");
        string root = Path.Combine(
            testOwner,
            $"agent-review-receipt-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ProvesExactReceiptCreateLoadAndRefusalTable(root);
        }
        finally
        {
            DeleteOwnedTestTree(root, testOwner);
        }
    }

    private static void ProvesExactReceiptCreateLoadAndRefusalTable(string root)
    {
        var labRoot = new WorkspacePath(root);
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var bundleCodec = new AgentWorkflowBundleCodec(policy, labRoot);
        var service = new AgentReviewReceiptService(policy, labRoot);
        Assert(
            AgentReviewContract.FinishAnalyzeScope == "npc-finish-analyze" &&
            AgentReviewContract.FinishAnalyzeAuthorityNotice ==
                "This operator attestation binds the exact current package proposal and displayed preview artifacts, permits only Finish Analyze, and grants neither human-visual authority, game-runtime authority, nor promotion authority." &&
            AgentReviewContract.FinishAnalyzeAuthorityNoticeSha256 ==
                ExactAnalyzeAuthorityNoticeSha256 &&
            AgentReviewContract.FinishApplyScope == "npc-finish-apply" &&
            AgentReviewContract.FinishApplyAuthorityNotice ==
                "This operator attestation binds the exact current Finish proposal and displayed preview artifacts, permits only Finish Apply, and grants neither human-visual authority, game-runtime authority, nor promotion authority." &&
            AgentReviewContract.FinishApplyAuthorityNoticeSha256 ==
                ExactApplyAuthorityNoticeSha256,
            "the exact dual review scopes, notices, or UTF-8 hashes drifted");

        WorkflowArtifactBinding request = WriteArtifact(
            root,
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            "finish-request.json",
            "finish request"u8.ToArray());
        WorkflowArtifactBinding proposal = WriteArtifact(
            root,
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            "finish-proposal.json",
            "finish proposal"u8.ToArray());
        WorkflowArtifactBinding package = WriteArtifact(
            root,
            WorkflowArtifactKinds.NpcPackageManifest,
            "package.json",
            "package manifest"u8.ToArray());
        WorkflowArtifactBinding preview = WriteArtifact(
            root,
            WorkflowArtifactKinds.NpcPreviewManifest,
            "preview.json",
            "preview manifest"u8.ToArray());
        AgentWorkflowBundle reviewedBundle = Bundle([request, proposal, preview]);
        WorkflowEvaluation finishReviewEvaluation = Evaluate(reviewedBundle);
        Assert(
            finishReviewEvaluation.Outcome == WorkflowEvaluationOutcome.Blocked &&
            finishReviewEvaluation.Phase == AgentWorkflowPhase.Review &&
            finishReviewEvaluation.NextActions is [var finishReviewAction] &&
            finishReviewAction.Command == "gui" &&
            finishReviewAction.RequiresHumanAction,
            "receiptless Finish proposal did not derive its review checkpoint");
        reviewedBundle = reviewedBundle with
        {
            Phase = finishReviewEvaluation.Phase,
            Authority = finishReviewEvaluation.Authority,
            NextActions = finishReviewEvaluation.NextActions
        };
        AgentWorkflowBundleDocument bundleDocument = bundleCodec.WriteNew(
            reviewedBundle,
            new WorkspacePath(Path.Combine(root, "reviewed-workflow.json")));
        ImmutableArray<WorkflowArtifactBinding> displayed = [proposal, preview];

        AgentWorkflowBundle analyzeBundle = Bundle([package, preview]);
        WorkflowEvaluation analyzePredecessorEvaluation = Evaluate(analyzeBundle);
        analyzeBundle = analyzeBundle with
        {
            Phase = analyzePredecessorEvaluation.Phase,
            Authority = analyzePredecessorEvaluation.Authority,
            NextActions = analyzePredecessorEvaluation.NextActions
        };
        AgentWorkflowBundleDocument analyzeBundleDocument = bundleCodec.WriteNew(
            analyzeBundle,
            NewPath(root, "analyze-reviewed-workflow.json"));
        ImmutableArray<WorkflowArtifactBinding> analyzeDisplayed = [package, preview];
        AgentReviewReceiptDocument analyzeAccepted = service.Create(
            analyzeBundleDocument,
            package.Sha256,
            analyzeDisplayed,
            ReviewOutcome.Accepted,
            "Exact package proposal and preview reviewed.",
            NewPath(root, "analyze-accepted-review.json"));
        AgentReviewReceiptDocument analyzeLoaded = service.Load(
            analyzeBundleDocument,
            package.Sha256,
            analyzeDisplayed,
            analyzeAccepted.Path,
            analyzeAccepted.Sha256);
        Assert(
            analyzeLoaded.Receipt.Scope ==
                AgentReviewContract.FinishAnalyzeScope &&
            analyzeLoaded.Receipt.AuthorityNoticeSha256 ==
                AgentReviewContract.FinishAnalyzeAuthorityNoticeSha256 &&
            analyzeLoaded.Receipt.ProposalSha256 == package.Sha256,
            "pre-analysis receipt did not bind the package proposal and Analyze-only notice");
        WorkflowEvaluation analyzeEvaluation = EvaluateWithReceipt(
            analyzeBundle,
            analyzeLoaded,
            analyzeBundleDocument.Sha256);
        Assert(
            analyzeEvaluation.Outcome == WorkflowEvaluationOutcome.Blocked &&
            analyzeEvaluation.Phase == AgentWorkflowPhase.Analyze &&
            analyzeEvaluation.NextActions is [var analyzeAction] &&
            analyzeAction.Command == "npc finish analyze" &&
            !analyzeAction.RequiresHumanAction &&
            analyzeAction.RequiredBindings.Any(binding =>
                binding.Option == "--review-receipt" &&
                binding.Value == analyzeAccepted.Path.Value &&
                binding.ArtifactSha256 == analyzeAccepted.Sha256) &&
            analyzeAction.MissingPrerequisites.SequenceEqual(
                ["--request", "--request-sha256", "--proposal",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"]) &&
            analyzeEvaluation.Diagnostics is [var analyzeDiagnostic] &&
            analyzeDiagnostic.Code ==
                "workflow-next-action-prerequisites-missing" &&
            !analyzeEvaluation.Diagnostics.Any(diagnostic =>
                diagnostic.Code.StartsWith(
                    "workflow-review-",
                    StringComparison.Ordinal)),
            "service-produced Analyze review did not resolve the review prerequisite exactly");

        var lifecycle = new AgentWorkflowBundleTransitionService(bundleCodec);
        var analyzePredecessor = new AgentWorkflowBundleTransition(
            analyzeBundleDocument,
            analyzePredecessorEvaluation);
        AgentWorkflowBundleTransition analyzeSuccessor =
            lifecycle.AdvanceReviewed(
                analyzePredecessor,
                analyzeBundle.Npc,
                analyzeBundle.RequestDigest,
                analyzeAccepted,
                [package, preview, analyzeAccepted.Artifact],
                NewPath(root, "analyze-successor-workflow.json"));
        Assert(
            analyzeSuccessor.Document.Bundle.Artifacts
                .Select(artifact => artifact.Kind)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(new[]
                {
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    WorkflowArtifactKinds.ReviewReceipt
                }.Order(StringComparer.Ordinal)) &&
            analyzeSuccessor.Evaluation.Outcome ==
                WorkflowEvaluationOutcome.Blocked &&
            analyzeSuccessor.Evaluation.NextActions is [var successorAction] &&
            successorAction.Command == "npc finish analyze" &&
            successorAction.MissingPrerequisites.SequenceEqual(
                ["--request", "--request-sha256", "--proposal",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"]) &&
            !analyzeSuccessor.Evaluation.Diagnostics.Any(diagnostic =>
                diagnostic.Code.StartsWith(
                    "workflow-review-",
                    StringComparison.Ordinal)),
            "accepted package review did not create the exact resumable Analyze successor");
        ProvesExactDisplayedAuthorityAtEvaluatorAndPublicTransitions(
            root,
            lifecycle,
            analyzePredecessor,
            analyzeAccepted,
            [package, preview],
            analyzeAccepted.Sha256,
            directExtraUsesReceiptArtifact: true,
            "package");
        AgentReviewReceiptDocument successorReceipt = service.LoadForSuccessor(
            analyzeSuccessor.Document,
            analyzeAccepted.Path,
            analyzeAccepted.Sha256);
        Assert(
            successorReceipt.Utf8Json.AsSpan().SequenceEqual(
                analyzeAccepted.Utf8Json.AsSpan()) &&
            successorReceipt.Artifact.Path == analyzeAccepted.Artifact.Path &&
            successorReceipt.Artifact.Size == analyzeAccepted.Artifact.Size &&
            successorReceipt.Artifact.Sha256 == analyzeAccepted.Artifact.Sha256 &&
            successorReceipt.Artifact.InputArtifactHashes.SequenceEqual(
                analyzeAccepted.Artifact.InputArtifactHashes),
            "successor receipt load changed exact accepted evidence");
        int receiptLoaderCalls = 0;
        AgentWorkflowBundleTransition loadedReviewed =
            lifecycle.LoadForReviewedCommand(
                analyzeSuccessor.Document.Path,
                analyzeSuccessor.Document.Sha256,
                "npc finish analyze",
                successor =>
                {
                    receiptLoaderCalls++;
                    return service.LoadForSuccessor(
                        successor,
                        analyzeAccepted.Path,
                        analyzeAccepted.Sha256);
                });
        Assert(
            receiptLoaderCalls == 1 &&
            loadedReviewed.Document.Sha256 ==
                analyzeSuccessor.Document.Sha256 &&
            loadedReviewed.Evaluation.NextActions is [var loadedAction] &&
            loadedAction.Command == "npc finish analyze",
            "reviewed command load did not reopen one exact receipt and action");
        AssertWorkflowRefused(
            () => lifecycle.LoadForReviewedCommand(
                analyzeSuccessor.Document.Path,
                analyzeSuccessor.Document.Sha256,
                "npc finish apply",
                successor => service.LoadForSuccessor(
                    successor,
                    analyzeAccepted.Path,
                    analyzeAccepted.Sha256)),
            "workflow-transition-command-mismatch",
            "reviewed successor admitted the wrong exact command");

        AgentWorkflowBundle foreignPredecessorBundle = analyzeBundle with
        {
            Npc = analyzeBundle.Npc with
            {
                DisplayName = "Foreign Receipt NPC"
            }
        };
        WorkflowEvaluation foreignPredecessorEvaluation =
            Evaluate(foreignPredecessorBundle);
        foreignPredecessorBundle = foreignPredecessorBundle with
        {
            Phase = foreignPredecessorEvaluation.Phase,
            Authority = foreignPredecessorEvaluation.Authority,
            NextActions = foreignPredecessorEvaluation.NextActions
        };
        AgentWorkflowBundleDocument foreignPredecessorDocument =
            bundleCodec.WriteNew(
                foreignPredecessorBundle,
                NewPath(root, "foreign-analyze-predecessor.json"));
        WorkspacePath wrongPredecessorOutput = NewPath(
            root,
            "wrong-predecessor-successor.json");
        AssertWorkflowRefused(
            () => lifecycle.AdvanceReviewed(
                new AgentWorkflowBundleTransition(
                    foreignPredecessorDocument,
                    foreignPredecessorEvaluation),
                foreignPredecessorBundle.Npc,
                foreignPredecessorBundle.RequestDigest,
                analyzeAccepted,
                [package, preview, analyzeAccepted.Artifact],
                wrongPredecessorOutput),
            "workflow-review-binding-stale",
            "receipt from another predecessor authorized a successor");
        Assert(
            !File.Exists(wrongPredecessorOutput.Value),
            "wrong predecessor receipt left a successor file");

        AgentReviewReceiptDocument closureDriftReceipt = analyzeAccepted with
        {
            Artifact = analyzeAccepted.Artifact with
            {
                InputArtifactHashes = analyzeAccepted.Artifact
                    .InputArtifactHashes.Add(HashA)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray()
            }
        };
        WorkspacePath closureDriftOutput = NewPath(
            root,
            "closure-drift-advance.json");
        AssertWorkflowRefused(
            () => lifecycle.AdvanceReviewed(
                analyzePredecessor,
                analyzeBundle.Npc,
                analyzeBundle.RequestDigest,
                closureDriftReceipt,
                [package, preview, closureDriftReceipt.Artifact],
                closureDriftOutput),
            "workflow-review-artifact-binding-stale",
            "AdvanceReviewed accepted a non-closed receipt input-hash superset");
        Assert(
            !File.Exists(closureDriftOutput.Value),
            "receipt closure drift left a successor file");

        ProvesReviewedCarryoverRefusalTable(
            root,
            lifecycle,
            analyzePredecessor,
            analyzeAccepted,
            [package, preview]);
        ProvesReviewedCallbackRefusalTable(
            root,
            bundleCodec,
            lifecycle,
            service,
            analyzeSuccessor,
            analyzeAccepted);

        foreach (ReviewOutcome outcome in new[]
                 {
                     ReviewOutcome.Rejected,
                     ReviewOutcome.RevisionRequested
                 })
        {
            AgentReviewReceiptDocument nonAuthorizing = service.Create(
                analyzeBundleDocument,
                package.Sha256,
                analyzeDisplayed,
                outcome,
                null,
                NewPath(
                    root,
                    $"analyze-{outcome.ToString().ToLowerInvariant()}.json"));
            AgentReviewReceiptDocument reopened = service.Load(
                analyzeBundleDocument,
                package.Sha256,
                analyzeDisplayed,
                nonAuthorizing.Path,
                nonAuthorizing.Sha256);
            Assert(
                reopened.Receipt.Outcome == outcome,
                $"{outcome} Analyze receipt was not retained as valid evidence");
            WorkspacePath refusedOutput = NewPath(
                root,
                $"analyze-{outcome.ToString().ToLowerInvariant()}-successor.json");
            AssertWorkflowRefused(
                () => lifecycle.AdvanceReviewed(
                    analyzePredecessor,
                    analyzeBundle.Npc,
                    analyzeBundle.RequestDigest,
                    reopened,
                    [package, preview, reopened.Artifact],
                    refusedOutput),
                "workflow-review-not-accepted",
                $"{outcome} receipt authorized a workflow successor");
            Assert(
                !File.Exists(refusedOutput.Value),
                $"{outcome} receipt left an authorizing successor file");
        }

        ProvesSuccessorReceiptRefusalTable(
            root,
            bundleCodec,
            service,
            analyzeSuccessor,
            analyzeAccepted,
            package,
            preview);
        AgentWorkflowBundleDocument broadenedSuccessor = bundleCodec.WriteNew(
            analyzeSuccessor.Document.Bundle with
            {
                Artifacts =
                    [package, preview, request, analyzeAccepted.Artifact]
            },
            NewPath(root, "broadened-analyze-successor.json"));
        AssertRefused(
            () => service.LoadForSuccessor(
                broadenedSuccessor,
                analyzeAccepted.Path,
                analyzeAccepted.Sha256),
            "review-successor-signature-invalid",
            "successor-only receipt load accepted an invented request artifact");

        AgentReviewReceiptDocument accepted = service.Create(
            bundleDocument,
            proposal.Sha256,
            displayed,
            ReviewOutcome.Accepted,
            "Exact proposal and preview reviewed.",
            new WorkspacePath(Path.Combine(root, "accepted-review.json")));
        Assert(
            accepted.Receipt.Schema == AgentWorkflowSchemas.ReviewReceiptV1 &&
            accepted.Receipt.BundleSha256 == bundleDocument.Sha256 &&
            accepted.Receipt.ProposalSha256 == proposal.Sha256 &&
            accepted.Receipt.DisplayedArtifactHashes.SequenceEqual(
                displayed.Select(item => item.Sha256).Order(StringComparer.Ordinal)) &&
            accepted.Receipt.AuthorityNoticeSha256 ==
                AgentReviewContract.FinishApplyAuthorityNoticeSha256 &&
            accepted.Receipt.Scope == AgentReviewContract.FinishApplyScope &&
            accepted.Receipt.AttestationKind ==
                AgentWorkflowContractValidation.OperatorAttestationKind,
            "accepted receipt did not bind the exact application-owned review contract");
        Assert(
            accepted.Utf8Json.AsSpan().SequenceEqual(
                File.ReadAllBytes(accepted.Path.Value)) &&
            accepted.Size == accepted.Utf8Json.Length &&
            accepted.Sha256 == Hash(accepted.Utf8Json.AsSpan()) &&
            accepted.Utf8Json.Length >= 3 &&
            !(accepted.Utf8Json[0] == 0xEF &&
              accepted.Utf8Json[1] == 0xBB &&
              accepted.Utf8Json[2] == 0xBF) &&
            !accepted.Utf8Json.Contains((byte)'\r'),
            "receipt persistence was not exact canonical BOM-free UTF-8 with LF");
        string[] expectedInputs =
        [
            bundleDocument.Sha256,
            proposal.Sha256,
            preview.Sha256,
            AgentReviewContract.FinishApplyAuthorityNoticeSha256
        ];
        Array.Sort(expectedInputs, StringComparer.Ordinal);
        Assert(
            accepted.Artifact.Kind == WorkflowArtifactKinds.ReviewReceipt &&
            accepted.Artifact.SchemaOrMediaType ==
                AgentWorkflowSchemas.ReviewReceiptV1 &&
            accepted.Artifact.Path == accepted.Path &&
            accepted.Artifact.Size == accepted.Size &&
            accepted.Artifact.Sha256 == accepted.Sha256 &&
            accepted.Artifact.ProducerCommand == "gui" &&
            accepted.Artifact.RequestDigest == reviewedBundle.RequestDigest &&
            accepted.Artifact.InputArtifactHashes.SequenceEqual(expectedInputs),
            "receipt artifact authority did not bind bundle, proposal, display, and notice inputs");

        AgentReviewReceiptDocument loaded = service.Load(
            bundleDocument,
            proposal.Sha256,
            displayed,
            accepted.Path,
            accepted.Sha256);
        Assert(
            loaded.Receipt.Schema == accepted.Receipt.Schema &&
            loaded.Receipt.BundleSha256 == accepted.Receipt.BundleSha256 &&
            loaded.Receipt.ProposalSha256 == accepted.Receipt.ProposalSha256 &&
            loaded.Receipt.DisplayedArtifactHashes.SequenceEqual(
                accepted.Receipt.DisplayedArtifactHashes) &&
            loaded.Receipt.AuthorityNoticeSha256 ==
                accepted.Receipt.AuthorityNoticeSha256 &&
            loaded.Receipt.Outcome == accepted.Receipt.Outcome &&
            loaded.Receipt.Scope == accepted.Receipt.Scope &&
            loaded.Receipt.ReviewerNote == accepted.Receipt.ReviewerNote &&
            loaded.Receipt.AttestationKind == accepted.Receipt.AttestationKind &&
            loaded.Utf8Json.AsSpan().SequenceEqual(accepted.Utf8Json.AsSpan()) &&
            loaded.Artifact.Kind == accepted.Artifact.Kind &&
            loaded.Artifact.Path == accepted.Artifact.Path &&
            loaded.Artifact.Size == accepted.Artifact.Size &&
            loaded.Artifact.Sha256 == accepted.Artifact.Sha256 &&
            loaded.Artifact.InputArtifactHashes.SequenceEqual(
                accepted.Artifact.InputArtifactHashes),
            "strict receipt load changed an exact persisted receipt");

        var finishPredecessor = new AgentWorkflowBundleTransition(
            bundleDocument,
            finishReviewEvaluation);
        AgentWorkflowBundleTransition finishSuccessor =
            lifecycle.AdvanceReviewed(
                finishPredecessor,
                reviewedBundle.Npc,
                reviewedBundle.RequestDigest,
                accepted,
                [request, proposal, preview, accepted.Artifact],
                NewPath(root, "finish-apply-successor.json"));
        ProtocolNextActionBinding proposalHashBinding =
            finishSuccessor.Evaluation.NextActions.Single()
                .RequiredBindings.Single(binding =>
                    binding.Option == "--proposal-sha256");
        Assert(
            finishSuccessor.Evaluation.Outcome ==
                WorkflowEvaluationOutcome.Blocked &&
            finishSuccessor.Evaluation.Phase == AgentWorkflowPhase.Apply &&
            finishSuccessor.Evaluation.Diagnostics is
                [{ Code: "workflow-next-action-prerequisites-missing" }] &&
            finishSuccessor.Evaluation.NextActions is [var applyAction] &&
            applyAction.Command == "npc finish apply" &&
            applyAction.MissingPrerequisites.SequenceEqual(
                ["--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"]) &&
            proposalHashBinding.Value == proposal.SemanticSha256 &&
            proposalHashBinding.Value != proposal.Sha256 &&
            proposalHashBinding.ArtifactSha256 == proposal.Sha256,
            "accepted Finish review did not derive an Apply action bound to the distinct semantic proposal hash");
        ProvesExactDisplayedAuthorityAtEvaluatorAndPublicTransitions(
            root,
            lifecycle,
            finishPredecessor,
            accepted,
            [request, proposal, preview],
            request.Sha256,
            directExtraUsesReceiptArtifact: false,
            "finish");
        AgentReviewReceiptDocument loadedFinishSuccessorReceipt =
            service.LoadForSuccessor(
                finishSuccessor.Document,
                accepted.Path,
                accepted.Sha256);
        Assert(
            loadedFinishSuccessorReceipt.Utf8Json.AsSpan().SequenceEqual(
                accepted.Utf8Json.AsSpan()),
            "Finish Apply successor receipt load changed canonical evidence");
        AgentWorkflowBundleTransition loadedFinishCommand =
            lifecycle.LoadForReviewedCommand(
                finishSuccessor.Document.Path,
                finishSuccessor.Document.Sha256,
                "npc finish apply",
                successor => service.LoadForSuccessor(
                    successor,
                    accepted.Path,
                    accepted.Sha256));
        Assert(
            loadedFinishCommand.Evaluation.NextActions is [var loadedApply] &&
            loadedApply.Command == "npc finish apply" &&
            loadedApply.RequiredBindings.Single(binding =>
                binding.Option == "--proposal-sha256").Value ==
                proposal.SemanticSha256,
            "reviewed Finish Apply command load lost the semantic proposal binding");
        foreach (string? invalidSemantic in new[]
                 {
                     null,
                     HashA.ToLowerInvariant()
                 })
        {
            AgentWorkflowBundle invalidProposalBundle = reviewedBundle with
            {
                Artifacts =
                [
                    request,
                    proposal with { SemanticSha256 = invalidSemantic },
                    preview
                ]
            };
            WorkflowEvaluation invalidProposalEvaluation = EvaluateWithReceipt(
                invalidProposalBundle,
                accepted,
                bundleDocument.Sha256);
            Assert(
                invalidProposalEvaluation.Outcome ==
                    WorkflowEvaluationOutcome.Blocked &&
                invalidProposalEvaluation.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == "workflow-input-invalid") &&
                invalidProposalEvaluation.NextActions.IsEmpty,
                "Finish Apply action accepted an absent or invalid semantic proposal hash");
        }

        ProvesReviewedCarryoverRefusalTable(
            root,
            lifecycle,
            finishPredecessor,
            accepted,
            [request, proposal, preview]);
        ProvesFinishSuccessorRefusalTable(
            root,
            bundleCodec,
            lifecycle,
            service,
            finishPredecessor,
            finishSuccessor,
            accepted,
            request,
            proposal,
            preview);

        foreach (ReviewOutcome outcome in Enum.GetValues<ReviewOutcome>())
        {
            AgentReviewReceiptDocument document = outcome == ReviewOutcome.Accepted
                ? accepted
                : service.Create(
                    bundleDocument,
                    proposal.Sha256,
                    displayed,
                    outcome,
                    null,
                    new WorkspacePath(Path.Combine(
                        root,
                        $"{outcome.ToString().ToLowerInvariant()}-review.json")));
            AgentReviewReceiptDocument reopened = service.Load(
                bundleDocument,
                proposal.Sha256,
                displayed,
                document.Path,
                document.Sha256);
            Assert(reopened.Receipt.Outcome == outcome,
                $"{outcome} receipt did not persist as valid evidence");

            WorkflowEvaluation evaluation = EvaluateWithReceipt(
                reviewedBundle,
                reopened,
                bundleDocument.Sha256);
            Assert(
                (outcome == ReviewOutcome.Accepted &&
                 evaluation.Outcome == WorkflowEvaluationOutcome.Blocked) ||
                (outcome != ReviewOutcome.Accepted &&
                 evaluation.Outcome == WorkflowEvaluationOutcome.Blocked &&
                 evaluation.Diagnostics.Any(diagnostic =>
                     diagnostic.Code == "workflow-review-not-accepted")),
                $"{outcome} receipt produced the wrong Finish Apply authorization result");
            if (outcome == ReviewOutcome.Accepted)
                Assert(
                    evaluation.NextActions is [var loopApplyAction] &&
                    loopApplyAction.Command == "npc finish apply" &&
                    !loopApplyAction.RequiresHumanAction &&
                    loopApplyAction.MissingPrerequisites.SequenceEqual(
                        ["--workflow-bundle", "--workflow-bundle-sha256",
                            "--workflow-output"]),
                    "service-produced Apply review did not authorize the exact ready action");
            else
            {
                WorkspacePath refusedSuccessor = NewPath(
                    root,
                    $"finish-{outcome.ToString().ToLowerInvariant()}-advance-refused.json");
                AssertWorkflowRefused(
                    () => lifecycle.AdvanceReviewed(
                        finishPredecessor,
                        reviewedBundle.Npc,
                        reviewedBundle.RequestDigest,
                        reopened,
                        [request, proposal, preview, reopened.Artifact],
                        refusedSuccessor),
                    "workflow-review-not-accepted",
                    $"{outcome} Finish receipt authorized Apply");
                Assert(
                    !File.Exists(refusedSuccessor.Value),
                    $"{outcome} Finish receipt left an Apply successor");
            }
            Assert(
                Required(evaluation, AgentAuthorityKind.HumanVisualAcceptance) &&
                Required(evaluation, AgentAuthorityKind.GameRuntimeVerification) &&
                Required(evaluation, AgentAuthorityKind.PromotionApproval),
                $"{outcome} receipt granted visual, runtime, or promotion authority");
        }

        var refusalCases = new (string Name, string Code, Action Act)[]
        {
            ("empty display", "review-displayed-empty", () => service.Create(
                bundleDocument, proposal.Sha256, [], ReviewOutcome.Accepted, null,
                NewPath(root, "empty-display.json"))),
            ("duplicate display", "review-displayed-duplicate", () => service.Create(
                bundleDocument, proposal.Sha256, [preview, preview], ReviewOutcome.Accepted, null,
                NewPath(root, "duplicate-display.json"))),
            ("unbound display", "review-displayed-unbound", () => service.Create(
                bundleDocument, proposal.Sha256,
                [preview with { Sha256 = HashA }], ReviewOutcome.Accepted, null,
                NewPath(root, "unbound-display.json"))),
            ("proposal semantic drift", "review-displayed-unbound", () => service.Create(
                bundleDocument, proposal.Sha256,
                [proposal with { SemanticSha256 = HashA }, preview],
                ReviewOutcome.Accepted, null,
                NewPath(root, "proposal-semantic-drift.json"))),
            ("wrong display kind", "review-displayed-kind-invalid", () => service.Create(
                bundleDocument, proposal.Sha256, [request, preview], ReviewOutcome.Accepted, null,
                NewPath(root, "wrong-display-kind.json"))),
            ("missing preview", "review-preview-required", () => service.Create(
                bundleDocument, proposal.Sha256, [proposal], ReviewOutcome.Accepted, null,
                NewPath(root, "missing-preview.json"))),
            ("missing proposal", "review-proposal-required", () => service.Create(
                bundleDocument, proposal.Sha256, [preview], ReviewOutcome.Accepted, null,
                NewPath(root, "missing-proposal.json"))),
            ("missing proposal load", "review-proposal-required", () => service.Load(
                bundleDocument, proposal.Sha256, [preview], accepted.Path,
                accepted.Sha256)),
            ("stale proposal", "review-proposal-stale", () => service.Create(
                bundleDocument, HashA, displayed, ReviewOutcome.Accepted, null,
                NewPath(root, "stale-proposal.json"))),
            ("note carriage return", "review-contract-invalid", () => service.Create(
                bundleDocument, proposal.Sha256, displayed, ReviewOutcome.Accepted, "one\rtwo",
                NewPath(root, "note-cr.json"))),
            ("note oversized", "review-contract-invalid", () => service.Create(
                bundleDocument, proposal.Sha256, displayed, ReviewOutcome.Accepted,
                new string('n', 1025), NewPath(root, "note-oversized.json"))),
            ("note ill formed", "review-contract-invalid", () => service.Create(
                bundleDocument, proposal.Sha256, displayed, ReviewOutcome.Accepted, "\uD800",
                NewPath(root, "note-ill-formed.json"))),
            ("overwrite", "review-output-exists", () => service.Create(
                bundleDocument, proposal.Sha256, displayed, ReviewOutcome.Accepted, null,
                accepted.Path)),
            ("cross-scope Analyze receipt", "review-receipt-binding-stale", () =>
                service.Load(
                    bundleDocument, proposal.Sha256, displayed,
                    analyzeLoaded.Path, analyzeLoaded.Sha256)),
            ("cross-scope Apply receipt", "review-receipt-binding-stale", () =>
                service.Load(
                    analyzeBundleDocument, package.Sha256, analyzeDisplayed,
                    accepted.Path, accepted.Sha256))
        };
        foreach ((string name, string code, Action act) in refusalCases)
            AssertRefused(act, code, $"{name} was accepted");

        byte[] proposalBytes = File.ReadAllBytes(proposal.Path.Value);
        File.WriteAllText(proposal.Path.Value, "stale proposal bytes", new UTF8Encoding(false));
        AssertRefused(
            () => service.Create(
                bundleDocument, proposal.Sha256, displayed, ReviewOutcome.Accepted, null,
                NewPath(root, "stale-bundle.json")),
            "review-bundle-stale",
            "a bundle whose bound proposal changed physically was accepted");
        File.WriteAllBytes(proposal.Path.Value, proposalBytes);

        AssertMutatedReceiptRefused(
            service,
            bundleDocument,
            proposal,
            displayed,
            root,
            "notice-drift.json",
            AgentReviewReceiptService.ComputeCanonicalBytes(accepted.Receipt with
            {
                AuthorityNoticeSha256 = HashA
            }),
            "review-receipt-binding-stale");
        AssertMutatedReceiptRefused(
            service,
            bundleDocument,
            proposal,
            displayed,
            root,
            "scope-drift.json",
            AgentReviewReceiptService.ComputeCanonicalBytes(accepted.Receipt with
            {
                Scope = "foreign-finish-scope"
            }),
            "review-receipt-binding-stale");

        WorkflowEvaluation noticeDriftEvaluation = EvaluateWithReceipt(
            reviewedBundle,
            accepted with
            {
                Receipt = accepted.Receipt with { AuthorityNoticeSha256 = HashA }
            },
            bundleDocument.Sha256);
        WorkflowEvaluation scopeDriftEvaluation = EvaluateWithReceipt(
            reviewedBundle,
            accepted with
            {
                Receipt = accepted.Receipt with { Scope = "foreign-finish-scope" }
            },
            bundleDocument.Sha256);
        WorkflowEvaluation crossScopeEvaluation = EvaluateWithReceipt(
            analyzeBundle,
            accepted,
            analyzeBundleDocument.Sha256);
        Assert(
            noticeDriftEvaluation.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "workflow-review-contract-drift") &&
            scopeDriftEvaluation.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "workflow-review-contract-drift") &&
            crossScopeEvaluation.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "workflow-review-contract-drift"),
            "evaluator accepted review notice, scope drift, or cross-scope reuse");

        AgentWorkflowBundle ambiguousBundle = Bundle(
            [package, proposal, preview, request]);
        AgentWorkflowBundleDocument ambiguousDocument = bundleCodec.WriteNew(
            ambiguousBundle,
            NewPath(root, "ambiguous-workflow.json"));
        AssertRefused(
            () => service.Create(
                ambiguousDocument,
                proposal.Sha256,
                [proposal, preview],
                ReviewOutcome.Accepted,
                null,
                NewPath(root, "ambiguous-review.json")),
            "review-purpose-ambiguous",
            "bundle with both review proposal authorities was accepted");
        AgentWorkflowBundle missingPurposeBundle = Bundle([request, preview]);
        AgentWorkflowBundleDocument missingPurposeDocument = bundleCodec.WriteNew(
            missingPurposeBundle,
            NewPath(root, "missing-purpose-workflow.json"));
        AssertRefused(
            () => service.Create(
                missingPurposeDocument,
                proposal.Sha256,
                [preview],
                ReviewOutcome.Accepted,
                null,
                NewPath(root, "missing-purpose-review.json")),
            "review-purpose-missing",
            "bundle without a review proposal authority was accepted");

        AgentWorkflowBundle foreignBundle = reviewedBundle with
        {
            RequestDigest = HashA
        };
        AgentWorkflowBundleDocument foreignBundleDocument = bundleCodec.WriteNew(
            foreignBundle,
            NewPath(root, "foreign-workflow.json"));
        AgentReviewReceiptDocument foreignReceipt = service.Create(
            foreignBundleDocument,
            proposal.Sha256,
            displayed,
            ReviewOutcome.Accepted,
            null,
            NewPath(root, "foreign-review.json"));
        AssertRefused(
            () => service.Load(
                bundleDocument,
                proposal.Sha256,
                displayed,
                foreignReceipt.Path,
                foreignReceipt.Sha256),
            "review-receipt-binding-stale",
            "a receipt from another workflow bundle was accepted");

        string canonicalText = Encoding.UTF8.GetString(accepted.Utf8Json.AsSpan());
        AssertMutatedReceiptRefused(
            service, bundleDocument, proposal, displayed, root,
            "unknown-property.json",
            Encoding.UTF8.GetBytes(canonicalText.Replace(
                "{\n", "{\n  \"unknown\": true,\n", StringComparison.Ordinal)),
            "review-json-unknown-property");
        AssertMutatedReceiptRefused(
            service, bundleDocument, proposal, displayed, root,
            "duplicate-property.json",
            Encoding.UTF8.GetBytes(canonicalText.Replace(
                "{\n",
                "{\n  \"schema\": \"actorwright.review-receipt.v1\",\n",
                StringComparison.Ordinal)),
            "review-json-duplicate-property");
        AssertMutatedReceiptRefused(
            service, bundleDocument, proposal, displayed, root,
            "noncanonical.json",
            [.. accepted.Utf8Json, (byte)'\n'],
            "review-json-noncanonical");

        string oversized = Path.Combine(root, "oversized-review.json");
        using (var stream = new FileStream(
                   oversized, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength((64 * 1024) + 1);
        AssertRefused(
            () => service.Load(
                bundleDocument,
                proposal.Sha256,
                displayed,
                new WorkspacePath(oversized),
                Hash(File.ReadAllBytes(oversized))),
            "review-file-size-invalid",
            "an oversized receipt was accepted");

        ProveReparseRefusalWhenSupported(
            service,
            bundleDocument,
            proposal,
            displayed,
            accepted,
            root);
        Assert(
            !Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.TopDirectoryOnly).Any(),
            "receipt refusals leaked a sibling staging file");
        ProvesConvenienceWrapperReleasesPublishedLease(
            root,
            policy,
            bundleDocument,
            proposal,
            displayed);
        ProvesPublicCreateReleasesPublishedLease(
            root,
            policy,
            bundleDocument,
            proposal,
            displayed);
    }

    private static void ProvesReviewedCarryoverRefusalTable(
        string root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentWorkflowBundleTransition predecessor,
        AgentReviewReceiptDocument receipt,
        ImmutableArray<WorkflowArtifactBinding> nonReceiptArtifacts)
    {
        var mutations = new (
            string Name,
            Func<ImmutableArray<WorkflowArtifactBinding>,
                ImmutableArray<WorkflowArtifactBinding>> Mutate)[]
        {
            (
                "kind",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with
                    {
                        Kind = WorkflowArtifactKinds.ReviewedWorkspaceIntake
                    })),
            (
                "path",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with
                    {
                        Path = NewPath(root, "foreign-carryover.bin")
                    })),
            (
                "size",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with { Size = artifacts[0].Size + 1 })),
            (
                "schema",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with
                    {
                        SchemaOrMediaType = "application/octet-stream"
                    })),
            (
                "producer",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with { ProducerCommand = "foreign producer" })),
            (
                "request",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with { RequestDigest = HashA })),
            (
                "input-closure",
                artifacts => artifacts.SetItem(
                    0,
                    artifacts[0] with { InputArtifactHashes = [HashA] })),
            (
                "order",
                artifacts => artifacts.Reverse().ToImmutableArray())
        };
        if (nonReceiptArtifacts.Any(artifact => artifact.Kind ==
                WorkflowArtifactKinds.NpcFinishCoreProposal))
        {
            mutations =
            [
                .. mutations,
                (
                    "semantic-sha256",
                    artifacts =>
                    {
                        int index = Array.FindIndex(
                            artifacts.ToArray(),
                            artifact => artifact.Kind ==
                                WorkflowArtifactKinds.NpcFinishCoreProposal);
                        return artifacts.SetItem(
                            index,
                            artifacts[index] with { SemanticSha256 = HashA });
                    })
            ];
        }

        foreach ((string name,
                     Func<ImmutableArray<WorkflowArtifactBinding>,
                         ImmutableArray<WorkflowArtifactBinding>> mutate) in
                 mutations)
        {
            ImmutableArray<WorkflowArtifactBinding> drifted = mutate(
                nonReceiptArtifacts);
            WorkspacePath output = NewPath(
                root,
                $"carryover-{predecessor.Document.Path.GetHashCode():X8}-{name}.json");
            AssertWorkflowRefused(
                () => lifecycle.AdvanceReviewed(
                    predecessor,
                    predecessor.Document.Bundle.Npc,
                    predecessor.Document.Bundle.RequestDigest,
                    receipt,
                    [.. drifted, receipt.Artifact],
                    output),
                "workflow-reviewed-carryover-mismatch",
                $"reviewed successor accepted same-hash {name} carryover drift");
            Assert(
                !File.Exists(output.Value),
                $"same-hash {name} carryover drift left a successor file");
        }
    }

    private static void ProvesReviewedCallbackRefusalTable(
        string root,
        AgentWorkflowBundleCodec codec,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService service,
        AgentWorkflowBundleTransition successor,
        AgentReviewReceiptDocument receipt)
    {
        var mutations = new (
            string Name,
            Func<AgentReviewReceiptDocument, AgentReviewReceiptDocument>
                Mutate)[]
        {
            (
                "path",
                document => document with
                {
                    Path = NewPath(root, "callback-foreign-receipt.json")
                }),
            (
                "size",
                document => document with { Size = document.Size + 1 }),
            (
                "sha256",
                document => document with { Sha256 = HashA }),
            (
                "bytes",
                document => document with
                {
                    Utf8Json = [.. document.Utf8Json, (byte)'\n']
                }),
            (
                "artifact",
                document => document with
                {
                    Artifact = document.Artifact with
                    {
                        ProducerCommand = "foreign reviewer"
                    }
                }),
            (
                "receipt",
                document => document with
                {
                    Receipt = document.Receipt with
                    {
                        Scope = "foreign-review-scope"
                    }
                }),
            (
                "closure",
                document => document with
                {
                    Artifact = document.Artifact with
                    {
                        InputArtifactHashes = document.Artifact
                            .InputArtifactHashes.Add(HashA)
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToImmutableArray()
                    }
                })
        };
        foreach ((string name,
                     Func<AgentReviewReceiptDocument,
                         AgentReviewReceiptDocument> mutate) in mutations)
        {
            AgentReviewReceiptDocument forged = mutate(receipt);
            AssertReviewOrWorkflowRefused(
                () => lifecycle.LoadForReviewedCommand(
                    successor.Document.Path,
                    successor.Document.Sha256,
                    "npc finish analyze",
                    _ => forged),
                "review-receipt-binding-stale",
                $"reviewed command accepted callback {name} drift");
        }

        AgentWorkflowBundleDocument sizeDrift = successor.Document with
        {
            Bundle = successor.Document.Bundle with
            {
                Artifacts = ReplaceReceipt(
                    successor.Document.Bundle.Artifacts,
                    receipt.Artifact with
                    {
                        Size = receipt.Artifact.Size + 1
                    })
            }
        };
        AssertRefused(
            () => service.LoadForSuccessor(
                sizeDrift,
                receipt.Path,
                receipt.Sha256),
            "review-receipt-binding-stale",
            "successor receipt artifact size drift was accepted");

        AgentWorkflowBundleDocument stateTamper = codec.WriteNew(
            successor.Document.Bundle with
            {
                Phase = AgentWorkflowPhase.Discover
            },
            NewPath(root, "reviewed-derived-state-tamper.json"));
        AssertWorkflowRefused(
            () => lifecycle.LoadForReviewedCommand(
                stateTamper.Path,
                stateTamper.Sha256,
                "npc finish analyze",
                current => service.LoadForSuccessor(
                    current,
                    receipt.Path,
                    receipt.Sha256)),
            "workflow-derived-state-mismatch",
            "reviewed command accepted persisted derived-state tamper");
    }

    private static void
        ProvesExactDisplayedAuthorityAtEvaluatorAndPublicTransitions(
        string root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentWorkflowBundleTransition predecessor,
        AgentReviewReceiptDocument accepted,
        ImmutableArray<WorkflowArtifactBinding> nonReceiptArtifacts,
        string extraDisplayedHash,
        bool directExtraUsesReceiptArtifact,
        string signature)
    {
        WorkflowArtifactBinding preview = nonReceiptArtifacts.Single(
            artifact => artifact.Kind ==
                WorkflowArtifactKinds.NpcPreviewManifest);
        var mutations = new (string Name, AgentReviewReceipt Receipt)[]
        {
            (
                "missing-proposal",
                accepted.Receipt with
                {
                    DisplayedArtifactHashes = [preview.Sha256]
                }),
            (
                "extra-displayed-hash",
                accepted.Receipt with
                {
                    DisplayedArtifactHashes = accepted.Receipt
                        .DisplayedArtifactHashes
                        .Add(extraDisplayedHash)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToImmutableArray()
                })
        };

        foreach ((string name, AgentReviewReceipt receipt) in mutations)
        {
            AgentReviewReceiptDocument canonical = WriteReceiptVariant(
                root,
                accepted,
                $"{signature}-{name}",
                receipt);
            AgentReviewReceiptDocument direct = canonical;
            if (name == "extra-displayed-hash" &&
                directExtraUsesReceiptArtifact)
            {
                direct = accepted with
                {
                    Receipt = receipt
                };
            }

            WorkflowEvaluation evaluation = EvaluateWithReceipt(
                predecessor.Document.Bundle,
                direct,
                predecessor.Document.Sha256);
            Assert(
                evaluation.Outcome == WorkflowEvaluationOutcome.Blocked &&
                evaluation.Diagnostics is
                    [{ Code: "workflow-review-binding-stale" }] &&
                evaluation.NextActions.IsEmpty,
                $"The direct {signature} evaluator authorized {name} displayed authority.");

            ImmutableArray<WorkflowArtifactBinding> successorArtifacts =
                [.. nonReceiptArtifacts, canonical.Artifact];
            WorkspacePath publicOutput = NewPath(
                root,
                $"{signature}-{name}-advance.json");
            AssertWorkflowRefused(
                () => lifecycle.AdvanceReviewed(
                    predecessor,
                    predecessor.Document.Bundle.Npc,
                    predecessor.Document.Bundle.RequestDigest,
                    canonical,
                    successorArtifacts,
                    publicOutput),
                "workflow-review-binding-stale",
                $"AdvanceReviewed authorized {signature} {name} displayed authority");
            Assert(
                !File.Exists(publicOutput.Value),
                $"AdvanceReviewed left a {signature} {name} successor.");

            WorkspacePath retainedOutput = NewPath(
                root,
                $"{signature}-{name}-advance-retained.json");
            AssertWorkflowRefused(
                () =>
                {
                    using AgentWorkflowBundleTransitionLease _ =
                        lifecycle.AdvanceReviewedRetained(
                            predecessor,
                            predecessor.Document.Bundle.Npc,
                            predecessor.Document.Bundle.RequestDigest,
                            canonical,
                            successorArtifacts,
                            retainedOutput);
                },
                "workflow-review-binding-stale",
                $"AdvanceReviewedRetained authorized {signature} {name} displayed authority");
            Assert(
                !File.Exists(retainedOutput.Value),
                $"AdvanceReviewedRetained left a {signature} {name} successor.");
        }
    }

    private static void ProvesFinishSuccessorRefusalTable(
        string root,
        AgentWorkflowBundleCodec codec,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService service,
        AgentWorkflowBundleTransition predecessor,
        AgentWorkflowBundleTransition successor,
        AgentReviewReceiptDocument accepted,
        WorkflowArtifactBinding request,
        WorkflowArtifactBinding proposal,
        WorkflowArtifactBinding preview)
    {
        AgentWorkflowBundle foreignBundle = predecessor.Document.Bundle with
        {
            Npc = predecessor.Document.Bundle.Npc with
            {
                DisplayName = "Foreign Finish Review NPC"
            }
        };
        WorkflowEvaluation foreignEvaluation = Evaluate(foreignBundle);
        foreignBundle = foreignBundle with
        {
            Phase = foreignEvaluation.Phase,
            Authority = foreignEvaluation.Authority,
            NextActions = foreignEvaluation.NextActions
        };
        AgentWorkflowBundleDocument foreignDocument = codec.WriteNew(
            foreignBundle,
            NewPath(root, "foreign-finish-predecessor.json"));
        WorkspacePath wrongPredecessorOutput = NewPath(
            root,
            "foreign-finish-successor.json");
        AssertWorkflowRefused(
            () => lifecycle.AdvanceReviewed(
                new AgentWorkflowBundleTransition(
                    foreignDocument,
                    foreignEvaluation),
                foreignBundle.Npc,
                foreignBundle.RequestDigest,
                accepted,
                [request, proposal, preview, accepted.Artifact],
                wrongPredecessorOutput),
            "workflow-review-binding-stale",
            "Finish receipt from another predecessor authorized Apply");
        Assert(
            !File.Exists(wrongPredecessorOutput.Value),
            "foreign Finish predecessor left an Apply successor");

        var mutations = new (string Name, AgentReviewReceipt Receipt)[]
        {
            (
                "finish-wrong-proposal",
                accepted.Receipt with { ProposalSha256 = HashA }),
            (
                "finish-wrong-preview",
                accepted.Receipt with
                {
                    DisplayedArtifactHashes =
                        new[] { proposal.Sha256, HashA }
                            .Order(StringComparer.Ordinal)
                            .ToImmutableArray()
                }),
            (
                "finish-missing-proposal-display",
                accepted.Receipt with
                {
                    DisplayedArtifactHashes = [preview.Sha256]
                }),
            (
                "finish-wrong-scope",
                accepted.Receipt with { Scope = "foreign-review-scope" }),
            (
                "finish-wrong-notice",
                accepted.Receipt with { AuthorityNoticeSha256 = HashA }),
            (
                "finish-rejected",
                accepted.Receipt with { Outcome = ReviewOutcome.Rejected }),
            (
                "finish-revision",
                accepted.Receipt with
                {
                    Outcome = ReviewOutcome.RevisionRequested
                })
        };
        foreach ((string name, AgentReviewReceipt receipt) in mutations)
        {
            (AgentWorkflowBundleDocument document, WorkspacePath receiptPath,
                string receiptSha256) = WriteSuccessorVariant(
                    root,
                    codec,
                    successor.Document.Bundle,
                    accepted.Artifact,
                    [request, proposal, preview],
                    name,
                    receipt,
                    inputClosureMutation: null);
            AssertRefused(
                () => service.LoadForSuccessor(
                    document,
                    receiptPath,
                    receiptSha256),
                receipt.Outcome == ReviewOutcome.Accepted
                    ? "review-receipt-binding-stale"
                    : "review-receipt-not-accepted",
                $"Finish successor accepted {name} receipt drift");
        }

        (AgentWorkflowBundleDocument closureDrift, WorkspacePath closurePath,
            string closureSha256) = WriteSuccessorVariant(
                root,
                codec,
                successor.Document.Bundle,
                accepted.Artifact,
                [request, proposal, preview],
                "finish-closure-drift",
                accepted.Receipt,
                inputs => inputs.Add(HashA).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray());
        AssertRefused(
            () => service.LoadForSuccessor(
                closureDrift,
                closurePath,
                closureSha256),
            "review-receipt-binding-stale",
            "Finish successor accepted receipt input-closure drift");
    }

    private static void ProvesSuccessorReceiptRefusalTable(
        string root,
        AgentWorkflowBundleCodec codec,
        AgentReviewReceiptService service,
        AgentWorkflowBundleTransition successor,
        AgentReviewReceiptDocument accepted,
        WorkflowArtifactBinding package,
        WorkflowArtifactBinding preview)
    {
        byte[] originalReceiptBytes = File.ReadAllBytes(accepted.Path.Value);
        File.WriteAllBytes(
            accepted.Path.Value,
            "stale physical receipt bytes"u8.ToArray());
        AssertRefused(
            () => service.LoadForSuccessor(
                successor.Document,
                accepted.Path,
                accepted.Sha256),
            "review-hash-mismatch",
            "stale physical receipt bytes were accepted from a successor");
        File.WriteAllBytes(accepted.Path.Value, originalReceiptBytes);

        AssertRefused(
            () => service.LoadForSuccessor(
                successor.Document,
                accepted.Path,
                HashA),
            "review-receipt-binding-stale",
            "a receipt digest different from the successor binding was accepted");

        AgentWorkflowBundleDocument pathDrift = successor.Document with
        {
            Bundle = successor.Document.Bundle with
            {
                Artifacts = ReplaceReceipt(
                    successor.Document.Bundle.Artifacts,
                    accepted.Artifact with
                    {
                        Path = NewPath(root, "foreign-receipt-path.json")
                    })
            }
        };
        AssertRefused(
            () => service.LoadForSuccessor(
                pathDrift,
                accepted.Path,
                accepted.Sha256),
            "review-receipt-binding-stale",
            "receipt path drift inside the successor binding was accepted");

        var mutations = new (string Name, AgentReviewReceipt Receipt)[]
        {
            (
                "wrong-proposal",
                accepted.Receipt with { ProposalSha256 = HashA }),
            (
                "wrong-preview",
                accepted.Receipt with
                {
                    DisplayedArtifactHashes =
                        new[] { package.Sha256, HashA }
                            .Order(StringComparer.Ordinal)
                            .ToImmutableArray()
                }),
            (
                "missing-proposal-display",
                accepted.Receipt with
                {
                    DisplayedArtifactHashes = [preview.Sha256]
                }),
            (
                "wrong-scope",
                accepted.Receipt with { Scope = "foreign-review-scope" }),
            (
                "wrong-notice",
                accepted.Receipt with { AuthorityNoticeSha256 = HashA })
        };
        foreach ((string name, AgentReviewReceipt receipt) in mutations)
        {
            (AgentWorkflowBundleDocument document, WorkspacePath receiptPath,
                string receiptSha256) = WriteSuccessorVariant(
                    root,
                    codec,
                    successor.Document.Bundle,
                    accepted.Artifact,
                    [package, preview],
                    name,
                    receipt,
                    inputClosureMutation: null);
            AssertRefused(
                () => service.LoadForSuccessor(
                    document,
                    receiptPath,
                    receiptSha256),
                "review-receipt-binding-stale",
                $"successor accepted {name} receipt authority");
        }

        (AgentWorkflowBundleDocument closureDrift, WorkspacePath closurePath,
            string closureSha256) = WriteSuccessorVariant(
                root,
                codec,
                successor.Document.Bundle,
                accepted.Artifact,
                [package, preview],
                "input-closure-drift",
                accepted.Receipt,
                inputs => inputs.Add(HashA).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray());
        AssertRefused(
            () => service.LoadForSuccessor(
                closureDrift,
                closurePath,
                closureSha256),
            "review-receipt-binding-stale",
            "receipt artifact accepted a non-closed input-hash superset");

        var bindingMutations = new (
            string Name,
            Func<WorkflowArtifactBinding, WorkflowArtifactBinding> Mutate)[]
        {
            (
                "schema-drift",
                artifact => artifact with
                {
                    SchemaOrMediaType = "application/json"
                }),
            (
                "producer-drift",
                artifact => artifact with
                {
                    ProducerCommand = "foreign reviewer"
                }),
            (
                "request-drift",
                artifact => artifact with { RequestDigest = HashA })
        };
        foreach ((string name,
                     Func<WorkflowArtifactBinding, WorkflowArtifactBinding>
                         mutate) in bindingMutations)
        {
            WorkflowArtifactBinding mutated = mutate(accepted.Artifact);
            AgentWorkflowBundleDocument document = codec.WriteNew(
                successor.Document.Bundle with
                {
                    Artifacts = [package, preview, mutated]
                },
                NewPath(root, $"{name}-successor.json"));
            AssertRefused(
                () => service.LoadForSuccessor(
                    document,
                    accepted.Path,
                    accepted.Sha256),
                "review-receipt-binding-stale",
                $"successor accepted receipt artifact {name}");
        }

        foreach (ReviewOutcome outcome in new[]
                 {
                     ReviewOutcome.Rejected,
                     ReviewOutcome.RevisionRequested
                 })
        {
            AgentReviewReceipt receipt = accepted.Receipt with
            {
                Outcome = outcome
            };
            (AgentWorkflowBundleDocument document, WorkspacePath receiptPath,
                string receiptSha256) = WriteSuccessorVariant(
                    root,
                    codec,
                    successor.Document.Bundle,
                    accepted.Artifact,
                    [package, preview],
                    $"successor-{outcome.ToString().ToLowerInvariant()}",
                    receipt,
                    inputClosureMutation: null);
            AssertRefused(
                () => service.LoadForSuccessor(
                    document,
                    receiptPath,
                    receiptSha256),
                "review-receipt-not-accepted",
                $"{outcome} successor receipt was treated as authorizing");
        }
    }

    private static AgentReviewReceiptDocument WriteReceiptVariant(
        string root,
        AgentReviewReceiptDocument template,
        string name,
        AgentReviewReceipt receipt)
    {
        byte[] bytes = AgentReviewReceiptService.ComputeCanonicalBytes(receipt);
        WorkspacePath path = NewPath(root, $"{name}-receipt.json");
        File.WriteAllBytes(path.Value, bytes);
        ImmutableArray<string> inputs =
        [
            receipt.BundleSha256,
            receipt.ProposalSha256,
            receipt.AuthorityNoticeSha256,
            .. receipt.DisplayedArtifactHashes
        ];
        inputs = inputs.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        WorkflowArtifactBinding artifact = template.Artifact with
        {
            Path = path,
            Size = bytes.LongLength,
            Sha256 = Hash(bytes),
            InputArtifactHashes = inputs
        };
        return new AgentReviewReceiptDocument(
            receipt,
            path,
            bytes.LongLength,
            artifact.Sha256,
            bytes.ToImmutableArray(),
            artifact);
    }

    private static (
        AgentWorkflowBundleDocument Document,
        WorkspacePath ReceiptPath,
        string ReceiptSha256) WriteSuccessorVariant(
        string root,
        AgentWorkflowBundleCodec codec,
        AgentWorkflowBundle successor,
        WorkflowArtifactBinding receiptTemplate,
        ImmutableArray<WorkflowArtifactBinding> nonReceiptArtifacts,
        string name,
        AgentReviewReceipt receipt,
        Func<ImmutableArray<string>, ImmutableArray<string>>?
            inputClosureMutation)
    {
        byte[] bytes = AgentReviewReceiptService.ComputeCanonicalBytes(receipt);
        WorkspacePath receiptPath = NewPath(root, $"{name}-receipt.json");
        File.WriteAllBytes(receiptPath.Value, bytes);
        ImmutableArray<string> inputs =
        [
            receipt.BundleSha256,
            receipt.ProposalSha256,
            receipt.AuthorityNoticeSha256,
            .. receipt.DisplayedArtifactHashes
        ];
        inputs = inputs.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (inputClosureMutation is not null)
            inputs = inputClosureMutation(inputs);
        WorkflowArtifactBinding receiptArtifact = receiptTemplate with
        {
            Path = receiptPath,
            Size = bytes.LongLength,
            Sha256 = Hash(bytes),
            InputArtifactHashes = inputs
        };
        AgentWorkflowBundle variant = successor with
        {
            Artifacts = [.. nonReceiptArtifacts, receiptArtifact]
        };
        AgentWorkflowBundleDocument document = codec.WriteNew(
            variant,
            NewPath(root, $"{name}-successor.json"));
        return (document, receiptPath, receiptArtifact.Sha256);
    }

    private static ImmutableArray<WorkflowArtifactBinding> ReplaceReceipt(
        ImmutableArray<WorkflowArtifactBinding> artifacts,
        WorkflowArtifactBinding replacement) => artifacts
            .Select(artifact => artifact.Kind == WorkflowArtifactKinds.ReviewReceipt
                ? replacement
                : artifact)
            .ToImmutableArray();

    private static WorkflowEvaluation Evaluate(AgentWorkflowBundle bundle) =>
        AgentWorkflowService.Evaluate(
            bundle,
            bundle.Artifacts.ToImmutableDictionary(
                artifact => artifact.Kind,
                artifact => new VerifiedWorkflowArtifact(
                    artifact.Kind,
                    artifact.Path,
                    artifact.Size,
                    artifact.Sha256,
                    true),
                StringComparer.Ordinal));

    private static WorkflowEvaluation EvaluateWithReceipt(
        AgentWorkflowBundle reviewedBundle,
        AgentReviewReceiptDocument receipt,
        string reviewedBundleSha256)
    {
        AgentWorkflowBundle evaluationBundle = reviewedBundle with
        {
            Artifacts = [.. reviewedBundle.Artifacts, receipt.Artifact]
        };
        ImmutableDictionary<string, VerifiedWorkflowArtifact> verified =
            evaluationBundle.Artifacts.ToImmutableDictionary(
                artifact => artifact.Kind,
                artifact => new VerifiedWorkflowArtifact(
                    artifact.Kind,
                    artifact.Path,
                    artifact.Size,
                    artifact.Sha256,
                    true),
                StringComparer.Ordinal);
        return AgentWorkflowService.Evaluate(new WorkflowEvaluationInput(
            evaluationBundle,
            verified,
            receipt.Receipt,
            receipt.Sha256,
            reviewedBundleSha256));
    }

    private static bool Required(
        WorkflowEvaluation evaluation,
        AgentAuthorityKind kind) =>
        evaluation.Authority.Single(item => item.Kind == kind).State ==
        AgentAuthorityState.Required;

    private static void AssertMutatedReceiptRefused(
        AgentReviewReceiptService service,
        AgentWorkflowBundleDocument bundle,
        WorkflowArtifactBinding proposal,
        ImmutableArray<WorkflowArtifactBinding> displayed,
        string root,
        string name,
        byte[] bytes,
        string code)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        AssertRefused(
            () => service.Load(
                bundle,
                proposal.Sha256,
                displayed,
                new WorkspacePath(path),
                Hash(bytes)),
            code,
            $"mutated receipt '{name}' was accepted");
    }

    private static void ProvesConvenienceWrapperReleasesPublishedLease(
        string root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleDocument bundle,
        WorkflowArtifactBinding proposal,
        ImmutableArray<WorkflowArtifactBinding> displayed)
    {
        var service = new AgentReviewReceiptService(policy, new WorkspacePath(root));
        SetPinnedFileSystem(
            service,
            new FaceGeomHairRegionsPinnedFileSystem(
                new WorkspacePath(root),
                new FailPromotedReadbackHooks()));
        WorkspacePath output = NewPath(
            root,
            "desktop-wrapper-promoted-readback.json");
        try
        {
            service.CreateWithPublicationValidation(
                bundle,
                proposal.Sha256,
                displayed,
                ReviewOutcome.Accepted,
                reviewerNote: null,
                output,
                validateBeforePublication: static () => { },
                validateAfterPublication: static () => { });
            throw new InvalidOperationException(
                "The injected promoted readback failure was not observed.");
        }
        catch (AgentReviewReceiptPublishedException exception)
        {
            Assert(
                exception.Code == "review-post-publication-operation-failed",
                "The convenience wrapper changed its published failure semantics.");
        }

        byte[] committed = File.ReadAllBytes(output.Value);
        using (var exclusive = new FileStream(
                   output.Value,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Assert(exclusive.Length == committed.LongLength,
                "The convenience wrapper released a different receipt file.");
        }
        Assert(File.ReadAllBytes(output.Value).SequenceEqual(committed),
            "The convenience wrapper changed committed receipt bytes while releasing its lease.");
    }

    private static void ProvesPublicCreateReleasesPublishedLease(
        string root,
        KOnlyWorkspacePolicy policy,
        AgentWorkflowBundleDocument bundle,
        WorkflowArtifactBinding proposal,
        ImmutableArray<WorkflowArtifactBinding> displayed)
    {
        var service = new AgentReviewReceiptService(
            policy,
            new WorkspacePath(root));
        SetPinnedFileSystem(
            service,
            new FaceGeomHairRegionsPinnedFileSystem(
                new WorkspacePath(root),
                new FailPromotedReadbackHooks()));
        WorkspacePath output = NewPath(
            root,
            "public-create-promoted-readback.json");
        AgentReviewReceiptPublishedException? publishedFailure = null;
        try
        {
            try
            {
                service.Create(
                    bundle,
                    proposal.Sha256,
                    displayed,
                    ReviewOutcome.Accepted,
                    reviewerNote: null,
                    output);
                throw new InvalidOperationException(
                    "The injected public Create promoted readback failure was not observed.");
            }
            catch (AgentReviewReceiptPublishedException exception)
            {
                publishedFailure = exception;
                Assert(
                    exception.Code ==
                        "review-post-publication-operation-failed",
                    "Public Create changed its normalized published failure semantics.");
            }

            byte[] expected = publishedFailure.PublishedReceipt.Document
                .Utf8Json.AsSpan().ToArray();
            using (var exclusive = new FileStream(
                       output.Value,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                Assert(
                    exclusive.Length == expected.LongLength,
                    "Public Create released a different committed receipt file.");
            }
            byte[] committed = File.ReadAllBytes(output.Value);
            Assert(
                committed.SequenceEqual(expected) &&
                File.ReadAllBytes(output.Value).SequenceEqual(committed),
                "Public Create changed committed receipt bytes while releasing its published lease.");
        }
        finally
        {
            publishedFailure?.PublishedReceipt.Dispose();
        }
    }

    private static void SetPinnedFileSystem(
        AgentReviewReceiptService service,
        FaceGeomHairRegionsPinnedFileSystem fileSystem)
    {
        System.Reflection.FieldInfo field =
            typeof(AgentReviewReceiptService).GetField(
                "fileSystem",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "AgentReviewReceiptService file-system field was not found.");
        field.SetValue(service, fileSystem);
    }

    private sealed class FailPromotedReadbackHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private bool promoted;
        private int resolutionsAfterRename;

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath)
        {
            if (promoted && ++resolutionsAfterRename == 2)
                throw new IOException(
                    "Injected promoted receipt readback failure.");
            return actualFinalPath;
        }

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
            promoted = true;
            resolutionsAfterRename = 0;
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private static void ProveReparseRefusalWhenSupported(
        AgentReviewReceiptService service,
        AgentWorkflowBundleDocument bundle,
        WorkflowArtifactBinding proposal,
        ImmutableArray<WorkflowArtifactBinding> displayed,
        AgentReviewReceiptDocument accepted,
        string root)
    {
        string target = Path.Combine(root, "review-link-target");
        string link = Path.Combine(root, "review-link");
        Directory.CreateDirectory(target);
        string linkedReceipt = Path.Combine(target, "receipt.json");
        File.WriteAllBytes(linkedReceipt, accepted.Utf8Json.AsSpan().ToArray());
        if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                link, target, root))
            return;
        try
        {
            AssertRefused(
                () => service.Load(
                    bundle,
                    proposal.Sha256,
                    displayed,
                    new WorkspacePath(Path.Combine(link, "receipt.json")),
                    accepted.Sha256),
                "review-reparse-refused",
                "receipt loader followed a reparse traversal");
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    private static WorkflowArtifactBinding WriteArtifact(
        string root,
        string kind,
        string name,
        byte[] bytes)
    {
        string? semanticSha256 = null;
        if (kind == WorkflowArtifactKinds.NpcFinishCoreProposal)
        {
            var request = new SkyrimNpcFinishCoreRequest
            {
                AiPolicy = new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                    Confidence = SkyrimNpcFinishCoreConfidence.Average,
                    Energy = 50,
                    Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsAllies,
                    Mood = SkyrimNpcFinishCoreMood.Neutral
                },
                SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                {
                    Template = new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x1B217)),
                    TemplateEditorId = "DefaultSandboxEditorLocation512"
                },
                Output = new SkyrimNpcFinishCoreOutput
                {
                    PluginFileName = "Receipt.esp"
                }
            };
            var proposal = new SkyrimNpcFinishCoreProposal
            {
                RequestSha256 = new Sha256Hash(HashB),
                Request = request,
                Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
                NextFormId = new FormId(0x800),
                MasterOrder = ["Skyrim.esm"]
            };
            byte[] withoutSelf =
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    proposal,
                    new WorkspacePath(root));
            Sha256Hash semantic =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    withoutSelf);
            bytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal with { ProposalSha256 = semantic },
                new WorkspacePath(root));
            semanticSha256 = semantic.Value.ToUpperInvariant();
        }
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return new WorkflowArtifactBinding(
            kind,
            "application/json",
            new WorkspacePath(path),
            bytes.LongLength,
            Hash(bytes),
            "npc finish analyze",
            HashB,
            [],
            semanticSha256);
    }

    private static AgentWorkflowBundle Bundle(
        ImmutableArray<WorkflowArtifactBinding> artifacts) =>
        new(
            AgentWorkflowSchemas.BundleV1,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            GameEdition.SkyrimSpecialEdition,
            new WorkflowNpcIdentity("ReceiptNpc", "Receipt NPC", "Receipt.esp", "0x00000800"),
            AgentWorkflowPhase.Review,
            HashB,
            artifacts,
            [],
            []);

    private static WorkspacePath NewPath(string root, string name) =>
        new(Path.Combine(root, name));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void AssertRefused(
        Action action,
        string expectedCode,
        string message)
    {
        try
        {
            action();
        }
        catch (AgentReviewReceiptException exception)
        {
            Assert(
                exception.Code == expectedCode,
                $"{message}; expected '{expectedCode}', observed '{exception.Code}'");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertWorkflowRefused(
        Action action,
        string expectedCode,
        string message)
    {
        try
        {
            action();
        }
        catch (AgentWorkflowCodecException exception)
        {
            Assert(
                exception.Code == expectedCode,
                $"{message}; expected '{expectedCode}', observed '{exception.Code}'");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertReviewOrWorkflowRefused(
        Action action,
        string expectedCode,
        string message)
    {
        try
        {
            action();
        }
        catch (AgentReviewReceiptException exception)
        {
            Assert(
                exception.Code == expectedCode,
                $"{message}; expected '{expectedCode}', observed review '{exception.Code}'");
            return;
        }
        catch (AgentWorkflowCodecException exception)
        {
            Assert(
                exception.Code == expectedCode,
                $"{message}; expected '{expectedCode}', observed workflow '{exception.Code}'");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate the Actorwright repository root.");
    }

    private static void DeleteOwnedTestTree(string root, string testOwner)
    {
        string expectedParent = Path.GetFullPath(testOwner);
        string resolved = Path.GetFullPath(root);
        if (!string.Equals(
                Path.GetDirectoryName(resolved),
                expectedParent,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith(
                "agent-review-receipt-",
                StringComparison.Ordinal))
            throw new InvalidOperationException("Refused unsafe receipt test cleanup.");
        Directory.Delete(resolved, recursive: true);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
