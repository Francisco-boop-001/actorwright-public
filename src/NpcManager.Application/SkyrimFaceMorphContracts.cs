using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Typed Skyrim native face-morph payload. NAM9 contains 18 UI sliders followed by a
/// preserved engine-owned trailing float; NAMA contains four family/type values and uses
/// uint.MaxValue as the upstream unset sentinel.</summary>
public sealed record SkyrimFaceMorphPatch(
    ImmutableArray<float> Nam9Sliders,
    float Nam9Trailing,
    ImmutableArray<uint> NamaValues);

public sealed record SkyrimFaceMorphSnapshot(
    ImmutableArray<float> Nam9Sliders,
    float Nam9Trailing,
    ImmutableArray<uint> NamaValues,
    bool HasNam9,
    bool HasNama);

/// <summary>
/// Hash-bound read request for the engine-owned native face-morph fields on a
/// copied Skyrim NPC record.
/// </summary>
public sealed record SkyrimFaceMorphSnapshotRequest(
    GameEdition Edition,
    WorkspacePath Plugin,
    Sha256Hash ExpectedPluginSha256,
    FormId NpcFormId);

public sealed record SkyrimFaceMorphSnapshotResult(
    bool Resolved,
    Sha256Hash? PluginSha256,
    SkyrimFaceMorphSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceMorphSnapshotService
{
    ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
        SkyrimFaceMorphSnapshotRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimFaceMorphPatchRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    SkyrimFaceMorphPatch Patch,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record SkyrimFaceMorphPatchProposal(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    Sha256Hash InputHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record SkyrimFaceMorphPatchResult(
    bool Applied,
    SkyrimFaceMorphPatchProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceMorphPatchService
{
    ValueTask<SkyrimFaceMorphPatchProposal> AnalyzeAsync(SkyrimFaceMorphPatchRequest request, CancellationToken cancellationToken);

    ValueTask<SkyrimFaceMorphPatchResult> ApplyAsync(SkyrimFaceMorphPatchRequest request,
        SkyrimFaceMorphPatchProposal proposal, CancellationToken cancellationToken);
}
