using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum BodyOverlayTarget
{
    Body,
    Hands,
    Feet
}

public readonly record struct Fo4OverlaySlot(int Slot, string Material);

/// <summary>One source-shaped overlay layer. The game edition selects the valid union branch:
/// Fallout 4 uses LooksMenu template data; Skyrim SE uses RaceMenu node data.</summary>
public sealed record BodyOverlayLayerInput(
    string? Template,
    int Priority,
    ImmutableArray<float> Tint,
    ImmutableArray<float> OffsetUv,
    ImmutableArray<float> ScaleUv,
    ImmutableArray<Fo4OverlaySlot> Slots,
    string? Node,
    string? Diffuse,
    string? Normal,
    ImmutableArray<float> SseTint,
    float? Alpha,
    int SourceIndex);

public sealed record BodyOverlayPatchRequest(
    GameEdition Edition,
    FormId NpcFormId,
    ImmutableArray<BodyOverlayLayerInput> Layers,
    Sha256Hash? SourceSha256 = null);

public sealed record BodyOverlayResolvedLayer(
    int Order,
    int SourceIndex,
    int? Priority,
    string? Template,
    ImmutableArray<Fo4OverlaySlot> Slots,
    BodyOverlayTarget? Target,
    int? NodeIndex,
    string? Node,
    string? Diffuse,
    string? Normal,
    ImmutableArray<float> Tint,
    ImmutableArray<float> OffsetUv,
    ImmutableArray<float> ScaleUv,
    float? Alpha);

public sealed record BodyOverlayPatchResult(
    GameEdition Edition,
    FormId NpcFormId,
    Sha256Hash? SourceSha256,
    Sha256Hash CanonicalSha256,
    ImmutableArray<BodyOverlayResolvedLayer> Layers,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IBodyOverlayPatchService
{
    BodyOverlayPatchResult Resolve(BodyOverlayPatchRequest request);
}
