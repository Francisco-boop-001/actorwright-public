using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class GameInventoryService(
    IPluginReader pluginReader,
    IAssetIndexer assetIndexer,
    IWorkspacePolicy? workspacePolicy = null,
    WorkspacePath? policyRoot = null) : IGameInventoryService
{
    public async ValueTask<GameInventory> ReadAsync(GameInventoryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (workspacePolicy is not null && policyRoot is { } root)
        {
            diagnostics.AddRange(workspacePolicy.Evaluate(root, request.DataRoot));
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                return new GameInventory(request.Edition, [], [], [], null, diagnostics.ToImmutable());
        }
        var pluginPaths = ResolvePluginPaths(request, diagnostics);
        var inspections = ImmutableArray.CreateBuilder<PluginInspection>();
        foreach (var pluginPath in pluginPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                inspections.Add(await pluginReader.ReadAsync(new PluginReadRequest(request.Edition,
                    new WorkspacePath(pluginPath)), cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("plugin-read-failed", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(pluginPath)}' could not be read: {exception.Message}"));
            }
        }

        var knownPlugins = inspections.Select(item => item.Plugin).ToHashSet();
        foreach (var inspection in inspections)
        {
            diagnostics.AddRange(inspection.Diagnostics);
            foreach (var master in inspection.Masters.Where(master => !knownPlugins.Contains(master)))
            {
                diagnostics.Add(new Diagnostic("missing-master", DiagnosticSeverity.Error,
                    $"Plugin '{inspection.Plugin}' requires missing master '{master}'."));
            }
        }

        var npcs = BuildNpcSummaries(inspections, request.PluginOrder.IsDefaultOrEmpty)
            .Where(record => Matches(request, record))
            .OrderBy(record => record.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.FormId.Value)
            .ToImmutableArray();

        AssetIndex? assets = null;
        if (request.IncludeAssets)
        {
            assets = await assetIndexer.IndexAsync(new AssetIndexRequest(request.Edition, request.DataRoot), cancellationToken);
            diagnostics.AddRange(assets.Diagnostics);
        }

        return new GameInventory(request.Edition,
            inspections.Select(item => item.Plugin).ToImmutableArray(),
            inspections.SelectMany(item => item.Masters).Distinct().OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            npcs,
            assets,
            diagnostics.ToImmutable());
    }

    private static IEnumerable<NpcRecordSummary> BuildNpcSummaries(
        ImmutableArray<PluginInspection>.Builder inspections,
        bool inferredOrder)
    {
        var records = inspections.SelectMany(inspection => inspection.Records
                .Where(record => record.IsNpc)
                .Select(record => (
                    Provider: inspection.Plugin,
                    Owner: record.OwnerPlugin ?? inspection.Plugin,
                    Record: record)))
            .ToArray();
        var chains = records.GroupBy(item => (item.Owner, item.Record.FormId))
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.Provider).ToImmutableArray());

        foreach (var group in records.GroupBy(item =>
                     (item.Owner, item.Record.FormId)))
        {
            var (provider, owner, record) = group.Last();
            var chain = chains[(owner, record.FormId)];
            var isOverrideRecord = !string.Equals(
                owner.Value,
                provider.Value,
                StringComparison.OrdinalIgnoreCase);
            var isWinningOverride = isOverrideRecord ||
                chain.Length > 1 && string.Equals(
                    chain[^1].Value,
                    provider.Value,
                    StringComparison.OrdinalIgnoreCase);
            var provenanceKind = chain.Length > 1 || isOverrideRecord
                ? isWinningOverride
                    ? inferredOrder ? NpcProvenanceKind.InferredOrder : NpcProvenanceKind.Override
                    : NpcProvenanceKind.Base
                : inferredOrder ? NpcProvenanceKind.Unknown : NpcProvenanceKind.Base;
            var metadata = record.NpcMetadata;
            var categories = Classify(metadata);
            var changeState = record.IsDeleted
                ? NpcChangeState.Deleted
                : isWinningOverride ? NpcChangeState.Changed
                : inferredOrder ? NpcChangeState.Unknown
                : NpcChangeState.Unchanged;
            yield return new NpcRecordSummary(provider, record.FormId, record.EditorId,
                record.Name, record.Signature, record.IsDeleted, metadata,
                new NpcProvenance(provenanceKind, owner, chain), changeState,
                categories, owner);
        }
    }

    private static ImmutableArray<NpcCategory> Classify(NpcRecordMetadata? metadata)
    {
        if (metadata is null)
            return ImmutableArray<NpcCategory>.Empty;

        var categories = ImmutableArray.CreateBuilder<NpcCategory>();
        var inWorld = metadata.IsPlaced || metadata.IsInLeveledList;
        if (inWorld)
            categories.Add(metadata.HasTemplate ? NpcCategory.Generic : NpcCategory.Unique);
        if (metadata.IsTemplateSource)
            categories.Add(NpcCategory.Template);
        if (!inWorld && !metadata.IsTemplateSource && !metadata.IsCharGenFacePreset)
            categories.Add(NpcCategory.Unused);
        return categories.ToImmutable();
    }

    private static ImmutableArray<string> ResolvePluginPaths(GameInventoryRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("data-root-missing", DiagnosticSeverity.Error, "The explicit Data root does not exist."));
            return ImmutableArray<string>.Empty;
        }

        if (!request.PluginOrder.IsDefaultOrEmpty)
        {
            var explicitPaths = ImmutableArray.CreateBuilder<string>();
            foreach (var plugin in request.PluginOrder)
            {
                var path = Path.Combine(request.DataRoot.Value, plugin.Value);
                if (!File.Exists(path))
                {
                    diagnostics.Add(new Diagnostic("plugin-missing", DiagnosticSeverity.Error,
                        $"Explicit plugin '{plugin}' does not exist under the Data root."));
                    continue;
                }
                explicitPaths.Add(Path.GetFullPath(path));
            }
            return explicitPaths.ToImmutable();
        }

        return Directory.EnumerateFiles(request.DataRoot.Value)
            .Where(path => IsPlugin(path))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static bool Matches(GameInventoryRequest request, NpcRecordSummary record)
    {
        if (request.FormId is { } formId && record.FormId != formId) return false;
        if (request.Categories is { Count: > 0 } categories && !record.Categories.Any(categories.Contains)) return false;
        if (request.ChangedOnly && record.ChangeState != NpcChangeState.Changed) return false;
        if (string.IsNullOrWhiteSpace(request.Search)) return true;
        return (record.EditorId?.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (record.Name?.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool IsPlugin(string path) =>
        Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);
}
