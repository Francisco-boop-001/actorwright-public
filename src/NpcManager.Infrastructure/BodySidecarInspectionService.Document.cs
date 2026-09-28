using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class BodySidecarInspectionService
{
    private static JsonDocument? ParseJson(byte[] bytes, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 64,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-json-invalid", DiagnosticSeverity.Error,
                $"BodySlide sidecar JSON is invalid: {exception.Message}"));
            return null;
        }
    }

    private static int ReadVersion(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var version))
        {
            diagnostics.Add(new Diagnostic("body-sidecar-version", DiagnosticSeverity.Error,
                "BodySlide sidecar version must be an integer."));
            return 0;
        }
        if (version < 1 || version > CurrentSchemaVersion)
            diagnostics.Add(new Diagnostic("body-sidecar-version-unsupported", DiagnosticSeverity.Error,
                $"BodySlide sidecar version {version} is outside the supported range 1-{CurrentSchemaVersion}."));
        return version;
    }

    private static PluginName? ReadPlugin(JsonElement root, WorkspacePath file,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("plugin", out var value) || value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-plugin", DiagnosticSeverity.Error,
                "BodySlide sidecar plugin must be a string."));
            return null;
        }
        PluginName plugin;
        try { plugin = new PluginName(value.GetString() ?? string.Empty); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-plugin", DiagnosticSeverity.Error, exception.Message));
            return null;
        }
        var expected = Path.GetFileNameWithoutExtension(file.Value);
        var pluginStem = Path.GetFileNameWithoutExtension(plugin.Value);
        if (!string.Equals(expected, pluginStem, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("body-sidecar-plugin-path", DiagnosticSeverity.Error,
                $"Sidecar plugin '{plugin.Value}' does not match the sidecar filename '{expected}'."));
        return plugin;
    }

}
