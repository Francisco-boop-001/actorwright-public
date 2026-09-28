using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Three finite scalar values used by the bounded face-pose interchange contract.</summary>
public readonly record struct FacePoseVector(float X, float Y, float Z);

public sealed record FaceBoneRegionBone(
    string Bone,
    FacePoseVector MinPosition,
    FacePoseVector MaxPosition,
    FacePoseVector MinRotation,
    FacePoseVector MaxRotation,
    FacePoseVector MinScale,
    FacePoseVector MaxScale);

public sealed record FaceBoneRegion(
    int Id,
    string Name,
    FacePoseVector DefaultPosition,
    FacePoseVector DefaultRotation,
    FacePoseVector DefaultScale,
    ImmutableArray<FaceBoneRegionBone> Bones);

public sealed record FaceMorphSlider(
    int RegionId,
    FacePoseVector Position,
    FacePoseVector Rotation,
    float Scale);

public sealed record FaceVertexDelta(int Index, FacePoseVector Delta);

/// <summary>
/// One vertex channel in the same order in which the pinned MultiMorphResolver appends
/// resolver plans. The channel is intentionally mesh-independent; TRI topology belongs to a
/// later renderer/bake slice.
/// </summary>
public sealed record FaceVertexMorphChannel(
    string Resolver,
    string Name,
    float Weight,
    ImmutableArray<FaceVertexDelta> Vertices);

public sealed record FacePoseInput(
    int SchemaVersion,
    GameEdition Edition,
    FormId NpcFormId,
    float FacialMorphIntensity,
    ImmutableArray<FaceBoneRegion> Regions,
    ImmutableArray<FaceMorphSlider> FaceMorphs,
    ImmutableArray<FaceVertexMorphChannel> VertexMorphs);

public sealed record FacePoseResolveRequest(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath PresetPath);

public sealed record FaceBonePose(
    string Bone,
    FacePoseVector Position,
    FacePoseVector Rotation,
    FacePoseVector Scale);

public sealed record FaceVertexDeltaResult(int Index, FacePoseVector Delta);

public sealed record FacePoseResolveResult(
    FacePoseInput Input,
    Sha256Hash SourceHash,
    ImmutableArray<FaceBonePose> BonePoses,
    ImmutableArray<FaceVertexDeltaResult> VertexDeltas,
    ImmutableArray<string> AppliedChannels,
    ImmutableArray<string> CombinationOrder,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsResolved => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IFacePoseResolver
{
    ValueTask<FacePoseResolveResult> ResolveAsync(FacePoseResolveRequest request,
        CancellationToken cancellationToken);
}
