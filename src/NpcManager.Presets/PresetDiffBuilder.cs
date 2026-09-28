using System.Collections.Immutable;
using System.Globalization;
using NpcManager.Application;

namespace NpcManager.Presets;

/// <summary>Builds stable, field-level differences for the immutable preset model.</summary>
internal static class PresetDiffBuilder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static ImmutableArray<PresetDifference> Build(PresetAppearance left, PresetAppearance right)
    {
        var differences = ImmutableArray.CreateBuilder<PresetDifference>();
        Add(differences, "presence.gender", Flag(left.Presence.Gender), Flag(right.Presence.Gender));
        Add(differences, "presence.headParts", Flag(left.Presence.HeadParts), Flag(right.Presence.HeadParts));
        Add(differences, "presence.hairColor", Flag(left.Presence.HairColor), Flag(right.Presence.HairColor));
        Add(differences, "presence.weight", Flag(left.Presence.Weight), Flag(right.Presence.Weight));
        Add(differences, "presence.morphs", Flag(left.Presence.Morphs), Flag(right.Presence.Morphs));
        Add(differences, "presence.bodyMorphs", Flag(left.Presence.BodyMorphs), Flag(right.Presence.BodyMorphs));
        Add(differences, "presence.tints", Flag(left.Presence.Tints), Flag(right.Presence.Tints));
        Add(differences, "presence.overlays", Flag(left.Presence.Overlays), Flag(right.Presence.Overlays));
        Add(differences, "presence.skin", Flag(left.Presence.Skin), Flag(right.Presence.Skin));
        Add(differences, "presence.fallout4BodyMorphs", Flag(left.Presence.Fallout4BodyMorphs), Flag(right.Presence.Fallout4BodyMorphs));
        Add(differences, "presence.chargenFaceMorphs", Flag(left.Presence.ChargenFaceMorphs), Flag(right.Presence.ChargenFaceMorphs));
        Add(differences, "presence.faceBoneRegions", Flag(left.Presence.FaceBoneRegions), Flag(right.Presence.FaceBoneRegions));
        Add(differences, "presence.facialMorphIntensity", Flag(left.Presence.FacialMorphIntensity), Flag(right.Presence.FacialMorphIntensity));

        Add(differences, "gender", left.Gender?.ToString(Invariant), right.Gender?.ToString(Invariant));
        AddIndexed(differences, "headParts", left.HeadParts, right.HeadParts, FormatHeadPart);
        Add(differences, "hairColor", FormatHair(left.HairColor), FormatHair(right.HairColor));
        Add(differences, "weight", FormatWeight(left.Weight), FormatWeight(right.Weight));
        Add(differences, "skin", left.Skin, right.Skin);
        AddMap(differences, "morphs", left.Morphs, right.Morphs);
        AddMap(differences, "bodyMorphs", left.BodyMorphs, right.BodyMorphs);
        AddMap(differences, "customMorphs", left.CustomMorphs, right.CustomMorphs);
        AddFloatArray(differences, "sliderMorphs", left.SliderMorphs, right.SliderMorphs);
        Add(differences, "morphs.Values", FormatBodyRegions(left.Fallout4BodyMorphs), FormatBodyRegions(right.Fallout4BodyMorphs));
        AddMap(differences, "morphs.Presets", left.ChargenFaceMorphs ?? ImmutableDictionary<uint, float>.Empty,
            right.ChargenFaceMorphs ?? ImmutableDictionary<uint, float>.Empty);
        AddRegionMap(differences, "morphs.Regions", left.FaceBoneRegions ?? ImmutableDictionary<uint, ImmutableArray<float>>.Empty,
            right.FaceBoneRegions ?? ImmutableDictionary<uint, ImmutableArray<float>>.Empty);
        Add(differences, "morphs.Intensity", Number(left.FacialMorphIntensity), Number(right.FacialMorphIntensity));
        AddIndexed(differences, "tints", left.Tints.OrderBy(item => item.Index).ThenBy(item => item.Type).ToImmutableArray(),
            right.Tints.OrderBy(item => item.Index).ThenBy(item => item.Type).ToImmutableArray(), FormatTint);
        AddIndexed(differences, "overlays", left.Overlays.OrderBy(item => item.Priority).ThenBy(item => item.Template, StringComparer.Ordinal).ToImmutableArray(),
            right.Overlays.OrderBy(item => item.Priority).ThenBy(item => item.Template, StringComparer.Ordinal).ToImmutableArray(), FormatOverlay);

        AddRaceMenu(differences, left.RaceMenu, right.RaceMenu);
        return differences.ToImmutable();
    }

    internal static ImmutableArray<Diagnostic> BuildDiagnostics(PresetAppearance appearance, string side)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var unknown in appearance.UnknownFields.OrderBy(item => item.Path, StringComparer.Ordinal))
            diagnostics.Add(new Diagnostic("preset-diff-unsupported-field", DiagnosticSeverity.Warning,
                $"{side} preset contains unsupported field '{unknown.Path}' ({unknown.JsonKind}); it is not compared as typed data."));

        foreach (var (path, identifier) in EnumerateIdentifiers(appearance))
        {
            if (identifier.Plugin is not null && identifier.FormId is not null) continue;
            diagnostics.Add(new Diagnostic("preset-diff-unresolved-identifier", DiagnosticSeverity.Warning,
                $"{side} preset identifier at '{path}' cannot be resolved without a valid Plugin|FormID value and load-order context."));
        }

        return diagnostics.ToImmutable();
    }

    private static IEnumerable<(string Path, PresetIdentifier Identifier)> EnumerateIdentifiers(PresetAppearance appearance)
    {
        for (var index = 0; index < appearance.HeadParts.Length; index++)
            yield return ($"headParts[{index}].identifier", appearance.HeadParts[index].Identifier);
        if (appearance.HairColor?.FormIdentifier is { } hair)
            yield return ("hairColor", hair);
    }

    private static void AddRaceMenu(ImmutableArray<PresetDifference>.Builder differences,
        RaceMenuPresetData? left, RaceMenuPresetData? right)
    {
        Add(differences, "raceMenu", left is null ? null : "present", right is null ? null : "present");
        if (left is null || right is null) return;

        Add(differences, "raceMenu.headTexture", left.HeadTexture, right.HeadTexture);
        Add(differences, "raceMenu.sculptDivisor", left.SculptDivisor.ToString(Invariant), right.SculptDivisor.ToString(Invariant));
        AddUIntArray(differences, "raceMenu.faceMorphPresets", left.FaceMorphPresets, right.FaceMorphPresets);
        AddMapOfMaps(differences, "raceMenu.bodyMorphsKeyed", left.BodyMorphsKeyed, right.BodyMorphsKeyed);
        AddIndexed(differences, "raceMenu.sculptParts", left.SculptParts.OrderBy(item => item.Host, StringComparer.Ordinal).ToImmutableArray(),
            right.SculptParts.OrderBy(item => item.Host, StringComparer.Ordinal).ToImmutableArray(), FormatSculptPart);
        AddIndexed(differences, "raceMenu.bodyOverlays", left.BodyOverlays.OrderBy(item => item.Node, StringComparer.Ordinal).ToImmutableArray(),
            right.BodyOverlays.OrderBy(item => item.Node, StringComparer.Ordinal).ToImmutableArray(), FormatBodyOverlay);
        AddIndexed(differences, "raceMenu.nodeTransforms", left.NodeTransforms.OrderBy(item => item.Node, StringComparer.Ordinal).ThenBy(item => item.FirstPerson).ToImmutableArray(),
            right.NodeTransforms.OrderBy(item => item.Node, StringComparer.Ordinal).ThenBy(item => item.FirstPerson).ToImmutableArray(), FormatTransform);
        AddIndexed(differences, "raceMenu.skinOverrides", left.SkinOverrides.OrderBy(item => item.SlotMask).ThenBy(item => item.FirstPerson).ToImmutableArray(),
            right.SkinOverrides.OrderBy(item => item.SlotMask).ThenBy(item => item.FirstPerson).ToImmutableArray(), FormatSkinOverride);
        AddIndexed(differences, "raceMenu.faceTextures", left.FaceTextures.OrderBy(item => item.Index).ToImmutableArray(),
            right.FaceTextures.OrderBy(item => item.Index).ToImmutableArray(), item => $"{item.Index}:{TextValue(item.Texture)}");
        AddIndexed(differences, "raceMenu.modNames", left.ModNames, right.ModNames, item => item.Value);
        AddIndexed(differences, "raceMenu.mods", left.Mods.OrderBy(item => item.Index).ToImmutableArray(),
            right.Mods.OrderBy(item => item.Index).ToImmutableArray(), item => $"{item.Index}:{item.Name.Value}");
        Add(differences, "raceMenu.version", FormatVersion(left.Version), FormatVersion(right.Version));
    }

    private static void AddMap(ImmutableArray<PresetDifference>.Builder differences, string prefix,
        ImmutableDictionary<string, float> left, ImmutableDictionary<string, float> right)
    {
        foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            var leftValue = left.TryGetValue(key, out var leftNumber) ? Number(leftNumber) : null;
            var rightValue = right.TryGetValue(key, out var rightNumber) ? Number(rightNumber) : null;
            Add(differences, $"{prefix}.{key}", leftValue, rightValue);
        }
    }

    private static void AddMap(ImmutableArray<PresetDifference>.Builder differences, string prefix,
        ImmutableDictionary<uint, float> left, ImmutableDictionary<uint, float> right)
    {
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(item => item))
        {
            var leftValue = left.TryGetValue(key, out var leftNumber) ? Number(leftNumber) : null;
            var rightValue = right.TryGetValue(key, out var rightNumber) ? Number(rightNumber) : null;
            Add(differences, $"{prefix}.{key:X8}", leftValue, rightValue);
        }
    }

    private static void AddMapOfMaps(ImmutableArray<PresetDifference>.Builder differences, string prefix,
        ImmutableDictionary<string, ImmutableDictionary<string, float>> left,
        ImmutableDictionary<string, ImmutableDictionary<string, float>> right)
    {
        foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
        {
            var leftMap = left.TryGetValue(key, out var leftValues) ? leftValues : ImmutableDictionary<string, float>.Empty;
            var rightMap = right.TryGetValue(key, out var rightValues) ? rightValues : ImmutableDictionary<string, float>.Empty;
            AddMap(differences, $"{prefix}.{key}", leftMap, rightMap);
            if (!left.ContainsKey(key) || !right.ContainsKey(key))
                Add(differences, $"{prefix}.{key}.__presence", left.ContainsKey(key) ? "present" : null, right.ContainsKey(key) ? "present" : null);
        }
    }

    private static void AddRegionMap(ImmutableArray<PresetDifference>.Builder differences, string prefix,
        ImmutableDictionary<uint, ImmutableArray<float>> left, ImmutableDictionary<uint, ImmutableArray<float>> right)
    {
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(item => item))
        {
            var leftValue = left.TryGetValue(key, out var leftRegion) ? FormatFloatArray(leftRegion) : null;
            var rightValue = right.TryGetValue(key, out var rightRegion) ? FormatFloatArray(rightRegion) : null;
            Add(differences, $"{prefix}.{key:X8}", leftValue, rightValue);
        }
    }

    private static void AddFloatArray(ImmutableArray<PresetDifference>.Builder differences, string path,
        ImmutableArray<float> left, ImmutableArray<float> right) =>
        AddIndexed(differences, path, left, right, Number);

    private static void AddUIntArray(ImmutableArray<PresetDifference>.Builder differences, string path,
        ImmutableArray<uint> left, ImmutableArray<uint> right) =>
        AddIndexed(differences, path, left, right, value => value.ToString(Invariant));

    private static void AddIndexed<T>(ImmutableArray<PresetDifference>.Builder differences, string prefix,
        ImmutableArray<T> left, ImmutableArray<T> right, Func<T, string> formatter)
    {
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
            Add(differences, $"{prefix}[{index}]", index < left.Length ? formatter(left[index]) : null,
                index < right.Length ? formatter(right[index]) : null);
    }

    private static void Add(ImmutableArray<PresetDifference>.Builder differences, string path, string? left, string? right)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal)) differences.Add(new PresetDifference(path, left, right, "changed"));
    }

    private static string? FormatHair(PresetHairColor? hair) => hair is null ? null :
        hair.FormIdentifier is { } identifier ? $"identifier:{identifier.Raw}" : $"rgb:0x{hair.PackedRgb.GetValueOrDefault():X8}";

    private static string? FormatWeight(PresetWeight? weight) => weight is null ? null :
        $"value={Number(weight.Value)};thin={OptionalNumber(weight.Thin)};muscular={OptionalNumber(weight.Muscular)};fat={OptionalNumber(weight.Fat)}";

    private static string FormatHeadPart(PresetHeadPart headPart) => $"{headPart.Identifier.Raw};type={headPart.Type}";

    private static string FormatTint(PresetTint tint) =>
        $"index={tint.Index};type={tint.Type};color=0x{tint.Color:X8};texture={tint.Texture};percent={tint.Percent};colorId={tint.ColorId?.ToString(Invariant) ?? "null"}";

    private static string FormatOverlay(PresetOverlay overlay) =>
        $"template={overlay.Template};priority={overlay.Priority};tint={FormatFloatArray(overlay.Tint)};offset={FormatFloatArray(overlay.OffsetUv)};scale={FormatFloatArray(overlay.ScaleUv)}";

    private static string? FormatBodyRegions(Fallout4BodyMorphValues? values) => values is null ? null :
        string.Join(",", Fallout4BodyRegionCatalog.Ordered.Select(region => Number(values.Get(region))));

    private static string FormatSculptPart(RaceMenuSculptPart part) =>
        $"host={part.Host};count={part.VertexCount};hasCount={part.HasVertexCount};hasData={part.HasData};vertices=" +
        string.Join(";", part.Vertices.OrderBy(vertex => vertex.Index).Select(vertex =>
            $"{vertex.Index}:{Number(vertex.Dx)},{Number(vertex.Dy)},{Number(vertex.Dz)}"));

    private static string FormatBodyOverlay(RaceMenuBodyOverlay overlay) =>
        $"node={overlay.Node};diffuse={TextValue(overlay.Diffuse)};normal={TextValue(overlay.Normal)};tint={FormatFloatArray(overlay.Tint)};alpha={OptionalNumber(overlay.Alpha)};values={FormatValues(overlay.Values)}";

    private static string FormatTransform(SkyrimNodeTransform transform) =>
        $"node={transform.Node};firstPerson={transform.FirstPerson};scale={OptionalNumber(transform.Scale)};scaleMode={transform.ScaleMode?.ToString(Invariant) ?? "null"};position={FormatFloatArray(transform.Position)};rotation={FormatFloatArray(transform.RotationMatrix)};keySets={string.Join("|", transform.KeySets.Select(item => $"{TextValue(item.Name)}:{FormatValues(item.Values)}"))}";

    private static string FormatSkinOverride(SkyrimSkinOverride skin) =>
        $"slotMask=0x{skin.SlotMask:X8};firstPerson={skin.FirstPerson};textures={string.Join(",", skin.Textures.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{TextValue(pair.Value)}"))};tint={FormatFloatArray(skin.Tint)};alpha={OptionalNumber(skin.Alpha)};values={FormatValues(skin.Values)}";

    private static string? FormatVersion(RaceMenuVersion? version) => version is null ? null :
        $"{version.FormatVersion}/{version.RuntimeVersion}/{version.Signature}/{version.SkseVersion}";

    private static string FormatValues(ImmutableArray<RaceMenuValue> values) => values.IsDefaultOrEmpty ? string.Empty :
        string.Join(";", values.OrderBy(value => value.Key).ThenBy(value => value.Type).ThenBy(value => value.Index)
            .Select(value => $"{value.Key}/{value.Type}/{value.Index}={FormatScalar(value.Data)}"));

    private static string FormatScalar(RaceMenuScalar scalar) => scalar.Kind switch
    {
        RaceMenuScalarKind.SignedInteger => $"integer:{scalar.IntegerValue.ToString(Invariant)}",
        RaceMenuScalarKind.FloatingPoint => $"number:{scalar.NumberValue.ToString("R", Invariant)}",
        RaceMenuScalarKind.Text => $"text:{TextValue(scalar.StringValue)}",
        RaceMenuScalarKind.Boolean => scalar.BooleanValue ? "boolean:true" : "boolean:false",
        RaceMenuScalarKind.Null => "null",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar.Kind, "Unsupported RaceMenu scalar kind.")
    };

    private static string FormatFloatArray(ImmutableArray<float> values) => values.IsDefaultOrEmpty ? string.Empty :
        string.Join(",", values.Select(Number));

    private static string Number(float value) => value.ToString("R", Invariant);
    private static string Number(double value) => value.ToString("R", Invariant);
    private static string? OptionalNumber(float? value) => value is { } number ? Number(number) : null;
    private static string TextValue(string? value) => value is null ? "<null>" : value;
    private static string Flag(bool value) => value ? "true" : "false";
}
