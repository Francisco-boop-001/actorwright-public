using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Exact copied-NPC authority for the engine-owned nineteenth NAM9 float that
/// RaceMenu does not own. The execution service reopens this plugin and refuses
/// if the hash, record, or expected value disagrees.
/// </summary>
public sealed record RaceMenuNpcNam9TrailingAuthority(
    WorkspacePath Plugin,
    Sha256Hash ExpectedPluginSha256,
    FormId NpcFormId,
    float ExpectedTrailingValue);

/// <summary>
/// Hash-bound non-plugin inputs needed to turn an admitted RaceMenu plan into
/// a standalone package. Caller-supplied package assets are copied under Data
/// by the proven blank-NPC transaction, but they must not include the reserved
/// Skyrim apply-script destination. The compatible product-owned PEX is
/// embedded, verified, and added automatically by the execution service.
/// </summary>
public sealed record RaceMenuNpcStandaloneAssetAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

/// <summary>
/// Hash-bound evidence that a complete same-stem CharGen pair was exported by
/// RaceMenu after the operator loaded and visually confirmed the exact preset.
/// This authority is static only and may select the exported NIF as its own
/// complete carrier; it never grants runtime authority.
/// </summary>
public sealed record RaceMenuNpcExternalCharGenExportAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    string AuthorityId,
    WorkspacePath Preset,
    Sha256Hash PresetSha256,
    WorkspacePath FaceGeom,
    Sha256Hash FaceGeomSha256,
    WorkspacePath FaceTint,
    Sha256Hash FaceTintSha256,
    FormReference Race,
    NpcSex Sex,
    bool UserConfirmedVisualMatch,
    bool RuntimeAuthority);

