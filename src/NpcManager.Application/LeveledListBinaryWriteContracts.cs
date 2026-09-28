using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Materializes one hash-bound LVLI proposal into a new ordinary plugin.</summary>
public sealed record LeveledListBinaryWriteRequest(
    GameEdition Edition,
    WorkspacePath Proposal,
    WorkspacePath Output);

public sealed record LeveledListBinaryWriteResult(
    bool Written,
    WorkspacePath Proposal,
    WorkspacePath Output,
    FormId? ListFormId,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ILeveledListBinaryWriteService
{
    ValueTask<LeveledListBinaryWriteResult> WriteAsync(LeveledListBinaryWriteRequest request,
        CancellationToken cancellationToken);
}
