using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class RaceMenuJslotCodec
{
    private static RaceMenuPresetData? ReadRaceMenuData(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableArray<PresetUnknownField>.Builder unknowns,
        out ImmutableArray<float> nestedSliders,
        out ImmutableDictionary<string, float> nestedCustomMorphs,
        out ImmutableArray<SkyrimRaceMenuCustomMorphValue> orderedNestedCustomMorphs)
    {
        nestedSliders = default;
        nestedCustomMorphs = ImmutableDictionary<string, float>.Empty;
        orderedNestedCustomMorphs = ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty;
        var hasData = false;
        var faceTextures = ReadFaceTextures(root, unknowns, diagnostics, out var hasFaceTextures);
        var modNames = ReadModNames(root, diagnostics, out var hasModNames);
        var mods = ReadMods(root, unknowns, diagnostics, out var hasMods);
        var version = ReadVersion(root, unknowns, diagnostics, out var hasVersion);
        hasData = hasFaceTextures || hasModNames || hasMods || hasVersion;
        string? headTexture = null;
        if (PresetJsonSupport.TryGet(root, "actor", out var actorElement) && actorElement.ValueKind == JsonValueKind.Object &&
            PresetJsonSupport.TryGet(actorElement, "headTexture", out var actorTextureElement))
        {
            hasData = true;
            headTexture = ReadHeadTexture(actorTextureElement, "$.actor.headTexture", diagnostics);
        }
        else if (PresetJsonSupport.TryGet(root, "headTexture", out var textureElement))
        {
            hasData = true;
            headTexture = ReadHeadTexture(textureElement, "$.headTexture", diagnostics);
        }

        var facePresets = ImmutableArray.CreateBuilder<uint>();
        var sculptDivisor = 10_000;
        var sculptParts = ImmutableArray<RaceMenuSculptPart>.Empty;
        var hasSculpt = false;
        if (PresetJsonSupport.TryGet(root, "morphs", out var morphs))
        {
            hasData = true;
            if (morphs.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-morphs-shape", DiagnosticSeverity.Error,
                    "RaceMenu 'morphs' must be an object."));
            }
            else
            {
                if (PresetJsonSupport.TryGet(morphs, "default", out var defaultMorphs))
                {
                    PresetJsonSupport.AddUnknownFields(defaultMorphs,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "morphs", "presets" },
                        "$.morphs.default", unknowns, diagnostics);
                    if (defaultMorphs.ValueKind != JsonValueKind.Object)
                        diagnostics.Add(new Diagnostic("preset-racemenu-default-morphs-shape", DiagnosticSeverity.Error,
                            "RaceMenu 'morphs.default' must be an object."));
                    else
                    {
                        if (PresetJsonSupport.TryGet(defaultMorphs, "morphs", out var morphValues))
                            nestedSliders = ReadFloatArrayElement(morphValues, "$.morphs.default.morphs", diagnostics);
                        if (PresetJsonSupport.TryGet(defaultMorphs, "presets", out var presetValues))
                            ReadFaceMorphPresets(presetValues, facePresets, diagnostics);
                    }
                }
                PresetJsonSupport.AddUnknownFields(morphs,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default", "custom", "sculptDivisor", "sculpt" },
                    "$.morphs", unknowns, diagnostics);
                if (PresetJsonSupport.TryGet(morphs, "custom", out var customMorphs))
                    nestedCustomMorphs = ReadNamedValuesElement(customMorphs, "$.morphs.custom", unknowns,
                        diagnostics, out orderedNestedCustomMorphs);
                if (PresetJsonSupport.TryGet(morphs, "sculptDivisor", out var divisorElement))
                {
                    var validDivisor = PresetJsonSupport.TryReadInt(divisorElement, "$.morphs.sculptDivisor", out sculptDivisor, diagnostics) &&
                        sculptDivisor is >= 1 and <= 1_000_000;
                    if (!validDivisor)
                    {
                        diagnostics.Add(new Diagnostic("preset-racemenu-sculpt-divisor", DiagnosticSeverity.Error,
                            "RaceMenu 'morphs.sculptDivisor' must be between 1 and 1000000."));
                        sculptDivisor = 10_000;
                    }
                }
                if (PresetJsonSupport.TryGet(morphs, "sculpt", out var sculptElement))
                {
                    if (sculptElement.ValueKind != JsonValueKind.Null)
                    {
                        hasSculpt = true;
                        sculptParts = ReadSculptParts(sculptElement, sculptDivisor, "$.morphs.sculpt", unknowns, diagnostics);
                    }
                }
            }
        }

        var bodyOverlays = ImmutableArray<RaceMenuBodyOverlay>.Empty;
        if (PresetJsonSupport.TryGet(root, "overrides", out var overlayElement))
        {
            hasData = true;
            bodyOverlays = ReadBodyOverlays(overlayElement, unknowns, diagnostics);
        }

        var transforms = ImmutableArray<SkyrimNodeTransform>.Empty;
        var skinOverrides = ImmutableArray<SkyrimSkinOverride>.Empty;
        if (root.TryGetProperty("transforms", out _) || root.TryGetProperty("skinOverrides", out _))
        {
            hasData = true;
            var bytes = Encoding.UTF8.GetBytes(root.GetRawText());
            if (SkyrimBodyTransformCodec.TryParseSource(bytes, out var sections, diagnostics) && sections is not null)
            {
                transforms = sections.Transforms;
                skinOverrides = sections.SkinOverrides;
            }
        }

        if (!hasData && facePresets.Count == 0 && !hasSculpt && bodyOverlays.IsEmpty &&
            transforms.IsEmpty && skinOverrides.IsEmpty)
            return null;
        return new RaceMenuPresetData(headTexture, facePresets.ToImmutable(), sculptDivisor,
            sculptParts,
            ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty,
            bodyOverlays, transforms, skinOverrides, faceTextures, modNames, mods, version);
    }

    private static string? ReadHeadTexture(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        diagnostics.Add(new Diagnostic("preset-headtexture-shape", DiagnosticSeverity.Error,
            $"'{path}' must be a portable form-identifier string."));
        return null;
    }

    private static ImmutableArray<float> ReadFloatArrayElement(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-slider-shape", DiagnosticSeverity.Error,
                $"'{path}' must be an array."));
            return ImmutableArray<float>.Empty;
        }
        var values = ImmutableArray.CreateBuilder<float>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (PresetJsonSupport.TryReadFloat(item, $"{path}[{index}]", out var value, diagnostics)) values.Add(value);
            index++;
        }
        return values.ToImmutable();
    }

    private static void ReadFaceMorphPresets(JsonElement element, ImmutableArray<uint>.Builder values,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-presets-shape", DiagnosticSeverity.Error,
                "RaceMenu 'morphs.default.presets' must be an array of unsigned integers."));
            return;
        }
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!PresetJsonSupport.TryReadUInt32(item, $"$.morphs.default.presets[{index}]", out var value, diagnostics))
                diagnostics.Add(new Diagnostic("preset-racemenu-preset-invalid", DiagnosticSeverity.Error,
                    $"RaceMenu face preset at index {index} is not an unsigned integer."));
            else values.Add(value);
            index++;
        }
    }

}
