using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SseBodyWeightGender
{
    Male,
    Female
}

public readonly record struct SseBodyWeightVector(float X, float Y, float Z);

public sealed record SseBodyWeightInput(
    int SchemaVersion,
    GameEdition Edition,
    SseBodyWeightGender Gender,
    float WeightPercent,
    byte WeightSliderFlags,
    int BaseDigit,
    ImmutableArray<SseBodyWeightVector> BaseVertices,
    ImmutableArray<SseBodyWeightVector> TwinVertices);

public sealed record SseBodyWeightDelta(int Index, SseBodyWeightVector Delta);

public sealed record SseBodyWeightResolutionRequest(
    GameEdition Edition,
    WorkspacePath InputPath);

public sealed record SseBodyWeightResolutionResult(
    GameEdition Edition,
    WorkspacePath InputPath,
    Sha256Hash? SourceSha256,
    bool SliderEnabled,
    bool Applied,
    SseBodyWeightGender? Gender,
    float? WeightPercent,
    float? ClampedWeight,
    int? BaseDigit,
    float? ChannelWeight,
    string? ChannelName,
    int BaseVertexCount,
    int TwinVertexCount,
    ImmutableArray<SseBodyWeightDelta> Deltas,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface ISseBodyWeightResolutionService
{
    ValueTask<SseBodyWeightResolutionResult> ResolveAsync(
        SseBodyWeightResolutionRequest request, CancellationToken cancellationToken);
}
