using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ExternalHeadPartDependencyDisposition
{
    RecordOnlyExternal = 0
}

public enum ExternalHeadPartPhysicsBindingMode
{
    DirectNifExtraData = 0,
    DefaultBbpMap = 1
}

public enum ExternalHeadPartPhysicsBindingOrigin
{
    Direct = 0,
    DefaultBbp = 1,
    InheritedFromRoot = 2
}

public enum ExternalHeadPartPhysicsBindingDisposition
{
    InheritRoot = 0
}

public enum ExternalInstallDependencyState
{
    NotRequired = 0,
    DeclaredUnverified = 1,
    Verified = 2
}

public enum ExternalHeadPartRedistributionMode
{
    ExternalProviderRequired = 0
}

public static class ExternalHeadPartSchemaIdentifiers
{
    public const string Descriptor = "npc.external-headpart-dependency.v1";
    public const string FaceGeomExclusion =
        "npc.external-headpart-facegeom-exclusion.v1";
    public const string InstallVerification =
        "npc.external-install-dependency-verification.v1";
}

public sealed record ExternalHeadPartInstallVerificationContext(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> EnabledPluginOrder,
    FormReference TargetRace);

public sealed record ExternalHeadPartProviderIdentity(
    PluginName Plugin,
    Sha256Hash PluginSha256,
    long PluginByteLength,
    ExternalHeadPartRedistributionMode RedistributionMode);

public sealed record ExternalHeadPartRecordDependency(
    FormReference OriginForm,
    PluginName RequiredOutputMaster,
    FormReference WinningForm,
    PluginName WinningPlugin,
    Sha256Hash WinningPluginSha256,
    long WinningPluginByteLength,
    Sha256Hash WinningRecordSha256,
    string EditorId,
    NpcHeadPartType DeclaredType,
    NpcHeadPartType EffectiveType,
    AssetPath? ModelNif,
    ImmutableArray<SkyrimHdptTriRoute> TriRoutes,
    ImmutableArray<FormReference> HnamEdges,
    FormReference? Parent,
    int Depth,
    int RouteOrder,
    NpcSex? AppliesToSex,
    FormReference? ValidRace);

public enum ExternalHeadPartMemberAuthorityKind
{
    PrimaryProvider = 0,
    OfficialVanilla = 1,
    Unsupported = 2
}

/// <summary>
/// Classifies graph-member ownership once so provider, codec, and physics
/// boundaries apply the same closed authority rule.
/// </summary>
public static class ExternalHeadPartMemberAuthority
{
    private static readonly ImmutableHashSet<string> OfficialVanillaMasters =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "Skyrim.esm",
            "Update.esm",
            "Dawnguard.esm",
            "HearthFires.esm",
            "Dragonborn.esm");

    public static bool IsOfficialVanillaMaster(PluginName plugin) =>
        OfficialVanillaMasters.Contains(plugin.Value);

    public static ExternalHeadPartMemberAuthorityKind Classify(
        ExternalHeadPartRecordDependency member,
        ExternalHeadPartProviderIdentity provider)
    {
        if (IsPrimaryProviderMember(member, provider))
            return ExternalHeadPartMemberAuthorityKind.PrimaryProvider;
        if (IsSelfOwnedVanillaMember(member))
            return ExternalHeadPartMemberAuthorityKind.OfficialVanilla;
        return ExternalHeadPartMemberAuthorityKind.Unsupported;
    }

    public static bool IsPrimaryProviderMember(
        ExternalHeadPartRecordDependency member,
        ExternalHeadPartProviderIdentity provider) =>
        member.OriginForm.Plugin == provider.Plugin &&
        member.RequiredOutputMaster == provider.Plugin &&
        member.WinningForm == member.OriginForm &&
        member.WinningPlugin == provider.Plugin &&
        member.WinningPluginSha256 == provider.PluginSha256 &&
        member.WinningPluginByteLength == provider.PluginByteLength;

    public static bool IsSelfOwnedVanillaMember(
        ExternalHeadPartRecordDependency member) =>
        IsOfficialVanillaMaster(member.OriginForm.Plugin) &&
        member.RequiredOutputMaster == member.OriginForm.Plugin &&
        member.WinningForm == member.OriginForm &&
        member.WinningPlugin == member.OriginForm.Plugin;

    public static bool TryGetChainRoot(FormReference member,
        IReadOnlyDictionary<FormReference, ExternalHeadPartRecordDependency> members,
        out FormReference root)
    {
        root = member;
        var visited = new HashSet<FormReference>();
        while (members.TryGetValue(root, out var current) && visited.Add(root))
        {
            if (current.Parent is not { } parent) return true;
            if (!members.TryGetValue(parent, out var parentMember) ||
                !parentMember.HnamEdges.Contains(root)) return false;
            root = parent;
        }
        return false;
    }
}

