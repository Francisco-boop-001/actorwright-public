using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Closed input for compiling a complete Skyrim FaceGeom NIF from an accepted
/// RaceMenu appearance plan. The CharGen NIF is source evidence only; every
/// emitted carrier XYZ lane must come from the product-owned morph bake.
/// </summary>
public sealed record RaceMenuNpcFaceGeomBuildRequest(
    RaceMenuNpcAppearancePlan AppearancePlan,
    SkyrimFaceMorphSnapshot NativeMorphs,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> OrderedCustomMorphs,
    WorkspacePath AllowedRoot,
    WorkspacePath FaceBakeAuthorityManifest,
    Sha256Hash ExpectedFaceBakeAuthorityManifestSha256,
    WorkspacePath CompleteCarrierNif,
    Sha256Hash ExpectedCompleteCarrierNifSha256,
    WorkspacePath SourceCharGenNif,
    Sha256Hash ExpectedSourceCharGenNifSha256,
    WorkspacePath OwnedStagingRoot,
    WorkspacePath OutputNif);

/// <summary>Exact materialized provider evidence for one TRI consumed by the bake.</summary>
public sealed record RaceMenuNpcFaceGeomTriEvidence(
    SkyrimFaceMorphTriRole Role,
    AssetPath AssetPath,
    Sha256Hash ContentSha256,
    string ProviderId,
    SkyrimFaceBakeAuthorityProviderKind ProviderKind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    SkyrimRaceMenuFaceBakeTriDisposition Disposition);

/// <summary>Exact record, asset, and generated-XYZ evidence for one carrier shape.</summary>
public sealed record RaceMenuNpcFaceGeomShapeEvidence(
    FormReference HeadPart,
    SkyrimFaceRecordProvider RecordProvider,
    AssetPath ModelNif,
    Sha256Hash ModelNifSha256,
    string ModelShapeName,
    string CarrierShapeName,
    int VertexCount,
    Sha256Hash ModelPositionSha256,
    Sha256Hash TopologySha256,
    WorkspacePath GeneratedXyzFile,
    Sha256Hash GeneratedXyzSha256,
    Sha256Hash FinalPositionSha256,
    ImmutableArray<RaceMenuNpcFaceGeomTriEvidence> TriEvidence);

/// <summary>
/// Evidence emitted only after the generated-XYZ merge has been independently
/// reopened and verified. RuntimeAuthority is deliberately false.
/// </summary>
public sealed record RaceMenuNpcFaceGeomBuildArtifact(
    string ResultLabel,
    string AuthorityId,
    Sha256Hash AuthorityManifestSha256,
    WorkspacePath SourceCharGenNif,
    Sha256Hash SourceCharGenNifSha256,
    WorkspacePath CompleteCarrierNif,
    Sha256Hash CompleteCarrierNifSha256,
    WorkspacePath OutputNif,
    Sha256Hash OutputNifSha256,
    ImmutableArray<FormReference> GeometrySelectedRootHeadParts,
    SkyrimFaceRecordRoute RecordRoute,
    ImmutableArray<RaceMenuNpcFaceGeomShapeEvidence> Shapes,
    RaceMenuCharGenFaceGeomMergeArtifact MergeArtifact,
    RaceMenuCharGenFaceGeomMergeVerificationResult IndependentVerification,
    bool RuntimeAuthority);

public sealed record RaceMenuNpcFaceGeomBuildResult(
    bool Written,
    bool Verified,
    RaceMenuNpcFaceGeomBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Resolves exact record and asset authority, bakes every admitted carrier
/// shape, and performs a two-pass generated-XYZ FaceGeom merge.
/// </summary>
public interface IRaceMenuNpcFaceGeomBuildService
{
    ValueTask<RaceMenuNpcFaceGeomBuildResult> BuildAsync(
        RaceMenuNpcFaceGeomBuildRequest request,
        CancellationToken cancellationToken);
}
