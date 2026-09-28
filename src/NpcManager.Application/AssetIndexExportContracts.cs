using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record AssetIndexEntry(
    AssetPath Path,
    AssetProvider Winner,
    ImmutableArray<AssetProvider> Providers);

public sealed record AssetIndexArtifact(
    string SchemaVersion,
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<AssetIndexEntry> Entries,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record AssetIndexExportRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    WorkspacePath Output);

public sealed record AssetIndexExportResult(
    bool Written,
    WorkspacePath Output,
    AssetIndexArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IAssetIndexExportService
{
    ValueTask<AssetIndexExportResult> ExportAsync(
        AssetIndexExportRequest request, CancellationToken cancellationToken);
}
