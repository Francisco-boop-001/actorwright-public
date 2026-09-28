using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed record AssetIndexArtifactSnapshot(
    GameEdition? Edition,
    WorkspacePath? DataRoot,
    ImmutableArray<string> ArchiveSources,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Reads only the persisted asset-index fields needed by consistency preflight.</summary>
internal sealed class AssetIndexArtifactReader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    public static AssetIndexArtifactSnapshot Read(WorkspacePath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            if (File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-reparse", DiagnosticSeverity.Error,
                    "The asset-index artifact may not be a reparse point."));
                return Invalid(diagnostics);
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path.Value), JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-invalid", DiagnosticSeverity.Error,
                    "The asset-index artifact root must be an object."));
                return Invalid(diagnostics);
            }

            if (!TryGetString(root, "schemaVersion", out var schemaVersion) || schemaVersion != "1")
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-schema", DiagnosticSeverity.Error,
                    "The asset-index artifact schema version is unsupported."));
            }

            GameEdition? edition = null;
            if (!TryGetString(root, "edition", out var editionValue) ||
                !GameEditionExtensions.TryParseWireName(editionValue, out var parsedEdition))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-edition", DiagnosticSeverity.Error,
                    "The asset-index artifact edition is missing or unsupported."));
            }
            else
            {
                edition = parsedEdition;
            }

            WorkspacePath? dataRoot = null;
            if (!TryGetString(root, "dataRoot", out var dataRootValue))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-data-root", DiagnosticSeverity.Error,
                    "The asset-index artifact data root is missing."));
            }
            else
            {
                try
                {
                    dataRoot = new WorkspacePath(dataRootValue);
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(new Diagnostic("archive-consistency-index-data-root", DiagnosticSeverity.Error,
                        $"The asset-index artifact data root is invalid: {exception.Message}"));
                }
            }

            ReadReportedDiagnostics(root, diagnostics);
            var archives = ReadArchiveSources(root, diagnostics, cancellationToken);
            return new AssetIndexArtifactSnapshot(edition, dataRoot, archives, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic("archive-consistency-index-invalid", DiagnosticSeverity.Error,
                $"The asset-index artifact could not be read: {exception.Message}"));
            return Invalid(diagnostics);
        }
    }

    private static ImmutableArray<string> ReadArchiveSources(
        JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("archive-consistency-index-entries", DiagnosticSeverity.Error,
                "The asset-index artifact entries field must be an array."));
            return [];
        }

        var sources = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Object || !TryGetString(entry, "path", out var entryPath) ||
                !IsSafeAssetPath(entryPath) || !entry.TryGetProperty("winner", out var winner) ||
                !entry.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-entry", DiagnosticSeverity.Error,
                    "Every asset-index entry must contain a safe path, winner, and provider array."));
                continue;
            }
            if (!seenPaths.Add(entryPath))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-duplicate", DiagnosticSeverity.Error,
                    $"Asset-index entry '{entryPath}' appears more than once."));
            }

            if (winner.ValueKind != JsonValueKind.Object || !ValidateProvider(winner, entryPath, diagnostics))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-provider", DiagnosticSeverity.Error,
                    $"The winner for asset-index entry '{entryPath}' is malformed."));
            }

            var providerCount = 0;
            foreach (var provider in providers.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                providerCount++;
                if (!ValidateProvider(provider, entryPath, diagnostics) ||
                    !TryGetString(provider, "kind", out var kind) ||
                    !TryGetString(provider, "source", out var source))
                {
                    diagnostics.Add(new Diagnostic("archive-consistency-index-provider", DiagnosticSeverity.Error,
                        $"A provider for asset-index entry '{entryPath}' is malformed."));
                    continue;
                }

                if (!string.Equals(kind, "archive", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (source.Any(char.IsControl) || source.Contains('/') || source.Contains('\\') ||
                    string.Equals(source, ".", StringComparison.Ordinal) || string.Equals(source, "..", StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic("archive-consistency-index-provider", DiagnosticSeverity.Error,
                        $"Archive provider source '{source}' is not a safe filename."));
                    continue;
                }
                if (!seen.Add(source))
                {
                    diagnostics.Add(new Diagnostic("archive-consistency-index-duplicate", DiagnosticSeverity.Error,
                        $"Archive provider '{source}' appears more than once in the asset index."));
                    continue;
                }
                sources.Add(source);
            }
            if (providerCount == 0)
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-provider", DiagnosticSeverity.Error,
                    $"Asset-index entry '{entryPath}' has no providers."));
            }
            else if (winner.ValueKind == JsonValueKind.Object &&
                     !ProviderEquals(winner, providers[0]))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-winner", DiagnosticSeverity.Error,
                    $"The winner for asset-index entry '{entryPath}' is not the first provider."));
            }
        }

        return sources.ToImmutable();
    }

    private static void ReadReportedDiagnostics(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("diagnostics", out var reported) || reported.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("archive-consistency-index-diagnostics", DiagnosticSeverity.Error,
                "The asset-index artifact diagnostics field must be an array."));
            return;
        }

        foreach (var item in reported.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "severity", out var severity))
                continue;
            if (string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-index-reported-error", DiagnosticSeverity.Error,
                    "The asset-index artifact contains an unresolved error diagnostic."));
                return;
            }
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool ValidateProvider(JsonElement provider, string entryPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (provider.ValueKind != JsonValueKind.Object || !TryGetString(provider, "path", out var path) ||
            !string.Equals(path, entryPath, StringComparison.Ordinal) || !IsSafeAssetPath(path) ||
            !TryGetString(provider, "kind", out var kind) ||
            !string.Equals(kind, "loose", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kind, "archive", StringComparison.OrdinalIgnoreCase) ||
            !TryGetString(provider, "source", out var source) || string.IsNullOrWhiteSpace(source) ||
            source.Any(char.IsControl) || source.Contains('/') || source.Contains('\\') ||
            !provider.TryGetProperty("size", out var size) || !size.TryGetInt64(out var sizeValue) || sizeValue < 0 ||
            !TryGetString(provider, "sha256", out var hash) || !IsSha256(hash))
            return false;

        return true;
    }

    private static bool ProviderEquals(JsonElement left, JsonElement right) =>
        TryGetString(left, "path", out var leftPath) && TryGetString(right, "path", out var rightPath) &&
        TryGetString(left, "kind", out var leftKind) && TryGetString(right, "kind", out var rightKind) &&
        TryGetString(left, "source", out var leftSource) && TryGetString(right, "source", out var rightSource) &&
        left.TryGetProperty("size", out var leftSize) && right.TryGetProperty("size", out var rightSize) &&
        leftSize.TryGetInt64(out var leftSizeValue) && rightSize.TryGetInt64(out var rightSizeValue) &&
        TryGetString(left, "sha256", out var leftHash) && TryGetString(right, "sha256", out var rightHash) &&
        string.Equals(leftPath, rightPath, StringComparison.Ordinal) &&
        string.Equals(leftKind, rightKind, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(leftSource, rightSource, StringComparison.Ordinal) &&
        leftSizeValue == rightSizeValue && string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] is '/' or '\\' || path.Contains('\\') || path.Contains(':'))
            return false;
        var parts = path.Split('/');
        return parts.All(part => part.Length > 0 && part != "." && part != "..");
    }

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(character => int.TryParse(character.ToString(), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out _));

    private static AssetIndexArtifactSnapshot Invalid(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(null, null, [], diagnostics.ToImmutable());
}
