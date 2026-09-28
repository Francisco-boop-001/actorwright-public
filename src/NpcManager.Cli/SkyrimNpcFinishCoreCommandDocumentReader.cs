using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed class SkyrimNpcFinishCoreCommandDocumentReader(WorkspacePath workspaceRoot)
{
    internal async ValueTask<byte[]> ReadBoundFileAsync(
        WorkspacePath path,
        Sha256Hash expected,
        Func<byte[], byte[]> canonicalize,
        CancellationToken cancellationToken)
    {
        RequireReadAllowed(workspaceRoot, path);
        if (!path.IsUnder(workspaceRoot) || !File.Exists(path.Value) ||
            File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Finish Core documents must be ordinary K-local files.");
        byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
        byte[] canonical = canonicalize(bytes);
        Sha256Hash actual = new(Convert.ToHexString(SHA256.HashData(bytes)));
        Sha256Hash canonicalHash = new(Convert.ToHexString(SHA256.HashData(canonical)));
        return actual == expected || canonicalHash == expected
            ? canonical
            : throw new InvalidDataException(
                $"Finish Core document hash binding failed: expectedSha256={expected.Value}; " +
                $"receivedSha256={actual.Value}; canonicalSha256={canonicalHash.Value}.");
    }

    internal async ValueTask<byte[]> ReadProposalBoundFileAsync(
        WorkspacePath path,
        Sha256Hash expected,
        CancellationToken cancellationToken)
    {
        RequireReadAllowed(workspaceRoot, path);
        if (!path.IsUnder(workspaceRoot) || !File.Exists(path.Value) ||
            File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Finish Core documents must be ordinary K-local files.");
        byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
        byte[] canonical = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            SkyrimNpcFinishCoreDocumentCodec.ParseProposal(bytes, workspaceRoot), workspaceRoot);
        Sha256Hash actual = new(Convert.ToHexString(SHA256.HashData(bytes)));
        Sha256Hash canonicalHash = new(Convert.ToHexString(SHA256.HashData(canonical)));
        Sha256Hash proposalHash = SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(canonical);
        return actual == expected || canonicalHash == expected || proposalHash == expected
            ? canonical
            : throw new InvalidDataException(
                $"Finish Core proposal hash binding failed: expectedSha256={expected.Value}; " +
                $"receivedSha256={actual.Value}; canonicalSha256={canonicalHash.Value}; " +
                $"proposalSha256={proposalHash.Value}.");
    }

    internal static void RequireReadAllowed(WorkspacePath workspaceRoot, WorkspacePath path)
    {
        if (!Path.IsPathFullyQualified(path.Value) || !path.IsUnder(workspaceRoot))
            throw new InvalidDataException("Finish Core documents must be ordinary K-local files.");
        var policy = new KOnlyWorkspacePolicy(workspaceRoot, ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
        var diagnostics = policy.EvaluateReadRoot(workspaceRoot, path);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(string.Join(" | ", diagnostics.Select(item => item.Code + ": " + item.Message)));
    }
}