/// <summary>
/// Exact product-owned authority used by the generic RaceMenu FaceGeom bake.
/// The manifest is loaded again by the FaceGeom facade; this reference only
/// binds the standalone-asset declaration to its immutable bytes.
/// </summary>
public sealed record RaceMenuNpcFaceBakeAuthorityReference(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

/// <summary>
/// Exact product-owned authority for composing the conventional FaceTint and
/// the NPC-private residual head diffuse from ordinary, hash-bound inputs.
/// The referenced manifest is parsed and independently revalidated by the
/// texture-composition service; it is never a finished-output oracle.
/// </summary>
public sealed record RaceMenuNpcFaceTextureBakeAuthorityReference(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

public sealed record RaceMenuNpcBodySlidePresetAuthorityReference(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

public sealed record RaceMenuNpcBodyMeshAuthorityReference(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

public sealed record RaceMenuNpcBodySlidePresetAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    string AuthorityId,
    WorkspacePath PresetXml,
    Sha256Hash PresetXmlSha256,
    string PresetName,
    string SliderSet,
    ImmutableArray<string> Groups,
    int SliderCount,
    bool RuntimeAuthority);

public enum RaceMenuNpcBodyMeshRole
{
    Body0,
    Body1,
    Hands0,
    Hands1,
    Feet0,
    Feet1
}

public static class RaceMenuNpcBodyMeshRoleExtensions
{
    public static string ToWireName(this RaceMenuNpcBodyMeshRole role) =>
        role switch
        {
            RaceMenuNpcBodyMeshRole.Body0 => "body0",
            RaceMenuNpcBodyMeshRole.Body1 => "body1",
            RaceMenuNpcBodyMeshRole.Hands0 => "hands0",
            RaceMenuNpcBodyMeshRole.Hands1 => "hands1",
            RaceMenuNpcBodyMeshRole.Feet0 => "feet0",
            RaceMenuNpcBodyMeshRole.Feet1 => "feet1",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role,
                "Unsupported body mesh role.")
        };

    public static bool TryParseWireName(string value, out RaceMenuNpcBodyMeshRole role)
    {
        role = value switch
        {
            "body0" => RaceMenuNpcBodyMeshRole.Body0,
            "body1" => RaceMenuNpcBodyMeshRole.Body1,
            "hands0" => RaceMenuNpcBodyMeshRole.Hands0,
            "hands1" => RaceMenuNpcBodyMeshRole.Hands1,
            "feet0" => RaceMenuNpcBodyMeshRole.Feet0,
            "feet1" => RaceMenuNpcBodyMeshRole.Feet1,
            _ => default
        };
        return value is "body0" or "body1" or "hands0" or "hands1" or "feet0" or "feet1";
    }
}

public sealed record RaceMenuNpcBodyMeshAsset(
    RaceMenuNpcBodyMeshRole Role,
    WorkspacePath Source,
    Sha256Hash ExpectedSha256,
    AssetPath Destination);

public sealed record RaceMenuNpcBodyMeshAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    string AuthorityId,
    RaceMenuNpcBodySlidePresetAuthorityReference BodySlidePresetAuthority,
    ImmutableArray<RaceMenuNpcBodyMeshAsset> Meshes,
    bool RuntimeAuthority);

/// <summary>
/// A hash-bound operator decision that permits one exact RaceMenu overlay row
/// to be omitted. The source preset, row index, node, and primary texture all
/// remain part of the authority so the decision cannot drift to another row.
/// </summary>
public enum RaceMenuNpcOverlayDecisionAction
{
    Omit
}

public sealed record RaceMenuNpcUserDecisionEvidence(
    WorkspacePath Path,
    Sha256Hash ExpectedSha256);

public sealed record RaceMenuNpcOverlayDecision(
    int SourceIndex,
    string Node,
    AssetPath ExpectedTexture,
    RaceMenuNpcOverlayDecisionAction Action,
    string Reason,
    RaceMenuNpcUserDecisionEvidence UserDecisionEvidence);

public sealed record RaceMenuNpcOverlayDecisionSet(
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    Sha256Hash PresetSha256,
    ImmutableArray<RaceMenuNpcOverlayDecision> Decisions);

/// <summary>
/// Exact provider authority for a runtime texture that the package must not
/// redistribute. <see cref="Provider"/> is the provider label returned by the
/// asset indexer (an archive file name, or <c>loose</c>).
/// </summary>
public sealed record RaceMenuNpcExternalTextureAuthority(
    AssetPath DataRelativePath,
    string Provider,
    WorkspacePath Source,
    Sha256Hash ExpectedSourceSha256,
    Sha256Hash ExpectedMemberSha256);

/// <summary>
/// One immutable evidence file supporting an admitted final-output oracle.
/// The oracle is a bounded escape hatch for a previously qualified artifact;
/// it is not a claim that the general RaceMenu bake has been implemented.
/// </summary>
public sealed record RaceMenuNpcFinalOutputEvidence(
    WorkspacePath Path,
    Sha256Hash ExpectedSha256);

/// <summary>
/// A complete, hash-bound FaceGeom/FaceTint pair that has already passed an
/// independent static qualification. The source preset and both original
/// CharGen artifacts remain bound so the authority cannot drift to another
/// character. Outputs are still reopened by the normal transaction and never
/// acquire runtime authority merely because the source pair had runtime use.
/// </summary>
public sealed record RaceMenuNpcFinalOutputAuthority(
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    string AuthorityId,
    Sha256Hash PresetSha256,
    Sha256Hash CharGenFaceGeomSha256,
    Sha256Hash CharGenFaceTintSha256,
    WorkspacePath FaceGeom,
    Sha256Hash FaceGeomSha256,
    WorkspacePath FaceTint,
    Sha256Hash FaceTintSha256,
    int FaceTintWidth,
    int FaceTintHeight,
    ImmutableArray<RaceMenuNpcFinalOutputEvidence> Evidence,
    bool RuntimeAuthority);

public sealed record RaceMenuNpcStandaloneAssets(
    string AssetSetId,
    RaceMenuNpcNam9TrailingAuthority Nam9Authority,
    SkyrimPrivateHeadTexturePaths PrivateHeadTextures,
    int FaceTintWidth,
    int FaceTintHeight,
    ImmutableArray<BlankNpcTransitivePackageAsset> PackageAssets,
    RaceMenuNpcOverlayDecisionSet? OverlayDecisions,
    ImmutableArray<RaceMenuNpcExternalTextureAuthority> ExternalTextureAuthorities,
    RaceMenuNpcFinalOutputAuthority? FinalOutputAuthority)
{
    /// <summary>
/// Schema 3 is the bounded accepted-output oracle. Schema 4 is the generic
/// provider/TRI bake and must not carry a finished-output authority. Schema
/// 5 adds product-owned FaceTint/private-diffuse composition and also must
/// not carry a finished-output authority. Schema 6 qualifies the exact
/// same-stem CharGen NIF/DDS from the selected preset bundle directly; it
/// carries neither a finished-output oracle nor a product bake authority.
/// Schema 7 extends that direct CharGen route with external BodySlide XML
/// identity plus K-local staged body/hands/feet mesh authority. Schema 8 is
/// the Manager-owned FaceGeom-carrier route: it retains only paired,
/// hash-bound external head-part descriptor and exclusion-attestation arrays,
/// with optional paired BodySlide authority.
    /// </summary>
    public int SchemaVersion { get; init; } = 3;

    public RaceMenuNpcFaceBakeAuthorityReference? FaceBakeAuthority { get; init; }

    public RaceMenuNpcFaceTextureBakeAuthorityReference? FaceTextureBakeAuthority { get; init; }

    public RaceMenuNpcBodySlidePresetAuthority? BodySlidePresetAuthority { get; init; }

    public RaceMenuNpcBodyMeshAuthority? BodyMeshAuthority { get; init; }

    public RaceMenuNpcExternalCharGenExportAuthority? ExternalCharGenExportAuthority
    { get; init; }

    /// <summary>
    /// Provider-neutral external head-part descriptors admitted by standalone
    /// authority schema 8. The descriptor is portable dependency evidence; it
    /// does not grant permission to copy provider assets.
    /// </summary>
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>
        ExternalHeadPartDependencies { get; init; } = [];

    /// <summary>
    /// One Manager-owned FaceGeom exclusion attestation per schema-8
    /// descriptor. Attestations bind the transaction-local carrier hash and
    /// the proof that external provider geometry stayed out of FaceGeom.
    /// </summary>
    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
        ExternalHeadPartExclusionAttestations { get; init; } = [];

    /// <summary>
    /// Exact selected-HDPT rows retained in the output PNAM while their
    /// provider closures are parsed as external dependencies and excluded from
    /// the product-owned FaceGeom geometry.
    /// </summary>
    public ImmutableArray<SkyrimNativeFaceGeomExternalHeadPart>
        NativeFaceGeomExternalHeadParts { get; init; } = [];
}

/// <summary>
/// Transaction-local, path/hash-backed proof that the Manager itself created
/// and independently reopened the exact FaceGeom carrier. This is not
/// accepted from execution-request JSON and does not grant runtime authority.
/// </summary>
public sealed record RaceMenuManagerOwnedFaceGeomCarrierAuthority(
    WorkspacePath FaceGeom,
    Sha256Hash FaceGeomSha256);

/// <summary>
/// Manager-authored, hash-bound external dependency evidence for the exact
/// selected preset. It is evidence only and grants no runtime authority.
/// </summary>
public sealed record RaceMenuSelectedDependencyManifestAuthority(
    string DependencyId,
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

public sealed record RaceMenuNpcExecutionRequest(
    RaceMenuNpcBuildRequest Build,
    RaceMenuNpcStandaloneAssetAuthority AssetAuthority)
{
    /// <summary>
    /// Mirrors the pinned loader's checked-by-default BodySlide choice. False
    /// deliberately omits preset body morphs from BodyGen while preserving the
    /// preset document and all other appearance channels.
    /// </summary>
    public bool ApplyBodySlide { get; init; } = true;

    /// <summary>
    /// Explicitly admits an inherited race naked-skin route whose winning
    /// body, hand, or foot ARMA omits TXST and instead uses texture paths
    /// embedded in its hash-bound loose NIF. False preserves the legacy
    /// TXST-only admission rule.
    /// </summary>
    public bool AllowInheritedMeshEmbeddedSkinTextureRoute { get; init; }

    /// <summary>
    /// Selects the bone-node transform authority used when the Manager
    /// assembles a new FaceGeom carrier. The legacy source-model mode remains
    /// the default; identity bones are an explicit bounded route for sources
    /// such as UBE whose skin-to-bone data already carries the bind transform.
    /// </summary>
    public SseFaceGeomCarrierSkeletonAuthority FaceGeomSkeletonAuthority
    { get; init; } =
        SseFaceGeomCarrierSkeletonAuthority.SourceModelWorldTranslations;

    /// <summary>
    /// Optional in-memory authority set only by the Manager-owned JSlot facade
    /// after its native companion bake has completed and reopened. Generic
    /// schema-6 callers continue to use the provider carrier.
    /// </summary>
    public RaceMenuManagerOwnedFaceGeomCarrierAuthority?
        ManagerOwnedFaceGeomCarrier
    { get; init; }

    /// <summary>
    /// Optional in-memory authority emitted by the Manager-owned JSlot
    /// selection transaction. Prepared execution-request JSON cannot supply
    /// or replace this evidence.
    /// </summary>
    public RaceMenuSelectedDependencyManifestAuthority?
        SelectedDependencyManifest
    { get; init; }

    /// <summary>
    /// In-memory-only descriptor authority emitted by the Manager-owned
    /// selection transaction. Prepared execution-request JSON cannot supply
    /// or replace this evidence.
    /// </summary>
    [JsonIgnore]
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>
        ExternalHeadPartDependencies { get; init; } = [];

    /// <summary>
    /// In-memory-only FaceGeom exclusion attestations paired with
    /// <see cref="ExternalHeadPartDependencies"/>. Prepared execution-request
    /// JSON cannot supply or replace this evidence.
    /// </summary>
    [JsonIgnore]
    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
        ExternalHeadPartExclusionAttestations { get; init; } = [];

    /// <summary>
    /// Opaque, non-serializable marker for the first phase of the JSlot
    /// external-output binding transaction. The singleton value is owned by
    /// the Pipeline assembly; callers cannot manufacture a valid probe token.
    /// </summary>
    [JsonIgnore]
    public RaceMenuJslotOutputBindingProbeToken?
        JslotOutputBindingProbe { get; init; }
}

/// <summary>
/// Gate 2 product result. Completion means the real static package was written
/// and independently reopened; it never means Skyrim executed the PEX or
/// rendered the actor.
/// </summary>
public sealed record RaceMenuNpcExecutionResult(
    bool Completed,
    RaceMenuNpcAppearancePlan? Plan,
    RaceMenuNpcStandaloneAssets? Assets,
    SkyrimNpcRuntimeAppearancePayload? RuntimeAppearance,
    SkyrimNpcApplySseVmadPayload? RuntimeVmad,
    BodyGenBuildResult? BodyGen,
    BlankNpcBuildResult? Build,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ExistingNpcAppearanceBuildResult? ExistingNpcBuild { get; init; }

    public SkyrimNpcWholeSkinAuthority? WholeSkinAuthority { get; init; }

    /// <summary>
    /// Strict static evidence for a completed external-SMP JSlot candidate.
    /// Ordinary builds and the private probe phase leave this null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RaceMenuNpcExternalInstallPrepublicationArtifact?
        ExternalInstallPrepublication { get; init; }

    /// <summary>
    /// In-memory-only facts from the private JSlot output-plugin probe. The
    /// probe deliberately crosses the build-service boundary as an
    /// incomplete result: it never carries a package Build or authority.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RaceMenuNpcExternalInstallOutputPluginProbeArtifact?
        ExternalInstallOutputPluginProbe { get; init; }
}

public sealed record RaceMenuNpcExternalInstallOutputPluginProbeArtifact(
    PluginName OutputPlugin,
    WorkspacePath PluginPath,
    Sha256Hash PluginSha256,
    FormId AllocatedFormId);

/// <summary>
/// Opaque in-memory token type for the private JSlot probe. Constructing a
/// token does not authorize a probe; Pipeline accepts only its own singleton
/// by reference identity.
/// </summary>
public sealed class RaceMenuJslotOutputBindingProbeToken
{
    public RaceMenuJslotOutputBindingProbeToken()
    {
    }
}

public sealed record RaceMenuNpcExternalInstallPrepublicationBinding(
    Sha256Hash DescriptorId,
    Sha256Hash FaceGeomExclusionAttestationSha256);

public sealed record RaceMenuNpcExternalInstallPrepublicationArtifact(
    Sha256Hash PackageManifestSha256,
    Sha256Hash SelectedManifestSha256,
    ImmutableArray<RaceMenuNpcExternalInstallPrepublicationBinding> Bindings,
    ExternalHeadPartInstallVerificationArtifact Verification);

public interface IRaceMenuNpcBuildService
{
    ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync(
        RaceMenuNpcExecutionRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Read-only production admission result for standalone authority.</summary>
public sealed record RaceMenuNpcStandaloneAuthorityReadResult(
    bool Accepted,
    RaceMenuNpcStandaloneAssets? Assets,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuNpcStandaloneAuthorityReader
{
    ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
        RaceMenuNpcStandaloneAssetAuthority authority,
        CancellationToken cancellationToken);
}
