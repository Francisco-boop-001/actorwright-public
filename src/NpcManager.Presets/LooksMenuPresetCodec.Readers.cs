using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Presets;

internal static partial class LooksMenuPresetCodec
{
    private static ImmutableDictionary<string, float> ReadFloatMap(JsonElement root, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence, bool flattenNested)
    {
        if (!PresetJsonSupport.TryGet(root, name, out var element)) return ImmutableDictionary<string, float>.Empty;
        if (name == "Morphs") presence.Morphs = true; else presence.BodyMorphs = true;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-map-shape", DiagnosticSeverity.Error, $"'{path}' must be an object."));
            return ImmutableDictionary<string, float>.Empty;
        }
        var builder = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (name == "Morphs" &&
                (string.Equals(property.Name, "Values", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(property.Name, "Presets", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(property.Name, "Regions", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(property.Name, "Intensity", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetSingle(out var value) && float.IsFinite(value))
            {
                builder[property.Name] = value;
            }
            else if (flattenNested && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in property.Value.EnumerateObject())
                {
                    if (nested.Value.ValueKind == JsonValueKind.Number && nested.Value.TryGetSingle(out var nestedValue) && float.IsFinite(nestedValue))
                        builder[$"{property.Name}.{nested.Name}"] = nestedValue;
                    else diagnostics.Add(new Diagnostic("preset-morph-invalid", DiagnosticSeverity.Warning, $"Unsupported morph value at '{path}.{property.Name}.{nested.Name}'."));
                }
            }
            else diagnostics.Add(new Diagnostic("preset-map-value-invalid", DiagnosticSeverity.Error, $"'{path}.{property.Name}' must be a finite number."));
        }
        return builder.ToImmutable();
    }

    private static Fallout4BodyMorphValues? ReadFallout4BodyMorphs(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Morphs", out var morphs) ||
            morphs.ValueKind != JsonValueKind.Object ||
            !PresetJsonSupport.TryGet(morphs, "Values", out var values)) return null;

        presence.Fallout4BodyMorphs = true;
        if (values.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-body-region-shape", DiagnosticSeverity.Error,
                "LooksMenu 'Morphs.Values' must be an array of up to five finite numbers."));
            return null;
        }
        if (values.GetArrayLength() > Fallout4BodyRegionCatalog.Ordered.Length)
        {
            diagnostics.Add(new Diagnostic("preset-body-region-count", DiagnosticSeverity.Error,
                "LooksMenu 'Morphs.Values' cannot contain more than five body-region values."));
            return null;
        }
        if (values.GetArrayLength() < Fallout4BodyRegionCatalog.Ordered.Length)
        {
            diagnostics.Add(new Diagnostic("preset-body-region-padding", DiagnosticSeverity.Warning,
                "LooksMenu 'Morphs.Values' contained fewer than five values; missing regions are zero-filled."));
        }

