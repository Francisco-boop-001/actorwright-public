using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class AgentWorkflowService
{
    private static void AddUnreviewedFinishTransitions(
        ImmutableDictionary<string, TransitionDefinition>.Builder table)
    {
        AddReviewed(GoldenTransition.FinishVerify, AgentWorkflowPhase.Verify,
        [
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            WorkflowArtifactKinds.NpcPreviewManifest,
            WorkflowArtifactKinds.NpcFinishCoreManifest
        ]);
        foreach (bool preview in new[] { false, true })
        {
            string[] common = [WorkflowArtifactKinds.NpcPackageManifest,
                WorkflowArtifactKinds.NpcFinishCoreRequest, WorkflowArtifactKinds.NpcFinishCoreProposal,
                .. preview ? new[] { WorkflowArtifactKinds.NpcPreviewManifest } : []];
            Add(GoldenTransition.UnreviewedFinishApply, common);
            Add(GoldenTransition.UnreviewedFinishVerify, [.. common, WorkflowArtifactKinds.NpcFinishCoreManifest]);
            Add(GoldenTransition.UnreviewedFinishReview,
                [.. common, WorkflowArtifactKinds.NpcFinishCoreVerification, WorkflowArtifactKinds.PackageArchive]);
            if (preview)
            {
                AddReviewed(GoldenTransition.FinishApply, AgentWorkflowPhase.Apply, common);
                AddReviewed(GoldenTransition.FinishVerify, AgentWorkflowPhase.Verify,
                    [.. common, WorkflowArtifactKinds.NpcFinishCoreManifest]);
                AddReviewed(GoldenTransition.RuntimeWithoutReport, AgentWorkflowPhase.RuntimeAcceptance,
                    [.. common, WorkflowArtifactKinds.NpcFinishCoreVerification, WorkflowArtifactKinds.PackageArchive]);
            }
        }
        void Add(GoldenTransition transition, string[] kinds) => table.Add(Signature(kinds),
            new TransitionDefinition(AgentWorkflowPhase.ReviewRequired, transition));
        void AddReviewed(GoldenTransition transition, AgentWorkflowPhase phase, string[] kinds) =>
            table.Add(Signature([.. kinds, WorkflowArtifactKinds.ReviewReceipt]), new(phase, transition));
    }

    public static bool IsFinishReviewContinuation(AgentWorkflowBundle bundle, bool reviewed)
    {
        string[] kinds = bundle.Artifacts.Select(artifact => artifact.Kind).ToArray();
        if (!kinds.Contains(WorkflowArtifactKinds.NpcFinishCoreProposal, StringComparer.Ordinal) ||
            !kinds.Contains(WorkflowArtifactKinds.NpcPreviewManifest, StringComparer.Ordinal) ||
            kinds.Contains(WorkflowArtifactKinds.ReviewReceipt, StringComparer.Ordinal) != reviewed ||
            !TransitionTable.TryGetValue(Signature(kinds), out TransitionDefinition? definition))
            return false;
        return reviewed
            ? definition.Transition is GoldenTransition.FinishApply or GoldenTransition.FinishVerify or GoldenTransition.RuntimeWithoutReport
            : bundle.Phase == AgentWorkflowPhase.ReviewRequired && definition.Phase == AgentWorkflowPhase.ReviewRequired;
    }

    private static WorkflowEvaluation EvaluateUnreviewedFinish(
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts, GoldenTransition transition)
    {
        ImmutableArray<ProtocolNextAction>.Builder actions = ImmutableArray.CreateBuilder<ProtocolNextAction>();
        if (transition == GoldenTransition.UnreviewedFinishApply)
            actions.Add(Action("npc finish apply", "Apply the exact static Finish proposal; human visual acceptance remains required.",
                [ArtifactBinding("--request", artifacts[WorkflowArtifactKinds.NpcFinishCoreRequest]),
                 HashBinding("--request-sha256", artifacts[WorkflowArtifactKinds.NpcFinishCoreRequest]),
                 ArtifactBinding("--proposal", artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
                 SemanticHashBinding("--proposal-sha256", artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal])],
                ["--workflow-bundle", "--workflow-bundle-sha256", "--workflow-output"], false));
        else if (transition == GoldenTransition.UnreviewedFinishVerify)
            actions.Add(Action("npc finish verify", "Independently verify static output while retaining the outstanding human review requirement.",
                [ArtifactBinding("--manifest", artifacts[WorkflowArtifactKinds.NpcFinishCoreManifest]),
                 HashBinding("--manifest-sha256", artifacts[WorkflowArtifactKinds.NpcFinishCoreManifest])],
                ["--verification-output", "--workflow-bundle", "--workflow-bundle-sha256", "--workflow-output"], false));
        bool hasPreview = artifacts.TryGetValue(WorkflowArtifactKinds.NpcPreviewManifest, out WorkflowArtifactBinding? preview);
        actions.Add(Action("gui", hasPreview
            ? "Review the exact Finish proposal and bound preview; static Finish results grant no acceptance or promotion."
            : "Obtain a bound preview from the retained source-package checkpoint, then reenter Finish with that bundle and fresh outputs before requesting a receipt; preview flags alone cannot add immutable lineage.",
            [ArtifactBinding("--proposal", artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
             HashBinding("--proposal-sha256", artifacts[WorkflowArtifactKinds.NpcFinishCoreProposal]),
             .. hasPreview ? new[] { ArtifactBinding("--preview-manifest", preview!), HashBinding("--preview-manifest-sha256", preview!) } : []],
            ["--outcome", "--operator-attestation", "--receipt-output", "--workflow-bundle", "--workflow-bundle-sha256", "--workflow-output",
             .. hasPreview ? Array.Empty<string>() : ["--preview-manifest", "--preview-manifest-sha256"]], true));
        return new(WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.ReviewRequired, Authority(artifacts),
            actions.ToImmutable(), [Diagnostic("workflow-human-review-required", "Human visual acceptance is still required before promotion.")]);
    }
}
