using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class RaceMenuNpcFaceTextureBuildService
{
    private const uint DdsMagic = 0x2053_4444;
    private const uint DdsHeaderSize = 124;

    private async ValueTask<CompositionOutcome?> ComposeAsync(
        FaceTextureAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var fullComposite = await DecodeBoundSourceAsync(authority.FullComposite,
            "full composite", diagnostics, cancellationToken);
        var baseDiffuse = await DecodeBoundSourceAsync(authority.BaseDiffuse,
            "base diffuse", diagnostics, cancellationToken);
        if (fullComposite is null || baseDiffuse is null || HasErrors(diagnostics))
            return null;
        if (!HasOpaqueAlpha(fullComposite, cancellationToken))
        {
            diagnostics.Add(Error("face-texture-source-alpha",
                "The raw full-composite FaceTint must have opaque alpha."));
            return null;
        }

        var pixelCount = checked(authority.FullComposite.Width * authority.FullComposite.Height);
        var conventional = new float[checked(pixelCount * 3)];
        var fullModel = new float[checked(pixelCount * 3)];
        var activePosition = 0;
        var mappedCount = 0;
        var bakedCount = 0;

        foreach (var layer in authority.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.Disposition == RaceMenuNpcTintDispositionKind.Inactive)
                continue;

            var mask = await DecodeWithinCompositionBudgetAsync(layer.Source!.Value,
                $"tint row {layer.JslotIndex}", diagnostics, cancellationToken);
            if (mask is null || !mask.Decoded || mask.Bytes is null ||
                mask.SourceSha256 != layer.SourceSha256)
            {
                if (!HasErrors(diagnostics))
                    diagnostics.Add(Error("face-texture-mask-hash",
                        $"Tint row {layer.JslotIndex} did not decode with its exact provider hash."));
                return null;
            }

            var alpha = ((layer.PresetColor >> 24) & 0xFF) / 255F;
            var red = ((layer.PresetColor >> 16) & 0xFF) / 255F;
            var green = ((layer.PresetColor >> 8) & 0xFF) / 255F;
            var blue = (layer.PresetColor & 0xFF) / 255F;
            if (activePosition++ == 0)
            {
                FillRgb(conventional, red, green, blue, cancellationToken);
                FillRgb(fullModel, red, green, blue, cancellationToken);
            }
            else
            {
                BlendMask(fullModel, authority.FullComposite.Width,
                    authority.FullComposite.Height, mask.Bytes, mask.Width, mask.Height,
                    alpha, red, green, blue, cancellationToken);
                if (layer.Disposition == RaceMenuNpcTintDispositionKind.MappedRecord)
                {
                    BlendMask(conventional, authority.FullComposite.Width,
                        authority.FullComposite.Height, mask.Bytes, mask.Width, mask.Height,
                        alpha, red, green, blue, cancellationToken);
                }
            }

            if (layer.Disposition == RaceMenuNpcTintDispositionKind.MappedRecord)
                mappedCount++;
            else
                bakedCount++;
        }

        var conventionalBgra = QuantizeFloorBgra(conventional, cancellationToken);
        var fullModelBgra = QuantizeFloorBgra(fullModel, cancellationToken);
        var compositeModelError = MeasureRgbError(fullModelBgra, fullComposite,
            cancellationToken);
        if (compositeModelError.Maximum > authority.Tolerances.TintModel.MaximumRgbByteError ||
            compositeModelError.Mean > authority.Tolerances.TintModel.MaximumMeanRgbByteError)
        {
            diagnostics.Add(Error("face-texture-composite-model-mismatch",
                $"Resolved tint providers reconstruct the raw composite with max/mean RGB error " +
                $"{compositeModelError.Maximum}/{compositeModelError.Mean:F9}, exceeding " +
                $"{authority.Tolerances.TintModel.MaximumRgbByteError}/" +
                $"{authority.Tolerances.TintModel.MaximumMeanRgbByteError:F9}."));
            return null;
        }

        var privateDiffuse = BuildPrivateDiffuse(baseDiffuse, fullComposite,
            conventionalBgra, authority.ProtectedNeck, authority.FullComposite.Width,
            authority.FullComposite.Height, diagnostics, cancellationToken);
        if (privateDiffuse is null) return null;
        var reconstructionError = MeasureShaderReconstruction(privateDiffuse,
            conventionalBgra, baseDiffuse, fullComposite, cancellationToken);
        if (reconstructionError.Maximum > authority.Tolerances.SplitShader.MaximumRgbByteError ||
            reconstructionError.Mean > authority.Tolerances.SplitShader.MaximumMeanRgbByteError)
        {
            diagnostics.Add(Error("face-texture-reconstruction-mismatch",
                $"Private diffuse and conventional FaceTint reconstruct the raw composite with " +
                $"max/mean RGB error {reconstructionError.Maximum}/{reconstructionError.Mean:F9}, " +
                $"exceeding {authority.Tolerances.SplitShader.MaximumRgbByteError}/" +
                $"{authority.Tolerances.SplitShader.MaximumMeanRgbByteError:F9}."));
            return null;
        }

        return new CompositionOutcome(fullComposite, baseDiffuse, conventionalBgra,
            privateDiffuse, mappedCount, bakedCount, compositeModelError.Maximum,
            compositeModelError.Mean, reconstructionError.Maximum,
            reconstructionError.Mean);
    }

    private async ValueTask<byte[]?> DecodeBoundSourceAsync(
        SourceAuthority source,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var decoded = await DecodeWithinCompositionBudgetAsync(source.Path, role,
            diagnostics, cancellationToken);
        if (decoded is null || !decoded.Decoded || decoded.Bytes is null ||
            decoded.SourceSha256 != source.Sha256 ||
            decoded.Width != source.Width || decoded.Height != source.Height)
        {
            if (!HasErrors(diagnostics))
                diagnostics.Add(Error("face-texture-source-binding",
                    $"The decoded {role} does not match its exact hash and dimensions."));
            return null;
        }
        return decoded.Bytes;
    }

    private async ValueTask<FaceTintTextureDecodeResult?>
        DecodeWithinCompositionBudgetAsync(
            WorkspacePath source,
            string role,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (!WindowsPinnedPath.TryOpenFile(source.Value, false, out var pinned,
                out _, out var openError) || pinned is null)
        {
            diagnostics.Add(Error("face-texture-source-preflight-open",
                $"The {role} DDS could not be identity-pinned before decode: {openError}"));
            return null;
        }

        using (pinned)
        {
            var prefix = await pinned.ReadPrefixAsync(20, cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0, 4)) != DdsMagic ||
                BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4, 4)) != DdsHeaderSize)
            {
                diagnostics.Add(Error("face-texture-source-preflight-header",
                    $"The {role} input does not contain an admitted DDS header."));
                return null;
            }

            var height = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12, 4));
            var width = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(16, 4));
            var pixels = (ulong)width * height;
            if (width == 0 || height == 0 ||
                width > RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels ||
                height > RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels ||
                pixels > RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels)
            {
                diagnostics.Add(Error("face-texture-source-pixel-budget",
                    $"The {role} DDS raster {width}x{height} exceeds the product " +
                    $"composition budget of " +
                    $"{RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels} pixels."));
                return null;
            }

            var decoded = await sourceDecoder.DecodeAsync(source, cancellationToken);
            diagnostics.AddRange(decoded.Diagnostics);
            if (decoded.Decoded && decoded.Bytes is { } bytes &&
                ((long)decoded.Width * decoded.Height >
                    RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels ||
                 bytes.LongLength >
                    (long)RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels * 4))
            {
                diagnostics.Add(Error("face-texture-source-decoded-pixel-budget",
                    $"The decoded {role} raster exceeded the product composition budget."));
                return null;
            }
            return decoded;
        }
    }

    private static void FillRgb(
        float[] target,
        float red,
        float green,
        float blue,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < target.Length; offset += 3)
        {
            if ((offset & 0x3F_FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            target[offset] = red;
            target[offset + 1] = green;
            target[offset + 2] = blue;
        }
    }

    private static void BlendMask(
        float[] target,
        int width,
        int height,
        byte[] maskBgra,
        int maskWidth,
        int maskHeight,
        float alpha,
        float red,
        float green,
        float blue,
        CancellationToken cancellationToken)
    {
        if (maskBgra.Length != checked(maskWidth * maskHeight * 4))
            throw new InvalidDataException("Decoded tint-mask byte count is inconsistent.");
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var maskRed = SampleRedBilinear(maskBgra, maskWidth, maskHeight,
                    x, y, width, height);
                var effective = maskRed / 255F * alpha;
                var inverse = 1F - effective;
                var offset = checked((y * width + x) * 3);
                target[offset] = target[offset] * inverse + red * effective;
                target[offset + 1] = target[offset + 1] * inverse + green * effective;
                target[offset + 2] = target[offset + 2] * inverse + blue * effective;
            }
        }
    }

    private static byte SampleRedBilinear(
        byte[] bgra,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
            return bgra[checked((y * sourceWidth + x) * 4 + 2)];

        var sourceX = (x + 0.5D) * sourceWidth / destinationWidth - 0.5D;
        var sourceY = (y + 0.5D) * sourceHeight / destinationHeight - 0.5D;
        var x0 = (int)Math.Floor(sourceX);
        var y0 = (int)Math.Floor(sourceY);
        var xWeight = sourceX - x0;
        var yWeight = sourceY - y0;
        if (x0 < 0) { x0 = 0; xWeight = 0D; }
        if (y0 < 0) { y0 = 0; yWeight = 0D; }
        var x1 = Math.Min(x0 + 1, sourceWidth - 1);
        var y1 = Math.Min(y0 + 1, sourceHeight - 1);
        if (x0 >= sourceWidth - 1) { x0 = sourceWidth - 1; x1 = x0; xWeight = 0D; }
        if (y0 >= sourceHeight - 1) { y0 = sourceHeight - 1; y1 = y0; yWeight = 0D; }
        var topLeft = bgra[checked((y0 * sourceWidth + x0) * 4 + 2)];
        var topRight = bgra[checked((y0 * sourceWidth + x1) * 4 + 2)];
        var bottomLeft = bgra[checked((y1 * sourceWidth + x0) * 4 + 2)];
        var bottomRight = bgra[checked((y1 * sourceWidth + x1) * 4 + 2)];
        var top = topLeft * (1D - xWeight) + topRight * xWeight;
        var bottom = bottomLeft * (1D - xWeight) + bottomRight * xWeight;
        return (byte)Math.Clamp(Math.Round(top * (1D - yWeight) + bottom * yWeight,
            MidpointRounding.ToEven), 0D, 255D);
    }

    private static byte[] QuantizeFloorBgra(
        float[] rgb,
        CancellationToken cancellationToken)
    {
        var pixels = new byte[checked(rgb.Length / 3 * 4)];
        for (var source = 0; source < rgb.Length; source += 3)
        {
            if ((source & 0x3F_FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var destination = source / 3 * 4;
            pixels[destination] = FloorByte(rgb[source + 2]);
            pixels[destination + 1] = FloorByte(rgb[source + 1]);
            pixels[destination + 2] = FloorByte(rgb[source]);
            pixels[destination + 3] = byte.MaxValue;
        }
        return pixels;
    }

    private static byte FloorByte(float value) =>
        (byte)Math.Clamp(MathF.Floor(value * 255F), 0F, 255F);

    private static byte[]? BuildPrivateDiffuse(
        byte[] baseDiffuse,
        byte[] fullComposite,
        byte[] conventionalFaceTint,
        ProtectedNeckAuthority neck,
        int width,
        int height,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var expectedLength = checked(width * height * 4);
        if (baseDiffuse.Length != expectedLength || fullComposite.Length != expectedLength ||
            conventionalFaceTint.Length != expectedLength)
            throw new InvalidDataException("Face-texture raster lengths are inconsistent.");
        var result = new byte[expectedLength];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var protectedRow = y >= neck.StartRowInclusive && y <= neck.EndRowInclusive;
            for (var x = 0; x < width; x++)
            {
                var offset = checked((y * width + x) * 4);
                if (protectedRow)
                {
                    Buffer.BlockCopy(baseDiffuse, offset, result, offset, 4);
                    continue;
                }
                for (var channel = 0; channel < 3; channel++)
                {
                    var conventional = conventionalFaceTint[offset + channel];
                    if (conventional == 0)
                    {
                        diagnostics.Add(Error("face-texture-zero-conventional-channel",
                            "The conventional FaceTint contains a zero RGB channel and cannot define a stable residual diffuse."));
                        return null;
                    }
                    var ratio = (float)fullComposite[offset + channel] / conventional;
                    var value = (float)baseDiffuse[offset + channel] * ratio;
                    result[offset + channel] = (byte)Math.Clamp(
                        MathF.Round(value, MidpointRounding.ToEven), 0F, 255F);
                }
                result[offset + 3] = baseDiffuse[offset + 3];
            }
        }
        return result;
    }

    private static (int Maximum, double Mean) MeasureRgbError(
        byte[] actual,
        byte[] expected,
        CancellationToken cancellationToken)
    {
        if (actual.Length != expected.Length || (actual.Length & 3) != 0)
            throw new InvalidDataException("RGB error rasters are inconsistent.");
        long total = 0;
        var maximum = 0;
        long samples = 0;
        for (var offset = 0; offset < actual.Length; offset += 4)
        {
            if ((offset & 0x3F_FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < 3; channel++)
            {
                var error = Math.Abs(actual[offset + channel] - expected[offset + channel]);
                maximum = Math.Max(maximum, error);
                total += error;
                samples++;
            }
        }
        return (maximum, total / (double)samples);
    }

    private static (int Maximum, double Mean) MeasureShaderReconstruction(
        byte[] privateDiffuse,
        byte[] conventionalFaceTint,
        byte[] baseDiffuse,
        byte[] fullComposite,
        CancellationToken cancellationToken)
    {
        if (privateDiffuse.Length != conventionalFaceTint.Length ||
            privateDiffuse.Length != baseDiffuse.Length ||
            privateDiffuse.Length != fullComposite.Length ||
            (privateDiffuse.Length & 3) != 0)
            throw new InvalidDataException("Shader reconstruction rasters are inconsistent.");
        long total = 0;
        var maximum = 0;
        long samples = 0;
        for (var offset = 0; offset < privateDiffuse.Length; offset += 4)
        {
            if ((offset & 0x3F_FFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < 3; channel++)
            {
                var reconstructed = (int)Math.Clamp(MathF.Round(
                    2F * privateDiffuse[offset + channel] *
                    conventionalFaceTint[offset + channel] / 255F,
                    MidpointRounding.ToEven), 0F, 255F);
                var reference = (int)Math.Clamp(MathF.Round(
                    2F * baseDiffuse[offset + channel] *
                    fullComposite[offset + channel] / 255F,
                    MidpointRounding.ToEven), 0F, 255F);
                var error = Math.Abs(reconstructed - reference);
                maximum = Math.Max(maximum, error);
                total += error;
                samples++;
            }
        }
        return (maximum, total / (double)samples);
    }

    private static bool HasOpaqueAlpha(
        byte[] bgra,
        CancellationToken cancellationToken)
    {
        for (var offset = 3; offset < bgra.Length; offset += 4)
        {
            if ((offset & 0x3F_FFFF) == 3) cancellationToken.ThrowIfCancellationRequested();
            if (bgra[offset] != byte.MaxValue) return false;
        }
        return true;
    }
}
