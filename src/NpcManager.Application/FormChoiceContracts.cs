using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Read-only request for the reusable FormID picker/search core.</summary>
public sealed record FormChoiceSearchRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<RecordSignature> AllowedSignatures,
    string? Search,
    FormId? FormId,
    bool AllowNull,
    ImmutableArray<PluginName> PluginOrder);

public enum FormChoiceProvenanceKind
{
    Base,
    Override,
    InferredOrder,
    Unknown
}

public sealed record FormChoiceProvenance(
    FormChoiceProvenanceKind Kind,
    PluginName SourcePlugin,
    ImmutableArray<PluginName> OverrideChain);

public sealed record FormChoiceCandidate(
    PluginName Plugin,
    FormId FormId,
    RecordSignature Signature,
    string? EditorId,
    string? Name,
    bool IsDeleted,
    FormChoiceProvenance Provenance);

public sealed record FormChoiceSearchResult(
    GameEdition Edition,
    bool AllowNull,
    ImmutableArray<FormChoiceCandidate> Candidates,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFormChoiceService
{
    ValueTask<FormChoiceSearchResult> SearchAsync(FormChoiceSearchRequest request, CancellationToken cancellationToken);
}
