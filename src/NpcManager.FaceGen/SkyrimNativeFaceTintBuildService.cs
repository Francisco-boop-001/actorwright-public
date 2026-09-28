using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Product-owned Skyrim SE FaceTint compositor. It applies the RACE-ordered
/// red-channel masks over the engine's 0.5 seed, writes DXT5, and reopens the
/// exact promoted bytes before reporting success.
/// </summary>
public sealed partial class SkyrimNativeFaceTintBuildService(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot,
    IFaceTintTextureContentDecoder decoder) : ISkyrimNativeFaceTintBuildService
{
    private const int Width = 512;
    private const int Height = 512;
    // Real Skyrim RACE providers can expose substantially more than the small
    // set authored on one NPC. Keep the compositor aligned with the resolver's
    // bounded engine-order table; zero-coverage rows are retained as evidence
    // and skipped during pixel composition.
    private const int MaximumLayers = 256;
    private const int MaximumMaskBytes = 32 * 1024 * 1024;

    public async ValueTask<SkyrimNativeFaceTintBuildResult> BuildAsync(
        SkyrimNativeFaceTintBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        Dictionary<string, SkyrimNativeFaceTintMaskInput> masks = request.Masks
            .ToDictionary(item => item.AssetPath.Value, StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, FaceTintTextureDecodeResult>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var mask in request.Masks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FaceTintTextureDecodeResult result = await decoder.DecodeAsync(
                mask.Content, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            if (!result.Decoded || result.Bytes is null ||
                result.SourceSha256 != mask.ContentSha256)
            {
                diagnostics.Add(Error("skyrim-native-tint-mask-decode",
                    $"Mask '{mask.AssetPath}' did not decode with its exact declared hash."));
                continue;
            }
            decoded.Add(mask.AssetPath.Value, result);
        }
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        byte[] linearRaster = Compose(request.RecordRoute, decoded, cancellationToken);
        Sha256Hash linearHash = Hash(linearRaster);
        byte[] outputBytes;
        try
        {
            outputBytes = await EncodeBc3Async(linearRaster, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           OverflowException)
        {
            diagnostics.Add(Error("skyrim-native-tint-encode", exception.Message));
            return Refused(diagnostics);
        }

        Sha256Hash outputHash = Hash(outputBytes);
        string temporary = request.OutputPath.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        var promoted = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, request.OutputPath.Value, overwrite: false);
            promoted = true;

            byte[] reopened = await File.ReadAllBytesAsync(
                request.OutputPath.Value, CancellationToken.None).ConfigureAwait(false);
            if (!reopened.AsSpan().SequenceEqual(outputBytes) || Hash(reopened) != outputHash)
            {
                diagnostics.Add(Error("skyrim-native-tint-output-drift",
                    "Promoted FaceTint DDS bytes do not match the encoded candidate."));
                DeleteCanonicalOutput(request.OutputPath.Value, diagnostics);
                return Refused(diagnostics);
            }

            FaceTintTextureDecodeResult readback = await decoder.DecodeAsync(
                ImmutableCollectionsMarshal.AsImmutableArray(reopened), CancellationToken.None)
                .ConfigureAwait(false);
            diagnostics.AddRange(readback.Diagnostics);
            if (!readback.Decoded || readback.Bytes is null ||
                readback.SourceSha256 != outputHash ||
                readback.Width != Width || readback.Height != Height ||
                !HasOpaqueAlpha(readback.Bytes))
            {
                diagnostics.Add(Error("skyrim-native-tint-readback",
                    "Reopened FaceTint DDS did not retain its hash, dimensions, and opaque alpha."));
                DeleteCanonicalOutput(request.OutputPath.Value, diagnostics);
                return Refused(diagnostics);
            }

            var evidence = request.RecordRoute.Layers.Select(layer =>
            {
                bool applied = layer.Coverage > 0F;
                Sha256Hash? maskHash = applied
                    ? masks[layer.MaskPath.Value].ContentSha256
                    : null;
                return new SkyrimNativeFaceTintLayerEvidence(
                    layer.RaceOrder, layer.Index, layer.MaskType, layer.MaskPath,
                    maskHash, layer.Red, layer.Green, layer.Blue, layer.Coverage,
                    layer.ColorSource, applied);
            }).ToImmutableArray();
            var artifact = new SkyrimNativeFaceTintBuildArtifact(
                "1", "skyrim-native-facetint-dds", request.RecordRoute.Npc,
                request.RecordRoute.Race, request.RecordRoute.Sex,
                Width, Height, "bc3-dxt5", ReadMipCount(reopened), linearHash,
                outputHash, Hash(readback.Bytes), evidence, RuntimeAuthority: false);
            return new SkyrimNativeFaceTintBuildResult(
                true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (promoted)
            {
                DeleteCanonicalOutput(request.OutputPath.Value, diagnostics);
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            if (promoted)
            {
                DeleteCanonicalOutput(request.OutputPath.Value, diagnostics);
            }
            diagnostics.Add(Error("skyrim-native-tint-write", exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async ValueTask<byte[]> EncodeBc3Async(
        byte[] bgra,
        CancellationToken cancellationToken)
    {
        var encoder = new BcEncoder(CompressionFormat.Bc3);
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.MaxMipMapLevel = 10;
        encoder.OutputOptions.Format = CompressionFormat.Bc3;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.DdsPreferDxt10Header = false;
        using var output = new MemoryStream();
        await encoder.EncodeToStreamAsync(
            bgra, Width, Height, PixelFormat.Bgra32, output, cancellationToken)
            .ConfigureAwait(false);
        return output.ToArray();
    }

    private static int ReadMipCount(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < 128 ||
            BinaryPrimitives.ReadUInt32LittleEndian(dds[..4]) != 0x2053_4444 ||
            BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(4, 4)) != 124)
        {
            throw new InvalidDataException("Encoded output is not an ordinary DDS file.");
        }
        return checked((int)Math.Max(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(28, 4))));
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool HasOpaqueAlpha(byte[] pixels)
    {
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != byte.MaxValue)
            {
                return false;
            }
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteCanonicalOutput(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error("skyrim-native-tint-rollback-failed",
                $"Invalid canonical output could not be removed: {exception.Message}"));
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimNativeFaceTintBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
