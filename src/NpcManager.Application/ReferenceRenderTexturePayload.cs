using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using NpcManager.Domain;

namespace NpcManager.Application;

internal static class ReferenceRenderTexturePayload
{
    internal const string EncodingName = "zlib-rgba-v1";
    private const int MaximumDecodedTextureBytes =
        128 * 1024 * 1024;
    private const int MaximumCompressionOverheadBytes =
        1024 * 1024;

    internal static string Encode(
        ImmutableArray<byte> rgba)
    {
        if (rgba.IsDefaultOrEmpty)
            return string.Empty;
        using var output = new MemoryStream();
        using (var compressed = new ZLibStream(
                   output,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            compressed.Write(rgba.AsSpan());
        }
        return Convert.ToBase64String(output.GetBuffer(), 0,
            checked((int)output.Length));
    }

    internal static bool TryDecode(
        ReferenceRenderTexture texture,
        out ImmutableArray<byte> rgba,
        out string error)
    {
        rgba = [];
        error = string.Empty;
        long expectedLength;
        try
        {
            expectedLength = checked(
                (long)texture.Width *
                texture.Height * 4);
        }
        catch (OverflowException)
        {
            error = "The decoded texture length overflows.";
            return false;
        }
        if (texture.Width <= 0 ||
            texture.Height <= 0 ||
            expectedLength <= 0 ||
            expectedLength > MaximumDecodedTextureBytes ||
            texture.CanonicalRgbaLength != expectedLength)
        {
            error =
                "The decoded texture dimensions or declared length are invalid.";
            return false;
        }

        if (!texture.CanonicalRgba.IsDefaultOrEmpty)
        {
            if (texture.CanonicalRgba.Length != expectedLength ||
                Hash(texture.CanonicalRgba.AsSpan()) !=
                texture.CanonicalRgbaSha256)
            {
                error =
                    "The in-memory decoded texture does not match its authority hash.";
                return false;
            }
            rgba = texture.CanonicalRgba;
            return true;
        }

        if (!string.Equals(
                texture.CanonicalRgbaEncoding,
                EncodingName,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(
                texture.CanonicalRgbaPayloadBase64))
        {
            error =
                "The persisted decoded texture payload is absent or uses an unsupported encoding.";
            return false;
        }

        int maximumEncodedCharacters = checked(
            ((MaximumDecodedTextureBytes +
              MaximumCompressionOverheadBytes + 2) / 3) * 4);
        if (texture.CanonicalRgbaPayloadBase64.Length >
            maximumEncodedCharacters)
        {
            error =
                "The persisted decoded texture payload exceeds its encoded byte budget.";
            return false;
        }

        try
        {
            byte[] encoded = Convert.FromBase64String(
                texture.CanonicalRgbaPayloadBase64);
            if (encoded.Length >
                MaximumDecodedTextureBytes +
                MaximumCompressionOverheadBytes)
            {
                error =
                    "The persisted decoded texture payload exceeds its compressed byte budget.";
                return false;
            }

            byte[] decoded = new byte[
                checked((int)expectedLength)];
            using var input = new MemoryStream(
                encoded,
                writable: false);
            using var inflater = new ZLibStream(
                input,
                CompressionMode.Decompress,
                leaveOpen: false);
            int offset = 0;
            while (offset < decoded.Length)
            {
                int read = inflater.Read(
                    decoded,
                    offset,
                    decoded.Length - offset);
                if (read == 0)
                    break;
                offset += read;
            }
            if (offset != decoded.Length ||
                inflater.ReadByte() != -1)
            {
                error =
                    "The persisted decoded texture payload does not expand to its declared length.";
                return false;
            }
            if (Hash(decoded) !=
                texture.CanonicalRgbaSha256)
            {
                error =
                    "The persisted decoded texture payload does not match its authority hash.";
                return false;
            }
            rgba = ImmutableArray.CreateRange(decoded);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or
            InvalidDataException or
            IOException or
            OverflowException)
        {
            error =
                "The persisted decoded texture payload could not be reopened: " +
                exception.Message;
            return false;
        }
    }

    private static Sha256Hash Hash(
        ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(bytes)));
}
