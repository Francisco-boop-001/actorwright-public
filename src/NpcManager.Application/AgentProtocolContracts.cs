using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ProtocolReadiness { Legacy, V2 }
public enum AgentContractStatus { Incomplete, Complete }
#pragma warning disable CA1720 // Exact protocol value-kind names are wire contract.
public enum AgentValueKind { Boolean, String, Integer, Enum, Path, Sha256, Identifier, Json, ArtifactReference, EnumList }
#pragma warning restore CA1720
public enum AgentOptionRelationshipKind { RequiredWhen, RequiresTogether, ForbiddenWhen, AtLeastOne }
public enum AgentPredicateCombination { All, Any }
public enum AgentPredicateSource { OptionValue, InputArtifactSchema }
public enum AgentPredicateMatch { AnyOf, NoneOf }
public enum AgentEffectKind { ReadWorkspace, WriteNewArtifact, InvokeAdmittedProcess, DeployToCopiedData, LaunchDesktop, AppendLocalOperationJournal }
public enum AgentRetryPolicy { SafeUnchanged, RequiresFreshOutput, RequiresReanalysis, RequiresHumanAction, NotRetryable }
public enum AgentDeterminism { Deterministic, PinnedInputsAndTools, EnvironmentDependent }
public enum AgentAuthorityKind { InputAdmission, SourceProviderIdentity, DeterministicMaterialization, IndependentStaticVerification, OffEnginePreview, HumanVisualAcceptance, GameRuntimeVerification, PromotionApproval }
public enum AgentAuthorityState { Established, Required, Blocked, NotApplicable }
public enum AgentWorkflowPhase { Discover, Preflight, Analyze, Propose, Review, Apply, Verify, Package, Deploy, RuntimeAcceptance,
    [JsonStringEnumMemberName("review-required")] ReviewRequired }

public sealed record AgentOptionContract(
    string CliName,
    string JsonName,
    AgentValueKind ValueKind,
    bool Required,
    ImmutableArray<string> AllowedValues,
    ImmutableArray<string> ConflictsWith,
    bool SecretLike)
{
    public string ValueSyntax { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string? AliasFor { get; init; }
}

public sealed record AgentOptionRelationshipContract(
    AgentOptionRelationshipKind Kind,
    ImmutableArray<string> Options,
    ImmutableArray<string> ReferencedOptions,
    string Condition)
{
    public AgentOptionRelationshipTrigger? Trigger { get; init; }
}

public sealed record AgentOptionRelationshipTrigger(
    AgentPredicateCombination Operator,
    ImmutableArray<AgentRelationshipPredicate> Predicates);

public sealed record AgentRelationshipPredicate(
    AgentPredicateSource Source,
    string Subject,
    AgentPredicateMatch Match,
    ImmutableArray<string> Values,
    bool MatchesWhenAbsent);

public sealed record AgentArtifactContract(
    string Kind,
    ImmutableArray<string> SchemaIds,
    string Description)
{
    public AgentOptionRelationshipTrigger? Trigger { get; init; }
}

public sealed record AgentEffectContract(
    AgentEffectKind Kind,
    string Condition,
    string Scope)
{
    public AgentOptionRelationshipTrigger? Trigger { get; init; }

    [JsonIgnore]
    public ImmutableArray<ApplicationEffectScope> AllowedResultScopes
    {
        get;
        init;
    } = [];
}

public sealed record AgentAuthorityContract(
    AgentAuthorityKind Kind,
    AgentAuthorityState State,
    string Reason);

public sealed record AgentTransitionContract(
    AgentWorkflowPhase From,
    AgentWorkflowPhase To,
    ImmutableArray<string> RequiredBindings);

public sealed record AgentCommandContract(
    string Name,
    ImmutableArray<string> Aliases,
    string CommandSchemaVersion,
    ProtocolReadiness Readiness,
    ImmutableArray<GameEdition> SupportedGames,
    string Purpose,
    ImmutableArray<string> Limitations,
    ImmutableArray<AgentOptionContract> Options,
    ImmutableArray<string> InputArtifactKinds,
    ImmutableArray<string> ResultSchemaIds,
    ImmutableArray<AgentEffectContract> Effects,
    AgentRetryPolicy RetryPolicy,
    AgentDeterminism Determinism,
    bool SupportsDryRun,
    ImmutableArray<AgentAuthorityContract> Authority,
    ImmutableArray<AgentTransitionContract> Transitions)
{
    public AgentContractStatus ContractStatus { get; init; } =
        AgentContractStatus.Incomplete;

    public string? CanonicalCommand { get; init; }

    public ImmutableArray<AgentOptionRelationshipContract> OptionRelationships
    {
        get;
        init;
    } = [];

    public ImmutableArray<AgentArtifactContract> InputArtifacts { get; init; } = [];

    public ImmutableArray<AgentArtifactContract> OutputArtifacts { get; init; } = [];

    public string ResultShape { get; init; } = "unspecified";

    public string ResultDescription { get; init; } =
        "Result metadata has not yet been populated.";
}
