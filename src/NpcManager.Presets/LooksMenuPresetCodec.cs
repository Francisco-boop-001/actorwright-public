using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class LooksMenuPresetCodec
{
    private static readonly HashSet<string> KnownRootFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "BodyMorphs", "Gender", "HairColor", "HeadParts", "Morphs", "Overlays", "Skin", "Tints", "TintOrder", "Weight"
    };

    internal static PresetDocument Parse(JsonDocument document, GameEdition edition, NpcManager.Domain.Sha256Hash hash,
        ImmutableArray<Diagnostic> initialDiagnostics)
    {
        var diagnostics = initialDiagnostics.ToBuilder();
        var unknowns = ImmutableArray.CreateBuilder<PresetUnknownField>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-root-shape", DiagnosticSeverity.Error, "LooksMenu preset root must be a JSON object."));
            return EmptyDocument(edition, hash, diagnostics.ToImmutable());
        }

        PresetJsonSupport.AddUnknownFields(root, KnownRootFields, "$", unknowns, diagnostics);
        var presence = new PresenceBuilder();
        var gender = ReadGender(root, diagnostics, presence);
        var headParts = ReadHeadParts(root, diagnostics, presence);
        var hairColor = ReadHairColor(root, diagnostics, presence);
        var weight = ReadWeight(root, diagnostics, presence);
        var morphs = ReadFloatMap(root, "Morphs", "$.Morphs", diagnostics, presence, flattenNested: true);
        var chargenFaceMorphs = ReadChargenFaceMorphs(root, diagnostics, presence);
        var faceBoneRegions = ReadFaceBoneRegions(root, diagnostics, presence);
        var facialMorphIntensity = ReadFacialMorphIntensity(root, diagnostics, presence);
        var fallout4BodyMorphs = ReadFallout4BodyMorphs(root, diagnostics, presence);
        var bodyMorphs = ReadFloatMap(root, "BodyMorphs", "$.BodyMorphs", diagnostics, presence, flattenNested: false);
        var tints = ReadTints(root, diagnostics, presence);
        var overlays = ReadOverlays(root, diagnostics, presence);
        var skin = ReadSkin(root, diagnostics, presence);

        var appearance = new PresetAppearance(gender, headParts, hairColor, weight,
            morphs, bodyMorphs, ImmutableDictionary<string, float>.Empty, ImmutableArray<float>.Empty,
            tints, overlays, skin, presence.Build(), unknowns.ToImmutable(), fallout4BodyMorphs,
            chargenFaceMorphs, faceBoneRegions, facialMorphIntensity);
        return new PresetDocument(PresetFormat.LooksMenu, edition, appearance, hash, diagnostics.ToImmutable());
    }

    private static int? ReadGender(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Gender", out var element)) return null;
        presence.Gender = true;
        if (!PresetJsonSupport.TryReadInt(element, "$.Gender", out var value, diagnostics)) return null;
        if (value is < 0 or > byte.MaxValue)
        {
            diagnostics.Add(new Diagnostic("preset-gender-range", DiagnosticSeverity.Error,
                "LooksMenu 'Gender' must be an integer from 0 through 255."));
            return null;
        }
        return value;
    }

    private static ImmutableArray<PresetHeadPart> ReadHeadParts(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "HeadParts", out var element)) return ImmutableArray<PresetHeadPart>.Empty;
        presence.HeadParts = true;
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-headparts-shape", DiagnosticSeverity.Error, "'HeadParts' must be an array."));
            return ImmutableArray<PresetHeadPart>.Empty;
        }
        var parts = ImmutableArray.CreateBuilder<PresetHeadPart>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                diagnostics.Add(new Diagnostic("preset-headpart-invalid", DiagnosticSeverity.Error, $"'$.HeadParts[{index}]' must be a form identifier string."));
            else
            {
                try { parts.Add(new PresetHeadPart(PresetIdentifier.Parse(item.GetString()!), 0)); }
                catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("preset-headpart-invalid", DiagnosticSeverity.Error, exception.Message)); }
            }
            index++;
        }
        return parts.ToImmutable();
    }

    private static PresetHairColor? ReadHairColor(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "HairColor", out var element)) return null;
        presence.HairColor = true;
        if (element.ValueKind == JsonValueKind.String)
        {
            try { return PresetHairColor.FromIdentifier(element.GetString()!); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("preset-haircolor-invalid", DiagnosticSeverity.Error, exception.Message));
                return null;
            }
        }
        diagnostics.Add(new Diagnostic("preset-haircolor-shape", DiagnosticSeverity.Error, "LooksMenu 'HairColor' must be a form identifier string."));
        return null;
    }

    private static PresetWeight? ReadWeight(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Weight", out var element)) return null;
        presence.Weight = true;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
        {
            diagnostics.Add(new Diagnostic("preset-weight-shape", DiagnosticSeverity.Error, "LooksMenu 'Weight' must be an array of [thin, muscular, fat]."));
            return null;
        }
        var values = new float[3];
        for (var index = 0; index < values.Length; index++)
            PresetJsonSupport.TryReadFloat(element[index], $"$.Weight[{index}]", out values[index], diagnostics);
        return new PresetWeight(values[0], values[0], values[1], values[2]);
    }

    private static string? ReadSkin(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics, PresenceBuilder presence)
    {
        if (!PresetJsonSupport.TryGet(root, "Skin", out var element)) return null;
        presence.Skin = true;
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        diagnostics.Add(new Diagnostic("preset-skin-shape", DiagnosticSeverity.Error, "LooksMenu 'Skin' must be a string."));
        return null;
    }

    internal static ImmutableArray<Diagnostic> ValidateForWrite(PresetAppearance appearance)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (appearance.Gender is null)
            diagnostics.Add(new Diagnostic("preset-export-gender-required", DiagnosticSeverity.Error,
                "LooksMenu export requires a typed Gender value."));
        if (!appearance.Presence.HeadParts)
            diagnostics.Add(new Diagnostic("preset-export-headparts-required", DiagnosticSeverity.Error,
                "LooksMenu export requires a typed HeadParts section, including an explicit empty array."));
        if (appearance.Weight is null)
            diagnostics.Add(new Diagnostic("preset-export-weight-required", DiagnosticSeverity.Error,
                "LooksMenu export requires a typed three-axis Weight value."));

        var tintIndexes = new HashSet<int>();
        foreach (var tint in appearance.Tints)
        {
            if (tint.Index is < 0 or > ushort.MaxValue)
                diagnostics.Add(new Diagnostic("preset-export-tint-index-range", DiagnosticSeverity.Error,
                    $"LooksMenu tint index {tint.Index} is outside the unsigned 16-bit range."));
            if (tint.Type is < 0 or > ushort.MaxValue)
                diagnostics.Add(new Diagnostic("preset-export-tint-type-range", DiagnosticSeverity.Error,
                    $"LooksMenu tint type {tint.Type} is outside the unsigned 16-bit range."));
            if (tint.Percent is < 0 or > 100)
                diagnostics.Add(new Diagnostic("preset-export-tint-percent-range", DiagnosticSeverity.Error,
                    $"LooksMenu tint percent {tint.Percent} is outside the 0 through 100 range."));
            if (!tintIndexes.Add(tint.Index))
                diagnostics.Add(new Diagnostic("preset-export-tint-duplicate", DiagnosticSeverity.Error,
                    $"LooksMenu export contains duplicate tint index {tint.Index}."));
        }

        return diagnostics.ToImmutable();
    }

    internal static byte[] Write(PresetAppearance appearance)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            if (!appearance.BodyMorphs.IsEmpty) WriteFloatMap(writer, "BodyMorphs", appearance.BodyMorphs);
            writer.WriteNumber("Gender", appearance.Gender.GetValueOrDefault());
            if (appearance.Presence.HairColor && appearance.HairColor is { FormIdentifier: { } identifier }) writer.WriteString("HairColor", identifier.Raw);
            writer.WriteStartArray("HeadParts");
            foreach (var part in appearance.HeadParts) writer.WriteStringValue(part.Identifier.Raw);
            writer.WriteEndArray();
            if (!appearance.Morphs.IsEmpty || appearance.Fallout4BodyMorphs is not null ||
                appearance.ChargenFaceMorphs is { Count: > 0 } || appearance.FaceBoneRegions is { Count: > 0 } ||
                appearance.FacialMorphIntensity != 1.0F)
                WriteMorphs(writer, appearance);
            if (!appearance.Overlays.IsEmpty) WriteOverlays(writer, appearance.Overlays);
            if (!string.IsNullOrEmpty(appearance.Skin)) writer.WriteString("Skin", appearance.Skin);

            var emittedTints = appearance.Tints.Where(item => item.Percent > 0).ToImmutableArray();
            if (!emittedTints.IsDefaultOrEmpty)
            {
                writer.WriteStartObject("Tints");
                foreach (var tint in emittedTints.OrderBy(item => item.Index.ToString("X", System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal))
                {
                    writer.WriteStartObject(tint.Index.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                    if (tint.Type == 1)
                    {
                        writer.WriteNumber("Color", tint.Color);
                        writer.WriteNumber("ColorID", tint.ColorId ?? 0);
                    }
                    writer.WriteNumber("Percent", tint.Percent);
                    writer.WriteNumber("Type", tint.Type);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteStartArray("TintOrder");
                foreach (var tint in emittedTints) writer.WriteStringValue(tint.Index.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteEndArray();
            }
            var weight = appearance.Weight!;
            writer.WriteStartArray("Weight");
            writer.WriteNumberValue(weight.Thin ?? weight.Value);
            writer.WriteNumberValue(weight.Muscular ?? weight.Value);
            writer.WriteNumberValue(weight.Fat ?? weight.Value);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
