using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class SkyrimBodyTransformCodec
{
    private static readonly ImmutableHashSet<string> TransformPatchFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "node", "firstPerson", "scale", "scaleMode", "position", "rotation");
    private static readonly ImmutableHashSet<string> SkinPatchFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "slotMask", "firstPerson", "textures", "diffuse", "normal", "tint", "alpha");

    private static bool TryParseJson(ReadOnlySpan<byte> bytes, string description, out JsonDocument? document,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        document = null;
        if (bytes.Length > MaxBytes)
        {
            diagnostics.Add(new Diagnostic("sse-transform-patch-size-limit", DiagnosticSeverity.Error,
                $"{description} exceeds the {MaxBytes} byte safety limit."));
            return false;
        }
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            CheckDuplicateProperties(document.RootElement, "$", diagnostics);
            return true;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("sse-transform-patch-json", DiagnosticSeverity.Error,
                $"{description} JSON is invalid: {exception.Message}"));
            return false;
        }
    }

    private static void CheckAllowed(JsonElement element, ImmutableHashSet<string> allowed, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                diagnostics.Add(new Diagnostic("sse-transform-unknown-field", DiagnosticSeverity.Error,
                    $"{path}.{property.Name} is not part of the supported RaceMenu contract."));
        }
    }

    private static void CheckDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    diagnostics.Add(new Diagnostic("sse-transform-duplicate-field", DiagnosticSeverity.Error,
                        $"{path} contains duplicate field '{property.Name}'."));
                CheckDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var (item, index) in element.EnumerateArray().Select((value, position) => (value, position)))
                CheckDuplicateProperties(item, $"{path}[{index}]", diagnostics);
        }
    }

    private static void CheckCaseVariants(JsonElement root, string expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var property in root.EnumerateObject())
            if (!string.Equals(property.Name, expected, StringComparison.Ordinal) &&
                string.Equals(property.Name, expected, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("sse-transform-field-casing", DiagnosticSeverity.Error,
                    $"Root field '{property.Name}' must use exact casing '{expected}'."));
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value) => element.TryGetProperty(name, out value);

    private static string? RequiredString(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, int maxLength)
    {
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maxLength || value.GetString()!.Contains('\0'))
        {
            diagnostics.Add(new Diagnostic("sse-transform-string", DiagnosticSeverity.Error,
                $"{path}.{name} must be a non-empty string of at most {maxLength} characters."));
            return null;
        }
        return value.GetString()!.Trim();
    }

    private static bool OptionalBool(JsonElement element, string name, bool fallback, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(element, name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
        {
            diagnostics.Add(new Diagnostic("sse-transform-bool", DiagnosticSeverity.Error, $"{path}.{name} must be boolean."));
            return fallback;
        }
        return value.GetBoolean();
    }

    private static float? OptionalFloat(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (!TryFinite(value, out var parsed))
        {
            diagnostics.Add(new Diagnostic("sse-transform-float", DiagnosticSeverity.Error, $"{path}.{name} must be finite."));
            return null;
        }
        return (float)parsed;
    }

    private static int? OptionalInt(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(element, name, out var value)) return null;
        if (!value.TryGetInt32(out var parsed))
        {
            diagnostics.Add(new Diagnostic("sse-transform-int", DiagnosticSeverity.Error, $"{path}.{name} must be a 32-bit integer."));
            return null;
        }
        return parsed;
    }

    private static ImmutableArray<float> OptionalVector(JsonElement element, string name, int length, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, float absoluteLimit)
    {
        if (!TryGet(element, name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
        {
            diagnostics.Add(new Diagnostic("sse-transform-vector-shape", DiagnosticSeverity.Error,
                $"{path}.{name} must contain exactly {length} numbers."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<float>(length);
        foreach (var number in value.EnumerateArray())
        {
            if (!TryFinite(number, out var parsed) || Math.Abs(parsed) > absoluteLimit)
                diagnostics.Add(new Diagnostic("sse-transform-vector-value", DiagnosticSeverity.Error,
                    $"{path}.{name} contains a non-finite or unsafe value."));
            else result.Add((float)parsed);
        }
        return result.Count == length ? result.ToImmutable() : [];
    }

    private static uint RequiredUInt(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(element, name, out var value) || !value.TryGetUInt32(out var parsed))
        {
            diagnostics.Add(new Diagnostic("sse-skin-slot-mask", DiagnosticSeverity.Error, $"{path}.{name} must be an unsigned 32-bit integer."));
            return 0;
        }
        return parsed;
    }

    private static bool ReadInt(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out int value)
    {
        if (TryGet(element, name, out var child) && child.TryGetInt32(out value)) return true;
        value = 0;
        diagnostics.Add(new Diagnostic("sse-transform-int", DiagnosticSeverity.Error, $"{path}.{name} must be a 32-bit integer."));
        return false;
    }

    private static bool ReadUInt(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out uint value)
    {
        if (TryGet(element, name, out var child) && child.TryGetUInt32(out value)) return true;
        value = 0;
        diagnostics.Add(new Diagnostic("sse-skin-slot-mask", DiagnosticSeverity.Error, $"{path}.{name} must be an unsigned 32-bit integer."));
        return false;
    }

    private static bool TryFinite(JsonElement value, out double parsed)
    {
        parsed = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed) && double.IsFinite(parsed);
    }

    private static bool ValidTexturePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxTexturePathLength || Path.IsPathRooted(path) ||
            path.Contains('\0') || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\')) return false;
        var normalized = path.Replace('/', '\\');
        if (normalized.Split('\\').Any(part => part is "." or "..")) return false;
        return normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private static float? NumberByKey(ImmutableArray<RaceMenuValue> values, int key)
    {
        var value = values.FirstOrDefault(item => item.Key == key);
        return value?.Data.Kind == RaceMenuScalarKind.FloatingPoint && double.IsFinite(value.Data.NumberValue)
            ? (float)value.Data.NumberValue : null;
    }

    private static int? IntegerByKey(ImmutableArray<RaceMenuValue> values, int key)
    {
        var value = values.FirstOrDefault(item => item.Key == key);
        return value?.Data.Kind == RaceMenuScalarKind.SignedInteger && value.Data.IntegerValue is >= int.MinValue and <= int.MaxValue
            ? (int)value.Data.IntegerValue : null;
    }

    private static ImmutableArray<float> Vector(ImmutableArray<RaceMenuValue> values, int key, int count)
    {
        var selected = values.Where(value => value.Key == key).OrderBy(value => value.Index).ToArray();
        return selected.Length == count && selected.Select((value, index) => value.Index == index).All(item => item)
            ? selected.Select(value => (float)value.Data.NumberValue).ToImmutableArray() : [];
    }

    private static bool IsRotationMatrix(ImmutableArray<float> values)
    {
        if (values.Length != 9 || values.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1.5F)) return false;
        var r0 = (values[0], values[1], values[2]);
        var r1 = (values[3], values[4], values[5]);
        var r2 = (values[6], values[7], values[8]);
        static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        var determinant = r0.Item1 * (r1.Item2 * r2.Item3 - r1.Item3 * r2.Item2) -
            r0.Item2 * (r1.Item1 * r2.Item3 - r1.Item3 * r2.Item1) +
            r0.Item3 * (r1.Item1 * r2.Item2 - r1.Item2 * r2.Item1);
        return Math.Abs(Dot(r0, r0) - 1) <= 0.05F && Math.Abs(Dot(r1, r1) - 1) <= 0.05F &&
            Math.Abs(Dot(r2, r2) - 1) <= 0.05F && Math.Abs(Dot(r0, r1)) <= 0.05F &&
            Math.Abs(Dot(r0, r2)) <= 0.05F && Math.Abs(Dot(r1, r2)) <= 0.05F && determinant > 0.8F;
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
