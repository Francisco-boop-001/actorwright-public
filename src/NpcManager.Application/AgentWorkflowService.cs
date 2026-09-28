using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum WorkflowEvaluationOutcome
{
    Ready,
    Blocked
}

public sealed record WorkflowEvaluationInput(
    AgentWorkflowBundle Bundle,
    ImmutableDictionary<string, VerifiedWorkflowArtifact> VerifiedArtifacts,
    AgentReviewReceipt? VerifiedReviewReceipt,
    string? VerifiedReviewReceiptSha256,
    string? VerifiedBundleSha256);

public sealed record WorkflowEvaluation(
    WorkflowEvaluationOutcome Outcome,
    AgentWorkflowPhase Phase,
    ImmutableArray<WorkflowAuthorityEvidence> Authority,
    ImmutableArray<ProtocolNextAction> NextActions,
    ImmutableArray<ProtocolDiagnostic> Diagnostics);

public static partial class AgentWorkflowService
{
    private enum GoldenTransition
    {
        Discover,
        PresetInspect,
        CreateFromJslotPreflight,
        CreateFromJslot,
        Preview,
        PackagePreviewReview,
        FinishAnalyze,
        FinishProposalReview,
        FinishApply,
        FinishVerify,
        RuntimeWithoutReport,
        RuntimeWithGenericReport,
        UnreviewedFinishApply,
        UnreviewedFinishVerify,
        UnreviewedFinishReview
    }

    private sealed record TransitionDefinition(
        AgentWorkflowPhase Phase,
        GoldenTransition Transition);

    private static readonly ImmutableDictionary<string, TransitionDefinition> TransitionTable =
        CreateTransitionTable();

    public static WorkflowEvaluation Evaluate(
        AgentWorkflowBundle bundle,
        ImmutableDictionary<string, VerifiedWorkflowArtifact> verifiedArtifacts) =>
        Evaluate(new WorkflowEvaluationInput(bundle, verifiedArtifacts, null, null, null));

    public static WorkflowEvaluation Evaluate(WorkflowEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Bundle is null || input.VerifiedArtifacts is null)
            return Blocked(AgentWorkflowPhase.Discover, "workflow-input-invalid",
                "The workflow evaluation input is not structurally initialized.");

        try
        {
            AgentWorkflowContractValidation.Validate(input.Bundle);
        }
        catch (ArgumentException exception)
        {
            return Blocked(input.Bundle.Phase, "workflow-input-invalid", exception.Message);
        }

        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts =
            input.Bundle.Artifacts.ToImmutableDictionary(
                artifact => artifact.Kind,
                StringComparer.Ordinal);
        foreach (WorkflowArtifactBinding artifact in input.Bundle.Artifacts)
        {
            if (!input.VerifiedArtifacts.TryGetValue(artifact.Kind, out VerifiedWorkflowArtifact? verified))
                return Blocked(input.Bundle.Phase, "workflow-artifact-unverified",
                    $"Artifact '{artifact.Kind}' has no independent verification evidence.");
            try
            {
                AgentWorkflowContractValidation.Validate(verified);
            }
            catch (ArgumentException exception)
            {
                return Blocked(input.Bundle.Phase, "workflow-input-invalid", exception.Message);
            }

            if (!verified.IndependentlyVerified)
                return Blocked(input.Bundle.Phase, "workflow-artifact-unverified",
                    $"Artifact '{artifact.Kind}' was not independently verified.");
            if (verified.Kind != artifact.Kind || verified.Path != artifact.Path ||
                verified.Size != artifact.Size || verified.Sha256 != artifact.Sha256)
                return Blocked(input.Bundle.Phase, "workflow-artifact-binding-stale",
                    $"Artifact '{artifact.Kind}' no longer matches its verified binding.");
        }

        if (input.VerifiedArtifacts.Count != artifacts.Count ||
            input.VerifiedArtifacts.Keys.Any(kind => !artifacts.ContainsKey(kind)))
            return Blocked(input.Bundle.Phase, "workflow-transition-invalid",
                "Verified evidence contains artifacts outside the workflow bundle.");

        if (!TransitionTable.TryGetValue(Signature(artifacts.Keys), out TransitionDefinition? transition))
            return InvalidTransition(input.Bundle.Phase, artifacts);

