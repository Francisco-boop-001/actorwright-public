using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>One authored Skyrim NPC face-tint layer. The wire order is TINI, TINC,
/// TINV, TIAS; <see cref="Coverage"/> is the TINV percentage and <see cref="Alpha"/>
/// is the fourth TINC byte. RaceMenu custom mask paths are intentionally not represented:
/// they have no NPC-record home and must be handled by a preset sidecar.</summary>
public sealed record SkyrimFaceTintLayer(
    ushort Index,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha,
    uint Coverage,
    short PresetIndex);

public sealed record SkyrimFaceTintPatch(ImmutableArray<SkyrimFaceTintLayer> Layers)
{
    public bool IsEmpty => Layers.IsDefaultOrEmpty;
}

public sealed record SkyrimFaceTintSnapshot(ImmutableArray<SkyrimFaceTintLayer> Layers);

public sealed record SkyrimFaceTintPatchRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    SkyrimFaceTintPatch Patch,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record SkyrimFaceTintPatchProposal(
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

public sealed record SkyrimFaceTintPatchResult(
    bool Applied,
    SkyrimFaceTintPatchProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimFaceTintPatchService
{
    ValueTask<SkyrimFaceTintPatchProposal> AnalyzeAsync(SkyrimFaceTintPatchRequest request, CancellationToken cancellationToken);

    ValueTask<SkyrimFaceTintPatchResult> ApplyAsync(SkyrimFaceTintPatchRequest request,
        SkyrimFaceTintPatchProposal proposal, CancellationToken cancellationToken);
}
