using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Enumerates typed, winning FormID candidates without opening a live profile or writing records.</summary>
public sealed class FormChoiceService(
    IPluginReader pluginReader,
    IWorkspacePolicy? workspacePolicy = null,
    WorkspacePath? policyRoot = null) : IFormChoiceService
{
    public async ValueTask<FormChoiceSearchResult> SearchAsync(
        FormChoiceSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.AllowedSignatures.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("form-choice-signatures-required", DiagnosticSeverity.Error,
                "At least one allowed record signature is required."));
            return new FormChoiceSearchResult(request.Edition, request.AllowNull, [], diagnostics.ToImmutable());
        }

        if (workspacePolicy is not null && policyRoot is { } root)
        {
            diagnostics.AddRange(workspacePolicy.Evaluate(root, request.DataRoot));
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                return new FormChoiceSearchResult(request.Edition, request.AllowNull, [], diagnostics.ToImmutable());
        }

        var allowed = request.AllowedSignatures.ToImmutableHashSet();
        if (!request.PluginOrder.IsDefaultOrEmpty &&
            request.PluginOrder.Select(plugin => plugin.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(new Diagnostic("form-choice-duplicate-plugin", DiagnosticSeverity.Error,
                "Explicit plugin order contains a duplicate plugin name."));
            return new FormChoiceSearchResult(request.Edition, request.AllowNull, [], diagnostics.ToImmutable());
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
                diagnostics.Add(new Diagnostic("form-choice-plugin-read-failed", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(pluginPath)}' could not be read: {exception.Message}"));
            }
        }

        var records = new List<(PluginName Provider, PluginName Owner, PluginRecordSummary Record, RecordSignature Signature)>();
        foreach (var inspection in inspections)
        {
            diagnostics.AddRange(inspection.Diagnostics);
            foreach (var record in inspection.Records)
            {
                try
                {
                    records.Add((inspection.Plugin, record.OwnerPlugin ?? inspection.Plugin,
                        record, new RecordSignature(record.Signature)));
                }
                catch (ArgumentException)
                {
                    diagnostics.Add(new Diagnostic("form-choice-signature-invalid", DiagnosticSeverity.Warning,
                        $"Record {record.FormId} in '{inspection.Plugin}' has an invalid signature and was skipped."));
                }
            }
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new FormChoiceSearchResult(request.Edition, request.AllowNull, [], diagnostics.ToImmutable());

        var search = request.Search?.Trim();
        var inferredOrder = request.PluginOrder.IsDefaultOrEmpty;
        var chains = records
            .GroupBy(item => (Owner: item.Owner.Value.ToUpperInvariant(), item.Record.FormId))
            .Select(group => group.ToImmutableArray())
            .ToImmutableArray();
        var candidates = chains
            .Where(chain => allowed.Contains(chain[^1].Signature))
            .Where(chain => request.FormId is not { } formId || chain[^1].Record.FormId == formId)
            .Where(chain => Matches(search, chain[^1].Record))
            .Select(chain => ToCandidate(chain[^1], chain, inferredOrder))
            .OrderBy(item => item.Signature.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Name ?? item.EditorId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FormId.Value)
            .ThenBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new FormChoiceSearchResult(request.Edition, request.AllowNull, candidates, diagnostics.ToImmutable());
    }

    private static FormChoiceCandidate ToCandidate(
        (PluginName Provider, PluginName Owner, PluginRecordSummary Record, RecordSignature Signature) item,
        ImmutableArray<(PluginName Provider, PluginName Owner, PluginRecordSummary Record, RecordSignature Signature)> chain,
        bool inferredOrder)
    {
        var overrideChain = chain.Select(candidate => candidate.Provider)
            .ToImmutableArray();
        var isOverride = overrideChain.Length > 1 || !string.Equals(
            item.Provider.Value, item.Owner.Value, StringComparison.OrdinalIgnoreCase);
        var kind = isOverride
            ? inferredOrder ? FormChoiceProvenanceKind.InferredOrder : FormChoiceProvenanceKind.Override
            : inferredOrder ? FormChoiceProvenanceKind.Unknown : FormChoiceProvenanceKind.Base;
        return new FormChoiceCandidate(item.Provider, item.Record.FormId, item.Signature,
            item.Record.EditorId, item.Record.Name, item.Record.IsDeleted,
            new FormChoiceProvenance(kind, item.Owner, overrideChain));
    }

    private static bool Matches(string? search, PluginRecordSummary record)
    {
        if (string.IsNullOrEmpty(search)) return true;
        return (record.EditorId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (record.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               record.FormId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static ImmutableArray<string> ResolvePluginPaths(
        FormChoiceSearchRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("form-choice-data-root-missing", DiagnosticSeverity.Error,
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
                {
                    diagnostics.Add(new Diagnostic("form-choice-plugin-missing", DiagnosticSeverity.Error,
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
}