public sealed record ExternalHeadPartArchiveMemberAuthority(
    AssetPath ArchivePath,
    Sha256Hash ArchiveSha256,
    long ArchiveByteLength,
    AssetPath MemberPath,
    Sha256Hash MemberSha256,
    long MemberByteLength);

public sealed record ExternalHeadPartAssetDependency(
    AssetPath Path,
    Sha256Hash Sha256,
    long ByteLength,
    PluginName ProviderPlugin,
    Sha256Hash ProviderPluginSha256,
    ExternalHeadPartArchiveMemberAuthority? ArchiveMember);

public sealed record ExternalHeadPartPhysicsShapeBinding(
    FormReference MemberForm,
    AssetPath ModelNif,
    string ShapeName,
    AssetPath XmlPath,
    Sha256Hash XmlSha256,
    long XmlByteLength,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExternalHeadPartPhysicsBindingOrigin? Origin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExternalHeadPartPhysicsBindingRoot? InheritedFromRoot = null);

public sealed record ExternalHeadPartPhysicsBindingRoot(
    FormReference MemberForm,
    AssetPath ModelNif,
    string ShapeName);

public sealed record ExternalHeadPartPhysicsMappingAuthority(
    AssetPath Path,
    Sha256Hash Sha256,
    long ByteLength);

public sealed record ExternalHeadPartPhysicsBinding(
    ExternalHeadPartPhysicsBindingMode Mode,
    ImmutableArray<ExternalHeadPartPhysicsShapeBinding> Shapes,
    ExternalHeadPartPhysicsMappingAuthority? MappingAuthority);

public sealed record ExternalHeadPartRuntimePrerequisite(
    string Kind,
    string Requirement,
    string EvidenceSource);

public sealed record ExternalHeadPartDependencyDescriptor(
    string SchemaIdentifier,
    Sha256Hash DescriptorId,
    ExternalHeadPartDependencyDisposition Disposition,
    FormReference RootSourceForm,
    FormReference RootWinningForm,
    NpcHeadPartType RootType,
    Sha256Hash GraphSha256,
    ExternalHeadPartProviderIdentity Provider,
    ImmutableArray<ExternalHeadPartRecordDependency> Members,
    ExternalHeadPartPhysicsBinding Physics,
    ImmutableArray<ExternalHeadPartAssetDependency> Assets,
    ImmutableArray<ExternalHeadPartRuntimePrerequisite> RuntimePrerequisites,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExternalHeadPartPhysicsBindingDisposition? PhysicsBinding = null);

public sealed record ExternalHeadPartExcludedShapeEvidence(
    AssetPath ProviderModel,
    string ShapeName);

public sealed record ExternalHeadPartExcludedMetadataEvidence(
    string Kind,
    string PortableValue);

public sealed record ExternalHeadPartFaceGeomExclusionAttestation(
    string SchemaIdentifier,
    Sha256Hash AttestationSha256,
    Sha256Hash DescriptorId,
    AssetPath OutputFaceGeomPath,
    Sha256Hash OutputFaceGeomSha256,
    long OutputFaceGeomByteLength,
    ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> IncludedOrdinaryShapes,
    ImmutableArray<ExternalHeadPartExcludedShapeEvidence> ExcludedShapes,
    ImmutableArray<ExternalHeadPartExcludedMetadataEvidence> ExcludedMetadata,
    string VerifierVersion);

