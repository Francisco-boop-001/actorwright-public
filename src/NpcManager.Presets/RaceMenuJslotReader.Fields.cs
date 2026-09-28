using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class RaceMenuJslotCodec
{
    private static string? ReadSafeAsset(JsonElement item, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PresetJsonSupport.TryGet(item, name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-asset-shape", DiagnosticSeverity.Error, $"'{path}.{name}' must be a string."));
            return null;
        }
        var raw = value.GetString()!;
        try { return new AssetPath(raw).Value; }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-asset-path", DiagnosticSeverity.Error, exception.Message));
            return null;
        }
    }

    private static ImmutableArray<float> ReadVector(JsonElement element, int size, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != size)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-vector-shape", DiagnosticSeverity.Error, $"'{path}' must contain {size} finite numbers."));
            return ImmutableArray<float>.Empty;
        }
        var vector = ImmutableArray.CreateBuilder<float>(size);
        for (var index = 0; index < size; index++)
            if (PresetJsonSupport.TryReadFloat(element[index], $"{path}[{index}]", out var value, diagnostics)) vector.Add(value);
        return vector.ToImmutable();
    }

    private static ImmutableArray<PresetHeadPart> ReadHeadParts(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableArray<PresetUnknownField>.Builder unknowns,
        PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "headParts", out var element)) return ImmutableArray<PresetHeadPart>.Empty;
        presence.HeadParts = true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-headparts-shape", DiagnosticSeverity.Error, "'.jslot headParts' must be an array."));
            return ImmutableArray<PresetHeadPart>.Empty;
        }
        var parts = ImmutableArray.CreateBuilder<PresetHeadPart>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("preset-headpart-invalid", DiagnosticSeverity.Error, $"'$.headParts[{index}]' must be an object.")); index++; continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "formId", "formIdentifier", "type" },
                $"$.headParts[{index}]", unknowns, diagnostics);
            var raw = string.Empty;
            if (PresetJsonSupport.TryGet(item, "formIdentifier", out var identifier) && identifier.ValueKind == JsonValueKind.String) raw = identifier.GetString()!;
            else if (PresetJsonSupport.TryGet(item, "formId", out var formId) && formId.TryGetUInt32(out var numeric)) raw = $"0x{numeric:X8}";
            if (string.IsNullOrWhiteSpace(raw))
            {
                diagnostics.Add(new Diagnostic("preset-headpart-invalid", DiagnosticSeverity.Error, $"'$.headParts[{index}]' needs formIdentifier or formId.")); index++; continue;
            }
            var type = 0;
            if (PresetJsonSupport.TryGet(item, "type", out var typeElement)) PresetJsonSupport.TryReadInt(typeElement, $"$.headParts[{index}].type", out type, diagnostics);
            parts.Add(new PresetHeadPart(PresetIdentifier.Parse(raw), type)); index++;
        }
        return parts.ToImmutable();
    }

    private static void ReadActor(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<PresetUnknownField>.Builder unknowns, PresenceBuilder presence,
        out PresetHairColor? hairColor, out PresetWeight? weight)
    {
        hairColor = null; weight = null;
        if (!PresetJsonSupport.TryGet(root, "actor", out var actor)) return;
        if (actor.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-actor-shape", DiagnosticSeverity.Error, "'.jslot actor' must be an object.")); return;
        }
        PresetJsonSupport.AddUnknownFields(actor,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hairColor", "headTexture", "weight" }, "$.actor", unknowns, diagnostics);
        if (PresetJsonSupport.TryGet(actor, "hairColor", out var hair))
        {
            presence.HairColor = true;
            if (PresetJsonSupport.TryReadUInt32(hair, "$.actor.hairColor", out var packed, diagnostics))
            {
                if (packed > 0x00FF_FFFF)
                    diagnostics.Add(new Diagnostic("preset-haircolor-range", DiagnosticSeverity.Error,
                        "RaceMenu actor.hairColor must be a packed RGB value from 0 through 0xFFFFFF."));
                else hairColor = PresetHairColor.FromPackedRgb(packed);
            }
            else diagnostics.Add(new Diagnostic("preset-haircolor-shape", DiagnosticSeverity.Error, "'.jslot actor.hairColor' must be an unsigned integer."));
        }
        if (PresetJsonSupport.TryGet(actor, "weight", out var actorWeight))
        {
            presence.Weight = true;
            if (PresetJsonSupport.TryReadFloat(actorWeight, "$.actor.weight", out var value, diagnostics))
            {
                if (value is < 0 or > 100)
                    diagnostics.Add(new Diagnostic("preset-racemenu-weight-range", DiagnosticSeverity.Error,
                        "RaceMenu actor.weight must be between 0 and 100."));
                else weight = new PresetWeight(value, null, null, null);
            }
        }
    }

    private static ImmutableArray<float> ReadFloatArray(JsonElement root, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out bool present)
    {
        present = PresetJsonSupport.TryGet(root, name, out var element);
        if (!present) return ImmutableArray<float>.Empty;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-slider-shape", DiagnosticSeverity.Error, $"'{path}' must be an array.")); return ImmutableArray<float>.Empty;
        }
        var values = ImmutableArray.CreateBuilder<float>();
        for (var index = 0; index < element.GetArrayLength(); index++)
            if (PresetJsonSupport.TryReadFloat(element[index], $"{path}[{index}]", out var value, diagnostics)) values.Add(value);
        return values.ToImmutable();
    }

    private static ImmutableDictionary<string, float> ReadNamedValues(JsonElement root, string name, string path,
        ImmutableArray<PresetUnknownField>.Builder unknowns, ImmutableArray<Diagnostic>.Builder diagnostics,
        out ImmutableArray<SkyrimRaceMenuCustomMorphValue> orderedValues)
    {
        if (!PresetJsonSupport.TryGet(root, name, out var element))
        {
            orderedValues = ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty;
            return ImmutableDictionary<string, float>.Empty;
        }
        return ReadNamedValuesElement(element, path, unknowns, diagnostics, out orderedValues);
    }

    private static ImmutableDictionary<string, float> ReadNamedValuesElement(JsonElement element, string path,
        ImmutableArray<PresetUnknownField>.Builder unknowns, ImmutableArray<Diagnostic>.Builder diagnostics,
        out ImmutableArray<SkyrimRaceMenuCustomMorphValue> orderedValues)
    {
        orderedValues = ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-custom-morph-shape", DiagnosticSeverity.Error, $"'{path}' must be an array.")); return ImmutableDictionary<string, float>.Empty;
        }
        var values = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.Ordinal);
        var ordered = new List<SkyrimRaceMenuCustomMorphValue>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(item, "name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                !PresetJsonSupport.TryGet(item, "value", out var valueElement) || !PresetJsonSupport.TryReadFloat(valueElement, $"{path}[{index}].value", out var value, diagnostics))
            {
                diagnostics.Add(new Diagnostic("preset-custom-morph-invalid", DiagnosticSeverity.Error, $"'{path}[{index}]' needs string name and numeric value.")); index++; continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "value" }, $"{path}[{index}]", unknowns, diagnostics);
            var morphName = nameElement.GetString()!;
            if (values.ContainsKey(morphName))
            {
                diagnostics.Add(new Diagnostic("preset-custom-morph-duplicate", DiagnosticSeverity.Warning,
                    $"Duplicate custom morph '{morphName}' remains in the ordered RaceMenu rows; the typed lookup map uses its last value."));
            }
            values[morphName] = value;
            ordered.Add(new SkyrimRaceMenuCustomMorphValue(morphName, value));
            index++;
        }
        orderedValues = ordered.ToImmutableArray();
        return values.ToImmutable();
    }

    private static ImmutableDictionary<string, float> ReadBodyMorphs(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableArray<PresetUnknownField>.Builder unknowns,
        PresenceBuilder presence,
        out ImmutableDictionary<string, ImmutableDictionary<string, float>> keyed)
    {
        keyed = ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty;
        if (!PresetJsonSupport.TryGet(root, "bodyMorphs", out var element)) return ImmutableDictionary<string, float>.Empty;
        presence.BodyMorphs = true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-body-morph-shape", DiagnosticSeverity.Error, "'.jslot bodyMorphs' must be an array.")); return ImmutableDictionary<string, float>.Empty;
        }
        var values = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.OrdinalIgnoreCase);
        var keyedValues = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(item, "name", out var name) || name.ValueKind != JsonValueKind.String ||
                !PresetJsonSupport.TryGet(item, "keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
            {
                diagnostics.Add(new Diagnostic("preset-body-morph-invalid", DiagnosticSeverity.Error, $"'$.bodyMorphs[{index}]' needs name and a non-empty keys array.")); index++; continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "keys" }, $"$.bodyMorphs[{index}]", unknowns, diagnostics);
            var morphName = name.GetString()!;
            if (keyedValues.ContainsKey(morphName))
                diagnostics.Add(new Diagnostic("preset-body-morph-duplicate", DiagnosticSeverity.Warning,
                    $"Duplicate body morph '{morphName}' uses the last value."));
            var keyedMorphs = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.OrdinalIgnoreCase);
            var keyIndex = 0;
            foreach (var key in keys.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(key, "key", out var keyName) || keyName.ValueKind != JsonValueKind.String ||
                    !PresetJsonSupport.TryGet(key, "value", out var value) || !PresetJsonSupport.TryReadFloat(value, $"$.bodyMorphs[{index}].keys[{keyIndex}].value", out var parsed, diagnostics))
                {
                    diagnostics.Add(new Diagnostic("preset-body-morph-key-invalid", DiagnosticSeverity.Error,
                        $"'$.bodyMorphs[{index}].keys[{keyIndex}]' needs a string key and finite value."));
                }
                else
                {
                    PresetJsonSupport.AddUnknownFields(key,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "key", "value" },
                        $"$.bodyMorphs[{index}].keys[{keyIndex}]", unknowns, diagnostics);
                    var keyText = keyName.GetString()!;
                    if (keyedMorphs.ContainsKey(keyText))
                        diagnostics.Add(new Diagnostic("preset-body-morph-key-duplicate", DiagnosticSeverity.Warning,
                            $"Duplicate body morph key '{keyText}' uses the last value."));
                    keyedMorphs[keyText] = parsed;
                }
                keyIndex++;
            }
            if (keyedMorphs.Count == 0) { index++; continue; }
            keyedValues[morphName] = keyedMorphs.ToImmutable();
            var flatValue = keyedMorphs.Values.Aggregate(0F, static (sum, value) => sum + value);
            if (!float.IsFinite(flatValue))
                diagnostics.Add(new Diagnostic("preset-body-morph-sum-invalid", DiagnosticSeverity.Error,
                    $"'$.bodyMorphs[{index}]' keyed contributions do not have a finite sum."));
            else
                values[morphName] = flatValue;
            index++;
        }
        keyed = keyedValues.ToImmutable();
        return values.ToImmutable();
    }

    private static ImmutableArray<PresetTint> ReadTints(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<PresetUnknownField>.Builder unknowns, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "tintInfo", out var element)) return ImmutableArray<PresetTint>.Empty;
        presence.Tints = true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-tint-shape", DiagnosticSeverity.Error, "'.jslot tintInfo' must be an array.")); return ImmutableArray<PresetTint>.Empty;
        }
        var values = ImmutableArray.CreateBuilder<PresetTint>(); var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(item, "color", out var color) || !PresetJsonSupport.TryReadUInt32(color, $"$.tintInfo[{index}].color", out var packed, diagnostics) ||
                !PresetJsonSupport.TryGet(item, "index", out var position) || !position.TryGetInt32(out var tintIndex))
            {
                diagnostics.Add(new Diagnostic("preset-tint-invalid", DiagnosticSeverity.Error, $"'$.tintInfo[{index}]' needs unsigned color and integer index.")); index++; continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "color", "index", "texture" },
                $"$.tintInfo[{index}]", unknowns, diagnostics);
            var texture = string.Empty;
            if (PresetJsonSupport.TryGet(item, "texture", out var textureElement) && textureElement.ValueKind == JsonValueKind.String) texture = textureElement.GetString()!;
            values.Add(new PresetTint(tintIndex, packed, texture)); index++;
        }
        return values.ToImmutable();
    }
}
