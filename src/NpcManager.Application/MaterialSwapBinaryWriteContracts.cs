using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Materializes one hash-bound Fallout 4 MSWP proposal into an ordinary plugin.</summary>
public sealed record MaterialSwapBinaryWriteRequest(
    GameEdition Edition,
    WorkspacePath Proposal,
    WorkspacePath Output);

public sealed record MaterialSwapBinaryWriteResult(
    bool Written,
    WorkspacePath Proposal,
    WorkspacePath Output,
    FormId? TargetFormId,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IMaterialSwapBinaryWriteService
{
    ValueTask<MaterialSwapBinaryWriteResult> WriteAsync(MaterialSwapBinaryWriteRequest request,
        CancellationToken cancellationToken);
}