public sealed record ExternalHeadPartInstallContextFingerprint(
    Sha256Hash Sha256,
    ImmutableArray<ExternalHeadPartInstallObservation> Observations);

public sealed record ExternalHeadPartInstallObservation(
    string Kind,
    string PortableIdentity,
    Sha256Hash Sha256,
    long ByteLength,
    int Order);

public sealed record ExternalHeadPartInstallProviderObservation(
    PluginName ProviderPlugin,
    Sha256Hash ExpectedSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Sha256Hash? CurrentSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled);

public sealed record ExternalHeadPartInstallPrerequisite(
    string DiagnosticCode,
    string PortableIdentity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Sha256Hash? ExpectedSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Sha256Hash? CurrentSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled,
    string NextAction);

public sealed record ExternalHeadPartVerifiedInstallSnapshot(
    Sha256Hash SelectedManifestSha256,
    ImmutableArray<Sha256Hash> DescriptorIds,
    ExternalHeadPartInstallContextFingerprint ContextFingerprint);

public enum ExternalHeadPartDependencyDiscoveryStatus
{
    NotApplicable = 0,
    Accepted = 1,
    Refused = 2
}

public sealed record ExternalHeadPartDependencyDiscoveryRequest(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    SkyrimFaceRecordRoute RecordRoute,
    NpcSex Sex,
    ExternalHeadPartDependencyDescriptor? ExpectedDescriptor = null,
    ExternalHeadPartPhysicsBindingDisposition? PhysicsBinding = null);

