using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Enumerates explicit OTFT records and winning override provenance without live-profile inference.</summary>
public sealed class OutfitChoiceService(
    IPluginReader pluginReader,
    IWorkspacePolicy? workspacePolicy = null,
    WorkspacePath? policyRoot = null) : IOutfitChoiceService
{
    public async ValueTask<OutfitChoiceSearchResult> SearchAsync(
        OutfitChoiceSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (workspacePolicy is not null && policyRoot is { } root)
        {
            diagnostics.AddRange(workspacePolicy.Evaluate(root, request.DataRoot));
            if (HasErrors(diagnostics)) return Empty(request, diagnostics);
        }
        if (!request.PluginOrder.IsDefaultOrEmpty &&
            request.PluginOrder.Select(plugin => plugin.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(new Diagnostic("outfit-choice-duplicate-plugin", DiagnosticSeverity.Error,
                "Explicit plugin order contains a duplicate plugin name."));
            return Empty(request, diagnostics);
        }

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
                diagnostics.Add(new Diagnostic("outfit-choice-plugin-read-failed", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(pluginPath)}' could not be read: {exception.Message}"));
            }
        }

        var records = inspections.SelectMany(inspection =>
                inspection.Records
                    .Where(record => string.Equals(record.Signature, "OTFT", StringComparison.Ordinal))
                    .Select(record => (
                        record,
                        inspection,
                        owner: record.OwnerPlugin ?? inspection.Plugin)))
            .ToArray();
        foreach (var inspection in inspections) diagnostics.AddRange(inspection.Diagnostics);
        if (HasErrors(diagnostics)) return Empty(request, diagnostics);

        var inferredOrder = request.PluginOrder.IsDefaultOrEmpty;
        var chains = records.GroupBy(item =>
                (Owner: item.owner.Value.ToUpperInvariant(), item.record.FormId))
            .Select(group => group.ToImmutableArray())
            .ToImmutableArray();
        var candidates = chains
            .Where(chain => Matches(request.Search, chain[^1].record))
            .Select(chain => ToCandidate(chain[^1], chain, inferredOrder))
            .OrderBy(item => item.Name ?? item.EditorId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FormId.Value)
            .ThenBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new OutfitChoiceSearchResult(request.Edition, candidates, diagnostics.ToImmutable());
    }

    private static OutfitChoiceCandidate ToCandidate(
        (PluginRecordSummary record, PluginInspection inspection, PluginName owner) winning,
        ImmutableArray<(PluginRecordSummary record, PluginInspection inspection, PluginName owner)> chain,
        bool inferredOrder)
    {
        var overrideChain = chain.Select(item => item.inspection.Plugin).ToImmutableArray();
        var isOverride = overrideChain.Length > 1 || !string.Equals(
            winning.inspection.Plugin.Value,
            winning.owner.Value,
            StringComparison.OrdinalIgnoreCase);
        var kind = isOverride
            ? inferredOrder ? OutfitChoiceProvenanceKind.InferredOrder : OutfitChoiceProvenanceKind.Override
            : inferredOrder ? OutfitChoiceProvenanceKind.Unknown : OutfitChoiceProvenanceKind.Base;
        return new OutfitChoiceCandidate(winning.inspection.Plugin, winning.record.FormId,
            winning.record.EditorId, winning.record.Name, winning.record.OutfitItems,
            winning.record.IsDeleted,
            new OutfitChoiceProvenance(kind, winning.owner, overrideChain),
            winning.record.OutfitItemReferences.IsDefault
                ? []
                : winning.record.OutfitItemReferences);
    }

    private static bool Matches(string? search, PluginRecordSummary record)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var value = search.Trim();
        return (record.EditorId?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (record.Name?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
               record.FormId.ToString().Contains(value, StringComparison.OrdinalIgnoreCase) ||
               record.OutfitItems.Any(item => item.ToString().Contains(value, StringComparison.OrdinalIgnoreCase)) ||
               (!record.OutfitItemReferences.IsDefault && record.OutfitItemReferences.Any(item =>
                   item.ToString().Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static ImmutableArray<string> ResolvePluginPaths(OutfitChoiceSearchRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("outfit-choice-data-root-missing", DiagnosticSeverity.Error,
                "The explicit Data root does not exist."));
            return [];
        }
        if (!request.PluginOrder.IsDefaultOrEmpty)
        {
            var explicitPaths = ImmutableArray.CreateBuilder<string>();
            foreach (var plugin in request.PluginOrder)
            {
                var path = Path.Combine(request.DataRoot.Value, plugin.Value);
                if (!File.Exists(path))
                    diagnostics.Add(new Diagnostic("outfit-choice-plugin-missing", DiagnosticSeverity.Error,
                        $"Explicit plugin '{plugin.Value}' does not exist under the Data root."));
                else if (IsReparsePoint(path, diagnostics, plugin.Value))
                    diagnostics.Add(new Diagnostic("outfit-choice-plugin-reparse-refused", DiagnosticSeverity.Error,
                        $"Explicit plugin '{plugin.Value}' is a reparse point."));
                else explicitPaths.Add(Path.GetFullPath(path));
            }
            return explicitPaths.ToImmutable();
        }
        return Directory.EnumerateFiles(request.DataRoot.Value)
            .Where(path => Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsReparsePoint(path, diagnostics, Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static bool IsReparsePoint(string path, ImmutableArray<Diagnostic>.Builder diagnostics, string displayName)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("outfit-choice-plugin-attributes-failed", DiagnosticSeverity.Error,
                $"Plugin '{displayName}' attributes could not be inspected: {exception.Message}"));
            return true;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static OutfitChoiceSearchResult Empty(OutfitChoiceSearchRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(request.Edition, [], diagnostics.ToImmutable());
}
