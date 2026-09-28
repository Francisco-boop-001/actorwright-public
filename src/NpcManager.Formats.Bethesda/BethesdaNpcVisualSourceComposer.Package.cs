using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaNpcVisualSourceComposer
{
    private async ValueTask<PackageOverlay?> ReadPackageOverlayAsync(
        NpcVisualPreviewPackageOverlay? requested,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (requested is null) return null;
        WorkspacePath manifest = requested.ManifestPath;
        if (!manifest.IsUnder(labRoot) ||
            !File.Exists(manifest.Value) ||
            Directory.Exists(manifest.Value))
        {
            diagnostics.Add(Error(
                "npc-preview-package-manifest-invalid",
                "The optional package manifest must be an existing K-local file."));
            return null;
        }
        diagnostics.AddRange(policy.EvaluateReadRoot(
            labRoot, manifest));
        Sha256Hash observed =
            await HashFileAsync(manifest, cancellationToken);
        if (observed != requested.ExpectedManifestSha256)
        {
            diagnostics.Add(Error(
                "npc-preview-package-manifest-stale",
                "The package manifest changed after its expected hash was recorded."));
            return null;
        }
        try
        {
            await using FileStream stream = File.OpenRead(
                manifest.Value);
            using JsonDocument document =
                await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling =
                            JsonCommentHandling.Disallow,
                        MaxDepth = 64
                    },
                    cancellationToken);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty(
                    "schemaVersion", out JsonElement schema) ||
                !schema.TryGetInt32(out int schemaVersion) ||
                !root.TryGetProperty(
                    "artifacts", out JsonElement artifacts) ||
                artifacts.ValueKind != JsonValueKind.Array ||
                artifacts.GetArrayLength() is 0 or > 10000)
            {
                diagnostics.Add(Error(
                    "npc-preview-package-manifest-schema",
                    "Only a bounded Manager package manifest schema 1 is supported."));
                return null;
            }
            string? artifactKind =
                root.TryGetProperty(
                    "artifactKind",
                    out JsonElement artifactKindElement) &&
                artifactKindElement.ValueKind ==
                JsonValueKind.String
                    ? artifactKindElement.GetString()
                    : null;
            bool supportedSchema =
                schemaVersion == 1 ||
                (schemaVersion is 2 or 3 &&
                 string.Equals(
                     artifactKind,
                     "skyrim-paired-follower-finish-package",
                     StringComparison.Ordinal));
            if (!supportedSchema)
            {
                diagnostics.Add(Error(
                    "npc-preview-package-manifest-schema",
                    "Only Manager package schema 1 and paired-follower package schemas 2/3 are supported."));
                return null;
            }
            string packageRoot =
                Path.GetDirectoryName(manifest.Value)!;
            var files =
                ImmutableArray.CreateBuilder<PackageFile>();
            var relativePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement row in artifacts.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative =
                    row.GetProperty("relativePath").GetString() ??
                    "";
                string kind =
                    row.TryGetProperty(
                        "kind", out JsonElement kindElement) &&
                    kindElement.ValueKind == JsonValueKind.String
                        ? kindElement.GetString() ?? ""
                        : InferPackageKind(relative);
                long bytes =
                    row.GetProperty("byteLength").GetInt64();
                var hash = new Sha256Hash(
                    row.GetProperty("sha256").GetString() ?? "");
                if (!relativePaths.Add(relative))
                {
                    diagnostics.Add(Error(
                        "npc-preview-package-artifact-duplicate",
                        $"Package artifact '{relative}' is declared more than once."));
                    continue;
                }
                string full = Path.GetFullPath(Path.Combine(
                    packageRoot,
                    relative.Replace(
                        '/', Path.DirectorySeparatorChar)));
                var fullPath = new WorkspacePath(full);
                var rootPath = new WorkspacePath(packageRoot);
                diagnostics.AddRange(
                    policy.EvaluateReadRoot(
                        labRoot,
                        fullPath));
                if (!fullPath.IsUnder(rootPath) ||
                    !File.Exists(full) ||
                    Directory.Exists(full) ||
                    new FileInfo(full).Length != bytes ||
                    await HashFileAsync(
                        fullPath, cancellationToken) != hash)
                {
                    diagnostics.Add(Error(
                        "npc-preview-package-artifact-stale",
                        $"Package artifact '{relative}' is missing, escapes its root, or differs from the manifest."));
                    continue;
                }
                files.Add(new(
                    kind, relative, bytes, hash, fullPath));
            }
            if (HasErrors(diagnostics)) return null;
            string dataRoot = Path.Combine(
                packageRoot, "Data");
            if (!Directory.Exists(dataRoot))
            {
                diagnostics.Add(Error(
                    "npc-preview-package-data-root",
                    "The package manifest does not own a Data directory."));
                return null;
            }
            return new(
                new WorkspacePath(packageRoot),
                new WorkspacePath(dataRoot),
                files.ToImmutable());
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                ArgumentException or
                KeyNotFoundException or
                InvalidOperationException)
        {
            diagnostics.Add(Error(
                "npc-preview-package-manifest-read",
                exception.Message));
            return null;
        }
    }

    private static string InferPackageKind(string relativePath)
    {
        string normalized = relativePath.Replace(
            '\\', '/');
        if (normalized.EndsWith(
                ".esp",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(
                ".esm",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(
                ".esl",
                StringComparison.OrdinalIgnoreCase))
            return "plugin";
        if (normalized.Contains(
                "/FaceGeom/",
                StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith(
                ".nif",
                StringComparison.OrdinalIgnoreCase))
            return "facegeom";
        if (normalized.Contains(
                "/FaceTint/",
                StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith(
                ".dds",
                StringComparison.OrdinalIgnoreCase))
            return "facetint";
        return "support";
    }

    private async ValueTask<ImmutableArray<PluginAuthority>>
        ReadPluginAuthoritiesAsync(
            ReviewedGameIntake intake,
            PackageOverlay? overlay,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        var authorities =
            ImmutableArray.CreateBuilder<PluginAuthority>();
        var positions = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (PluginClosureReviewEntry entry in intake.Plugins
                     .Where(item =>
                         item.Enabled &&
                         item.Exists &&
                         item.ReadSucceeded &&
                         item.SourceHash is not null)
                     .OrderBy(item => item.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot, entry.Path));
            if (HasErrors(diagnostics))
                continue;
            if (!File.Exists(entry.Path.Value) ||
                Directory.Exists(entry.Path.Value) ||
                await HashFileAsync(
                    entry.Path, cancellationToken) !=
                entry.SourceHash!.Value)
            {
                diagnostics.Add(Error(
                    "npc-preview-plugin-stale",
                    $"Reviewed plugin '{entry.Plugin}' changed after intake."));
                continue;
            }
            if (positions.ContainsKey(entry.Plugin.Value))
            {
                diagnostics.Add(Error(
                    "npc-preview-plugin-duplicate",
                    $"Reviewed plugin '{entry.Plugin}' occurs more than once."));
                continue;
            }
            positions[entry.Plugin.Value] = authorities.Count;
            authorities.Add(new(
                entry.Plugin,
                entry.Path,
                entry.SourceHash.Value));
        }

        if (overlay is not null)
        {
            PackageFile[] plugins = overlay.Files
                .Where(item =>
                    string.Equals(
                        item.Kind, "plugin",
                        StringComparison.OrdinalIgnoreCase) ||
                    item.RelativePath.EndsWith(
                        ".esp", StringComparison.OrdinalIgnoreCase) ||
                    item.RelativePath.EndsWith(
                        ".esm", StringComparison.OrdinalIgnoreCase) ||
                    item.RelativePath.EndsWith(
                        ".esl", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (PackageFile file in plugins)
            {
                var name = new PluginName(
                    Path.GetFileName(file.FullPath.Value));
                var authority = new PluginAuthority(
                    name, file.FullPath, file.Sha256);
                if (positions.TryGetValue(
                        name.Value, out int index))
                    authorities[index] = authority;
                else
                {
                    positions[name.Value] = authorities.Count;
                    authorities.Add(authority);
                }
            }
        }
        return authorities.ToImmutable();
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream, cancellationToken)));
    }
}
