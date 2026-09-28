using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class SkyrimBodyTransformCodec
{
    private static ImmutableArray<SkyrimSkinOverride> ParseSkinOverrides(JsonElement element,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("sse-skin-array-shape", DiagnosticSeverity.Error,
                "RaceMenu 'skinOverrides' must be an array."));
            return [];
        }
        if (element.GetArrayLength() > MaxEntries)
            diagnostics.Add(new Diagnostic("sse-skin-entry-limit", DiagnosticSeverity.Error,
                $"RaceMenu 'skinOverrides' contains more than {MaxEntries} entries."));
        var result = ImmutableArray.CreateBuilder<SkyrimSkinOverride>();
        var seen = new HashSet<(bool FirstPerson, uint SlotMask)>();
        foreach (var (item, index) in element.EnumerateArray().Take(MaxEntries).Select((value, position) => (value, position)))
        {
            var parsed = ParseSkin(item, index, diagnostics);
            if (parsed is not null)
            {
                if (!seen.Add((parsed.FirstPerson, parsed.SlotMask)))
                    diagnostics.Add(new Diagnostic("sse-skin-duplicate", DiagnosticSeverity.Error,
                        $"$.skinOverrides[{index}] duplicates a firstPerson/slotMask identity."));
                result.Add(parsed);
            }
        }
        return result.ToImmutable();
    }

    private static SkyrimSkinOverride? ParseSkin(JsonElement item, int index,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var path = $"$.skinOverrides[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("sse-skin-item-shape", DiagnosticSeverity.Error, $"{path} must be an object."));
            return null;
        }
        CheckAllowed(item, SkinFields, path, diagnostics);
        var firstPerson = OptionalBool(item, "firstPerson", false, path, diagnostics);
        if (!ReadUInt(item, "slotMask", path, diagnostics, out var slotMask)) return null;
        if (!TryGet(item, "values", out var valueArray) || valueArray.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("sse-skin-values-shape", DiagnosticSeverity.Error, $"{path}.values must be an array."));
            return null;
        }
        if (valueArray.GetArrayLength() > MaxValues)
            diagnostics.Add(new Diagnostic("sse-skin-value-limit", DiagnosticSeverity.Error, $"{path} contains more than {MaxValues} values."));
        var values = ImmutableArray.CreateBuilder<RaceMenuValue>();
        var textures = ImmutableDictionary.CreateBuilder<int, string>();
        var seen = new HashSet<(int Key, int Index)>();
        ImmutableArray<float> tint = [];
        float? alpha = null;
        foreach (var (element, valueIndex) in valueArray.EnumerateArray().Take(MaxValues).Select((value, position) => (value, position)))
        {
            var value = ParseSkinValue(element, $"{path}.values[{valueIndex}]", diagnostics);
            if (value is null) continue;
            if (!seen.Add((value.Key, value.Index)))
                diagnostics.Add(new Diagnostic("sse-skin-value-duplicate", DiagnosticSeverity.Error,
                    $"{path}.values[{valueIndex}] duplicates key {value.Key}/index {value.Index}."));
            values.Add(value);
            if (value.Key == 9) textures[value.Index] = value.Data.StringValue!;
            if (value.Key == 7) tint = DecodeTint(value.Data.IntegerValue);
            if (value.Key == 8) alpha = (float)value.Data.NumberValue;
        }
        return new SkyrimSkinOverride(slotMask, firstPerson, values.ToImmutable(), textures.ToImmutable(), tint, alpha);
    }

    private static RaceMenuValue? ParseSkinValue(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("sse-skin-value-shape", DiagnosticSeverity.Error, $"{path} must be an object."));
            return null;
        }
        CheckAllowed(element, ValueFields, path, diagnostics);
        if (!ReadInt(element, "key", path, diagnostics, out var key) ||
            !ReadInt(element, "type", path, diagnostics, out var type) ||
            !ReadInt(element, "index", path, diagnostics, out var index) ||
            !TryGet(element, "data", out var data)) return null;
        switch (key)
        {
            case 9 when type == 2 && index is >= 0 and < MaxValues && data.ValueKind == JsonValueKind.String:
                var texture = data.GetString();
                if (texture is null || !ValidTexturePath(texture))
                    diagnostics.Add(new Diagnostic("sse-skin-texture-path", DiagnosticSeverity.Error, $"{path}.data is not a safe relative DDS path."));
                return new RaceMenuValue(key, type, index, RaceMenuScalar.FromText(texture ?? string.Empty));
            case 7 when type == 3 && index == -1 && data.TryGetInt64(out var tint):
                if (tint < int.MinValue || tint > uint.MaxValue)
                    diagnostics.Add(new Diagnostic("sse-skin-tint-shape", DiagnosticSeverity.Error, $"{path}.data is not a 32-bit ARGB value."));
                return new RaceMenuValue(key, type, index, RaceMenuScalar.FromInteger(tint));
            case 8 when type == 4 && index == -1 && TryFinite(data, out var alpha):
                if (alpha is < 0 or > 1)
                    diagnostics.Add(new Diagnostic("sse-skin-alpha", DiagnosticSeverity.Error, $"{path}.data must be between 0 and 1."));
                return new RaceMenuValue(key, type, index, RaceMenuScalar.FromNumber(alpha));
            default:
                diagnostics.Add(new Diagnostic("sse-skin-key-unsupported", DiagnosticSeverity.Error,
                    $"{path} uses unsupported skin key/type/index ({key}/{type}/{index}). Supported keys are 7, 8, and 9."));
                return null;
        }
    }

    private static ImmutableArray<float> DecodeTint(long value)
    {
        var bits = unchecked((uint)value);
        return [(bits >> 16 & 0xFF) / 255F, (bits >> 8 & 0xFF) / 255F,
            (bits & 0xFF) / 255F, (bits >> 24 & 0xFF) / 255F];
    }

    private static ImmutableDictionary<int, string> ReadPatchTextures(JsonElement item, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = ImmutableDictionary.CreateBuilder<int, string>();
        if (item.TryGetProperty("diffuse", out var diffuse)) ReadPatchTexture(diffuse, 0, path, result, diagnostics);
        if (item.TryGetProperty("normal", out var normal)) ReadPatchTexture(normal, 1, path, result, diagnostics);
        if (!item.TryGetProperty("textures", out var textures)) return result.ToImmutable();
        if (textures.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("sse-skin-textures-shape", DiagnosticSeverity.Error, $"{path}.textures must be an object."));
            return result.ToImmutable();
        }
        foreach (var property in textures.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index is < 0 or >= MaxValues ||
                property.Value.ValueKind != JsonValueKind.String || !ValidTexturePath(property.Value.GetString() ?? string.Empty))
            {
                diagnostics.Add(new Diagnostic("sse-skin-texture-index", DiagnosticSeverity.Error,
                    $"{path}.textures keys must be unique integers 0-31 with safe relative DDS paths."));
                continue;
            }
            if (result.ContainsKey(index))
                diagnostics.Add(new Diagnostic("sse-skin-texture-duplicate", DiagnosticSeverity.Error, $"{path}.textures duplicates slot {index}."));
            else result[index] = property.Value.GetString()!;
        }
        return result.ToImmutable();
    }

    private static void ReadPatchTexture(JsonElement value, int index, string path,
        ImmutableDictionary<int, string>.Builder result, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value.ValueKind != JsonValueKind.String || !ValidTexturePath(value.GetString() ?? string.Empty))
        {
            diagnostics.Add(new Diagnostic("sse-skin-texture-path", DiagnosticSeverity.Error,
                $"{path}.{(index == 0 ? "diffuse" : "normal")} must be a safe relative DDS path."));
            return;
        }
        if (!result.TryAdd(index, value.GetString()!))
            diagnostics.Add(new Diagnostic("sse-skin-texture-duplicate", DiagnosticSeverity.Error, $"{path} duplicates texture slot {index}."));
    }

}
