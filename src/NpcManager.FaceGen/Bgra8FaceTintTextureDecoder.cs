using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Reads a single-mip, uncompressed 32-bit DDS without launching an external
/// codec and returns the BGRA8 byte layout required by the FaceTint pipeline.
/// RaceMenu commonly exports RGBA8, which is converted in-process. Compressed
/// provider textures remain outside this deliberately narrow boundary.
/// </summary>
public sealed class Bgra8FaceTintTextureDecoder(WorkspacePath labRoot)
    : IFaceTintTextureDecoder
{
    private const long MaxSourceBytes = 64L * 1024 * 1024;
    private const uint DdsMagic = 0x2053_4444;
    private const uint DdsHeaderSize = 124;
    private const uint PixelFormatSize = 32;
    private const uint Bgra8PixelFormatFlags = 0x41;
    private const uint Bgra8RgbBitCount = 32;
    private const uint Bgra8RedMask = 0x00FF_0000;
    private const uint Bgra8GreenMask = 0x0000_FF00;
    private const uint Bgra8BlueMask = 0x0000_00FF;
    private const uint Bgra8AlphaMask = 0xFF00_0000;
    private const uint Rgba8RedMask = 0x0000_00FF;
    private const uint Rgba8GreenMask = 0x0000_FF00;
    private const uint Rgba8BlueMask = 0x00FF_0000;
    private const uint MaxDimension = 8192;

    public async ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        WorkspacePath sourceDds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!sourceDds.IsUnder(labRoot) ||
            !sourceDds.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("facetint-bgra8-source-invalid",
                "The FaceTint source must be a K-local DDS file."));
            return Refused(diagnostics);
        }

        if (!WindowsPinnedPath.TryOpenFile(sourceDds.Value, false, out var source,
                out _, out var openError) || source is null)
        {
            diagnostics.Add(Error("facetint-bgra8-source-open",
                $"The FaceTint source is not an ordinary identity-pinned file: {openError}"));
            return Refused(diagnostics);
        }

        using (source)
        {
            try
            {
                byte[] bytes = await source.ReadAllBytesAsync(MaxSourceBytes, cancellationToken);
                var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
                if (!TryDecode(bytes, cancellationToken, out var width, out var height, out var pixels,
                        out var validationError))
                {
                    diagnostics.Add(Error("facetint-bgra8-format-refused", validationError));
                    return Refused(diagnostics, width, height, sourceHash);
                }

                return new FaceTintTextureDecodeResult(
                    true,
                    checked((int)width),
                    checked((int)height),
                    pixels,
                    sourceHash,
                    diagnostics.ToImmutable());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               OverflowException)
            {
                diagnostics.Add(Error("facetint-bgra8-read-failed", exception.Message));
                return Refused(diagnostics);
            }
        }
    }

    private static bool TryDecode(
        byte[] bytes,
        CancellationToken cancellationToken,
        out uint width,
        out uint height,
        out byte[]? pixels,
        out string error)
    {
        width = 0;
        height = 0;
        pixels = null;
        error = string.Empty;
        if (bytes.Length < 128 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != DdsMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != DdsHeaderSize)
        {
            error = "The file does not contain a complete DDS header.";
            return false;
        }

        height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        uint mipCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));
        if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
        {
            error = $"DDS dimensions {width}x{height} are outside the admitted range.";
            return false;
        }

        long pixelLength = checked((long)width * height * 4);
        if (bytes.LongLength != 128 + pixelLength || mipCount > 1)
        {
            error = "The RaceMenu FaceTint DDS must contain exactly one uncompressed BGRA8 raster.";
            return false;
        }

        uint redMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(92, 4));
        uint greenMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96, 4));
        uint blueMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(100, 4));
        uint alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104, 4));
        bool isBgra = redMask == Bgra8RedMask && greenMask == Bgra8GreenMask &&
                      blueMask == Bgra8BlueMask && alphaMask == Bgra8AlphaMask;
        bool isRgba = redMask == Rgba8RedMask && greenMask == Rgba8GreenMask &&
                      blueMask == Rgba8BlueMask && alphaMask == Bgra8AlphaMask;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76, 4)) != PixelFormatSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80, 4)) != Bgra8PixelFormatFlags ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84, 4)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88, 4)) != Bgra8RgbBitCount ||
            (!isBgra && !isRgba))
        {
            error = "The DDS pixel format is not an admitted uncompressed RGBA8/BGRA8 layout.";
            return false;
        }

        ReadOnlySpan<byte> sourcePixels = bytes.AsSpan(128);
        pixels = sourcePixels.ToArray();
        if (isRgba)
        {
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if ((offset & 0x3F_FFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                (pixels[offset], pixels[offset + 2]) =
                    (pixels[offset + 2], pixels[offset]);
            }
        }
        return true;
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static FaceTintTextureDecodeResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        uint width = 0,
        uint height = 0,
        Sha256Hash? sourceHash = null) =>
        new(false,
            width > int.MaxValue ? 0 : (int)width,
            height > int.MaxValue ? 0 : (int)height,
            null,
            sourceHash,
            diagnostics.ToImmutable());
}
