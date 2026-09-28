using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class SkyrimBodyTransformCodec
{
    private static ImmutableArray<SkyrimNodeTransform> ParseTransforms(JsonElement element,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("sse-transform-array-shape", DiagnosticSeverity.Error,
                "RaceMenu 'transforms' must be an array."));
            return [];
        }
        if (element.GetArrayLength() > MaxEntries)
            diagnostics.Add(new Diagnostic("sse-transform-entry-limit", DiagnosticSeverity.Error,
                $"RaceMenu 'transforms' contains more than {MaxEntries} entries."));
        var result = ImmutableArray.CreateBuilder<SkyrimNodeTransform>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, index) in element.EnumerateArray().Take(MaxEntries).Select((value, position) => (value, position)))
        {
            var parsed = ParseTransform(item, index, diagnostics);
            if (parsed is not null)
            {
                var identity = $"{(parsed.FirstPerson ? 1 : 0)}\0{parsed.Node}";
                if (!seen.Add(identity))
                    diagnostics.Add(new Diagnostic("sse-transform-node-duplicate", DiagnosticSeverity.Error,
                        $"$.transforms[{index}] duplicates node '{parsed.Node}' for the same first-person state."));
                result.Add(parsed);
            }
        }
        return result.ToImmutable();
    }

    private static SkyrimNodeTransform? ParseTransform(JsonElement item, int index,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var path = $"$.transforms[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("sse-transform-item-shape", DiagnosticSeverity.Error, $"{path} must be an object."));
            return null;
        }
        CheckAllowed(item, TransformFields, path, diagnostics);
        var node = RequiredString(item, "node", path, diagnostics, MaxNodeLength);
        var firstPerson = OptionalBool(item, "firstPerson", false, path, diagnostics);
        if (!TryGet(item, "keys", out var keys))
        {
            diagnostics.Add(new Diagnostic("sse-transform-keys-required", DiagnosticSeverity.Error, $"{path}.keys is required."));
            return null;
        }
        if (keys.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("sse-transform-keys-shape", DiagnosticSeverity.Error, $"{path}.keys must be an array."));
            return null;
        }
        var keySets = ImmutableArray.CreateBuilder<RaceMenuTransformKeySet>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyElement, keyIndex) in keys.EnumerateArray().Select((value, position) => (value, position)))
        {
            var keyPath = $"{path}.keys[{keyIndex}]";
            if (keyElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("sse-transform-key-shape", DiagnosticSeverity.Error, $"{keyPath} must be an object."));
                continue;
            }
            CheckAllowed(keyElement, KeyFields, keyPath, diagnostics);
            var name = RequiredString(keyElement, "name", keyPath, diagnostics, 64);
            if (name is not null && !seenNames.Add(name))
                diagnostics.Add(new Diagnostic("sse-transform-key-name-duplicate", DiagnosticSeverity.Error,
                    $"{keyPath}.name '{name}' appears more than once in the same transform."));
            if (!TryGet(keyElement, "values", out var valueArray) || valueArray.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new Diagnostic("sse-transform-values-shape", DiagnosticSeverity.Error, $"{keyPath}.values must be an array."));
                continue;
            }
            var values = ImmutableArray.CreateBuilder<RaceMenuValue>();
            var seen = new HashSet<(int Key, int Index)>();
            foreach (var (valueElement, valueIndex) in valueArray.EnumerateArray().Select((value, position) => (value, position)))
            {
                if (values.Count >= MaxValues)
                {
                    diagnostics.Add(new Diagnostic("sse-transform-value-limit", DiagnosticSeverity.Error,
                        $"{path} contains more than {MaxValues} values."));
                    break;
                }
                var parsed = ParseTransformValue(valueElement, $"{keyPath}.values[{valueIndex}]", diagnostics);
                if (parsed is null) continue;
                if (!seen.Add((parsed.Key, parsed.Index)))
                    diagnostics.Add(new Diagnostic("sse-transform-value-duplicate", DiagnosticSeverity.Error,
                        $"{keyPath}.values[{valueIndex}] duplicates key {parsed.Key}/index {parsed.Index}."));
                values.Add(parsed);
            }
            var keyValues = values.ToImmutable();
            ValidateTransformShape(keyValues, keyPath, diagnostics);
            if (name is not null)
                keySets.Add(new RaceMenuTransformKeySet(name, keyValues));
        }

        var all = keySets.ToImmutable();
        if (node is null || all.IsDefaultOrEmpty || all.All(item => item.Values.IsDefaultOrEmpty)) return null;
        var interpreted = all.FirstOrDefault(item =>
                string.Equals(item.Name, "RSMTransform", StringComparison.Ordinal))?.Values
            ?? (all.Length == 1 ? all[0].Values : ImmutableArray<RaceMenuValue>.Empty);
        return new SkyrimNodeTransform(node, firstPerson, all, NumberByKey(interpreted, 30),
            IntegerByKey(interpreted, 33), Vector(interpreted, 31, 3), Vector(interpreted, 32, 9));
    }

    private static RaceMenuValue? ParseTransformValue(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("sse-transform-value-shape", DiagnosticSeverity.Error, $"{path} must be an object."));
            return null;
        }
        CheckAllowed(element, ValueFields, path, diagnostics);
        if (!ReadInt(element, "key", path, diagnostics, out var key) ||
            !ReadInt(element, "type", path, diagnostics, out var type) ||
            !ReadInt(element, "index", path, diagnostics, out var index) ||
            !TryGet(element, "data", out var data)) return null;
        if (key is not (30 or 31 or 32 or 33))
        {
            diagnostics.Add(new Diagnostic("sse-transform-key-unsupported", DiagnosticSeverity.Error,
                $"{path}.key {key} is not a supported RaceMenu transform key (30-33)."));
            return null;
        }
        if (key == 33)
        {
            if (type != 3 || index < 0 || index >= MaxValues || !data.TryGetInt64(out var mode))
                diagnostics.Add(new Diagnostic("sse-transform-scale-mode-shape", DiagnosticSeverity.Error,
                    $"{path} scale-mode must be type 3, a non-negative bounded index, and an integer."));
            else if (mode is < 0 or > 3)
                diagnostics.Add(new Diagnostic("sse-transform-scale-mode", DiagnosticSeverity.Error, $"{path} scale-mode is outside 0-3."));
            return new RaceMenuValue(key, type, index, data.TryGetInt64(out mode) ? RaceMenuScalar.FromInteger(mode) : RaceMenuScalar.Empty());
        }
        if (type != 4 || !TryFinite(data, out var number))
        {
            diagnostics.Add(new Diagnostic("sse-transform-number-shape", DiagnosticSeverity.Error,
                $"{path} must use type 4 and a finite numeric data value."));
            return null;
        }
        var maxIndex = key switch { 30 or 33 => MaxValues - 1, 31 => 2, _ => 8 };
        if (index < 0 || index > maxIndex)
            diagnostics.Add(new Diagnostic("sse-transform-index", DiagnosticSeverity.Error,
                $"{path}.index is outside the supported range for key {key}."));
        if (key == 31 && Math.Abs(number) > 10000)
            diagnostics.Add(new Diagnostic("sse-transform-position-range", DiagnosticSeverity.Error, $"{path} position exceeds the safe range."));
        if (key == 32 && Math.Abs(number) > 1.5)
            diagnostics.Add(new Diagnostic("sse-transform-rotation-range", DiagnosticSeverity.Error, $"{path} rotation matrix value exceeds the safe range."));
        if (key == 30 && (number < MinScale || number > MaxScale))
            diagnostics.Add(new Diagnostic("sse-transform-scale", DiagnosticSeverity.Error, $"{path} scale is outside the safe range."));
        return new RaceMenuValue(key, type, index, RaceMenuScalar.FromNumber(number));
    }

    private static void ValidateTransformShape(ImmutableArray<RaceMenuValue> values, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateComplete(values, 31, 3, path, diagnostics);
        ValidateComplete(values, 32, 9, path, diagnostics);
        var rotation = Vector(values, 32, 9);
        if (!rotation.IsDefaultOrEmpty && !IsRotationMatrix(rotation))
            diagnostics.Add(new Diagnostic("sse-transform-rotation", DiagnosticSeverity.Error,
                $"{path} rotation values do not form a finite right-handed matrix."));
    }

    private static void ValidateComplete(ImmutableArray<RaceMenuValue> values, int key, int count,
        string path, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var indexes = values.Where(item => item.Key == key).Select(item => item.Index).Distinct().OrderBy(item => item).ToArray();
        if (indexes.Length == 0) return;
        if (indexes.Length != count || indexes.Select((value, index) => value == index).Any(item => !item))
            diagnostics.Add(new Diagnostic("sse-transform-component-set", DiagnosticSeverity.Error,
                $"{path} key {key} must contain exactly indexes 0 through {count - 1}."));
    }

}
