using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum NpcVisualPreviewRoute
{
    Cbbe3Ba,
    Cotr,
    Ube
}

public enum NpcVisualAssetRole
{
    FaceGeom,
    FaceTint,
    Body,
    Hands,
    Feet,
    Outfit,
    Hair,
    Eyes,
    Skeleton,
    Tri,
    Texture
}

public sealed record NpcVisualPreviewPackageOverlay(
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256);

public sealed record NpcVisualPreviewOptions(
    bool RenderBody = true,
    bool RenderOutfit = true,
    bool AlternateLighting = true,
    int Width = 900,
    int Height = 900);

public sealed record NpcVisualPreviewComposeRequest(
    ReviewedGameIntake Intake,
    SkyrimMainWorkspaceIdentity Identity,
    NpcVisualPreviewPackageOverlay? PackageOverlay,
    NpcVisualPreviewOptions Options,
    WorkspacePath OutputRoot);

public sealed record NpcVisualTextureSlot(
    int Slot,
    string Semantic,
    AssetPath AssetPath,
    string Provider,
    Sha256Hash Sha256,
    WorkspacePath MaterializedPath);

public sealed record NpcVisualMaterial(
    string Shape,
    uint ShaderType,
    ImmutableArray<NpcVisualTextureSlot> TextureSlots,
    uint ShaderFlags1,
    uint ShaderFlags2,
    bool HasAlpha,
    float Alpha,
    string? TintHex);

public sealed record NpcVisualAsset(
    NpcVisualAssetRole Role,
    AssetPath AssetPath,
    string Provider,
    Sha256Hash Sha256,
    long Bytes,
    WorkspacePath MaterializedPath,
    bool BakedIntoFaceGeom,
    ImmutableArray<NpcVisualMaterial> Materials,
    uint BipedSlotMask = 0,
    AssetPath? LowWeightAssetPath = null,
    Sha256Hash? LowWeightSha256 = null,
    WorkspacePath? LowWeightMaterializedPath = null);

public sealed record NpcVisualMorph(
    string Source,
    string Name,
    float Value,
    string? Provider = null,
    AssetPath? AssetPath = null,
    Sha256Hash? Sha256 = null);

public sealed record NpcVisualSourceGraph(
    NpcVisualPreviewRoute Route,
    SkyrimMainWorkspaceIdentity Identity,
    NpcSex Sex,
    float Weight,
    string Race,
    string? HairColorHex,
    string? SkinTintHex,
    ImmutableArray<NpcVisualAsset> Assets,
    ImmutableArray<NpcVisualMorph> Morphs,
    bool HasDeclaredOutfit,
    ImmutableArray<Diagnostic> Diagnostics,
    float SkinTintAlpha = 1);

public sealed record NpcVisualPreviewRenderRequest(
    string SceneSchemaVersion,
    NpcVisualSourceGraph Source,
    WorkspacePath OutputRoot,
    NpcVisualPreviewOptions Options);

public sealed record NpcVisualPreviewView(
    string Id,
    WorkspacePath ImagePath,
    Sha256Hash ImageSha256,
    WorkspacePath RoleMaskPath,
    Sha256Hash RoleMaskSha256,
    int Width,
    int Height);

public sealed record NpcVisualPreviewImportedMesh(
    NpcVisualAssetRole Role,
    AssetPath AssetPath,
    string ObjectName,
    int VertexCount,
    ImmutableArray<string> Materials,
    ImmutableArray<double> WorldTransform);

public sealed record NpcVisualPreviewImportedMaterial(
    string ObjectName,
    string MaterialName,
    ImmutableDictionary<string, string> TextureBindings,
    ImmutableArray<string> LoadedImages,
    string BlendMethod,
    bool HasAlpha);

public sealed record NpcVisualPreviewRenderEvidence(
    string BlenderVersion,
    string RenderEngine,
    int FaceGeomImportCount,
    bool FaceCameraUsedAuthoritativeGeometry,
    int ArmatureCount,
    ImmutableArray<string> Skeletons,
    int FallbackMaterialCount,
    ImmutableDictionary<string, int> MaterialApplicationCounts,
    ImmutableDictionary<string, long> RoleMaskPixelCounts,
    ImmutableDictionary<string, double> RoleMeanLuminance,
    ImmutableArray<NpcVisualPreviewImportedMesh> Meshes,
    ImmutableArray<NpcVisualPreviewImportedMaterial> Materials,
    WorkspacePath StatusPath,
    Sha256Hash StatusSha256);

public sealed record NpcVisualPreviewVisualEvidence(
    int DetectedFaceCount,
    double DetectorScore,
    int LandmarkCount,
    int SemanticAnchorCount,
    bool EyesNoseAndMouthBounded,
    ImmutableArray<Diagnostic> Diagnostics);

