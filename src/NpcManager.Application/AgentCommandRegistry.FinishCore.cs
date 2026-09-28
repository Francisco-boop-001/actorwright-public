using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static AgentCommandContract FinishCoreV2(CommandDescriptor descriptor)
    {
        bool apply = descriptor.Name == "npc finish apply";
        AgentCommandContract legacy = apply ? FinishApplyV1(descriptor) : FinishAnalyze(descriptor);
        AgentOptionRelationshipTrigger nonValidation = NotTruthy("validate-all", "true", "1");
        ImmutableArray<AgentArtifactContract> inputs = [.. legacy.InputArtifacts,
            new("workflow-bundle", [AgentWorkflowSchemas.BundleV1], "Exact predecessor workflow bound by path and physical SHA-256."),
            new(WorkflowArtifactKinds.ReviewReceipt, [AgentWorkflowSchemas.ReviewReceiptV1],
                "Optional exact operator receipt; when supplied it must bind the predecessor and is independently validated.")
                { Trigger = new(AgentPredicateCombination.All, [PresentPredicate("review-receipt")]) }];
        return legacy with
        {
            Readiness = ProtocolReadiness.V2,
            Options = [.. legacy.Options,
                Option("workflow-bundle", AgentValueKind.Path, true),
                Option("workflow-bundle-sha256", AgentValueKind.Sha256, true),
                Option("workflow-output", AgentValueKind.Path, true),
                Option("review-receipt", AgentValueKind.Path, false),
                Option("review-receipt-sha256", AgentValueKind.Sha256, false)],
            OptionRelationships = [RequiresTogether("data-root", "plugins"), RequiresTogether("review-receipt", "review-receipt-sha256")],
            InputArtifacts = inputs, InputArtifactKinds = ArtifactKinds(inputs),
            OutputArtifacts = [.. legacy.OutputArtifacts,
                .. apply ? new[] { new AgentArtifactContract(WorkflowArtifactKinds.PackageArchive, [],
                    "Fresh archive emitted with the applied Finish package.") { Trigger = nonValidation } } : [],
                new AgentArtifactContract("workflow-bundle", [AgentWorkflowSchemas.BundleV1],
                    "Fresh successor; exact receipt-free Finish may undergo static package verification and archive creation while human visual, game runtime, and promotion authority remain unestablished.") { Trigger = nonValidation }],
            ResultSchemaIds = [apply ? AgentProtocolSchemaIds.FinishApplyResult : AgentProtocolSchemaIds.FinishAnalyzeResult],
            ResultDescription = "Typed Finish outcome and optional nonpromoting validation report; artifacts and next actions bind the exact static workflow.",
            Authority = legacy.Authority.Select(item => item.Kind switch
            {
                AgentAuthorityKind.HumanVisualAcceptance => item with { State = AgentAuthorityState.Required },
                AgentAuthorityKind.PromotionApproval => item with { State = AgentAuthorityState.NotApplicable },
                _ => item
            }).ToImmutableArray(),
            Limitations = descriptor.Limitations.Add("Callable without an operator review receipt. An exact receipt-free Finish workflow may undergo static package verification and archive creation. Human visual acceptance, game runtime verification, and promotion approval remain unestablished. Any supplied review receipt is validated against the exact workflow.")
                .Add("A master-owned CSTY seed-required diagnostic offers npc finish analyze recovery: set combatPolicy.seedLocalStyle=true, retain copied master/provider hash authority, rehash the request, and reanalyze. Missing or duplicate CSTY evidence does not authorize that recovery."),
            Transitions = [new AgentTransitionContract(apply ? AgentWorkflowPhase.Apply : AgentWorkflowPhase.Analyze,
                apply ? AgentWorkflowPhase.Verify : AgentWorkflowPhase.Apply, apply
                    ? [WorkflowArtifactKinds.NpcFinishCoreManifest] : [WorkflowArtifactKinds.NpcFinishCoreProposal]),
                new AgentTransitionContract(apply ? AgentWorkflowPhase.Apply : AgentWorkflowPhase.Analyze,
                AgentWorkflowPhase.ReviewRequired, apply
                    ? [WorkflowArtifactKinds.NpcFinishCoreManifest] : [WorkflowArtifactKinds.NpcFinishCoreProposal])]
        };
    }
}
