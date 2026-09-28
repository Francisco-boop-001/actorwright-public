using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BCnEncoder.Decoder;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public enum InProcessDdsTextureDecodeProfile
{
    FaceTintComposition,
    ReferencePreview
}

/// <summary>
/// Pure-managed, in-process DDS decoder used for hash-bound provider textures.
/// The admitted surface is deliberately decode-only: no encoder, child
/// process, network access, or live-root discovery is exposed here.
/// </summary>
public sealed class InProcessDdsTextureDecoder
    : IFaceTintTextureDecoder, IFaceTintTextureContentDecoder
{
    private const long FaceTintMaximumSourceBytes = 32L * 1024 * 1024;
    private const long ReferencePreviewMaximumSourceBytes =
        128L * 1024 * 1024;
    private const int ReferencePreviewMaximumPixels = 4096 * 4096;
    private const uint DdsMagic = 0x2053_4444;
    private const uint DdsHeaderSize = 124;
    private const uint FourCcDx10 = 0x3031_5844;
    private const uint FourCcDxt1 = 0x3154_5844;
    private const uint FourCcDxt3 = 0x3354_5844;
    private const uint FourCcDxt5 = 0x3554_5844;
    private const uint FourCcAti1 = 0x3149_5441;
    private const uint FourCcAti2 = 0x3249_5441;
    private const uint FourCcBc4U = 0x5534_4342;
    private const uint FourCcBc5U = 0x5535_4342;
    private const uint PixelFormatFourCc = 0x4;
    private const uint PixelFormatRgb = 0x40;
    private const uint HeaderFlagPitch = 0x8;
    private const uint Texture2DResourceDimension = 3;
    private const uint TextureCubeMiscFlag = 0x4;
    private const uint MaximumDimension = 8192;
    private readonly WorkspacePath labRoot;
    private readonly long maximumSourceBytes;
    private readonly int maximumPixels;

    public InProcessDdsTextureDecoder(
        WorkspacePath labRoot,
        InProcessDdsTextureDecodeProfile profile =
            InProcessDdsTextureDecodeProfile.FaceTintComposition)
    {
        this.labRoot = labRoot;
        maximumSourceBytes = profile ==
                             InProcessDdsTextureDecodeProfile.ReferencePreview
            ? ReferencePreviewMaximumSourceBytes
            : FaceTintMaximumSourceBytes;
        maximumPixels = profile ==
                        InProcessDdsTextureDecodeProfile.ReferencePreview
            ? ReferencePreviewMaximumPixels
            : RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels;
    }

    public async ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        WorkspacePath sourceDds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!sourceDds.IsUnder(labRoot) ||
            !sourceDds.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("face-texture-dds-source-invalid",
                "The provider texture must be a K-local DDS file."));
            return Refused(diagnostics);
        }

        if (!WindowsPinnedPath.TryOpenFile(sourceDds.Value, false, out var pinned,
                out _, out var openError) || pinned is null)
        {
            diagnostics.Add(Error("face-texture-dds-source-open",
                $"The provider texture is not an ordinary identity-pinned file: {openError}"));
            return Refused(diagnostics);
        }

        using (pinned)
        {
            try
            {
                var prefix = await pinned.ReadPrefixAsync(20, cancellationToken);
                if (!TryReadAdmittedDimensions(
                        prefix,
                        maximumPixels,
                        out var preflightWidth,
                        out var preflightHeight,
                        out var preflightError))
                {
                    diagnostics.Add(Error("face-texture-dds-header-refused",
                        preflightError));
                    return Refused(diagnostics, preflightWidth, preflightHeight);
                }
                var bytes = await pinned.ReadAllBytesAsync(
                    maximumSourceBytes, cancellationToken);
                return await DecodeContentAsync(
                        bytes, maximumPixels, diagnostics, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException or
                                               OverflowException or
                                               NotSupportedException)
            {
                diagnostics.Add(Error("face-texture-dds-decode-failed", exception.Message));
                return Refused(diagnostics);
            }
        }
    }

    public ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        ImmutableArray<byte> sourceDds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (sourceDds.IsDefaultOrEmpty ||
            sourceDds.Length > maximumSourceBytes)
        {
            diagnostics.Add(Error("face-texture-dds-content-size",
                $"Resolved DDS content must contain between 1 and {maximumSourceBytes} bytes."));
            return ValueTask.FromResult(Refused(diagnostics));
        }

        byte[] bytes = ImmutableCollectionsMarshal.AsArray(sourceDds) ?? sourceDds.ToArray();
        return DecodeContentAsync(
            bytes, maximumPixels, diagnostics, cancellationToken);
    }

    private static async ValueTask<FaceTintTextureDecodeResult> DecodeContentAsync(
        byte[] bytes,
        int maximumPixels,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (!TryReadAdmittedHeader(
                    bytes,
                    maximumPixels,
                    out var width,
                    out var height,
                    out var error))
            {
                diagnostics.Add(Error("face-texture-dds-header-refused", error));
                return Refused(diagnostics, width, height, hash);
            }

            if (IsLegacyBgr24(bytes))
            {
                byte[] bgra24 = DecodeLegacyBgr24(
                    bytes, width, height, cancellationToken);
                return new FaceTintTextureDecodeResult(
                    true, width, height, bgra24, hash, diagnostics.ToImmutable());
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new BcDecoder();
            var image = await decoder.Decode2DAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (image.Width != width || image.Height != height)
            {
                diagnostics.Add(Error("face-texture-dds-dimension-drift",
                    $"The decoder returned {image.Width}x{image.Height} for a {width}x{height} DDS."));
                return Refused(diagnostics, width, height, hash);
            }

            var bgra = new byte[checked(width * height * 4)];
            var span = image.Span;
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = span.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    var offset = checked((y * width + x) * 4);
                    bgra[offset] = pixel.b;
                    bgra[offset + 1] = pixel.g;
                    bgra[offset + 2] = pixel.r;
                    bgra[offset + 3] = pixel.a;
                }
            }

            return new FaceTintTextureDecodeResult(
                true, width, height, bgra, hash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           InvalidDataException or
                                           ArgumentException or
                                           OverflowException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("face-texture-dds-decode-failed", exception.Message));
            return Refused(diagnostics);
        }
    }

    private static bool TryReadAdmittedHeader(
        byte[] bytes,
        int maximumPixels,
        out int width,
        out int height,
        out string error)
    {
        width = 0;
        height = 0;
        error = string.Empty;
        if (bytes.Length < 128 ||
            !TryReadAdmittedDimensions(
                bytes, maximumPixels, out width, out height, out error))
        {
            return false;
        }

        var depth = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
        var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(112, 4));
        if (depth > 1 || caps2 != 0)
        {
            error = "Volume, cube, and multi-face DDS inputs are outside the texture-composition contract.";
            return false;
        }

        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84, 4));
        var pixelFormatFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80, 4));
        if (fourCc == FourCcDx10)
        {
            if (bytes.Length < 148)
            {
                error = "The DDS declares DX10 metadata but does not contain the complete header.";
                return false;
            }
            var resourceDimension = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(132, 4));
            var miscFlag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(136, 4));
            var arraySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140, 4));
            if (resourceDimension != Texture2DResourceDimension || arraySize != 1 ||
                (miscFlag & TextureCubeMiscFlag) != 0)
            {
                error = "DX10 DDS inputs must contain exactly one ordinary 2D texture.";
                return false;
            }
            var dxgiFormat = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128, 4));
            if (!IsAllowedDxgiFormat(dxgiFormat))
            {
                error = $"DXGI format {dxgiFormat} is not an admitted 8-bit color/mask layout.";
                return false;
            }
        }
        else if ((pixelFormatFlags & PixelFormatFourCc) != 0)
        {
            if (fourCc is not (FourCcDxt1 or FourCcDxt3 or FourCcDxt5 or
                FourCcAti1 or FourCcAti2 or FourCcBc4U or FourCcBc5U))
            {
                error = $"Legacy DDS FourCC 0x{fourCc:X8} is not an admitted BC1-BC5 layout.";
                return false;
            }
        }
        else if ((pixelFormatFlags & PixelFormatRgb) != 0)
        {
            var bitCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88, 4));
            var redMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(92, 4));
            var greenMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96, 4));
            var blueMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(100, 4));
            var alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104, 4));
            var rgba = redMask == 0x0000_00FF && greenMask == 0x0000_FF00 &&
                       blueMask == 0x00FF_0000 && alphaMask == 0xFF00_0000;
            var bgra = redMask == 0x00FF_0000 && greenMask == 0x0000_FF00 &&
                       blueMask == 0x0000_00FF && alphaMask == 0xFF00_0000;
            var bgr24 = bitCount == 24 &&
                        redMask == 0x00FF_0000 &&
                        greenMask == 0x0000_FF00 &&
                        blueMask == 0x0000_00FF &&
                        alphaMask == 0;
            if ((bitCount != 32 || (!rgba && !bgra)) && !bgr24)
            {
                error = "Uncompressed DDS inputs must use an admitted 24-bit BGR8 or 32-bit RGBA8/BGRA8 layout.";
                return false;
            }
        }
        else
        {
            error = "The DDS pixel format is not an admitted BC1-BC5, BC7, BGR8, RGBA8, or BGRA8 layout.";
            return false;
        }
        return true;
    }

    private static bool IsLegacyBgr24(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 128 &&
        (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(80, 4)) &
         PixelFormatRgb) != 0 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84, 4)) == 0 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(88, 4)) == 24 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(92, 4)) == 0x00FF_0000 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(96, 4)) == 0x0000_FF00 &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(100, 4)) == 0x0000_00FF &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(104, 4)) == 0;

    private static byte[] DecodeLegacyBgr24(
        byte[] bytes,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        const int dataOffset = 128;
        int tightRowBytes = checked(width * 3);
        uint headerFlags = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(8, 4));
        uint pitchOrLinearSize = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(20, 4));
        int rowBytes = (headerFlags & HeaderFlagPitch) != 0
            ? checked((int)pitchOrLinearSize)
            : tightRowBytes;
        if (rowBytes < tightRowBytes ||
            checked((long)dataOffset + (long)rowBytes * height) > bytes.Length)
        {
            throw new InvalidDataException(
                "The admitted 24-bit BGR DDS does not contain its complete top-level raster.");
        }

        var bgra = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceRow = checked(dataOffset + y * rowBytes);
            int destinationRow = checked(y * width * 4);
            for (var x = 0; x < width; x++)
            {
                int source = checked(sourceRow + x * 3);
                int destination = checked(destinationRow + x * 4);
                bgra[destination] = bytes[source];
                bgra[destination + 1] = bytes[source + 1];
                bgra[destination + 2] = bytes[source + 2];
                bgra[destination + 3] = byte.MaxValue;
            }
        }
        return bgra;
    }

    private static bool TryReadAdmittedDimensions(
        ReadOnlySpan<byte> bytes,
        int maximumPixels,
        out int width,
        out int height,
        out string error)
    {
        width = 0;
        height = 0;
        error = string.Empty;
        if (bytes.Length < 20 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) != DdsMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != DdsHeaderSize)
        {
            error = "The file does not contain a complete DDS header prefix.";
            return false;
        }

        var rawHeight = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
        var rawWidth = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4));
        if (rawWidth == 0 || rawHeight == 0 || rawWidth > MaximumDimension ||
            rawHeight > MaximumDimension || rawWidth > int.MaxValue || rawHeight > int.MaxValue ||
            (ulong)rawWidth * rawHeight > (ulong)maximumPixels)
        {
            error = $"DDS dimensions {rawWidth}x{rawHeight} are outside the admitted range.";
            return false;
        }
        width = checked((int)rawWidth);
        height = checked((int)rawHeight);
        return true;
    }

    private static bool IsAllowedDxgiFormat(uint format) => format is
        28 or 29 or // R8G8B8A8_UNORM[_SRGB]
        71 or 72 or // BC1_UNORM[_SRGB]
        74 or 75 or // BC2_UNORM[_SRGB]
        77 or 78 or // BC3_UNORM[_SRGB]
        80 or 81 or // BC4_UNORM/SNORM
        83 or 84 or // BC5_UNORM/SNORM
        87 or 88 or // B8G8R8A8/B8G8R8X8_UNORM
        91 or 93 or // B8G8R8A8/B8G8R8X8_UNORM_SRGB
        98 or 99;   // BC7_UNORM[_SRGB]

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static FaceTintTextureDecodeResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        int width = 0,
        int height = 0,
        Sha256Hash? hash = null) =>
        new(false, width, height, null, hash, diagnostics.ToImmutable());
}
