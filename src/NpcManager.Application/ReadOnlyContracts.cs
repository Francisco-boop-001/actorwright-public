using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PluginReadRequest(GameEdition Edition, WorkspacePath PluginPath)
{
    public bool AllowSkeletalWorldParents { get; init; }

    // Audit-only virtual layout; default retains exact physical record digests.
    public ImmutableArray<PluginName> NormalizedMasterOrder { get; init; }
}

public sealed record PluginRecordSummary(
    FormId FormId,
    string Signature,
    string? EditorId,
    string? Name,
    bool IsNpc,
    bool IsDeleted,
    NpcRecordMetadata? NpcMetadata = null,
    AssetPath? ModelPath = null,
    ImmutableArray<FormId> OutfitItems = default,
    string? RawRecordSha256 = null,
    PluginName? OwnerPlugin = null,
    ImmutableArray<FormReference> OutfitItemReferences = default);

public sealed record NpcRecordMetadata(
    NpcSex? Sex,
    FormId? RaceFormId,
    float? SkyrimWeight,
    float? Fallout4ThinWeight,
    float? Fallout4MuscularWeight,
    float? Fallout4FatWeight,
    bool HasTemplate,
    FormId? TemplateFormId,
    bool IsTemplateSource,
    bool IsPlaced,
    bool IsInLeveledList,
    bool IsCharGenFacePreset,
    ImmutableArray<FormId> HeadPartFormIds = default,
    FormReference? Race = null,
    ImmutableArray<FormReference> HeadParts = default);

public enum NpcSex
{
    Male,
    Female
}

public enum NpcCategory
{
    Unique,
    Generic,
    Template,
    Unused
}

public enum NpcChangeState
{
    Unchanged,
    Changed,
    Deleted,
    Unknown
}

public enum NpcProvenanceKind
{
    Base,
    Override,
    InferredOrder,
    Unknown
}

public sealed record NpcProvenance(
    NpcProvenanceKind Kind,
    PluginName SourcePlugin,
    ImmutableArray<PluginName> OverrideChain);

public sealed record PluginInspection(
    GameEdition Edition,
    PluginName Plugin,
    ImmutableArray<PluginName> Masters,
    ImmutableArray<PluginRecordSummary> Records,
    ImmutableArray<Diagnostic> Diagnostics);

// InvalidDataException is sealed in the target .NET runtime, so coded parser
// diagnostics use a distinct exception type while malformed bounds remain
// ordinary InvalidDataException.
public sealed class PluginReadDiagnosticException : Exception
{
    public PluginReadDiagnosticException(string code, string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Diagnostic code is required.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}

public interface IPluginReader
{
    ValueTask<PluginInspection> ReadAsync(PluginReadRequest request, CancellationToken cancellationToken);
}

public sealed record AssetIndexRequest(GameEdition Edition, WorkspacePath DataRoot);

public enum AssetProviderKind
{
    Loose,
    Archive
}

public sealed record AssetProvider(
    AssetPath Path,
    AssetProviderKind Kind,
    string Source,
    long Size,
    string Sha256);

public sealed record AssetIndex(
    GameEdition Edition,
    ImmutableArray<AssetProvider> Providers,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IAssetIndexer
{
    ValueTask<AssetIndex> IndexAsync(AssetIndexRequest request, CancellationToken cancellationToken);
}

public sealed record GameInventoryRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    string? Search,
    FormId? FormId,
    bool IncludeAssets,
    ImmutableArray<PluginName> PluginOrder,
    ImmutableHashSet<NpcCategory>? Categories = null,
    bool ChangedOnly = false);

public sealed record NpcRecordSummary(
    PluginName Plugin,
    FormId FormId,
    string? EditorId,
    string? Name,
    string Signature,
    bool IsDeleted,
    NpcRecordMetadata? Metadata = null,
    NpcProvenance? Provenance = null,
    NpcChangeState ChangeState = NpcChangeState.Unknown,
    ImmutableArray<NpcCategory> Categories = default,
    PluginName? OwnerPlugin = null);

public sealed record GameInventory(
    GameEdition Edition,
    ImmutableArray<PluginName> Plugins,
    ImmutableArray<PluginName> Masters,
    ImmutableArray<NpcRecordSummary> Npcs,
    AssetIndex? Assets,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IGameInventoryService
{
    ValueTask<GameInventory> ReadAsync(GameInventoryRequest request, CancellationToken cancellationToken);
}
