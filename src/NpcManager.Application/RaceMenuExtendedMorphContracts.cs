using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>One RaceMenu NiOverride custom morph keyed by its exact slider name.</summary>
public sealed record RaceMenuExtendedMorph(string Name, float Value);

public sealed record RaceMenuExtendedMorphPatch(ImmutableArray<RaceMenuExtendedMorph> Morphs);

public sealed record RaceMenuExtendedMorphPatchRequest(
    GameEdition Edition,
    WorkspacePath InputPreset,
    WorkspacePath OutputPreset,
    RaceMenuExtendedMorphPatch Patch,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record RaceMenuExtendedMorphPatchProposal(
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

public sealed record RaceMenuExtendedMorphPatchResult(
    bool Applied,
    RaceMenuExtendedMorphPatchProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuExtendedMorphPatchService
{
    ValueTask<RaceMenuExtendedMorphPatchProposal> AnalyzeAsync(RaceMenuExtendedMorphPatchRequest request,
        CancellationToken cancellationToken);

    ValueTask<RaceMenuExtendedMorphPatchResult> ApplyAsync(RaceMenuExtendedMorphPatchRequest request,
        RaceMenuExtendedMorphPatchProposal proposal, CancellationToken cancellationToken);
}
