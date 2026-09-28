using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Deterministic CPU implementation of the pinned Skyrim overlay fold.
/// It keeps the upstream sequence explicit: optional facetint fold, skee masks by
/// layer index, then Face [OvlN] decals by node index. No provider lookup or runtime
/// rendering is hidden in this service; the CLI supplies already-resolved rasters.</summary>
public sealed class SkyrimOverlayFoldService : ISkyrimOverlayFoldService
{
    private const int MaxLayers = 256;
    private const int MaxDimension = 4096;
    private const long MaxPixels = 4_194_304;
    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    public SkyrimOverlayFoldResult Fold(SkyrimOverlayFoldRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRaster(request.Base, "$.base", diagnostics);
        if (request.Layers.Length > MaxLayers)
            diagnostics.Add(new Diagnostic("sse-fold-layer-limit", DiagnosticSeverity.Error,
                $"A Skyrim overlay fold may contain at most {MaxLayers} layers."));
        ValidateOptionalRaster(request.Facetint, request.Base, "$.facetint", diagnostics);
        ValidateOptionalRaster(request.Detail, request.Base, "$.detail", diagnostics);

        var seenSourceIndexes = new HashSet<int>();
        foreach (var layer in request.Layers)
            ValidateLayer(layer, request.Base, seenSourceIndexes, diagnostics);

        var canonical = CanonicalJson(request);
        var canonicalHash = Hash(canonical);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SkyrimOverlayFoldResult(false, null, null, request.SourceSha256, canonicalHash, null,
                diagnostics.ToImmutable(), []);

        cancellationToken.ThrowIfCancellationRequested();
        var ordered = OrderLayers(request.Layers);
        var pixels = request.Base.Pixels.ToArray();
        if (request.Facetint is not null)
            SkyrimOverlayFoldMath.FoldFacetint(pixels, request.Facetint, request.Detail, cancellationToken);
        foreach (var layer in ordered)
            SkyrimOverlayFoldMath.ApplyLayer(pixels, layer, cancellationToken);

        var output = new SkyrimRgbaRaster(request.Base.Width, request.Base.Height, pixels.ToImmutableArray());
        var outputDds = SkyrimOverlayDdsCodec.Encode(output);
        return new SkyrimOverlayFoldResult(true, output, outputDds, request.SourceSha256, canonicalHash,
            Hash(outputDds), diagnostics.ToImmutable(), ordered);
    }

    private static ImmutableArray<SkyrimOverlayLayerInput> OrderLayers(
        ImmutableArray<SkyrimOverlayLayerInput> layers) =>
        layers.Where(item => item.Source == SkyrimOverlaySource.SkeeMask).OrderBy(item => item.SourceIndex)
            .Concat(layers.Where(item => item.Source == SkyrimOverlaySource.FaceOverlay)
                .OrderBy(item => ParseFaceNodeIndex(item.Node!)).ThenBy(item => item.SourceIndex))
            .ToImmutableArray();

    private static void ValidateLayer(SkyrimOverlayLayerInput layer, SkyrimRgbaRaster? baseRaster,
        HashSet<int> seenSourceIndexes, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var path = $"$.layers[{layer.SourceIndex}]";
        if (!Enum.IsDefined(layer.Source))
            diagnostics.Add(new Diagnostic("sse-fold-source-invalid", DiagnosticSeverity.Error,
                $"{path}.source is not a supported Skyrim overlay source."));
        if (layer.SourceIndex < 0 || !seenSourceIndexes.Add(layer.SourceIndex))
            diagnostics.Add(new Diagnostic("sse-fold-source-index-invalid", DiagnosticSeverity.Error,
                $"{path}.sourceIndex must be unique and non-negative."));
        if (layer.Color.IsDefaultOrEmpty || layer.Color.Length != 4 || layer.Color.Any(value => !FiniteUnit(value)))
            diagnostics.Add(new Diagnostic("sse-fold-color-invalid", DiagnosticSeverity.Error,
                $"{path}.color must contain four finite values from 0 to 1."));
        if (!FiniteUnit(layer.Opacity))
            diagnostics.Add(new Diagnostic("sse-fold-opacity-invalid", DiagnosticSeverity.Error,
                $"{path}.opacity must be finite and from 0 to 1."));

        if (layer.Source == SkyrimOverlaySource.FaceOverlay)
        {
            if (layer.LayerType != 0)
                diagnostics.Add(new Diagnostic("sse-fold-face-layer-type-invalid", DiagnosticSeverity.Error,
                    $"{path}.layerType is not valid for a RaceMenu Face overlay."));
            if (ParseFaceNodeIndex(layer.Node) < 0)
                diagnostics.Add(new Diagnostic("sse-fold-face-node-invalid", DiagnosticSeverity.Error,
                    $"{path}.node must use the exact Face [OvlN] form with N from 0 to 127."));
            if (layer.Blend != SkyrimOverlayBlendMode.Normal)
                diagnostics.Add(new Diagnostic("sse-fold-face-blend-invalid", DiagnosticSeverity.Error,
                    $"{path}.blend must be normal for a RaceMenu Face overlay."));
            ValidateTexture(layer.Texture, baseRaster, path, diagnostics, required: true);
            return;
        }

        if (layer.Node is not null)
            diagnostics.Add(new Diagnostic("sse-fold-skee-node-invalid", DiagnosticSeverity.Error,
                $"{path}.node is only valid for a Face overlay."));
        if (!Enum.IsDefined(layer.Blend))
            diagnostics.Add(new Diagnostic("sse-fold-blend-invalid", DiagnosticSeverity.Error,
                $"{path}.blend is not a supported Skyrim blend mode."));
        if (layer.LayerType is < 0 or > 2)
            diagnostics.Add(new Diagnostic("sse-fold-layer-type-invalid", DiagnosticSeverity.Error,
                $"{path}.layerType must be 0 (texture), 1 (mask), or 2 (solid)."));
        ValidateTexture(layer.Texture, baseRaster, path, diagnostics, required: layer.LayerType != 2);
        if (layer.LayerType == 2 && layer.Texture is not null)
            diagnostics.Add(new Diagnostic("sse-fold-solid-texture-invalid", DiagnosticSeverity.Error,
                $"{path}.texture must be omitted for a solid layer."));
    }

