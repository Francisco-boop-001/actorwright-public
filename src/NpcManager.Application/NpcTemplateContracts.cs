using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>TES4 NPC template categories, kept aligned with the pinned engine/xEdit bit order.</summary>
public enum NpcTemplateCategory
{
    Traits = 0,
    Stats = 1,
    Factions = 2,
    SpellList = 3,
    AiData = 4,
    AiPackages = 5,
    ModelAnimation = 6,
    BaseData = 7,
    Inventory = 8,
    Script = 9,
    DefaultPackageList = 10,
    AttackData = 11,
    Keywords = 12
}

public static class NpcTemplateCategoryExtensions
{
    public static string ToWireName(this NpcTemplateCategory category) => category switch
    {
        NpcTemplateCategory.Traits => "traits",
        NpcTemplateCategory.Stats => "stats",
        NpcTemplateCategory.Factions => "factions",
        NpcTemplateCategory.SpellList => "spell-list",
        NpcTemplateCategory.AiData => "ai-data",
        NpcTemplateCategory.AiPackages => "ai-packages",
        NpcTemplateCategory.ModelAnimation => "model-animation",
        NpcTemplateCategory.BaseData => "base-data",
        NpcTemplateCategory.Inventory => "inventory",
        NpcTemplateCategory.Script => "script",
        NpcTemplateCategory.DefaultPackageList => "default-package-list",
        NpcTemplateCategory.AttackData => "attack-data",
        NpcTemplateCategory.Keywords => "keywords",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown NPC template category.")
    };

    public static bool TryParseWireName(string value, out NpcTemplateCategory category)
    {
        foreach (var candidate in Enum.GetValues<NpcTemplateCategory>())
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase))
            {
                category = candidate;
                return true;
            }
        }

        category = default;
        return false;
    }
}

public sealed record NpcTemplateCategoryEvidence(
    NpcTemplateCategory Category,
    bool Inherited,
    FormReference? DirectSource,
    FormReference? ResolvedSource,
    ImmutableArray<string> InheritedFields,
    bool Supported);

public sealed record NpcTemplateMaterializationRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    ImmutableHashSet<NpcTemplateCategory>? Categories,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record NpcTemplateMaterializationProposal(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    Sha256Hash InputHash,
    ImmutableArray<NpcTemplateCategoryEvidence> Categories,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record NpcTemplateMaterializationResult(
    bool Applied,
    NpcTemplateMaterializationProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcTemplateMaterializationService
{
    ValueTask<NpcTemplateMaterializationProposal> AnalyzeAsync(
        NpcTemplateMaterializationRequest request, CancellationToken cancellationToken);

    ValueTask<NpcTemplateMaterializationResult> ApplyAsync(
        NpcTemplateMaterializationRequest request,
        NpcTemplateMaterializationProposal proposal,
        CancellationToken cancellationToken);
}
