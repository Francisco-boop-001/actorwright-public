using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Enumerates normalized asset choices without opening a live profile or writing game files.</summary>
public sealed class AssetChoiceService(
    IPluginReader pluginReader,
    IAssetIndexer assetIndexer,
    IWorkspacePolicy? workspacePolicy = null,
    WorkspacePath? policyRoot = null) : IAssetChoiceService
{
    public async ValueTask<AssetChoiceSearchResult> SearchAsync(
        AssetChoiceSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (workspacePolicy is not null && policyRoot is { } root)
        {
            diagnostics.AddRange(workspacePolicy.Evaluate(root, request.DataRoot));
            if (HasErrors(diagnostics))
                return Empty(request, diagnostics);
        }

        return request.Kind switch
        {
            AssetChoiceKind.Mesh => await SearchMeshesAsync(request, diagnostics, cancellationToken),
            AssetChoiceKind.HeadPart => await SearchHeadPartsAsync(request, diagnostics, cancellationToken),
            _ => InvalidKind(request, diagnostics)
        };
    }

    private async ValueTask<AssetChoiceSearchResult> SearchMeshesAsync(
        AssetChoiceSearchRequest request, ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var index = await assetIndexer.IndexAsync(new AssetIndexRequest(request.Edition, request.DataRoot), cancellationToken);
        diagnostics.AddRange(index.Diagnostics);
        ValidateProviders(index.Providers, diagnostics);
        if (HasErrors(diagnostics))
            return Empty(request, diagnostics);

        var search = request.Search?.Trim();
        var candidates = index.Providers
            .Where(provider => provider.Path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .GroupBy(provider => provider.Path.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToMeshCandidate(group.Key, group, search))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new AssetChoiceSearchResult(request.Edition, request.Kind, candidates, diagnostics.ToImmutable());
    }

    private async ValueTask<AssetChoiceSearchResult> SearchHeadPartsAsync(
        AssetChoiceSearchRequest request, ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var index = await assetIndexer.IndexAsync(new AssetIndexRequest(request.Edition, request.DataRoot), cancellationToken);
        diagnostics.AddRange(index.Diagnostics);
        ValidateProviders(index.Providers, diagnostics);
        if (HasErrors(diagnostics))
            return Empty(request, diagnostics);

        var pluginPaths = ResolvePluginPaths(request, diagnostics);
        var inspections = ImmutableArray.CreateBuilder<PluginInspection>();
        foreach (var pluginPath in pluginPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                inspections.Add(await pluginReader.ReadAsync(
                    new PluginReadRequest(request.Edition, new WorkspacePath(pluginPath)), cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("asset-choice-plugin-read-failed", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(pluginPath)}' could not be read: {exception.Message}"));
            }
        }

        foreach (var inspection in inspections)
            diagnostics.AddRange(inspection.Diagnostics);
        if (HasErrors(diagnostics))
            return Empty(request, diagnostics);

        var records = new List<(PluginName Provider, PluginName Owner, PluginRecordSummary Record)>();
        foreach (var inspection in inspections)
        {
            foreach (var record in inspection.Records.Where(record =>
                         string.Equals(record.Signature, "HDPT", StringComparison.Ordinal)))
            {
                if (record.ModelPath is null)
                {
                    diagnostics.Add(new Diagnostic("asset-choice-model-missing", DiagnosticSeverity.Warning,
                        $"Headpart {record.FormId} in '{inspection.Plugin}' has no model path and was skipped."));
                    continue;
                }
                records.Add((inspection.Plugin, record.OwnerPlugin ?? inspection.Plugin, record));
            }
        }

        var inferredOrder = request.PluginOrder.IsDefaultOrEmpty;
        var chains = records.GroupBy(item =>
                (Owner: item.Owner.Value.ToUpperInvariant(), item.Record.FormId))
            .Select(group => group.ToImmutableArray())
            .ToImmutableArray();
        var providersByPath = index.Providers
            .GroupBy(provider => provider.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);
        var search = request.Search?.Trim();
        var candidates = new List<AssetChoiceCandidate>();
        foreach (var chain in chains)
        {
            var winning = chain[^1];
            var modelPath = winning.Record.ModelPath!.Value;
            if (!Matches(search, winning.Record, modelPath))
                continue;
            var evidence = providersByPath.TryGetValue(modelPath.Value, out var providers)
                ? providers.Select(ToProviderEvidence).ToImmutableArray()
                : ImmutableArray<AssetChoiceProviderEvidence>.Empty;
            if (evidence.IsDefaultOrEmpty)
            {
                diagnostics.Add(new Diagnostic("asset-choice-provider-missing", DiagnosticSeverity.Warning,
                    $"Headpart {winning.Record.FormId} references '{modelPath}', but no indexed provider was found."));
            }
            var chainPlugins = chain.Select(item => item.Provider).ToImmutableArray();
            var isOverride = chainPlugins.Length > 1 || !string.Equals(
                winning.Provider.Value,
                winning.Owner.Value,
                StringComparison.OrdinalIgnoreCase);
            var provenanceKind = isOverride
                ? inferredOrder ? FormChoiceProvenanceKind.InferredOrder : FormChoiceProvenanceKind.Override
                : inferredOrder ? FormChoiceProvenanceKind.Unknown : FormChoiceProvenanceKind.Base;
            candidates.Add(new AssetChoiceCandidate(AssetChoiceKind.HeadPart, modelPath,
                new AssetChoiceProvider(evidence.IsDefaultOrEmpty ? AssetChoiceProviderStatus.Missing : AssetChoiceProviderStatus.Resolved, evidence),
                winning.Provider, winning.Record.FormId, new RecordSignature("HDPT"), winning.Record.EditorId,
                winning.Record.Name, new AssetChoiceProvenance(provenanceKind, winning.Owner, chainPlugins)));
        }

        return new AssetChoiceSearchResult(request.Edition, request.Kind,
            candidates.OrderBy(candidate => candidate.EditorId ?? candidate.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.FormId!.Value.Value).ThenBy(candidate => candidate.Plugin!.Value.Value, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(), diagnostics.ToImmutable());
    }

    private static AssetChoiceCandidate? ToMeshCandidate(string path,
        IEnumerable<AssetProvider> providers, string? search)
    {
        var assetPath = new AssetPath(path);
        if (!string.IsNullOrEmpty(search) && !assetPath.Value.Contains(search, StringComparison.OrdinalIgnoreCase))
            return null;
        var evidence = providers.Select(ToProviderEvidence).ToImmutableArray();
        return new AssetChoiceCandidate(AssetChoiceKind.Mesh, assetPath,
            new AssetChoiceProvider(AssetChoiceProviderStatus.Resolved, evidence), null, null, null, null, null, null);
    }

    private static AssetChoiceProviderEvidence ToProviderEvidence(AssetProvider provider) =>
        new(provider.Kind, provider.Source, provider.Size, new Sha256Hash(provider.Sha256));

    private static void ValidateProviders(IEnumerable<AssetProvider> providers,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var provider in providers)
        {
            if (provider.Kind is not (AssetProviderKind.Loose or AssetProviderKind.Archive) ||
                provider.Size < 0 || string.IsNullOrWhiteSpace(provider.Source) ||
                provider.Source.Any(char.IsControl))
            {
                diagnostics.Add(new Diagnostic("asset-choice-provider-invalid", DiagnosticSeverity.Error,
                    $"Provider evidence for '{provider.Path}' is malformed."));
                continue;
            }
            try { _ = new Sha256Hash(provider.Sha256); }
            catch (ArgumentException)
            {
                diagnostics.Add(new Diagnostic("asset-choice-provider-invalid", DiagnosticSeverity.Error,
                    $"Provider evidence for '{provider.Path}' has an invalid SHA-256."));
            }
        }
    }

    private static bool Matches(string? search, PluginRecordSummary record, AssetPath modelPath)
    {
        if (string.IsNullOrEmpty(search)) return true;
        return (record.EditorId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (record.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               modelPath.Value.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               record.FormId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static ImmutableArray<string> ResolvePluginPaths(
        AssetChoiceSearchRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("asset-choice-data-root-missing", DiagnosticSeverity.Error,
                "The explicit Data root does not exist."));
            return [];
        }
        if (!request.PluginOrder.IsDefaultOrEmpty &&
            request.PluginOrder.Select(plugin => plugin.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(new Diagnostic("asset-choice-duplicate-plugin", DiagnosticSeverity.Error,
                "Explicit plugin order contains a duplicate plugin name."));
            return [];
        }
        if (!request.PluginOrder.IsDefaultOrEmpty)
        {
            var explicitPaths = ImmutableArray.CreateBuilder<string>();
            foreach (var plugin in request.PluginOrder)
            {
                var path = Path.Combine(request.DataRoot.Value, plugin.Value);
                if (!File.Exists(path))
                {
                    diagnostics.Add(new Diagnostic("asset-choice-plugin-missing", DiagnosticSeverity.Error,
                        $"Explicit plugin '{plugin}' does not exist under the Data root."));
                    continue;
                }
                explicitPaths.Add(Path.GetFullPath(path));
            }
            return explicitPaths.ToImmutable();
        }
        return Directory.EnumerateFiles(request.DataRoot.Value)
            .Where(path => Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static AssetChoiceSearchResult Empty(AssetChoiceSearchRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(request.Edition, request.Kind, [], diagnostics.ToImmutable());

    private static AssetChoiceSearchResult InvalidKind(AssetChoiceSearchRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.Add(new Diagnostic("asset-choice-kind-invalid", DiagnosticSeverity.Error,
            "The asset choice kind is unsupported."));
        return Empty(request, diagnostics);
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
