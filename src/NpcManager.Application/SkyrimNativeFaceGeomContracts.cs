using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Exact schema-bound HDPT rows whose record authority remains in PNAM while
/// their provider NIF closure is parsed as an external dependency and excluded
/// from the product-owned FaceGeom geometry.
/// </summary>
public sealed record SkyrimNativeFaceGeomExternalHeadPart(
    int Order,
    string SourceFormIdentifier,
    string ProviderFormKey,
    string Disposition);

public static class SkyrimNativeFaceGeomExternalHeadPartAuthority
{
    public const string DintPlugin = "[dint999] HairPack02.esp";
    public const string DintPluginSha256 =
        "104F3E6A8DC7EF7D142070E0B0742E3D27CD967A93C00313DA0883CAE79414A5";
    public const string DintSourceFormIdentifier =
        DintPlugin + "|00BC05";
    public const string DintProviderFormKey =
        DintPlugin + "|0x0000BC05";
    public const string RecordOnlyExternalDisposition =
        "record-only-external";
}

public sealed record SkyrimNativeFaceGeomBuildRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FaceGenBakeTarget Target,
    AssetPath FaceTintPath,
    WorkspacePath OutputNif,
    SkyrimFaceGenSidecarOverlay? SidecarOverlay = null)
{
    public SseFaceGeomCarrierSkeletonAuthority SkeletonAuthority
    { get; init; } =
        SseFaceGeomCarrierSkeletonAuthority.SourceModelWorldTranslations;

    public ImmutableArray<SkyrimNativeFaceGeomExternalHeadPart>
        NativeFaceGeomExternalHeadParts { get; init; } = [];

    public uint? EffectiveHairColorPackedRgb { get; init; }

    public ImmutableArray<SkyrimFaceRecordPluginAuthority>
        StagedPluginAuthorities { get; init; } = [];

    /// <summary>
    /// Optional descriptor expectation supplied by a caller that already
    /// attested an external head-part graph. Discovery must prove this exact
    /// descriptor before any output is written.
    /// </summary>
    public ExternalHeadPartDependencyDescriptor?
        ExpectedExternalHeadPartDescriptor { get; init; }
}

public sealed record SkyrimNativeFaceGeomShapeEvidence(
    FormReference HeadPart,
    NpcHeadPartType EffectiveType,
    AssetPath ModelNif,
    Sha256Hash ModelSha256,
    string ModelShapeName,
    string OutputShapeName,
    int VertexCount,
    Sha256Hash TopologySha256,
    Sha256Hash BasePositionSha256,
    Sha256Hash FinalPositionSha256,
    ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence> TriEvidence,
    bool UsesFaceTint);

/// <summary>Exact reader-admitted dummy omitted from the owned carrier.</summary>
public sealed record SkyrimNativeFaceGeomOmittedShape(
    FormReference HeadPart, AssetPath ModelNif, Sha256Hash ModelSha256, string ModelShapeName);

public sealed record SkyrimNativeFaceGeomBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    FaceGenBakeTarget Target,
    SkyrimFaceRecordRoute RecordRoute,
    SkyrimFaceMorphSnapshot NativeMorphs,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginAuthorities,
    ImmutableArray<SkyrimAssetAuthority> AssetAuthorities,
    ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> Shapes,
    SseFaceGeomCarrierMaterializationArtifact Materialization,
    bool RuntimeAuthority,
    ImmutableArray<SkyrimAssetAuthority> CatalogAuthorities = default,
    SkyrimRaceMenuSliderCatalog? RaceMenuCatalog = null,
    SkyrimFaceGenSidecarOverlay? SidecarOverlay = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<SkyrimNativeFaceGeomOmittedShape>? OmittedShaderlessDummies { get; init; }

    /// <summary>
    /// Exact external NIF/TRI/DDS winners consumed by the native build or
    /// retained in its output carrier. This is a packaging dependency closure,
    /// not permission to redistribute the provider files.
    /// </summary>
    public ImmutableArray<SkyrimAssetAuthority> ExternalDependencyAuthorities
    { get; init; } = [];

    /// <summary>
    /// Exact external provider sidecars, such as an admitted HDT XML file.
    /// These are retained separately and never enter the FaceGeom
    /// NIF/TRI/DDS dependency manifest arrays.
    /// </summary>
    public ImmutableArray<SkyrimAssetAuthority>
        ExternalProviderSidecarAuthorities
    { get; init; } = [];

    public ImmutableArray<SkyrimNativeFaceGeomExternalHeadPart>
        ExternalHeadParts { get; init; } = [];

    public ImmutableArray<SkyrimAssetAuthority> FinalTextureAuthorities
    { get; init; } = [];

    /// <summary>
    /// Generic external head-part descriptors are emitted only for builds
    /// that actually discovered one. Null is omitted to preserve ordinary
    /// artifact bytes and legacy JSON readability.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>?
        ExternalHeadPartDependencies { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>?
        ExternalHeadPartExclusionAttestations { get; init; }
}

public sealed record SkyrimNativeFaceGeomBuildResult(
    bool Written,
    bool Verified,
    SkyrimNativeFaceGeomBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimNativeFaceGeomBuildService
{
    ValueTask<SkyrimNativeFaceGeomBuildResult> BuildAsync(
        SkyrimNativeFaceGeomBuildRequest request,
        CancellationToken cancellationToken);
}
