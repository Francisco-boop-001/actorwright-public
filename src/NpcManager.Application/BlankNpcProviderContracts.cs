using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Binds every admitted appearance-provider component to one hash-pinned
/// identity. Individual component hashes alone cannot establish that a
/// template, FaceGeom, FaceTint source, and dependency contract belong
/// together.
/// </summary>
public sealed record BlankNpcProviderBindingRequest(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256,
    GameEdition Edition,
    NpcSex Sex,
    WorkspacePath TemplatePlugin,
    Sha256Hash ExpectedTemplatePluginSha256,
    FormId TemplateNpcFormId,
    WorkspacePath FaceGeomCarrier,
    Sha256Hash ExpectedFaceGeomCarrierSha256,
    WorkspacePath FaceTintManifest,
    WorkspacePath FaceTintProviderRoot,
    WorkspacePath DependencyManifest)
{
    public ProviderResourceAuthoritySet? ProviderResources { get; init; }
}

/// <summary>
/// Closed, verified provider identity consumed by every build stage. The
/// template master list is exact and intentionally separate from a preset's
/// broader source-provenance dependencies.
/// </summary>
public sealed record BlankNpcProviderArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string ProviderId,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    GameEdition Edition,
    NpcSex Sex,
    ImmutableArray<PluginName> TemplateMasters,
    Sha256Hash ExpectedFaceGeomGraphSha256,
    ImmutableArray<string> ExpectedShapeNames,
    AssetPath FaceTintSourceAssetPath,
    Sha256Hash FaceTintSourceAssetSha256,
    Sha256Hash FaceTintManifestSha256,
    string DependencyId,
    Sha256Hash DependencyManifestSha256,
    int HeadPartCount,
    int LooseAssetCount,
    int ArchiveCount)
{
    public ProviderResourceAuthoritySet? ProviderResources { get; init; }
}

public sealed record BlankNpcProviderBindingResult(
    bool Qualified,
    BlankNpcProviderArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IBlankNpcProviderService
{
    ValueTask<BlankNpcProviderBindingResult> QualifyAsync(
        BlankNpcProviderBindingRequest request,
        CancellationToken cancellationToken);
}
