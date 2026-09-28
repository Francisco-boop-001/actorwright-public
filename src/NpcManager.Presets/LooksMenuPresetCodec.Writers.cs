using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Presets;

internal static partial class LooksMenuPresetCodec
{
    private static void WriteFloatMap(Utf8JsonWriter writer, string name, ImmutableDictionary<string, float> values)
    {
        writer.WriteStartObject(name);
        foreach (var pair in values.OrderBy(item => item.Key, StringComparer.Ordinal)) writer.WriteNumber(pair.Key, pair.Value);
        writer.WriteEndObject();
    }

    private static void WriteMorphs(Utf8JsonWriter writer, PresetAppearance appearance)
    {
        writer.WriteStartObject("Morphs");
        if (appearance.FacialMorphIntensity != 1.0F)
            writer.WriteNumber("Intensity", appearance.FacialMorphIntensity);
        if (appearance.ChargenFaceMorphs is { Count: > 0 })
        {
            writer.WriteStartObject("Presets");
            foreach (var pair in appearance.ChargenFaceMorphs!.OrderBy(item => item.Key.ToString("X", System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal))
                writer.WriteNumber(pair.Key.ToString("X", System.Globalization.CultureInfo.InvariantCulture), pair.Value);
            writer.WriteEndObject();
        }
        if (appearance.FaceBoneRegions is { Count: > 0 })
        {
            writer.WriteStartObject("Regions");
            foreach (var pair in appearance.FaceBoneRegions!.OrderBy(item => item.Key.ToString("X", System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal))
            {
                writer.WriteStartArray(pair.Key.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                for (var index = 0; index < 8; index++)
                    writer.WriteNumberValue(index < pair.Value.Length ? pair.Value[index] : 0.0F);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        foreach (var pair in appearance.Morphs.OrderBy(item => item.Key, StringComparer.Ordinal))
            writer.WriteNumber(pair.Key, pair.Value);
        if (appearance.Fallout4BodyMorphs is not null)
        {
            var values = appearance.Fallout4BodyMorphs ?? Fallout4BodyMorphValues.Zero;
            writer.WriteStartArray("Values");
            foreach (var region in Fallout4BodyRegionCatalog.Ordered) writer.WriteNumberValue(values.Get(region));
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteOverlays(Utf8JsonWriter writer, ImmutableArray<PresetOverlay> overlays)
    {
        writer.WriteStartArray("Overlays");
        foreach (var overlay in overlays)
        {
            writer.WriteStartObject();
            WriteVector(writer, "offsetUV", overlay.OffsetUv);
            writer.WriteNumber("priority", overlay.Priority);
            WriteVector(writer, "scaleUV", overlay.ScaleUv);
            writer.WriteString("template", overlay.Template);
            WriteVector(writer, "tint", overlay.Tint);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteVector(Utf8JsonWriter writer, string name, ImmutableArray<float> values)
    {
        if (values.IsDefaultOrEmpty) return;
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }
}
