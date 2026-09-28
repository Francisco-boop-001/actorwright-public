using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>Parses bounded manifest animation clips and resolves explicit frame/time playback.</summary>
internal static class PreviewAnimationCatalog
{
    private const int MaxClips = 256;
    private const int MaxIdLength = 128;
    private const int MaxFrames = 1_000_000;
    private const int MaxRoles = 16;
    private const int MaxMetadataLength = 128;

    internal static ImmutableArray<PreviewAnimationClip> Read(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "animations", out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is 0 or > MaxClips)
        {
            diagnostics.Add(new Diagnostic("preview-animations-invalid", DiagnosticSeverity.Error,
                $"animations must be a non-empty array with at most {MaxClips} entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<PreviewAnimationClip>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.animations[{index++}]";
            if (item.ValueKind != JsonValueKind.Object || !TryGetId(item, path, ids, diagnostics, out var id) ||
                !TryGetAssetPath(item, "path", path, diagnostics, out var animationPath) ||
                !TryGetAssetPath(item, "skeleton", path, diagnostics, out var skeletonPath) ||
                !TryGetFrames(item, path, diagnostics, out var frameCount) ||
                !TryGetFloat(item, "fps", 1, 240, path, diagnostics, out var fps))
                continue;
            var additive = false;
            if (TryGet(item, "additive", out var additiveElement) &&
                (additiveElement.ValueKind != JsonValueKind.True && additiveElement.ValueKind != JsonValueKind.False))
            {
                diagnostics.Add(new Diagnostic("preview-animation-additive-invalid", DiagnosticSeverity.Error,
                    $"'{path}.additive' must be a boolean."));
                continue;
            }
            if (additiveElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                additive = additiveElement.GetBoolean();
            var hasBehaviorGraph = TryGet(item, "behaviorGraph", out _);
            if (!TryGetOptionalString(item, "name", path, diagnostics, out var clipName) ||
                !TryGetOptionalString(item, "category", path, diagnostics, out var category) ||
                !TryGetOptionalString(item, "stateAxes", path, diagnostics, out var stateAxes) ||
                !TryGetOptionalString(item, "folder", path, diagnostics, out var folder) ||
                !TryGetRoles(item, path, diagnostics, out var roles) ||
                !TryGetOptionalBoolean(item, "femaleOnly", path, diagnostics, out var requiresFemale) ||
                !TryGetOptionalBoolean(item, "firstPersonOnly", path, diagnostics, out var firstPersonOnly) ||
                !TryGetOptionalBoolean(item, "behaviorGraph", path, diagnostics, out var fromBehaviorGraph))
                continue;
            if (!hasBehaviorGraph && !string.IsNullOrWhiteSpace(category))
                fromBehaviorGraph = false;
            if (string.IsNullOrWhiteSpace(clipName))
                clipName = System.IO.Path.GetFileNameWithoutExtension(animationPath.Value.Replace('/', '\\'));
            if (string.IsNullOrWhiteSpace(folder))
                folder = InferFolder(animationPath.Value);
            result.Add(new PreviewAnimationClip(id, animationPath, skeletonPath, frameCount, fps, additive,
                clipName, roles, category, stateAxes, requiresFemale, firstPersonOnly, fromBehaviorGraph, folder));
        }
        return result.ToImmutable();
    }

    internal static PreviewSceneAnimation? Resolve(ImmutableArray<PreviewAnimationClip> clips, string? animationId,
        int? frame, float? timeSeconds, float? playbackRate, bool playing,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var hasPlaybackOption = frame.HasValue || timeSeconds.HasValue || playbackRate.HasValue || playing;
        if (string.IsNullOrWhiteSpace(animationId))
        {
            if (hasPlaybackOption)
                diagnostics.Add(new Diagnostic("preview-animation-options-without-animation", DiagnosticSeverity.Error,
                    "Animation frame/time/rate/play options require --animation."));
            return null;
        }
        var clip = clips.FirstOrDefault(item => string.Equals(item.Id, animationId,
            StringComparison.OrdinalIgnoreCase));
        if (clip is null)
        {
            diagnostics.Add(new Diagnostic("preview-animation-unknown", DiagnosticSeverity.Error,
                $"Preview animation '{animationId}' is not present in the manifest catalog."));
            return null;
        }
        if (frame.HasValue == timeSeconds.HasValue)
        {
            diagnostics.Add(new Diagnostic("preview-animation-position-required", DiagnosticSeverity.Error,
                "Preview animation requires exactly one --frame or --time value."));
            return null;
        }
        var resolvedFrame = frame ?? (int)Math.Floor(timeSeconds!.Value * clip.FramesPerSecond);
        var resolvedTime = timeSeconds ?? resolvedFrame / clip.FramesPerSecond;
        if (resolvedFrame < 0 || resolvedFrame >= clip.FrameCount || !float.IsFinite(resolvedTime) ||
            resolvedTime < 0 || resolvedTime > (clip.FrameCount - 1) / clip.FramesPerSecond)
        {
            diagnostics.Add(new Diagnostic("preview-animation-position-out-of-range", DiagnosticSeverity.Error,
                $"Preview animation position must remain within frames 0..{clip.FrameCount - 1} and its clip duration."));
            return null;
        }
        var rate = playbackRate ?? clip.FramesPerSecond;
        if (!float.IsFinite(rate) || rate < 1 || rate > 240)
        {
            diagnostics.Add(new Diagnostic("preview-animation-rate-invalid", DiagnosticSeverity.Error,
                "Preview animation playback rate must be finite and between 1 and 240 FPS."));
            return null;
        }
        return new PreviewSceneAnimation(clip.Id, clip.Path.Value, clip.Skeleton.Value, resolvedFrame,
            resolvedTime, rate, playing, clip.Additive);
    }

    private static bool TryGetId(JsonElement element, string path, HashSet<string> ids,
        ImmutableArray<Diagnostic>.Builder diagnostics, out string id)
    {
        id = string.Empty;
        if (!TryGet(element, "id", out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > MaxIdLength ||
            text != text.Trim() || text.Any(char.IsControl) || !ids.Add(text))
        {
            diagnostics.Add(new Diagnostic("preview-animation-id-invalid", DiagnosticSeverity.Error,
                $"'{path}.id' must be a unique non-empty identifier of at most {MaxIdLength} characters."));
            return false;
        }
        id = text;
        return true;
    }

    private static bool TryGetAssetPath(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out AssetPath assetPath)
    {
        try
        {
            if (TryGet(element, name, out var property) && property.ValueKind == JsonValueKind.String &&
                property.GetString() is { } text)
            {
                assetPath = new AssetPath(text);
                return true;
            }
        }
        catch (ArgumentException) { }
        assetPath = default;
        diagnostics.Add(new Diagnostic("preview-animation-path-invalid", DiagnosticSeverity.Error,
            $"'{path}.{name}' must be a normalized relative asset path."));
        return false;
    }

    private static bool TryGetFrames(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out int frames)
    {
        if (TryGet(element, "frames", out var property) && property.TryGetInt32(out frames) &&
            frames is > 0 and <= MaxFrames)
            return true;
        frames = 0;
        diagnostics.Add(new Diagnostic("preview-animation-frames-invalid", DiagnosticSeverity.Error,
            $"'{path}.frames' must be an integer from 1 to {MaxFrames}."));
        return false;
    }

    private static bool TryGetFloat(JsonElement element, string name, float minimum, float maximum,
        string path, ImmutableArray<Diagnostic>.Builder diagnostics, out float value)
    {
        if (TryGet(element, name, out var property) && property.TryGetSingle(out value) &&
            float.IsFinite(value) && value >= minimum && value <= maximum)
            return true;
        value = 0;
        diagnostics.Add(new Diagnostic("preview-animation-value-invalid", DiagnosticSeverity.Error,
            $"'{path}.{name}' must be finite and within {minimum.ToString(CultureInfo.InvariantCulture)}..{maximum.ToString(CultureInfo.InvariantCulture)}."));
        return false;
    }

    private static bool TryGetOptionalString(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out string value)
    {
        value = string.Empty;
        if (!TryGet(element, name, out var property)) return true;
        if (property.ValueKind == JsonValueKind.String && property.GetString() is { } text &&
            text.Length <= MaxMetadataLength && text == text.Trim() && !text.Any(char.IsControl))
        {
            value = text;
            return true;
        }
        diagnostics.Add(new Diagnostic("preview-animation-metadata-invalid", DiagnosticSeverity.Error,
            $"'{path}.{name}' must be a string of at most {MaxMetadataLength} characters without control characters."));
        return false;
    }

    private static bool TryGetOptionalBoolean(JsonElement element, string name, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out bool value)
    {
        value = name.Equals("behaviorGraph", StringComparison.OrdinalIgnoreCase);
        if (!TryGet(element, name, out var property)) return true;
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }
        diagnostics.Add(new Diagnostic("preview-animation-metadata-invalid", DiagnosticSeverity.Error,
            $"'{path}.{name}' must be a boolean."));
        return false;
    }

    private static bool TryGetRoles(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, out ImmutableArray<string> roles)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        if (TryGet(element, "role", out var role))
        {
            if (role.ValueKind != JsonValueKind.String || role.GetString() is not { } roleText ||
                !TryAddRole(roleText, builder))
            {
                diagnostics.Add(new Diagnostic("preview-animation-roles-invalid", DiagnosticSeverity.Error,
                    $"'{path}.role' must be a non-empty role string of at most {MaxMetadataLength} characters."));
                roles = [];
                return false;
            }
        }
        if (TryGet(element, "roles", out var roleList))
        {
            if (roleList.ValueKind != JsonValueKind.Array || roleList.GetArrayLength() > MaxRoles)
            {
                diagnostics.Add(new Diagnostic("preview-animation-roles-invalid", DiagnosticSeverity.Error,
                    $"'{path}.roles' must be an array with at most {MaxRoles} role strings."));
                roles = [];
                return false;
            }
            foreach (var item in roleList.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } roleText ||
                    !TryAddRole(roleText, builder))
                {
                    diagnostics.Add(new Diagnostic("preview-animation-roles-invalid", DiagnosticSeverity.Error,
                        $"'{path}.roles' contains an invalid role string."));
                    roles = [];
                    return false;
                }
            }
        }
        roles = builder.ToImmutable();
        return true;
    }

    private static bool TryAddRole(string role, ImmutableArray<string>.Builder roles)
    {
        if (string.IsNullOrWhiteSpace(role) || role.Length > MaxMetadataLength || role != role.Trim() ||
            role.Any(char.IsControl) || roles.Any(item => string.Equals(item, role, StringComparison.OrdinalIgnoreCase)) ||
            roles.Count >= MaxRoles)
            return false;
        roles.Add(role);
        return true;
    }

    private static string InferFolder(string animationPath)
    {
        var normalized = animationPath.Replace('\\', '/');
        var marker = normalized.IndexOf("animations/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return string.Empty;
        var rest = normalized[(marker + "animations/".Length)..];
        var slash = rest.LastIndexOf('/');
        return slash > 0 ? rest[..slash] : string.Empty;
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
