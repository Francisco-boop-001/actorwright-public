using System.Runtime.InteropServices;
using SkiaSharp;

namespace NpcManager.Infrastructure;

public readonly record struct FaceGeomHairRegionsPngInspection(
    int Width,
    int Height,
    long NonEmptyPixelCount);

/// <summary>
/// Independently decodes a declared preview PNG and derives its non-empty
/// pixel count from canonical RGBA bytes. Renderer-supplied pixel metadata is
/// never accepted as proof by itself.
/// </summary>
public static class FaceGeomHairRegionsPreviewArtifactInspector
{
    private const int MaximumDimension = 16_384;
    private const long MaximumDecodedBytes = 512L * 1024L * 1024L;

    public static long CountNonEmptyPngPixels(
        ReadOnlyMemory<byte> encoded) =>
        InspectPng(encoded).NonEmptyPixelCount;

    public static FaceGeomHairRegionsPngInspection InspectPng(
        ReadOnlyMemory<byte> encoded)
    {
        if (encoded.IsEmpty ||
            encoded.Length > 128 * 1024 * 1024)
            throw new InvalidDataException(
                "Preview PNG encoded length is outside the accepted range.");
        try
        {
            using SKData data = SKData.CreateCopy(encoded.Span);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null ||
                codec.EncodedFormat != SKEncodedImageFormat.Png ||
                codec.FrameCount > 1)
                throw new InvalidDataException(
                    "Preview image proof must be one non-animated PNG.");
            int width = codec.Info.Width;
            int height = codec.Info.Height;
            if (width is <= 0 or > MaximumDimension ||
                height is <= 0 or > MaximumDimension)
                throw new InvalidDataException(
                    "Preview PNG dimensions are outside the accepted range.");
            long decodedLength = checked(
                checked((long)width * height) * 4L);
            if (decodedLength > MaximumDecodedBytes ||
                decodedLength > int.MaxValue)
                throw new InvalidDataException(
                    "Preview PNG decoded bytes exceed the accepted limit.");

            using SKColorSpace colorSpace = SKColorSpace.CreateSrgb();
            var info = new SKImageInfo(
                width,
                height,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul,
                colorSpace);
            var rgba = new byte[checked((int)decodedLength)];
            SKCodecResult result = codec.GetPixels(info, rgba);
            if (result != SKCodecResult.Success)
                throw new InvalidDataException(
                    $"Preview PNG did not decode completely ({result}).");
            long nonEmpty = 0;
            for (int offset = 0; offset < rgba.Length; offset += 4)
            {
                if (rgba[offset] != 0 ||
                    rgba[offset + 1] != 0 ||
                    rgba[offset + 2] != 0)
                    nonEmpty++;
            }
            return new FaceGeomHairRegionsPngInspection(
                width,
                height,
                nonEmpty);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                ExternalException or
                DllNotFoundException or
                EntryPointNotFoundException or
                TypeInitializationException)
        {
            throw new InvalidDataException(
                "Preview PNG could not be decoded safely.",
                exception);
        }
    }
}
