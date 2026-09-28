using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// The bounded fields currently accepted by the true existing-NPC override
/// transaction. Scalar NAM7 weight is included because it has an exact NPC
/// subrecord home. Other appearance fields remain closed so they cannot be
/// applied without matching FaceGen and sidecar transactions.
/// </summary>
public sealed record NpcOverridePatch(
    EditorId? EditorId,
    NpcName? Name,
    NpcStatsPatch? Stats,
    NpcKeywordPatch? Keywords = null,
    NpcFactionPatch? Factions = null,
    NpcInventoryPatch? Inventory = null,
    NpcOutfitPatch? Outfits = null,
    NpcPerkPatch? Perks = null,
    NpcActorEffectPatch? ActorEffects = null,
    NpcEditableNamesPatch? Names = null,
    NpcArchetypePatch? Archetype = null,
    NpcWeightPatch? Weight = null)
{
    public bool IsEmpty => EditorId is null && Name is null &&
                           (Names is null || Names.IsEmpty) &&
                           (Archetype is null || Archetype.IsEmpty) &&
                           (Weight is null || Weight.IsEmpty) &&
                           (Stats is null || Stats.IsEmpty) &&
                           (Keywords is null || Keywords.IsEmpty) &&
                           (Factions is null || Factions.IsEmpty) &&
                           (Inventory is null || Inventory.IsEmpty) &&
                           (Outfits is null || Outfits.IsEmpty) &&
                           (Perks is null || Perks.IsEmpty) &&
                           (ActorEffects is null || ActorEffects.IsEmpty);
}

/// <summary>
/// Read-only source state used to seed lossless editor controls. Every
/// collection is the exact ordered value reopened from the hash-bound plugin;
/// it is not a mutation proposal or runtime authority.
/// </summary>
public sealed record NpcOverrideSourceSnapshot(
    EditorId? EditorId,
    NpcName? Name,
    NpcSex Sex,
    float SkyrimWeight,
    NpcArchetypeReferences Archetype,
    NpcStatsSnapshot Stats,
    NpcKeywordSnapshot Keywords,
    NpcFactionSnapshot Factions,
    NpcInventorySnapshot Inventory,
    NpcOutfitSnapshot Outfits,
    NpcPerkSnapshot Perks,
    NpcActorEffectSnapshot ActorEffects,
    string? ShortName = null);

public sealed record NpcOverrideSourceInspection(
    bool IsValid,
    WorkspacePath SourcePlugin,
    Sha256Hash? SourceSha256,
    FormId TargetFormId,
    NpcOverrideSourceSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Hash-bound intent for one ordinary output ESP containing a genuine override
/// of an NPC owned by <see cref="SourcePlugin"/>. Analyze may persist the
/// proposal; Apply requires that exact persisted proposal and never overwrites.
/// </summary>
public sealed record NpcOverrideRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    Sha256Hash ExpectedSourceSha256,
    FormId TargetFormId,
    WorkspacePath? ProposalPath,
    WorkspacePath OutputPlugin,
    NpcOverridePatch Patch,
    ExistingNpcEditOutputKind OutputKind = ExistingNpcEditOutputKind.SourceMasteredOverride);

public sealed record NpcOverrideProposal(
    int SchemaVersion,
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    Sha256Hash SourceSha256,
    PluginName SourcePluginName,
    FormId TargetFormId,
    WorkspacePath? ProposalPath,
    Sha256Hash? ProposalSha256,
    WorkspacePath OutputPlugin,
    PluginName OutputPluginName,
    NpcOverridePatch Patch,
    ImmutableArray<PluginName> RequiredMasters,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ExistingNpcEditOutputKind OutputKind { get; init; } =
        ExistingNpcEditOutputKind.SourceMasteredOverride;

    public bool IsApplicable =>
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) &&
        Changes.Length > 0;
}

/// <summary>
/// Independent ownership and field read-back. A valid result proves the source
/// is a declared master, the only major record is the source-owned target NPC,
/// and no self-owned duplicate target exists.
/// </summary>
public sealed record NpcOverrideVerificationResult(
    bool IsValid,
    WorkspacePath SourcePlugin,
    Sha256Hash? SourceSha256,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    PluginName SourcePluginName,
    FormId TargetFormId,
    ImmutableArray<PluginName> RequiredMasters,
    ImmutableArray<PluginName> ObservedMasters,
    int MajorRecordCount,
    int NpcRecordCount,
    int SourceOwnedTargetCount,
    int SelfOwnedTargetCount,
    ImmutableArray<MutationChange> ObservedChanges,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ExistingNpcEditOutputKind OutputKind { get; init; } =
        ExistingNpcEditOutputKind.SourceMasteredOverride;
    public bool IndependentPreservation { get; init; }
}

public sealed record NpcOverrideResult(
    bool Applied,
    NpcOverrideProposal Proposal,
    Sha256Hash? OutputSha256,
    NpcOverrideVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcOverrideService
{
    ValueTask<NpcOverrideSourceInspection> InspectSourceAsync(
        GameEdition edition,
        WorkspacePath sourcePlugin,
        Sha256Hash expectedSourceSha256,
        FormId targetFormId,
        CancellationToken cancellationToken);

    ValueTask<NpcOverrideProposal> AnalyzeAsync(
        NpcOverrideRequest request,
        CancellationToken cancellationToken);

    ValueTask<NpcOverrideResult> ApplyAsync(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<NpcOverrideVerificationResult> VerifyAsync(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        CancellationToken cancellationToken);
}
