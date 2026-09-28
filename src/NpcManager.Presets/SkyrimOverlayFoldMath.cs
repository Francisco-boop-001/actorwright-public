using NpcManager.Application;

namespace NpcManager.Presets;

internal static class SkyrimOverlayFoldMath
{
    public const double FgTintAmplitude = 255d / 64d;
    public const double FgTintOffsetR = 1d / 255d;
    public const double FgTintOffsetG = 0d;
    public const double FgTintOffsetB = 1d / 255d;

    public static void FoldFacetint(double[] accumulator, SkyrimRgbaRaster facetint, SkyrimRgbaRaster? detail,
        CancellationToken cancellationToken)
    {
        for (var pixel = 0; pixel < facetint.PixelCount; pixel++)
        {
            if ((pixel & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var source = pixel * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var baseLinear = SrgbToLinear(accumulator[source + channel]);
                var detailValue = detail is null ? 0.5d : detail.Pixels[source + channel];
                var softLight = baseLinear * baseLinear + 2d * baseLinear * detailValue * (1d - baseLinear);
                var offset = channel switch { 0 => FgTintOffsetR, 1 => FgTintOffsetG, _ => FgTintOffsetB };
                var fgTint = (facetint.Pixels[source + channel] + offset) * FgTintAmplitude;
                accumulator[source + channel] = LinearToSrgb(softLight * fgTint);
            }
        }
    }

    public static void ApplyLayer(double[] accumulator, SkyrimOverlayLayerInput layer,
        CancellationToken cancellationToken)
    {
        var texture = layer.Texture?.Pixels;
        for (var pixel = 0; pixel < accumulator.Length / 4; pixel++)
        {
            if ((pixel & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var offset = pixel * 4;
            var tr = texture is null ? 1d : texture.Value[offset];
            var tg = texture is null ? 1d : texture.Value[offset + 1];
            var tb = texture is null ? 1d : texture.Value[offset + 2];
            var ta = texture is null ? 1d : texture.Value[offset + 3];
            var colorAlpha = layer.Color[3];
            double red;
            double green;
            double blue;
            double sourceAlpha;
            double coverage;
            switch (layer.Source)
            {
                case SkyrimOverlaySource.FaceOverlay:
                    red = tr * layer.Color[0];
                    green = tg * layer.Color[1];
                    blue = tb * layer.Color[2];
                    sourceAlpha = ta;
                    coverage = ta * layer.Opacity;
                    break;
                case SkyrimOverlaySource.SkeeMask when layer.LayerType == 1:
                    red = layer.Color[0];
                    green = layer.Color[1];
                    blue = layer.Color[2];
                    sourceAlpha = tr * colorAlpha;
                    coverage = tr * colorAlpha * layer.Opacity;
                    break;
                case SkyrimOverlaySource.SkeeMask when layer.LayerType == 2:
                    red = layer.Color[0];
                    green = layer.Color[1];
                    blue = layer.Color[2];
                    sourceAlpha = colorAlpha;
                    coverage = colorAlpha * layer.Opacity;
                    break;
                default:
                    red = tr * layer.Color[0];
                    green = tg * layer.Color[1];
                    blue = tb * layer.Color[2];
                    sourceAlpha = ta * colorAlpha;
                    coverage = ta * colorAlpha * layer.Opacity;
                    break;
            }

            coverage = Clamp01(coverage);
            if (coverage <= 0d) continue;
            if (layer.Source == SkyrimOverlaySource.FaceOverlay || layer.Blend is SkyrimOverlayBlendMode.Normal
                or SkyrimOverlayBlendMode.Rnm or SkyrimOverlayBlendMode.TextureMode)
            {
                accumulator[offset] = red * coverage + accumulator[offset] * (1d - coverage);
                accumulator[offset + 1] = green * coverage + accumulator[offset + 1] * (1d - coverage);
                accumulator[offset + 2] = blue * coverage + accumulator[offset + 2] * (1d - coverage);
                continue;
            }

            var sourceRed = sourceAlpha <= 0d ? 0d : Clamp01(red / sourceAlpha);
            var sourceGreen = sourceAlpha <= 0d ? 0d : Clamp01(green / sourceAlpha);
            var sourceBlue = sourceAlpha <= 0d ? 0d : Clamp01(blue / sourceAlpha);
            var blended = layer.Blend == SkyrimOverlayBlendMode.Grayscale
                ? Grayscale(accumulator[offset], accumulator[offset + 1], accumulator[offset + 2], sourceRed, sourceGreen, sourceBlue)
                : layer.Blend == SkyrimOverlayBlendMode.ColorMode
                    ? ColorMode(accumulator[offset], accumulator[offset + 1], accumulator[offset + 2], sourceRed, sourceGreen, sourceBlue)
                    : new[]
                    {
                        BlendChannel(layer.Blend, accumulator[offset], sourceRed),
                        BlendChannel(layer.Blend, accumulator[offset + 1], sourceGreen),
                        BlendChannel(layer.Blend, accumulator[offset + 2], sourceBlue)
                    };
            accumulator[offset] = accumulator[offset] * (1d - coverage) + blended[0] * coverage;
            accumulator[offset + 1] = accumulator[offset + 1] * (1d - coverage) + blended[1] * coverage;
            accumulator[offset + 2] = accumulator[offset + 2] * (1d - coverage) + blended[2] * coverage;
        }
    }

    public static double SrgbToLinear(double value)
    {
        value = Clamp01(value);
        return value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
    }

    public static double LinearToSrgb(double value)
    {
        value = Clamp01(value);
        return value <= 0.0031308d ? value * 12.92d : 1.055d * Math.Pow(value, 1d / 2.4d) - 0.055d;
    }

    private static double BlendChannel(SkyrimOverlayBlendMode mode, double destination, double source)
    {
        destination = Clamp01(destination);
        source = Clamp01(source);
        return mode switch
        {
            SkyrimOverlayBlendMode.Multiply => destination * source,
            SkyrimOverlayBlendMode.Overlay or SkyrimOverlayBlendMode.Tint =>
                destination >= 0.5d ? 1d - 2d * (1d - destination) * (1d - source) : 2d * destination * source,
            SkyrimOverlayBlendMode.SoftLight => (1d - 2d * source) * destination * destination + 2d * source * destination,
            SkyrimOverlayBlendMode.LinearDodge => Math.Min(1d, destination + source),
            SkyrimOverlayBlendMode.LinearBurn => Math.Max(0d, destination + source - 1d),
            SkyrimOverlayBlendMode.LinearLight => Clamp01(destination + 2d * source - 1d),
            SkyrimOverlayBlendMode.ColorDodge => source >= 1d ? 1d : Math.Min(1d, destination / (1d - source)),
            SkyrimOverlayBlendMode.ColorBurn => source <= 0d ? 0d : 1d - Math.Min(1d, (1d - destination) / source),
            SkyrimOverlayBlendMode.Darken => Math.Min(destination, source),
            SkyrimOverlayBlendMode.Lighten => Math.Max(destination, source),
            _ => source
        };
    }

    private static double[] Grayscale(double r, double g, double b, double sr, double sg, double sb)
    {
        var luminance = 0.299d * r + 0.587d * g + 0.114d * b;
        return [luminance * sr, luminance * sg, luminance * sb];
    }

    private static double[] ColorMode(double r, double g, double b, double sr, double sg, double sb)
    {
        var hsv = RgbToHsv(sr, sg, sb);
        return HsvToRgb(hsv[0], hsv[1], Math.Max(r, Math.Max(g, b)));
    }

    private static double[] RgbToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = 0d;
        if (delta > 1e-12)
        {
            hue = max == r ? ((g - b) / delta) % 6d : max == g ? (b - r) / delta + 2d : (r - g) / delta + 4d;
            hue /= 6d;
            if (hue < 0d) hue += 1d;
        }
        return [hue, max <= 0d ? 0d : delta / max, max];
    }

    private static double[] HsvToRgb(double hue, double saturation, double value)
    {
        static double Channel(double h) => Clamp01(Math.Abs(h % 6d - 3d) - 1d);
        var r = Channel(hue * 6d);
        var g = Channel(hue * 6d + 4d);
        var b = Channel(hue * 6d + 2d);
        return [value * (1d + saturation * (r - 1d)), value * (1d + saturation * (g - 1d)), value * (1d + saturation * (b - 1d))];
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);
}
