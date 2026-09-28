using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PluginWriteRequest(GameEdition Edition, WorkspacePath Proposal, WorkspacePath Output, bool NoOverwrite = true,
    WorkspacePath? InputPlugin = null, Sha256Hash? ExpectedInputHash = null, WorkspacePath? DataRoot = null, WorkspacePath? PrivateRoot = null);

public sealed record PluginWriteResult(
    bool Applied,
    WorkspacePath Proposal,
    WorkspacePath Output,
    Sha256Hash? OutputHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPluginWriteService
{
    ValueTask<PluginWriteResult> WriteAsync(PluginWriteRequest request, CancellationToken cancellationToken);
}
