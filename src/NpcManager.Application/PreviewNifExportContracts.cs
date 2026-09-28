using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Requests a sandbox-only export plan from a completed semantic scene artifact.</summary>
public sealed record PreviewNifExportRequest(
    GameEdition Edition,
    WorkspacePath ScenePath,
    WorkspacePath OutputPath);

public sealed record PreviewNifExportAsset(
    string Category,
    string Path,
    string Provider,
    string Sha256);

public sealed record PreviewNifExportMorph(string Name, float Value);

public sealed record PreviewNifExportDependency(string Path, string Sha256);

/// <summary>Describes the exact scene inputs a future NIF writer must consume.</summary>
public sealed record PreviewNifExportArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string InputSceneSha256,
    string SourceScenePath,
    string TargetFormat,
    ImmutableArray<PreviewNifExportAsset> Assets,
    PreviewCameraPreset? Camera,
    PreviewLightingPreset? Lighting,
    PreviewSceneAnimation? Animation,
    PreviewHairZapPlan? HairZap = null);

public sealed record PreviewNifExportResult(
    bool Written,
    PreviewNifExportArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewNifExportService
{
    ValueTask<PreviewNifExportResult> ExportPlanAsync(PreviewNifExportRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Requests a hash-bound, K-local binary NIF export from copied scene assets.</summary>
public sealed record PreviewNifBinaryExportRequest(
    GameEdition Edition,
    WorkspacePath ScenePath,
    WorkspacePath AssetRoot,
    WorkspacePath OutputPath);

public sealed record PreviewNifBinaryExportArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string SceneSha256,
    string OutputPath,
    string OutputSha256,
    long ByteLength,
    int MeshCount,
    ImmutableArray<PreviewNifExportAsset> SourceAssets,
    string Exporter,
    string ImportMode,
    ImmutableArray<PreviewNifExportMorph> Morphs = default,
    bool MorphDeformed = false,
    string? BaseVertexSha256 = null,
    string? BakedVertexSha256 = null,
    ImmutableArray<PreviewNifExportDependency> MorphDependencies = default,
    int ArmatureCount = 0,
    string? DeformationMode = null,
    PreviewHairZapPlan? HairZap = null,
    bool HairZapApplied = false,
    int HairZapAffectedMeshCount = 0,
    int HairZapRemovedFaceCount = 0,
    bool FaceCullApplied = false,
    int FaceCullAffectedMeshCount = 0);

public sealed record PreviewNifBinaryExportResult(
    bool Written,
    PreviewNifBinaryExportArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewNifBinaryExportService
{
    ValueTask<PreviewNifBinaryExportResult> ExportAsync(
        PreviewNifBinaryExportRequest request,
        CancellationToken cancellationToken);
}
