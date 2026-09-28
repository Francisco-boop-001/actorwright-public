using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Resolves the native Skyrim FaceTint record inputs for one winning NPC. The
/// plugin order is ascending and hash-bound; later copied records win by FormKey.
/// </summary>
public sealed record SkyrimNativeFaceTintRecordRequest(
    GameEdition Edition,
    FormReference Npc,
    NpcSex ExpectedSex,
    FormReference ExpectedRace,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder,
    ImmutableArray<SkyrimNativeFaceTintMaskOverride> MaskOverrides = default);

public sealed record SkyrimNativeFaceTintMaskOverride(
    ushort Index,
    AssetPath MaskPath);

public enum SkyrimNativeFaceTintColorSource
{
    NpcAuthored,
    RaceDefault
}

/// <summary>
/// One effective tint layer in RACE order. Color and coverage have already been
/// selected from the NPC-authored row or the RACE default preset. A real RACE
/// may reuse an index across distinct masks; such rows are admitted only with
/// race-default routing because an NPC-authored value would be ambiguous.
/// </summary>
public sealed record SkyrimNativeFaceTintLayerRoute(
    int RaceOrder,
    ushort Index,
    int? MaskType,
    AssetPath MaskPath,
    byte Red,
    byte Green,
    byte Blue,
    float Coverage,
    SkyrimNativeFaceTintColorSource ColorSource,
    FormReference? DefaultColorReference,
    SkyrimFaceRecordProvider? DefaultColorProvider);

public sealed record SkyrimNativeFaceTintRecordRoute(
    FormReference Npc,
    SkyrimFaceRecordProvider NpcProvider,
    FormReference Race,
    SkyrimFaceRecordProvider RaceProvider,
    NpcSex Sex,
    ImmutableArray<SkyrimNativeFaceTintLayerRoute> Layers);

public sealed record SkyrimNativeFaceTintRecordResult(
    bool Accepted,
    SkyrimNativeFaceTintRecordRoute? Route,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimNativeFaceTintRecordResolver
{
    ValueTask<SkyrimNativeFaceTintRecordResult> ResolveAsync(
        SkyrimNativeFaceTintRecordRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact winning DDS bytes for one RACE-declared mask path. Loose/archive
/// winner discovery and extraction happen before this closed composition seam.
/// </summary>
public sealed record SkyrimNativeFaceTintMaskInput(
    AssetPath AssetPath,
    Sha256Hash ContentSha256,
    ImmutableArray<byte> Content);

public sealed record SkyrimNativeFaceTintBuildRequest(
    SkyrimNativeFaceTintRecordRoute RecordRoute,
    ImmutableArray<SkyrimNativeFaceTintMaskInput> Masks,
    WorkspacePath OutputPath);

public sealed record SkyrimNativeFaceTintLayerEvidence(
    int RaceOrder,
    ushort Index,
    int? MaskType,
    AssetPath MaskPath,
    Sha256Hash? MaskSha256,
    byte Red,
    byte Green,
    byte Blue,
    float Coverage,
    SkyrimNativeFaceTintColorSource ColorSource,
    bool Applied);

/// <summary>
/// Independently read-back DXT5 artifact evidence. RuntimeAuthority remains
/// false until the game proves the active provider and rendered result.
/// </summary>
public sealed record SkyrimNativeFaceTintBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    FormReference Npc,
    FormReference Race,
    NpcSex Sex,
    int Width,
    int Height,
    string Format,
    int MipCount,
    Sha256Hash LinearRasterSha256,
    Sha256Hash OutputSha256,
    Sha256Hash ReadbackRasterSha256,
    ImmutableArray<SkyrimNativeFaceTintLayerEvidence> Layers,
    bool RuntimeAuthority);

public sealed record SkyrimNativeFaceTintBuildResult(
    bool Written,
    SkyrimNativeFaceTintBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimNativeFaceTintBuildService
{
    ValueTask<SkyrimNativeFaceTintBuildResult> BuildAsync(
        SkyrimNativeFaceTintBuildRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact loose or BSA winner declaration for one RACE-declared tint mask.
/// </summary>
public sealed record SkyrimNativeFaceTintMaskAuthority(
    string ProviderId,
    AssetProviderKind ProviderKind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    AssetPath AssetPath,
    long ContentLength,
    Sha256Hash ContentSha256);

/// <summary>
/// Plans exact winners for a union of RACE-declared masks. A loose file wins;
/// an archive member is accepted only when exactly one copied BSA provides it.
/// Ambiguous archive conflicts require an explicit authority instead of guessing.
/// </summary>
public sealed record SkyrimNativeFaceTintAuthorityPlanRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<AssetPath> RequiredMasks);

public sealed record SkyrimNativeFaceTintAuthorityPlanResult(
    bool Accepted,
    ImmutableArray<SkyrimNativeFaceTintMaskAuthority> Authorities,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimNativeFaceTintAuthorityPlanner
{
    ValueTask<SkyrimNativeFaceTintAuthorityPlanResult> PlanAsync(
        SkyrimNativeFaceTintAuthorityPlanRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimNativeFaceTintMaterializationRequest(
    SkyrimNativeFaceTintRecordRequest RecordRequest,
    WorkspacePath AssetAllowedRoot,
    ImmutableArray<SkyrimNativeFaceTintMaskAuthority> MaskAuthorities,
    WorkspacePath OutputPath);

public sealed record SkyrimNativeFaceTintMaterializationResult(
    bool Written,
    SkyrimNativeFaceTintBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Shared orchestration boundary from hash-bound records and exact loose/BSA
/// winners to one independently reopened canonical FaceTint DDS.
/// </summary>
public interface ISkyrimNativeFaceTintMaterializationService
{
    ValueTask<SkyrimNativeFaceTintMaterializationResult> MaterializeAsync(
        SkyrimNativeFaceTintMaterializationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// User-facing automatic path from an explicit copied Data root and ascending
/// plugin order to one independently reopened native Skyrim FaceTint DDS.
/// </summary>
public sealed record SkyrimNativeFaceTintPipelineRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FormReference Npc,
    NpcSex ExpectedSex,
    FormReference ExpectedRace,
    WorkspacePath OutputPath,
    ImmutableArray<SkyrimNativeFaceTintMaskOverride> MaskOverrides = default)
{
    public ImmutableArray<SkyrimFaceRecordPluginAuthority>
        StagedPluginAuthorities { get; init; } = [];
}

public sealed record SkyrimNativeFaceTintPipelineResult(
    bool Written,
    SkyrimNativeFaceTintBuildArtifact? Artifact,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginAuthorities,
    ImmutableArray<SkyrimNativeFaceTintMaskAuthority> MaskAuthorities,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimNativeFaceTintPipelineService
{
    ValueTask<SkyrimNativeFaceTintPipelineResult> BuildAsync(
        SkyrimNativeFaceTintPipelineRequest request,
        CancellationToken cancellationToken);
}
