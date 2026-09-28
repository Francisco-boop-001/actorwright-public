using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimNpcFinishCoreBodyRoute
{
    Cbbe3Ba,
    Cotr,
    Ube
}

public enum SkyrimNpcFinishCoreOutfitPolicy
{
    ExistingOutfit,
    PrivateOutfit
}

public enum SkyrimNpcFinishCoreInventoryPolicy
{
    PreserveInventory,
    ReplaceExactInventory
}

public enum SkyrimNpcFinishCoreAggression
{
    Unaggressive,
    Aggressive,
    VeryAggressive,
    Frenzied
}

public enum SkyrimNpcFinishCoreConfidence
{
    Cowardly,
    Cautious,
    Average,
    Brave,
    Foolhardy
}

public enum SkyrimNpcFinishCoreMorality
{
    AnyCrime,
    ViolenceAgainstEnemies,
    PropertyCrimeOnly,
    NoCrime
}

public enum SkyrimNpcFinishCoreAssistance
{
    HelpsNobody,
    HelpsAllies,
    HelpsFriendsAndAllies
}

public enum SkyrimNpcFinishCoreMood
{
    Neutral,
    Angry,
    Fear,
    Happy,
    Sad,
    Surprise,
    Puzzled,
    Disgusted
}

public enum SkyrimNpcFinishCoreOutfitRacePolicy { Refuse, Clone }

public enum SkyrimNpcFinishCoreCombatProfile
{
    Defensive,
    RangedFirst,
    MeleeFirst
}

public sealed record SkyrimNpcFinishCoreCombatPolicy
{
    public bool SeedLocalStyle { get; init; }
    public SkyrimNpcFinishCoreCombatProfile? Profile { get; init; }
}

public sealed record SkyrimNpcFinishCorePerk(FormReference Form, byte Rank);

public enum SkyrimNpcFinishCoreStatus
{
    ReadyForReviewedWrite,
    NoChanges,
    Refused,
    StaticPassRuntimeRequired,
    StaticPassInstallDependencyRequired
}

public sealed record SkyrimNpcFinishCoreSource
{
    public WorkspacePath? PackageRoot { get; init; }

    public WorkspacePath? PackageManifest { get; init; }

    public Sha256Hash? PackageManifestSha256 { get; init; }

    public Sha256Hash? PackageTreeSha256 { get; init; }

    public WorkspacePath? PluginPath { get; init; }

    public PluginName? Plugin { get; init; }

    public Sha256Hash? PluginSha256 { get; init; }
}

public sealed record SkyrimNpcFinishCoreActor
{
    public EditorId? EditorId { get; init; }

    public FormId? FormId { get; init; }
}

public sealed record SkyrimNpcFinishCoreProviderAuthority
{
    public PluginName? Plugin { get; init; }

    public WorkspacePath? Path { get; init; }

    public Sha256Hash? Sha256 { get; init; }

    public long ByteLength { get; init; }
}

public sealed record SkyrimNpcFinishCoreAdditionalMasterBinding(
    PluginName Plugin,
    WorkspacePath Path,
    Sha256Hash Sha256,
    long ByteLength,
    int LoadOrderIndex);

public sealed record SkyrimNpcFinishCoreVerifiedAdditionalMaster(
    PluginName Plugin,
    WorkspacePath Path,
    Sha256Hash Sha256,
    long ByteLength,
    int LoadOrderIndex,
    ImmutableArray<PluginName> MasterDependencies);

