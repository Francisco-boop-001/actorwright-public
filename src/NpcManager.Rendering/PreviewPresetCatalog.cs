using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;

namespace NpcManager.Rendering;

/// <summary>Parses and resolves bounded camera/lighting catalogs for semantic preview scenes.</summary>
internal static class PreviewPresetCatalog
{
    private const int MaxPresets = 64;
    private const int MaxLights = 8;
    private const int MaxIdLength = 128;

    internal static (ImmutableArray<PreviewCameraPreset> Cameras, ImmutableArray<PreviewLightingPreset> Lightings)
        Read(JsonElement root, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var cameras = ReadCameras(root, diagnostics);
        var lightings = ReadLightings(root, diagnostics);
        return (cameras, lightings);
    }

    internal static bool TryResolveCamera(ImmutableArray<PreviewCameraPreset> cameras, string? id,
        out PreviewCameraPreset? selected, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        selected = Resolve(cameras, id, "camera", diagnostics);
        return selected is not null;
    }

    internal static bool TryResolveLighting(ImmutableArray<PreviewLightingPreset> lightings, string? id,
        out PreviewLightingPreset? selected, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        selected = Resolve(lightings, id, "lighting", diagnostics);
        return selected is not null;
    }

    private static ImmutableArray<PreviewCameraPreset> ReadCameras(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "cameraPresets", out var element))
            return [new PreviewCameraPreset("default", 1, 0, 0, 4, 50)];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is 0 or > MaxPresets)
        {
            diagnostics.Add(new Diagnostic("preview-camera-presets-invalid", DiagnosticSeverity.Error,
                $"cameraPresets must be a non-empty array with at most {MaxPresets} entries."));
            return [];
        }

        var result = ImmutableArray.CreateBuilder<PreviewCameraPreset>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.cameraPresets[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetId(item, "id", path, ids, diagnostics,
                    out var id) || !TryGetVersion(item, path, diagnostics, out var version) ||
                !TryGetFloat(item, "yaw", -180, 180, path, diagnostics, out var yaw) ||
                !TryGetFloat(item, "pitch", -89, 89, path, diagnostics, out var pitch) ||
                !TryGetFloat(item, "distance", 0.1f, 100, path, diagnostics, out var distance) ||
                !TryGetFloat(item, "fov", 1, 179, path, diagnostics, out var fov))
                continue;
            result.Add(new PreviewCameraPreset(id, version, yaw, pitch, distance, fov));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<PreviewLightingPreset> ReadLightings(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "lightingPresets", out var element))
        {
            return [new PreviewLightingPreset("default", 1, 0.2f,
                [new PreviewLightSource("key", -35, 25, 1, 1, 0.95f, 0.9f)])];
        }
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is 0 or > MaxPresets)
        {
            diagnostics.Add(new Diagnostic("preview-lighting-presets-invalid", DiagnosticSeverity.Error,
                $"lightingPresets must be a non-empty array with at most {MaxPresets} entries."));
            return [];
        }

        var result = ImmutableArray.CreateBuilder<PreviewLightingPreset>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.lightingPresets[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetId(item, "id", path, ids, diagnostics,
                    out var id) || !TryGetVersion(item, path, diagnostics, out var version) ||
                !TryGetFloat(item, "ambient", 0, 4, path, diagnostics, out var ambient))
                continue;
            if (!TryGet(item, "lights", out var lightsElement) || lightsElement.ValueKind != JsonValueKind.Array ||
                lightsElement.GetArrayLength() is 0 or > MaxLights)
            {
                diagnostics.Add(new Diagnostic("preview-lighting-lights-invalid", DiagnosticSeverity.Error,
                    $"'{path}.lights' must be a non-empty array with at most {MaxLights} entries."));
                continue;
            }
            var lights = ReadLights(lightsElement, path, diagnostics);
            if (lights.Length == 0) continue;
            result.Add(new PreviewLightingPreset(id, version, ambient, lights));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<PreviewLightSource> ReadLights(JsonElement element, string parentPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = ImmutableArray.CreateBuilder<PreviewLightSource>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"{parentPath}.lights[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetId(item, "id", path, ids, diagnostics,
                    out var id) || !TryGetFloat(item, "azimuth", -180, 180, path, diagnostics, out var azimuth) ||
                !TryGetFloat(item, "elevation", -90, 90, path, diagnostics, out var elevation) ||
                !TryGetFloat(item, "intensity", 0, 8, path, diagnostics, out var intensity) ||
                !TryGetFloat(item, "red", 0, 1, path, diagnostics, out var red) ||
                !TryGetFloat(item, "green", 0, 1, path, diagnostics, out var green) ||
                !TryGetFloat(item, "blue", 0, 1, path, diagnostics, out var blue))
                continue;
            result.Add(new PreviewLightSource(id, azimuth, elevation, intensity, red, green, blue));
        }
        return result.ToImmutable();
    }

    private static T? Resolve<T>(ImmutableArray<T> presets, string? id, string kind,
        ImmutableArray<Diagnostic>.Builder diagnostics) where T : class
    {
        if (presets.Length == 0)
        {
            diagnostics.Add(new Diagnostic($"preview-{kind}-presets-empty", DiagnosticSeverity.Error,
                $"Preview {kind} presets must contain at least one valid entry."));
            return null;
        }
        if (string.IsNullOrWhiteSpace(id)) return presets[0];
        var selected = presets.FirstOrDefault(item => string.Equals(GetId(item), id,
            StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            diagnostics.Add(new Diagnostic($"preview-{kind}-preset-unknown", DiagnosticSeverity.Error,
                $"Preview {kind} preset '{id}' is not present in the manifest catalog."));
        return selected;
    }

    private static string GetId<T>(T preset) => preset switch
    {
        PreviewCameraPreset camera => camera.Id,
        PreviewLightingPreset lighting => lighting.Id,
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    private static bool TryGetId(JsonElement element, string name, string path, HashSet<string> ids,
        ImmutableArray<Diagnostic>.Builder diagnostics, out string id)
    {
        id = string.Empty;
        if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > MaxIdLength ||
            text != text.Trim() || text.Any(char.IsControl) || !ids.Add(text))
        {
            diagnostics.Add(new Diagnostic("preview-preset-id-invalid", DiagnosticSeverity.Error,
                $"'{path}.{name}' must be a unique non-empty identifier of at most {MaxIdLength} characters."));
            return false;
        }
        id = text;
        return true;
    }

    private static bool TryGetVersion(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out int version)
    {
        if (TryGet(element, "version", out var value) && value.TryGetInt32(out version) && version is > 0 and <= 1000)
            return true;
        version = 0;
        diagnostics.Add(new Diagnostic("preview-preset-version-invalid", DiagnosticSeverity.Error,
            $"'{path}.version' must be an integer from 1 to 1000."));
        return false;
    }

    private static bool TryGetFloat(JsonElement element, string name, float minimum, float maximum,
        string path, ImmutableArray<Diagnostic>.Builder diagnostics, out float value)
    {
        if (TryGet(element, name, out var property) && property.TryGetSingle(out value) &&
            float.IsFinite(value) && value >= minimum && value <= maximum)
            return true;
        value = 0;
        diagnostics.Add(new Diagnostic("preview-preset-value-invalid", DiagnosticSeverity.Error,
            $"'{path}.{name}' must be finite and within {minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)}."));
        return false;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default;
        return false;
    }
}