public sealed record ExternalHeadPartDependencyDiscoveryResult(
    ExternalHeadPartDependencyDiscoveryStatus Status,
    ExternalHeadPartDependencyDescriptor? Descriptor,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ExternalHeadPartPhysicsBindingRequest(
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    ExternalHeadPartProviderIdentity Provider,
    ImmutableArray<ExternalHeadPartRecordDependency> ProviderMembers,
    ExternalHeadPartPhysicsBindingDisposition? PhysicsBinding = null,
    ImmutableArray<ExternalHeadPartRecordDependency> ChainMembers = default);

public sealed record ExternalHeadPartPhysicsBindingResult(
    bool Accepted,
    ExternalHeadPartPhysicsBinding? Binding,
    ImmutableArray<ExternalHeadPartAssetDependency> PhysicsAssets,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ExternalHeadPartFaceGeomExclusionVerificationRequest(
    WorkspacePath OutputFaceGeom,
    Sha256Hash ExpectedFaceGeomSha256,
    long ExpectedFaceGeomByteLength,
    ExternalHeadPartDependencyDescriptor Descriptor,
    ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> IncludedOrdinaryShapes);

public sealed record ExternalHeadPartFaceGeomExclusionVerificationResult(
    bool Verified,
    ExternalHeadPartFaceGeomExclusionAttestation? Attestation,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record ExternalHeadPartInstallVerificationRequest(
    WorkspacePath PackageRoot,
    WorkspacePath OutputPlugin,
    PluginName OutputPluginName,
    Sha256Hash ExpectedOutputPluginSha256,
    WorkspacePath SelectedManifestPath,
    Sha256Hash ExpectedSelectedManifestSha256,
    ExternalHeadPartInstallVerificationContext? Context,
    bool RequireCurrentAuthority,
    // External PNAM observations are scoped to this request actor.  A null
    // value is retained for legacy non-promoted callers that do not perform
    // output-plugin PNAM closure.
    FormId? TargetActorFormId = null,
    // Create-time JSlot publication has no historical Finish manifest yet;
    // its current snapshot must remain historical-null.
    bool CreatePrepublication = false);

public sealed record ExternalHeadPartInstallVerificationResult(
    bool DescriptorClosureValid,
    ExternalHeadPartInstallVerificationArtifact Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IExternalHeadPartDependencyDiscovery
{
    ValueTask<ExternalHeadPartDependencyDiscoveryResult> DiscoverAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        CancellationToken cancellationToken);
}

public interface IExternalHeadPartPhysicsBindingResolver
{
    ValueTask<ExternalHeadPartPhysicsBindingResult> ResolveAsync(
        ExternalHeadPartPhysicsBindingRequest request,
        CancellationToken cancellationToken);
}

public interface IExternalHeadPartFaceGeomExclusionVerifier
{
    ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult> VerifyAsync(
        ExternalHeadPartFaceGeomExclusionVerificationRequest request,
        CancellationToken cancellationToken);
}

public interface IExternalHeadPartInstallVerifier
{
    ValueTask<ExternalHeadPartInstallVerificationResult> VerifyAsync(
        ExternalHeadPartInstallVerificationRequest request,
        CancellationToken cancellationToken);
}

public sealed record ExternalHeadPartPromotedOutputVerificationRequest(
    WorkspacePath PackageRoot,
    WorkspacePath OutputPlugin,
    PluginName OutputPluginName,
    Sha256Hash ExpectedOutputPluginSha256,
    WorkspacePath SelectedManifestPath,
    Sha256Hash ExpectedSelectedManifestSha256,
    WorkspacePath PromotedOutputBindingPath,
    Sha256Hash ExpectedPromotedOutputBindingSha256,
    ExternalHeadPartInstallVerificationContext? Context,
    bool RequireCurrentAuthority,
    // Promoted output verification binds PNAM observations to this request
    // actor, never to every NPC in the plugin.
    FormId? TargetActorFormId = null);

public interface IExternalHeadPartPromotedOutputVerifier
{
    ValueTask<ExternalHeadPartInstallVerificationResult> VerifyPromotedOutputAsync(
        ExternalHeadPartPromotedOutputVerificationRequest request,
        CancellationToken cancellationToken);
}

public sealed record ExternalHeadPartInstallVerificationArtifact(
    string SchemaIdentifier,
    bool PackageIntegrity,
    bool DescriptorClosureValid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? HistoricalSnapshotValid,
    ImmutableArray<Sha256Hash> DescriptorIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExternalHeadPartVerifiedInstallSnapshot? VerifiedInstallSnapshot,
    ExternalInstallDependencyState CurrentInstallDependencyState,
    bool InstallReady,
    bool InstallDependencyAuthority,
    bool RuntimeAuthority,
    bool VisualAuthority,
    ImmutableArray<ExternalHeadPartInstallProviderObservation>
        ProviderObservations,
    ImmutableArray<ExternalHeadPartInstallPrerequisite>
        MissingPrerequisites);

public static class ExternalHeadPartDiagnosticCodes
{
    public const string RootUnsupported = "external-headpart-root-unsupported";
    public const string RootWigOnly = "external-headpart-root-wig-only";
    public const string GraphCycle = "external-headpart-graph-cycle";
    public const string GraphMixedProvider = "external-headpart-graph-mixed-provider";
    public const string RecordUnresolved = "external-headpart-record-unresolved";
    public const string RecordDrift = "external-headpart-record-drift";
    public const string WinningProviderOverrideUnsupported =
        "external-headpart-winning-provider-override-unsupported";
    public const string PhysicsMissing = "external-headpart-physics-missing";
    public const string PhysicsAmbiguous = "external-headpart-physics-ambiguous";
    public const string AssetMissing = "external-headpart-asset-missing";
    public const string AssetDrift = "external-headpart-asset-drift";
    public const string FaceGeomContaminated = "external-headpart-facegeom-contaminated";
    public const string DescriptorLost = "external-headpart-descriptor-lost";
    public const string OutputMasterMissing = "external-headpart-output-master-missing";
    public const string OutputReferenceMissing = "external-headpart-output-reference-missing";
    public const string ProviderMissing = "external-headpart-provider-missing";
    public const string ProviderDisabled = "external-headpart-provider-disabled";
    public const string InstallContextAbsent = "external-headpart-install-context-absent";
    public const string PrecheckUnavailable = "external-headpart-precheck-unavailable";
    public const string TypedExecutorUnavailable = "external-headpart-typed-executor-unavailable";
    public const string ContextFingerprintMismatch = "external-headpart-context-fingerprint-mismatch";
}
