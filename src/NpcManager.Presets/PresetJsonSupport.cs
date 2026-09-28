using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;

namespace NpcManager.Presets;

internal static class PresetJsonSupport
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxArrayItems = 8192;

    /// <summary>
    /// Sculpt <c>data</c> arrays carry vertex rows; High Poly Head
    /// payloads exceed the generic bound (Rachel: 11,255 rows), so they admit
    /// up to the largest head vertex count while <see cref="MaxBytes" /> and the
    /// generic bound keep bounding everything else.
    /// </summary>
    internal const int MaxMorphArrayItems = 65_536;
    internal const int MaxObjectProperties = 8192;

    internal static bool TryParse(ReadOnlyMemory<byte> bytes, out JsonDocument? document, out ImmutableArray<Diagnostic> diagnostics)
    {
        var items = ImmutableArray.CreateBuilder<Diagnostic>();
        document = null;
        if (bytes.Length > MaxBytes)
        {
            items.Add(new Diagnostic("preset-size-limit", DiagnosticSeverity.Error, $"Preset exceeds the {MaxBytes} byte safety limit."));
            diagnostics = items.ToImmutable();
            return false;
        }

        try
        {
            var jsonBytes = bytes.Length >= 3 && bytes.Span[0] == 0xEF && bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF
                ? bytes[3..]
                : bytes;
            document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            ValidateShape(document.RootElement, "$", items, 0, ShapeLocation.Root);
            if (items.Any(item => item.Severity == DiagnosticSeverity.Error))
            {
                document.Dispose();
                document = null;
            }
        }
        catch (JsonException exception)
        {
            items.Add(new Diagnostic("preset-json-invalid", DiagnosticSeverity.Error, exception.Message));
        }

        diagnostics = items.ToImmutable();
        return document is not null;
    }

    internal static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    internal static void AddUnknownFields(JsonElement element, ISet<string> known, string path,
        ImmutableArray<PresetUnknownField>.Builder unknowns, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            if (known.Contains(property.Name)) continue;
            var field = new PresetUnknownField($"{path}.{property.Name}", property.Value.ValueKind.ToString());
            unknowns.Add(field);
            diagnostics.Add(new Diagnostic("preset-unknown-field", DiagnosticSeverity.Warning,
                $"Unsupported preset field '{field.Path}' was retained as a loss diagnostic."));
        }
    }

    internal static bool TryReadFloat(JsonElement element, string path, out float value,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out value) && float.IsFinite(value))
            return true;
        value = default;
        diagnostics.Add(new Diagnostic("preset-number-invalid", DiagnosticSeverity.Error, $"'{path}' must be a finite JSON number."));
        return false;
    }

    internal static bool TryReadInt(JsonElement element, string path, out int value,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)) return true;
        value = default;
        diagnostics.Add(new Diagnostic("preset-integer-invalid", DiagnosticSeverity.Error, $"'{path}' must be a JSON integer."));
        return false;
    }

    internal static bool TryReadUInt32(JsonElement element, string path, out uint value,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetUInt32(out value)) return true;
            if (element.TryGetInt64(out var signed) && signed >= int.MinValue && signed <= uint.MaxValue)
            {
                value = unchecked((uint)signed);
                return true;
            }
        }
        value = default;
        diagnostics.Add(new Diagnostic("preset-unsigned-integer-invalid", DiagnosticSeverity.Error, $"'{path}' must be a uint32-compatible JSON integer."));
        return false;
    }

    internal static string FormatFloat(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateShape(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, int depth, ShapeLocation location)
    {
        if (depth > 64)
        {
            diagnostics.Add(new Diagnostic("preset-depth-limit", DiagnosticSeverity.Error, "Preset JSON exceeds the maximum nesting depth."));
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var property in element.EnumerateObject())
            {
                if (++count > MaxObjectProperties)
                {
                    diagnostics.Add(new Diagnostic("preset-object-limit", DiagnosticSeverity.Error, $"'{path}' has too many properties."));
                    break;
                }

                if (!names.Add(property.Name))
                {
                    diagnostics.Add(new Diagnostic("preset-duplicate-key", DiagnosticSeverity.Error,
                        $"Duplicate JSON property '{path}.{property.Name}' is not accepted."));
                }
                ValidateShape(property.Value, $"{path}.{property.Name}", diagnostics, depth + 1,
                    ChildLocation(location, property.Name));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var limit = location == ShapeLocation.SculptData
                ? MaxMorphArrayItems
                : MaxArrayItems;
            var count = element.GetArrayLength();
            if (count > limit)
            {
                string alternative = limit == MaxMorphArrayItems
                    ? " Omit sculpt from the host and keep it runtime-owned when a larger payload is required."
                    : string.Empty;
                diagnostics.Add(new Diagnostic("preset-array-limit", DiagnosticSeverity.Error,
                    $"'{path}' has {count} items; at most {limit} are accepted.{alternative}"));
                return;
            }
            var index = 0;
            foreach (var item in element.EnumerateArray())
                ValidateShape(item, $"{path}[{index++}]", diagnostics, depth + 1,
                    location == ShapeLocation.SculptArray
                        ? ShapeLocation.SculptItem
                        : ShapeLocation.Other);
        }
    }

    private static ShapeLocation ChildLocation(ShapeLocation location, string property) =>
        (location, property) switch
        {
            (ShapeLocation.Root, var name) when name.Equals("morphs", StringComparison.OrdinalIgnoreCase) => ShapeLocation.Morphs,
            (ShapeLocation.Morphs, var name) when name.Equals("sculpt", StringComparison.OrdinalIgnoreCase) => ShapeLocation.SculptArray,
            (ShapeLocation.SculptItem, var name) when name.Equals("data", StringComparison.OrdinalIgnoreCase) => ShapeLocation.SculptData,
            _ => ShapeLocation.Other
        };

    private enum ShapeLocation
    {
        Other,
        Root,
        Morphs,
        SculptArray,
        SculptItem,
        SculptData
    }
}
