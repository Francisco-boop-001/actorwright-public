using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class RaceMenuNpcFaceTextureBuildService
{
    private sealed record SourceAuthority(
        WorkspacePath Path,
        Sha256Hash Sha256,
        int Width,
        int Height);

    private sealed record LayerAuthority(
        int JslotIndex,
        RaceMenuNpcTintDispositionKind Disposition,
        uint PresetColor,
        string PresetTexture,
        string? Provider,
        WorkspacePath? Source,
        Sha256Hash? SourceSha256);

    private sealed record ProtectedNeckAuthority(
        int StartRowInclusive,
        int EndRowInclusive);

    private sealed record RgbErrorToleranceAuthority(
        int MaximumRgbByteError,
        double MaximumMeanRgbByteError);

    private sealed record FaceTextureToleranceAuthority(
        RgbErrorToleranceAuthority TintModel,
        RgbErrorToleranceAuthority SplitShader);

    private sealed record FaceTextureAuthority(
        string AuthorityId,
        Sha256Hash ManifestSha256,
        Sha256Hash PresetSha256,
        AssetPath PrivateDiffuseDestination,
        SourceAuthority FullComposite,
        SourceAuthority BaseDiffuse,
        ProtectedNeckAuthority ProtectedNeck,
        FaceTextureToleranceAuthority Tolerances,
        ImmutableArray<LayerAuthority> Layers);

    private sealed record CompositionOutcome(
        byte[] FullComposite,
        byte[] BaseDiffuse,
        byte[] ConventionalFaceTint,
        byte[] PrivateDiffuse,
        int MappedLayerCount,
        int BakedLayerCount,
        int CompositeModelMaximumRgbByteError,
        double CompositeModelMeanRgbByteError,
        int ReconstructionMaximumRgbByteError,
        double ReconstructionMeanRgbByteError);

    private sealed record CompositionEvidence(
        int SchemaVersion,
        string ArtifactKind,
        string AuthorityId,
        string AuthorityManifestSha256,
        string PresetSha256,
        string FullCompositeSha256,
        string BaseDiffuseSha256,
        string ConventionalFaceTintSha256,
        string PrivateDiffuseSha256,
        string PrivateDiffuseDestination,
        int Width,
        int Height,
        int ProtectedNeckStartRowInclusive,
        int ProtectedNeckEndRowInclusive,
        int MappedLayerCount,
        int BakedLayerCount,
        int CompositeModelMaximumRgbByteError,
        double CompositeModelMeanRgbByteError,
        int ReconstructionMaximumRgbByteError,
        double ReconstructionMeanRgbByteError,
        bool FaceTintOpaqueAlpha,
        bool PrivateAlphaMatchesBase,
        bool ProtectedNeckExact,
        bool IndependentlyDecoded,
        bool RuntimeAuthority);
}
