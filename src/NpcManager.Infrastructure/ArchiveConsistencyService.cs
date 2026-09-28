using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Checks a selected copied plugin against the persisted archive inventory without mutation.</summary>
public sealed class ArchiveConsistencyService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IArchiveConsistencyService
{
    public ValueTask<ArchiveConsistencyResult> EvaluateAsync(
        ArchiveConsistencyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var pluginDataRoot = TryGetParent(request.PluginPath, diagnostics, "plugin");
        var indexParent = TryGetParent(request.AssetIndexPath, diagnostics, "asset-index");
        if (pluginDataRoot is { } pluginRoot)
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, pluginRoot));
        if (indexParent is not null)
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.AssetIndexPath));

        if (!File.Exists(request.PluginPath.Value))
        {
            diagnostics.Add(new Diagnostic("archive-consistency-plugin-missing", DiagnosticSeverity.Error,
                "The selected plugin does not exist."));
        }
        else
        {
            try
            {
                if (File.GetAttributes(request.PluginPath.Value).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("archive-consistency-plugin-reparse", DiagnosticSeverity.Error,
                        "The selected plugin may not be a reparse point."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("archive-consistency-plugin-unreadable", DiagnosticSeverity.Error,
                    $"The selected plugin attributes could not be read: {exception.Message}"));
            }
        }

        try
        {
            _ = new PluginName(Path.GetFileName(request.PluginPath.Value));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("archive-consistency-plugin-invalid", DiagnosticSeverity.Error,
                $"The selected path is not a valid plugin filename: {exception.Message}"));
        }

        if (!File.Exists(request.AssetIndexPath.Value))
        {
            diagnostics.Add(new Diagnostic("archive-consistency-index-missing", DiagnosticSeverity.Error,
                "The persisted asset-index artifact does not exist."));
        }

        AssetIndexArtifactSnapshot snapshot = new(null, null, [], []);
        if (!HasErrors(diagnostics) && File.Exists(request.AssetIndexPath.Value))
            snapshot = AssetIndexArtifactReader.Read(request.AssetIndexPath, cancellationToken);
        diagnostics.AddRange(snapshot.Diagnostics);

        if (snapshot.Edition is { } artifactEdition && artifactEdition != request.Edition)
        {
            diagnostics.Add(new Diagnostic("archive-consistency-edition-mismatch", DiagnosticSeverity.Error,
                $"The asset-index edition is {artifactEdition.ToWireName()}, but the request is {request.Edition.ToWireName()}."));
        }

        if (snapshot.DataRoot is not { } dataRoot)
        {
            if (pluginDataRoot is { } fallbackRoot)
                dataRoot = fallbackRoot;
        }
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, dataRoot));
            if (!Directory.Exists(dataRoot.Value))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-data-root-missing", DiagnosticSeverity.Error,
                    "The asset-index data root does not exist."));
            }
            if (pluginDataRoot is { } actualPluginRoot && !SamePath(actualPluginRoot, dataRoot))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-plugin-data-root-mismatch", DiagnosticSeverity.Error,
                    "The selected plugin is not under the asset-index data root."));
            }
        }

        ImmutableArray<ArchiveConsistencyEntry> entries = [];
        if (snapshot.DataRoot is { } readableRoot && Directory.Exists(readableRoot.Value) && !HasErrors(diagnostics))
            entries = CompareArchives(request.Edition, readableRoot, snapshot.ArchiveSources, diagnostics, cancellationToken);

        var isConsistent = !HasErrors(diagnostics);
        return ValueTask.FromResult(new ArchiveConsistencyResult(request.Edition, request.PluginPath,
            request.AssetIndexPath, snapshot.DataRoot ?? pluginDataRoot, isConsistent, entries, diagnostics.ToImmutable()));
    }

    private static ImmutableArray<ArchiveConsistencyEntry> CompareArchives(
        GameEdition edition,
        WorkspacePath dataRoot,
        ImmutableArray<string> indexedSources,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var extension = edition == GameEdition.Fallout4 ? ".ba2" : ".bsa";
        var actualSupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(dataRoot.Value, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("archive-consistency-archive-reparse", DiagnosticSeverity.Error,
                        $"Archive '{Path.GetFileName(file)}' is a reparse point and cannot be trusted."));
                    continue;
                }
                if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    actualSupported.Add(Path.GetFileName(file));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("archive-consistency-archive-scan-failed", DiagnosticSeverity.Error,
                $"The Data root archive scan failed: {exception.Message}"));
            return [];
        }

        var indexed = indexedSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexedSupported = indexed.Where(name => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allNames = actualSupported.Union(indexedSupported, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToImmutableArray();
        var entries = ImmutableArray.CreateBuilder<ArchiveConsistencyEntry>();
        foreach (var name in allNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var present = actualSupported.Contains(name);
            var isIndexed = indexedSupported.Contains(name);
            var status = present && isIndexed
                ? ArchiveConsistencyStatus.Matched
                : present ? ArchiveConsistencyStatus.NotIndexed : ArchiveConsistencyStatus.MissingOnDisk;
            entries.Add(new ArchiveConsistencyEntry(name, present, isIndexed, status));
            if (status == ArchiveConsistencyStatus.NotIndexed)
            {
                diagnostics.Add(new Diagnostic("archive-consistency-archive-not-indexed", DiagnosticSeverity.Error,
                    $"Archive '{name}' exists on disk but has no provider in the asset index."));
            }
            else if (status == ArchiveConsistencyStatus.MissingOnDisk)
            {
                diagnostics.Add(new Diagnostic("archive-consistency-archive-missing-on-disk", DiagnosticSeverity.Error,
                    $"Archive '{name}' is listed by the asset index but does not exist on disk."));
            }
        }

        foreach (var source in indexed.Except(indexedSupported, StringComparer.OrdinalIgnoreCase))
        {
            if (!source.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("archive-consistency-archive-edition-mismatch", DiagnosticSeverity.Error,
                    $"Archive '{source}' does not use the {edition.ToWireName()} archive extension '{extension}'."));
                entries.Add(new ArchiveConsistencyEntry(source, File.Exists(Path.Combine(dataRoot.Value, source)), true,
                    ArchiveConsistencyStatus.UnsupportedEdition));
            }
        }

        return entries.ToImmutable()
            .OrderBy(entry => entry.ArchiveName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ArchiveName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static WorkspacePath? TryGetParent(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role)
    {
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is null)
        {
            diagnostics.Add(new Diagnostic($"archive-consistency-{role}-parent-invalid", DiagnosticSeverity.Error,
                $"The {role} path has no parent directory."));
            return null;
        }
        try { return new WorkspacePath(parent); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic($"archive-consistency-{role}-parent-invalid", DiagnosticSeverity.Error,
                $"The {role} parent directory is invalid: {exception.Message}"));
            return null;
        }
    }

    private static bool SamePath(WorkspacePath left, WorkspacePath right) =>
        string.Equals(left.Value.TrimEnd(Path.DirectorySeparatorChar),
            right.Value.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
