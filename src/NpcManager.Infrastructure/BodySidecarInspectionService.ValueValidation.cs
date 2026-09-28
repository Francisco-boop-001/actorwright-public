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
    private static void AddSseOnlyDiagnostic(GameEdition edition, string field, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (edition == GameEdition.SkyrimSpecialEdition) return;
        diagnostics.Add(new Diagnostic("body-sidecar-game-field", DiagnosticSeverity.Error,
            $"'{path}.{field}' is supported only for Skyrim Special Edition sidecars."));
    }

    private static string? ReadOptionalString(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-string", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be a string."));
            return null;
        }
        return value.GetString();
    }

    private static string? ReadRequiredString(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var value = ReadOptionalString(parent, name, path, diagnostics);
        if (value is null && !parent.TryGetProperty(name, out _))
            diagnostics.Add(new Diagnostic("body-sidecar-required-field", DiagnosticSeverity.Error,
                $"'{path}.{name}' is required."));
        return value;
    }

    private static void ReadOptionalPath(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, bool required)
    {
        var value = required ? ReadRequiredString(parent, name, path, diagnostics) : ReadOptionalString(parent, name, path, diagnostics);
        if (value is not null && !IsSafeRelativePath(value))
            diagnostics.Add(new Diagnostic("body-sidecar-asset-path", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be a safe relative asset path."));
    }

    private static void ReadOptionalInt(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (parent.TryGetProperty(name, out var value) && !value.TryGetInt32(out _))
            diagnostics.Add(new Diagnostic("body-sidecar-integer", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an integer."));
    }

    private static void ReadRequiredInt(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetInt32(out _))
            diagnostics.Add(new Diagnostic("body-sidecar-integer", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an integer."));
    }

    private static void ReadOptionalUInt(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (parent.TryGetProperty(name, out var value) && (!value.TryGetUInt32(out _)))
            diagnostics.Add(new Diagnostic("body-sidecar-unsigned-integer", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an unsigned integer."));
    }

    private static void ReadRequiredUInt(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!parent.TryGetProperty(name, out var value) || !value.TryGetUInt32(out _))
            diagnostics.Add(new Diagnostic("body-sidecar-unsigned-integer", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be an unsigned integer."));
    }

    private static void ValidateRequiredFinite(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!parent.TryGetProperty(name, out var value) || !TryFinite(value, out _))
            diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be a finite number."));
    }

    private static void ValidateOptionalFinite(JsonElement parent, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (parent.TryGetProperty(name, out var value) && !TryFinite(value, out _))
            diagnostics.Add(new Diagnostic("body-sidecar-number", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be a finite number."));
    }

    private static void ValidateFloatArray(JsonElement parent, string name, int expectedLength, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!parent.TryGetProperty(name, out var value)) return;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != expectedLength ||
            value.EnumerateArray().Any(item => !TryFinite(item, out _)))
        {
            diagnostics.Add(new Diagnostic("body-sidecar-float-array", DiagnosticSeverity.Error,
                $"'{path}.{name}' must contain exactly {expectedLength} finite numbers."));
        }
    }

    private static bool ValidateArrayLimit(JsonElement value, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value.GetArrayLength() <= MaxArrayEntries) return true;
        diagnostics.Add(new Diagnostic("body-sidecar-array-count-limit",
            DiagnosticSeverity.Error,
            $"'{path}' exceeds the {MaxArrayEntries} entry array limit."));
        return false;
    }

    private static bool TryFinite(JsonElement value, out float number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out number) && float.IsFinite(number);
    }

    private static void CheckKnownFields(JsonElement value, ImmutableHashSet<string> known, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
            if (!known.Contains(property.Name))
                diagnostics.Add(new Diagnostic("body-sidecar-unknown-field", DiagnosticSeverity.Error,
                    $"Unknown field '{path}.{property.Name}' cannot be preserved by the typed sidecar contract."));
    }

    private static void ValidateJsonStructure(JsonElement value, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    diagnostics.Add(new Diagnostic("body-sidecar-duplicate-key", DiagnosticSeverity.Error,
                        $"Duplicate JSON property '{path}.{property.Name}' is not accepted."));
                ValidateJsonStructure(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (!ValidateArrayLimit(value, path, diagnostics)) return;
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateJsonStructure(item, $"{path}[{index}]", diagnostics);
                index++;
            }
        }
    }

    private static bool TryValidateIdentifier(string value, out string error)
    {
        error = string.Empty;
        var separator = value.IndexOf('|');
        if (separator <= 0 || separator != value.LastIndexOf('|') || separator == value.Length - 1)
        {
            error = "expected Master.esp|HEX6";
            return false;
        }
        try { _ = new PluginName(value[..separator]); }
        catch (ArgumentException exception) { error = exception.Message; return false; }
        var local = value[(separator + 1)..];
        if (local.Length != 6 || !uint.TryParse(local, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var formId) || formId > 0xFFFFFF)
        {
            error = "the local FormID must be exactly six hexadecimal digits";
            return false;
        }
        return true;
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.StartsWith('/') || value.StartsWith('\\') || value.Contains(':'))
            return false;
        return value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(part => part is not ("." or ".."));
    }

    private static byte[] Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            root.WriteTo(writer);
            writer.Flush();
        }
        return stream.ToArray();
    }

    private static bool IsStableRoundTrip(byte[] canonical, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var parsed = ParseJson(canonical, diagnostics);
        if (parsed is null) return false;
        var second = Canonicalize(parsed.RootElement);
        return canonical.AsSpan().SequenceEqual(second);
    }

}