public static class NpcVisualPreviewPersistenceContract
{
    public const string BundleSchema = "npc-preview-bundle/1";
    public const string SceneSchema = "npc-preview-scene/2";
    public const string OffEngineLabel =
        "High-fidelity off-engine preview — Skyrim runtime remains authoritative";

    public static ImmutableArray<string> RequiredViewIds { get; } =
    [
        "face-front",
        "face-left",
        "face-right",
        "face-alternate-light",
        "body-front",
        "body-back"
    ];
}

public sealed record NpcVisualPreviewPersistenceDocument(
    string SchemaVersion,
    string SceneSchemaVersion,
    string Label,
    bool RuntimeAuthority,
    NpcVisualSourceGraph Source,
    ImmutableArray<NpcVisualPreviewView> Views,
    WorkspacePath ContactSheetPath,
    Sha256Hash ContactSheetSha256,
    NpcVisualPreviewRenderEvidence RenderEvidence,
    NpcVisualPreviewVisualEvidence VisualEvidence,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record NpcVisualPreviewRenderResult(
    bool Rendered,
    ImmutableArray<NpcVisualPreviewView> Views,
    WorkspacePath? ContactSheetPath,
    Sha256Hash? ContactSheetSha256,
    NpcVisualPreviewRenderEvidence? Evidence,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcVisualPreviewRenderer
{
    ValueTask<NpcVisualPreviewRenderResult> RenderAsync(
        NpcVisualPreviewRenderRequest request,
        CancellationToken cancellationToken);
}

public interface INpcVisualPreviewVisualValidator
{
    ValueTask<NpcVisualPreviewVisualEvidence> ValidateAsync(
        NpcVisualPreviewView faceFront,
        CancellationToken cancellationToken);

    ValueTask<NpcVisualPreviewVisualEvidence>
        ValidateEncodedAsync(
            NpcVisualPreviewView faceFront,
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken);
}

public enum NpcVisualComparisonKind
{
    Reference,
    SkyrimRuntime
}

public sealed record NpcVisualComparisonRequest(
    WorkspacePath PreviewImagePath,
    Sha256Hash ExpectedPreviewImageSha256,
    WorkspacePath ComparisonImagePath,
    Sha256Hash ExpectedComparisonImageSha256,
    string ComparisonLabel,
    NpcVisualComparisonKind Kind,
    WorkspacePath OutputRoot);

public sealed record NpcVisualComparisonTransform(
    double Scale,
    double RotationRadians,
    double TranslationX,
    double TranslationY);

public sealed record NpcVisualComparisonResult(
    bool Produced,
    WorkspacePath? AlignedComparisonPath,
    Sha256Hash? AlignedComparisonSha256,
    WorkspacePath? SideBySidePath,
    Sha256Hash? SideBySideSha256,
    WorkspacePath? OverlayPath,
    Sha256Hash? OverlaySha256,
    WorkspacePath? HeatmapPath,
    Sha256Hash? HeatmapSha256,
    WorkspacePath? BlinkPreviewPath,
    Sha256Hash? BlinkPreviewSha256,
    WorkspacePath? BlinkComparisonPath,
    Sha256Hash? BlinkComparisonSha256,
    WorkspacePath? EvidencePath,
    Sha256Hash? EvidenceSha256,
    NpcVisualComparisonTransform? Transform,
    double MeanAbsoluteRgbDifference,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcVisualComparisonService
{
    ValueTask<NpcVisualComparisonResult> CompareAsync(
        NpcVisualComparisonRequest request,
        CancellationToken cancellationToken);
}

public sealed record NpcVisualPreviewBundle(
    string SchemaVersion,
    string SceneSchemaVersion,
    string Label,
    bool RuntimeAuthority,
    NpcVisualSourceGraph Source,
    ImmutableArray<NpcVisualPreviewView> Views,
    WorkspacePath ContactSheetPath,
    Sha256Hash ContactSheetSha256,
    WorkspacePath BundlePath,
    Sha256Hash BundleSha256,
    WorkspacePath HashManifestPath,
    Sha256Hash HashManifestSha256,
    NpcVisualPreviewRenderEvidence RenderEvidence,
    NpcVisualPreviewVisualEvidence VisualEvidence,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record NpcVisualPreviewComposeResult(
    bool Composed,
    NpcVisualPreviewBundle? Bundle,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcVisualPreviewComposer
{
    ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
        NpcVisualPreviewComposeRequest request,
        CancellationToken cancellationToken);
}

public interface INpcVisualSourceComposer
{
    ValueTask<NpcVisualSourceComposeResult> ComposeSourceAsync(
        NpcVisualPreviewComposeRequest request,
        CancellationToken cancellationToken);
}

public sealed record NpcVisualSourceComposeResult(
    bool Composed,
    NpcVisualSourceGraph? Source,
    ImmutableArray<Diagnostic> Diagnostics);