    private static int ParseFaceNodeIndex(string? node)
    {
        if (string.IsNullOrWhiteSpace(node)) return -1;
        var value = node.Trim();
        const string marker = "Face [Ovl";
        if (!value.StartsWith(marker, StringComparison.OrdinalIgnoreCase) || !value.EndsWith(']')) return -1;
        var digits = value[marker.Length..^1];
        return digits.Length is > 0 and <= 3 && int.TryParse(digits, out var index) && index is >= 0 and <= 127
            ? index : -1;
    }

    private static void ValidateTexture(SkyrimRgbaRaster? texture, SkyrimRgbaRaster? baseRaster, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, bool required)
    {
        if (texture is null)
        {
            if (required) diagnostics.Add(new Diagnostic("sse-fold-texture-required", DiagnosticSeverity.Error,
                $"{path}.texture is required for this layer."));
            return;
        }

        ValidateRaster(texture, $"{path}.texture", diagnostics);
        if (baseRaster is not null && (texture.Width != baseRaster.Width || texture.Height != baseRaster.Height))
            diagnostics.Add(new Diagnostic("sse-fold-dimension-mismatch", DiagnosticSeverity.Error,
                $"{path}.texture must match the base raster dimensions."));
    }

    private static void ValidateOptionalRaster(SkyrimRgbaRaster? raster, SkyrimRgbaRaster? baseRaster, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (raster is null) return;
        ValidateRaster(raster, path, diagnostics);
        if (baseRaster is not null && (raster.Width != baseRaster.Width || raster.Height != baseRaster.Height))
            diagnostics.Add(new Diagnostic("sse-fold-dimension-mismatch", DiagnosticSeverity.Error,
                $"{path} must match the base raster dimensions."));
    }

    private static void ValidateRaster(SkyrimRgbaRaster? raster, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (raster is null || raster.Width is <= 0 or > MaxDimension || raster.Height is <= 0 or > MaxDimension)
        {
            diagnostics.Add(new Diagnostic("sse-fold-raster-dimensions", DiagnosticSeverity.Error,
                $"{path} dimensions must be positive and at most {MaxDimension} per side."));
            return;
        }

        long pixels;
        try { pixels = checked((long)raster.Width * raster.Height); }
        catch (OverflowException)
        {
            diagnostics.Add(new Diagnostic("sse-fold-raster-too-large", DiagnosticSeverity.Error,
                $"{path} dimensions overflow the raster limit."));
            return;
        }
        if (pixels > MaxPixels || raster.Pixels.IsDefaultOrEmpty || raster.Pixels.Length != pixels * 4)
            diagnostics.Add(new Diagnostic("sse-fold-raster-shape", DiagnosticSeverity.Error,
                $"{path}.pixels must contain exactly width*height*4 values and remain within the raster limit."));
        else if (raster.Pixels.Any(value => !FiniteUnit(value)))
            diagnostics.Add(new Diagnostic("sse-fold-raster-value", DiagnosticSeverity.Error,
                $"{path}.pixels must contain only finite values from 0 to 1."));
    }

    private static bool FiniteUnit(double value) => double.IsFinite(value) && value is >= 0d and <= 1d;

    private static byte[] CanonicalJson(SkyrimOverlayFoldRequest request)
    {
        var payload = new
        {
            schemaVersion = 1,
            @base = CanonicalRaster(request.Base),
            facetint = request.Facetint is null ? null : CanonicalRaster(request.Facetint),
            detail = request.Detail is null ? null : CanonicalRaster(request.Detail),
            layers = request.Layers.Select(layer => new
            {
                source = layer.Source.ToString(),
                node = layer.Node,
                layerType = layer.LayerType,
                blend = layer.Blend.ToString(),
                color = layer.Color.ToArray(),
                opacity = layer.Opacity,
                texture = layer.Texture is null ? null : CanonicalRaster(layer.Texture),
                sourceIndex = layer.SourceIndex
            }).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalOptions);
    }

    private static CanonicalRasterPayload? CanonicalRaster(SkyrimRgbaRaster? raster) => raster is null
        ? null
        : new CanonicalRasterPayload(raster.Width, raster.Height, raster.Pixels.ToArray());

    private sealed record CanonicalRasterPayload(int Width, int Height, double[] Pixels);

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
