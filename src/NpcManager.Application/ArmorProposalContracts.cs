using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum ArmorProposalMode
{
    New,
    Override
}

public sealed record ArmorAddonProposal(ushort Index, FormReference Addon);

public sealed record ArmorObjectBounds(
    short MinimumX,
    short MinimumY,
    short MinimumZ,
    short MaximumX,
    short MaximumY,
    short MaximumZ);

public sealed record ArmorProposalRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    ArmorProposalMode Mode,
    EditorId? EditorId,
    string? Name,
    uint? SlotMask,
    FormReference? Race,
    string? MaleWorldModel,
    string? FemaleWorldModel,
    long? Value,
    double? Weight,
    uint? Health,
    double? ArmorRating,
    ImmutableArray<FormReference>? Keywords,
    ImmutableArray<ArmorAddonProposal>? ArmorAddons,
    WorkspacePath OutputProposal,
    FormId? TargetFormId = null,
    string? Description = null,
    bool? NonPlayable = null,
    FormReference? Enchantment = null,
    FormReference? PickupSound = null,
    FormReference? DropSound = null,
    FormReference? EquipmentType = null,
    FormReference? AlternateBlockMaterial = null,
    FormReference? TemplateArmor = null,
    ArmorObjectBounds? ObjectBounds = null,
    bool CompleteDocument = false);

public sealed record ArmorAddonArtifact(ushort Index, string Addon);

public sealed record ArmorProposalArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    ArmorProposalMode Mode,
    string SourcePlugin,
    string SourceFormId,
    string EditorId,
    string InputSha256,
    string PatchSha256,
    string? Name,
    uint? SlotMask,
    string? Race,
    string? MaleWorldModel,
    string? FemaleWorldModel,
    long? Value,
    double? Weight,
    uint? Health,
    double? ArmorRating,
    ImmutableArray<string> Keywords,
    ImmutableArray<ArmorAddonArtifact> ArmorAddons,
    ImmutableArray<string> ChangedFields,
    ImmutableArray<string> MasterDependencies,
    bool NoUnrelatedRecords,
    string? TargetFormId = null,
    string? Description = null,
    bool? NonPlayable = null,
    string? Enchantment = null,
    string? PickupSound = null,
    string? DropSound = null,
    string? EquipmentType = null,
    string? AlternateBlockMaterial = null,
    string? TemplateArmor = null,
    ArmorObjectBounds? ObjectBounds = null,
    bool CompleteDocument = false);

public sealed record ArmorProposalResult(
    bool Written,
    ArmorProposalArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArmorProposalService
{
    ValueTask<ArmorProposalResult> ProposeAsync(ArmorProposalRequest request,
        CancellationToken cancellationToken);
}
