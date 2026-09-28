using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Enumerates typed records from an explicit copied Data root. It never infers
/// a live profile and never writes game-facing files.
/// </summary>
public sealed class RecordListingService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IRecordListingService
{
    public async ValueTask<RecordListResult> ListAsync(
        RecordListRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = policy.Evaluate(labRoot, request.DataRoot).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new RecordListResult(request.Edition, [], [], diagnostics.ToImmutable());
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("data-root-missing", DiagnosticSeverity.Error,
                "The explicit Data root does not exist."));
            return new RecordListResult(request.Edition, [], [], diagnostics.ToImmutable());
        }

        var paths = ResolvePluginPaths(request, diagnostics);
        var plugins = ImmutableArray.CreateBuilder<PluginName>(paths.Length);
        var records = ImmutableArray.CreateBuilder<ListedRecord>();
        var seenPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginName plugin;
            try { plugin = new PluginName(Path.GetFileName(path)); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("plugin-name-invalid", DiagnosticSeverity.Error, exception.Message));
                continue;
            }

            if (!seenPlugins.Add(plugin.Value))
            {
                diagnostics.Add(new Diagnostic("plugin-duplicate", DiagnosticSeverity.Error,
                    $"Plugin '{plugin.Value}' was requested more than once."));
                continue;
            }
            plugins.Add(plugin);
            if (IsReparsePoint(path, diagnostics, plugin.Value)) continue;

            PluginInspection inspection;
            try
            {
                inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition,
                    new WorkspacePath(path)), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                diagnostics.Add(new Diagnostic("plugin-read-failed", DiagnosticSeverity.Error,
                    $"Plugin '{plugin.Value}' could not be read: {exception.Message}"));
                continue;
            }

            diagnostics.AddRange(inspection.Diagnostics);
            foreach (var record in inspection.Records.Where(record => Matches(request, record)))
            {
                records.Add(new ListedRecord(plugin, record.FormId, record.Signature, record.EditorId,
                    record.Name, record.IsNpc, record.IsDeleted, record.RawRecordSha256));
            }
        }

        return new RecordListResult(request.Edition, plugins.ToImmutable(),
            records.OrderBy(record => record.Plugin.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.FormId.Value)
                .ThenBy(record => record.Signature, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(), diagnostics.ToImmutable());
    }

    private static ImmutableArray<string> ResolvePluginPaths(
        RecordListRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.PluginOrder.IsDefaultOrEmpty)
        {
            var paths = ImmutableArray.CreateBuilder<string>(request.PluginOrder.Length);
            foreach (var plugin in request.PluginOrder)
            {
                var path = Path.Combine(request.DataRoot.Value, plugin.Value);
                if (!File.Exists(path))
                {
                    diagnostics.Add(new Diagnostic("plugin-missing", DiagnosticSeverity.Error,
                        $"Explicit plugin '{plugin.Value}' does not exist under the Data root."));
                    continue;
                }
                paths.Add(Path.GetFullPath(path));
            }
            return paths.ToImmutable();
        }

        try
        {
            return Directory.EnumerateFiles(request.DataRoot.Value)
                .Where(IsPlugin)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-enumeration-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-enumeration-denied", DiagnosticSeverity.Error, exception.Message));
        }
        return [];
    }

    private static bool Matches(RecordListRequest request, PluginRecordSummary record)
    {
        if (!string.IsNullOrWhiteSpace(request.Signature) &&
            !string.Equals(record.Signature, request.Signature, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(request.Search)) return true;
        return record.Signature.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ||
               (record.EditorId?.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (record.Name?.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool IsReparsePoint(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string plugin)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-attributes-failed", DiagnosticSeverity.Error,
                $"Plugin '{plugin}' attributes could not be read: {exception.Message}"));
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-attributes-denied", DiagnosticSeverity.Error,
                $"Plugin '{plugin}' attributes could not be read: {exception.Message}"));
            return true;
        }

        diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error,
            $"Plugin '{plugin}' is a reparse point and cannot be read."));
        return true;
    }

    private static bool IsPlugin(string path) =>
        Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);
}
