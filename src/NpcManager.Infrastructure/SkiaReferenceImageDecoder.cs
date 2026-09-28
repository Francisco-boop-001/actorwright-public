using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using SkiaSharp;

namespace NpcManager.Infrastructure;

public sealed class SkiaReferenceImageDecoder(
    WorkspacePath labRoot) :
    IReferenceImageDecoder,
    IReferenceImageBytesDecoder
{
    private readonly WorkspacePath _labRoot = labRoot;

    public ValueTask<ReferenceImageDecodeResult> DecodeAsync(
        ReferenceImageDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ReferenceImageAuthority authority = request.Image;
        if (authority is null)
        {
            diagnostics.Add(Error(
                "reference-image-authority",
                "A reference image authority is required."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        if (!authority.SourcePath.IsUnder(_labRoot))
        {
            diagnostics.Add(Error(
                "reference-image-outside-lab",
                "Reference images must remain under the declared K-local lab root."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        string path = authority.SourcePath.Value;
        if (!File.Exists(path))
        {
            diagnostics.Add(Error(
                "reference-image-missing",
                $"Reference image '{authority.ImageId}' does not exist."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        if (ContainsReparsePoint(path, _labRoot.Value, diagnostics))
        {
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        FileInfo info = new(path);
        if (info.Length != authority.EncodedLength)
        {
            diagnostics.Add(Error(
                "reference-image-length-drift",
                $"Reference image '{authority.ImageId}' changed length after intake."));
        }

        if (info.Length <= 0 ||
            info.Length >
            ReferencePresetAuthoringRules.MaximumEncodedImageBytes)
        {
            diagnostics.Add(Error(
                "reference-image-encoded-bytes",
                $"Reference image '{authority.ImageId}' is outside the encoded-byte limit."));
        }

        if (request.RemainingDecodedByteBudget <= 0 ||
            request.RemainingDecodedByteBudget >
            ReferencePresetAuthoringRules.MaximumDecodedBytes)
        {
            diagnostics.Add(Error(
                "reference-image-decoded-budget",
                "The remaining canonical image budget is invalid."));
        }

        if (diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        byte[] encoded;
        try
        {
            encoded = ReadEncodedBytes(
                path,
                checked((int)info.Length),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                "reference-image-read",
                $"Reference image '{authority.ImageId}' could not be read: {exception.Message}"));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        return DecodeEncoded(
            request,
            authority,
            path,
            encoded,
            diagnostics,
            cancellationToken);
    }

    public ValueTask<ReferenceImageDecodeResult>
        DecodeBytesAsync(
            ReferenceImageDecodeRequest request,
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        ReferenceImageAuthority authority = request.Image;
        if (authority is null)
        {
            diagnostics.Add(Error(
                "reference-image-authority",
                "A reference image authority is required."));
            return ValueTask.FromResult(
                Rejected(diagnostics));
        }
        if (!authority.SourcePath.IsUnder(_labRoot))
        {
            diagnostics.Add(Error(
                "reference-image-outside-lab",
                "Reference images must remain under the declared K-local lab root."));
        }
        if (encodedImage.Length !=
                authority.EncodedLength ||
            encodedImage.IsEmpty ||
            encodedImage.Length >
            ReferencePresetAuthoringRules
                .MaximumEncodedImageBytes)
        {
            diagnostics.Add(Error(
                "reference-image-encoded-bytes",
                $"Reference image '{authority.ImageId}' is outside its exact encoded-byte authority."));
        }
        if (request.RemainingDecodedByteBudget <= 0 ||
            request.RemainingDecodedByteBudget >
            ReferencePresetAuthoringRules.MaximumDecodedBytes)
        {
            diagnostics.Add(Error(
                "reference-image-decoded-budget",
                "The remaining canonical image budget is invalid."));
        }
        if (diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
            return ValueTask.FromResult(
                Rejected(diagnostics));
        return DecodeEncoded(
            request,
            authority,
            authority.SourcePath.Value,
            encodedImage.ToArray(),
            diagnostics,
            cancellationToken);
    }

    private static ValueTask<ReferenceImageDecodeResult>
        DecodeEncoded(
            ReferenceImageDecodeRequest request,
            ReferenceImageAuthority authority,
            string path,
            byte[] encoded,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encodedHash = new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(encoded)));
        if (encodedHash != authority.SourceSha256)
        {
            diagnostics.Add(Error(
                "reference-image-hash-drift",
                $"Reference image '{authority.ImageId}' changed after intake."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        try
        {
            using SKData data = SKData.CreateCopy(encoded);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null)
            {
                diagnostics.Add(Error(
                    "reference-image-codec",
                    $"Reference image '{authority.ImageId}' is not a supported still image."));
                return ValueTask.FromResult(Rejected(diagnostics));
            }

            ReferenceImageFormat? format =
                MapFormat(codec.EncodedFormat);
            if (format is null)
            {
                diagnostics.Add(Error(
                    "reference-image-format",
                    $"Reference image '{authority.ImageId}' is not PNG, JPEG, or WebP."));
            }
            else if (!ExtensionMatches(path, format.Value))
            {
                diagnostics.Add(Error(
                    "reference-image-extension-format",
                    $"Reference image '{authority.ImageId}' extension does not match its encoded format."));
            }

            if (codec.FrameCount > 1)
            {
                diagnostics.Add(Error(
                    "reference-image-frame-count",
                    $"Reference image '{authority.ImageId}' must not contain animation frames."));
            }

            int sourceWidth = codec.Info.Width;
            int sourceHeight = codec.Info.Height;
            if (sourceWidth <= 0 ||
                sourceHeight <= 0 ||
                sourceWidth >
                ReferencePresetAuthoringRules.MaximumImageDimension ||
                sourceHeight >
                ReferencePresetAuthoringRules.MaximumImageDimension)
            {
                diagnostics.Add(Error(
                    "reference-image-dimensions",
                    $"Reference image '{authority.ImageId}' exceeds the decoded dimension limit."));
            }

            long decodedLength;
            try
            {
                decodedLength = checked(
                    checked((long)sourceWidth * sourceHeight) * 4L);
            }
            catch (OverflowException)
            {
                decodedLength = long.MaxValue;
            }

            if (decodedLength >
                    ReferencePresetAuthoringRules.MaximumDecodedBytes ||
                decodedLength > request.RemainingDecodedByteBudget ||
                decodedLength > int.MaxValue)
            {
                diagnostics.Add(Error(
                    "reference-image-decoded-budget",
                    $"Reference image '{authority.ImageId}' exceeds the canonical RGBA budget."));
            }

            if (diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error))
            {
                return ValueTask.FromResult(Rejected(diagnostics));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using SKColorSpace colorSpace = SKColorSpace.CreateSrgb();
            var imageInfo = new SKImageInfo(
                sourceWidth,
                sourceHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Unpremul,
                colorSpace);
            var sourceRgba = new byte[checked((int)decodedLength)];
            SKCodecResult decodeResult =
                codec.GetPixels(imageInfo, sourceRgba);
            if (decodeResult != SKCodecResult.Success)
            {
                diagnostics.Add(Error(
                    "reference-image-decode",
                    $"Reference image '{authority.ImageId}' did not decode completely ({decodeResult})."));
                return ValueTask.FromResult(Rejected(diagnostics));
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReferenceImageOrientation orientation =
                MapOrientation(codec.EncodedOrigin);
            (byte[] canonicalRgba, int width, int height) =
                ApplyOrientation(
                    sourceRgba,
                    sourceWidth,
                    sourceHeight,
                    orientation);
            var hashInput =
                new byte[checked(8 + canonicalRgba.Length)];
            BinaryPrimitives.WriteInt32LittleEndian(
                hashInput.AsSpan(0, 4),
                width);
            BinaryPrimitives.WriteInt32LittleEndian(
                hashInput.AsSpan(4, 4),
                height);
            canonicalRgba.CopyTo(hashInput, 8);
            var canonicalHash = new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(hashInput)));
            var image = new DecodedReferenceImage(
                authority.ImageId,
                authority.ViewRole,
                authority.SourcePath,
                authority.SourceSha256,
                authority.EncodedLength,
                format!.Value,
                orientation,
                width,
                height,
                checked(width * 4),
                ImmutableArray.Create(canonicalRgba),
                canonicalHash);
            return ValueTask.FromResult(
                new ReferenceImageDecodeResult(
                    image,
                    diagnostics.ToImmutable()));
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
            diagnostics.Add(Error(
                "reference-image-decode",
                $"Reference image '{authority.ImageId}' could not be decoded safely: {exception.Message}"));
            return ValueTask.FromResult(Rejected(diagnostics));
        }
    }

    private static byte[] ReadEncodedBytes(
        string path,
        int length,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(
                bytes,
                offset,
                Math.Min(64 * 1024, bytes.Length - offset));
            if (read == 0)
            {
                throw new IOException(
                    "The encoded image ended before its declared length.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new IOException(
                "The encoded image grew while it was being read.");
        }

        return bytes;
    }

    private static bool ContainsReparsePoint(
        string path,
        string root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string current = Path.GetFullPath(path);
        string boundary = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        while (true)
        {
            try
            {
                if (File.GetAttributes(current)
                    .HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error(
                        "reference-image-reparse",
                        "Reference image paths may not traverse a reparse point."));
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException)
            {
                diagnostics.Add(Error(
                    "reference-image-path-inspection",
                    $"Reference image path authority could not be inspected: {exception.Message}"));
                return true;
            }

            if (string.Equals(
                    current,
                    boundary,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "reference-image-outside-lab",
                    "Reference image path does not terminate at the declared lab root."));
                return true;
            }

            current = parent;
        }
    }

    private static ReferenceImageFormat? MapFormat(
        SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => ReferenceImageFormat.Png,
        SKEncodedImageFormat.Jpeg => ReferenceImageFormat.Jpeg,
        SKEncodedImageFormat.Webp => ReferenceImageFormat.WebP,
        _ => null
    };

    private static bool ExtensionMatches(
        string path,
        ReferenceImageFormat format)
    {
        string extension = Path.GetExtension(path);
        return format switch
        {
            ReferenceImageFormat.Png =>
                extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
            ReferenceImageFormat.Jpeg =>
                extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            ReferenceImageFormat.WebP =>
                extension.Equals(".webp", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static ReferenceImageOrientation MapOrientation(
        SKEncodedOrigin origin)
    {
        int value = (int)origin;
        return Enum.IsDefined(
                typeof(ReferenceImageOrientation),
                value)
            ? (ReferenceImageOrientation)value
            : ReferenceImageOrientation.TopLeft;
    }

    private static (byte[] Bytes, int Width, int Height) ApplyOrientation(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        ReferenceImageOrientation orientation)
    {
        bool swapsAxes = orientation is
            ReferenceImageOrientation.LeftTop or
            ReferenceImageOrientation.RightTop or
            ReferenceImageOrientation.RightBottom or
            ReferenceImageOrientation.LeftBottom;
        int width = swapsAxes ? sourceHeight : sourceWidth;
        int height = swapsAxes ? sourceWidth : sourceHeight;
        var destination = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                (int sourceX, int sourceY) = orientation switch
                {
                    ReferenceImageOrientation.TopLeft => (x, y),
                    ReferenceImageOrientation.TopRight =>
                        (sourceWidth - 1 - x, y),
                    ReferenceImageOrientation.BottomRight =>
                        (sourceWidth - 1 - x, sourceHeight - 1 - y),
                    ReferenceImageOrientation.BottomLeft =>
                        (x, sourceHeight - 1 - y),
                    ReferenceImageOrientation.LeftTop => (y, x),
                    ReferenceImageOrientation.RightTop =>
                        (y, sourceHeight - 1 - x),
                    ReferenceImageOrientation.RightBottom =>
                        (sourceWidth - 1 - y, sourceHeight - 1 - x),
                    ReferenceImageOrientation.LeftBottom =>
                        (sourceWidth - 1 - y, x),
                    _ => (x, y)
                };
                int sourceOffset =
                    checked((sourceY * sourceWidth + sourceX) * 4);
                int destinationOffset =
                    checked((y * width + x) * 4);
                Buffer.BlockCopy(
                    source,
                    sourceOffset,
                    destination,
                    destinationOffset,
                    4);
            }
        }

        return (destination, width, height);
    }

    private static ReferenceImageDecodeResult Rejected(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
