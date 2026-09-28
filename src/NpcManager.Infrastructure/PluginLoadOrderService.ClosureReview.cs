using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class PluginLoadOrderService
{
    public async ValueTask<PluginClosureReviewResult> ReviewClosureAsync(
        PluginClosureReviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadOrder = await ResolveAsync(new PluginLoadOrderRequest(
            request.Edition, request.PluginsRoot, request.LoadOrderPath), cancellationToken);
        var diagnostics = loadOrder.Diagnostics.ToBuilder();
        if (loadOrder.Entries.IsDefaultOrEmpty || HasErrors(diagnostics))
            return EmptyClosure(request.Edition, loadOrder, diagnostics.ToImmutable());

        var entryByName = loadOrder.Entries.ToDictionary(
            item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase);
        var requested = request.SelectedPlugins.IsDefault
            ? loadOrder.Entries.Where(item => item.Enabled).Select(item => item.Plugin).ToImmutableArray()
            : request.SelectedPlugins;
        var requestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in requested)
        {
            if (!requestedNames.Add(plugin.Value))
                diagnostics.Add(new Diagnostic("plugin-selection-duplicate", DiagnosticSeverity.Error,
                    $"Plugin '{plugin.Value}' is selected more than once."));
            if (!entryByName.ContainsKey(plugin.Value))
                diagnostics.Add(new Diagnostic("plugin-selection-unlisted", DiagnosticSeverity.Error,
                    $"Selected plugin '{plugin.Value}' is not listed in the load-order manifest."));
        }
        if (requestedNames.Count == 0)
            diagnostics.Add(new Diagnostic("plugin-selection-empty", DiagnosticSeverity.Error,
                "Select at least one plugin before reviewing the copied workspace."));
        if (HasErrors(diagnostics))
            return EmptyClosure(request.Edition, loadOrder, diagnostics.ToImmutable());

        var pending = new Queue<string>(requestedNames.OrderBy(
            name => entryByName[name].Order).ThenBy(name => name, StringComparer.OrdinalIgnoreCase));
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspections = new Dictionary<string, PluginInspection>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, Sha256Hash>(StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<string, ImmutableArray<PluginName>>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = pending.Dequeue();
            if (!closure.Add(name)) continue;
            if (!entryByName.TryGetValue(name, out var entry)) continue;
            if (!entry.Exists)
            {
                diagnostics.Add(new Diagnostic("plugin-closure-file-missing", DiagnosticSeverity.Error,
                    $"Selected plugin or required master '{name}' is missing from the copied Data root."));
                continue;
            }

            var path = new WorkspacePath(Path.Combine(request.PluginsRoot.Value, entry.Plugin.Value));
            var inspection = await InspectAsync(request, entry, path, diagnostics, cancellationToken);
            if (inspection is null) continue;

            inspections.Add(name, inspection);
            graph.Add(name, inspection.Masters);
            var sourceHash = await HashPluginAsync(entry, path, diagnostics, cancellationToken);
            if (sourceHash is not null) hashes.Add(name, sourceHash.Value);
            QueueMasters(entry, inspection, entryByName, pending, diagnostics);
        }

        DetectCycles(graph, diagnostics);
        var reviewedEntries = loadOrder.Entries.Select(entry =>
        {
            var inClosure = closure.Contains(entry.Plugin.Value);
            var path = new WorkspacePath(Path.Combine(request.PluginsRoot.Value, entry.Plugin.Value));
            return new PluginClosureReviewEntry(
                entry.Plugin, entry.Order, entry.Enabled, entry.Exists,
                requestedNames.Contains(entry.Plugin.Value),
                inClosure && !requestedNames.Contains(entry.Plugin.Value),
                inspections.ContainsKey(entry.Plugin.Value), path,
                hashes.GetValueOrDefault(entry.Plugin.Value),
                inspections.GetValueOrDefault(entry.Plugin.Value)?.Masters ?? ImmutableArray<PluginName>.Empty);
        }).ToImmutableArray();
        var reviewedByName = reviewedEntries.ToDictionary(
            item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase);
        var resolvedLoadOrder = loadOrder with
        {
            Entries = loadOrder.Entries.Select(entry =>
                entry with { Masters = reviewedByName[entry.Plugin.Value].Masters }).ToImmutableArray()
        };
        var orderedClosure = reviewedEntries.Where(item => closure.Contains(item.Plugin.Value))
            .OrderBy(item => item.Order).Select(item => item.Plugin).ToImmutableArray();
        return new PluginClosureReviewResult(request.Edition, resolvedLoadOrder,
            reviewedEntries, orderedClosure, diagnostics.ToImmutable());
    }

    private async ValueTask<PluginInspection?> InspectAsync(
        PluginClosureReviewRequest request,
        PluginLoadOrderResolvedEntry entry,
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await pluginReader.ReadAsync(
                new PluginReadRequest(request.Edition, path), cancellationToken);
            if (inspection.Edition == request.Edition &&
                string.Equals(inspection.Plugin.Value, entry.Plugin.Value, StringComparison.OrdinalIgnoreCase))
                return inspection;
            diagnostics.Add(new Diagnostic("plugin-inspection-identity-mismatch", DiagnosticSeverity.Error,
                $"Plugin '{entry.Plugin.Value}' returned mismatched inspection identity."));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-game-incompatible", DiagnosticSeverity.Error,
                $"Plugin '{entry.Plugin.Value}' is not valid for {request.Edition.ToWireName()}: {exception.Message}"));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-invalid", DiagnosticSeverity.Error,
                $"Plugin '{entry.Plugin.Value}' is malformed: {exception.Message}"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("plugin-read-failed", DiagnosticSeverity.Error,
                $"Plugin '{entry.Plugin.Value}' could not be read: {exception.Message}"));
        }
        return null;
    }

    private static async ValueTask<Sha256Hash?> HashPluginAsync(
        PluginLoadOrderResolvedEntry entry,
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("plugin-hash-failed", DiagnosticSeverity.Error,
                $"Plugin '{entry.Plugin.Value}' could not be hash-bound: {exception.Message}"));
            return null;
        }
    }

    private static void QueueMasters(
        PluginLoadOrderResolvedEntry entry,
        PluginInspection inspection,
        Dictionary<string, PluginLoadOrderResolvedEntry> entryByName,
        Queue<string> pending,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var master in inspection.Masters)
        {
            if (!entryByName.TryGetValue(master.Value, out var masterEntry))
            {
                diagnostics.Add(new Diagnostic("plugin-master-missing-from-load-order", DiagnosticSeverity.Error,
                    $"Plugin '{entry.Plugin.Value}' requires master '{master.Value}', which is not listed."));
                continue;
            }
            if (!masterEntry.Exists)
                diagnostics.Add(new Diagnostic("plugin-master-file-missing", DiagnosticSeverity.Error,
                    $"Plugin '{entry.Plugin.Value}' requires missing master '{master.Value}'."));
            if (masterEntry.Order >= entry.Order)
                diagnostics.Add(new Diagnostic("plugin-master-after-plugin", DiagnosticSeverity.Error,
                    $"Master '{master.Value}' must load before '{entry.Plugin.Value}'."));
            pending.Enqueue(master.Value);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PluginClosureReviewResult EmptyClosure(
        GameEdition edition,
        PluginLoadOrderResult loadOrder,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(edition, loadOrder, ImmutableArray<PluginClosureReviewEntry>.Empty,
            ImmutableArray<PluginName>.Empty, diagnostics);
}
