using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Renders one compatible HDPT plus its admitted HNAM model closure through the
/// existing pinned off-engine image renderer. Cached PNGs remain static K-local
/// evidence and never establish Skyrim runtime appearance.
/// </summary>
public sealed class SkyrimHeadPartPreviewService(
    IPreviewImageRenderer renderer,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    WorkspacePath cacheRoot) : ISkyrimHeadPartPreviewService
{
    private const string CacheSchema = "skyrim-headpart-preview-v1";
    private const int MaximumModels = 64;
    private const long MaximumModelBytes = 256L * 1024 * 1024;
    private const long MaximumCachedImageBytes = 64L * 1024 * 1024;
    private const long MaximumCacheMetadataBytes = 64L * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    public async ValueTask<SkyrimHeadPartPreviewResult> RenderAsync(
        SkyrimHeadPartPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<BoundPreviewModel> models = await BindModelsAsync(
            request,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (HasErrors(diagnostics) || models.IsDefaultOrEmpty)
            return Refused(diagnostics);
        if (!EnsureCacheRoot(diagnostics)) return Refused(diagnostics);

        string cacheKey = CacheKey(request, models);
        var output = new WorkspacePath(Path.Combine(
            cacheRoot.Value,
            $"headpart-{cacheKey}.png"));
        var metadataPath = new WorkspacePath(Path.Combine(
            cacheRoot.Value,
            $"headpart-{cacheKey}.json"));

        WorkspacePath? temporaryOutput = null;
        WorkspacePath? temporaryMetadata = null;
        bool promotedOutput = false;
        bool promotedMetadata = false;
        await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SkyrimHeadPartPreviewResult? cached = await TryReadCacheAsync(
                output,
                metadataPath,
                cacheKey,
                request,
                models,
                cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                diagnostics.AddRange(cached.Diagnostics);
                return cached with { Diagnostics = diagnostics.ToImmutable() };
            }
            if (File.Exists(output.Value) || File.Exists(metadataPath.Value))
            {
                diagnostics.Add(Error(
                    "headpart-preview-cache-invalid",
                    "The deterministic head-part preview cache entry is incomplete or invalid."));
                return Refused(diagnostics);
            }

            string nonce = Guid.NewGuid().ToString("N");
            temporaryOutput = new WorkspacePath(Path.Combine(
                cacheRoot.Value,
                $".headpart-{cacheKey}-{nonce}.tmp.png"));
            temporaryMetadata = new WorkspacePath(Path.Combine(
                cacheRoot.Value,
                $".headpart-{cacheKey}-{nonce}.tmp.json"));

            ImmutableArray<PreviewSceneAsset> assets = models.Select((item, index) =>
                new PreviewSceneAsset(
                    index == 0 ? "headpart" : "headpart-extra",
                    item.RelativePath.Value,
                    item.Model.Provider.Plugin.Value,
                    item.Sha256.Value,
                    Visible: true,
                    Included: true)).ToImmutableArray();
            PreviewImageRenderResult rendered = await renderer.RenderAsync(
                new PreviewImageRenderRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.DataRoot,
                    temporaryOutput.Value,
                    assets,
                    request.Width,
                    request.Height),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(rendered.Diagnostics);
            if (!rendered.Rendered || rendered.Image is null || HasErrors(diagnostics))
                return Refused(diagnostics);
            if (!string.Equals(rendered.Image.Path, temporaryOutput.Value.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !await IsExpectedPngAsync(
                    temporaryOutput.Value,
                    rendered.Image.Sha256,
                    request.Width,
                    request.Height,
                    cancellationToken).ConfigureAwait(false))
            {
                diagnostics.Add(Error(
                    "headpart-preview-render-binding",
                    "The renderer output was not the exact requested PNG and dimensions."));
                return Refused(diagnostics);
            }

            var metadata = new CacheMetadata(
                CacheSchema,
                cacheKey,
                request.Candidate.Reference.ToString(),
                rendered.Image.Sha256,
                rendered.Image.Width,
                rendered.Image.Height,
                rendered.Image.MeshCount,
                models.Select(item => new CacheModel(
                    item.Model.Reference.ToString(),
                    item.RelativePath.Value,
                    item.Sha256.Value)).ToImmutableArray());
            await WriteCacheMetadataAsync(temporaryMetadata.Value, metadata, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryOutput.Value.Value, output.Value, overwrite: false);
            promotedOutput = true;
            File.Move(temporaryMetadata.Value.Value, metadataPath.Value, overwrite: false);
            promotedMetadata = true;
            return new SkyrimHeadPartPreviewResult(
                true,
                rendered.Image with { Path = output.Value },
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           InvalidDataException or
                                           JsonException)
        {
            diagnostics.Add(Error(
                "headpart-preview-cache-write",
                $"The K-local preview cache could not be completed: {exception.Message}"));
            return Refused(diagnostics);
        }
        finally
        {
            TryDelete(temporaryOutput?.Value);
            TryDelete(temporaryMetadata?.Value);
            if (promotedOutput && !promotedMetadata) TryDelete(output.Value);
            if (promotedMetadata && !promotedOutput) TryDelete(metadataPath.Value);
            RenderGate.Release();
        }
    }

    private void ValidateRequest(
        SkyrimHeadPartPreviewRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (!request.DataRoot.IsUnder(labRoot) || request.DataRoot == labRoot ||
            !Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(Error(
                "headpart-preview-data-root",
                "The preview Data root must be an existing K-local copied directory."));
        }
        if (!cacheRoot.IsUnder(labRoot) || cacheRoot == labRoot)
        {
            diagnostics.Add(Error(
                "headpart-preview-cache-root",
                "The preview cache must remain under the K-only lab root."));
        }
        if (request.Width is < 64 or > 1024 || request.Height is < 64 or > 1024)
        {
            diagnostics.Add(Error(
                "headpart-preview-dimensions",
                "Head-part preview dimensions must be between 64 and 1024 pixels."));
        }
        if (request.Candidate.PreviewModels.IsDefaultOrEmpty ||
            request.Candidate.PreviewModels.Length > MaximumModels ||
            request.Candidate.PreviewModels.Count(item => item.IsRoot) != 1 ||
            !request.Candidate.PreviewModels[0].IsRoot ||
            request.Candidate.PreviewModels[0].Reference != request.Candidate.Reference)
        {
            diagnostics.Add(Error(
                "headpart-preview-model-closure",
                "The selected HDPT requires one bounded, root-first preview model closure."));
        }
    }

    private static async ValueTask<ImmutableArray<BoundPreviewModel>> BindModelsAsync(
        SkyrimHeadPartPreviewRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<BoundPreviewModel>();
        foreach (SkyrimHeadPartPreviewModel model in request.Candidate.PreviewModels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetPath relative = WithMeshesPrefix(model.ModelNif);
            string fullPath = Path.GetFullPath(Path.Combine(
                request.DataRoot.Value,
                relative.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!new WorkspacePath(fullPath).IsUnder(request.DataRoot) ||
                !File.Exists(fullPath) || HasReparsePoint(fullPath))
            {
                diagnostics.Add(Error(
                    "headpart-preview-model-missing",
                    $"Preview model '{relative}' is missing or unsafe in the copied Data root."));
                continue;
            }
            long length = new FileInfo(fullPath).Length;
            if (length <= 0 || length > MaximumModelBytes)
            {
                diagnostics.Add(Error(
                    "headpart-preview-model-size",
                    $"Preview model '{relative}' is outside the accepted byte bound."));
                continue;
            }
            Sha256Hash hash = await HashAsync(
                new WorkspacePath(fullPath),
                cancellationToken).ConfigureAwait(false);
            result.Add(new BoundPreviewModel(model, relative, hash));
        }
        return result.ToImmutable();
    }

    private static AssetPath WithMeshesPrefix(AssetPath path) =>
        path.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)
            ? path
            : new AssetPath("meshes/" + path.Value);

    private bool EnsureCacheRoot(ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, cacheRoot));
            if (HasErrors(diagnostics)) return false;
            Directory.CreateDirectory(cacheRoot.Value);
            if (HasReparsePoint(cacheRoot.Value))
            {
                diagnostics.Add(Error(
                    "headpart-preview-cache-reparse",
                    "The preview cache traverses a reparse point."));
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            diagnostics.Add(Error(
                "headpart-preview-cache-create",
                $"The K-local preview cache could not be prepared: {exception.Message}"));
            return false;
        }
    }

    private static async ValueTask<SkyrimHeadPartPreviewResult?> TryReadCacheAsync(
        WorkspacePath output,
        WorkspacePath metadataPath,
        string cacheKey,
        SkyrimHeadPartPreviewRequest request,
        ImmutableArray<BoundPreviewModel> models,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(output.Value) && !File.Exists(metadataPath.Value))
            return null;
        if (!File.Exists(output.Value) || !File.Exists(metadataPath.Value) ||
            HasReparsePoint(output.Value) || HasReparsePoint(metadataPath.Value))
        {
            return null;
        }
        var metadataInfo = new FileInfo(metadataPath.Value);
        var imageInfo = new FileInfo(output.Value);
        if (metadataInfo.Length is <= 0 or > MaximumCacheMetadataBytes ||
            imageInfo.Length is < 24 or > MaximumCachedImageBytes)
        {
            return null;
        }
        CacheMetadata? metadata;
        await using (var stream = new FileStream(metadataPath.Value, FileMode.Open,
                         FileAccess.Read, FileShare.Read, 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            metadata = await JsonSerializer.DeserializeAsync<CacheMetadata>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        if (metadata is null || metadata.SchemaVersion != CacheSchema ||
            metadata.CacheKey != cacheKey ||
            metadata.Reference != request.Candidate.Reference.ToString() ||
            metadata.Width != request.Width || metadata.Height != request.Height ||
            metadata.MeshCount <= 0 || metadata.Models.Length != models.Length)
        {
            return null;
        }
        for (int index = 0; index < models.Length; index++)
        {
            CacheModel cached = metadata.Models[index];
            BoundPreviewModel current = models[index];
            if (cached.Reference != current.Model.Reference.ToString() ||
                !string.Equals(cached.Path, current.RelativePath.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(cached.Sha256, current.Sha256.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        if (!await IsExpectedPngAsync(
                output,
                metadata.ImageSha256,
                request.Width,
                request.Height,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        Sha256Hash imageHash = await HashAsync(output, cancellationToken)
            .ConfigureAwait(false);
        var diagnostics = ImmutableArray.Create(new Diagnostic(
            "headpart-preview-cache-hit",
            DiagnosticSeverity.Info,
            "Reused the exact hash-bound off-engine HDPT preview."));
        return new SkyrimHeadPartPreviewResult(
            true,
            new PreviewRenderedImage(
                output.Value,
                imageHash.Value,
                metadata.Width,
                metadata.Height,
                metadata.MeshCount,
                Edition: GameEdition.SkyrimSpecialEdition.ToWireName(),
                DeformationMode: "cached-static-headpart-preview"),
            diagnostics);
    }

    private static async ValueTask WriteCacheMetadataAsync(
        WorkspacePath path,
        CacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            metadata,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CacheKey(
        SkyrimHeadPartPreviewRequest request,
        ImmutableArray<BoundPreviewModel> models)
    {
        string authority = string.Join('|',
            CacheSchema,
            request.Candidate.Reference.ToString(),
            request.Candidate.Provider.Plugin.Value,
            request.Candidate.Provider.Sha256.Value,
            request.Width,
            request.Height,
            string.Join(';', models.Select(item =>
                $"{item.Model.Reference}:{item.RelativePath.Value}:{item.Sha256.Value}")));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(authority)))[..32];
    }

    private static async ValueTask<Sha256Hash> HashAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path.Value, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask<bool> IsExpectedPngAsync(
        WorkspacePath path,
        string expectedSha256,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Value) || HasReparsePoint(path.Value)) return false;
        var info = new FileInfo(path.Value);
        if (info.Length is < 24 or > MaximumCachedImageBytes) return false;

        var header = new byte[24];
        await using (var stream = new FileStream(
                         path.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        ReadOnlySpan<byte> png = header;
        if (!png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)) != expectedWidth ||
            BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)) != expectedHeight)
        {
            return false;
        }

        Sha256Hash hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(hash.Value, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only; incomplete deterministic cache pairs fail closed.
        }
    }

    private static bool HasReparsePoint(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimHeadPartPreviewResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record BoundPreviewModel(
        SkyrimHeadPartPreviewModel Model,
        AssetPath RelativePath,
        Sha256Hash Sha256);

    private sealed record CacheMetadata(
        string SchemaVersion,
        string CacheKey,
        string Reference,
        string ImageSha256,
        int Width,
        int Height,
        int MeshCount,
        ImmutableArray<CacheModel> Models);

    private sealed record CacheModel(
        string Reference,
        string Path,
        string Sha256);
}
