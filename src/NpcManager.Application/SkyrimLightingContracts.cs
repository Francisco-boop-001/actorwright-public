using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimLightingEditResult(
    bool Accepted,
    PreviewLightingPreset? Preset,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimLightingSettingsLoadResult(
    bool LoadedFromDisk,
    PreviewLightingPreset Preset,
    Sha256Hash? Sha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimLightingSettingsSaveResult(
    bool Saved,
    PreviewLightingPreset? Preset,
    Sha256Hash? Sha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimLightingSettingsService
{
    ValueTask<SkyrimLightingSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    ValueTask<SkyrimLightingSettingsSaveResult> SaveAsync(
        PreviewLightingPreset preset,
        CancellationToken cancellationToken);
}

public static class SkyrimLightingRules
{
    public const int MaximumLights = 8;
    public const int MaximumIdLength = 128;

    private static readonly PreviewLightingPreset Default = new(
        "default",
        1,
        0.2f,
        [new PreviewLightSource("key", -35, 25, 1, 1, 0.95f, 0.9f)]);

    public static PreviewLightingPreset DefaultPreset { get; } = Default;

    public static ImmutableArray<Diagnostic> Validate(
        PreviewLightingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateId(preset.Id, "preset", diagnostics);
        if (preset.Version is < 1 or > 1000)
        {
            diagnostics.Add(Error(
                "lighting-version-invalid",
                "Lighting preset version must be from 1 to 1000."));
        }
        ValidateFiniteRange(
            preset.AmbientIntensity,
            0,
            4,
            "lighting-ambient-invalid",
            "Ambient intensity",
            diagnostics);

        if (preset.Lights.IsDefault ||
            preset.Lights.Length is < 1 or > MaximumLights)
        {
            diagnostics.Add(Error(
                "lighting-count-invalid",
                $"A lighting rig must contain one to {MaximumLights} lights."));
            return diagnostics.ToImmutable();
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < preset.Lights.Length; index++)
        {
            PreviewLightSource light = preset.Lights[index];
            ValidateId(light.Id, $"light {index + 1}", diagnostics);
            if (!string.IsNullOrWhiteSpace(light.Id) && !ids.Add(light.Id))
            {
                diagnostics.Add(Error(
                    "lighting-light-id-duplicate",
                    $"Light ID '{light.Id}' appears more than once."));
            }
            ValidateFiniteRange(
                light.AzimuthDegrees,
                -180,
                180,
                "lighting-azimuth-invalid",
                $"Light {index + 1} azimuth",
                diagnostics);
            ValidateFiniteRange(
                light.ElevationDegrees,
                -90,
                90,
                "lighting-elevation-invalid",
                $"Light {index + 1} elevation",
                diagnostics);
            ValidateFiniteRange(
                light.Intensity,
                0,
                8,
                "lighting-intensity-invalid",
                $"Light {index + 1} intensity",
                diagnostics);
            ValidateFiniteRange(light.Red, 0, 1, "lighting-red-invalid",
                $"Light {index + 1} red channel", diagnostics);
            ValidateFiniteRange(light.Green, 0, 1, "lighting-green-invalid",
                $"Light {index + 1} green channel", diagnostics);
            ValidateFiniteRange(light.Blue, 0, 1, "lighting-blue-invalid",
                $"Light {index + 1} blue channel", diagnostics);
        }
        return diagnostics.ToImmutable();
    }

    public static SkyrimLightingEditResult SetAmbient(
        PreviewLightingPreset preset,
        float ambientIntensity) =>
        AcceptIfValid(preset with { AmbientIntensity = ambientIntensity });

    public static SkyrimLightingEditResult AddLight(
        PreviewLightingPreset preset,
        PreviewLightSource light)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(light);
        ImmutableArray<Diagnostic> existing = Validate(preset);
        if (existing.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new SkyrimLightingEditResult(false, null, existing);
        return AcceptIfValid(preset with { Lights = preset.Lights.Add(light) });
    }

    public static SkyrimLightingEditResult ReplaceLight(
        PreviewLightingPreset preset,
        int index,
        PreviewLightSource light)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(light);
        if (index < 0 || index >= preset.Lights.Length)
            return Refused("lighting-light-index", "The selected light no longer exists.");
        return AcceptIfValid(preset with
        {
            Lights = preset.Lights.SetItem(index, light)
        });
    }

    public static SkyrimLightingEditResult RemoveLight(
        PreviewLightingPreset preset,
        int index)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (index < 0 || index >= preset.Lights.Length)
            return Refused("lighting-light-index", "The selected light no longer exists.");
        if (preset.Lights.Length == 1)
            return Refused("lighting-last-light", "A lighting rig must retain at least one light.");
        return AcceptIfValid(preset with
        {
            Lights = preset.Lights.RemoveAt(index)
        });
    }

    public static SkyrimLightingEditResult MoveLight(
        PreviewLightingPreset preset,
        int index,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (index < 0 || index >= preset.Lights.Length)
            return Refused("lighting-light-index", "The selected light no longer exists.");
        int destination = index + offset;
        if (destination < 0 || destination >= preset.Lights.Length)
            return Refused("lighting-light-move", "The selected light cannot move farther in that direction.");
        PreviewLightSource moving = preset.Lights[index];
        ImmutableArray<PreviewLightSource> reordered = preset.Lights
            .RemoveAt(index)
            .Insert(destination, moving);
        return AcceptIfValid(preset with { Lights = reordered });
    }

    public static bool AreEquivalent(
        PreviewLightingPreset left,
        PreviewLightingPreset right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        left.Version == right.Version &&
        left.AmbientIntensity.Equals(right.AmbientIntensity) &&
        left.Lights.SequenceEqual(right.Lights);

    private static SkyrimLightingEditResult AcceptIfValid(
        PreviewLightingPreset candidate)
    {
        ImmutableArray<Diagnostic> diagnostics = Validate(candidate);
        return new SkyrimLightingEditResult(
            diagnostics.All(item => item.Severity != DiagnosticSeverity.Error),
            diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
                ? null
                : candidate,
            diagnostics);
    }

    private static SkyrimLightingEditResult Refused(string code, string message) =>
        new(false, null, [Error(code, message)]);

    private static void ValidateId(
        string? id,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id.Length > MaximumIdLength ||
            !string.Equals(id, id.Trim(), StringComparison.Ordinal) ||
            id.Any(char.IsControl))
        {
            diagnostics.Add(Error(
                "lighting-id-invalid",
                $"The {role} ID must be trimmed, non-empty, free of control characters, and at most {MaximumIdLength} characters."));
        }
    }

    private static void ValidateFiniteRange(
        float value,
        float minimum,
        float maximum,
        string code,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            diagnostics.Add(Error(
                code,
                $"{role} must be a finite value from {minimum} to {maximum}."));
        }
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
