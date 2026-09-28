using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

/// <summary>
/// Resolves one exact indexed Skyrim NIF and renders a deterministic K-local
/// off-engine geometry preview. No copied Data file is written or replaced.
/// </summary>
public sealed class SkyrimMeshPreviewService(
    ISkyrimAssetContentResolver contentResolver,
    IPreviewImageRenderer renderer,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    WorkspacePath cacheRoot) : ISkyrimMeshPreviewService
{
    private const string CacheSchema = "skyrim-mesh-preview-v1";
    private const long MaximumContentBytes = 256L * 1024 * 1024;
    private const long MaximumImageBytes = 64L * 1024 * 1024;
    private const long MaximumMetadataBytes = 1024 * 1024;
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    public async ValueTask<SkyrimMeshPreviewResult> RenderAsync(
        SkyrimMeshPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimAssetContentAuthority? authority = await BuildAuthorityAsync(
            request,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (authority is null || HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimAssetContentResolutionResult resolution =
            await contentResolver.ResolveAsync(
                new SkyrimAssetContentResolutionRequest(
                    request.DataRoot,
                    [authority]),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(resolution.Diagnostics);
        ResolvedSkyrimAssetContent? resolved = resolution.Resolved &&
            resolution.Assets.Length == 1
                ? resolution.Assets[0]
                : null;
        if (resolved is null || !Matches(request.Candidate, authority, resolved))
        {
            diagnostics.Add(Error(
                "mesh-preview-content-binding",
                "The resolved NIF content did not match the selected path, provider, size, and SHA-256."));
            return Refused(diagnostics);
        }

        Sha256Hash actualContentHash = new(Convert.ToHexString(
            SHA256.HashData(resolved.Content.AsSpan())));
        if (actualContentHash != resolved.ContentSha256 ||
            actualContentHash != request.Candidate.SelectedProvider.Sha256)
        {
            diagnostics.Add(Error(
                "mesh-preview-content-binding",
                "The resolved NIF bytes did not match the selected content SHA-256."));
            return Refused(diagnostics);
        }

        diagnostics.AddRange(policy.Evaluate(labRoot, cacheRoot));
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        await RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspacePath contentRoot = EnsureContentCache(
                request,
                resolved,
                diagnostics);
            if (HasErrors(diagnostics)) return Refused(diagnostics);
            string imageDirectory = Path.Combine(cacheRoot.Value, "images");
            Directory.CreateDirectory(imageDirectory);
            if (HasReparsePoint(imageDirectory))
            {
                diagnostics.Add(Error(
                    "mesh-preview-cache-reparse",
                    "The mesh preview image cache may not traverse a reparse point."));
                return Refused(diagnostics);
            }
            var imagePath = new WorkspacePath(Path.Combine(
                imageDirectory,
                $"mesh-{actualContentHash.Value}-{request.Width}x{request.Height}.png"));
            var metadataPath = new WorkspacePath(Path.ChangeExtension(
                imagePath.Value,
                ".json"));
            PreviewRenderedImage? cached = await TryReadCachedImageAsync(
                imagePath,
                metadataPath,
                actualContentHash,
                request,
                cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                diagnostics.Add(new Diagnostic(
                    "mesh-preview-cache-hit",
                    DiagnosticSeverity.Info,
                    "Reused the exact hash-bound off-engine mesh preview."));
                return new SkyrimMeshPreviewResult(
                    true,
                    cached,
                    diagnostics.ToImmutable());
            }
            if (File.Exists(imagePath.Value) || File.Exists(metadataPath.Value))
            {
                diagnostics.Add(Error(
                    "mesh-preview-cache-invalid",
                    "The deterministic mesh preview cache entry is malformed."));
                return Refused(diagnostics);
            }

            PreviewImageRenderResult rendered = await renderer.RenderAsync(
                new PreviewImageRenderRequest(
                    GameEdition.SkyrimSpecialEdition,
                    contentRoot,
                    imagePath,
                    [new PreviewSceneAsset(
                        "mesh",
                        request.Candidate.IndexedPath.Value,
                        request.Candidate.SelectedProvider.Source,
                        actualContentHash.Value,
                        Visible: true,
                        Included: true)],
                    request.Width,
                    request.Height),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(rendered.Diagnostics);
            if (!rendered.Rendered || rendered.Image is null ||
                !string.Equals(rendered.Image.Path, imagePath.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !await IsExpectedPngAsync(
                    imagePath,
                    rendered.Image.Sha256,
                    request.Width,
                    request.Height,
                    cancellationToken).ConfigureAwait(false))
            {
                diagnostics.Add(Error(
                    "mesh-preview-render-binding",
                    "The renderer did not return the exact requested PNG, dimensions, and hash."));
                return Refused(diagnostics);
            }
            await WriteCacheMetadataAsync(
                metadataPath,
                new CacheMetadata(
                    CacheSchema,
                    actualContentHash.Value,
                    request.Candidate.IndexedPath.Value,
                    request.Candidate.SelectedProvider.Kind.ToString(),
                    request.Candidate.SelectedProvider.Source,
                    rendered.Image.Sha256,
                    rendered.Image.Width,
                    rendered.Image.Height,
                    rendered.Image.MeshCount),
                cancellationToken).ConfigureAwait(false);
            return new SkyrimMeshPreviewResult(
                true,
                rendered.Image with
                {
                    Edition = GameEdition.SkyrimSpecialEdition.ToWireName(),
                    DeformationMode = "static-mesh-picker-preview"
                },
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           InvalidDataException or
                                           InvalidOperationException or
                                           JsonException)
        {
            diagnostics.Add(Error(
                "mesh-preview-failed",
                $"The mesh preview could not be completed: {exception.Message}"));
            return Refused(diagnostics);
        }
        finally
        {
            RenderGate.Release();
        }
    }

    private void ValidateRequest(
        SkyrimMeshPreviewRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (!request.DataRoot.IsUnder(labRoot) || request.DataRoot == labRoot ||
            !Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error(
                "mesh-preview-data-root",
                "The mesh preview requires one existing K-local copied Data root."));
        if (!cacheRoot.IsUnder(labRoot) || cacheRoot == labRoot)
            diagnostics.Add(Error(
                "mesh-preview-cache-root",
                "The mesh preview cache must remain below the K-only lab root."));
        if (request.Width is < 64 or > 1024 || request.Height is < 64 or > 1024)
            diagnostics.Add(Error(
                "mesh-preview-dimensions",
                "Mesh preview dimensions must be between 64 and 1024 pixels."));
        if (!request.Candidate.IndexedPath.Value.StartsWith(
                "meshes/", StringComparison.OrdinalIgnoreCase) ||
            !request.Candidate.IndexedPath.Value.EndsWith(
                ".nif", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.Candidate.IndexedPath.Value["meshes/".Length..],
                request.Candidate.RelativePath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            request.Candidate.Providers.IsDefaultOrEmpty ||
            !request.Candidate.Providers.Contains(
                request.Candidate.SelectedProvider))
            diagnostics.Add(Error(
                "mesh-preview-candidate",
                "The selected mesh candidate is incomplete or internally inconsistent."));
    }

    private static async ValueTask<SkyrimAssetContentAuthority?> BuildAuthorityAsync(
        SkyrimMeshPreviewRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        AssetChoiceProviderEvidence selected = request.Candidate.SelectedProvider;
        SkyrimAssetContentProviderKind kind;
        string providerPath;
        Sha256Hash providerHash;
        if (selected.Kind == AssetProviderKind.Loose)
        {
            kind = SkyrimAssetContentProviderKind.Loose;
            providerPath = Path.GetFullPath(Path.Combine(
                request.DataRoot.Value,
                request.Candidate.IndexedPath.Value.Replace(
                    '/', Path.DirectorySeparatorChar)));
            providerHash = selected.Sha256;
        }
        else if (selected.Kind == AssetProviderKind.Archive &&
                 string.Equals(selected.Source, Path.GetFileName(selected.Source),
                     StringComparison.Ordinal) &&
                 selected.Source.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
        {
            kind = SkyrimAssetContentProviderKind.Bsa;
            providerPath = Path.GetFullPath(Path.Combine(
                request.DataRoot.Value,
                selected.Source));
            if (!File.Exists(providerPath) || HasReparsePoint(providerPath))
            {
                diagnostics.Add(Error(
                    "mesh-preview-provider-missing",
                    "The selected BSA provider is missing or unsafe in the copied Data root."));
                return null;
            }
            providerHash = await HashAsync(
                new WorkspacePath(providerPath),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            diagnostics.Add(Error(
                "mesh-preview-provider-invalid",
                "The selected mesh provider kind or archive filename is invalid."));
            return null;
        }
        var path = new WorkspacePath(providerPath);
        if (!path.IsUnder(request.DataRoot))
        {
            diagnostics.Add(Error(
                "mesh-preview-provider-escape",
                "The selected mesh provider escapes the copied Data root."));
            return null;
        }
        return new SkyrimAssetContentAuthority(
            $"mesh-picker-{selected.Kind.ToString().ToLowerInvariant()}",
            kind,
            path,
            providerHash,
            request.Candidate.IndexedPath,
            selected.Size,
            selected.Sha256);
    }

    private WorkspacePath EnsureContentCache(
        SkyrimMeshPreviewRequest request,
        ResolvedSkyrimAssetContent resolved,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string contentRoot = Path.Combine(
            cacheRoot.Value,
            "content",
            resolved.ContentSha256.Value);
        string destination = Path.GetFullPath(Path.Combine(
            contentRoot,
            request.Candidate.IndexedPath.Value.Replace(
                '/', Path.DirectorySeparatorChar)));
        var root = new WorkspacePath(contentRoot);
        var path = new WorkspacePath(destination);
        if (!root.IsUnder(cacheRoot) || !path.IsUnder(root))
        {
            diagnostics.Add(Error(
                "mesh-preview-cache-escape",
                "The selected mesh path escapes the K-local content cache."));
            return root;
        }
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null)
        {
            diagnostics.Add(Error(
                "mesh-preview-cache-parent",
                "The staged mesh path has no parent directory."));
            return root;
        }
        Directory.CreateDirectory(parent);
        if (HasReparsePoint(parent))
        {
            diagnostics.Add(Error(
                "mesh-preview-cache-reparse",
                "The staged mesh cache may not traverse a reparse point."));
            return root;
        }
        if (File.Exists(destination))
        {
            string hash;
            using (var stream = File.OpenRead(destination))
                hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(hash, resolved.ContentSha256.Value,
                    StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(Error(
                    "mesh-preview-content-cache-invalid",
                    "The staged mesh cache entry does not match its content hash."));
            return root;
        }
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, resolved.Content.ToArray());
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
        return root;
    }

    private static bool Matches(
        SkyrimMeshPickerCandidate candidate,
        SkyrimAssetContentAuthority authority,
        ResolvedSkyrimAssetContent resolved) =>
        resolved.AssetPath == candidate.IndexedPath &&
        resolved.AssetPath == authority.AssetPath &&
        resolved.Kind == authority.Kind &&
        resolved.ProviderPath == authority.ProviderPath &&
        resolved.ProviderSha256 == authority.ProviderSha256 &&
        resolved.ContentLength == candidate.SelectedProvider.Size &&
        resolved.ContentLength == resolved.Content.Length &&
        resolved.ContentLength is > 0 and <= MaximumContentBytes &&
        resolved.ContentSha256 == candidate.SelectedProvider.Sha256;

    private static async ValueTask<PreviewRenderedImage?> TryReadCachedImageAsync(
        WorkspacePath imagePath,
        WorkspacePath metadataPath,
        Sha256Hash contentHash,
        SkyrimMeshPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(imagePath.Value) ||
            !File.Exists(metadataPath.Value) ||
            HasReparsePoint(imagePath.Value) ||
            HasReparsePoint(metadataPath.Value) ||
            new FileInfo(metadataPath.Value).Length is <= 0 or > MaximumMetadataBytes)
            return null;
        CacheMetadata? metadata;
        await using (var stream = new FileStream(
                         metadataPath.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            metadata = await JsonSerializer.DeserializeAsync<CacheMetadata>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (metadata is null ||
            metadata.SchemaVersion != CacheSchema ||
            !string.Equals(metadata.ContentSha256, contentHash.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.IndexedPath,
                request.Candidate.IndexedPath.Value,
                StringComparison.Ordinal) ||
            metadata.ProviderKind !=
                request.Candidate.SelectedProvider.Kind.ToString() ||
            metadata.ProviderSource !=
                request.Candidate.SelectedProvider.Source ||
            metadata.Width != request.Width ||
            metadata.Height != request.Height ||
            metadata.MeshCount <= 0)
            return null;
        if (!await IsExpectedPngAsync(
                imagePath,
                metadata.ImageSha256,
                request.Width,
                request.Height,
                cancellationToken).ConfigureAwait(false))
            return null;
        Sha256Hash hash = await HashAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);
        return new PreviewRenderedImage(
            imagePath.Value,
            hash.Value,
            request.Width,
            request.Height,
            metadata.MeshCount,
            Edition: GameEdition.SkyrimSpecialEdition.ToWireName(),
            DeformationMode: "cached-static-mesh-picker-preview");
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
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> IsExpectedPngAsync(
        WorkspacePath path,
        string? expectedSha256,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Value) || HasReparsePoint(path.Value)) return false;
        var info = new FileInfo(path.Value);
        if (info.Length is < 24 or > MaximumImageBytes) return false;
        var header = new byte[24];
        await using (var stream = new FileStream(
                         path.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.ReadExactlyAsync(header, cancellationToken)
                .ConfigureAwait(false);
        }
        ReadOnlySpan<byte> png = header;
        if (!png[..8].SequenceEqual(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)) != expectedWidth ||
            BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)) != expectedHeight)
            return false;
        if (expectedSha256 is null) return true;
        Sha256Hash hash = await HashAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
            hash.Value,
            expectedSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<Sha256Hash> HashAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static bool HasReparsePoint(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            // Best-effort temporary-file cleanup only.
        }
    }

    private static SkyrimMeshPreviewResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private sealed record CacheMetadata(
        string SchemaVersion,
        string ContentSha256,
        string IndexedPath,
        string ProviderKind,
        string ProviderSource,
        string ImageSha256,
        int Width,
        int Height,
        int MeshCount);
}
