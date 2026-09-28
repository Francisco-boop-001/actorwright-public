using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimOverlaySource
{
    SkeeMask,
    FaceOverlay
}

public enum SkyrimOverlayBlendMode
{
    Normal,
    Multiply,
    Overlay,
    SoftLight,
    LinearDodge,
    LinearBurn,
    LinearLight,
    ColorDodge,
    ColorBurn,
    Darken,
    Lighten,
    Tint,
    Grayscale,
    ColorMode,
    Rnm,
    TextureMode
}

/// <summary>Normalized RGBA raster used by the bounded Skyrim fold manifest.
/// Color rasters are stored in their authored space; the fold service applies the
/// source's explicit sRGB/linear conversions and never guesses from a file name.</summary>
public sealed record SkyrimRgbaRaster(int Width, int Height, ImmutableArray<double> Pixels)
{
    public int PixelCount => checked(Width * Height);
}

public sealed record SkyrimOverlayLayerInput(
    SkyrimOverlaySource Source,
    string? Node,
    int LayerType,
    SkyrimOverlayBlendMode Blend,
    ImmutableArray<double> Color,
    double Opacity,
    SkyrimRgbaRaster? Texture,
    int SourceIndex);

public sealed record SkyrimOverlayFoldRequest(
    SkyrimRgbaRaster Base,
    ImmutableArray<SkyrimOverlayLayerInput> Layers,
    SkyrimRgbaRaster? Facetint,
    SkyrimRgbaRaster? Detail,
    Sha256Hash? SourceSha256 = null);

public sealed record SkyrimOverlayFoldResult(
    bool IsValid,
    SkyrimRgbaRaster? Output,
    byte[]? OutputDds,
    Sha256Hash? SourceSha256,
    Sha256Hash CanonicalSha256,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<SkyrimOverlayLayerInput> OrderedLayers)
{
    public bool IsSuccess => IsValid && Output is not null && OutputDds is not null;
}

public interface ISkyrimOverlayFoldService
{
    SkyrimOverlayFoldResult Fold(SkyrimOverlayFoldRequest request, CancellationToken cancellationToken);
}
