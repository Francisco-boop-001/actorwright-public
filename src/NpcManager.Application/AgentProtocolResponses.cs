using System.Collections.Immutable;

namespace NpcManager.Application;

public enum ProtocolOutcome
{
    Succeeded,
    Refused,
    Failed,
    Cancelled
}

public sealed record ProtocolArtifact(
    string Kind,
    string SchemaOrMediaType,
    string Path,
    long? Size,
    string? Sha256,
    string ProducerCommand,
    string RequestDigest,
    ImmutableArray<string> InputBindings,
    string State);

public sealed record ProtocolAuthority(
    AgentAuthorityKind Kind,
    AgentAuthorityState State,
    string Reason);

public sealed record ProtocolNextActionBinding(
    string Option,
    string Value,
    string? ArtifactSha256);

public sealed record ProtocolNextAction(
    string Command,
    string Reason,
    ImmutableArray<ProtocolNextActionBinding> RequiredBindings,
    ImmutableArray<string> MissingPrerequisites,
    bool RequiresHumanAction);
