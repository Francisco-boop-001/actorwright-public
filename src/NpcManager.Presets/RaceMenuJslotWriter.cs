using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class RaceMenuJslotCodec
{
    internal static byte[] Write(PresetAppearance appearance)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            WriteVersion(writer, appearance.RaceMenu?.Version);
            WriteFaceTextures(writer, appearance.RaceMenu?.FaceTextures ?? []);
            WriteModNames(writer, appearance.RaceMenu?.ModNames ?? []);
            WriteMods(writer, appearance.RaceMenu?.Mods ?? []);
            writer.WriteStartObject("actor");
            if (appearance.HairColor is { PackedRgb: { } packed }) writer.WriteNumber("hairColor", packed);
            if (!string.IsNullOrWhiteSpace(appearance.RaceMenu?.HeadTexture)) writer.WriteString("headTexture", appearance.RaceMenu.HeadTexture);
            if (appearance.Weight is { } weight) writer.WriteNumber("weight", weight.Value);
            writer.WriteEndObject();

            writer.WriteStartArray("headParts");
            foreach (var part in appearance.HeadParts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("formId", part.Identifier.FormId?.Value ?? 0);
                writer.WriteString("formIdentifier", part.Identifier.Raw);
                writer.WriteNumber("type", part.Type);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("tintInfo");
            foreach (var tint in appearance.Tints.OrderBy(item => item.Index))
            {
                writer.WriteStartObject();
                writer.WriteNumber("color", tint.Color);
                writer.WriteNumber("index", tint.Index);
                writer.WriteString("texture", tint.Texture);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            WriteMorphs(writer, appearance);
            WriteBodyMorphs(writer, appearance);
            WriteBodyOverlays(writer, appearance.RaceMenu?.BodyOverlays ?? ImmutableArray<RaceMenuBodyOverlay>.Empty);
            WriteTransforms(writer, appearance.RaceMenu?.NodeTransforms ?? ImmutableArray<SkyrimNodeTransform>.Empty);
            WriteSkinOverrides(writer, appearance.RaceMenu?.SkinOverrides ?? ImmutableArray<SkyrimSkinOverride>.Empty);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteVersion(Utf8JsonWriter writer, RaceMenuVersion? version)
    {
        if (version is null) return;
        writer.WriteStartObject("version");
        writer.WriteNumber("formatVersion", version.FormatVersion);
        writer.WriteNumber("runtimeVersion", version.RuntimeVersion);
        writer.WriteNumber("signature", version.Signature);
        writer.WriteNumber("skseVersion", version.SkseVersion);
        writer.WriteEndObject();
    }

    private static void WriteFaceTextures(Utf8JsonWriter writer, ImmutableArray<RaceMenuFaceTexture> textures)
    {
        if (textures.IsDefaultOrEmpty) return;
        writer.WriteStartArray("faceTextures");
        foreach (var texture in textures.OrderBy(item => item.Index))
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", texture.Index);
            writer.WriteString("texture", texture.Texture);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteModNames(Utf8JsonWriter writer, ImmutableArray<PluginName> names)
    {
        if (names.IsDefaultOrEmpty) return;
        writer.WriteStartArray("modNames");
        foreach (var name in names) writer.WriteStringValue(name.Value);
        writer.WriteEndArray();
    }

    private static void WriteMods(Utf8JsonWriter writer, ImmutableArray<RaceMenuModEntry> mods)
    {
        if (mods.IsDefaultOrEmpty) return;
        writer.WriteStartArray("mods");
        foreach (var mod in mods.OrderBy(item => item.Index))
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", mod.Index);
            writer.WriteString("name", mod.Name.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteMorphs(Utf8JsonWriter writer, PresetAppearance appearance)
    {
        writer.WriteStartObject("morphs");
        writer.WriteStartObject("default");
        writer.WriteStartArray("morphs");
        foreach (var value in appearance.SliderMorphs) writer.WriteNumberValue(value);
        writer.WriteEndArray();
        if (appearance.RaceMenu is { FaceMorphPresets.IsDefaultOrEmpty: false })
        {
            writer.WriteStartArray("presets");
            foreach (var value in appearance.RaceMenu.FaceMorphPresets) writer.WriteNumberValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WriteStartArray("custom");
        IEnumerable<SkyrimRaceMenuCustomMorphValue> customMorphs =
            appearance.OrderedCustomMorphs.IsDefaultOrEmpty
                ? appearance.CustomMorphs.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new SkyrimRaceMenuCustomMorphValue(item.Key, item.Value))
                : appearance.OrderedCustomMorphs;
        foreach (var morph in customMorphs)
        {
            writer.WriteStartObject(); writer.WriteString("name", morph.Name); writer.WriteNumber("value", morph.Value); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("sculptDivisor", appearance.RaceMenu?.SculptDivisor ?? 10_000);
        writer.WriteStartArray("sculpt");
        if (appearance.RaceMenu is { SculptParts.IsDefaultOrEmpty: false })
        {
            foreach (var part in appearance.RaceMenu.SculptParts)
            {
                writer.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(part.Host)) writer.WriteString("host", part.Host);
                if (part.HasVertexCount || part.VertexCount > 0) writer.WriteNumber("vertices", part.VertexCount);
                if (part.HasData || !part.Vertices.IsDefaultOrEmpty)
                {
                    writer.WriteStartArray("data");
                    foreach (var vertex in part.Vertices)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(vertex.Index);
                        writer.WriteNumberValue(checked((int)Math.Round(vertex.Dx * (appearance.RaceMenu?.SculptDivisor ?? 10_000))));
                        writer.WriteNumberValue(checked((int)Math.Round(vertex.Dy * (appearance.RaceMenu?.SculptDivisor ?? 10_000))));
                        writer.WriteNumberValue(checked((int)Math.Round(vertex.Dz * (appearance.RaceMenu?.SculptDivisor ?? 10_000))));
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBodyMorphs(Utf8JsonWriter writer, PresetAppearance appearance)
    {
        var keyed = appearance.RaceMenu?.BodyMorphsKeyed;
        if (keyed is null || keyed.Count == 0)
        {
            if (appearance.BodyMorphs.IsEmpty) return;
            keyed = appearance.BodyMorphs.ToImmutableDictionary(
                pair => pair.Key,
                pair => ImmutableDictionary<string, float>.Empty.Add("NPCManager", pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }
        writer.WriteStartArray("bodyMorphs");
        foreach (var pair in keyed.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStartObject(); writer.WriteString("name", pair.Key); writer.WriteStartArray("keys");
            foreach (var key in pair.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject(); writer.WriteString("key", key.Key); writer.WriteNumber("value", key.Value); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBodyOverlays(Utf8JsonWriter writer, ImmutableArray<RaceMenuBodyOverlay> overlays)
    {
        if (overlays.IsDefaultOrEmpty) return;
        writer.WriteStartArray("overrides");
        foreach (var overlay in overlays.OrderBy(item => item.Node, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStartObject(); writer.WriteString("node", overlay.Node); writer.WriteStartArray("values");
            foreach (var value in BuildOverlayValues(overlay)) WriteRaceMenuValue(writer, value);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTransforms(Utf8JsonWriter writer, ImmutableArray<SkyrimNodeTransform> transforms)
    {
        if (transforms.IsDefaultOrEmpty) return;
        writer.WriteStartArray("transforms");
        foreach (var transform in transforms.OrderBy(item => item.Node, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.FirstPerson))
        {
            writer.WriteStartObject(); writer.WriteBoolean("firstPerson", transform.FirstPerson); writer.WriteString("node", transform.Node);
            writer.WriteStartArray("keys");
            foreach (var keySet in transform.KeySets)
            {
                writer.WriteStartObject(); writer.WriteString("name", keySet.Name); writer.WriteStartArray("values");
                foreach (var value in keySet.Values) WriteRaceMenuValue(writer, value);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSkinOverrides(Utf8JsonWriter writer, ImmutableArray<SkyrimSkinOverride> overrides)
    {
        if (overrides.IsDefaultOrEmpty) return;
        writer.WriteStartArray("skinOverrides");
        foreach (var skin in overrides.OrderBy(item => item.FirstPerson).ThenBy(item => item.SlotMask))
        {
            writer.WriteStartObject(); writer.WriteBoolean("firstPerson", skin.FirstPerson); writer.WriteNumber("slotMask", skin.SlotMask); writer.WriteStartArray("values");
            foreach (var value in BuildSkinValues(skin)) WriteRaceMenuValue(writer, value);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static ImmutableArray<RaceMenuValue> BuildOverlayValues(RaceMenuBodyOverlay overlay)
    {
        var values = overlay.Values.IsDefaultOrEmpty
            ? new List<RaceMenuValue>()
            : overlay.Values.ToList();
        ReplaceValue(values, 9, 2, 0, overlay.Diffuse is null ? null : RaceMenuScalar.FromText(overlay.Diffuse));
        ReplaceValue(values, 9, 2, 1, overlay.Normal is null ? null : RaceMenuScalar.FromText(overlay.Normal));
        if (overlay.Tint.Length == 4)
        {
            var packedAlpha = ClampByte(overlay.Tint[3]);
            var red = ClampByte(overlay.Tint[0]);
            var green = ClampByte(overlay.Tint[1]);
            var blue = ClampByte(overlay.Tint[2]);
            var packed = (packedAlpha << 24) | (red << 16) | (green << 8) | blue;
            ReplaceValue(values, 7, 3, -1, RaceMenuScalar.FromInteger(unchecked((int)packed)));
        }
        else ReplaceValue(values, 7, 3, -1, null);
        ReplaceUnitScalar(values, 8, 4, -1, overlay.Alpha);
        return values.ToImmutableArray();
    }

    private static ImmutableArray<RaceMenuValue> BuildSkinValues(SkyrimSkinOverride skin)
    {
        var values = skin.Values.IsDefaultOrEmpty ? new List<RaceMenuValue>() : skin.Values.ToList();
        foreach (var texture in skin.Textures.OrderBy(item => item.Key))
            ReplaceValue(values, 9, 2, texture.Key, RaceMenuScalar.FromText(texture.Value));
        if (skin.Tint.Length == 4)
        {
            var packed = (ClampByte(skin.Tint[3]) << 24) | (ClampByte(skin.Tint[0]) << 16) |
                (ClampByte(skin.Tint[1]) << 8) | ClampByte(skin.Tint[2]);
            ReplaceValue(values, 7, 3, -1, RaceMenuScalar.FromInteger(unchecked((int)packed)));
        }
        else ReplaceValue(values, 7, 3, -1, null);
        ReplaceUnitScalar(values, 8, 4, -1, skin.Alpha);
        return values.ToImmutableArray();
    }

    private static void ReplaceVector(List<RaceMenuValue> values, int key, int type,
        ImmutableArray<float> vector)
    {
        if (vector.IsDefaultOrEmpty) return;
        for (var index = 0; index < vector.Length; index++)
            ReplaceValue(values, key, type, index, RaceMenuScalar.FromNumber(vector[index]));
    }

    private static void ReplaceFirstKey(List<RaceMenuValue> values, int key, int type, RaceMenuScalar? data)
    {
        var existing = values.FindIndex(value => value.Key == key);
        if (existing >= 0)
        {
            if (data is null) values.RemoveAt(existing);
            else values[existing] = new RaceMenuValue(key, type, values[existing].Index, data);
        }
        else if (data is not null)
            values.Add(new RaceMenuValue(key, type, 0, data));
    }

    private static uint ClampByte(float value) => (uint)Math.Clamp((int)Math.Round(value * 255F), 0, 255);

    private static bool IsSafeTexturePath(string value)
    {
        try { _ = new AssetPath(value); return value.Length <= 260; }
        catch (ArgumentException) { return false; }
    }

    private static void ReplaceValue(List<RaceMenuValue> values, int key, int type, int index, RaceMenuScalar? data)
    {
        var found = false;
        for (var i = values.Count - 1; i >= 0; i--)
        {
            if (values[i].Key != key || values[i].Index != index) continue;
            if (data is null) values.RemoveAt(i);
            else if (!found) { values[i] = new RaceMenuValue(key, type, index, data); found = true; }
            else values.RemoveAt(i);
        }
        if (!found && data is not null) values.Add(new RaceMenuValue(key, type, index, data));
    }

    private static void ReplaceUnitScalar(List<RaceMenuValue> values, int key, int type, int index, float? value)
    {
        if (value is null)
        {
            ReplaceValue(values, key, type, index, null);
            return;
        }

        var matches = values.Where(item => item.Key == key && item.Index == index).ToArray();
        var scalar = matches.Length == 1 && IsEquivalent(matches[0].Data, value.Value)
            ? matches[0].Data
            : RaceMenuScalar.FromNumber(value.Value);
        ReplaceValue(values, key, type, index, scalar);
    }

    private static bool IsEquivalent(RaceMenuScalar scalar, float value) => scalar.Kind switch
    {
        RaceMenuScalarKind.FloatingPoint => (float)scalar.NumberValue == value,
        RaceMenuScalarKind.SignedInteger => (float)scalar.IntegerValue == value,
        _ => false
    };

    private static void WriteRaceMenuValue(Utf8JsonWriter writer, RaceMenuValue value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("key", value.Key);
        writer.WriteNumber("type", value.Type);
        writer.WriteNumber("index", value.Index);
        writer.WritePropertyName("data");
        switch (value.Data.Kind)
        {
            case RaceMenuScalarKind.SignedInteger: writer.WriteNumberValue(value.Data.IntegerValue); break;
            case RaceMenuScalarKind.FloatingPoint: writer.WriteNumberValue(value.Data.NumberValue); break;
            case RaceMenuScalarKind.Text: writer.WriteStringValue(value.Data.StringValue); break;
            case RaceMenuScalarKind.Boolean: writer.WriteBooleanValue(value.Data.BooleanValue); break;
            default: writer.WriteNullValue(); break;
        }
        writer.WriteEndObject();
    }

}
