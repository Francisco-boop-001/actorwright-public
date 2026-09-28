using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceTintOutputFormat
{
    Bgra8,
    Bc3,
    Bc7
}

public enum FaceTintAlphaMode
{
    Preserve,
    Opaque
}

public enum FaceTintBlendMode
{
    Replace,
    Over,
    Multiply,
    Add
}

/// <summary>One ordered, already-resolved tint input in the semantic build manifest.</summary>
public sealed record FaceTintBuildLayer(
    string Name,
    string Source,
    string Provider,
    FaceTintBlendMode Blend,
    double Opacity,
    ImmutableArray<double> Color);

/// <summary>A deterministic probe of the composited semantic raster.</summary>
public sealed record FaceTintPixelProbe(
    int X,
    int Y,
    double Red,
    double Green,
    double Blue,
    double Alpha);

/// <summary>
/// A reproducible FaceTint build description. This JSON records the exact raster
/// contract, probes, optional provider source bindings, and optional DDS output
/// hashes. Live game/plugin provider resolution remains a separate authority.
/// </summary>
public sealed record FaceTintBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string InputManifestSha256,
    int Width,
    int Height,
    string Format,
    int MipCount,
    string AlphaMode,
    string ChannelOrder,
    ImmutableArray<double> BaseColor,
    ImmutableArray<FaceTintBuildLayer> OrderedLayers,
    ImmutableArray<FaceTintPixelProbe> Probes,
    string SemanticRasterSha256,
    string? TextureOutputPath = null,
    string? TextureDdsSha256 = null,
    string RasterSource = "uniform",
    ImmutableArray<FaceTintProviderBinding>? ProviderSources = null);

public sealed record FaceTintBuildRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    WorkspacePath OutputPath,
    FormId? NpcFormId,
    int? Resolution,
    FaceTintOutputFormat? Format,
    int? MipCount,
    FaceTintAlphaMode? AlphaMode,
    WorkspacePath? TextureOutputPath = null,
    WorkspacePath? ProviderRoot = null);

public sealed record FaceTintBuildResult(
    bool Written,
    FaceTintBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics,
    Sha256Hash? TextureOutputSha256 = null);

public sealed record FaceTintTextureEncodeResult(
    bool Encoded,
    byte[]? Bytes,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>A decoded, provider-source BGRA8 raster and its source binding.</summary>
public sealed record FaceTintTextureDecodeResult(
    bool Decoded,
    int Width,
    int Height,
    byte[]? Bytes,
    Sha256Hash? SourceSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record FaceTintProviderBinding(
    string Source,
    string Sha256,
    int Width,
    int Height);

/// <summary>Encodes a K-local uncompressed DDS into a validated compressed FaceTint texture.</summary>
public interface IFaceTintTextureEncoder
{
    ValueTask<FaceTintTextureEncodeResult> EncodeAsync(
        FaceTintOutputFormat format,
        WorkspacePath sourceDds,
        CancellationToken cancellationToken);
}

/// <summary>Decodes a K-local provider DDS into a validated BGRA8 raster.</summary>
public interface IFaceTintTextureDecoder
{
    ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        WorkspacePath sourceDds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Decodes already-resolved DDS content without requiring an intermediate file.
/// This is the archive-member seam used by native Skyrim FaceTint composition.
/// </summary>
public interface IFaceTintTextureContentDecoder
{
    ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        ImmutableArray<byte> sourceDds,
        CancellationToken cancellationToken);
}

public interface IFaceTintBuildService
{
    ValueTask<FaceTintBuildResult> BuildAsync(FaceTintBuildRequest request, CancellationToken cancellationToken);
}
