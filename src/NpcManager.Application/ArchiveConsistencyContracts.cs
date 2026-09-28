using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ArchiveConsistencyRequest(
    GameEdition Edition,
    WorkspacePath PluginPath,
    WorkspacePath AssetIndexPath);

public enum ArchiveConsistencyStatus
{
    Matched,
    NotIndexed,
    MissingOnDisk,
    UnsupportedEdition
}

public sealed record ArchiveConsistencyEntry(
    string ArchiveName,
    bool PresentOnDisk,
    bool Indexed,
    ArchiveConsistencyStatus Status);

public sealed record ArchiveConsistencyResult(
    GameEdition Edition,
    WorkspacePath PluginPath,
    WorkspacePath AssetIndexPath,
    WorkspacePath? DataRoot,
    bool IsConsistent,
    ImmutableArray<ArchiveConsistencyEntry> Archives,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArchiveConsistencyService
{
    ValueTask<ArchiveConsistencyResult> EvaluateAsync(
        ArchiveConsistencyRequest request, CancellationToken cancellationToken);
}
