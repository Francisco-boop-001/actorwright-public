using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class RaceMenuJslotCodec
{
    private static readonly HashSet<string> KnownRootFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "headParts", "headTexture", "actor", "sliderMorphs", "morphs", "customMorphs", "tintInfo",
        "bodyMorphs", "overrides", "transforms", "skinOverrides", "faceTextures", "modNames", "mods", "version"
    };

    internal static PresetDocument Parse(JsonDocument document, GameEdition edition, NpcManager.Domain.Sha256Hash hash,
        ImmutableArray<Diagnostic> initialDiagnostics)
    {
        var diagnostics = initialDiagnostics.ToBuilder();
        var unknowns = ImmutableArray.CreateBuilder<PresetUnknownField>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-root-shape", DiagnosticSeverity.Error, "RaceMenu .jslot root must be a JSON object."));
            return EmptyDocument(edition, hash, diagnostics.ToImmutable());
        }
        PresetJsonSupport.AddUnknownFields(root, KnownRootFields, "$", unknowns, diagnostics);
        var presence = new PresenceBuilder();
        var headParts = ReadHeadParts(root, diagnostics, unknowns, presence);
        ReadActor(root, diagnostics, unknowns, presence, out var hairColor, out var weight);
        var raceMenu = ReadRaceMenuData(root, diagnostics, unknowns, out var nestedSliders,
            out var nestedCustomMorphs, out var orderedNestedCustomMorphs);
        var sliders = ReadFloatArray(root, "sliderMorphs", "$.sliderMorphs", diagnostics, out var sliderPresence);
        if (!sliderPresence && !nestedSliders.IsDefault)
            sliders = nestedSliders;
        var custom = ReadNamedValues(root, "customMorphs", "$.customMorphs", unknowns, diagnostics,
            out _);
        if (!nestedCustomMorphs.IsEmpty)
        {
            var customBuilder = custom.ToBuilder();
            var rootNames = custom.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var pair in orderedNestedCustomMorphs)
            {
                if (rootNames.Remove(pair.Name))
                {
                    diagnostics.Add(new Diagnostic("preset-custom-morph-duplicate", DiagnosticSeverity.Warning,
                        $"Duplicate custom morph '{pair.Name}' uses the nested RaceMenu value."));
                }
                customBuilder[pair.Name] = pair.Value;
            }
            custom = customBuilder.ToImmutable();
        }
        var body = ReadBodyMorphs(root, diagnostics, unknowns, presence, out var keyedBodyMorphs);
        var tints = ReadTints(root, diagnostics, unknowns, presence);
        if (raceMenu is null && keyedBodyMorphs.Count > 0)
            raceMenu = new RaceMenuPresetData(null, ImmutableArray<uint>.Empty, 10_000,
                ImmutableArray<RaceMenuSculptPart>.Empty, keyedBodyMorphs,
                ImmutableArray<RaceMenuBodyOverlay>.Empty, ImmutableArray<SkyrimNodeTransform>.Empty,
                ImmutableArray<SkyrimSkinOverride>.Empty);
        else if (raceMenu is not null)
            raceMenu = raceMenu with { BodyMorphsKeyed = keyedBodyMorphs };
        var appearance = new PresetAppearance(null, headParts, hairColor, weight,
            ImmutableDictionary<string, float>.Empty, body, custom, sliders, tints,
            ImmutableArray<PresetOverlay>.Empty, null, presence.Build(sliderPresence || !nestedSliders.IsDefault), unknowns.ToImmutable(),
            RaceMenu: raceMenu,
            OrderedCustomMorphs: orderedNestedCustomMorphs);
        return new PresetDocument(PresetFormat.RaceMenuJslot, edition, appearance, hash, diagnostics.ToImmutable());
    }

    internal static ImmutableArray<Diagnostic> ValidateForWrite(PresetAppearance appearance)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (appearance.Weight is { Value: var weight } && (weight is < 0 or > 100 || !float.IsFinite(weight)))
            diagnostics.Add(new Diagnostic("preset-export-weight-range", DiagnosticSeverity.Error,
                "RaceMenu actor.weight must be a finite value between 0 and 100."));
        foreach (var (name, value) in appearance.CustomMorphs)
            if (string.IsNullOrWhiteSpace(name) || !float.IsFinite(value))
                diagnostics.Add(new Diagnostic("preset-export-custom-morph-invalid", DiagnosticSeverity.Error,
                    "RaceMenu custom morph names must be non-empty and values must be finite."));
        if (!appearance.OrderedCustomMorphs.IsDefaultOrEmpty)
        {
            var orderedLastValues = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var morph in appearance.OrderedCustomMorphs)
            {
                if (string.IsNullOrWhiteSpace(morph.Name) || !float.IsFinite(morph.Value))
                {
                    diagnostics.Add(new Diagnostic("preset-export-custom-morph-order-mismatch",
                        DiagnosticSeverity.Error,
                        "Ordered RaceMenu custom morph rows require non-empty names and finite values."));
                    break;
                }
                orderedLastValues[morph.Name] = morph.Value;
            }
            if (orderedLastValues.Count != appearance.CustomMorphs.Count ||
                orderedLastValues.Any(pair =>
                    !appearance.CustomMorphs.TryGetValue(pair.Key, out var mappedValue) ||
                    BitConverter.SingleToInt32Bits(mappedValue) !=
                    BitConverter.SingleToInt32Bits(pair.Value)))
                diagnostics.Add(new Diagnostic("preset-export-custom-morph-order-incomplete",
                    DiagnosticSeverity.Error,
                    "The last value for each ordered RaceMenu custom morph must exactly match the typed name/value map."));
        }
        foreach (var value in appearance.SliderMorphs)
            if (!float.IsFinite(value)) diagnostics.Add(new Diagnostic("preset-export-slider-invalid", DiagnosticSeverity.Error,
                "RaceMenu slider morphs must be finite."));
        var raceMenu = appearance.RaceMenu;
        if (raceMenu is null) return diagnostics.ToImmutable();
        if (raceMenu.SculptDivisor is < 1 or > 1_000_000)
            diagnostics.Add(new Diagnostic("preset-export-sculpt-divisor", DiagnosticSeverity.Error,
                "RaceMenu sculpt divisor must be between 1 and 1000000."));
        foreach (var pair in raceMenu.BodyMorphsKeyed)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                diagnostics.Add(new Diagnostic("preset-export-body-morph-name", DiagnosticSeverity.Error,
                    "RaceMenu body morph names must be non-empty."));
            foreach (var value in pair.Value.Values)
                if (!float.IsFinite(value)) diagnostics.Add(new Diagnostic("preset-export-body-morph-value", DiagnosticSeverity.Error,
                    "RaceMenu body morph values must be finite."));
        }
        foreach (var overlay in raceMenu.BodyOverlays)
        {
            if (string.IsNullOrWhiteSpace(overlay.Node))
                diagnostics.Add(new Diagnostic("preset-export-overlay-node", DiagnosticSeverity.Error,
                    "RaceMenu overlay nodes must be non-empty."));
            if (overlay.Alpha is { } alpha && (alpha is < 0 or > 1 || !float.IsFinite(alpha)))
                diagnostics.Add(new Diagnostic("preset-export-overlay-alpha", DiagnosticSeverity.Error,
                    "RaceMenu overlay alpha must be a finite value between 0 and 1."));
            if (!overlay.Tint.IsDefaultOrEmpty && (overlay.Tint.Length != 4 || overlay.Tint.Any(value => value is < 0 or > 1 || !float.IsFinite(value))))
                diagnostics.Add(new Diagnostic("preset-export-overlay-tint", DiagnosticSeverity.Error,
                    "RaceMenu overlay tint must contain four unit-range finite values."));
        }
        foreach (var transform in raceMenu.NodeTransforms)
        {
            if (string.IsNullOrWhiteSpace(transform.Node))
                diagnostics.Add(new Diagnostic("preset-export-transform-node", DiagnosticSeverity.Error,
                    "RaceMenu transform nodes must be non-empty."));
            if (transform.Scale is { } scale && (!float.IsFinite(scale) || scale is < 0.01F or > 100F))
                diagnostics.Add(new Diagnostic("preset-export-transform-scale", DiagnosticSeverity.Error,
                    "RaceMenu transform scale must be finite and between 0.01 and 100."));
            if (transform.ScaleMode is { } mode && mode is < 0 or > 3)
                diagnostics.Add(new Diagnostic("preset-export-transform-scale-mode", DiagnosticSeverity.Error,
                    "RaceMenu transform scale mode must be between 0 and 3."));
            if ((!transform.Position.IsDefaultOrEmpty && transform.Position.Length != 3) ||
                transform.Position.Any(value => !float.IsFinite(value) || Math.Abs(value) > 10_000F))
                diagnostics.Add(new Diagnostic("preset-export-transform-position", DiagnosticSeverity.Error,
                    "RaceMenu transform position must contain three finite safe values."));
            if ((!transform.RotationMatrix.IsDefaultOrEmpty && transform.RotationMatrix.Length != 9) ||
                transform.RotationMatrix.Any(value => !float.IsFinite(value) || Math.Abs(value) > 1.5F))
                diagnostics.Add(new Diagnostic("preset-export-transform-rotation", DiagnosticSeverity.Error,
                    "RaceMenu transform rotation must contain nine finite safe values."));
        }
        if (raceMenu.FaceTextures.Select(item => item.Index).Distinct().Count() != raceMenu.FaceTextures.Length ||
            raceMenu.FaceTextures.Any(item => item.Index is < 0 or > 255 || !IsSafeTexturePath(item.Texture)))
            diagnostics.Add(new Diagnostic("preset-export-face-textures", DiagnosticSeverity.Error,
                "RaceMenu face textures must use unique byte indexes and safe relative texture paths."));
        if (raceMenu.ModNames.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != raceMenu.ModNames.Length)
            diagnostics.Add(new Diagnostic("preset-export-mod-names", DiagnosticSeverity.Error,
                "RaceMenu modNames must not contain duplicate plugin names."));
        if (raceMenu.Mods.Select(item => item.Index).Distinct().Count() != raceMenu.Mods.Length ||
            raceMenu.Mods.Select(item => item.Name.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != raceMenu.Mods.Length)
            diagnostics.Add(new Diagnostic("preset-export-mods", DiagnosticSeverity.Error,
                "RaceMenu mods must use unique load-order indexes and plugin names."));
        foreach (var skin in raceMenu.SkinOverrides)
        {
            foreach (var texture in skin.Textures.Values)
                if (!IsSafeTexturePath(texture))
                    diagnostics.Add(new Diagnostic("preset-export-skin-texture", DiagnosticSeverity.Error,
                        "RaceMenu skin texture paths must be safe relative DDS paths."));
            if (!skin.Tint.IsDefaultOrEmpty && (skin.Tint.Length != 4 || skin.Tint.Any(value => value is < 0 or > 1 || !float.IsFinite(value))))
                diagnostics.Add(new Diagnostic("preset-export-skin-tint", DiagnosticSeverity.Error,
                    "RaceMenu skin tint must contain four unit-range finite values."));
            if (skin.Alpha is { } alpha && (alpha is < 0 or > 1 || !float.IsFinite(alpha)))
                diagnostics.Add(new Diagnostic("preset-export-skin-alpha", DiagnosticSeverity.Error,
                    "RaceMenu skin alpha must be a finite value between 0 and 1."));
        }
        return diagnostics.ToImmutable();
    }


    private static PresetDocument EmptyDocument(GameEdition edition, NpcManager.Domain.Sha256Hash hash, ImmutableArray<Diagnostic> diagnostics) =>
        new(PresetFormat.RaceMenuJslot, edition,
            new PresetAppearance(null, ImmutableArray<PresetHeadPart>.Empty, null, null,
                ImmutableDictionary<string, float>.Empty, ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty, ImmutableArray<float>.Empty, ImmutableArray<PresetTint>.Empty,
                ImmutableArray<PresetOverlay>.Empty, null, new PresenceBuilder().Build(false), ImmutableArray<PresetUnknownField>.Empty), hash, diagnostics);

    private sealed class PresenceBuilder
    {
        internal bool HeadParts; internal bool HairColor; internal bool Weight; internal bool BodyMorphs; internal bool Tints;
        internal PresetFieldPresence Build(bool sliders) => new(false, HeadParts, HairColor, Weight, false, BodyMorphs, Tints, false, false);
    }
}
