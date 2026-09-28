using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Materializes one hash-bound ARMO proposal into a new ordinary plugin.</summary>
public sealed record ArmorBinaryWriteRequest(
    GameEdition Edition,
    WorkspacePath Proposal,
    WorkspacePath Output);

public sealed record ArmorBinaryWriteResult(
    bool Written,
    WorkspacePath Proposal,
    WorkspacePath Output,
    FormId? TargetFormId,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArmorBinaryWriteService
{
    ValueTask<ArmorBinaryWriteResult> WriteAsync(ArmorBinaryWriteRequest request,
        CancellationToken cancellationToken);
}
