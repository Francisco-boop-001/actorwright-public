using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Builds a deterministic FaceTint artifact from explicit layer inputs. The
/// default path remains a uniform semantic contract; an explicit provider root
/// opts into hash-bound DDS sampling through the admitted decoder.
/// </summary>
public sealed partial class FaceTintBuildService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IFaceTintTextureEncoder? textureEncoder = null,
    IFaceTintTextureDecoder? textureDecoder = null) : IFaceTintBuildService
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxDimension = 8192;
    private const int MaxLayers = 2048;
    private const int MaxProbes = 256;
    private const long MaxTextureBytes = 64L * 1024 * 1024;
    private static readonly int[] SupportedResolutions = [512, 1024, 2048, 4096, 8192];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceTintBuildResult> BuildAsync(FaceTintBuildRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        if (request.TextureOutputPath is { } textureOutput)
            diagnostics.AddRange(ValidateTextureDestination(textureOutput));
        if (request.ProviderRoot is { } providerRoot)
            diagnostics.AddRange(ValidateProviderRoot(providerRoot));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ManifestPath));
        if (!string.Equals(Path.GetExtension(request.ManifestPath.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facetint-build-manifest-extension", DiagnosticSeverity.Error,
                "FaceTint build manifests must use the .json extension."));
        if (!File.Exists(request.ManifestPath.Value))
            diagnostics.Add(new Diagnostic("facetint-build-manifest-missing", DiagnosticSeverity.Error,
                "The FaceTint build manifest does not exist."));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ManifestPath.Value);
            if (info.Length > MaxBytes)
            {
                diagnostics.Add(new Diagnostic("facetint-build-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceTint build manifests may not exceed {MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("facetint-build-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        ManifestData manifest;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            manifest = ReadManifest(document.RootElement, request, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-build-manifest-json-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics, inputHash);
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics, inputHash);

        var composedColor = ComposeColor(manifest);
        var raster = await BuildRasterAsync(manifest, request.ProviderRoot, diagnostics, cancellationToken);
        if (!raster.Built)
            return Refused(diagnostics, inputHash);
        cancellationToken.ThrowIfCancellationRequested();
        var probes = request.ProviderRoot is null
            ? manifest.ProbeCoordinates.Select(coordinate => ComposeProbe(coordinate, manifest)).ToImmutableArray()
            : manifest.ProbeCoordinates.Select(coordinate => ProbeFromRaster(coordinate, manifest.Width,
                raster.Pixels)).ToImmutableArray();
        byte[]? textureBytes = null;
        Sha256Hash? textureHash = null;
        if (request.TextureOutputPath is { } textureOutputPath)
        {
            var payloadBytes = (long)manifest.Width * manifest.Height * 4;
            if (payloadBytes > MaxTextureBytes)
                diagnostics.Add(new Diagnostic("facetint-dds-size-limit", DiagnosticSeverity.Error,
                    $"The optional FaceTint DDS output may not exceed {MaxTextureBytes} bytes of pixel payload."));
            if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            {
                var bgraBytes = EncodeBgra8(manifest.Width, manifest.Height, raster.Pixels);
                if (manifest.Format == FaceTintOutputFormat.Bgra8)
                {
                    textureBytes = bgraBytes;
                }
                else if (textureEncoder is null)
                {
                    diagnostics.Add(new Diagnostic("facetint-dds-codec-unavailable", DiagnosticSeverity.Error,
                        "BC3/BC7 DDS output requires the pinned K-local DirectXTex encoder."));
                }
                else
                {
                    var sourcePath = Path.Combine(Path.GetDirectoryName(textureOutputPath.Value)!,
                        ".facetint-source-" + Guid.NewGuid().ToString("N") + ".dds");
                    try
                    {
                        await File.WriteAllBytesAsync(sourcePath, bgraBytes, cancellationToken);
                        var encoded = await textureEncoder.EncodeAsync(manifest.Format,
                            new WorkspacePath(sourcePath), cancellationToken);
                        diagnostics.AddRange(encoded.Diagnostics);
                        if (encoded.Encoded && encoded.Bytes is not null)
                            textureBytes = encoded.Bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        diagnostics.Add(new Diagnostic("facetint-dds-codec-io-failed", DiagnosticSeverity.Error,
                            exception.Message));
                    }
                    finally { TryDelete(sourcePath); }
                }
                if (textureBytes is not null)
                    textureHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(textureBytes)));
            }
        }
        var semanticHash = request.ProviderRoot is null
            ? new Sha256Hash(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                width = manifest.Width,
                height = manifest.Height,
                color = composedColor.Select(value => value.ToString("R", CultureInfo.InvariantCulture)).ToArray()
            }))))
            : new Sha256Hash(Convert.ToHexString(SHA256.HashData(raster.Pixels)));
        var artifact = new FaceTintBuildArtifact(
            "1", "facetint-semantic-build", request.Edition.ToWireName(),
            request.NpcFormId?.ToString() ?? manifest.NpcFormId,
            inputHash.Value, manifest.Width, manifest.Height,
            manifest.Format.ToString().ToLowerInvariant(), manifest.MipCount,
            manifest.AlphaMode.ToString().ToLowerInvariant(), "bgra8",
            manifest.BaseColor, manifest.Layers, probes, semanticHash.Value,
            request.TextureOutputPath?.Value, textureHash?.Value,
            request.ProviderRoot is null ? "uniform" : "provider-sampled",
            request.ProviderRoot is null ? null : raster.ProviderSources);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics, inputHash);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputPath.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var outputPromoted = false;
        try
        {
            await File.WriteAllBytesAsync(temporary, outputBytes, cancellationToken);
            File.Move(temporary, request.OutputPath.Value, overwrite: false);
            outputPromoted = true;
            if (textureBytes is not null && request.TextureOutputPath is { } texturePath)
            {
                var textureTemporary = texturePath.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                try
                {
                    await File.WriteAllBytesAsync(textureTemporary, textureBytes, cancellationToken);
                    File.Move(textureTemporary, texturePath.Value, overwrite: false);
                }
                catch (OperationCanceledException) { TryDelete(textureTemporary); TryDelete(request.OutputPath.Value); throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    TryDelete(textureTemporary);
                    TryDelete(request.OutputPath.Value);
                    diagnostics.Add(new Diagnostic("facetint-dds-write-failed", DiagnosticSeverity.Error, exception.Message));
                    return new FaceTintBuildResult(false, artifact, null, diagnostics.ToImmutable());
                }
            }
            return new FaceTintBuildResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable(), textureHash);
        }
        catch (OperationCanceledException) { TryDelete(temporary); if (outputPromoted) TryDelete(request.OutputPath.Value); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            if (outputPromoted) TryDelete(request.OutputPath.Value);
            diagnostics.Add(new Diagnostic("facetint-build-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new FaceTintBuildResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private static FaceTintBuildResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record RasterBuildResult(
        bool Built,
        byte[] Pixels,
        ImmutableArray<FaceTintProviderBinding> ProviderSources)
    {
        public static RasterBuildResult Failed { get; } = new(false, [], []);
    }

    private sealed record ManifestData(string NpcFormId, int Width, int Height, FaceTintOutputFormat Format,
        int MipCount, FaceTintAlphaMode AlphaMode, ImmutableArray<double> BaseColor,
        ImmutableArray<FaceTintBuildLayer> Layers, ImmutableArray<(int X, int Y)> ProbeCoordinates)
    {
        public static ManifestData Empty { get; } = new("unknown", 0, 0, FaceTintOutputFormat.Bgra8, 1,
            FaceTintAlphaMode.Preserve, [], [], []);
    }
}