        ProtocolDiagnostic? receiptDiagnostic = ValidateReceipt(input, artifacts);
        if (receiptDiagnostic is not null)
            return Blocked(transition.Phase, artifacts, receiptDiagnostic);

        return EvaluateTransition(input.Bundle, artifacts, transition);
    }

    private static ImmutableDictionary<string, TransitionDefinition> CreateTransitionTable()
    {
        ImmutableDictionary<string, TransitionDefinition>.Builder table =
            ImmutableDictionary.CreateBuilder<string, TransitionDefinition>(StringComparer.Ordinal);
        Add(GoldenTransition.Discover, AgentWorkflowPhase.Discover);
        Add(GoldenTransition.PresetInspect, AgentWorkflowPhase.Analyze,
            WorkflowArtifactKinds.ReviewedWorkspaceIntake);
        Add(GoldenTransition.CreateFromJslotPreflight, AgentWorkflowPhase.Analyze,
            WorkflowArtifactKinds.ReviewedWorkspaceIntake,
            WorkflowArtifactKinds.RaceMenuJslot);
        Add(GoldenTransition.CreateFromJslot, AgentWorkflowPhase.Apply,
            WorkflowArtifactKinds.ReviewedWorkspaceIntake,
            WorkflowArtifactKinds.NpcBuildPreflight,
            WorkflowArtifactKinds.RaceMenuJslot);
        Add(GoldenTransition.Preview, AgentWorkflowPhase.Verify,
            WorkflowArtifactKinds.ReviewedWorkspaceIntake,
            WorkflowArtifactKinds.NpcPackageManifest);
        Add(GoldenTransition.PackagePreviewReview, AgentWorkflowPhase.Review,
            WorkflowArtifactKinds.NpcPackageManifest,
            WorkflowArtifactKinds.NpcPreviewManifest);
        Add(GoldenTransition.FinishAnalyze, AgentWorkflowPhase.Analyze,
            WorkflowArtifactKinds.NpcPackageManifest,
            WorkflowArtifactKinds.NpcPreviewManifest,
            WorkflowArtifactKinds.ReviewReceipt);
        Add(GoldenTransition.FinishProposalReview, AgentWorkflowPhase.Review,
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            WorkflowArtifactKinds.NpcPreviewManifest);
        Add(GoldenTransition.FinishApply, AgentWorkflowPhase.Apply,
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            WorkflowArtifactKinds.NpcPreviewManifest,
            WorkflowArtifactKinds.ReviewReceipt);
        Add(GoldenTransition.FinishVerify, AgentWorkflowPhase.Verify,
            WorkflowArtifactKinds.NpcFinishCoreManifest);
        Add(GoldenTransition.RuntimeWithoutReport, AgentWorkflowPhase.RuntimeAcceptance,
            WorkflowArtifactKinds.NpcFinishCoreVerification,
            WorkflowArtifactKinds.PackageArchive);
        Add(GoldenTransition.RuntimeWithGenericReport, AgentWorkflowPhase.RuntimeAcceptance,
            WorkflowArtifactKinds.NpcFinishCoreVerification,
            WorkflowArtifactKinds.PackageArchive,
            WorkflowArtifactKinds.RuntimeEvidence);
        AddUnreviewedFinishTransitions(table);
        return table.ToImmutable();

        void Add(
            GoldenTransition transition,
            AgentWorkflowPhase phase,
            params string[] artifactKinds) =>
            table.Add(Signature(artifactKinds), new TransitionDefinition(phase, transition));
    }

    private static WorkflowEvaluation EvaluateTransition(
        AgentWorkflowBundle bundle,
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts,
        TransitionDefinition definition)
    {
        if (definition.Phase == AgentWorkflowPhase.ReviewRequired)
            return EvaluateUnreviewedFinish(artifacts, definition.Transition);
        ProtocolNextAction? action = definition.Transition switch
        {
            GoldenTransition.Discover => Action(
                "workspace preflight",
                "Persist one reviewed copied-workspace intake before NPC authoring.",
                [],
                [
                    "--game",
                    "--workspace-root",
                    "--data-root",
                    "--output-root",
                    "--load-order",
                    "--intake-output",
                    "--npc-editor-id",
                    "--workflow-output"
                ],
                false),
            GoldenTransition.PresetInspect => Action(
                "preset inspect",
                "Inspect an explicitly supplied RaceMenu JSlot.",
                [
                    LiteralBinding("--format", "racemenu-jslot"),
                    LiteralBinding("--edition", "skyrimse")
                ],
                [
                    "--input", "--input-sha256", "--inspection-output",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"
                ],
                false),
            GoldenTransition.CreateFromJslotPreflight => Action(
                "npc create-from-jslot",
                "Preflight the exact admitted JSlot through the real NPC build gate.",
                [
                    ArtifactBinding("--preset", artifacts[WorkflowArtifactKinds.RaceMenuJslot]),
                    HashBinding("--preset-sha256", artifacts[WorkflowArtifactKinds.RaceMenuJslot])
                ],
                [
                    "--request", "--request-sha256", "--data-root",
                    "--plugins", "--companion-root", "--preflight-output",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"
                ],
                false),
            GoldenTransition.CreateFromJslot => Action(
                "npc create-from-jslot",
                "Build the exact reviewed JSlot preflight through the existing static NPC pipeline.",
                [
                    ArtifactBinding(
                        "--preset",
                        artifacts[WorkflowArtifactKinds.RaceMenuJslot]),
                    HashBinding(
                        "--preset-sha256",
                        artifacts[WorkflowArtifactKinds.RaceMenuJslot]),
                    ArtifactBinding(
                        "--reviewed-preflight",
                        artifacts[WorkflowArtifactKinds.NpcBuildPreflight]),
                    HashBinding(
                        "--reviewed-preflight-sha256",
                        artifacts[WorkflowArtifactKinds.NpcBuildPreflight])
                ],
                [
                    "--request", "--request-sha256", "--data-root",
                    "--plugins", "--companion-root", "--workflow-bundle",
                    "--workflow-bundle-sha256", "--workflow-output"
                ],
                false),
            GoldenTransition.Preview => PreviewAction(bundle, artifacts),
            GoldenTransition.PackagePreviewReview => Action(
                "gui",
                "Persist an explicit operator review receipt for the exact package and preview evidence.",
                [
                    ArtifactBinding(
                        "--proposal",
                        artifacts[WorkflowArtifactKinds.NpcPackageManifest]),
                    HashBinding(
                        "--proposal-sha256",
                        artifacts[WorkflowArtifactKinds.NpcPackageManifest]),
                    ArtifactBinding(
                        "--preview-manifest",
                        artifacts[WorkflowArtifactKinds.NpcPreviewManifest]),
                    HashBinding(
                        "--preview-manifest-sha256",
                        artifacts[WorkflowArtifactKinds.NpcPreviewManifest])
                ],
                [
                    "--outcome", "--operator-attestation",
                    "--receipt-output", "--workflow-bundle",
                    "--workflow-bundle-sha256", "--workflow-output"
                ],
                true),
            GoldenTransition.FinishAnalyze => Action(
                "npc finish analyze",
                "Analyze the exact Finish Core request after physical review-receipt validation.",
                [
                    ArtifactBinding(
                        "--review-receipt",
                        artifacts[WorkflowArtifactKinds.ReviewReceipt]),
                    HashBinding(
                        "--review-receipt-sha256",
                        artifacts[WorkflowArtifactKinds.ReviewReceipt])
                ],
                [
                    "--request", "--request-sha256", "--proposal",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"
                ],
                false),
            GoldenTransition.FinishProposalReview => Action(
                "gui",
                "Persist an explicit operator review receipt for the exact Finish proposal and preview evidence.",
                [
                    ArtifactBinding(
                        "--proposal",
                        artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
                    HashBinding(
                        "--proposal-sha256",
                        artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
                    ArtifactBinding(
                        "--preview-manifest",
                        artifacts[WorkflowArtifactKinds.NpcPreviewManifest]),
                    HashBinding(
                        "--preview-manifest-sha256",
                        artifacts[WorkflowArtifactKinds.NpcPreviewManifest])
                ],
                [
                    "--outcome", "--operator-attestation",
                    "--receipt-output", "--workflow-bundle",
                    "--workflow-bundle-sha256", "--workflow-output"
                ],
                true),
            GoldenTransition.FinishApply => Action(
                "npc finish apply",
                "Apply only the accepted hash-bound Finish Core proposal.",
                [
                    ArtifactBinding("--request", artifacts[WorkflowArtifactKinds.NpcFinishCoreRequest]),
                    HashBinding("--request-sha256", artifacts[WorkflowArtifactKinds.NpcFinishCoreRequest]),
                    ArtifactBinding("--proposal", artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
                    SemanticHashBinding(
                        "--proposal-sha256",
                        artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
                    ArtifactBinding(
                        "--review-receipt",
                        artifacts[WorkflowArtifactKinds.ReviewReceipt]),
                    HashBinding(
                        "--review-receipt-sha256",
                        artifacts[WorkflowArtifactKinds.ReviewReceipt])
                ],
                [
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"
                ],
                false),
            GoldenTransition.FinishVerify => Action(
                "npc finish verify",
                "Independently verify the exact Finish Core manifest.",
                [
                    ArtifactBinding("--manifest", artifacts[WorkflowArtifactKinds.NpcFinishCoreManifest]),
                    HashBinding("--manifest-sha256", artifacts[WorkflowArtifactKinds.NpcFinishCoreManifest])
                ],
                [
                    "--verification-output", "--workflow-bundle",
                    "--workflow-bundle-sha256", "--workflow-output"
                ],
                false),
            GoldenTransition.RuntimeWithoutReport => Action(
                "runtime smoke verify",
                "Runtime authority requires a typed runtime report and package acceptance.",
                [LiteralBinding("--edition", "skyrimse")],
                ["--runtime-report", "--package-acceptance"],
                true),
            GoldenTransition.RuntimeWithGenericReport => Action(
                "runtime smoke verify",
                "Generic verified bytes can bind the report path but cannot establish game runtime authority.",
                [
                    LiteralBinding("--edition", "skyrimse"),
                    ArtifactBinding("--runtime-report", artifacts[WorkflowArtifactKinds.RuntimeEvidence])
                ],
                ["--package-acceptance"],
                true),
            _ => throw new InvalidOperationException("Undefined golden workflow transition.")
        };

        if (action is null)
            throw new InvalidOperationException("A non-discover transition must define a next action.");

        return action.MissingPrerequisites.IsEmpty
            ? new WorkflowEvaluation(
                WorkflowEvaluationOutcome.Ready,
                definition.Phase,
                Authority(artifacts),
                [action],
                [])
            : new WorkflowEvaluation(
                WorkflowEvaluationOutcome.Blocked,
                definition.Phase,
                Authority(artifacts),
                [action],
                [Diagnostic(
                    "workflow-next-action-prerequisites-missing",
                    "The legal next command is blocked until every required CLI option is supplied.")]);
    }

    private static ProtocolNextAction PreviewAction(
        AgentWorkflowBundle bundle,
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts)
    {
        ImmutableArray<ProtocolNextActionBinding>.Builder bindings =
            ImmutableArray.CreateBuilder<ProtocolNextActionBinding>();
        ImmutableArray<string>.Builder missing = ImmutableArray.CreateBuilder<string>();
        if (bundle.Npc.Plugin is { Length: > 0 } plugin)
            bindings.Add(LiteralBinding("--plugin", plugin));
        else
            missing.Add("--plugin");
        if (bundle.Npc.LocalFormId is { Length: > 0 } localFormId)
            bindings.Add(LiteralBinding("--form", localFormId));
        else
            missing.Add("--form");
        bindings.Add(ArtifactBinding(
            "--package-manifest",
            artifacts[WorkflowArtifactKinds.NpcPackageManifest]));
        bindings.Add(HashBinding(
            "--expected-package-sha256",
            artifacts[WorkflowArtifactKinds.NpcPackageManifest]));
        bindings.Add(ArtifactBinding(
            "--intake",
            artifacts[WorkflowArtifactKinds.ReviewedWorkspaceIntake]));
        missing.Add("--output-root");
        return Action(
            "preview npc",
            "Produce an off-engine preview without claiming visual or runtime acceptance.",
            bindings.ToImmutable(),
            missing.ToImmutable(),
            false);
    }

    private static ProtocolDiagnostic? ValidateReceipt(
        WorkflowEvaluationInput input,
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts)
    {
        bool hasReceiptArtifact = artifacts.ContainsKey(WorkflowArtifactKinds.ReviewReceipt);
        bool hasReceiptInput = input.VerifiedReviewReceipt is not null ||
                               input.VerifiedReviewReceiptSha256 is not null ||
                               input.VerifiedBundleSha256 is not null;
        if (!hasReceiptArtifact && hasReceiptInput)
            return Diagnostic("workflow-input-invalid",
                "Review receipt inputs are meaningful only with a verified receipt artifact.");
        if (!hasReceiptArtifact)
            return null;

        if (input.VerifiedReviewReceipt is null ||
            input.VerifiedReviewReceiptSha256 is null ||
            input.VerifiedBundleSha256 is null)
            return Diagnostic("workflow-input-invalid",
                "The parsed receipt, verified receipt SHA-256, and verified bundle SHA-256 are required together.");

        try
        {
            AgentWorkflowContractValidation.Validate(input.VerifiedReviewReceipt);
        }
        catch (ArgumentException exception)
        {
            return Diagnostic("workflow-input-invalid", exception.Message);
        }

        string proposalKind = artifacts.ContainsKey(
            WorkflowArtifactKinds.NpcFinishCoreProposal)
            ? WorkflowArtifactKinds.NpcFinishCoreProposal
            : WorkflowArtifactKinds.NpcPackageManifest;
        if (!AgentReviewContract.TryGetPurpose(
                proposalKind,
                out string expectedScope,
                out string expectedNoticeSha256) ||
            !string.Equals(
                input.VerifiedReviewReceipt.Scope,
                expectedScope,
                StringComparison.Ordinal) ||
            !string.Equals(
                input.VerifiedReviewReceipt.AuthorityNoticeSha256,
                expectedNoticeSha256,
                StringComparison.Ordinal))
            return Diagnostic(
                "workflow-review-contract-drift",
                "The review receipt does not use the exact lifecycle scope and authority notice.");

        if (input.VerifiedReviewReceiptSha256 !=
            artifacts[WorkflowArtifactKinds.ReviewReceipt].Sha256)
            return Diagnostic("workflow-review-artifact-binding-stale",
                "The parsed review receipt does not bind the independently verified physical receipt artifact.");

        if (input.VerifiedReviewReceipt.BundleSha256 != input.VerifiedBundleSha256)
            return Diagnostic("workflow-review-binding-stale",
                "The review receipt does not bind the verified workflow bundle.");

        string? currentProposal = artifacts.TryGetValue(
            proposalKind,
            out WorkflowArtifactBinding? proposal)
            ? proposal.Sha256
            : null;
        ImmutableArray<string> expectedDisplayed = currentProposal is not null &&
            artifacts.TryGetValue(
                WorkflowArtifactKinds.NpcPreviewManifest,
                out WorkflowArtifactBinding? preview)
            ? new[] { currentProposal, preview.Sha256 }
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray()
            : [];
        if (currentProposal is null ||
            input.VerifiedReviewReceipt.ProposalSha256 != currentProposal ||
            !input.VerifiedReviewReceipt.DisplayedArtifactHashes.SequenceEqual(
                expectedDisplayed,
                StringComparer.Ordinal))
            return Diagnostic("workflow-review-binding-stale",
                "The review receipt does not bind the current proposal and displayed preview artifacts.");

        return input.VerifiedReviewReceipt.Outcome == ReviewOutcome.Accepted
            ? null
            : Diagnostic("workflow-review-not-accepted",
                "Rejected or revision-requested review cannot authorize an apply transition.");
    }

    private static ImmutableArray<WorkflowAuthorityEvidence> Authority(
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts)
    {
        WorkflowAuthorityEvidence Evidence(
            AgentAuthorityKind kind,
            AgentAuthorityState state,
            string reason,
            params string[] artifactKinds) =>
            new(kind, state, reason, artifactKinds
                .Where(artifacts.ContainsKey)
                .Select(kindName => artifacts[kindName].Sha256)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray());

        return
        [
            Evidence(AgentAuthorityKind.InputAdmission,
                State(
                    WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                    WorkflowArtifactKinds.NpcPackageManifest),
                "Reviewed intake or its exact verified package lineage establishes input admission.",
                WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                WorkflowArtifactKinds.NpcPackageManifest),
            Evidence(AgentAuthorityKind.SourceProviderIdentity,
                State(
                    WorkflowArtifactKinds.NpcBuildPreflight,
                    WorkflowArtifactKinds.NpcPackageManifest),
                "Reviewed preflight or its exact verified package lineage binds source-provider identity.",
                WorkflowArtifactKinds.NpcBuildPreflight,
                WorkflowArtifactKinds.NpcPackageManifest),
            Evidence(AgentAuthorityKind.DeterministicMaterialization,
                artifacts.ContainsKey(WorkflowArtifactKinds.NpcPackageManifest) ||
                artifacts.ContainsKey(WorkflowArtifactKinds.NpcFinishCoreManifest)
                    ? AgentAuthorityState.Established : AgentAuthorityState.Required,
                "Only hash-bound package or Finish Core manifests establish deterministic materialization.",
                WorkflowArtifactKinds.NpcPackageManifest,
                WorkflowArtifactKinds.NpcFinishCoreManifest),
            Evidence(AgentAuthorityKind.IndependentStaticVerification,
                State(
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcFinishCoreVerification),
                "A retained-read package or Finish verification artifact establishes independent static verification.",
                WorkflowArtifactKinds.NpcPackageManifest,
                WorkflowArtifactKinds.NpcFinishCoreVerification),
            Evidence(AgentAuthorityKind.OffEnginePreview,
                State(WorkflowArtifactKinds.NpcPreviewManifest),
                "Preview production is off-engine and is not human visual acceptance.",
                WorkflowArtifactKinds.NpcPreviewManifest),
            Evidence(AgentAuthorityKind.HumanVisualAcceptance,
                AgentAuthorityState.Required,
                "Operator-attested review does not establish human visual acceptance."),
            Evidence(AgentAuthorityKind.GameRuntimeVerification,
                AgentAuthorityState.Required,
                "Generic runtime bytes do not establish structurally verified game runtime evidence.",
                WorkflowArtifactKinds.RuntimeEvidence),
            Evidence(AgentAuthorityKind.PromotionApproval,
                AgentAuthorityState.Required,
                "Workflow artifacts and review receipts never grant promotion approval.")
        ];

        AgentAuthorityState State(params string[] kinds) =>
            kinds.Any(artifacts.ContainsKey)
                ? AgentAuthorityState.Established
                : AgentAuthorityState.Required;
    }

    private static ProtocolNextAction Action(
        string command,
        string reason,
        ImmutableArray<ProtocolNextActionBinding> bindings,
        ImmutableArray<string> missing,
        bool human) => new(command, reason, bindings, missing, human);

    private static ProtocolNextActionBinding LiteralBinding(string option, string value) =>
        new(option, value, null);

    private static ProtocolNextActionBinding ArtifactBinding(
        string option,
        WorkflowArtifactBinding artifact) =>
        new(option, artifact.Path.Value, artifact.Sha256);

    private static ProtocolNextActionBinding HashBinding(
        string option,
        WorkflowArtifactBinding artifact) =>
        new(option, artifact.Sha256, artifact.Sha256);

    private static ProtocolNextActionBinding SemanticHashBinding(
        string option,
        WorkflowArtifactBinding artifact) =>
        new(
            option,
            artifact.SemanticSha256 ?? throw new InvalidOperationException(
                "A Finish Core proposal transition requires its semantic SHA-256."),
            artifact.Sha256);

    private static string Signature(IEnumerable<string> artifactKinds) =>
        string.Join("\n", artifactKinds.Order(StringComparer.Ordinal));

    private static WorkflowEvaluation InvalidTransition(
        AgentWorkflowPhase phase,
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts) =>
        Blocked(
            phase,
            artifacts,
            Diagnostic(
                "workflow-transition-invalid",
                "The exact artifact combination does not match the closed golden workflow transition table."));

    private static WorkflowEvaluation Blocked(
        AgentWorkflowPhase phase,
        string code,
        string message) => Blocked(phase, Diagnostic(code, message));

    private static WorkflowEvaluation Blocked(
        AgentWorkflowPhase phase,
        ProtocolDiagnostic diagnostic) => new(
            WorkflowEvaluationOutcome.Blocked,
            phase,
            [],
            [],
            [diagnostic]);

    private static WorkflowEvaluation Blocked(
        AgentWorkflowPhase phase,
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts,
        ProtocolDiagnostic diagnostic) => new(
            WorkflowEvaluationOutcome.Blocked,
            phase,
            Authority(artifacts),
            [],
            [diagnostic]);

    private static ProtocolDiagnostic Diagnostic(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message, DiagnosticClass.Validation,
            new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                "Supply one structurally valid, independently verified artifact graph.",
                false));
}
