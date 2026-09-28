using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record OutfitChoiceSearchRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    string? Search,
    ImmutableArray<PluginName> PluginOrder);

public enum OutfitChoiceProvenanceKind
{
    Base,
    Override,
    InferredOrder,
    Unknown
}

public sealed record OutfitChoiceProvenance(
    OutfitChoiceProvenanceKind Kind,
    PluginName SourcePlugin,
    ImmutableArray<PluginName> OverrideChain);

public sealed record OutfitChoiceCandidate(
    PluginName Plugin,
    FormId FormId,
    string? EditorId,
    string? Name,
    ImmutableArray<FormId> Items,
    bool IsDeleted,
    OutfitChoiceProvenance Provenance,
    ImmutableArray<FormReference> ItemReferences = default);

public sealed record OutfitChoiceSearchResult(
    GameEdition Edition,
    ImmutableArray<OutfitChoiceCandidate> Candidates,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IOutfitChoiceService
{
    ValueTask<OutfitChoiceSearchResult> SearchAsync(OutfitChoiceSearchRequest request,
        CancellationToken cancellationToken);
}
