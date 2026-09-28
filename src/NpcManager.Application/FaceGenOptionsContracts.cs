using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceGenChannelResolution
{
    Inherit = -1,
    R512 = 1,
    R1024 = 2,
    R2048 = 3,
    R4096 = 4,
    R8192 = 5
}

public enum FaceGenDiffuseCompression
{
    Bc3,
    Bc7,
    Uncompressed
}

public enum FaceGenNormalSpecularCompression
{
    Bc5,
    Uncompressed,
    Bc7,
    Bc3
}

public enum FaceTintWorkingSpace
{
    Linear,
    Srgb,
    G22,
    G24
}

public enum FaceTintMaskConversion
{
    Raw,
    SrgbEncode,
    SrgbDecode,
    G22Encode,
    G22Decode,
    G24Encode,
    G24Decode
}

public enum FaceTintFramework
{
    OverPrev,
    OverBase,
    AddBase,
    ModSrc
}

public enum FaceTintSoftLightModel
{
    W3C,
    Gimp,
    Illusions,
    Pegtop
}

public enum FaceTintMaskChannel
{
    ByKind = -1,
    R,
    G,
    B,
    A
}

public enum FaceTintSeedMode
{
    BaseTexture,
    Constant
}

public enum FaceTintSkinTonePlacement
{
    Positional,
    FirstOfAll,
    LastOfAll
}

public enum FaceTintFo4SortKey
{
    GroupIndex = 1,
    OptionIndex = 2,
    TemplateIndex = 3,
    NpcListOrder = 4,
    Slot = 5,
    EntryType = 6,
    BlendOperation = 7,
    Opacity = 8,
    FlagOnOffOnly = 9,
    FlagChargenDetail = 10,
    FlagTakesSkinTone = 11,
    TemplateColorIndex = 12,
    CategoryIndex = 13
}

public enum FaceTintFo4SwapSortKey
{
    GroupIndex,
    PresetIndex,
    MorphIndex,
    Slot,
    Intensity,
    NpcListOrder
}

public enum FaceTintSseSortKey
{
    RaceOrder,
    TintIndex,
    MaskType,
    Authored,
    Coverage
}

public enum FaceTintSseOverlaySortKey
{
    OverlayIndex,
    Alpha,
    HasTint
}

public sealed record FaceTintBucketConvention(
    FaceTintWorkingSpace WorkingSpace,
    FaceTintWorkingSpace CompositeSpace,
    FaceTintWorkingSpace SourceSpace,
    FaceTintWorkingSpace OutputSpace,
    FaceTintMaskConversion MaskConversion,
    FaceTintFramework Framework,
    FaceTintSoftLightModel SoftLight,
    FaceTintMaskChannel MaskChannel);

public sealed record FaceTintBlendWorkingSpaces(
    FaceTintWorkingSpace Replace,
    FaceTintWorkingSpace Multiply,
    FaceTintWorkingSpace Overlay,
    FaceTintWorkingSpace SoftLight,
    FaceTintWorkingSpace HardLight);

public sealed record FaceTintConventionSettings(
    FaceTintBucketConvention Diffuse,
    FaceTintBucketConvention NormalSpecular,
    FaceTintBucketConvention Swap,
    FaceTintBlendWorkingSpaces DiffuseWorkingSpaceByBlend,
    FaceTintWorkingSpace DiffuseTextureSourceSpace,
    bool SeedDiffuseG22,
    FaceTintSeedMode SeedMode,
    ImmutableArray<double> SeedConstant);

public sealed record FaceTintSortRule(int Key, bool Descending);

public sealed record FaceTintSortSettings(
    ImmutableArray<FaceTintSortRule> TintRules,
    ImmutableArray<FaceTintSortRule> SwapRules,
    FaceTintSkinTonePlacement SkinTonePlacement);

public sealed record CharGenOptions(
    string SchemaVersion,
    GameEdition Edition,
    bool PerLayerResolution,
    FaceGenChannelResolution DiffuseResolution,
    FaceGenChannelResolution NormalResolution,
    FaceGenChannelResolution SpecularResolution,
    FaceGenDiffuseCompression DiffuseCompression,
    FaceGenNormalSpecularCompression NormalCompression,
    FaceGenNormalSpecularCompression SpecularCompression,
    bool GenerateTga,
    bool ApplyGhoulHeadRearFix,
    bool ApplyEyebrowsFixedColor,
    bool ApplyMouthVanillaFix,
    bool BakeSseRaceMenuOverlays,
    FaceTintConventionSettings Convention,
    FaceTintSortSettings TintSort);

