using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ArmorAddonProposalMode
{
    New,
    Override
}

public sealed record ArmorAddonSculptProposal(byte Gender, string BoneName, double DeltaX, double DeltaY, double DeltaZ);

public sealed record ArmorAddonProposalPatch(
    EditorId? EditorId,
    uint? SlotMask,
    FormReference? Race,
    FormReference? FootstepSet,
    byte? MalePriority,
    byte? FemalePriority,
    byte? MaleWeightSliderFlags,
    byte? FemaleWeightSliderFlags,
    byte? DetectionSound,
    double? WeaponAdjust,
    string? MaleModel,
    string? FemaleModel,
    string? MaleFirstPersonModel,
    string? FemaleFirstPersonModel,
    byte? MaleModelFlags,
    byte? FemaleModelFlags,
    double? MaleColorRemapIndex,
    double? FemaleColorRemapIndex,
    FormReference? MaleSkinTexture,
    FormReference? FemaleSkinTexture,
    FormReference? MaleSkinTextureSwapList,
    FormReference? FemaleSkinTextureSwapList,
    FormReference? MaleMaterialSwap,
    FormReference? FemaleMaterialSwap,
    FormReference? MaleFirstPersonMaterialSwap,
    FormReference? FemaleFirstPersonMaterialSwap,
    FormReference? ArtObject,
    ImmutableArray<FormReference>? AdditionalRaces,
    ImmutableArray<ArmorAddonSculptProposal>? Sculpt,
    bool? NoUnderarmorScaling,
    bool? HasSculptData,
    bool? HiResFirstPersonOnly);

public sealed record ArmorAddonProposalRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    ArmorAddonProposalMode Mode,
    ArmorAddonProposalPatch Patch,
    WorkspacePath OutputProposal,
    FormId? TargetFormId = null,
    bool CompleteDocument = false,
    bool SeedFromSource = false,
    PluginName? TargetPlugin = null);

public sealed record ArmorAddonSculptArtifact(byte Gender, string BoneName, double DeltaX, double DeltaY, double DeltaZ);

public sealed record ArmorAddonProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    ArmorAddonProposalMode Mode,
    string SourcePlugin,
    string SourceFormId,
    string EditorId,
    string InputSha256,
    string PatchSha256,
    uint? SlotMask,
    string? Race,
    string? FootstepSet,
    byte? MalePriority,
    byte? FemalePriority,
    byte? MaleWeightSliderFlags,
    byte? FemaleWeightSliderFlags,
    byte? DetectionSound,
    double? WeaponAdjust,
    string? MaleModel,
    string? FemaleModel,
    string? MaleFirstPersonModel,
    string? FemaleFirstPersonModel,
    byte? MaleModelFlags,
    byte? FemaleModelFlags,
    double? MaleColorRemapIndex,
    double? FemaleColorRemapIndex,
    string? MaleSkinTexture,
    string? FemaleSkinTexture,
    string? MaleSkinTextureSwapList,
    string? FemaleSkinTextureSwapList,
    string? MaleMaterialSwap,
    string? FemaleMaterialSwap,
    string? MaleFirstPersonMaterialSwap,
    string? FemaleFirstPersonMaterialSwap,
    string? ArtObject,
    ImmutableArray<string> AdditionalRaces,
    ImmutableArray<ArmorAddonSculptArtifact> Sculpt,
    bool? NoUnderarmorScaling,
    bool? HasSculptData,
    bool? HiResFirstPersonOnly,
    ImmutableArray<string> ChangedFields,
    ImmutableArray<string> MasterDependencies,
    bool NoUnrelatedRecords,
    string? TargetFormId = null,
    bool CompleteDocument = false,
    bool SeedFromSource = false,
    string? TargetPlugin = null);

public sealed record ArmorAddonProposalResult(
    bool Written,
    ArmorAddonProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArmorAddonProposalService
{
    ValueTask<ArmorAddonProposalResult> ProposeAsync(ArmorAddonProposalRequest request,
        CancellationToken cancellationToken);
}
