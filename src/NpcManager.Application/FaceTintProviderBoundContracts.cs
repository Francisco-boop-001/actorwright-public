using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Binds one semantic FaceTint build to the canonical FaceGen provider selected
/// by an explicit copied-Data load order. The generated DDS is still sandbox
/// output; plugin loadability and runtime rendering remain separate authorities.
/// </summary>
public sealed record FaceTintProviderBoundBuildRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormId NpcFormId,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath ManifestPath,
    WorkspacePath OutputPath,
    WorkspacePath OutputRoot,
    FaceTintOutputFormat? Format = null,
    int? MipCount = null,
    FaceTintAlphaMode? AlphaMode = null);

public sealed record FaceTintProviderBoundBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string LocalFormId,
    string OriginatingPlugin,
    string WinningPlugin,
    ImmutableArray<PluginName> OverrideChain,
    string ProviderKind,
    AssetPath ProviderPath,
    AssetPath OutputPath,
    string ProviderSha256,
    FaceTintBuildArtifact BuildArtifact);

public sealed record FaceTintProviderBoundBuildResult(
    bool Written,
    FaceTintProviderBoundBuildArtifact? Artifact,
    Sha256Hash? OutputSha256,
    Sha256Hash? TextureOutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceTintProviderBoundBuildService
{
    ValueTask<FaceTintProviderBoundBuildResult> BuildAsync(
        FaceTintProviderBoundBuildRequest request, CancellationToken cancellationToken);
}
