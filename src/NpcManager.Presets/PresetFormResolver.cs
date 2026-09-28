using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

public sealed class PresetFormResolver(IWorkspacePolicy policy, WorkspacePath labRoot) : IPresetFormResolver
{
    public async ValueTask<PresetResolutionResult> ResolveAsync(PresetResolutionRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = Validate(request.LoadOrderPath).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
        try
        {
            if (new FileInfo(request.LoadOrderPath.Value).Length > PresetJsonSupport.MaxBytes)
                throw new InvalidDataException($"Load-order file '{request.LoadOrderPath}' exceeds {PresetJsonSupport.MaxBytes} bytes.");
            var bytes = await File.ReadAllBytesAsync(request.LoadOrderPath.Value, cancellationToken);
            if (!PresetJsonSupport.TryParse(bytes, out var document, out var jsonDiagnostics) || document is null)
            {
                diagnostics.AddRange(jsonDiagnostics);
                return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
            }
            using (document)
            {
                var map = ReadLoadOrder(document.RootElement, diagnostics);
                if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                    return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
                if (request.Identifier.Plugin is null || request.Identifier.FormId is null)
                {
                    diagnostics.Add(new Diagnostic("preset-form-identifier-unresolved", DiagnosticSeverity.Error,
                        $"'{request.Identifier.Raw}' is not a portable Plugin|FormID identifier."));
                    return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
                }
                if (!map.TryGetValue(request.Identifier.Plugin.Value.Value, out var pluginIndex))
                {
                    diagnostics.Add(new Diagnostic("preset-plugin-unresolved", DiagnosticSeverity.Error,
                        $"Plugin '{request.Identifier.Plugin.Value}' is absent from the supplied load-order map."));
                    return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
                }
                if (request.DataRoot is { } dataRoot)
                {
                    diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, dataRoot));
                    if (!Directory.Exists(dataRoot.Value))
                        diagnostics.Add(new Diagnostic("preset-data-root-missing", DiagnosticSeverity.Error,
                            $"Copied plugin directory '{dataRoot}' does not exist."));
                    if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                        return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
                    return ResolveCopied(request, dataRoot, map, diagnostics, cancellationToken);
                }
                if (map.Values.Any(index => index > byte.MaxValue))
                    throw new InvalidDataException("More than 256 enabled entries require --data-root to resolve separate full/light indexes.");
                var localId = request.Identifier.FormId.Value.Value & 0x00FF_FFFF;
                var resolved = new FormId(((uint)pluginIndex << 24) | localId);
                return new PresetResolutionResult(request.Identifier, resolved, diagnostics.ToImmutable());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic(exception is InvalidDataException ? "preset-load-order-invalid" : "preset-load-order-read-failed",
                DiagnosticSeverity.Error, exception.Message));
            return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> Validate(WorkspacePath path)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preset-load-order-outside-lab", DiagnosticSeverity.Error, "Load-order maps must remain under the K-only lab root."));
        if (!File.Exists(path.Value)) diagnostics.Add(new Diagnostic("preset-load-order-missing", DiagnosticSeverity.Error, "The explicit load-order map does not exist."));
        if (path.Value.IndexOf(':', 2) >= 0)
            diagnostics.Add(new Diagnostic("preset-load-order-ads-refused", DiagnosticSeverity.Error, "Load-order maps cannot use alternate data stream paths."));
        if (File.Exists(path.Value))
        {
            try
            {
                if (File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(new Diagnostic("preset-load-order-reparse-refused", DiagnosticSeverity.Error,
                        "Load-order maps must be ordinary files, not reparse points."));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("preset-load-order-attributes-failed", DiagnosticSeverity.Error, exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("preset-load-order-attributes-denied", DiagnosticSeverity.Error, exception.Message));
            }
        }
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is null) diagnostics.Add(new Diagnostic("preset-load-order-parent-invalid", DiagnosticSeverity.Error, "Load-order map has no parent directory."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private PresetResolutionResult ResolveCopied(PresetResolutionRequest request, WorkspacePath dataRoot,
        Dictionary<string, int> map, ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        uint fullIndex = 0, lightIndex = 0;
        FormId? resolved = null;
        Span<byte> header = stackalloc byte[24];
        foreach (var entry in map.OrderBy(item => item.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Copied provider name '{entry.Key}' is not a valid filename.");
            var path = new WorkspacePath(Path.Combine(dataRoot.Value, entry.Key));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                return new PresetResolutionResult(request.Identifier, null, diagnostics.ToImmutable());
            using var file = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length < header.Length)
                throw new InvalidDataException($"Copied provider '{path}' has a truncated TES4 header.");
            file.ReadExactly(header);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (!header[..4].SequenceEqual("TES4"u8) || size < 18 || size > file.Length - 24)
                throw new InvalidDataException($"Copied provider '{path}' has an invalid TES4 header boundary.");
            bool light = (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) & 0x200) != 0;
            if ((light && lightIndex >= 0x1000) || (!light && fullIndex >= 0xFE))
                throw new InvalidDataException($"Copied load order exceeds the {(light ? "4096 light" : "254 full")} plugin index limit at '{entry.Key}'.");
            uint prefix = light ? 0xFE000000 | (lightIndex++ << 12) : fullIndex++ << 24;
            if (string.Equals(entry.Key, request.Identifier.Plugin!.Value.Value, StringComparison.OrdinalIgnoreCase))
                resolved = new FormId(prefix | (request.Identifier.FormId!.Value.Value & (light ? 0xFFFU : 0x00FFFFFFU)));
        }
        return new PresetResolutionResult(request.Identifier, resolved, diagnostics.ToImmutable());
    }

    private static Dictionary<string, int> ReadLoadOrder(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-load-order-shape", DiagnosticSeverity.Error, "Load order must be a schema-1 JSON document or a legacy object of plugin name to byte index."));
            return map;
        }
        var indexes = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("schemaVersion", out var version) || root.TryGetProperty("plugins", out _))
        {
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1 ||
                !root.TryGetProperty("edition", out var edition) || edition.ValueKind != JsonValueKind.String ||
                !GameEditionExtensions.TryParseWireName(edition.GetString()!, out _) ||
                !root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array || plugins.GetArrayLength() == 0)
                throw new InvalidDataException("Load-order schema-1 requires schemaVersion 1, a supported edition and a nonempty plugins array.");
            foreach (var row in plugins.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !row.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out int index) || index < 0 ||
                    !row.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Each load-order plugins row requires a plugin name, non-negative integer order and boolean enabled.");
                string plugin;
                try { plugin = new PluginName(name.GetString()!).Value; }
                catch (ArgumentException exception) { throw new InvalidDataException($"Invalid load-order plugin '{name}': {exception.Message}"); }
                if (!names.Add(plugin) || !indexes.Add(index))
                    throw new InvalidDataException($"Duplicate load-order plugin or order at '{plugin}' / {index}.");
                if (enabled.GetBoolean()) map.Add(plugin, index);
            }
            return map.OrderBy(item => item.Value).Select((item, index) => (item.Key, Index: index))
                .ToDictionary(item => item.Key, item => item.Index, StringComparer.OrdinalIgnoreCase);
        }
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var index) || index < 0 || index > byte.MaxValue)
            {
                diagnostics.Add(new Diagnostic("preset-load-order-index-invalid", DiagnosticSeverity.Error, $"Load-order index for '{property.Name}' must be an integer from 0 to 255."));
                continue;
            }
            try
            {
                map.Add(new PluginName(property.Name).Value, index);
                if (!indexes.Add(index)) throw new ArgumentException($"Duplicate load-order index {index}.");
            }
            catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("preset-load-order-plugin-invalid", DiagnosticSeverity.Error, exception.Message)); }
        }
        return map;
    }
}
