using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Read-only request for normalized mesh and headpart choices.</summary>
public sealed record AssetChoiceSearchRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    AssetChoiceKind Kind,
    string? Search,
    ImmutableArray<PluginName> PluginOrder);

public enum AssetChoiceKind
{
    Mesh,
    HeadPart
}

public enum AssetChoiceProviderStatus
{
    Resolved,
    Missing
}

public sealed record AssetChoiceProvider(
    AssetChoiceProviderStatus Status,
    ImmutableArray<AssetChoiceProviderEvidence> Evidence);

public sealed record AssetChoiceProviderEvidence(
    AssetProviderKind Kind,
    string Source,
    long Size,
    Sha256Hash Sha256);

public sealed record AssetChoiceProvenance(
    FormChoiceProvenanceKind Kind,
    PluginName SourcePlugin,
    ImmutableArray<PluginName> OverrideChain);

public sealed record AssetChoiceCandidate(
    AssetChoiceKind Kind,
    AssetPath Path,
    AssetChoiceProvider Provider,
    PluginName? Plugin,
    FormId? FormId,
    RecordSignature? Signature,
    string? EditorId,
    string? Name,
    AssetChoiceProvenance? Provenance);

public sealed record AssetChoiceSearchResult(
    GameEdition Edition,
    AssetChoiceKind Kind,
    ImmutableArray<AssetChoiceCandidate> Candidates,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IAssetChoiceService
{
    ValueTask<AssetChoiceSearchResult> SearchAsync(
        AssetChoiceSearchRequest request, CancellationToken cancellationToken);
}
