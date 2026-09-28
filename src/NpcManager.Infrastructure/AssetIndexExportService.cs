using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a deterministic, provider-aware asset index without extracting or mutating archives.</summary>
public sealed class AssetIndexExportService(
    IAssetIndexer assetIndexer,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IAssetIndexExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<AssetIndexExportResult> ExportAsync(
        AssetIndexExportRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        var outputParent = Path.GetDirectoryName(request.Output.Value);
        if (outputParent is null)
        {
            diagnostics.Add(new Diagnostic("asset-index-output-parent-invalid", DiagnosticSeverity.Error,
                "The asset-index output path has no parent directory."));
        }
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(outputParent)));
            if (!Directory.Exists(outputParent))
            {
                diagnostics.Add(new Diagnostic("asset-index-output-parent-missing", DiagnosticSeverity.Error,
                    "The asset-index output directory must already exist."));
            }
        }
        ValidateOutputPath(request.Output, diagnostics);
        if (File.Exists(request.Output.Value))
        {
            diagnostics.Add(new Diagnostic("asset-index-output-exists", DiagnosticSeverity.Error,
                "The asset-index output path already exists; exports never overwrite artifacts."));
        }
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("asset-index-data-root-missing", DiagnosticSeverity.Error,
                "The explicit copied Data root does not exist."));
        }
        if (HasErrors(diagnostics))
            return Refused(request, diagnostics);

        AssetIndex index;
        try
        {
            index = await assetIndexer.IndexAsync(new AssetIndexRequest(request.Edition, request.DataRoot), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("asset-index-failed", DiagnosticSeverity.Error,
                $"Asset indexing failed: {exception.Message}"));
            return Refused(request, diagnostics);
        }
        diagnostics.AddRange(index.Diagnostics);
        var artifact = BuildArtifact(request, index, diagnostics);
        if (HasErrors(diagnostics))
            return new AssetIndexExportResult(false, request.Output, artifact, diagnostics.ToImmutable());

        try
        {
            WriteArtifact(request.Output, artifact);
            return new AssetIndexExportResult(true, request.Output, artifact, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("asset-index-write-failed", DiagnosticSeverity.Error,
                $"The asset-index artifact could not be written: {exception.Message}"));
            return new AssetIndexExportResult(false, request.Output, artifact, diagnostics.ToImmutable());
        }
    }

    private static AssetIndexArtifact BuildArtifact(
        AssetIndexExportRequest request,
        AssetIndex index,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var provider in index.Providers)
            ValidateProvider(provider, diagnostics);

        var entries = index.Providers
            .GroupBy(provider => provider.Path.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(PrecedenceKind)
                    .ThenBy(provider => provider.Source, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(provider => provider.Source, StringComparer.Ordinal)
                    .ThenBy(provider => provider.Sha256, StringComparer.Ordinal)
                    .ToImmutableArray();
                return new AssetIndexEntry(ordered[0].Path, ordered[0], ordered);
            })
            .OrderBy(entry => entry.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return new AssetIndexArtifact("1", request.Edition, request.DataRoot, entries, diagnostics.ToImmutable());
    }

    private static int PrecedenceKind(AssetProvider provider) => provider.Kind switch
    {
        AssetProviderKind.Loose => 0,
        AssetProviderKind.Archive => 1,
        _ => 2
    };

    private static void ValidateProvider(AssetProvider provider, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(provider.Path.Value) || provider.Path.Value.Any(char.IsControl) ||
            provider.Kind is not (AssetProviderKind.Loose or AssetProviderKind.Archive) ||
            provider.Size < 0 || string.IsNullOrWhiteSpace(provider.Source) || provider.Source.Any(char.IsControl))
        {
            diagnostics.Add(new Diagnostic("asset-index-provider-invalid", DiagnosticSeverity.Error,
                $"Provider evidence for '{provider.Path}' is malformed."));
            return;
        }
        try { _ = new Sha256Hash(provider.Sha256); }
        catch (ArgumentException)
        {
            diagnostics.Add(new Diagnostic("asset-index-provider-invalid", DiagnosticSeverity.Error,
                $"Provider evidence for '{provider.Path}' has an invalid SHA-256."));
        }
    }

    private static void ValidateOutputPath(WorkspacePath output, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (output.Value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            output.Value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            output.Value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic("asset-index-output-unsafe-path", DiagnosticSeverity.Error,
                "The asset-index output path may not use a device or UNC path."));
        }

        if (output.Value.IndexOf(':', 2) >= 0)
        {
            diagnostics.Add(new Diagnostic("asset-index-output-ads-refused", DiagnosticSeverity.Error,
                "The asset-index output path may not contain an alternate-data-stream delimiter."));
        }
    }

    private static void WriteArtifact(WorkspacePath output, AssetIndexArtifact artifact)
    {
        var document = new AssetIndexDocument(artifact.SchemaVersion, artifact.Edition.ToWireName(),
            artifact.DataRoot.Value, artifact.Entries.Select(AssetIndexEntryDocument.From).ToImmutableArray(), artifact.Diagnostics);
        var json = JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;
        var temporary = output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, output.Value, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static AssetIndexExportResult Refused(
        AssetIndexExportRequest request, ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, request.Output, null, diagnostics.ToImmutable());

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private sealed record AssetIndexDocument(
        string SchemaVersion,
        string Edition,
        string DataRoot,
        ImmutableArray<AssetIndexEntryDocument> Entries,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record AssetIndexEntryDocument(
        string Path,
        AssetProviderDocument Winner,
        ImmutableArray<AssetProviderDocument> Providers)
    {
        public static AssetIndexEntryDocument From(AssetIndexEntry entry) => new(entry.Path.Value,
            AssetProviderDocument.From(entry.Winner), entry.Providers.Select(AssetProviderDocument.From).ToImmutableArray());
    }

    private sealed record AssetProviderDocument(
        string Path,
        string Kind,
        string Source,
        long Size,
        string Sha256)
    {
        public static AssetProviderDocument From(AssetProvider provider) => new(provider.Path.Value,
            provider.Kind.ToString().ToLowerInvariant(), provider.Source, provider.Size, provider.Sha256);
    }
}
