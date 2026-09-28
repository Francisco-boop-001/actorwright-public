using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

[Flags]
public enum PreviewHairZapParts
{
    None = 0,
    Top = 1,
    LongHair = 2,
    Both = Top | LongHair
}

public enum PreviewAssetCategory
{
    Face,
    Body,
    Hair,
    Outfit,
    Underarmor,
    Armor,
    Headwear,
    Gore,
    Accessory
}

public static class PreviewAssetCategoryExtensions
{
    public static string ToWireName(this PreviewAssetCategory category) => category switch
    {
        PreviewAssetCategory.Face => "face",
        PreviewAssetCategory.Body => "body",
        PreviewAssetCategory.Hair => "hair",
        PreviewAssetCategory.Outfit => "outfit",
        PreviewAssetCategory.Underarmor => "underarmor",
        PreviewAssetCategory.Armor => "armor",
        PreviewAssetCategory.Headwear => "headwear",
        PreviewAssetCategory.Gore => "gore",
        PreviewAssetCategory.Accessory => "accessory",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    public static bool TryParseWireName(string value, out PreviewAssetCategory category)
    {
        var normalized = value.Trim().ToLowerInvariant();
        category = normalized switch
        {
            "face" => PreviewAssetCategory.Face,
            "body" => PreviewAssetCategory.Body,
            "hair" => PreviewAssetCategory.Hair,
            "outfit" => PreviewAssetCategory.Outfit,
            "underarmor" => PreviewAssetCategory.Underarmor,
            "armor" => PreviewAssetCategory.Armor,
            "headwear" => PreviewAssetCategory.Headwear,
            "gore" => PreviewAssetCategory.Gore,
            "accessory" => PreviewAssetCategory.Accessory,
            _ => default
        };
        return normalized is "face" or "body" or "hair" or "outfit" or
            "underarmor" or "armor" or "headwear" or "gore" or "accessory";
    }
}

public enum PreviewMorphCategory
{
    Bone,
    Vertex,
    Weight,
    Sculpt
}

public static class PreviewMorphCategoryExtensions
{
    public static string ToWireName(this PreviewMorphCategory category) => category switch
    {
        PreviewMorphCategory.Bone => "bone",
        PreviewMorphCategory.Vertex => "vertex",
        PreviewMorphCategory.Weight => "weight",
        PreviewMorphCategory.Sculpt => "sculpt",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    public static bool TryParseWireName(string value, out PreviewMorphCategory category)
    {
        var normalized = value.Trim().ToLowerInvariant();
        category = normalized switch
        {
            "bone" => PreviewMorphCategory.Bone,
            "vertex" => PreviewMorphCategory.Vertex,
            "weight" => PreviewMorphCategory.Weight,
            "sculpt" => PreviewMorphCategory.Sculpt,
            _ => default
        };
        return normalized is "bone" or "vertex" or "weight" or "sculpt";
    }
}

public sealed record PreviewSceneInputAsset(
    PreviewAssetCategory Category,
    AssetPath Path,
    string Provider,
    Sha256Hash Sha256);

public sealed record PreviewSceneAsset(
    string Category,
    string Path,
    string Provider,
    string Sha256,
    bool Visible,
    bool Included);

public sealed record PreviewSceneInputMorph(
    PreviewMorphCategory Category,
    string Name,
    float Value);

public sealed record PreviewSceneMorph(
    string Category,
    string Name,
    float Value,
    bool Applied,
    bool Included);

/// <summary>
/// Explicit headwear coverage for the pinned FO4 hair-partition rule. This is
/// copied-asset preview evidence, not live engine or runtime appearance proof.
/// </summary>
public sealed record PreviewHairZapPlan(
    string Edition,
    bool RenderHeadwear,
    ImmutableArray<int> CoveredSlots,
    bool TopCovered,
    bool LongCovered,
    bool FaceGenHeadCovered = false)
{
    public PreviewHairZapParts Parts => (TopCovered ? PreviewHairZapParts.Top : PreviewHairZapParts.None) |
        (LongCovered ? PreviewHairZapParts.LongHair : PreviewHairZapParts.None);
}

public sealed record PreviewSceneInputVariant(
    string Id,
    FormReference Outfit,
    ImmutableArray<AssetPath> AssetPaths,
    ImmutableArray<PreviewSceneInputMorph> Morphs);

public sealed record PreviewSceneVariant(
    string Id,
    string Outfit,
    int IncludedAssetCount,
    int IncludedMorphCount);

public sealed record PreviewSceneCategoryCount(string Category, int Count);

public sealed record PreviewRenderedImage(
    string Path,
    string Sha256,
    int Width,
    int Height,
    int MeshCount,
    ImmutableArray<string> MorphNames = default,
    bool MorphDeformed = false,
    string? Edition = null,
    int ArmatureCount = 0,
    string? DeformationMode = null,
    string? AnimationId = null,
    bool AnimationApplied = false,
    int? AnimationFrame = null,
    bool AnimationPoseDeformed = false,
    bool HairZapApplied = false,
    bool HairZapTop = false,
    bool HairZapLong = false,
    int HairZapAffectedMeshCount = 0,
    int HairZapRemovedFaceCount = 0,
    bool FaceCullApplied = false,
    int FaceCullAffectedMeshCount = 0);

public sealed record PreviewSceneArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string InputManifestSha256,
    string SceneSha256,
    int AssetCount,
    int IncludedAssetCount,
    int VisibleAssetCount,
    ImmutableArray<PreviewSceneCategoryCount> CategoryCounts,
    ImmutableArray<PreviewSceneAsset> Assets,
    int AppliedMorphCount,
    ImmutableArray<PreviewSceneMorph> Morphs,
    PreviewSceneVariant? Variant,
    PreviewCameraPreset? Camera = null,
    PreviewLightingPreset? Lighting = null,
    PreviewSceneAnimation? Animation = null,
    PreviewHairZapPlan? HairZap = null,
    PreviewRenderedImage? RenderedImage = null);

public sealed record PreviewSceneRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    WorkspacePath OutputPath,
    ImmutableHashSet<PreviewAssetCategory>? VisibleCategories = null,
    ImmutableHashSet<PreviewMorphCategory>? MorphCategories = null,
    FormReference? Outfit = null,
    string? VariantId = null,
    string? CameraId = null,
    string? LightingId = null,
    string? AnimationId = null,
    int? AnimationFrame = null,
    float? AnimationTimeSeconds = null,
    float? AnimationPlaybackRate = null,
    bool AnimationPlaying = false,
    WorkspacePath? AssetRoot = null,
    WorkspacePath? ImageOutputPath = null,
    int ImageWidth = 512,
    int ImageHeight = 512,
    ImmutableHashSet<int>? CoveredHairSlots = null,
    bool RenderHeadwear = false,
    PreviewLightingPreset? LightingOverride = null);

public sealed record PreviewSceneResult(
    bool Written,
    PreviewSceneArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewSceneService
{
    ValueTask<PreviewSceneResult> RenderAsync(PreviewSceneRequest request, CancellationToken cancellationToken);
}

public sealed record PreviewImageRenderRequest(
    GameEdition Edition,
    WorkspacePath AssetRoot,
    WorkspacePath OutputPath,
    ImmutableArray<PreviewSceneAsset> Assets,
    int Width,
    int Height,
    ImmutableArray<PreviewSceneMorph> Morphs = default,
    PreviewSceneAnimation? Animation = null,
    PreviewHairZapPlan? HairZap = null);

public sealed record PreviewImageRenderResult(
    bool Rendered,
    PreviewRenderedImage? Image,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPreviewImageRenderer
{
    ValueTask<PreviewImageRenderResult> RenderAsync(
        PreviewImageRenderRequest request,
        CancellationToken cancellationToken);
}
