using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record PackageManifestReadResult(
    PackageManifestIdentity? Identity,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed class PackageManifestReader(IWorkspacePolicy policy, WorkspacePath labRoot)
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    public async ValueTask<PackageManifestReadResult> ReadAsync(
        WorkspacePath manifestPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePath(manifestPath, diagnostics);
        if (HasErrors(diagnostics)) return Failed(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(manifestPath.Value);
            if (info.Length <= 0 || info.Length > MaximumManifestBytes)
            {
                diagnostics.Add(new Diagnostic("package-manifest-size", DiagnosticSeverity.Error,
                    "The package manifest must be non-empty and no larger than 4 MiB."));
                return Failed(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(manifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("package-manifest-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Failed(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("package-manifest-read-denied", DiagnosticSeverity.Error, exception.Message));
            return Failed(diagnostics);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("package-manifest-root", DiagnosticSeverity.Error,
                    "The package manifest root must be a JSON object."));
                return Failed(diagnostics);
            }
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            if (HasErrors(diagnostics)) return Failed(diagnostics);
            var root = document.RootElement;
            var schemaVersion = ReadInt(root, "schemaVersion", diagnostics);
            if (schemaVersion != 1)
                diagnostics.Add(new Diagnostic("package-manifest-schema-unsupported", DiagnosticSeverity.Error,
                    "Only package manifest schemaVersion 1 is supported."));
            var edition = ReadRequiredString(root, "edition", diagnostics);
            var presetFormat = ReadRequiredString(root, "presetFormat", diagnostics);
            var sourcePreset = ReadRequiredString(root, "sourcePreset", diagnostics);
            var sourcePlugin = ReadRequiredString(root, "sourcePlugin", diagnostics);
            var outputPlugin = ReadRequiredString(root, "outputPlugin", diagnostics);
            var targetFormIdText = ReadRequiredString(root, "targetFormId", diagnostics);
            var sourcePresetHash = ReadHash(root, "sourcePresetSha256", diagnostics);
            var sourcePluginHash = ReadHash(root, "sourcePluginSha256", diagnostics);
            var targetFormId = FormId.TryParse(targetFormIdText, out var parsedFormId) ? parsedFormId : default;
            if (!FormId.TryParse(targetFormIdText, out _))
                diagnostics.Add(new Diagnostic("package-manifest-form-id", DiagnosticSeverity.Error,
                    "The package targetFormId must be a hexadecimal FormID."));

            var files = ImmutableArray.CreateBuilder<PackageManifestFile>();
            if (!root.TryGetProperty("artifacts", out var artifactElement) ||
                artifactElement.ValueKind != JsonValueKind.Array || artifactElement.GetArrayLength() == 0)
            {
                diagnostics.Add(new Diagnostic("package-manifest-artifacts", DiagnosticSeverity.Error,
                    "The package manifest must contain a non-empty artifacts array."));
            }
            else
            {
                if (artifactElement.GetArrayLength() > 10_000)
                    diagnostics.Add(new Diagnostic("package-manifest-artifact-count", DiagnosticSeverity.Error,
                        "The package manifest may declare at most 10,000 artifacts."));
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;
                foreach (var item in artifactElement.EnumerateArray())
                {
                    var path = $"$.artifacts[{index}]";
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(new Diagnostic("package-manifest-artifact-shape", DiagnosticSeverity.Error,
                            $"{path} must be an object."));
                        index++;
                        continue;
                    }
                    var kind = ReadRequiredString(item, "kind", diagnostics, path);
                    var relative = ReadRequiredString(item, "relativePath", diagnostics, path);
                    var byteLength = ReadLong(item, "byteLength", diagnostics, path);
                    var hash = ReadHash(item, "sha256", diagnostics, path);
                    try
                    {
                        var assetPath = new AssetPath(relative);
                        if (!paths.Add(assetPath.Value))
                            diagnostics.Add(new Diagnostic("package-manifest-duplicate-path", DiagnosticSeverity.Error,
                                $"The package contains duplicate artifact path '{assetPath.Value}'."));
                        if (byteLength <= 0)
                            diagnostics.Add(new Diagnostic("package-manifest-artifact-size", DiagnosticSeverity.Error,
                                $"Artifact '{assetPath.Value}' must have a positive byteLength."));
                        files.Add(new PackageManifestFile(kind, assetPath, byteLength, hash));
                    }
                    catch (ArgumentException)
                    {
                        diagnostics.Add(new Diagnostic("package-manifest-artifact-path", DiagnosticSeverity.Error,
                            $"Artifact path '{relative}' is not a safe relative asset path."));
                    }
                    index++;
                }
            }

            if (HasErrors(diagnostics)) return Failed(diagnostics);
            var identity = new PackageManifestIdentity(schemaVersion, edition, presetFormat, sourcePreset,
                sourcePresetHash, sourcePlugin, sourcePluginHash, outputPlugin, targetFormId, manifestPath,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), files.ToImmutable());
            return new PackageManifestReadResult(identity, diagnostics.ToImmutable());
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("package-manifest-json", DiagnosticSeverity.Error, exception.Message));
        }
        catch (FormatException exception)
        {
            diagnostics.Add(new Diagnostic("package-manifest-value", DiagnosticSeverity.Error, exception.Message));
        }
        return Failed(diagnostics);
    }

    private void ValidatePath(WorkspacePath path, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("package-manifest-outside-lab", DiagnosticSeverity.Error,
                "The package manifest must remain under the K-only lab root."));
        if (!File.Exists(path.Value))
            diagnostics.Add(new Diagnostic("package-manifest-missing", DiagnosticSeverity.Error,
                "The package manifest does not exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, path));
            if (File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint) ||
                ContainsReparseBetween(labRoot.Value, path.Value))
                diagnostics.Add(new Diagnostic("package-manifest-reparse", DiagnosticSeverity.Error,
                    "The package manifest path contains a reparse point."));
        }
    }

    private static int ReadInt(JsonElement root, string name, ImmutableArray<Diagnostic>.Builder diagnostics,
        string path = "$")
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            diagnostics.Add(new Diagnostic("package-manifest-field", DiagnosticSeverity.Error,
                $"{path}.{name} must be a 32-bit integer."));
            return 0;
        }
        return result;
    }

    private static long ReadLong(JsonElement root, string name, ImmutableArray<Diagnostic>.Builder diagnostics,
        string path)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result))
        {
            diagnostics.Add(new Diagnostic("package-manifest-field", DiagnosticSeverity.Error,
                $"{path}.{name} must be a 64-bit integer."));
            return 0;
        }
        return result;
    }

    private static string ReadRequiredString(JsonElement root, string name,
        ImmutableArray<Diagnostic>.Builder diagnostics, string path = "$")
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            diagnostics.Add(new Diagnostic("package-manifest-field", DiagnosticSeverity.Error,
                $"{path}.{name} must be a non-empty string."));
            return string.Empty;
        }
        return value.GetString()!;
    }

    private static Sha256Hash ReadHash(JsonElement root, string name,
        ImmutableArray<Diagnostic>.Builder diagnostics, string path = "$")
    {
        var value = ReadRequiredString(root, name, diagnostics, path);
        try { return new Sha256Hash(value); }
        catch (ArgumentException)
        {
            diagnostics.Add(new Diagnostic("package-manifest-hash", DiagnosticSeverity.Error,
                $"{path}.{name} must be a 64-character hexadecimal SHA-256 hash."));
            return new Sha256Hash(new string('0', 64));
        }
    }

    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    diagnostics.Add(new Diagnostic("package-manifest-duplicate-key", DiagnosticSeverity.Error,
                        $"Duplicate JSON property '{path}.{property.Name}' is not accepted."));
                ValidateDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray())
                ValidateDuplicateProperties(child, $"{path}[{index++}]", diagnostics);
        }
    }

    private static bool ContainsReparseBetween(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        while (current.Length >= rootFull.Length)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
        return true;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PackageManifestReadResult Failed(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, diagnostics.ToImmutable());
}