public static class CharGenOptionsDefaults
{
    public static CharGenOptions For(GameEdition edition)
    {
        var isSse = edition == GameEdition.SkyrimSpecialEdition;
        var diffuse = new FaceTintBucketConvention(
            isSse ? FaceTintWorkingSpace.Linear : FaceTintWorkingSpace.G22,
            FaceTintWorkingSpace.Linear,
            isSse ? FaceTintWorkingSpace.Linear : FaceTintWorkingSpace.G22,
            isSse ? FaceTintWorkingSpace.Linear : FaceTintWorkingSpace.G22,
            isSse ? FaceTintMaskConversion.Raw : FaceTintMaskConversion.G22Encode,
            FaceTintFramework.OverPrev,
            FaceTintSoftLightModel.Gimp,
            isSse ? FaceTintMaskChannel.R : FaceTintMaskChannel.ByKind);
        var normal = isSse
            ? diffuse
            : new FaceTintBucketConvention(FaceTintWorkingSpace.Linear, FaceTintWorkingSpace.Linear,
                FaceTintWorkingSpace.Linear, FaceTintWorkingSpace.Linear, FaceTintMaskConversion.G22Encode,
                FaceTintFramework.OverPrev, FaceTintSoftLightModel.Gimp, FaceTintMaskChannel.ByKind);
        var swap = isSse
            ? diffuse
            : new FaceTintBucketConvention(FaceTintWorkingSpace.G22, FaceTintWorkingSpace.Linear,
                FaceTintWorkingSpace.Srgb, FaceTintWorkingSpace.G22, FaceTintMaskConversion.G22Encode,
                FaceTintFramework.OverPrev, FaceTintSoftLightModel.Gimp, FaceTintMaskChannel.ByKind);
        var convention = new FaceTintConventionSettings(diffuse, normal, swap,
            new FaceTintBlendWorkingSpaces(FaceTintWorkingSpace.Linear, FaceTintWorkingSpace.Linear,
                FaceTintWorkingSpace.Linear, FaceTintWorkingSpace.G22, FaceTintWorkingSpace.Linear),
            isSse ? FaceTintWorkingSpace.Linear : FaceTintWorkingSpace.Srgb,
            !isSse, isSse ? FaceTintSeedMode.Constant : FaceTintSeedMode.BaseTexture,
            [0.5, 0.5, 0.5]);
        var tintSort = isSse
            ? new FaceTintSortSettings([new FaceTintSortRule(0, false)], [new FaceTintSortRule(0, false)],
                FaceTintSkinTonePlacement.Positional)
            : new FaceTintSortSettings([new FaceTintSortRule(1, true), new FaceTintSortRule(2, true)],
                [new FaceTintSortRule(0, false), new FaceTintSortRule(1, false)],
                FaceTintSkinTonePlacement.Positional);
        return new CharGenOptions("1", edition, false,
            FaceGenChannelResolution.Inherit, FaceGenChannelResolution.Inherit, FaceGenChannelResolution.Inherit,
            FaceGenDiffuseCompression.Bc3,
            isSse ? FaceGenNormalSpecularCompression.Uncompressed : FaceGenNormalSpecularCompression.Bc5,
            FaceGenNormalSpecularCompression.Bc5, false, false, !isSse, false, isSse, convention, tintSort);
    }
}

public sealed record FaceGenOptionsRequest(
    GameEdition Edition,
    WorkspacePath Input,
    WorkspacePath? Output,
    Sha256Hash? ExpectedSha256,
    bool Apply);

public sealed record FaceGenOptionsResult(
    bool IsValid,
    bool Applied,
    GameEdition Edition,
    WorkspacePath Input,
    WorkspacePath? Output,
    Sha256Hash? InputSha256,
    Sha256Hash? OutputSha256,
    CharGenOptions? Options,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenOptionsService
{
    ValueTask<FaceGenOptionsResult> ValidateAsync(FaceGenOptionsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Writes one already accepted complete options document directly to a new
/// canonical K-local artifact, then reopens the exact promoted bytes.
/// </summary>
public sealed record FaceGenOptionsDocumentWriteRequest(
    GameEdition Edition,
    CharGenOptions Options,
    WorkspacePath Output);

public sealed record FaceGenOptionsDocumentWriteResult(
    bool Written,
    bool ReadbackVerified,
    GameEdition Edition,
    WorkspacePath Output,
    Sha256Hash? OutputSha256,
    CharGenOptions? Options,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenOptionsDocumentWriter
{
    ValueTask<FaceGenOptionsDocumentWriteResult> WriteAsync(
        FaceGenOptionsDocumentWriteRequest request,
        CancellationToken cancellationToken);
}
