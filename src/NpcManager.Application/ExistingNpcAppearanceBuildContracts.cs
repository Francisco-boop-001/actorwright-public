using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Complete static product request for applying an authored appearance to one
/// NPC owned by an existing Skyrim plugin. Game-facing FaceGen and BodyGen
/// destinations remain keyed to the source owner, never to the override ESP.
/// </summary>
public sealed record ExistingNpcAppearanceBuildRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    Sha256Hash ExpectedSourcePluginSha256,
    FormId TargetFormId,
    WorkspacePath OutputRoot,
    PluginName OutputPlugin,
    FormReference Race,
    NpcSex Sex,
    FullyAuthoredSkyrimNpcAppearanceSource Appearance,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
    ExactNifBlankNpcFaceGeomSource FaceGeomSource,
    ExactDdsBlankNpcFaceTintSource FaceTintSource,
    ImmutableArray<BodyGenMorph> BodyMorphs,
    ImmutableArray<BlankNpcTransitivePackageAsset> TransitivePackageAssets)
{
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];
}

/// <summary>Durable, origin-keyed output of the existing-NPC appearance build.</summary>
public sealed record ExistingNpcAppearanceBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Verdict,
    WorkspacePath OutputRoot,
    WorkspacePath Plugin,
    Sha256Hash PluginSha256,
    PluginName SourceOwnerPlugin,
    FormId SourceOwnerFormId,
    WorkspacePath FaceGeom,
    Sha256Hash FaceGeomSha256,
    WorkspacePath FaceTint,
    Sha256Hash FaceTintSha256,
    WorkspacePath Manifest,
    Sha256Hash ManifestSha256,
    bool RuntimeAuthority);

public sealed record ExistingNpcAppearanceBuildResult(
    bool Completed,
    ExistingNpcAppearanceBuildArtifact? Artifact,
    NpcAppearanceOverrideResult? AppearanceOverride,
    BodyGenBuildResult? BodyGen,
    QualifiedFaceGeomCarrierMaterializationResult? FaceGeom,
    FaceTintTextureDecodeResult? FaceTintReadback,
    PackageVerifyResult? PackageVerification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IExistingNpcAppearanceBuildService
{
    ValueTask<ExistingNpcAppearanceBuildResult> ExecuteAsync(
        ExistingNpcAppearanceBuildRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken);
}
