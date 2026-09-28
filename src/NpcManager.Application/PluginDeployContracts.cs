using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Deploys one already-built plugin into an explicit copied K-local Data root.</summary>
public sealed record PluginDeployRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath DataRoot,
    Sha256Hash ExpectedInputSha256);

public sealed record PluginDeployResult(
    bool Deployed,
    bool AlreadyPresent,
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath Destination,
    Sha256Hash? SourceSha256,
    Sha256Hash? DestinationSha256,
    long? ByteLength,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPluginDeployService
{
    ValueTask<PluginDeployResult> DeployAsync(PluginDeployRequest request,
        CancellationToken cancellationToken);
}
