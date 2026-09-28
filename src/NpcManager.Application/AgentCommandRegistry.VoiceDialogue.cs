using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static AgentCommandContract VoiceDialogueContract(CommandDescriptor descriptor)
    {
        bool discover = descriptor.Name == "npc voice discover";
        bool import = descriptor.Name == "npc voice import";
        bool synthesize = descriptor.Name == "npc voice synthesize";
        bool analyze = descriptor.Name == "npc dialogue analyze";
        bool apply = descriptor.Name == "npc dialogue apply";
        bool verify = descriptor.Name == "npc dialogue verify";
        ImmutableArray<AgentArtifactContract> inputs = descriptor.Name switch
        {
            "npc voice synthesize" =>
            [
                new("npc-dialogue-manifest", [SkyrimNpcDialogueSchemas.Manifest], "Exact reviewed dialogue manifest."),
                new("npc-voice-sample", [SkyrimNpcVoiceSchemas.Sample], "Exact sample-authority document.")
            ],
            "npc dialogue analyze" =>
            [
                new("npc-dialogue-manifest", [SkyrimNpcDialogueSchemas.Manifest], "Exact reviewed dialogue manifest in normal mode."),
                new("npc-voice-sample", [SkyrimNpcVoiceSchemas.Sample], "Exact sample authority in normal mode.")
            ],
            "npc dialogue apply" =>
            [
                new("npc-dialogue-proposal", [SkyrimNpcDialogueSchemas.Proposal], "Exact reviewed dialogue proposal."),
                new("npc-voice-synthesis", [SkyrimNpcVoiceSchemas.Synthesis], "Exact completed synthesis ledger.")
            ],
            "npc dialogue verify" => [new("npc-dialogue-output-manifest", [SkyrimNpcDialogueSchemas.OutputManifest], "Exact output manifest to reopen independently.")],
            _ => []
        };
        ImmutableArray<AgentArtifactContract> outputs = descriptor.Name switch
        {
            "npc voice discover" => [new("npc-voice-services", [SkyrimNpcVoiceSchemas.Services], "Fresh local service inventory when --output is supplied.")],
            "npc voice import" => [new("npc-voice-sample", [SkyrimNpcVoiceSchemas.Sample], "Fresh sample authority and normalized WAV.")],
            "npc voice synthesize" => [new("npc-voice-synthesis", [SkyrimNpcVoiceSchemas.Synthesis], "Resumable synthesis ledger and per-line WAVs.")],
            "npc dialogue analyze" =>
            [
                new("npc-dialogue-manifest", [SkyrimNpcDialogueSchemas.Manifest], "Fresh draft in template mode."),
                new("npc-dialogue-proposal", [SkyrimNpcDialogueSchemas.Proposal], "Fresh analysis proposal in normal mode.")
            ],
            "npc dialogue apply" => [new("npc-dialogue-output-manifest", [SkyrimNpcDialogueSchemas.OutputManifest], "Fresh package and output manifest.")],
            _ => [new("npc-dialogue-verification", [SkyrimNpcDialogueSchemas.Verification], "Independent verification result.")]
        };
        bool writes = !discover && !verify || descriptor.Name == "npc dialogue analyze";
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new AgentEffectContract(AgentEffectKind.ReadWorkspace, "When admitting explicit inputs or probing loopback service metadata.", "workspace"));
        if (writes || discover) effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when an explicit fresh output is requested and validation succeeds.", "k-local-output"));
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            Options = LegacyOptions(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = outputs,
            ResultShape = "object",
            ResultDescription = "Typed status, exact output bindings, and diagnostics.",
            Effects = effects.ToImmutable(),
            RetryPolicy = apply || import ? AgentRetryPolicy.RequiresFreshOutput : synthesize ? AgentRetryPolicy.SafeUnchanged : AgentRetryPolicy.NotRetryable,
            Determinism = discover || synthesize ? AgentDeterminism.EnvironmentDependent : AgentDeterminism.PinnedInputsAndTools,
            Authority = CompleteAuthority(
                "Explicit K-local inputs, hashes, and loopback endpoints are validated before work.",
                verify ? AgentAuthorityState.Established : discover ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Established,
                verify ? "The output is independently reopened and checked." : discover ? "Discovery does not materialize a game artifact." : "Fresh output remains bound to admitted inputs."),
            Transitions = analyze ? [new AgentTransitionContract(AgentWorkflowPhase.Analyze, AgentWorkflowPhase.Propose, ["manifest-sha256", "plugin-sha256", "sample-authority-sha256"])] :
                apply ? [new AgentTransitionContract(AgentWorkflowPhase.Apply, AgentWorkflowPhase.Verify, ["proposal-sha256", "synthesis-sha256"])] : []
        };
    }
}