        var slots = new float[Fallout4BodyRegionCatalog.Ordered.Length];
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (!PresetJsonSupport.TryReadFloat(value, $"$.Morphs.Values[{index}]", out slots[index], diagnostics))
                return null;
            index++;
        }
        return new Fallout4BodyMorphValues(slots[0], slots[1], slots[2], slots[3], slots[4]);
    }

    private static ImmutableDictionary<uint, float> ReadChargenFaceMorphs(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Morphs", out var morphs) || morphs.ValueKind != JsonValueKind.Object ||
            !PresetJsonSupport.TryGet(morphs, "Presets", out var presets))
            return ImmutableDictionary<uint, float>.Empty;
        presence.ChargenFaceMorphs = true;
        if (presets.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-chargen-morphs-shape", DiagnosticSeverity.Error,
                "LooksMenu 'Morphs.Presets' must be an object keyed by hexadecimal hashes."));
            return ImmutableDictionary<uint, float>.Empty;
        }

        var result = ImmutableDictionary.CreateBuilder<uint, float>();
        foreach (var property in presets.EnumerateObject())
        {
            if (!TryParseHexUInt(property.Name, out var hash))
            {
                diagnostics.Add(new Diagnostic("preset-chargen-morph-key-invalid", DiagnosticSeverity.Error,
                    $"LooksMenu morph preset key '{property.Name}' is not hexadecimal."));
                continue;
            }
            if (!PresetJsonSupport.TryReadFloat(property.Value, $"$.Morphs.Presets.{property.Name}", out var value, diagnostics))
                continue;
            result[hash] = value;
        }
        return result.ToImmutable();
    }

    private static ImmutableDictionary<uint, ImmutableArray<float>> ReadFaceBoneRegions(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Morphs", out var morphs) || morphs.ValueKind != JsonValueKind.Object ||
            !PresetJsonSupport.TryGet(morphs, "Regions", out var regions))
            return ImmutableDictionary<uint, ImmutableArray<float>>.Empty;
        presence.FaceBoneRegions = true;
        if (regions.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-face-regions-shape", DiagnosticSeverity.Error,
                "LooksMenu 'Morphs.Regions' must be an object keyed by hexadecimal region indexes."));
            return ImmutableDictionary<uint, ImmutableArray<float>>.Empty;
        }

        var result = ImmutableDictionary.CreateBuilder<uint, ImmutableArray<float>>();
        foreach (var property in regions.EnumerateObject())
        {
            if (!TryParseHexUInt(property.Name, out var region))
            {
                diagnostics.Add(new Diagnostic("preset-face-region-key-invalid", DiagnosticSeverity.Error,
                    $"LooksMenu face-region key '{property.Name}' is not hexadecimal."));
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new Diagnostic("preset-face-region-shape", DiagnosticSeverity.Error,
                    $"'$.Morphs.Regions.{property.Name}' must be an array of finite numbers."));
                continue;
            }
            var values = ImmutableArray.CreateBuilder<float>();
            var index = 0;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (PresetJsonSupport.TryReadFloat(item, $"$.Morphs.Regions.{property.Name}[{index}]", out var value, diagnostics))
                    values.Add(value);
                index++;
            }
            result[region] = values.ToImmutable();
        }
        return result.ToImmutable();
    }

    private static float ReadFacialMorphIntensity(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Morphs", out var morphs) || morphs.ValueKind != JsonValueKind.Object ||
            !PresetJsonSupport.TryGet(morphs, "Intensity", out var intensity))
            return 1.0F;
        presence.FacialMorphIntensity = true;
        return PresetJsonSupport.TryReadFloat(intensity, "$.Morphs.Intensity", out var value, diagnostics) ? value : 1.0F;
    }

    private static bool TryParseHexUInt(string value, out uint result)
    {
        result = 0;
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return normalized.Length > 0 && uint.TryParse(normalized, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static ImmutableArray<PresetTint> ReadTints(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Tints", out var element)) return ImmutableArray<PresetTint>.Empty;
        presence.Tints = true;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-tints-shape", DiagnosticSeverity.Error, "'Tints' must be an object."));
            return ImmutableArray<PresetTint>.Empty;
        }
        var builder = ImmutableArray.CreateBuilder<PresetTint>();
        var keys = new List<string>();
        var canonicalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (TryParseHexUInt(property.Name, out var parsed) && parsed <= ushort.MaxValue)
            {
                var canonical = parsed.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                if (!canonicalKeys.TryAdd(canonical, property.Name))
                    diagnostics.Add(new Diagnostic("preset-tint-duplicate", DiagnosticSeverity.Error,
                        $"LooksMenu 'Tints' contains duplicate index {canonical}."));
            }
        }
        if (PresetJsonSupport.TryGet(root, "TintOrder", out var order))
        {
            presence.Tints = true;
            if (order.ValueKind != JsonValueKind.Array)
                diagnostics.Add(new Diagnostic("preset-tint-order-shape", DiagnosticSeverity.Error,
                    "LooksMenu 'TintOrder' must be an array of tint indexes."));
            else
            {
                var seenOrderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in order.EnumerateArray())
                {
                    string? key = null;
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        key = item.GetString();
                        if (key is not null && TryParseHexUInt(key, out var parsedKey))
                            key = parsedKey.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var numericKey))
                        key = numericKey.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                    if (key is null)
                    {
                        diagnostics.Add(new Diagnostic("preset-tint-order-invalid", DiagnosticSeverity.Error,
                            "LooksMenu 'TintOrder' entries must be strings or unsigned integers."));
                    }
                    else if (!seenOrderKeys.Add(key))
                    {
                        diagnostics.Add(new Diagnostic("preset-tint-order-duplicate", DiagnosticSeverity.Error,
                            $"LooksMenu 'TintOrder' contains duplicate tint '{key}'."));
                    }
                    else keys.Add(key);
                }
            }
        }
        var orderedCanonicalKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            var canonical = TryParseHexUInt(property.Name, out var parsed)
                ? parsed.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                : property.Name;
            if (orderedCanonicalKeys.Add(canonical)) keys.Add(property.Name);
        }

        foreach (var key in keys)
        {
            var actualKey = canonicalKeys.GetValueOrDefault(key, key);
            if (!PresetJsonSupport.TryGet(element, actualKey, out var tint))
            {
                diagnostics.Add(new Diagnostic("preset-tint-order-missing", DiagnosticSeverity.Error,
                    $"LooksMenu 'TintOrder' references missing tint '{key}'."));
                continue;
            }
            if (!TryParseHexUInt(actualKey, out var rawIndex) || rawIndex > ushort.MaxValue)
            {
                diagnostics.Add(new Diagnostic("preset-tint-index-invalid", DiagnosticSeverity.Error, $"Tint key '{actualKey}' is not a 16-bit hexadecimal index."));
                continue;
            }
            if (tint.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("preset-tint-invalid", DiagnosticSeverity.Error, $"Tint '{key}' must be an object."));
                continue;
            }

            var type = 1;
            if (PresetJsonSupport.TryGet(tint, "Type", out var typeElement))
            {
                if (!PresetJsonSupport.TryReadInt(typeElement, $"$.Tints.{actualKey}.Type", out type, diagnostics) || type is < 0 or > ushort.MaxValue)
                    diagnostics.Add(new Diagnostic("preset-tint-type-range", DiagnosticSeverity.Error, $"Tint '{actualKey}' Type must be an unsigned 16-bit integer."));
            }

            uint color = 0;
            if (type == 1)
            {
                if (!PresetJsonSupport.TryGet(tint, "Color", out var colorElement) ||
                    !PresetJsonSupport.TryReadUInt32(colorElement, $"$.Tints.{actualKey}.Color", out color, diagnostics))
                {
                    diagnostics.Add(new Diagnostic("preset-tint-invalid", DiagnosticSeverity.Error,
                        $"Palette tint '{key}' must contain an unsigned Color."));
                    continue;
                }
            }

            var percent = 100;
            if (PresetJsonSupport.TryGet(tint, "Percent", out var percentElement))
            {
                if (!PresetJsonSupport.TryReadInt(percentElement, $"$.Tints.{actualKey}.Percent", out percent, diagnostics) || percent is < 0 or > 100)
                    diagnostics.Add(new Diagnostic("preset-tint-percent-range", DiagnosticSeverity.Error, $"Tint '{actualKey}' Percent must be from 0 through 100."));
            }

            int? colorId = null;
            if (PresetJsonSupport.TryGet(tint, "ColorID", out var colorIdElement))
            {
                if (PresetJsonSupport.TryReadInt(colorIdElement, $"$.Tints.{actualKey}.ColorID", out var parsedColorId, diagnostics)) colorId = parsedColorId;
            }
            builder.Add(new PresetTint((int)rawIndex, color, string.Empty, type, percent, colorId));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<PresetOverlay> ReadOverlays(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Overlays", out var element)) return ImmutableArray<PresetOverlay>.Empty;
        presence.Overlays = true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-overlays-shape", DiagnosticSeverity.Error, "'Overlays' must be an array."));
            return ImmutableArray<PresetOverlay>.Empty;
        }
        var builder = ImmutableArray.CreateBuilder<PresetOverlay>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(item, "template", out var template) || template.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("preset-overlay-invalid", DiagnosticSeverity.Error, $"'$.Overlays[{index}]' requires a template string."));
                index++;
                continue;
            }
            var priority = 0;
            if (PresetJsonSupport.TryGet(item, "priority", out var priorityElement)) PresetJsonSupport.TryReadInt(priorityElement, $"$.Overlays[{index}].priority", out priority, diagnostics);
            builder.Add(new PresetOverlay(template.GetString()!, priority, ReadVector(item, "tint", 4, index, diagnostics),
                ReadVector(item, "offsetUV", 2, index, diagnostics), ReadVector(item, "scaleUV", 2, index, diagnostics)));
            index++;
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<float> ReadVector(JsonElement item, string name, int size, int index, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PresetJsonSupport.TryGet(item, name, out var element)) return ImmutableArray<float>.Empty;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != size)
        {
            diagnostics.Add(new Diagnostic("preset-vector-shape", DiagnosticSeverity.Error, $"'$.Overlays[{index}].{name}' must contain {size} numbers."));
            return ImmutableArray<float>.Empty;
        }
        var builder = ImmutableArray.CreateBuilder<float>(size);
        for (var i = 0; i < size; i++)
        {
            if (PresetJsonSupport.TryReadFloat(element[i], $"$.Overlays[{index}].{name}[{i}]", out var value, diagnostics)) builder.Add(value);
        }
        return builder.ToImmutable();
    }

}
