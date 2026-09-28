using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Physical provider kind carried across the face-bake authority seam.</summary>
public enum SkyrimFaceBakeAuthorityProviderKind
{
    Loose,
    Bsa
}

/// <summary>
/// Exact winner-resolved bytes for one manifest-declared Skyrim asset. Provider
/// identity and content identity remain independently hash-bound for BSA members.
/// </summary>
public sealed record SkyrimFaceBakeAuthorityAsset(
    string Id,
    string ProviderId,
    SkyrimFaceBakeAuthorityProviderKind ProviderKind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    AssetPath AssetPath,
    long ContentLength,
    Sha256Hash ContentSha256,
    ImmutableArray<byte> Content);

/// <summary>One catalog configuration file in exact loaded-plugin order.</summary>
public sealed record SkyrimFaceBakeCatalogConfigAuthority(
    PluginName Plugin,
    SkyrimFaceBakeAuthorityAsset Asset);

/// <summary>
/// Exact TRI roles for one carrier shape. Null is an explicit absent role; the
/// ordered extended array is always materialized, including when empty.
/// </summary>
public sealed record SkyrimFaceBakeShapeTriAuthority(
    string CarrierShapeName,
    SkyrimFaceBakeAuthorityAsset? RaceMorphTri,
    SkyrimFaceBakeAuthorityAsset? ChargenMorphTri,
    SkyrimFaceBakeAuthorityAsset? MeshMorphTri,
    ImmutableArray<SkyrimFaceBakeAuthorityAsset> ExtendedMorphTris);

/// <summary>
/// Binds one routed HDPT to the exact model shape used as the bake/rest source
/// and to the complete-carrier shape it may update. Geometry hashes are checked
/// by the NIF-reader/composition seam, not reconstructed by this loader.
/// </summary>
public sealed record SkyrimFaceBakeCarrierShapeAuthority(
    FormReference HeadPart,
    SkyrimFaceBakeAuthorityAsset ModelNif,
    string ModelShapeName,
    string CarrierShapeName,
    Sha256Hash ExpectedModelPositionSha256,
    Sha256Hash ExpectedModelTopologySha256,
    Sha256Hash ExpectedCarrierTopologySha256);

/// <summary>
/// Closed, materialized Gate 2 input authority. It describes the complete
/// record/carrier closure and makes no claim about a particular preset's
/// selected headparts; that intersection belongs to the composition facade.
/// </summary>
public sealed record SkyrimFaceBakeAuthority(
    string AuthorityId,
    ImmutableArray<PluginName> LoadedPlugins,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> RecordPluginAuthorities,
    ImmutableArray<SkyrimFaceBakeAuthorityAsset> Assets,
    ImmutableArray<SkyrimFaceBakeCatalogConfigAuthority> CatalogConfigs,
    ImmutableArray<SkyrimFaceBakeShapeTriAuthority> ShapeTriInputs,
    ImmutableArray<SkyrimFaceBakeCarrierShapeAuthority> CarrierShapes,
    // Exact mapped HDPT rows that intentionally contribute a plugin record but
    // no FaceGeom mesh. This is an ordered, hash-bound exception closure; it
    // must never be inferred from broad headpart types such as Scar.
    ImmutableArray<FormReference> RecordOnlyMappedHeadParts,
    ImmutableArray<AssetPath> OptionalUnavailableExtensions,
    Sha256Hash ManifestSha256);

public enum SkyrimFaceBakeAuthorityLoadStatus
{
    Loaded,
    ValidationRefused,
    SecurityRefused,
    ContentRefused
}

/// <summary>
/// Reads one already-named, hash-bound K-local authority independently of any
/// current preset selection.
/// </summary>
public sealed record SkyrimFaceBakeAuthorityLoadRequest(
    WorkspacePath AllowedRoot,
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

public sealed record SkyrimFaceBakeAuthorityLoadResult(
    SkyrimFaceBakeAuthorityLoadStatus Status,
    SkyrimFaceBakeAuthority? Authority,
    Sha256Hash? ActualManifestSha256,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Loaded => Status == SkyrimFaceBakeAuthorityLoadStatus.Loaded && Authority is not null;
}

public interface ISkyrimFaceBakeAuthorityLoader
{
    ValueTask<SkyrimFaceBakeAuthorityLoadResult> LoadAsync(
        SkyrimFaceBakeAuthorityLoadRequest request,
        CancellationToken cancellationToken);
}
