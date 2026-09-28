using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Writes the standalone authority for one already admitted appearance plan.
/// The exact CharGen pair remains owned by the selected preset bundle; this
/// request only binds its NAM9 carrier, dimensions, and copied-Data texture
/// winners before an execution request can be committed.
/// </summary>
public sealed record RaceMenuPresetStandaloneAuthorityWriteRequest(
    RaceMenuNpcAppearancePlan Plan,
    RaceMenuPresetRecordAuthorityDraft RecordDraft,
    RaceMenuNpcNam9TrailingAuthority Nam9Authority,
    RaceMenuNpcOverlayDecisionSet? OverlayDecisions,
    ImmutableArray<RaceMenuNpcExternalTextureAuthority> RetainedExternalTextures,
    WorkspacePath Destination,
    RaceMenuNpcBodySlidePresetAuthority? BodySlidePresetAuthority = null,
    RaceMenuNpcBodyMeshAuthority? BodyMeshAuthority = null,
    RaceMenuNpcExternalCharGenExportAuthority?
        ExternalCharGenExportAuthority = null)
{
    /// <summary>Requested output paths, separate from the source TXST authority.</summary>
    public SkyrimPrivateHeadTexturePaths? PrivateHeadTextures { get; init; }

    /// <summary>Prior copied assets; only required private head textures are retained.</summary>
    public ImmutableArray<BlankNpcTransitivePackageAsset> RetainedPackageAssets { get; init; } = [];

    /// <summary>
    /// Provider-neutral external head-part descriptors. A non-empty pair of
    /// descriptor and attestation arrays selects standalone schema 8.
    /// </summary>
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>
        ExternalHeadPartDependencies { get; init; } = [];

    /// <summary>Schema-8 FaceGeom exclusion attestations paired by descriptor ID.</summary>
    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
        ExternalHeadPartExclusionAttestations { get; init; } = [];
}

public sealed record RaceMenuPresetStandaloneAuthorityArtifact(
    string AssetSetId,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    int FaceTintWidth,
    int FaceTintHeight,
    ImmutableArray<RaceMenuNpcExternalTextureAuthority> ExternalTextures,
    bool RuntimeAuthority,
    RaceMenuNpcBodySlidePresetAuthority? BodySlidePresetAuthority = null,
    RaceMenuNpcBodyMeshAuthority? BodyMeshAuthority = null,
    RaceMenuNpcExternalCharGenExportAuthority?
        ExternalCharGenExportAuthority = null)
{
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>
        ExternalHeadPartDependencies { get; init; } = [];

    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
        ExternalHeadPartExclusionAttestations { get; init; } = [];
}

public sealed record RaceMenuPresetStandaloneAuthorityWriteResult(
    bool Written,
    RaceMenuPresetStandaloneAuthorityArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetStandaloneAuthorityWriter
{
    ValueTask<RaceMenuPresetStandaloneAuthorityWriteResult> WriteAsync(
        RaceMenuPresetStandaloneAuthorityWriteRequest request,
        CancellationToken cancellationToken);
}
