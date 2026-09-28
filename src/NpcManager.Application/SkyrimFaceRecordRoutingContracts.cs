using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>One copied plugin admitted as read-only Skyrim face-record authority.</summary>
public sealed record SkyrimFaceRecordPluginAuthority(
    PluginName Plugin,
    WorkspacePath Path,
    Sha256Hash ExpectedSha256);

/// <summary>
/// Converts an explicit ascending plugin-name order below one copied Data root
/// into ordinary-file, SHA-256-bound record authorities.
/// </summary>
public sealed record SkyrimFaceRecordPluginAuthorityRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder)
{
    public ImmutableArray<SkyrimFaceRecordPluginAuthority>
        StagedPluginAuthorities { get; init; } = [];
}

public sealed record SkyrimFaceRecordPluginAuthorityResult(
    bool Accepted,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> Authorities,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceRecordPluginAuthorityLoader
{
    ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
        SkyrimFaceRecordPluginAuthorityRequest request,
        CancellationToken cancellationToken);
}

/// <summary>The three HDPT NAM0 roles consumed by Skyrim's FaceGen bake.</summary>
public enum SkyrimHdptTriRole
{
    RaceMorph = 0,
    Mesh = 1,
    CharGen = 2
}

/// <summary>
/// One explicitly selected HDPT root. RequiredTriRoles lets the caller state
/// which record-declared inputs its bake route actually needs; absent roles are
/// refused rather than reconstructed from filenames.
/// </summary>
public sealed record SkyrimFaceRecordHeadPartSelection(
    FormReference Reference,
    ImmutableArray<SkyrimHdptTriRole> RequiredTriRoles);

/// <summary>
/// Read-only request for the Skyrim HDPT/RACE routing layer. Plugins are in
/// ascending load order, so a later copied override wins the same FormKey.
/// </summary>
public sealed record SkyrimFaceRecordRouteRequest(
    GameEdition Edition,
    FormReference Race,
    NpcSex Sex,
    ImmutableArray<SkyrimFaceRecordHeadPartSelection> SelectedHeadParts,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder);

/// <summary>Identity of the copied plugin that supplied a winning record.</summary>
public sealed record SkyrimFaceRecordProvider(
    PluginName Plugin,
    WorkspacePath Path,
    Sha256Hash Sha256);

/// <summary>One exact record-declared HDPT NAM0/NAM1 TRI route.</summary>
public sealed record SkyrimHdptTriRoute(
    SkyrimHdptTriRole Role,
    AssetPath Path);

/// <summary>
/// One winning HDPT TNAM target. RawTxSlots is the exact eight-slot TX00-TX07
/// vector represented relative to Data/Textures, with backslash separators.
/// </summary>
public sealed record SkyrimFaceTextureSetRecordRoute(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    ImmutableArray<string> RawTxSlots);

/// <summary>
/// One route-only projection of the FLST referenced by an HDPT ValidRaces
/// field. The positional graph value remains the FLST pointer; this additive
/// projection carries the resolved record evidence needed by consumers.
/// </summary>
public sealed record SkyrimFaceFormListRoute(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    ImmutableArray<FormReference> Items,
    bool IsDeleted);

/// <summary>
/// One selected, race-default, or recursively reached HNAM record. DeclaredType
/// remains the HDPT PNAM value; EffectiveType applies upstream's Misc-child
/// inheritance rule without changing the record itself.
/// </summary>
public sealed record SkyrimFaceHeadPartRecordRoute(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    string EditorId,
    NpcHeadPartType DeclaredType,
    NpcHeadPartType EffectiveType,
    AssetPath ModelNif,
    ImmutableArray<SkyrimHdptTriRoute> TriRoutes,
    ImmutableArray<FormReference> ExtraParts,
    FormReference? Parent,
    int Depth,
    bool IsSelected,
    bool IsRaceDefault)
{
    public SkyrimFaceTextureSetRecordRoute? TextureSet { get; init; }
}

/// <summary>
/// One bounded visited HDPT node, including the winning copied-plugin evidence
/// needed by later external-provider discovery. This additive projection does
/// not replace the legacy model-bearing <see cref="SkyrimFaceHeadPartRecordRoute" />.
/// </summary>
public sealed record SkyrimFaceHeadPartGraphRoute(
    FormReference OriginForm,
    PluginName RequiredOutputMaster,
    FormReference WinningForm,
    PluginName WinningPlugin,
    Sha256Hash WinningPluginSha256,
    long WinningPluginByteLength,
    Sha256Hash WinningRecordSha256,
    string EditorId,
    NpcHeadPartType DeclaredType,
    NpcHeadPartType EffectiveType,
    AssetPath? ModelNif,
    ImmutableArray<SkyrimHdptTriRoute> TriRoutes,
    ImmutableArray<FormReference> HnamEdges,
    FormReference? Parent,
    int Depth,
    bool IsSelected,
    bool IsRaceDefault,
    int RouteOrder,
    NpcSex? AppliesToSex,
    FormReference? ValidRace)
{
    public SkyrimFaceFormListRoute? ValidRaceList { get; init; }
}

/// <summary>One RACE KWDA entry resolved to the KYWD EditorID used by FaceGen.</summary>
public sealed record SkyrimRaceKeywordRoute(
    FormReference Reference,
    string EditorId,
    SkyrimFaceRecordProvider Provider);

/// <summary>
/// Exact RACE inputs required by the upstream Skyrim morph planner. NAM8 is a
/// single-step morph-race redirect; absence uses the race's own EditorID.
/// </summary>
public sealed record SkyrimRaceFaceRecordRoute(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    string EditorId,
    FormReference? MorphRaceReference,
    string MorphRaceEditorId,
    ImmutableArray<SkyrimRaceKeywordRoute> Keywords,
    ImmutableArray<FormReference> MaleDefaultHeadParts,
    ImmutableArray<FormReference> FemaleDefaultHeadParts,
    ImmutableArray<FormReference> SelectedGenderDefaultHeadParts);

/// <summary>
/// Closed record route. RootHeadParts is the race-default/selected PNAM merge;
/// HeadParts is its bounded breadth-first HNAM expansion.
/// </summary>
public sealed record SkyrimFaceRecordRoute(
    SkyrimRaceFaceRecordRoute Race,
    ImmutableArray<FormReference> RootHeadParts,
    ImmutableArray<SkyrimFaceHeadPartRecordRoute> HeadParts)
{
    public ImmutableArray<SkyrimFaceHeadPartGraphRoute> HeadPartGraph { get; init; } = [];
}

public sealed record SkyrimFaceRecordRouteResult(
    bool Accepted,
    SkyrimFaceRecordRoute? Route,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceRecordRouteResolver
{
    ValueTask<SkyrimFaceRecordRouteResult> ResolveAsync(
        SkyrimFaceRecordRouteRequest request,
        CancellationToken cancellationToken);
}
