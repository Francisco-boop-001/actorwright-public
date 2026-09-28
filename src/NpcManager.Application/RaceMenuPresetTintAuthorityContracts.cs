using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum RaceMenuPresetTintAuthorityDispositionKind
{
    MappedRecord,
    Baked,
    Inactive
}

/// <summary>
/// One source tint's deterministic record/bake disposition before the record
/// authority manifest is serialized and hash-bound.
/// </summary>
public sealed record RaceMenuPresetTintAuthorityDisposition(
    PresetTint Source,
    RaceMenuPresetTintAuthorityDispositionKind Kind,
    ushort? TiniIndex,
    short? TiasPresetIndex,
    bool IsSkinTint);

public sealed record RaceMenuPresetQnamPlan(
    int SourceJslotTintIndex,
    float Red,
    float Green,
    float Blue);

public sealed record RaceMenuPresetTintAuthorityPlan(
    PresetDocument Preset,
    SkyrimRaceTintAuthority RaceAuthority,
    ImmutableArray<RaceMenuPresetTintAuthorityDisposition> Dispositions,
    RaceMenuPresetQnamPlan Qnam);

public sealed record RaceMenuPresetTintAuthorityPlanResult(
    bool Accepted,
    RaceMenuPresetTintAuthorityPlan? Plan,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetTintAuthorityMapper
{
    RaceMenuPresetTintAuthorityPlanResult Map(
        PresetDocument preset,
        SkyrimRaceTintAuthority raceAuthority);
}
