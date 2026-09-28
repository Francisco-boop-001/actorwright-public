using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private const int MaximumPackagedFaceTextureBytes =
        RaceMenuNpcFaceTextureCompositionLimits.MaximumEncodedBgra8DdsBytes;
    private const int MaximumFaceTextureEvidenceBytes = 1 * 1024 * 1024;

    private async ValueTask<bool> AddFaceTextureEvidenceAssetsAsync(
        RaceMenuNpcFaceTextureBakeAuthorityReference authority,
        AssetPath privateDiffuseDestination,
        RaceMenuNpcFaceTextureBuildArtifact artifact,
        WorkspacePath stagingRoot,
        ImmutableArray<BlankNpcTransitivePackageAsset>.Builder packageAssets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (artifact.AuthorityManifestSha256 != authority.ExpectedManifestSha256 ||
            artifact.RuntimeAuthority ||
            !artifact.ConventionalFaceTint.IsUnder(stagingRoot) ||
            !artifact.PrivateDiffuse.IsUnder(stagingRoot) ||
            !artifact.EvidenceReport.IsUnder(stagingRoot) ||
            !string.Equals(artifact.PrivateDiffuseDestination.Value,
                privateDiffuseDestination.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("racemenu-build-face-texture-artifact-invalid",
                "The generated face-texture artifact lost its authority, staged paths, package destination, or non-runtime status."));
            return false;
        }
        if (await ReadHashBoundOrdinaryFileAsync(artifact.ConventionalFaceTint,
                artifact.ConventionalFaceTintSha256, MaximumPackagedFaceTextureBytes,
                "racemenu-build-facetint-output", "verified conventional FaceTint",
                diagnostics, cancellationToken) is null ||
            await ReadHashBoundOrdinaryFileAsync(artifact.PrivateDiffuse,
                artifact.PrivateDiffuseSha256, MaximumPackagedFaceTextureBytes,
                "racemenu-build-private-diffuse-output", "verified private diffuse",
                diagnostics, cancellationToken) is null ||
            await ReadHashBoundOrdinaryFileAsync(artifact.EvidenceReport,
                artifact.EvidenceReportSha256, MaximumFaceTextureEvidenceBytes,
                "racemenu-build-face-texture-evidence", "face-texture composition evidence",
                diagnostics, cancellationToken) is null)
            return false;

        packageAssets.Add(new BlankNpcTransitivePackageAsset(
            artifact.PrivateDiffuse,
            artifact.PrivateDiffuseSha256,
            privateDiffuseDestination));
        packageAssets.Add(new BlankNpcTransitivePackageAsset(
            artifact.EvidenceReport,
            artifact.EvidenceReportSha256,
            new AssetPath("NPCManager/Evidence/FaceTextures/composition-result.json")));
        diagnostics.Add(new Diagnostic("racemenu-build-face-textures-staged",
            DiagnosticSeverity.Info,
            "Added the generated private diffuse and independently verified composition report to the immutable package inventory."));
        return true;
    }
}
