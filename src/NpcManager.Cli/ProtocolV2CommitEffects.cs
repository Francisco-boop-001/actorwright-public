using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Cli;

internal static class ProtocolV2CommitEffects
{
    internal static ImmutableArray<ProtocolEffect>
        ReadCompletedWriteRefused() =>
    [
        ProtocolEffect.Create(
            AgentEffectKind.ReadWorkspace,
            ApplicationEffectStatus.Completed,
            ApplicationEffectScope.Workspace),
        ProtocolEffect.Create(
            AgentEffectKind.WriteNewArtifact,
            ApplicationEffectStatus.Refused,
            ApplicationEffectScope.KLocalOutput)
    ];

    internal static ImmutableArray<ProtocolEffect>
        ReadCompletedWriteFailed() =>
    [
        ProtocolEffect.Create(
            AgentEffectKind.ReadWorkspace,
            ApplicationEffectStatus.Completed,
            ApplicationEffectScope.Workspace),
        ProtocolEffect.Create(
            AgentEffectKind.WriteNewArtifact,
            ApplicationEffectStatus.Failed,
            ApplicationEffectScope.KLocalOutput)
    ];

    internal static ImmutableArray<ProtocolEffect>
        PublishedArtifactWorkflowFailed() =>
    [
        ProtocolEffect.Create(
            AgentEffectKind.ReadWorkspace,
            ApplicationEffectStatus.Completed,
            ApplicationEffectScope.Workspace),
        ProtocolEffect.Create(
            AgentEffectKind.WriteNewArtifact,
            ApplicationEffectStatus.Completed,
            ApplicationEffectScope.KLocalOutput),
        ProtocolEffect.Create(
            AgentEffectKind.WriteNewArtifact,
            ApplicationEffectStatus.Failed,
            ApplicationEffectScope.KLocalOutput)
    ];
}
