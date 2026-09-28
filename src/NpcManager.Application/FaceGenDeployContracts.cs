using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Deploys a hash-bound FaceGen pack into an explicit copied K-local Data root.</summary>
public sealed record FaceGenDeployRequest(
    GameEdition Edition,
    WorkspacePath PackageManifest,
    WorkspacePath DataRoot);

public sealed record FaceGenDeployFileResult(
    string RelativePath,
    WorkspacePath Destination,
    long ByteLength,
    Sha256Hash SourceSha256,
    Sha256Hash DestinationSha256,
    bool Deployed,
    bool AlreadyPresent);

public sealed record FaceGenDeployResult(
    bool Deployed,
    bool AlreadyPresent,
    GameEdition Edition,
    WorkspacePath PackageManifest,
    WorkspacePath DataRoot,
    ImmutableArray<FaceGenDeployFileResult> Files,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenDeployService
{
    ValueTask<FaceGenDeployResult> DeployAsync(FaceGenDeployRequest request,
        CancellationToken cancellationToken);
}
