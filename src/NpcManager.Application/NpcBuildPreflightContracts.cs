using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class NpcBuildPreflightSchemas
{
    public const string Artifact = "actorwright-npc-build-preflight/1";
    public const int DerivationVersion = 1;
}

public sealed record NpcBuildPreflightAuthority(
    string Role,
    string Origin,
    string Identity,
    Sha256Hash Sha256);

public sealed record NpcBuildPreflightGate(
    string Id,
    bool Required,
    bool Passed,
    string Detail);

public sealed record NpcBuildPreflightHeadPart(
    int SourceIndex,
    string SourceType,
    string EffectiveType,
    string Disposition,
    string Provider,
    string GeometryStatus,
    string VertexLayoutStatus,
    string TopologyStatus,
    string PackedNormalStatus);

public sealed record NpcBuildPreflightAppearanceFact(
    string Field,
    string ProviderValue,
    string EffectiveValue,
    string Authority);

public sealed record NpcBuildPreflightPlannedOutput(
    string Role,
    string Path);

public sealed record NpcBuildPreflightDependency(
    string Kind,
    string Path,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Disposition = null);

public sealed record NpcBuildPreflightArtifact(
    string Schema,
    string Product,
    string ProductVersion,
    string SourceLine,
    Sha256Hash? ExecutableSha256,
    int DerivationVersion,
    WorkspacePath SourceRequest,
    Sha256Hash SourceRequestSha256,
    Sha256Hash PresetSha256,
    string Race,
    string Sex,
    ImmutableArray<string> WinningPluginOrder,
    ImmutableArray<NpcBuildPreflightAuthority> Authorities,
    ImmutableArray<NpcBuildPreflightHeadPart> HeadParts,
    ImmutableArray<NpcBuildPreflightAppearanceFact> Appearance,
    ImmutableArray<NpcBuildPreflightAuthority> FinalDependencyClosure,
    ImmutableArray<NpcBuildPreflightGate> RequiredGates,
    ImmutableArray<NpcBuildPreflightGate> OptionalPreview,
    ImmutableArray<NpcBuildPreflightPlannedOutput> PlannedOutputs,
    bool ReadyForBuild,
    bool PreviewReady,
    bool RuntimeAuthority,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<NpcBuildPreflightDependency>? DependencyClosure = null);

public sealed record NpcBuildPreflightDocument(
    NpcBuildPreflightArtifact Value,
    ImmutableArray<byte> Utf8Json,
    Sha256Hash Sha256,
    WorkspacePath? Path = null);

public sealed record NpcBuildPreflightReviewAuthority(
    WorkspacePath Path,
    Sha256Hash ExpectedSha256);

public sealed record NpcBuildPreflightRequest(
    RaceMenuNpcExecutionRequest EffectiveRequest,
    WorkspacePath SourceRequest,
    Sha256Hash SourceRequestSha256,
    WorkspacePath Preset,
    Sha256Hash ExpectedPresetSha256,
    WorkspacePath? DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath? CompanionRoot,
    WorkspacePath? Output = null,
    WorkspacePath? FaceBakeAuthorityOutput = null);

public sealed record NpcBuildPreflightResult(
    bool Created,
    bool ReadyForBuild,
    NpcBuildPreflightDocument? Document,
    ImmutableArray<Diagnostic> Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SkyrimFaceBakeAuthorityDerivationArtifact? FaceBakeAuthority = null);

public interface INpcBuildPreflightDocumentCodec
{
    NpcBuildPreflightDocument Encode(NpcBuildPreflightArtifact artifact);

    ValueTask<NpcBuildPreflightDocument> WriteNewAsync(
        NpcBuildPreflightDocument document,
        WorkspacePath output,
        CancellationToken cancellationToken);

    ValueTask<NpcBuildPreflightDocument> ReadExactAsync(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        CancellationToken cancellationToken);
}

public interface INpcBuildPreflightService
{
    ValueTask<NpcBuildPreflightResult> CreateAsync(
        NpcBuildPreflightRequest request,
        CancellationToken cancellationToken);

    ValueTask<NpcBuildPreflightResult> VerifyReviewedAsync(
        NpcBuildPreflightRequest request,
        NpcBuildPreflightReviewAuthority reviewed,
        CancellationToken cancellationToken);
}
