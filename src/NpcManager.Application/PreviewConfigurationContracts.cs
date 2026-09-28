using System.Collections.Immutable;

namespace NpcManager.Application;

/// <summary>A bounded, versioned camera configuration for semantic preview evidence.</summary>
public sealed record PreviewCameraPreset(
    string Id,
    int Version,
    float YawDegrees,
    float PitchDegrees,
    float Distance,
    float FieldOfViewDegrees);

/// <summary>A single deterministic light in a named preview rig.</summary>
public sealed record PreviewLightSource(
    string Id,
    float AzimuthDegrees,
    float ElevationDegrees,
    float Intensity,
    float Red,
    float Green,
    float Blue);

/// <summary>A bounded, versioned collection of preview lights.</summary>
public sealed record PreviewLightingPreset(
    string Id,
    int Version,
    float AmbientIntensity,
    ImmutableArray<PreviewLightSource> Lights);
