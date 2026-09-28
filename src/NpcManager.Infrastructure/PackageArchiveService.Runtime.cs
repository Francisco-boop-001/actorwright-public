using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class PackageArchiveService
{
    private const long MaximumRuntimeEntryBytes = 512L * 1024 * 1024;
    private const long MaximumRuntimePayloadBytes = 8L * 1024 * 1024 * 1024;

    private static bool IsRuntimePreset(string path) =>
        path.StartsWith("SKSE/Plugins/CharGen/Presets/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".jslot", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("F4SE/Plugins/F4EE/Presets/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool TryRuntimeKind(string path, AssetPath? selectedPreset, out string kind)
    {
        kind = string.Empty;
        if (selectedPreset is { } preset && string.Equals(path, preset.Value, StringComparison.OrdinalIgnoreCase) && IsRuntimePreset(path))
        {
            kind = "runtime-preset";
            return true;
        }
        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Equals("NPCManager", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Evidence", StringComparison.OrdinalIgnoreCase) || segment.Equals("Presets", StringComparison.OrdinalIgnoreCase)) ||
            segments[^1].EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)) return false;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (segments.Length == 1)
        {
            kind = extension is ".esp" or ".esm" or ".esl" ? "plugin" :
                extension is ".bsa" or ".ba2" ? "asset-archive" : string.Empty;
            return kind.Length != 0;
        }
        kind = segments[0].ToLowerInvariant() switch
        {
            "meshes" when extension is ".nif" or ".tri" or ".hkx" or ".xml" => "mesh-runtime",
            "textures" when extension == ".dds" => "texture",
            "seq" when segments.Length == 2 && extension == ".seq" => "seq",
            "scripts" when extension == ".pex" => "runtime-script",
            "materials" when extension is ".bgsm" or ".bgem" => "material",
            "sound" when extension is ".wav" or ".xwm" or ".fuz" => "sound",
            "strings" when extension is ".strings" or ".dlstrings" or ".ilstrings" => "strings",
            "skse" or "f4se" when segments.Length > 2 && segments[1].Equals("Plugins", StringComparison.OrdinalIgnoreCase) &&
                extension is ".dll" or ".ini" or ".json" or ".toml" or ".xml" => "runtime-plugin",
            _ => string.Empty
        };
        return kind.Length != 0;
    }

    public async ValueTask<RuntimeArchiveVerificationResult> VerifyRuntimeAsync(
        RuntimeArchiveVerificationRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        RuntimeArchiveVerificationResult Refuse() => new(false, null, diagnostics.ToImmutable());
        void Error(string code, string message) => diagnostics.Add(new(code, DiagnosticSeverity.Error, message));
        if (!request.Archive.IsUnder(labRoot) || !File.Exists(request.Archive.Value) ||
            !Path.GetExtension(request.Archive.Value).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            Error("package-runtime-archive-path", "Runtime archive must be an existing .zip file under the admitted workspace.");
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Archive));
        if (request.IncludeRuntimePreset is { } selected && !IsRuntimePreset(selected.Value))
            Error("package-runtime-preset-path", $"Explicit runtime preset '{selected}' must name a canonical supported SKSE/F4SE preset path.");
        if (HasErrors(diagnostics)) return Refuse();
        try
        {
            await using var input = new FileStream(request.Archive.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 or > MaximumEntryCount)
            {
                Error("package-runtime-entry-count", $"Archive '{request.Archive}' has {archive.Entries.Count} entries; admitted count is 1–{MaximumEntryCount}.");
                return Refuse();
            }
            var entries = ImmutableArray.CreateBuilder<PackageArchiveEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetPath path;
                try { path = new AssetPath(entry.FullName); }
                catch (ArgumentException)
                {
                    Error("package-runtime-entry-path", $"Archive entry '{entry.FullName}' is not a traversal-safe file path.");
                    continue;
                }
                if (path.Value != entry.FullName || !TryRuntimeKind(path.Value, request.IncludeRuntimePreset, out string kind) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    Error("package-runtime-entry-layout", $"Archive entry '{entry.FullName}' is not an admitted runtime file at Data-root layout.");
                    continue;
                }
                if (!seen.Add(path.Value))
                {
                    Error("package-runtime-entry-duplicate", $"Archive entry '{entry.FullName}' duplicates another path, ignoring case.");
                    continue;
                }
                if (entry.Length is < 0 or > MaximumRuntimeEntryBytes || totalBytes > MaximumRuntimePayloadBytes - entry.Length)
                {
                    Error("package-runtime-entry-size", $"Archive entry '{entry.FullName}' has {entry.Length} bytes; per-file limit is {MaximumRuntimeEntryBytes}, total limit {MaximumRuntimePayloadBytes} (prior total {totalBytes}).");
                    return Refuse();
                }
                await using Stream content = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                long length = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length += read;
                    if (length > entry.Length)
                        throw new InvalidDataException($"Archive entry '{entry.FullName}' expanded beyond its declared {entry.Length} bytes.");
                    hash.AppendData(buffer, 0, read);
                }
                if (length != entry.Length)
                    throw new InvalidDataException($"Archive entry '{entry.FullName}' streamed {length} bytes, expected {entry.Length}.");
                totalBytes += length;
                entries.Add(new(kind, path, null, length, new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()))));
            }
            if (!entries.Any(entry => entry.Kind == "plugin"))
                Error("package-runtime-plugin-missing", "A runtime archive must contain at least one plugin at its root.");
            if (request.IncludeRuntimePreset is { } preset && !seen.Contains(preset.Value))
                Error("package-runtime-preset-missing", $"Explicit runtime preset '{preset}' is absent from the archive.");
            if (HasErrors(diagnostics)) return Refuse();
            Sha256Hash archiveHash = await HashFileAsync(request.Archive.Value, cancellationToken);
            return new(true, new("1", "npcmanager-runtime-archive-layout", request.Archive, archiveHash,
                entries.OrderBy(entry => entry.ArchivePath.Value, StringComparer.Ordinal).ToImmutableArray(),
                ForwardSlashEntries: true, NoWrapperDirectory: true, IndependentlyReopened: true,
                SourceManifestBound: false, InstallDependencyAuthority: false, RuntimeProof: false), diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Error("package-runtime-archive-read", $"Archive '{request.Archive}' could not be independently read: {exception.Message}");
            return Refuse();
        }
    }
}
