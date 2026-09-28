using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Materializes one hash-bound ARMA proposal into a new ordinary plugin.</summary>
public sealed record ArmorAddonBinaryWriteRequest(
    GameEdition Edition,
    WorkspacePath Proposal,
    WorkspacePath Output);

public sealed record ArmorAddonBinaryWriteResult(
    bool Written,
    WorkspacePath Proposal,
    WorkspacePath Output,
    FormId? TargetFormId,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArmorAddonBinaryWriteService
{
    ValueTask<ArmorAddonBinaryWriteResult> WriteAsync(ArmorAddonBinaryWriteRequest request,
        CancellationToken cancellationToken);
}
