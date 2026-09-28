using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal sealed record FaceGeomHairRegionsPreviewCacheKey(
    Sha256Hash SourceSha256,
    Sha256Hash RequestSha256,
    Sha256Hash ProposalSha256,
    Sha256Hash IntakeSha256,
    Sha256Hash RendererAuthoritySha256,
    Sha256Hash RendererScriptSha256,
    Sha256Hash TextureCatalogSha256,
    Sha256Hash ResolvedTextureAuthoritySha256,
    Sha256Hash Fingerprint)
{
    public static FaceGeomHairRegionsPreviewCacheKey Create(
        Sha256Hash sourceSha256,
        Sha256Hash requestSha256,
        Sha256Hash proposalSha256,
        Sha256Hash intakeSha256,
        Sha256Hash rendererAuthoritySha256,
        Sha256Hash textureCatalogSha256,
        Sha256Hash resolvedTextureAuthoritySha256) =>
        Create(
            sourceSha256,
            requestSha256,
            proposalSha256,
            intakeSha256,
            rendererAuthoritySha256,
            rendererAuthoritySha256,
            textureCatalogSha256,
            resolvedTextureAuthoritySha256);

    public static FaceGeomHairRegionsPreviewCacheKey Create(
        Sha256Hash sourceSha256,
        Sha256Hash requestSha256,
        Sha256Hash proposalSha256,
        Sha256Hash intakeSha256,
        Sha256Hash rendererAuthoritySha256,
        Sha256Hash rendererScriptSha256,
        Sha256Hash textureCatalogSha256,
        Sha256Hash resolvedTextureAuthoritySha256)
    {
        string canonical = string.Join(
            "\n",
            $"source={sourceSha256.Value}",
            $"request={requestSha256.Value}",
            $"proposal={proposalSha256.Value}",
            $"intake={intakeSha256.Value}",
            $"rendererAuthority={rendererAuthoritySha256.Value}",
            $"rendererScript={rendererScriptSha256.Value}",
            $"textureCatalog={textureCatalogSha256.Value}",
            $"resolvedTextures={resolvedTextureAuthoritySha256.Value}");
        return new FaceGeomHairRegionsPreviewCacheKey(
            sourceSha256,
            requestSha256,
            proposalSha256,
            intakeSha256,
            rendererAuthoritySha256,
            rendererScriptSha256,
            textureCatalogSha256,
            resolvedTextureAuthoritySha256,
            new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical)))));
    }
}

internal sealed record FaceGeomHairRegionsPreviewCacheAuthority(
    FaceGeomHairRegionsPreviewCacheKey Key,
    Sha256Hash IntakeFingerprint);

