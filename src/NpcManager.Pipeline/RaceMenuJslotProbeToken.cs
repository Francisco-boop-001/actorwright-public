using NpcManager.Application;

namespace NpcManager.Pipeline;

/// <summary>
/// The only value that can authorize the private first-use JSlot probe. The
/// token is intentionally not exported from Application or exposed as a
/// public factory; all consumers must pass the exact object reference.
/// </summary>
internal static class RaceMenuJslotProbeToken
{
    internal static readonly RaceMenuJslotOutputBindingProbeToken Instance =
        new();

    internal static bool IsValid(
        RaceMenuJslotOutputBindingProbeToken? value) =>
        ReferenceEquals(value, Instance);

    internal static bool IsInvalid(
        RaceMenuJslotOutputBindingProbeToken? value) =>
        value is not null && !IsValid(value);
}
