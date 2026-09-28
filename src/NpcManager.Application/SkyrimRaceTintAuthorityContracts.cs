using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Reads the winning gender-specific tint table from one hash-bound Skyrim
/// RACE record. Unlike NPC FaceTint resolution, this authority does not require
/// an already-authored NPC and can therefore seed a new preset-derived actor.
/// </summary>
public sealed record SkyrimRaceTintAuthorityRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormReference Race,
    NpcSex Sex,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder);

/// <summary>
/// The semantic distinction needed when deriving NPC QNAM. SourceMaskType is
/// retained separately so unsupported future engine values are never guessed.
/// </summary>
public enum SkyrimRaceTintMaskKind
{
    Other,
    SkinTone
}

/// <summary>
/// One exact RACE TINI/TINT row in engine order. Real RACE providers can reuse
/// a TINI across distinct masks; consumers must not author such an ambiguous
/// index on an NPC and should retain that mask only in the FaceTint bake.
/// </summary>
public sealed record SkyrimRaceTintLayerAuthority(
    int RaceOrder,
    ushort Index,
    int? SourceMaskType,
    SkyrimRaceTintMaskKind Kind,
    AssetPath MaskPath);

/// <summary>
/// Closed static authority for a winning RACE tint table. RuntimeAuthority is
/// always false until Skyrim proves the active provider and rendered actor.
/// </summary>
public sealed record SkyrimRaceTintAuthority(
    FormReference Race,
    SkyrimFaceRecordProvider Provider,
    NpcSex Sex,
    ImmutableArray<SkyrimRaceTintLayerAuthority> Layers,
    bool RuntimeAuthority);

public sealed record SkyrimRaceTintAuthorityResult(
    bool Accepted,
    SkyrimRaceTintAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimRaceTintAuthorityReader
{
    ValueTask<SkyrimRaceTintAuthorityResult> ReadAsync(
        SkyrimRaceTintAuthorityRequest request,
        CancellationToken cancellationToken);
}
