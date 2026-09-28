using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PresetToNpcPipelineRequest(
    PresetFormat Format,
    GameEdition Edition,
    WorkspacePath PresetPath,
    WorkspacePath SourcePlugin,
    PluginName OutputPlugin,
    FormId TargetFormId,
    string ModName,
    WorkspacePath OutputRoot,
    EditorId? OutputEditorId,
    NpcName? OutputName,
    WorkspacePath? FaceGeomManifest = null,
    WorkspacePath? FaceTintManifest = null,
    WorkspacePath? RuntimeScriptBuild = null,
    WorkspacePath? RuntimeScriptPackage = null);

public sealed record PresetToNpcPackageArtifact(
    string Kind,
    AssetPath RelativePath,
    int ByteLength,
    Sha256Hash Sha256);

public sealed record PresetToNpcPackageManifest(
    int SchemaVersion,
    string Edition,
    string PresetFormat,
    string SourcePreset,
    Sha256Hash SourcePresetSha256,
    string SourcePlugin,
    Sha256Hash SourcePluginSha256,
    string OutputPlugin,
    FormId TargetFormId,
    ImmutableArray<PresetToNpcPackageArtifact> Artifacts);

public sealed record PresetToNpcPipelineResult(
    bool Completed,
    PresetDocument? Preset,
    NpcMutationProposal? MutationProposal,
    NpcMutationResult? MutationResult,
    BodyGenBuildResult? BodyGenResult,
    WorkspacePath? ManifestPath,
    Sha256Hash? ManifestHash,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ImmutableArray<PresetToNpcPackageArtifact> PackageArtifacts { get; init; } = [];
}

public interface IPresetToNpcPipeline
{
    ValueTask<PresetToNpcPipelineResult> ExecuteAsync(PresetToNpcPipelineRequest request, CancellationToken cancellationToken);
}
