using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Closed resource budget for product-owned FaceTint/private-diffuse
/// composition. The bounds are part of the application contract so authority
/// parsing, composition, and package admission cannot drift apart.
/// </summary>
public static class RaceMenuNpcFaceTextureCompositionLimits
{
    public const int MaximumAxisPixels = 8192;
    public const int MaximumPixels = 2048 * 2048;
    public const int MaximumActiveLayers = 32;
    public const int Bgra8DdsHeaderBytes = 128;
    public const int Bgra8BytesPerPixel = 4;
    public const int MaximumEncodedBgra8DdsBytes =
        Bgra8DdsHeaderBytes + MaximumPixels * Bgra8BytesPerPixel;
}

/// <summary>
/// Result of composing conventional record-mapped tints with a private
/// residual diffuse. All files are new K-local artifacts and have been decoded
/// again after writing. This is static build evidence, never runtime visual
/// authority.
/// </summary>
public sealed record RaceMenuNpcFaceTextureBuildArtifact(
    string AuthorityId,
    WorkspacePath AuthorityManifest,
    Sha256Hash AuthorityManifestSha256,
    WorkspacePath ConventionalFaceTint,
    Sha256Hash ConventionalFaceTintSha256,
    WorkspacePath PrivateDiffuse,
    Sha256Hash PrivateDiffuseSha256,
    AssetPath PrivateDiffuseDestination,
    WorkspacePath EvidenceReport,
    Sha256Hash EvidenceReportSha256,
    Sha256Hash FullCompositeSha256,
    Sha256Hash BaseDiffuseSha256,
    int Width,
    int Height,
    int ProtectedNeckStartRow,
    int ProtectedNeckEndRow,
    int MappedLayerCount,
    int BakedLayerCount,
    int ReconstructionMaximumRgbByteError,
    double ReconstructionMeanRgbByteError,
    bool RuntimeAuthority);

/// <summary>
/// Typed product request. Input identity and ordered tint semantics come from
/// the already-admitted appearance plan. File/provider identity comes from the
/// independently hash-bound authority manifest.
/// </summary>
public sealed record RaceMenuNpcFaceTextureBuildRequest(
    RaceMenuNpcAppearancePlan Plan,
    WorkspacePath AllowedRoot,
    RaceMenuNpcFaceTextureBakeAuthorityReference Authority,
    int Width,
    int Height,
    AssetPath PrivateDiffuseDestination,
    WorkspacePath StagingRoot,
    WorkspacePath FaceTintOutput,
    WorkspacePath PrivateDiffuseOutput,
    WorkspacePath EvidenceOutput);

public sealed record RaceMenuNpcFaceTextureBuildResult(
    bool Written,
    bool Verified,
    RaceMenuNpcFaceTextureBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuNpcFaceTextureBuildService
{
    ValueTask<RaceMenuNpcFaceTextureBuildResult> BuildAsync(
        RaceMenuNpcFaceTextureBuildRequest request,
        CancellationToken cancellationToken);
}
