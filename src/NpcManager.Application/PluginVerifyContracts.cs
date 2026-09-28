using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PluginVerifyRequest(GameEdition Edition, WorkspacePath Before, WorkspacePath After, WorkspacePath Proposal);

public sealed record PluginVerifyResult(bool IsValid, ImmutableArray<MutationChange> ObservedChanges, ImmutableArray<Diagnostic> Diagnostics);

public interface IPluginVerifyService
{
    ValueTask<PluginVerifyResult> VerifyAsync(PluginVerifyRequest request, CancellationToken cancellationToken);
}
