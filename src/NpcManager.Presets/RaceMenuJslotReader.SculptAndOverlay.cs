using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class RaceMenuJslotCodec
{
    private static ImmutableArray<RaceMenuSculptPart> ReadSculptParts(JsonElement element, int divisor, string pathRoot,
        ImmutableArray<PresetUnknownField>.Builder unknowns, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-shape", DiagnosticSeverity.Error,
                "RaceMenu sculpt must be an array of shape blocks."));
            return ImmutableArray<RaceMenuSculptPart>.Empty;
        }
        var parts = ImmutableArray.CreateBuilder<RaceMenuSculptPart>();
        var partIndex = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"{pathRoot}[{partIndex}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-invalid", DiagnosticSeverity.Error, $"'{path}' must be an object."));
                partIndex++;
                continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "host", "vertices", "data" },
                path, unknowns, diagnostics);
            var host = string.Empty;
            if (PresetJsonSupport.TryGet(item, "host", out var hostElement))
            {
                if (hostElement.ValueKind == JsonValueKind.String) host = hostElement.GetString()!;
                else diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-host", DiagnosticSeverity.Error, $"'{path}.host' must be a string."));
            }
            long vertexCount = 0;
            var hasVertexCount = PresetJsonSupport.TryGet(item, "vertices", out var countElement);
            if (hasVertexCount)
            {
                if (!countElement.TryGetInt64(out vertexCount) || vertexCount < 0)
                {
                    diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-vertices", DiagnosticSeverity.Error,
                        $"'{path}.vertices' must be a non-negative integer."));
                    vertexCount = 0;
                }
            }
            var vertices = ImmutableArray.CreateBuilder<RaceMenuSculptVertex>();
            var hasData = PresetJsonSupport.TryGet(item, "data", out var dataElement);
            if (hasData)
            {
                if (dataElement.ValueKind != JsonValueKind.Array)
                    diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-data", DiagnosticSeverity.Error, $"'{path}.data' must be an array."));
                else
                {
                    var rowIndex = 0;
                    var seen = new HashSet<int>();
                    foreach (var row in dataElement.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4 ||
                            !PresetJsonSupport.TryReadInt(row[0], $"{path}.data[{rowIndex}][0]", out var index, diagnostics) ||
                            !PresetJsonSupport.TryReadInt(row[1], $"{path}.data[{rowIndex}][1]", out var dx, diagnostics) ||
                            !PresetJsonSupport.TryReadInt(row[2], $"{path}.data[{rowIndex}][2]", out var dy, diagnostics) ||
                            !PresetJsonSupport.TryReadInt(row[3], $"{path}.data[{rowIndex}][3]", out var dz, diagnostics) ||
                            index < 0 || !seen.Add(index))
                        {
                            diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-row", DiagnosticSeverity.Error,
                                $"'{path}.data[{rowIndex}]' must contain a unique index and three integer deltas."));
                        }
                        else
                        {
                            if (vertexCount > 0 && index >= vertexCount)
                                diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-index", DiagnosticSeverity.Error,
                                    $"'{path}.data[{rowIndex}]' exceeds the declared vertex count."));
                            vertices.Add(new RaceMenuSculptVertex(index, dx / (float)divisor, dy / (float)divisor, dz / (float)divisor));
                        }
                        rowIndex++;
                    }
                }
            }
            parts.Add(new RaceMenuSculptPart(host, vertexCount, vertices.ToImmutable(), hasVertexCount, hasData));
            partIndex++;
        }
        return parts.ToImmutable();
    }

    private static ImmutableArray<RaceMenuBodyOverlay> ReadBodyOverlays(JsonElement element,
        ImmutableArray<PresetUnknownField>.Builder unknowns, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-overrides-shape", DiagnosticSeverity.Error,
                "RaceMenu 'overrides' must be an array."));
            return ImmutableArray<RaceMenuBodyOverlay>.Empty;
        }
        var overlays = ImmutableArray.CreateBuilder<RaceMenuBodyOverlay>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(item, "node", out var node) || node.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-invalid", DiagnosticSeverity.Error,
                    $"'$.overrides[{index}]' requires a node string."));
                index++;
                continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node", "values", "diffuse", "normal", "tint", "alpha" },
                $"$.overrides[{index}]", unknowns, diagnostics);
            var nodeName = node.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nodeName) || nodeName.Length > 256 || nodeName.Contains('\0'))
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-node", DiagnosticSeverity.Error,
                    $"'$.overrides[{index}].node' must be a non-empty node name of at most 256 characters."));
            string? diffuse;
            string? normal;
            ImmutableArray<float> tint;
            float? alpha;
            ImmutableArray<RaceMenuValue> values;
            if (PresetJsonSupport.TryGet(item, "values", out var valuesElement))
            {
                values = ReadOverlayValues(valuesElement, $"$.overrides[{index}].values", unknowns, diagnostics);
                DecodeOverlayValues(values, $"$.overrides[{index}]", diagnostics, out diffuse, out normal, out tint, out alpha);
            }
            else
            {
                values = ImmutableArray<RaceMenuValue>.Empty;
                diffuse = ReadSafeAsset(item, "diffuse", $"$.overrides[{index}]", diagnostics);
                normal = ReadSafeAsset(item, "normal", $"$.overrides[{index}]", diagnostics);
                tint = ImmutableArray<float>.Empty;
                if (PresetJsonSupport.TryGet(item, "tint", out var tintElement))
                {
                    tint = ReadVector(tintElement, 4, $"$.overrides[{index}].tint", diagnostics);
                    if (!tint.IsDefaultOrEmpty && tint.Any(value => value is < 0 or > 1))
                        diagnostics.Add(new Diagnostic("preset-racemenu-overlay-tint", DiagnosticSeverity.Error,
                            $"'$.overrides[{index}].tint' values must be between 0 and 1."));
                }
                alpha = null;
                if (PresetJsonSupport.TryGet(item, "alpha", out var alphaElement) && PresetJsonSupport.TryReadFloat(alphaElement, $"$.overrides[{index}].alpha", out var parsedAlpha, diagnostics))
                {
                    if (parsedAlpha is < 0 or > 1)
                        diagnostics.Add(new Diagnostic("preset-racemenu-overlay-alpha", DiagnosticSeverity.Error,
                            $"'$.overrides[{index}].alpha' must be between 0 and 1."));
                    alpha = parsedAlpha;
                }
            }
            overlays.Add(new RaceMenuBodyOverlay(nodeName, diffuse, normal, tint, alpha, values));
            index++;
        }
        return overlays.ToImmutable();
    }

    private static ImmutableArray<RaceMenuValue> ReadOverlayValues(JsonElement element, string path,
        ImmutableArray<PresetUnknownField>.Builder unknowns, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-overlay-values-shape", DiagnosticSeverity.Error,
                $"'{path}' must be an array."));
            return ImmutableArray<RaceMenuValue>.Empty;
        }
        var values = ImmutableArray.CreateBuilder<RaceMenuValue>();
        var seen = new HashSet<(int Key, int Index)>();
        var admitsLegacyInactiveRoot = DeclaresAlphaZero(element);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            if (item.ValueKind != JsonValueKind.Object ||
                !PresetJsonSupport.TryGet(item, "key", out var keyElement) || !PresetJsonSupport.TryReadInt(keyElement, $"{itemPath}.key", out var key, diagnostics) ||
                !PresetJsonSupport.TryGet(item, "type", out var typeElement) || !PresetJsonSupport.TryReadInt(typeElement, $"{itemPath}.type", out var type, diagnostics) ||
                !PresetJsonSupport.TryGet(item, "index", out var indexElement) || !PresetJsonSupport.TryReadInt(indexElement, $"{itemPath}.index", out var valueIndex, diagnostics) ||
                !PresetJsonSupport.TryGet(item, "data", out var dataElement) || !TryReadRaceMenuScalar(dataElement, $"{itemPath}.data", out var data, diagnostics))
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-value-invalid", DiagnosticSeverity.Error,
                    $"'{itemPath}' must contain integer key/type/index and a scalar data value."));
                index++;
                continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "key", "type", "index", "data" }, itemPath, unknowns, diagnostics);
            if (!seen.Add((key, valueIndex)))
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-value-duplicate", DiagnosticSeverity.Error,
                    $"'{itemPath}' duplicates key {key}/index {valueIndex}."));
            if (key == 9 && type == 2 && valueIndex >= 0 && data.Kind == RaceMenuScalarKind.Text)
            {
                try
                {
                    var raw = data.StringValue ?? string.Empty;
                    if (admitsLegacyInactiveRoot &&
                        raw.Length > 1 &&
                        raw[0] is '\\' or '/' &&
                        raw[1] is not ('\\' or '/'))
                    {
                        _ = new AssetPath(raw[1..]);
                    }
                    else
                    {
                        raw = new AssetPath(raw).Value;
                    }
                    data = RaceMenuScalar.FromText(raw);
                }
                catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("preset-racemenu-overlay-asset-path", DiagnosticSeverity.Error,
                    $"Overlay texture '{data.StringValue}' must be a relative asset path; one leading separator is accepted only for an inactive legacy row. {exception.Message}")); }
            }
            else if (key == 9 && type == 2 && valueIndex >= 0)
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-texture-shape", DiagnosticSeverity.Error,
                    $"'{itemPath}' texture values must contain string data."));
            else if (key == 7 && (type != 3 || valueIndex != -1 || data.Kind != RaceMenuScalarKind.SignedInteger))
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-tint-shape", DiagnosticSeverity.Error,
                    $"'{itemPath}' tint values must use key 7/type 3/index -1 and a signed integer."));
            else if (key == 8 && (type != 4 || valueIndex != -1 ||
                     data.Kind is not (RaceMenuScalarKind.FloatingPoint or RaceMenuScalarKind.SignedInteger)))
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-alpha-shape", DiagnosticSeverity.Error,
                    $"'{itemPath}' alpha values must use key 8/type 4/index -1 and a finite number."));
            values.Add(new RaceMenuValue(key, type, valueIndex, data));
            index++;
        }
        return values.ToImmutable();
    }

    private static bool DeclaresAlphaZero(JsonElement values)
    {
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !PresetJsonSupport.TryGet(item, "key", out var keyElement) ||
                !keyElement.TryGetInt32(out var key) ||
                key != 8 ||
                !PresetJsonSupport.TryGet(item, "type", out var typeElement) ||
                !typeElement.TryGetInt32(out var type) ||
                type != 4 ||
                !PresetJsonSupport.TryGet(item, "index", out var indexElement) ||
                !indexElement.TryGetInt32(out var valueIndex) ||
                valueIndex != -1 ||
                !PresetJsonSupport.TryGet(item, "data", out var dataElement))
            {
                continue;
            }

            if (dataElement.TryGetInt64(out var integer)) return integer == 0;
            if (dataElement.TryGetDouble(out var number)) return number == 0D;
        }

        return false;
    }

    private static bool TryReadRaceMenuScalar(JsonElement element, string path, out RaceMenuScalar scalar,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var integer):
                scalar = RaceMenuScalar.FromInteger(integer);
                return true;
            case JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number):
                scalar = RaceMenuScalar.FromNumber(number);
                return true;
            case JsonValueKind.String:
                scalar = RaceMenuScalar.FromText(element.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                scalar = new RaceMenuScalar(RaceMenuScalarKind.Boolean, BooleanValue: element.GetBoolean());
                return true;
            case JsonValueKind.Null:
                scalar = RaceMenuScalar.Empty();
                return true;
            default:
                diagnostics.Add(new Diagnostic("preset-racemenu-overlay-scalar", DiagnosticSeverity.Error,
                    $"'{path}' must be a finite number, string, boolean, or null."));
                scalar = RaceMenuScalar.Empty();
                return false;
        }
    }

    private static void DecodeOverlayValues(ImmutableArray<RaceMenuValue> values, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out string? diffuse, out string? normal,
        out ImmutableArray<float> tint, out float? alpha)
    {
        diffuse = null;
        normal = null;
        tint = ImmutableArray<float>.Empty;
        alpha = null;
        foreach (var value in values)
        {
            if (value.Key == 9 && value.Type == 2 && value.Data.Kind == RaceMenuScalarKind.Text && value.Index is 0 or 1)
            {
                if (value.Index == 0) diffuse = value.Data.StringValue;
                else normal = value.Data.StringValue;
            }
            else if (value.Key == 7 && value.Type == 3 && value.Index == -1 && value.Data.Kind == RaceMenuScalarKind.SignedInteger)
            {
                var packed = unchecked((uint)value.Data.IntegerValue);
                tint = [(packed >> 16 & 0xFF) / 255F, (packed >> 8 & 0xFF) / 255F,
                    (packed & 0xFF) / 255F, (packed >> 24 & 0xFF) / 255F];
                if (value.Data.IntegerValue is < int.MinValue or > uint.MaxValue)
                    diagnostics.Add(new Diagnostic("preset-racemenu-overlay-tint-range", DiagnosticSeverity.Error,
                        $"'{path}.values' tint is not a 32-bit ARGB value."));
            }
            else if (value.Key == 8 && value.Type == 4 && value.Index == -1 &&
                     value.Data.Kind is RaceMenuScalarKind.FloatingPoint or RaceMenuScalarKind.SignedInteger)
            {
                alpha = value.Data.Kind == RaceMenuScalarKind.FloatingPoint
                    ? (float)value.Data.NumberValue
                    : value.Data.IntegerValue;
                if (alpha is < 0 or > 1)
                    diagnostics.Add(new Diagnostic("preset-racemenu-overlay-alpha", DiagnosticSeverity.Error,
                        $"'{path}.values' alpha must be between 0 and 1."));
            }
        }
    }

}
