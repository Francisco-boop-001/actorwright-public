using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Complete typed input consumed by the Manager's temporary NPC writer and
/// native FaceGen baker. This is staging authority only; it does not claim that
/// Skyrim rendered the resulting actor.
/// </summary>
public sealed record RaceMenuJslotCompanionAppearance(
    FullyAuthoredSkyrimNpcAppearanceSource Appearance,
    SkyrimFaceGenSidecarOverlay SidecarOverlay);

/// <summary>
/// Exact inputs for manufacturing same-stem CharGen companions from a JSlot.
/// The copied Data root is read-only except for one new, transaction-owned
/// temporary plugin which is hash-deleted after the native bake.
/// </summary>
public sealed record RaceMenuJslotCompanionBuildRequest(
    RaceMenuNpcExecutionRequest CurrentRequest,
    PresetDocument Preset,
    WorkspacePath PresetPath,
    RaceMenuPresetTarget Target,
    WorkspacePath CompanionRoot)
{
    /// <summary>
    /// Manager-only precheck receipt. Native FaceGen receives the descriptor as
    /// an expectation, but still rediscovers it from the authoritative route.
    /// </summary>
    [JsonIgnore]
    public RaceMenuJslotExternalHeadPartPrecheckAcceptance?
        AcceptedExternalHeadPartPrecheck { get; init; }
}

public sealed record RaceMenuJslotCompanionBuildResult(
    bool Completed,
    WorkspacePath? CompanionRoot,
    RaceMenuPresetCompanionExport? Companion,
    NpcCreationResult? TemporaryNpc,
    FaceGenNpcBakeResult? FaceGen,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuJslotCompanionBuildService
{
    ValueTask<RaceMenuJslotCompanionBuildResult> BuildAsync(
        RaceMenuJslotCompanionBuildRequest request,
        CancellationToken cancellationToken);
}

public enum RaceMenuJslotExternalHeadPartPrecheckStatus
{
    NotApplicable = 0,
    Accepted = 1,
    Refused = 2
}

public sealed record RaceMenuJslotExternalHeadPartPrecheckRequest(
    WorkspacePath PresetPath,
    Sha256Hash ExpectedPresetSha256,
    RaceMenuPresetTarget Target,
    ExternalHeadPartDependencyDescriptor? ExpectedDescriptor);

public sealed record RaceMenuJslotExternalHeadPartPrecheckAcceptance(
    Sha256Hash PresetSha256,
    RaceMenuPresetTarget ReviewedTarget,
    ExternalHeadPartDependencyDescriptor Descriptor);

public sealed record RaceMenuJslotExternalHeadPartPrecheckResult(
    RaceMenuJslotExternalHeadPartPrecheckStatus Status,
    RaceMenuJslotExternalHeadPartPrecheckAcceptance? Acceptance,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuJslotExternalHeadPartPrecheckService
{
    ValueTask<RaceMenuJslotExternalHeadPartPrecheckResult> PrecheckAsync(
        RaceMenuJslotExternalHeadPartPrecheckRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional additive seam for the native FaceGen facade. The base bake request
/// predates external-provider admission and remains wire-compatible; the JSlot
/// companion uses this interface when the concrete baker supports expected
/// descriptor propagation.
/// </summary>
public interface IRaceMenuExternalDescriptorFaceGenNpcBakeService
{
    ValueTask<FaceGenNpcBakeResult> BakeAsync(
        FaceGenNpcBakeRequest request,
        ExternalHeadPartDependencyDescriptor? expectedDescriptor,
        CancellationToken cancellationToken);
}

/// <summary>
/// One Manager-only JSlot-to-NPC transaction. Plugin names are explicit and
/// ascending. The final NPC intent comes from the already reviewed request;
/// only preset-owned appearance and weight are rebound.
/// </summary>
public sealed record RaceMenuJslotNpcBuildRequest(
    RaceMenuNpcExecutionRequest CurrentRequest,
    WorkspacePath Preset,
    Sha256Hash ExpectedPresetSha256,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath CompanionRoot)
{
    public WorkspacePath? SourceRequest { get; init; }

    public Sha256Hash? SourceRequestSha256 { get; init; }

    public NpcBuildPreflightReviewAuthority? ReviewedPreflight { get; init; }

    /// <summary>
    /// Manager-only receipt accepted before companion creation. Prepared
    /// request JSON and command options cannot supply this authority.
    /// </summary>
    [JsonIgnore]
    public RaceMenuJslotExternalHeadPartPrecheckAcceptance?
        AcceptedExternalHeadPartPrecheck { get; init; }

    /// <summary>
    /// Optional output-plugin-bound schema-3 groups supplied by a typed
    /// Manager caller. They are never accepted from prepared request JSON.
    /// </summary>
    [JsonIgnore]
    public ImmutableArray<
        RaceMenuSelectedDependencyManifestExternalInstallDependency>
        ExternalInstallDependencies { get; init; } = [];
}

public sealed record RaceMenuJslotNpcBuildResult(
    bool Completed,
    PresetDocument? Preset,
    RaceMenuPresetTarget? Target,
    RaceMenuJslotCompanionBuildResult? CompanionBuild,
    RaceMenuPresetSelectionTransactionResult? Selection,
    RaceMenuNpcExecutionResult? Execution,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuJslotNpcBuildService
{
    ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
        RaceMenuJslotNpcBuildRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken);
}
