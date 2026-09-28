using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceGenNpcBakeStatus
{
    Baked,
    Skipped,
    Failed
}

public sealed record FaceGenNpcBakeRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FaceGenBakeTarget Target,
    WorkspacePath OutputDataRoot,
    SkyrimFaceGenSidecarOverlay? SidecarOverlay = null)
{
    public SseFaceGeomCarrierSkeletonAuthority SkeletonAuthority
    { get; init; } =
        SseFaceGeomCarrierSkeletonAuthority.SourceModelWorldTranslations;

    public ImmutableArray<SkyrimNativeFaceGeomExternalHeadPart>
        NativeFaceGeomExternalHeadParts { get; init; } = [];

    public uint? EffectiveHairColorPackedRgb { get; init; }

    public ImmutableArray<SkyrimFaceRecordPluginAuthority>
        StagedPluginAuthorities { get; init; } = [];
}

public sealed record FaceGenNpcBakeArtifact(
    FaceGenBakeTarget Target,
    WorkspacePath FaceGeomNif,
    Sha256Hash FaceGeomSha256,
    int FaceGeomByteLength,
    WorkspacePath FaceTintDds,
    Sha256Hash FaceTintSha256,
    int FaceTintByteLength,
    bool RuntimeAuthority)
{
    /// <summary>
    /// Hash-bound external providers consumed by the Manager-owned native
    /// FaceGeom transaction. The final package can retain this provenance
    /// without copying the provider assets.
    /// </summary>
    public ImmutableArray<SkyrimAssetAuthority> ExternalDependencyAuthorities
    { get; init; } = [];

    public ImmutableArray<SkyrimAssetAuthority>
        ExternalProviderSidecarAuthorities
    { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ExternalHeadPartDependencyDescriptor>?
        ExternalHeadPartDependencies { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>?
        ExternalHeadPartExclusionAttestations { get; init; }
}

public sealed record FaceGenNpcBakeResult(
    FaceGenNpcBakeStatus Status,
    FaceGenBakeTarget Target,
    FaceGenNpcBakeArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenNpcBakeService
{
    ValueTask<FaceGenNpcBakeResult> BakeAsync(
        FaceGenNpcBakeRequest request,
        CancellationToken cancellationToken);
}

public enum FaceGenBakeAllStatus
{
    Succeeded = 0,
    Fatal = 1,
    SomeFailed = 2,
    Cancelled = 3
}

public enum FaceGenBakeAllProgressPhase
{
    Discovering,
    Baking,
    Completed,
    Cancelled,
    Fatal
}

public sealed record FaceGenBakeAllProgress(
    int Sequence,
    FaceGenBakeAllProgressPhase Phase,
    int Completed,
    int Total,
    FaceGenBakeTarget? Target,
    string Message);

public sealed record FaceGenBakeAllRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath OutputDataRoot,
    PluginName? WinningPlugin = null);

public sealed record FaceGenBakeAllResult(
    FaceGenBakeAllStatus Status,
    int Discovered,
    int Baked,
    int Skipped,
    int Failed,
    ImmutableArray<FaceGenNpcBakeResult> Outcomes,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public int ExitCode => (int)Status;
}

public interface IFaceGenBakeAllService
{
    ValueTask<FaceGenBakeAllResult> RunAsync(
        FaceGenBakeAllRequest request,
        IProgress<FaceGenBakeAllProgress>? progress,
        CancellationToken cancellationToken);
}
