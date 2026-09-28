using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Persisted NPC sections that can be reset without discarding unrelated edits.</summary>
public enum NpcResetSection
{
    Identity,
    Archetype,
    Weight,
    Stats,
    Keywords,
    Factions,
    Inventory,
    Outfits,
    Perks,
    ActorEffects,
    Properties
}

public static class NpcResetSectionExtensions
{
    public static string ToWireName(this NpcResetSection section) => section switch
    {
        NpcResetSection.Identity => "identity",
        NpcResetSection.Archetype => "archetype",
        NpcResetSection.Weight => "weight",
        NpcResetSection.Stats => "stats",
        NpcResetSection.Keywords => "keywords",
        NpcResetSection.Factions => "factions",
        NpcResetSection.Inventory => "inventory",
        NpcResetSection.Outfits => "outfits",
        NpcResetSection.Perks => "perks",
        NpcResetSection.ActorEffects => "actor-effects",
        NpcResetSection.Properties => "properties",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported NPC reset section.")
    };

    public static bool TryParseWireName(string value, out NpcResetSection section)
    {
        foreach (var candidate in Enum.GetValues<NpcResetSection>())
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase))
            {
                section = candidate;
                return true;
            }
        }

        section = default;
        return false;
    }
}

public sealed record NpcResetRequest(
    GameEdition Edition,
    WorkspacePath CurrentPlugin,
    WorkspacePath BaselinePlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    NpcResetSection Section,
    Sha256Hash? ExpectedCurrentHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record NpcResetProposal(
    GameEdition Edition,
    WorkspacePath CurrentPlugin,
    WorkspacePath BaselinePlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    NpcResetSection Section,
    Sha256Hash CurrentHash,
    Sha256Hash BaselineHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record NpcResetResult(
    bool Applied,
    NpcResetProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcResetService
{
    ValueTask<NpcResetProposal> AnalyzeAsync(NpcResetRequest request, CancellationToken cancellationToken);

    ValueTask<NpcResetResult> ApplyAsync(NpcResetRequest request, NpcResetProposal proposal, CancellationToken cancellationToken);
}
