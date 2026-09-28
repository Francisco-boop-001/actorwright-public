using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using SkiaSharp;

namespace NpcManager.Rendering;

public sealed partial class BlenderFaceGeomHairRegionsRenderer
{
    private static void ValidateStatusAuthority(
        HairRenderStatus status,
        FaceGeomHairRegionsRenderRequest request,
        ImmutableArray<HairTextureRequestRow>
            stagedTextures,
        WorkspacePath privateProfileRoot,
        WorkspacePath pythonCacheRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(
                status.Schema,
                StatusSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                status.CandidateSha256,
                request.Source.Candidate.Sha256.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                status.TextureFingerprintSha256,
                request.Source.TextureFingerprintSha256.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(
                status.LoadedTextureObservationFingerprintSha256) ||
            status.FaceGeomImportCount != 1 ||
            status.NifImportInvocationCount != 1 ||
            !status.FaceCameraUsedAuthoritativeGeometry ||
            status.TintedMaterialCount <= 0)
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-status-authority",
                "Renderer status did not bind one exact candidate/import, texture inventory, face-derived framing, and tint application."));

        ValidateLoadedTextureAuthority(
            status,
            stagedTextures,
            diagnostics);
        ValidatePyniflyRuntimeAuthority(
            status,
            privateProfileRoot,
            pythonCacheRoot,
            diagnostics);

        HairRegionMapping[] mappings =
            status.RegionMappings ?? [];
        FaceGeomHairRegionsRegion[] expected =
            request.Regions
                .OrderBy(item => item.ShapeBlockId)
                .ToArray();
        if (mappings.Length != expected.Length)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-status-mapping",
                "Renderer status did not return one mapping per region."));
            return;
        }
        for (int index = 0; index < expected.Length; index++)
        {
            HairRegionMapping observed = mappings[index];
            FaceGeomHairRegionsRegion region = expected[index];
            if (!string.Equals(
                    observed.StructuralId,
                    region.StructuralId,
                    StringComparison.Ordinal) ||
                observed.ShapeBlockId !=
                    region.ShapeBlockId ||
                !string.Equals(
                    observed.ShapeBlockType,
                    region.ShapeBlockType,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    observed.Name,
                    region.Name,
                    StringComparison.Ordinal) ||
                observed.DuplicateNameOrdinal !=
                    region.DuplicateNameOrdinal ||
                observed.ShaderBlockId !=
                    region.ShaderBlockId ||
                !string.Equals(
                    observed.ShaderBlockType,
                    region.ShaderBlockType,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(
                    observed.ObjectName))
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-status-mapping",
                    $"Renderer mapping for '{region.StructuralId}' drifted from structural authority."));
        }
    }

    private static void ValidateLoadedTextureAuthority(
        HairRenderStatus status,
        ImmutableArray<HairTextureRequestRow> textures,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        LoadedTextureObservation[] observations =
            status.LoadedTextures ?? [];
        if (observations.Length > 4096 ||
            (textures.Length == 0) !=
                (observations.Length == 0))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-loaded-textures",
                "Renderer returned an invalid observed texture-node count."));
            return;
        }
        Dictionary<string, HairTextureRequestRow> expected =
            textures.ToDictionary(
                item => item.AssetPath,
                StringComparer.OrdinalIgnoreCase);
        var observedAssets = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(
            StringComparer.Ordinal);
        using IncrementalHash fingerprint =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (LoadedTextureObservation observation in
                 observations)
        {
            bool sampledImage = string.Equals(
                observation.BindingMode,
                nameof(FaceGeomHairTextureBindingMode.SampledImage),
                StringComparison.Ordinal);
            bool boundUnmodeled = string.Equals(
                observation.BindingMode,
                nameof(FaceGeomHairTextureBindingMode.BoundUnmodeled),
                StringComparison.Ordinal);
            bool allowedUnmodeledSemantic =
                string.Equals(
                    observation.BindingSemantic,
                    "BSShaderTextureSet_EnvMap",
                    StringComparison.Ordinal) ||
                string.Equals(
                    observation.BindingSemantic,
                    "BSShaderTextureSet_EnvMask",
                    StringComparison.Ordinal);
            bool validLoadedImagePath =
                sampledImage &&
                expected.TryGetValue(
                    observation.AssetPath ?? "",
                    out HairTextureRequestRow? expectedRow) &&
                IsExactLoadedImagePath(
                    observation.LoadedImagePath,
                    expectedRow.PreviewPath);
            if (string.IsNullOrWhiteSpace(
                    observation.AssetPath) ||
                !expected.TryGetValue(
                    observation.AssetPath,
                    out HairTextureRequestRow? row) ||
                !string.Equals(
                    observation.ProviderKind,
                    row.ProviderKind,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    observation.Provider,
                    row.Provider,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    observation.SourceSha256,
                    row.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                observation.SourceBytes != row.Bytes ||
                !string.Equals(
                    observation.PreviewSha256,
                    row.PreviewSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                observation.PreviewBytes !=
                    row.PreviewBytes ||
                !string.Equals(
                    observation.DecodeKind,
                    row.DecodeKind,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(
                    observation.BindingSemantic) ||
                (!sampledImage && !boundUnmodeled) ||
                (sampledImage && allowedUnmodeledSemantic) ||
                (sampledImage && !validLoadedImagePath) ||
                (boundUnmodeled &&
                    (!allowedUnmodeledSemantic ||
                     observation.LoadedImagePath is not null)) ||
                string.IsNullOrWhiteSpace(
                    observation.ObjectName) ||
                string.IsNullOrWhiteSpace(
                    observation.MaterialName) ||
                string.IsNullOrWhiteSpace(
                    observation.NodeName))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-loaded-textures",
                    "Renderer returned a texture observation outside the closed staged authority."));
                continue;
            }
            string identity =
                $"{observation.BindingMode}\0" +
                $"{observation.BindingSemantic}\0" +
                $"{observation.ObjectName}\0" +
                $"{observation.MaterialName}\0" +
                $"{observation.NodeName}";
            if (!identities.Add(identity))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-loaded-textures",
                    "Renderer returned a duplicate material/node observation."));
                continue;
            }
            observedAssets.Add(row.AssetPath);
            string canonical =
                $"{row.AssetPath}\0{row.ProviderKind}\0" +
                $"{row.Provider}\0{row.Sha256}\0" +
                $"{row.Bytes}\0{row.PreviewSha256}\0" +
                $"{row.PreviewBytes}\0{row.DecodeKind}\0" +
                $"{observation.BindingMode}\0" +
                $"{observation.BindingSemantic}\0" +
                $"{observation.ObjectName}\0" +
                $"{observation.MaterialName}\0" +
                $"{observation.NodeName}\0" +
                $"{observation.LoadedImagePath ?? ""}\n";
            fingerprint.AppendData(
                Encoding.UTF8.GetBytes(canonical));
        }
        if (observedAssets.Count != expected.Count ||
            !observedAssets.SetEquals(expected.Keys) ||
            !string.Equals(
                Convert.ToHexString(
                    fingerprint.GetHashAndReset()),
                status
                    .LoadedTextureObservationFingerprintSha256,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-loaded-textures",
                "Renderer did not prove every declared texture through the closed sampled-image or bound-unmodeled material authority."));
    }

    private static bool IsExactLoadedImagePath(
        string? observed,
        string expected)
    {
        if (string.IsNullOrWhiteSpace(observed))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(observed),
                expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private FaceGeomHairRegionsRenderAuthority
        BuildRenderAuthority(
            HairRenderStatus status,
            ImmutableArray<HairTextureRequestRow> textures,
            ProfileSnapshot openingProfile)
    {
        LoadedTextureObservation[] observations =
            status.LoadedTextures ?? [];
        ImmutableArray<FaceGeomHairTextureEvidence>
            textureEvidence = textures.Select(texture =>
                new FaceGeomHairTextureEvidence(
                    new AssetPath(texture.AssetPath),
                    Enum.Parse<AssetProviderKind>(
                        texture.ProviderKind,
                        ignoreCase: false),
                    texture.Provider,
                    new Sha256Hash(texture.Sha256),
                    texture.Bytes,
                    new Sha256Hash(
                        texture.PreviewSha256),
                    texture.PreviewBytes,
                    texture.DecodeKind,
                    observations
                        .Where(item =>
                            string.Equals(
                                item.AssetPath,
                                texture.AssetPath,
                                StringComparison.OrdinalIgnoreCase))
                        .Select(item =>
                            new FaceGeomHairTextureBindingEvidence(
                                Enum.Parse<
                                    FaceGeomHairTextureBindingMode>(
                                    item.BindingMode!,
                                    ignoreCase: false),
                                item.BindingSemantic!,
                                item.ObjectName!,
                                item.MaterialName!,
                                item.NodeName!))
                        .OrderBy(
                            item => item.Mode)
                        .ThenBy(
                            item => item.BindingSemantic,
                            StringComparer.Ordinal)
                        .ThenBy(
                            item => item.ObjectName,
                            StringComparer.Ordinal)
                        .ThenBy(
                            item => item.MaterialName,
                            StringComparer.Ordinal)
                        .ThenBy(
                            item => item.NodeName,
                            StringComparer.Ordinal)
                        .ToImmutableArray()))
            .ToImmutableArray();
        return new FaceGeomHairRegionsRenderAuthority(
            expectedBlenderSha256,
            expectedPyniflySha256,
            expectedPyniflyProfileSha256,
            expectedScriptSha256,
            expectedTexconvSha256,
            new Sha256Hash(
                status.TextureFingerprintSha256!),
            new Sha256Hash(
                status
                    .LoadedTextureObservationFingerprintSha256!),
            new Sha256Hash(
                status
                    .PyniflyModuleSourceFingerprintSha256!),
            status.PyniflyModuleCount,
            openingProfile.Fingerprint,
            openingProfile.FileCount,
            textureEvidence,
            (status.PyniflyModules ?? [])
                .Select(item =>
                    new FaceGeomHairPyniflyModuleEvidence(
                        item.ModuleName!,
                        Enum.Parse<
                            FaceGeomHairPyniflyModuleKind>(
                            item.ModuleKind!,
                            ignoreCase: false),
                        item.RelativeSource!,
                        new Sha256Hash(
                            item.SourceSha256!)))
                .OrderBy(
                    item => item.ModuleName,
                    StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static void ValidatePyniflyRuntimeAuthority(
        HairRenderStatus status,
        WorkspacePath privateProfileRoot,
        WorkspacePath pythonCacheRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        PyniflyModuleObservation[] modules =
            status.PyniflyModules ?? [];
        if (!TryGetFullPath(
                status.PythonCachePrefix,
                out string observedPythonCachePrefix) ||
            !string.Equals(
                observedPythonCachePrefix,
                pythonCacheRoot.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !status.PythonDontWriteBytecode ||
            status.PyniflyModuleCount != modules.Length ||
            modules.Length is <= 0 or > 512 ||
            string.IsNullOrWhiteSpace(
                status
                    .PyniflyModuleSourceFingerprintSha256))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-pynifly-runtime",
                "Renderer did not return a bounded source-only PyNifly module closure."));
            return;
        }
        string addonRoot = Path.Combine(
            privateProfileRoot.Value,
            "scripts",
            "addons",
            "io_scene_nifly");
        var moduleNames = new HashSet<string>(
            StringComparer.Ordinal);
        using IncrementalHash fingerprint =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (PyniflyModuleObservation module in
                 modules)
        {
            string relative =
                module.RelativeSource ?? "";
            bool sourceFile = string.Equals(
                module.ModuleKind,
                nameof(
                    FaceGeomHairPyniflyModuleKind
                        .SourceFile),
                StringComparison.Ordinal);
            bool namespaceDirectory = string.Equals(
                module.ModuleKind,
                nameof(
                    FaceGeomHairPyniflyModuleKind
                        .NamespaceDirectory),
                StringComparison.Ordinal);
            string full = "";
            bool valid =
                !string.IsNullOrWhiteSpace(
                    module.ModuleName) &&
                (module.ModuleName ==
                     "io_scene_nifly" ||
                 module.ModuleName.StartsWith(
                     "io_scene_nifly.",
                     StringComparison.Ordinal)) &&
                moduleNames.Add(module.ModuleName) &&
                !relative.Split('/')
                    .Contains(
                        "__pycache__",
                        StringComparer.Ordinal) &&
                (sourceFile || namespaceDirectory) &&
                TryResolveCanonicalPyniflyModuleSource(
                    addonRoot,
                    module.ModuleName,
                    relative,
                    sourceFile,
                    namespaceDirectory,
                    out full);
            if (valid && sourceFile)
            {
                valid =
                    relative.EndsWith(
                        ".py",
                        StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(full) &&
                    !Directory.Exists(full) &&
                    string.Equals(
                        HashFile(full).Hash.Value,
                        module.SourceSha256,
                        StringComparison.OrdinalIgnoreCase);
            }
            else if (valid && namespaceDirectory)
            {
                valid =
                    relative.EndsWith('/') &&
                    Directory.Exists(full) &&
                    !File.Exists(full) &&
                    module.CachedPath is null &&
                    TryFingerprintSourceDirectory(
                        full,
                        out string directoryFingerprint) &&
                    string.Equals(
                        directoryFingerprint,
                        module.SourceSha256,
                        StringComparison.OrdinalIgnoreCase);
            }
            if (valid && sourceFile &&
                !string.IsNullOrWhiteSpace(
                    module.CachedPath))
            {
                valid =
                    TryGetFullPath(
                        module.CachedPath,
                        out string cached) &&
                    new WorkspacePath(cached).IsUnder(
                        pythonCacheRoot) &&
                    !new WorkspacePath(cached).IsUnder(
                        privateProfileRoot) &&
                    Path.GetExtension(cached).Equals(
                        ".pyc",
                        StringComparison.OrdinalIgnoreCase);
            }
            if (!valid)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-pynifly-runtime",
                    "Renderer returned a PyNifly module outside the exact private source profile/cache boundary."));
                continue;
            }
            string canonical =
                $"{module.ModuleName}\0{module.ModuleKind}\0" +
                $"{relative}\0" +
                $"{module.SourceSha256}\n";
            fingerprint.AppendData(
                Encoding.UTF8.GetBytes(canonical));
        }
        if (!string.Equals(
                Convert.ToHexString(
                    fingerprint.GetHashAndReset()),
                status
                    .PyniflyModuleSourceFingerprintSha256,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-pynifly-runtime",
                "Renderer PyNifly module-source fingerprint is inconsistent."));
    }

    private static bool TryGetFullPath(
        string? path,
        out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryResolveCanonicalPyniflyModuleSource(
        string addonRoot,
        string moduleName,
        string relative,
        bool sourceFile,
        bool namespaceDirectory,
        out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relative) ||
            relative.Contains('\\') ||
            Path.IsPathFullyQualified(relative) ||
            !IsCanonicalPyniflyModuleMapping(
                moduleName,
                relative,
                sourceFile,
                namespaceDirectory))
            return false;
        try
        {
            if (!TryGetFullPath(
                    Path.Combine(
                        addonRoot,
                        relative.Replace(
                            '/',
                            Path.DirectorySeparatorChar)),
                    out fullPath))
                return false;
            var root = new WorkspacePath(addonRoot);
            var resolved = new WorkspacePath(fullPath);
            if (!resolved.IsUnder(root))
                return false;
            string canonicalRelative =
                Path.GetRelativePath(
                        addonRoot,
                        fullPath)
                    .Replace('\\', '/');
            if (namespaceDirectory)
                canonicalRelative =
                    canonicalRelative.TrimEnd('/') + "/";
            return string.Equals(
                relative,
                canonicalRelative,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            fullPath = "";
            return false;
        }
    }

    private static bool IsCanonicalPyniflyModuleMapping(
        string moduleName,
        string relative,
        bool sourceFile,
        bool namespaceDirectory)
    {
        const string rootModule = "io_scene_nifly";
        if (string.Equals(
                moduleName,
                rootModule,
                StringComparison.Ordinal))
            return sourceFile &&
                !namespaceDirectory &&
                string.Equals(
                    relative,
                    "__init__.py",
                    StringComparison.Ordinal);
        const string prefix = rootModule + ".";
        if (!moduleName.StartsWith(
                prefix,
                StringComparison.Ordinal))
            return false;
        string suffix = moduleName[prefix.Length..];
        string[] segments = suffix.Split('.');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment.Any(character =>
                    !(char.IsLetterOrDigit(character) ||
                      character == '_'))))
            return false;
        string relativeModule = string.Join(
            "/",
            segments);
        if (namespaceDirectory)
            return !sourceFile &&
                string.Equals(
                    relative,
                    relativeModule + "/",
                    StringComparison.Ordinal);
        return sourceFile &&
            (string.Equals(
                 relative,
                 relativeModule + ".py",
                 StringComparison.Ordinal) ||
             string.Equals(
                 relative,
                 relativeModule + "/__init__.py",
                 StringComparison.Ordinal));
    }

    private static bool TryFingerprintSourceDirectory(
        string directory,
        out string fingerprint)
    {
        fingerprint = "";
        var files = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        long totalBytes = 0;
        try
        {
            while (pending.Count > 0)
            {
                (string current, int depth) = pending.Pop();
                if (depth > 16 ||
                    File.GetAttributes(current).HasFlag(
                        FileAttributes.ReparsePoint))
                    return false;
                foreach (string entry in
                         Directory.EnumerateFileSystemEntries(
                             current))
                {
                    FileAttributes attributes =
                        File.GetAttributes(entry);
                    if (attributes.HasFlag(
                            FileAttributes.ReparsePoint))
                        return false;
                    if (attributes.HasFlag(
                            FileAttributes.Directory))
                    {
                        if (!Path.GetFileName(entry).Equals(
                                "__pycache__",
                                StringComparison.Ordinal))
                            pending.Push((entry, depth + 1));
                        continue;
                    }
                    if (Path.GetExtension(entry).Equals(
                            ".pyc",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var info = new FileInfo(entry);
                    if (info.Length > 4L * 1024L * 1024L)
                        return false;
                    totalBytes = checked(
                        totalBytes + info.Length);
                    if (totalBytes > 64L * 1024L * 1024L)
                        return false;
                    files.Add(entry);
                    if (files.Count > 4096)
                        return false;
                }
            }
            if (files.Count == 0)
                return false;
            using IncrementalHash hash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            foreach (string file in files.OrderBy(
                         item => Path.GetRelativePath(
                                 directory,
                                 item)
                             .Replace('\\', '/'),
                         StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(
                        directory,
                        file)
                    .Replace('\\', '/');
                (long length, Sha256Hash fileHash) = HashFile(file);
                string row =
                    $"{relative}\0{length}\0" +
                    $"{fileHash.Value}\n";
                hash.AppendData(
                    Encoding.UTF8.GetBytes(row));
            }
            fingerprint = Convert.ToHexString(
                hash.GetHashAndReset());
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                OverflowException)
        {
            return false;
        }
    }

    private static async ValueTask<
        ImmutableArray<FaceGeomHairRegionsPreviewArtifact>>
        ValidateArtifactsAsync(
            HairRenderStatus status,
            FaceGeomHairRegionsRenderRequest request,
            WorkspacePath renderOutputRoot,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        HairArtifact[] rows = status.Artifacts ?? [];
        int expectedCount =
            checked(2 + request.Regions.Length * 2);
        if (rows.Length != expectedCount)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-artifact-count",
                $"Renderer returned {rows.Length} artifacts; expected {expectedCount}."));
            return [];
        }
        var expected = new Dictionary<
            (string Kind, string? StructuralId),
            (string Name, int Width, int? Height)>();
        expected.Add(
            ("CombinedFace", null),
            ("combined-face.png", 900, 900));
        expected.Add(
            ("ContactSheet", null),
            ("contact-sheet.png", -1, null));
        foreach (FaceGeomHairRegionsRegion region in
                 request.Regions)
        {
            expected.Add(
                ("RegionThumbnail", region.StructuralId),
                ($"region-shape-{region.ShapeBlockId:000000}.png",
                    900,
                    900));
            expected.Add(
                ("RegionMask", region.StructuralId),
                ($"region-shape-{region.ShapeBlockId:000000}.mask.png",
                    900,
                    900));
        }

        var declaredSeen = new HashSet<
            (string Kind, string? StructuralId)>();
        var declaredLengths =
            ImmutableArray.CreateBuilder<long>(rows.Length);
        foreach (HairArtifact row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (row.Kind ?? "", row.StructuralId);
            if (!declaredSeen.Add(key) ||
                !expected.TryGetValue(
                    key,
                    out (string Name, int Width, int? Height)
                        authority))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-identity",
                    "Renderer returned an unknown or duplicate artifact identity."));
                continue;
            }
            string full;
            try
            {
                full = Path.GetFullPath(row.Path ?? "");
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-path",
                    exception.Message));
                continue;
            }
            if (!string.Equals(
                    Path.GetDirectoryName(full),
                    renderOutputRoot.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(full),
                    authority.Name,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-path",
                    $"Artifact '{key}' is outside its exact public filename."));
                continue;
            }
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-read",
                    $"Artifact '{key}' is absent."));
                continue;
            }
            declaredLengths.Add(info.Length);
        }
        if (HasErrors(diagnostics))
            return [];
        if (!FitsRenderedBundleBudget(
                declaredLengths.ToImmutable(),
                out _))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-artifact-budget",
                "The complete declared artifact tree exceeds the per-image or cumulative in-memory handoff budget."));
            return [];
        }

        var artifacts = ImmutableArray.CreateBuilder<
            FaceGeomHairRegionsPreviewArtifact>();
        var seen = new HashSet<
            (string Kind, string? StructuralId)>();
        long cumulativeBytes = 0;
        foreach (HairArtifact row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (row.Kind ?? "", row.StructuralId);
            if (!seen.Add(key) ||
                !expected.TryGetValue(
                    key,
                    out (string Name, int Width, int? Height)
                        authority))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-identity",
                    "Renderer returned an unknown or duplicate artifact identity."));
                continue;
            }
            string full = Path.GetFullPath(
                row.Path ?? "");
            if (!string.Equals(
                    Path.GetDirectoryName(full),
                    renderOutputRoot.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(full),
                    authority.Name,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-path",
                    $"Artifact '{key}' is outside its exact public filename."));
                continue;
            }
            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(
                    new WorkspacePath(full),
                    MaximumRenderedImageBytes,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-read",
                    exception.Message));
                continue;
            }
            cumulativeBytes = checked(
                cumulativeBytes + bytes.LongLength);
            if (cumulativeBytes >
                MaximumRenderedBundleBytes)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-budget",
                    "Renderer artifact contents exceed the bounded in-memory handoff budget."));
                continue;
            }
            Sha256Hash hash = Hash(bytes);
            if (!string.Equals(
                    hash.Value,
                    row.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-hash",
                    $"Artifact '{key}' differs from renderer status."));
                continue;
            }
            using SKBitmap? bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null ||
                (authority.Width > 0 &&
                 bitmap.Width != authority.Width) ||
                (authority.Height is int requiredHeight &&
                 bitmap.Height != requiredHeight) ||
                (key.Item1 == "ContactSheet" &&
                 (bitmap.Width is <= 0 or > 1800 ||
                  bitmap.Height is <= 0 or > 1800)))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-artifact-image",
                    $"Artifact '{key}' is not a bounded exact PNG."));
                continue;
            }
            long? nonempty = null;
            if (key.Item1 is
                "RegionThumbnail" or "RegionMask")
            {
                nonempty = CountNonempty(
                    bitmap,
                    mask:
                        key.Item1 == "RegionMask");
                if (nonempty <= 0 ||
                    row.NonEmptyPixelCount != nonempty)
                {
                    diagnostics.Add(Error(
                        "facegeom-hair-regions-render-artifact-mask",
                        $"Artifact '{key}' has false or empty pixel evidence."));
                    continue;
                }
            }
            FaceGeomHairRegionsPreviewArtifactKind kind =
                key.Item1 switch
                {
                    "CombinedFace" =>
                        FaceGeomHairRegionsPreviewArtifactKind
                            .CombinedFace,
                    "RegionThumbnail" =>
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionThumbnail,
                    "RegionMask" =>
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionMask,
                    "ContactSheet" =>
                        FaceGeomHairRegionsPreviewArtifactKind
                            .ContactSheet,
                    _ => throw new InvalidDataException(
                        "Unreachable artifact kind.")
                };
            artifacts.Add(new(
                kind,
                key.StructuralId,
                new WorkspacePath(Path.Combine(
                    request.OutputRoot.Value,
                    authority.Name)),
                bytes.LongLength,
                hash,
                nonempty,
                bytes.ToImmutableArray()));
        }
        string[] entries = Directory
            .EnumerateFiles(
                renderOutputRoot.Value,
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] declared = rows
            .Select(item => Path.GetFullPath(
                item.Path ?? ""))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!entries.SequenceEqual(
                declared,
                StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-artifact-tree",
                "Public output tree differs from the exact declared artifact set."));
        return artifacts.ToImmutable();
    }

    internal static bool FitsRenderedBundleBudget(
        ImmutableArray<long> artifactLengths,
        out long totalBytes)
    {
        totalBytes = 0;
        if (artifactLengths.IsDefaultOrEmpty)
            return false;
        foreach (long length in artifactLengths)
        {
            if (length is <= 0 or
                > MaximumRenderedImageBytes)
                return false;
            try
            {
                totalBytes = checked(totalBytes + length);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (totalBytes > MaximumRenderedBundleBytes)
                return false;
        }
        return true;
    }

    private static long CountNonempty(
        SKBitmap bitmap,
        bool mask)
    {
        long count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (mask)
                {
                    if (pixel.Red > 160 &&
                        pixel.Green > 160 &&
                        pixel.Blue > 160)
                        count++;
                }
                else if (
                    pixel.Alpha >= 16 &&
                    Math.Max(
                        pixel.Red,
                        Math.Max(
                            pixel.Green,
                            pixel.Blue)) > 28)
                    count++;
            }
        }
        return count;
    }

    private static (long Length, Sha256Hash Hash) HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        Sha256Hash hash = new(Convert.ToHexString(SHA256.HashData(stream)));
        return (stream.Position, hash);
    }

    private static async ValueTask<HairRenderStatus?>
        ReadStatusAsync(
            WorkspacePath statusPath,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await ReadBoundedAsync(
                statusPath,
                MaximumStatusBytes,
                cancellationToken);
            return JsonSerializer.Deserialize<HairRenderStatus>(
                bytes,
                JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-status-invalid",
                exception.Message));
            return null;
        }
    }

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed record HairRenderStatus(
        string? Schema,
        bool Rendered,
        string? BlenderVersion,
        string? RenderEngine,
        string? CandidateSha256,
        string? TextureFingerprintSha256,
        string? LoadedTextureObservationFingerprintSha256,
        LoadedTextureObservation[]? LoadedTextures,
        string? PythonCachePrefix,
        bool PythonDontWriteBytecode,
        int PyniflyModuleCount,
        string? PyniflyModuleSourceFingerprintSha256,
        PyniflyModuleObservation[]? PyniflyModules,
        int FaceGeomImportCount,
        int NifImportInvocationCount,
        bool FaceCameraUsedAuthoritativeGeometry,
        int TintedMaterialCount,
        HairRegionMapping[]? RegionMappings,
        HairArtifact[]? Artifacts,
        string? Error = null,
        string? Traceback = null);

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed record LoadedTextureObservation(
        string? AssetPath,
        string? ProviderKind,
        string? Provider,
        string? SourceSha256,
        long SourceBytes,
        string? PreviewSha256,
        long PreviewBytes,
        string? DecodeKind,
        string? BindingMode,
        string? BindingSemantic,
        string? ObjectName,
        string? MaterialName,
        string? NodeName,
        string? LoadedImagePath);

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed record PyniflyModuleObservation(
        string? ModuleName,
        string? ModuleKind,
        string? RelativeSource,
        string? SourceSha256,
        string? CachedPath);

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed record HairRegionMapping(
        string? StructuralId,
        int ShapeBlockId,
        string? ShapeBlockType,
        string? Name,
        int DuplicateNameOrdinal,
        int ShaderBlockId,
        string? ShaderBlockType,
        string? ObjectName);

    [JsonUnmappedMemberHandling(
        JsonUnmappedMemberHandling.Disallow)]
    private sealed record HairArtifact(
        string? Kind,
        string? StructuralId,
        string? Path,
        string? Sha256,
        int Width,
        int Height,
        long? NonEmptyPixelCount);

    private sealed record HairTextureRequestRow(
        string AssetPath,
        string ProviderKind,
        string Provider,
        string Sha256,
        long Bytes,
        string SourcePath,
        string PreviewPath,
        string PreviewSha256,
        long PreviewBytes,
        string DecodeKind);

    private sealed record DecodedTexture(
        WorkspacePath Path,
        Sha256Hash Sha256,
        long Bytes);

    private sealed record ProfileSnapshot(
        int FileCount,
        int DirectoryCount,
        long Bytes,
        Sha256Hash Fingerprint);
}
