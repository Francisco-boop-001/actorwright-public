using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class PluginLoadOrderService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Func<string, FileAttributes>? fileAttributesReader = null) : IPluginLoadOrderService
{
    private const int MaxManifestBytes = 1 * 1024 * 1024;
    private const int MaxPluginEntries = 4096;

    public async ValueTask<PluginLoadOrderResult> ResolveAsync(
        PluginLoadOrderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PluginLoadOrderResult(request.Edition, ImmutableArray<PluginLoadOrderResolvedEntry>.Empty,
                null, diagnostics.ToImmutable());

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.LoadOrderPath.Value);
            if (info.Length > MaxManifestBytes)
            {
                diagnostics.Add(new Diagnostic("load-order-size-limit", DiagnosticSeverity.Error,
                    $"Load-order manifest exceeds the {MaxManifestBytes} byte safety limit."));
                return new PluginLoadOrderResult(request.Edition, ImmutableArray<PluginLoadOrderResolvedEntry>.Empty,
                    null, diagnostics.ToImmutable());
            }
            bytes = await File.ReadAllBytesAsync(request.LoadOrderPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("load-order-read-failed", DiagnosticSeverity.Error, exception.Message));
            return new PluginLoadOrderResult(request.Edition, ImmutableArray<PluginLoadOrderResolvedEntry>.Empty,
                null, diagnostics.ToImmutable());
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("load-order-read-denied", DiagnosticSeverity.Error, exception.Message));
            return new PluginLoadOrderResult(request.Edition, ImmutableArray<PluginLoadOrderResolvedEntry>.Empty,
                null, diagnostics.ToImmutable());
        }

        var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        int parseDiagnosticStart = diagnostics.Count;
        var parsed = ParseManifest(bytes, request.Edition, diagnostics);
        for (int i = parseDiagnosticStart; i < diagnostics.Count; i++)
            diagnostics[i] = diagnostics[i] with
            {
                Message = $"Load-order manifest '{request.LoadOrderPath}': {diagnostics[i].Message}"
            };
        if (parsed.IsDefaultOrEmpty || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PluginLoadOrderResult(request.Edition, ImmutableArray<PluginLoadOrderResolvedEntry>.Empty,
                sourceHash, diagnostics.ToImmutable());

        var actual = DiscoverPlugins(request.PluginsRoot.Value, diagnostics);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orders = new HashSet<int>();
        var entries = ImmutableArray.CreateBuilder<PluginLoadOrderResolvedEntry>(parsed.Length);
        foreach (var item in parsed.OrderBy(item => item.Order).ThenBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (!names.Add(item.Plugin.Value))
                diagnostics.Add(new Diagnostic("load-order-duplicate-plugin", DiagnosticSeverity.Error,
                    $"Plugin '{item.Plugin.Value}' appears more than once in the load-order manifest."));
            if (!orders.Add(item.Order))
                diagnostics.Add(new Diagnostic("load-order-duplicate-index", DiagnosticSeverity.Error,
                    $"Load-order index {item.Order} is assigned more than once."));

            var exists = actual.ContainsKey(item.Plugin.Value);
            if (item.Enabled && !exists)
                diagnostics.Add(new Diagnostic("load-order-enabled-plugin-missing", DiagnosticSeverity.Error,
                    $"Enabled plugin '{item.Plugin.Value}' is missing from the explicit plugin root."));
            else if (!item.Enabled && !exists)
                diagnostics.Add(new Diagnostic("load-order-disabled-plugin-missing", DiagnosticSeverity.Warning,
                    $"Disabled plugin '{item.Plugin.Value}' is missing from the explicit plugin root."));
            entries.Add(new PluginLoadOrderResolvedEntry(item.Plugin, item.Order, item.Enabled, exists,
                ImmutableArray<PluginName>.Empty));
        }

        foreach (var actualPlugin in actual.Keys.Where(name => !names.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("load-order-unlisted-plugin", DiagnosticSeverity.Warning,
                $"Plugin '{actualPlugin}' exists in the explicit root but is not listed in the load-order manifest."));

        return new PluginLoadOrderResult(request.Edition, entries.ToImmutable(), sourceHash, diagnostics.ToImmutable());
    }

    public async ValueTask<PluginCompatibilityResult> ValidateAsync(
        PluginCompatibilityRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PluginName? plugin = null;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try { plugin = new PluginName(Path.GetFileName(request.PluginPath.Value)); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-name-invalid", DiagnosticSeverity.Error, exception.Message));
        }

        var parent = Path.GetDirectoryName(request.PluginPath.Value);
        if (parent is null)
        {
            diagnostics.Add(new Diagnostic("plugin-parent-invalid", DiagnosticSeverity.Error,
                "The plugin path has no parent directory."));
            return FailedCompatibility(request.Edition, plugin, diagnostics.ToImmutable());
        }

        var closureReview = await ReviewClosureAsync(new PluginClosureReviewRequest(
            request.Edition, new WorkspacePath(parent), request.LoadOrderPath), cancellationToken);
        var loadOrder = closureReview.LoadOrder;
        diagnostics.AddRange(closureReview.Diagnostics);
        if (plugin is null || loadOrder.Entries.IsDefaultOrEmpty)
            return new PluginCompatibilityResult(request.Edition, plugin, false, ImmutableArray<PluginName>.Empty,
                loadOrder, diagnostics.ToImmutable());

        var entryByName = loadOrder.Entries.ToDictionary(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase);
        if (!entryByName.TryGetValue(plugin.Value.Value, out var targetEntry))
        {
            diagnostics.Add(new Diagnostic("plugin-not-in-load-order", DiagnosticSeverity.Error,
                $"Plugin '{plugin.Value.Value}' is not listed in the load-order manifest."));
            return new PluginCompatibilityResult(request.Edition, plugin, false, ImmutableArray<PluginName>.Empty,
                loadOrder, diagnostics.ToImmutable());
        }
        if (!targetEntry.Enabled)
            diagnostics.Add(new Diagnostic("plugin-disabled", DiagnosticSeverity.Error,
                $"Plugin '{plugin.Value.Value}' is disabled in the load-order manifest."));
        if (!targetEntry.Exists)
            diagnostics.Add(new Diagnostic("plugin-file-missing", DiagnosticSeverity.Error,
                $"Plugin '{plugin.Value.Value}' is missing from the explicit plugin root."));

        foreach (var dependency in closureReview.Entries.Where(item => item.RequiredMaster && !item.Enabled))
            diagnostics.Add(new Diagnostic("plugin-master-disabled", DiagnosticSeverity.Error,
                $"Required master '{dependency.Plugin.Value}' is disabled in the load-order manifest."));

        var targetMasters = closureReview.Entries.FirstOrDefault(item =>
                string.Equals(item.Plugin.Value, plugin.Value.Value, StringComparison.OrdinalIgnoreCase))
            ?.Masters ?? ImmutableArray<PluginName>.Empty;
        var compatible = !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        return new PluginCompatibilityResult(request.Edition, plugin, compatible, targetMasters,
            loadOrder, diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidatePaths(PluginLoadOrderRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.PluginsRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("load-order-plugins-outside-lab", DiagnosticSeverity.Error,
                "Plugin roots must remain under the K-only lab root."));
        if (!request.LoadOrderPath.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("load-order-file-outside-lab", DiagnosticSeverity.Error,
                "Load-order manifests must remain under the K-only lab root."));
        if (!Directory.Exists(request.PluginsRoot.Value))
            diagnostics.Add(new Diagnostic("load-order-plugins-missing", DiagnosticSeverity.Error,
                "The explicit plugin root does not exist."));
        if (!File.Exists(request.LoadOrderPath.Value))
            diagnostics.Add(new Diagnostic("load-order-file-missing", DiagnosticSeverity.Error,
                "The explicit load-order manifest does not exist."));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.PluginsRoot));
        AddReparseDiagnostic(diagnostics, request.PluginsRoot.Value, "plugin root");
        AddReparseDiagnostic(diagnostics, request.LoadOrderPath.Value, "load-order manifest");
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<PluginLoadOrderEntry> ParseManifest(byte[] bytes, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            ReadOnlySpan<byte> json = bytes;
            ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
            if (json.StartsWith(utf8Bom))
                json = json[utf8Bom.Length..];
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("load-order-root-invalid", DiagnosticSeverity.Error,
                    $"Expected a schema-1 JSON object; observed {root.ValueKind}."));
                return ImmutableArray<PluginLoadOrderEntry>.Empty;
            }
            if (HasDuplicateKeys(root, diagnostics))
                return ImmutableArray<PluginLoadOrderEntry>.Empty;
            if (!TryGetInt(root, "schemaVersion", out var schemaVersion) || schemaVersion != 1)
            {
                diagnostics.Add(new Diagnostic("load-order-schema-unsupported", DiagnosticSeverity.Error,
                    "Load-order schemaVersion must be 1."));
                return ImmutableArray<PluginLoadOrderEntry>.Empty;
            }
            if (!TryGetString(root, "edition", out var editionText) ||
                !GameEditionExtensions.TryParseWireName(editionText, out var manifestEdition) || manifestEdition != edition)
            {
                diagnostics.Add(new Diagnostic("load-order-edition-mismatch", DiagnosticSeverity.Error,
                    "Load-order manifest edition must match the requested game edition."));
                return ImmutableArray<PluginLoadOrderEntry>.Empty;
            }
            if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array || plugins.GetArrayLength() == 0)
            {
                diagnostics.Add(new Diagnostic("load-order-plugins-invalid", DiagnosticSeverity.Error,
                    "Load-order manifest plugins must be a non-empty array."));
                return ImmutableArray<PluginLoadOrderEntry>.Empty;
            }
            if (plugins.GetArrayLength() > MaxPluginEntries)
            {
                diagnostics.Add(new Diagnostic("load-order-plugin-count-limit", DiagnosticSeverity.Error,
                    $"Load-order manifest contains more than {MaxPluginEntries} plugin entries."));
                return ImmutableArray<PluginLoadOrderEntry>.Empty;
            }

            var entries = ImmutableArray.CreateBuilder<PluginLoadOrderEntry>();
            var index = 0;
            foreach (var element in plugins.EnumerateArray())
            {
                var path = $"$.plugins[{index++}]";
                if (element.ValueKind != JsonValueKind.Object || HasDuplicateKeys(element, diagnostics) ||
                    !TryGetString(element, "name", out var nameText) || !TryGetInt(element, "order", out var order) ||
                    !element.TryGetProperty("enabled", out var enabledElement) || enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False)
                {
                    diagnostics.Add(new Diagnostic("load-order-entry-invalid", DiagnosticSeverity.Error,
                        $"{path} requires name, non-negative order, and boolean enabled fields."));
                    continue;
                }
                if (order < 0)
                {
                    diagnostics.Add(new Diagnostic("load-order-index-invalid", DiagnosticSeverity.Error,
                        $"{path}.order must be non-negative."));
                    continue;
                }
                try { entries.Add(new PluginLoadOrderEntry(new PluginName(nameText), order, enabledElement.GetBoolean())); }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(new Diagnostic("load-order-plugin-invalid", DiagnosticSeverity.Error,
                        $"{path}.name is invalid: {exception.Message}"));
                }
            }
            return entries.ToImmutable();
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("load-order-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return ImmutableArray<PluginLoadOrderEntry>.Empty;
        }
    }

    private Dictionary<string, string> DiscoverPlugins(string root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root); }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("load-order-plugins-read-failed", DiagnosticSeverity.Error, exception.Message));
            return paths;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("load-order-plugins-read-denied", DiagnosticSeverity.Error, exception.Message));
            return paths;
        }
        foreach (var path in files.Where(IsPlugin))
        {
            try
            {
                if ((fileAttributesReader?.Invoke(path) ?? File.GetAttributes(path)).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("load-order-plugin-reparse-refused", DiagnosticSeverity.Error,
                        $"Plugin '{Path.GetFileName(path)}' is a reparse point and cannot be trusted."));
                    continue;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("load-order-plugin-inspection-failed", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(path)}' could not be inspected: {exception.Message}"));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("load-order-plugin-inspection-denied", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(path)}' could not be inspected: {exception.Message}"));
                continue;
            }
            var name = Path.GetFileName(path);
            if (!paths.TryAdd(name, path))
                diagnostics.Add(new Diagnostic("load-order-filesystem-duplicate", DiagnosticSeverity.Error,
                    $"The plugin root contains duplicate plugin names differing only by case: '{name}'."));
        }
        return paths;
    }

    private static bool IsPlugin(string path) =>
        Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);

    private static bool HasDuplicateKeys(JsonElement element, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = false;
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name)) duplicate = true;
        if (duplicate) diagnostics.Add(new Diagnostic("load-order-duplicate-key", DiagnosticSeverity.Error,
            "Load-order JSON contains a duplicate object key."));
        return duplicate;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        value = text;
        return true;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static void DetectCycles(Dictionary<string, ImmutableArray<PluginName>> graph,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in graph.Keys) Visit(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        void Visit(string name, HashSet<string> path)
        {
            if (states.GetValueOrDefault(name) == 2) return;
            if (states.GetValueOrDefault(name) == 1)
            {
                diagnostics.Add(new Diagnostic("plugin-master-cycle", DiagnosticSeverity.Error,
                    $"Plugin master dependency cycle includes '{name}'."));
                return;
            }
            states[name] = 1;
            path.Add(name);
            if (graph.TryGetValue(name, out var masters))
                foreach (var master in masters)
                    if (graph.ContainsKey(master.Value)) Visit(master.Value, path);
            path.Remove(name);
            states[name] = 2;
        }
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("load-order-reparse-refused", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("load-order-path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("load-order-path-inspection-denied", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static PluginCompatibilityResult FailedCompatibility(GameEdition edition, PluginName? plugin,
        ImmutableArray<Diagnostic> diagnostics) => new(edition, plugin, false, ImmutableArray<PluginName>.Empty,
        new PluginLoadOrderResult(edition, ImmutableArray<PluginLoadOrderResolvedEntry>.Empty, null, diagnostics), diagnostics);

}
