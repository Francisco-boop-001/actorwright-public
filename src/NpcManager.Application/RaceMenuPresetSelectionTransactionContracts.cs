using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Immutable input for replacing only preset-owned authority on an already
/// reviewed execution request. The current request remains the rollback value.
/// </summary>
public sealed record RaceMenuPresetSelectionTransactionRequest(
    RaceMenuNpcExecutionRequest CurrentRequest,
    PresetDocument SelectedPreset,
    RaceMenuPresetCompanionExport Companion,
    RaceMenuPresetTarget Target,
    bool ApplyBodySlide,
    WorkspacePath CandidateParent)
{
    /// <summary>
    /// Exact external NIF/TRI/DDS winners retained from the Manager-owned
    /// native companion bake. Generic companion-selection callers leave this
    /// empty and do not claim selected-preset dependency evidence.
    /// </summary>
    public ImmutableArray<SkyrimAssetAuthority> FaceGenDependencyAuthorities
    { get; init; } = [];

    public ImmutableArray<SkyrimAssetAuthority>
        FaceGenProviderSidecarAuthorities
    { get; init; } = [];

    /// <summary>
    /// Manager-native external-provider evidence emitted by the companion
    /// bake. These arrays are transaction-local and are never read from the
    /// prepared execution-request JSON.
    /// </summary>
    [JsonIgnore]
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>
        ExternalHeadPartDependencies { get; init; } = [];

    [JsonIgnore]
    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
        ExternalHeadPartExclusionAttestations { get; init; } = [];

    [JsonIgnore]
    public RaceMenuManagerOwnedFaceGeomCarrierAuthority?
        ManagerOwnedFaceGeomCarrier { get; init; }

    /// <summary>
    /// Optional fully hash-bound schema-3 groups. A caller that has not yet
    /// materialized an output plugin leaves this empty; external admission is
    /// then refused closed rather than serializing an unbound manifest.
    /// </summary>
    [JsonIgnore]
    public ImmutableArray<
        RaceMenuSelectedDependencyManifestExternalInstallDependency>
        ExternalInstallDependencies { get; init; } = [];

    /// <summary>
    /// Opaque JSlot-only first-phase marker. The singleton value is owned by
    /// the Pipeline assembly and is never accepted from prepared request
    /// JSON; a valid probe emits no selected dependency manifest until the
    /// output plugin is reopened.
    /// </summary>
    [JsonIgnore]
    public RaceMenuJslotOutputBindingProbeToken?
        JslotOutputBindingProbe { get; init; }
}

public sealed record RaceMenuPresetSelectionTransactionResult(
    bool Committed,
    RaceMenuNpcExecutionRequest? CandidateRequest,
    WorkspacePath? CandidateRoot,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetSelectionTransactionService
{
    ValueTask<RaceMenuPresetSelectionTransactionResult> RebindAsync(
        RaceMenuPresetSelectionTransactionRequest request,
        CancellationToken cancellationToken);
}
