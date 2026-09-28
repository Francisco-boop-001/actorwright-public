using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Complete appearance-only intent for an NPC owned by an existing Skyrim
/// plugin. Identity, gameplay statistics, inventory, packages, factions, and
/// unrelated VMAD scripts remain source-owned and must be preserved.
/// </summary>
public sealed record NpcAppearanceOverrideRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    Sha256Hash ExpectedSourceSha256,
    FormId TargetFormId,
    WorkspacePath ProposalPath,
    WorkspacePath OutputPlugin,
    FormReference Race,
    NpcSex Sex,
    FullyAuthoredSkyrimNpcAppearanceSource Appearance,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
    NpcOutfitPatch? OutfitPatch = null,
    bool? IsCharGenFacePreset = null)
{
    public ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities { get; init; } = [];
}

/// <summary>
/// Persisted, hash-bound plan for one source-owned NPC appearance override and
/// its explicitly authorized output-owned CLFM/TXST/HDPT support records.
/// </summary>
public sealed record NpcAppearanceOverrideProposal(
    int SchemaVersion,
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    Sha256Hash SourceSha256,
    PluginName SourcePluginName,
    FormId TargetFormId,
    EditorId SourceEditorId,
    WorkspacePath ProposalPath,
    Sha256Hash? ProposalSha256,
    WorkspacePath OutputPlugin,
    PluginName OutputPluginName,
    FormReference Race,
    NpcSex Sex,
    FullyAuthoredSkyrimNpcAppearanceSource Appearance,
    SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
    ImmutableArray<PluginName> RequiredMasters,
    ImmutableArray<RecordSignature> ExpectedMajorRecordSignatures,
    ImmutableArray<string> ChangedNpcSubrecords,
    ImmutableArray<Diagnostic> Diagnostics,
    NpcOutfitPatch? OutfitPatch = null,
    bool? IsCharGenFacePreset = null)
{
    public bool IsApplicable =>
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Independent typed/raw proof for the appearance override. Validity requires
/// source ownership, the exact declared supporting-record surface, complete
/// appearance read-back, and preservation of every unrelated NPC subrecord and
/// unrelated VMAD script.
/// </summary>
public sealed record NpcAppearanceOverrideVerificationResult(
    bool IsValid,
    WorkspacePath OutputPlugin,
    Sha256Hash? OutputSha256,
    ImmutableArray<PluginName> ObservedMasters,
    ImmutableArray<RecordSignature> ObservedMajorRecordSignatures,
    int SourceOwnedTargetCount,
    int SelfOwnedTargetCount,
    bool AppearanceMatches,
    bool RecordPatchMatches,
    bool UnrelatedNpcSubrecordsPreserved,
    bool UnrelatedScriptsPreserved,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record NpcAppearanceOverrideResult(
    bool Applied,
    NpcAppearanceOverrideProposal Proposal,
    NpcAppearanceOverrideVerificationResult? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcAppearanceOverrideService
{
    ValueTask<NpcAppearanceOverrideProposal> AnalyzeAsync(
        NpcAppearanceOverrideRequest request,
        CancellationToken cancellationToken);

    ValueTask<NpcAppearanceOverrideResult> ApplyAsync(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<NpcAppearanceOverrideVerificationResult> VerifyAsync(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        CancellationToken cancellationToken);
}
