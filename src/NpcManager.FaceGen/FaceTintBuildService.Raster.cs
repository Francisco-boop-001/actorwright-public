using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class FaceTintBuildService
{
    private static FaceTintPixelProbe ComposeProbe((int X, int Y) coordinate, ManifestData manifest)
    {
        var color = ComposeColor(manifest);
        return new FaceTintPixelProbe(coordinate.X, coordinate.Y, color[0], color[1], color[2], color[3]);
    }

    private async ValueTask<RasterBuildResult> BuildRasterAsync(
        ManifestData manifest,
        WorkspacePath? providerRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var composedColor = ComposeColor(manifest);
        if (providerRoot is null)
            return new RasterBuildResult(true, CreateSolidPixels(manifest.Width, manifest.Height, composedColor), []);
        var decoder = textureDecoder;
        if (decoder is null)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-decoder-unavailable", DiagnosticSeverity.Error,
                "Provider-bound FaceTint sampling requires the pinned K-local DirectXTex decoder."));
            return RasterBuildResult.Failed;
        }
        var root = providerRoot.Value;

        var pixels = CreateSolidPixels(manifest.Width, manifest.Height, manifest.BaseColor);
        var bindings = ImmutableArray.CreateBuilder<FaceTintProviderBinding>();
        foreach (var layer in manifest.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveProviderSource(root, layer.Source, diagnostics);
            if (source is null) continue;
            var decoded = await decoder.DecodeAsync(source.Value, cancellationToken);
            diagnostics.AddRange(decoded.Diagnostics);
            if (!decoded.Decoded || decoded.Bytes is null || decoded.SourceSha256 is null)
            {
                diagnostics.Add(new Diagnostic("facetint-provider-source-decode-failed", DiagnosticSeverity.Error,
                    $"Provider source '{layer.Source}' could not be decoded."));
                continue;
            }
            if (decoded.Width != manifest.Width || decoded.Height != manifest.Height ||
                decoded.Bytes.Length != checked(manifest.Width * manifest.Height * 4))
            {
                diagnostics.Add(new Diagnostic("facetint-provider-source-dimensions", DiagnosticSeverity.Error,
                    $"Provider source '{layer.Source}' must match the {manifest.Width}x{manifest.Height} output raster."));
                continue;
            }
            ApplySampledLayer(pixels, decoded.Bytes, layer);
            bindings.Add(new FaceTintProviderBinding(layer.Source, decoded.SourceSha256.Value.Value,
                decoded.Width, decoded.Height));
        }
        if (manifest.AlphaMode == FaceTintAlphaMode.Opaque)
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return RasterBuildResult.Failed;
        return new RasterBuildResult(true, pixels, bindings.ToImmutable());
    }

    private static WorkspacePath? ResolveProviderSource(
        WorkspacePath providerRoot,
        string source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(source) || Path.IsPathRooted(source))
        {
            diagnostics.Add(new Diagnostic("facetint-provider-source-invalid", DiagnosticSeverity.Error,
                "Provider source paths must be non-empty relative DDS paths."));
            return null;
        }
        try
        {
            var normalized = source.Replace('/', Path.DirectorySeparatorChar);
            var path = new WorkspacePath(Path.GetFullPath(Path.Combine(providerRoot.Value, normalized)));
            if (!path.IsUnder(providerRoot) || !path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path.Value))
            {
                diagnostics.Add(new Diagnostic("facetint-provider-source-invalid", DiagnosticSeverity.Error,
                    $"Provider source '{source}' must be an existing DDS file under the provider root."));
                return null;
            }
            return path;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-source-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
        catch (NotSupportedException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-source-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
    }

    private static void ApplySampledLayer(
        byte[] target,
        byte[] source,
        FaceTintBuildLayer layer)
    {
        for (var offset = 0; offset < target.Length; offset += 4)
        {
            var sourceBlue = source[offset] / 255d * layer.Color[2];
            var sourceGreen = source[offset + 1] / 255d * layer.Color[1];
            var sourceRed = source[offset + 2] / 255d * layer.Color[0];
            var sourceAlpha = source[offset + 3] / 255d * layer.Color[3];
            var targetBlue = target[offset] / 255d;
            var targetGreen = target[offset + 1] / 255d;
            var targetRed = target[offset + 2] / 255d;
            var targetAlpha = target[offset + 3] / 255d;
            if (layer.Blend == FaceTintBlendMode.Replace)
            {
                target[offset] = Quantize(sourceBlue);
                target[offset + 1] = Quantize(sourceGreen);
                target[offset + 2] = Quantize(sourceRed);
                target[offset + 3] = Quantize(sourceAlpha);
                continue;
            }
            var opacity = layer.Opacity * sourceAlpha;
            var blue = layer.Blend switch
            {
                FaceTintBlendMode.Multiply => targetBlue * sourceBlue,
                FaceTintBlendMode.Add => Math.Min(1d, targetBlue + sourceBlue),
                _ => sourceBlue
            };
            var green = layer.Blend switch
            {
                FaceTintBlendMode.Multiply => targetGreen * sourceGreen,
                FaceTintBlendMode.Add => Math.Min(1d, targetGreen + sourceGreen),
                _ => sourceGreen
            };
            var red = layer.Blend switch
            {
                FaceTintBlendMode.Multiply => targetRed * sourceRed,
                FaceTintBlendMode.Add => Math.Min(1d, targetRed + sourceRed),
                _ => sourceRed
            };
            target[offset] = Quantize(blue * opacity + targetBlue * (1d - opacity));
            target[offset + 1] = Quantize(green * opacity + targetGreen * (1d - opacity));
            target[offset + 2] = Quantize(red * opacity + targetRed * (1d - opacity));
            target[offset + 3] = Quantize(opacity + targetAlpha * (1d - opacity));
        }
    }

    private static FaceTintPixelProbe ProbeFromRaster((int X, int Y) coordinate, int width, byte[] pixels)
    {
        var offset = checked((coordinate.Y * width + coordinate.X) * 4);
        return new FaceTintPixelProbe(coordinate.X, coordinate.Y,
            pixels[offset + 2] / 255d, pixels[offset + 1] / 255d,
            pixels[offset] / 255d, pixels[offset + 3] / 255d);
    }

    private static byte[] CreateSolidPixels(int width, int height, ImmutableArray<double> color)
    {
        var pixels = new byte[checked(width * height * 4)];
        var blue = Quantize(color[2]);
        var green = Quantize(color[1]);
        var red = Quantize(color[0]);
        var alpha = Quantize(color[3]);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
        return pixels;
    }

    private static ImmutableArray<double> ComposeColor(ManifestData manifest)
    {
        var result = manifest.BaseColor.ToArray();
        foreach (var layer in manifest.Layers)
        {
            var opacity = layer.Opacity * layer.Color[3];
            var source = layer.Color;
            var red = layer.Blend switch
            {
                FaceTintBlendMode.Multiply => result[0] * source[0],
                FaceTintBlendMode.Add => Math.Min(1d, result[0] + source[0]),
                _ => source[0]
            };
            var green = layer.Blend switch
            {
                FaceTintBlendMode.Multiply => result[1] * source[1],
                FaceTintBlendMode.Add => Math.Min(1d, result[1] + source[1]),
                _ => source[1]
            };
            var blue = layer.Blend switch
            {
                FaceTintBlendMode.Multiply => result[2] * source[2],
                FaceTintBlendMode.Add => Math.Min(1d, result[2] + source[2]),
                _ => source[2]
            };
            if (layer.Blend == FaceTintBlendMode.Replace)
            {
                result[0] = red; result[1] = green; result[2] = blue; result[3] = source[3];
                continue;
            }
            result[0] = red * opacity + result[0] * (1d - opacity);
            result[1] = green * opacity + result[1] * (1d - opacity);
            result[2] = blue * opacity + result[2] * (1d - opacity);
            result[3] = opacity + result[3] * (1d - opacity);
        }
        if (manifest.AlphaMode == FaceTintAlphaMode.Opaque) result[3] = 1d;
        return result.ToImmutableArray();
    }

}
