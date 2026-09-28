using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Applies one validated OTFT proposal to a new K-local plugin.</summary>
public sealed record OutfitBinaryWriteRequest(
    GameEdition Edition,
    WorkspacePath Proposal,
    WorkspacePath Output);

public sealed record OutfitBinaryWriteResult(
    bool Written,
    WorkspacePath Proposal,
    WorkspacePath Output,
    FormId? TargetFormId,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IOutfitBinaryWriteService
{
    ValueTask<OutfitBinaryWriteResult> WriteAsync(OutfitBinaryWriteRequest request,
        CancellationToken cancellationToken);
}