public sealed record SkyrimNpcFinishCoreAuthorityReadResult(
    bool Admitted,
    ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster> Masters,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimNpcFinishCoreMasterPlanRequest(
    PluginName SourcePlugin,
    PluginName OutputPlugin,
    ImmutableArray<PluginName> SourceMasters,
    ImmutableArray<PluginName> RequiredReferenceOwners,
    ImmutableArray<SkyrimNpcFinishCoreVerifiedAdditionalMaster> AdditionalMasters);

public sealed record SkyrimNpcFinishCoreMasterPlan(
    bool Admitted,
    ImmutableArray<PluginName> MasterOrder,
    ImmutableArray<PluginName> AppendedMasters,
    ImmutableArray<Diagnostic> Diagnostics)
{
    /// <summary>
    /// The immutable TES4 source-master prefix preserved in its original order.
    /// Its implicit indexes are zero through <c>Length - 1</c> because the
    /// planning request carries source names, not separate prefix rows.
    /// </summary>
    public ImmutableArray<PluginName> SourceMasterPrefix { get; init; } =
        ImmutableArray<PluginName>.Empty;

    /// <summary>
    /// Final load-order indexes aligned positionally with <see cref="MasterOrder"/>.
    /// Source-prefix positions use implicit indexes zero through
    /// <c>SourceMasterPrefix.Length - 1</c>; physically verified appended rows
    /// retain their admitted global indexes, including valid gaps.
    /// </summary>
    public ImmutableArray<int> MasterLoadOrderIndexes { get; init; } =
        ImmutableArray<int>.Empty;
}

public sealed record SkyrimNpcFinishCoreAuthorities
{
    public SkyrimNpcFinishCoreBodyRoute BodyRoute { get; init; }

    public ImmutableArray<SkyrimNpcFinishCoreProviderAuthority> Providers
    {
        get;
        init;
    } = ImmutableArray<SkyrimNpcFinishCoreProviderAuthority>.Empty;

    public ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding> AdditionalMasters
    {
        get;
        init;
    } = ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty;

    public Sha256Hash? ActorAssemblySha256 { get; init; }

    public Sha256Hash? BodyOwnerSha256 { get; init; }

    public Sha256Hash? ProtectedAppearanceTreeSha256 { get; init; }

    /// <summary>
    /// Portable authority for an intentionally external SMP head-part
    /// dependency.  This is deliberately separate from package-contained
    /// <see cref="Providers"/> rows.
    /// </summary>
    public SkyrimNpcFinishCoreExternalHeadPartAuthority? ExternalHeadParts
    {
        get;
        init;
    }
}

public sealed record SkyrimNpcFinishCoreFollowerPolicy
{
    public bool Recruitable { get; init; } = true;

    public bool DefensiveOnly { get; init; } = true;

    public FormReference? PotentialFollowerFaction { get; init; }

    public FormReference? CurrentFollowerFaction { get; init; }

    public string RelationshipRank { get; init; } = "Ally";
}

public sealed record SkyrimNpcFinishCoreAiPolicy
{
    public SkyrimNpcFinishCoreAggression Aggression { get; init; }

    public SkyrimNpcFinishCoreConfidence Confidence { get; init; }

    public byte Energy { get; init; }

    public SkyrimNpcFinishCoreMorality Morality { get; init; }

    public SkyrimNpcFinishCoreAssistance Assistance { get; init; }

    public SkyrimNpcFinishCoreMood? Mood { get; init; }
}

public sealed record SkyrimNpcFinishCoreOutfitPolicyDocument
{
    public SkyrimNpcFinishCoreOutfitPolicy Policy { get; init; }

    public FormReference? ExistingOutfit { get; init; }

    public ImmutableArray<FormReference> ArmorItems { get; init; } =
        ImmutableArray<FormReference>.Empty;
}

public sealed record SkyrimNpcFinishCoreInventoryPolicyDocument
{
    public SkyrimNpcFinishCoreInventoryPolicy Policy { get; init; }

    public ImmutableArray<string> ExpectedSourceItems { get; init; } =
        ImmutableArray<string>.Empty;

    public ImmutableArray<string> DesiredItems { get; init; } =
        ImmutableArray<string>.Empty;
}

public sealed record SkyrimNpcFinishCoreSandboxAuthority
{
    public WorkspacePath? CopiedMaster { get; init; }

    public Sha256Hash? CopiedMasterSha256 { get; init; }

    public FormReference Template { get; init; }

    public string TemplateEditorId { get; init; } = string.Empty;

    public Sha256Hash? RawRecordDigest { get; init; }
}

public sealed record SkyrimNpcFinishCoreOutput
{
    public WorkspacePath? Root { get; init; }

    public WorkspacePath? Archive { get; init; }

    public string PluginFileName { get; init; } = string.Empty;
}

public sealed record SkyrimNpcFinishCoreRequest
{
    public const string SchemaIdentifier = "npc.finish-core.request.v2";

    public const string LegacySchemaIdentifier = "npc.finish-core.request.v1";

    public const string ExternalSchemaIdentifier = "npc.finish-core.request.v3";

    public const string PolicySchemaIdentifier = "npc.finish-core.request.v4";

    public string Schema { get; init; } = SchemaIdentifier;

    public SkyrimNpcFinishCoreSource Source { get; init; } = new();

    public SkyrimNpcFinishCoreActor Actor { get; init; } = new();

    public SkyrimNpcFinishCoreAuthorities Authorities { get; init; } = new();

    public SkyrimNpcFinishCoreFollowerPolicy FollowerPolicy { get; init; } = new();

    public SkyrimNpcFinishCoreAiPolicy? AiPolicy { get; init; }

    public SkyrimNpcFinishCoreCombatPolicy? CombatPolicy { get; init; }

    // Default means omitted/preserve; an explicit empty array clears PRKR.
    public ImmutableArray<SkyrimNpcFinishCorePerk> PerkPolicy { get; init; }

    public SkyrimNpcFinishCoreOutfitPolicyDocument OutfitPolicy { get; init; } = new();

    public SkyrimNpcFinishCoreOutfitRacePolicy OutfitRacePolicy { get; init; }

    public SkyrimNpcFinishCoreInventoryPolicyDocument InventoryPolicy { get; init; } = new();

    public SkyrimNpcFinishCoreSandboxAuthority SandboxAuthority { get; init; } = new();

    public SkyrimNpcFinishCoreOutput Output { get; init; } = new();
}

public sealed record SkyrimNpcFinishCoreProposal
{
    public const string SchemaIdentifier = "npc.finish-core.proposal.v2";

    public const string LegacySchemaIdentifier = "npc.finish-core.proposal.v1";

    public const string ExternalSchemaIdentifier = "npc.finish-core.proposal.v3";

    public const string PolicySchemaIdentifier = "npc.finish-core.proposal.v4";

    public string Schema { get; init; } = SchemaIdentifier;

    public Sha256Hash? RequestSha256 { get; init; }

    public Sha256Hash? ProposalSha256 { get; init; }

    public SkyrimNpcFinishCoreRequest? Request { get; init; }

    public SkyrimNpcFinishCoreStatus Status { get; init; }

    public ImmutableArray<string> ExistingRecordChanges { get; init; } =
        ImmutableArray<string>.Empty;

    public ImmutableArray<string> NewRecords { get; init; } =
        ImmutableArray<string>.Empty;

    public ImmutableArray<string> AppendedMasters { get; init; } =
        ImmutableArray<string>.Empty;

    public ImmutableArray<string> ForbiddenRecordCounts { get; init; } =
        ImmutableArray<string>.Empty;

    public FormId NextFormId { get; init; }

    public uint SourceTes4Flags { get; init; }

    public ImmutableArray<string> MasterOrder { get; init; } =
        ImmutableArray<string>.Empty;

    public ImmutableArray<string> PackageFiles { get; init; } =
        ImmutableArray<string>.Empty;

    public bool RuntimeAuthority { get; init; }

    public SkyrimNpcFinishCoreExternalHeadPartProposalAuthority?
        ExternalHeadParts { get; init; }
}

public sealed record SkyrimNpcFinishCoreManifest
{
    public const string SchemaIdentifier = "npc.finish-core.manifest.v1";

    public const string ExternalSchemaIdentifier = "npc.finish-core.manifest.v2";

    public string Schema { get; init; } = SchemaIdentifier;

    public PluginName? Plugin { get; init; }

    public Sha256Hash? PluginSha256 { get; init; }

    public FormReference? BaseNpc { get; init; }

    public Sha256Hash? RequestSha256 { get; init; }

    public Sha256Hash? ProposalSha256 { get; init; }

    public bool PlacementIncluded { get; init; }

    public bool RuntimeAuthority { get; init; }

    public bool VisualAuthority { get; init; }

    public WorkspacePath? PackageRoot { get; init; }

    public WorkspacePath? Archive { get; init; }

    public Sha256Hash? ArchiveSha256 { get; init; }

    public Sha256Hash? SourcePackageTreeSha256 { get; init; }

    public Sha256Hash? PackageTreeSha256 { get; init; }

    public SkyrimNpcFinishCoreRuntimeIdentity RuntimeIdentity { get; init; } = new();

    public SkyrimNpcFinishCoreManifestEvidence Evidence { get; init; } = new();

    public SkyrimNpcFinishCoreExternalHeadPartManifestAuthority?
        ExternalHeadParts { get; init; }
}

public sealed record SkyrimNpcFinishCoreVerification
{
    public const string SchemaIdentifier = "npc.finish-core.verification.v1";

    public const string ExternalSchemaIdentifier = "npc.finish-core.verification.v2";

    public string Schema { get; init; } = SchemaIdentifier;

    public SkyrimNpcFinishCoreStatus Status { get; init; }

    public bool Verified { get; init; }

    public bool PlacementIncluded { get; init; }

    public bool RuntimeAuthority { get; init; }

    public bool VisualAuthority { get; init; }

    public ImmutableDictionary<string, int> TypedForbiddenCounts { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    public ImmutableDictionary<string, int> RawForbiddenCounts { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    public Sha256Hash? PluginSha256 { get; init; }

    public Sha256Hash? PackageTreeSha256 { get; init; }

    public Sha256Hash? SourcePackageTreeSha256 { get; init; }

    public Sha256Hash? ArchiveSha256 { get; init; }

    public SkyrimNpcFinishCoreRuntimeIdentity RuntimeIdentity { get; init; } = new();

    public SkyrimNpcFinishCoreExternalHeadPartVerification?
        ExternalHeadParts { get; init; }
}

public sealed record SkyrimNpcFinishCoreProposalResult(
    bool Proposed,
    SkyrimNpcFinishCoreProposal? Proposal,
    WorkspacePath? ProposalPath,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimNpcFinishCoreApplyResult(
    bool Applied,
    SkyrimNpcFinishCoreManifest? Manifest,
    WorkspacePath? OutputRoot,
    WorkspacePath? Archive,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimNpcFinishCoreVerificationResult(
    bool Verified,
    SkyrimNpcFinishCoreVerification? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public enum SkyrimNpcFinishCoreValidationPhaseState
{
    Reached,
    Skipped
}

public sealed record SkyrimNpcFinishCoreValidationPhase(
    string Name,
    SkyrimNpcFinishCoreValidationPhaseState State,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimNpcFinishCoreValidationResult(
    bool Valid,
    ImmutableArray<SkyrimNpcFinishCoreValidationPhase> Phases,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public const string SchemaIdentifier = "npc.finish-core.validation.v1";
}

public interface ISkyrimNpcFinishCoreService
{
    ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken);

    ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken);

    ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken);

    ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken);

    ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken);
}

/// <summary>
/// Contextful Finish Core operations are a separate surface so ephemeral
/// install context can never be reconstructed from a portable document.
/// </summary>
public interface ISkyrimNpcFinishCoreInstallContextService :
    ISkyrimNpcFinishCoreService
{
    ValueTask<SkyrimNpcFinishCoreProposalResult>
        AnalyzeWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken);

    ValueTask<SkyrimNpcFinishCoreApplyResult>
        ApplyWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken);

    ValueTask<SkyrimNpcFinishCoreVerificationResult>
        VerifyWithInstallContextAsync(
            WorkspacePath manifestPath,
            Sha256Hash manifestSha256,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken);
}
