using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>One RaceMenu vertex delta in world units. The .jslot stores the same values as
/// rounded integers multiplied by the document's sculpt divisor.</summary>
public sealed record RaceMenuSculptVertex(int Index, float Dx, float Dy, float Dz);

/// <summary>One RaceMenu per-shape sculpt block. Host identifies the chargen TRI and VertexCount
/// bounds the indices without pretending to validate the external mesh topology.</summary>
public sealed record RaceMenuSculptPart(string Host, long VertexCount, ImmutableArray<RaceMenuSculptVertex> Vertices,
    bool HasVertexCount = false, bool HasData = false);

public sealed record RaceMenuSculptPatch(int SculptDivisor, ImmutableArray<RaceMenuSculptPart> Parts);

public sealed record RaceMenuSculptPatchRequest(
    GameEdition Edition,
    WorkspacePath InputPreset,
    WorkspacePath OutputPreset,
    RaceMenuSculptPatch Patch,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record RaceMenuSculptPatchProposal(
    GameEdition Edition,
    WorkspacePath InputPreset,
    WorkspacePath OutputPreset,
    Sha256Hash InputHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record RaceMenuSculptPatchResult(
    bool Applied,
    RaceMenuSculptPatchProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuSculptPatchService
{
    ValueTask<RaceMenuSculptPatchProposal> AnalyzeAsync(RaceMenuSculptPatchRequest request,
        CancellationToken cancellationToken);

    ValueTask<RaceMenuSculptPatchResult> ApplyAsync(RaceMenuSculptPatchRequest request,
        RaceMenuSculptPatchProposal proposal, CancellationToken cancellationToken);
}