internal sealed record FaceGeomHairRegionsPreviewCacheLookupResult(
    bool Hit,
    FaceGeomHairRegionsPreviewResult? Result,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed record FaceGeomHairRegionsPreviewCacheStoreResult(
    bool Stored,
    long StoredBytes,
    FaceGeomHairRegionsPreviewResult? CachedResult,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed class FaceGeomHairRegionsPreviewCache
{
    public const long MaximumBytes =
        1024L * 1024L * 1024L;

    private const string CacheSchema =
        "npcmanager-facegeom-hair-regions-preview-cache/1";
    private const string ManifestPrefix = "entry-";
    private const string ManifestSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters =
            {
                new WorkspacePathConverter(),
                new Sha256HashConverter(),
                new AssetPathConverter()
            }
        };

    private readonly object sync = new();
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath root;
    private readonly long capacityBytes;
    private readonly Dictionary<string, CacheEntry> entries =
        new(StringComparer.Ordinal);
    private ImmutableArray<Diagnostic> startupDiagnostics =
        [];
    private long currentBytes;
    private long nextOrdinal = 1;

    public FaceGeomHairRegionsPreviewCache(
        WorkspacePath labRoot,
        WorkspacePath root,
        long capacityBytes = MaximumBytes)
    {
        if (capacityBytes <= 0 ||
            capacityBytes > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityBytes),
                capacityBytes,
                "Preview cache capacity must be positive and no greater than 1 GiB.");
        }

        this.labRoot =
            FaceGeomHairRegionsDesktopPathBoundary
                .RequireExactLabRoot(labRoot);
        FaceGeomHairRegionsDesktopPathBoundary
            .RequireCacheRoot(
                this.labRoot,
                root);
        this.root = root;
        this.capacityBytes = capacityBytes;
        Directory.CreateDirectory(root.Value);
        FaceGeomHairRegionsDesktopPathBoundary
            .RequireCacheRoot(
                this.labRoot,
                root);
        EnsureOrdinaryDirectory(root.Value);
        LoadExistingEntries();
        EnforceCapacity();
    }

    public WorkspacePath Root => root;

    public long CapacityBytes => capacityBytes;

    public long CurrentBytes
    {
        get
        {
            lock (sync)
            {
                return currentBytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (sync)
            {
                return entries.Count;
            }
        }
    }

    public ImmutableArray<Diagnostic> StartupDiagnostics =>
        startupDiagnostics;

    public bool Contains(
        FaceGeomHairRegionsPreviewCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (sync)
        {
            return entries.ContainsKey(
                key.Fingerprint.Value);
        }
    }

    public FaceGeomHairRegionsPreviewCacheLookupResult
        Lookup(
            FaceGeomHairRegionsPreviewCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (sync)
        {
            if (!entries.TryGetValue(
                    key.Fingerprint.Value,
                    out CacheEntry? entry) ||
                entry is null)
            {
                return new(
                    false,
                    null,
                    []);
            }

            try
            {
                ValidateEntry(entry);
                return new(
                    true,
                    entry.Document.Result,
                    []);
            }
            catch (Exception exception)
                when (IsExpectedFileFailure(exception))
            {
                ImmutableArray<Diagnostic>.Builder diagnostics =
                    ImmutableArray.CreateBuilder<Diagnostic>();
                diagnostics.Add(
                    new Diagnostic(
                        "hair-regions-preview-cache-invalid",
                        DiagnosticSeverity.Warning,
                        $"Cached preview {key.Fingerprint.Value} was rejected: {exception.Message}"));
                if (!TryRemoveEntry(
                        entry,
                        out string? cleanupFailure))
                {
                    diagnostics.Add(
                        new Diagnostic(
                            "hair-regions-preview-cache-cleanup-failed",
                            DiagnosticSeverity.Error,
                            $"Invalid cached preview survived at {entry.Directory.Value}: {cleanupFailure}"));
                }

                return new(
                    false,
                    null,
                    diagnostics.ToImmutable());
            }
        }
    }

    public FaceGeomHairRegionsPreviewCacheStoreResult
        Store(
            FaceGeomHairRegionsPreviewCacheKey key,
            FaceGeomHairRegionsPreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded ||
            result.Evidence is null ||
            result.Artifacts.IsDefaultOrEmpty)
        {
            return new(
                false,
                0,
                null,
                [
                    new Diagnostic(
                        "hair-regions-preview-cache-result-invalid",
                        DiagnosticSeverity.Warning,
                        "Only a successful preview with evidence and artifacts can enter the cache.")
                ]);
        }

        lock (sync)
        {
            ImmutableArray<Diagnostic>.Builder diagnostics =
                ImmutableArray.CreateBuilder<Diagnostic>();
            FaceGeomHairRegionsPreviewCacheLookupResult
                existing = Lookup(key);
            diagnostics.AddRange(
                existing.Diagnostics);
            if (existing.Hit)
            {
                CacheEntry entry =
                    entries[key.Fingerprint.Value];
                return new(
                    true,
                    entry.Size,
                    existing.Result,
                    diagnostics.ToImmutable());
            }

            WorkspacePath finalDirectory = EntryDirectory(key);
            WorkspacePath temporaryDirectory = new(
                Path.Combine(
                    root.Value,
                    $".tmp-{key.Fingerprint.Value}-{Guid.NewGuid():N}"));
            bool promoted = false;
            try
            {
                ValidateAuthorityBinding(
                    key,
                    result);
                Directory.CreateDirectory(
                    temporaryDirectory.Value);
                ImmutableArray<
                    FaceGeomHairRegionsPreviewArtifact>.Builder
                    cachedArtifacts =
                        ImmutableArray.CreateBuilder<
                            FaceGeomHairRegionsPreviewArtifact>(
                            result.Artifacts.Length);
                for (int index = 0;
                     index < result.Artifacts.Length;
                     index++)
                {
                    FaceGeomHairRegionsPreviewArtifact artifact =
                        result.Artifacts[index];
                    ValidateSourceArtifact(artifact);
                    string extension =
                        Path.GetExtension(
                            artifact.Path.Value);
                    string fileName =
                        $"artifact-{index:D4}{extension}";
                    string temporaryPath =
                        Path.Combine(
                            temporaryDirectory.Value,
                            fileName);
                    CopyCreateNew(
                        artifact.Path.Value,
                        temporaryPath);
                    ValidateExactFile(
                        temporaryPath,
                        artifact.ByteLength,
                        artifact.Sha256);
                    cachedArtifacts.Add(
                        artifact with
                        {
                            Path = new WorkspacePath(
                                Path.Combine(
                                    finalDirectory.Value,
                                    fileName)),
                            Content = []
                        });
                }

                FaceGeomHairRegionsPreviewResult cachedResult =
                    result with
                    {
                        Artifacts =
                            cachedArtifacts.ToImmutable()
                    };
                long ordinal = nextOrdinal++;
                var document =
                    new CacheDocument(
                        CacheSchema,
                        ordinal,
                        key,
                        cachedResult);
                byte[] documentBytes =
                    JsonSerializer.SerializeToUtf8Bytes(
                        document,
                        JsonOptions);
                Sha256Hash documentHash =
                    Hash(documentBytes);
                string manifestName =
                    $"{ManifestPrefix}{documentHash.Value}{ManifestSuffix}";
                string temporaryManifest =
                    Path.Combine(
                        temporaryDirectory.Value,
                        manifestName);
                WriteCreateNew(
                    temporaryManifest,
                    documentBytes);

                long entrySize =
                    GetDirectorySize(
                        temporaryDirectory.Value);
                if (entrySize > capacityBytes)
                {
                    diagnostics.Add(
                        new Diagnostic(
                            "hair-regions-preview-cache-entry-too-large",
                            DiagnosticSeverity.Warning,
                            $"Preview cache entry requires {entrySize} bytes, above the {capacityBytes}-byte cap."));
                    CleanupTemporary(
                        temporaryDirectory,
                        diagnostics);
                    return new(
                        false,
                        0,
                        null,
                        diagnostics.ToImmutable());
                }

                while (checked(currentBytes + entrySize) >
                       capacityBytes)
                {
                    CacheEntry victim = entries.Values
                        .OrderBy(item =>
                            item.Document.CreatedOrdinal)
                        .ThenBy(item =>
                            item.Document.Key.Fingerprint.Value,
                            StringComparer.Ordinal)
                        .First();
                    if (!TryRemoveEntry(
                            victim,
                            out string? evictionFailure))
                    {
                        diagnostics.Add(
                            new Diagnostic(
                                "hair-regions-preview-cache-eviction-failed",
                                DiagnosticSeverity.Error,
                                $"Preview cache could not evict {victim.Directory.Value}: {evictionFailure}"));
                        CleanupTemporary(
                            temporaryDirectory,
                            diagnostics);
                        return new(
                            false,
                            0,
                            null,
                            diagnostics.ToImmutable());
                    }
                }

                if (Directory.Exists(
                        finalDirectory.Value))
                {
                    throw new IOException(
                        $"Cache destination already exists outside the admitted index: {finalDirectory.Value}");
                }

                Directory.Move(
                    temporaryDirectory.Value,
                    finalDirectory.Value);
                promoted = true;
                var entry = new CacheEntry(
                    finalDirectory,
                    new WorkspacePath(
                        Path.Combine(
                            finalDirectory.Value,
                            manifestName)),
                    documentHash,
                    entrySize,
                    document);
                ValidateEntry(entry);
                entries.Add(
                    key.Fingerprint.Value,
                    entry);
                currentBytes =
                    checked(currentBytes + entrySize);
                return new(
                    true,
                    entrySize,
                    cachedResult,
                    diagnostics.ToImmutable());
            }
            catch (Exception exception)
                when (IsExpectedFileFailure(exception))
            {
                diagnostics.Add(
                    new Diagnostic(
                        "hair-regions-preview-cache-store-failed",
                        DiagnosticSeverity.Error,
                        $"Preview cache refused the entry: {exception.Message}"));
                CleanupTemporary(
                    temporaryDirectory,
                    diagnostics);
                if (promoted &&
                    Directory.Exists(
                        finalDirectory.Value))
                {
                    try
                    {
                        DeleteExactDirectory(
                            finalDirectory);
                    }
                    catch (Exception cleanupException)
                        when (IsExpectedFileFailure(
                            cleanupException))
                    {
                        diagnostics.Add(
                            new Diagnostic(
                                "hair-regions-preview-cache-cleanup-failed",
                                DiagnosticSeverity.Error,
                                $"Promoted cache artifact survived at {finalDirectory.Value}: {cleanupException.Message}"));
                    }
                }

                return new(
                    false,
                    0,
                    null,
                    diagnostics.ToImmutable());
            }
        }
    }

    private void LoadExistingEntries()
    {
        lock (sync)
        {
            foreach (string file in
                     Directory.EnumerateFiles(
                         root.Value))
            {
                throw new InvalidDataException(
                    $"Unexpected file exists at the cache root: {file}");
            }

            foreach (string directory in
                     Directory.EnumerateDirectories(
                         root.Value)
                         .OrderBy(
                             value => value,
                             StringComparer.Ordinal))
            {
                string name =
                    Path.GetFileName(directory);
                if (name.StartsWith(
                        ".tmp-",
                        StringComparison.Ordinal))
                {
                    DeleteExactDirectory(
                        new WorkspacePath(directory));
                    continue;
                }

                if (!IsSha256(name))
                {
                    throw new InvalidDataException(
                        $"Unexpected directory exists at the cache root: {directory}");
                }

                try
                {
                    CacheEntry entry =
                        LoadEntry(
                            new WorkspacePath(directory));
                    string fingerprint =
                        entry.Document.Key.Fingerprint.Value;
                    if (!string.Equals(
                            fingerprint,
                            name,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Cache directory {name} does not match manifest key {fingerprint}.");
                    }

                    ValidateEntry(entry);
                    entries.Add(
                        fingerprint,
                        entry);
                    currentBytes =
                        checked(currentBytes + entry.Size);
                    nextOrdinal =
                        Math.Max(
                            nextOrdinal,
                            checked(
                                entry.Document.CreatedOrdinal +
                                1));
                }
                catch (Exception exception)
                    when (IsExpectedFileFailure(
                        exception))
                {
                    var invalidDirectory =
                        new WorkspacePath(directory);
                    try
                    {
                        DeleteExactDirectory(
                            invalidDirectory);
                    }
                    catch (Exception cleanupException)
                        when (IsExpectedFileFailure(
                            cleanupException))
                    {
                        throw new IOException(
                            $"Invalid cache entry survived at {directory}: {cleanupException.Message}",
                            cleanupException);
                    }

                    startupDiagnostics =
                        startupDiagnostics.Add(
                            new Diagnostic(
                                "hair-regions-preview-cache-invalid",
                                DiagnosticSeverity.Warning,
                                $"Cached preview {name} was rejected during admission: {exception.Message}"));
                }
            }
        }
    }

    private static CacheEntry LoadEntry(
        WorkspacePath directory)
    {
        EnsureOrdinaryDirectory(
            directory.Value);
        string[] manifests =
            Directory.GetFiles(
                directory.Value,
                $"{ManifestPrefix}*{ManifestSuffix}",
                SearchOption.TopDirectoryOnly);
        if (manifests.Length != 1)
        {
            throw new InvalidDataException(
                $"Cache entry {directory.Value} must contain exactly one hash-named manifest.");
        }

        string manifestName =
            Path.GetFileName(manifests[0]);
        string encodedHash =
            manifestName[
                ManifestPrefix.Length..
                ^ManifestSuffix.Length];
        if (!IsSha256(encodedHash))
        {
            throw new InvalidDataException(
                $"Cache manifest name is not hash-bound: {manifestName}");
        }

        byte[] bytes =
            File.ReadAllBytes(
                manifests[0]);
        Sha256Hash observedHash =
            Hash(bytes);
        if (!string.Equals(
                encodedHash,
                observedHash.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cache manifest hash mismatch at {manifests[0]}.");
        }

        CacheDocument? document =
            JsonSerializer.Deserialize<
                CacheDocument>(
                bytes,
                JsonOptions);
        if (document is null ||
            !string.Equals(
                document.Schema,
                CacheSchema,
                StringComparison.Ordinal) ||
            document.CreatedOrdinal <= 0)
        {
            throw new InvalidDataException(
                $"Cache manifest is malformed at {manifests[0]}.");
        }

        return new CacheEntry(
            directory,
            new WorkspacePath(manifests[0]),
            observedHash,
            GetDirectorySize(directory.Value),
            document);
    }

    private static void ValidateEntry(
        CacheEntry entry)
    {
        ValidateAuthorityBinding(
            entry.Document.Key,
            entry.Document.Result);
        EnsureOrdinaryDirectory(
            entry.Directory.Value);
        ValidateExactFile(
            entry.Manifest.Value,
            new FileInfo(entry.Manifest.Value).Length,
            entry.ManifestSha256);
        long observedSize =
            GetDirectorySize(
                entry.Directory.Value);
        if (observedSize != entry.Size)
        {
            throw new InvalidDataException(
                $"Cache entry length changed from {entry.Size} to {observedSize} bytes.");
        }

        foreach (FaceGeomHairRegionsPreviewArtifact artifact in
                 entry.Document.Result.Artifacts)
        {
            if (!artifact.Path.IsUnder(
                    entry.Directory) ||
                string.Equals(
                    artifact.Path.Value,
                    entry.Directory.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Cached artifact escapes its entry: {artifact.Path.Value}");
            }

            ValidateExactFile(
                artifact.Path.Value,
                artifact.ByteLength,
                artifact.Sha256);
        }
    }

    private static void ValidateSourceArtifact(
        FaceGeomHairRegionsPreviewArtifact artifact)
    {
        if (!File.Exists(
                artifact.Path.Value))
        {
            throw new FileNotFoundException(
                "Preview artifact is missing.",
                artifact.Path.Value);
        }

        FileAttributes attributes =
            File.GetAttributes(
                artifact.Path.Value);
        if ((attributes &
             (FileAttributes.Directory |
              FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"Preview artifact is not an ordinary file: {artifact.Path.Value}");
        }

        ValidateExactFile(
            artifact.Path.Value,
            artifact.ByteLength,
            artifact.Sha256);
    }

    private void EnforceCapacity()
    {
        lock (sync)
        {
            while (currentBytes > capacityBytes)
            {
                CacheEntry victim = entries.Values
                    .OrderBy(item =>
                        item.Document.CreatedOrdinal)
                    .ThenBy(item =>
                        item.Document.Key.Fingerprint.Value,
                        StringComparer.Ordinal)
                    .First();
                if (!TryRemoveEntry(
                        victim,
                        out string? failure))
                {
                    throw new IOException(
                        $"Preview cache exceeds its cap and {victim.Directory.Value} could not be evicted: {failure}");
                }
            }
        }
    }

    private bool TryRemoveEntry(
        CacheEntry entry,
        out string? failure)
    {
        try
        {
            DeleteExactDirectory(
                entry.Directory);
            entries.Remove(
                entry.Document.Key.Fingerprint.Value);
            currentBytes =
                checked(currentBytes - entry.Size);
            failure = null;
            return true;
        }
        catch (Exception exception)
            when (IsExpectedFileFailure(exception))
        {
            failure = exception.Message;
            return false;
        }
    }

    private void CleanupTemporary(
        WorkspacePath temporaryDirectory,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(
                temporaryDirectory.Value))
        {
            return;
        }

        try
        {
            DeleteExactDirectory(
                temporaryDirectory);
        }
        catch (Exception exception)
            when (IsExpectedFileFailure(exception))
        {
            diagnostics.Add(
                new Diagnostic(
                    "hair-regions-preview-cache-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"Temporary cache artifact survived at {temporaryDirectory.Value}: {exception.Message}"));
        }
    }

    private void DeleteExactDirectory(
        WorkspacePath directory)
    {
        if (!directory.IsUnder(root) ||
            string.Equals(
                directory.Value,
                root.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(
                    directory.Value),
                root.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Cache cleanup target is outside the exact cache root: {directory.Value}");
        }

        EnsureOrdinaryDirectory(
            directory.Value);
        _ = GetDirectorySize(
            directory.Value);
        Directory.Delete(
            directory.Value,
            recursive: true);
    }

    private static void EnsureOrdinaryDirectory(
        string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        FileAttributes attributes =
            File.GetAttributes(path);
        if ((attributes &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Reparse directories are not admitted in the preview cache: {path}");
        }
    }

    private static long GetDirectorySize(
        string rootPath)
    {
        long total = 0;
        var pending =
            new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            string directory =
                pending.Pop();
            EnsureOrdinaryDirectory(directory);
            foreach (string childDirectory in
                     Directory.EnumerateDirectories(
                         directory))
            {
                EnsureOrdinaryDirectory(
                    childDirectory);
                pending.Push(
                    childDirectory);
            }

            foreach (string file in
                     Directory.EnumerateFiles(
                         directory))
            {
                FileAttributes attributes =
                    File.GetAttributes(file);
                if ((attributes &
                     (FileAttributes.Directory |
                      FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidDataException(
                        $"Cache content is not an ordinary file: {file}");
                }

                total =
                    checked(
                        total +
                        new FileInfo(file).Length);
            }
        }

        return total;
    }

    private static void CopyCreateNew(
        string source,
        string destination)
    {
        using FileStream input = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using FileStream output = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void WriteCreateNew(
        string path,
        ReadOnlySpan<byte> bytes)
    {
        using FileStream output = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    private static void ValidateExactFile(
        string path,
        long expectedLength,
        Sha256Hash expectedSha256)
    {
        var info =
            new FileInfo(path);
        if (!info.Exists ||
            info.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"File length mismatch at {path}.");
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        Sha256Hash observed =
            new(
                Convert.ToHexString(
                    SHA256.HashData(stream)));
        if (observed != expectedSha256)
        {
            throw new InvalidDataException(
                $"File hash mismatch at {path}.");
        }
    }

    private static void ValidateAuthorityBinding(
        FaceGeomHairRegionsPreviewCacheKey key,
        FaceGeomHairRegionsPreviewResult result)
    {
        if (!result.Succeeded ||
            result.Artifacts.IsDefaultOrEmpty)
        {
            throw new InvalidDataException(
                "Cached preview is not a successful artifact-bearing result.");
        }

        FaceGeomHairRegionsPreviewCacheKey expectedKey =
            FaceGeomHairRegionsPreviewCacheKey.Create(
                key.SourceSha256,
                key.RequestSha256,
                key.ProposalSha256,
                key.IntakeSha256,
                key.RendererAuthoritySha256,
                key.RendererScriptSha256,
                key.TextureCatalogSha256,
                key.ResolvedTextureAuthoritySha256);
        if (expectedKey.Fingerprint !=
            key.Fingerprint)
        {
            throw new InvalidDataException(
                "Preview cache key fingerprint does not match its authority fields.");
        }

        FaceGeomHairRegionsPreviewEvidence evidence =
            result.Evidence ??
            throw new InvalidDataException(
                "Cached preview has no evidence authority.");
        if (key.SourceSha256 !=
                evidence.StagedFaceGeomSha256 ||
            key.ProposalSha256 !=
                evidence.ProposalSha256 ||
            key.IntakeSha256 !=
                evidence.IntakeSha256 ||
            key.RendererScriptSha256 !=
                evidence.RendererSha256 ||
            key.ResolvedTextureAuthoritySha256 !=
                evidence.TextureFingerprintSha256 ||
            key.RendererScriptSha256 !=
                evidence.RenderAuthority
                    .RendererScriptSha256 ||
            key.ResolvedTextureAuthoritySha256 !=
                evidence.RenderAuthority
                    .TextureSourceFingerprintSha256)
        {
            throw new InvalidDataException(
                "Cached preview evidence does not match its content-addressed authority key.");
        }

        FaceGeomHairRegionsPreviewArtifact[] combinedFaces =
            result.Artifacts
                .Where(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace)
                .ToArray();
        if (combinedFaces.Length != 1 ||
            combinedFaces[0].NonEmptyPixelCount is not
                > 0 ||
            evidence.DetectedFaceCount != 1 ||
            evidence.LandmarkCount <= 0 ||
            evidence.SemanticAnchorCount != 31)
        {
            throw new InvalidDataException(
                "Cached preview does not prove one nonempty recognizable face with the admitted landmark and 31-anchor evidence.");
        }
    }

    private static Sha256Hash Hash(
        ReadOnlySpan<byte> bytes) =>
        new(
            Convert.ToHexString(
                SHA256.HashData(bytes)));

    private WorkspacePath EntryDirectory(
        FaceGeomHairRegionsPreviewCacheKey key) =>
        new(
            Path.Combine(
                root.Value,
                key.Fingerprint.Value));

    private static bool IsSha256(
        string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f');

    private static bool IsExpectedFileFailure(
        Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException;

    private sealed record CacheDocument(
        string Schema,
        long CreatedOrdinal,
        FaceGeomHairRegionsPreviewCacheKey Key,
        FaceGeomHairRegionsPreviewResult Result);

    private sealed record CacheEntry(
        WorkspacePath Directory,
        WorkspacePath Manifest,
        Sha256Hash ManifestSha256,
        long Size,
        CacheDocument Document);

    private sealed class WorkspacePathConverter :
        JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType !=
                JsonTokenType.String)
            {
                throw new JsonException(
                    "Workspace path must be a string.");
            }

            return new WorkspacePath(
                reader.GetString() ??
                throw new JsonException(
                    "Workspace path must be a string."));
        }

        public override void Write(
            Utf8JsonWriter writer,
            WorkspacePath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.Value);
    }

    private sealed class Sha256HashConverter :
        JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType !=
                JsonTokenType.String)
            {
                throw new JsonException(
                    "SHA-256 must be a string.");
            }

            return new Sha256Hash(
                reader.GetString() ??
                throw new JsonException(
                    "SHA-256 must be a string."));
        }

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.Value);
    }

    private sealed class AssetPathConverter :
        JsonConverter<AssetPath>
    {
        public override AssetPath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType !=
                JsonTokenType.String)
            {
                throw new JsonException(
                    "Asset path must be a string.");
            }

            return new AssetPath(
                reader.GetString() ??
                throw new JsonException(
                    "Asset path must be a string."));
        }

        public override void Write(
            Utf8JsonWriter writer,
            AssetPath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.Value);
    }
}

internal readonly record struct
    FaceGeomHairRegionsPathObservation(
        bool FileExists,
        bool DirectoryExists,
        FileAttributes Attributes);

internal static class FaceGeomHairRegionsDesktopPathBoundary
{
    private const string CacheRelativeRoot =
        @".actorwright\work\desktop-hair-regions-cache";
    private const string PreviewRelativeRoot =
        @".actorwright\work\desktop-hair-regions-preview";

    public static WorkspacePath RequireExactLabRoot(
        WorkspacePath labRoot)
    {
        RequireKLocalOrdinarySyntax(
            labRoot,
            "lab root");
        if (!Directory.Exists(
                labRoot.Value))
        {
            throw new DirectoryNotFoundException(
                $"Pinned lab root does not exist: {labRoot.Value}");
        }

        RequireExistingAncestorsOrdinary(
            labRoot,
            labRoot,
            "lab root");
        return labRoot;
    }

    public static WorkspacePath RequireCacheRoot(
        WorkspacePath labRoot,
        WorkspacePath cacheRoot)
    {
        WorkspacePath exactLab =
            RequireExactLabRoot(labRoot);
        WorkspacePath admitted = new(
            Path.Combine(
                exactLab.Value,
                CacheRelativeRoot));
        RequireOwnedDescendant(
            exactLab,
            admitted,
            cacheRoot,
            "preview cache root");
        return admitted;
    }

    public static WorkspacePath PreviewRoot(
        WorkspacePath labRoot)
    {
        WorkspacePath exactLab =
            RequireExactLabRoot(labRoot);
        WorkspacePath admitted = new(
            Path.Combine(
                exactLab.Value,
                PreviewRelativeRoot));
        RequireKLocalOrdinarySyntax(
            admitted,
            "preview output root");
        RequireExistingAncestorsOrdinary(
            exactLab,
            admitted,
            "preview output root");
        return admitted;
    }

    public static void RequirePreviewOutput(
        WorkspacePath labRoot,
        WorkspacePath admittedRoot,
        WorkspacePath outputRoot)
    {
        WorkspacePath exactLab =
            RequireExactLabRoot(labRoot);
        WorkspacePath exactAdmitted =
            PreviewRoot(exactLab);
        if (!string.Equals(
                exactAdmitted.Value,
                admittedRoot.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Preview cleanup authority drifted from {exactAdmitted.Value} to {admittedRoot.Value}.");
        }

        RequireOwnedDescendant(
            exactLab,
            exactAdmitted,
            outputRoot,
            "preview output");
    }

    private static void RequireOwnedDescendant(
        WorkspacePath labRoot,
        WorkspacePath admittedRoot,
        WorkspacePath candidate,
        string purpose)
    {
        RequireKLocalOrdinarySyntax(
            candidate,
            purpose);
        if (!candidate.IsUnder(admittedRoot) ||
            string.Equals(
                candidate.Value,
                admittedRoot.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{purpose} must be a strict descendant of {admittedRoot.Value}; received {candidate.Value}.",
                nameof(candidate));
        }

        RequireExistingAncestorsOrdinary(
            labRoot,
            candidate,
            purpose);
    }

    private static void RequireKLocalOrdinarySyntax(
        WorkspacePath path,
        string purpose)
    {
        string value = path.Value;
        if (value.StartsWith(
                @"\\",
                StringComparison.Ordinal) ||
            value.StartsWith(
                @"\\?\",
                StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(
                @"\\.\",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetPathRoot(value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase) ||
            value.IndexOf(
                ':',
                3) >= 0)
        {
            throw new ArgumentException(
                $"{purpose} must be an ordinary K-local path without UNC, device, or ADS syntax: {value}.",
                nameof(path));
        }
    }

    private static void RequireExistingAncestorsOrdinary(
        WorkspacePath labRoot,
        WorkspacePath candidate,
        string purpose) =>
        RequireExistingAncestorsOrdinary(
            labRoot,
            candidate,
            purpose,
            ObservePath);

    internal static void RequireExistingAncestorsOrdinary(
        WorkspacePath labRoot,
        WorkspacePath candidate,
        string purpose,
        Func<string, FaceGeomHairRegionsPathObservation>
            observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        if (!candidate.IsUnder(labRoot))
        {
            throw new ArgumentException(
                $"{purpose} escapes the exact lab root: {candidate.Value}.",
                nameof(candidate));
        }

        string relative =
            Path.GetRelativePath(
                labRoot.Value,
                candidate.Value);
        string current =
            labRoot.Value;
        RequireOrdinaryDirectoryIfPresent(
            current,
            purpose,
            observe(current));
        if (relative == ".")
        {
            return;
        }

        foreach (string segment in
                 relative.Split(
                     [Path.DirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current =
                Path.Combine(
                    current,
                    segment);
            RequireOrdinaryDirectoryIfPresent(
                current,
                purpose,
                observe(current));
        }
    }

    private static void RequireOrdinaryDirectoryIfPresent(
        string path,
        string purpose,
        FaceGeomHairRegionsPathObservation observation)
    {
        if (observation.FileExists)
        {
            throw new InvalidDataException(
                $"{purpose} crosses a file where a directory is required: {path}.");
        }

        if (!observation.DirectoryExists)
        {
            return;
        }

        if ((observation.Attributes &
             FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{purpose} crosses a reparse directory: {path}.");
        }
    }

    private static FaceGeomHairRegionsPathObservation
        ObservePath(
            string path)
    {
        bool fileExists =
            File.Exists(path);
        bool directoryExists =
            Directory.Exists(path);
        FileAttributes attributes =
            directoryExists
                ? File.GetAttributes(path)
                : 0;
        return new(
            fileExists,
            directoryExists,
            attributes);
    }
}
