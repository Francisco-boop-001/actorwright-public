using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    private static async ValueTask<SkyrimNpcFinishCoreProposalResult> WriteProposalAsync(
        SkyrimNpcFinishCoreProposal proposal,
        WorkspacePath proposalPath,
        WorkspacePath projectRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] withoutHash = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal with { ProposalSha256 = null },
                projectRoot);
            Sha256Hash proposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(withoutHash);
            byte[] canonical = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal with { ProposalSha256 = proposalSha },
                projectRoot);
            if (File.Exists(proposalPath.Value) || Directory.Exists(proposalPath.Value))
                return new SkyrimNpcFinishCoreProposalResult(
                    false,
                    null,
                    null,
                    null,
                    [new Diagnostic(
                        "finish-core-proposal-exists",
                        DiagnosticSeverity.Error,
                        "Analyze refuses to overwrite an existing proposal path.")]);
            await using FileStream stream = new(
                proposalPath.Value,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(canonical, cancellationToken);
            return new SkyrimNpcFinishCoreProposalResult(
                true,
                proposal with { ProposalSha256 = proposalSha },
                proposalPath,
                proposalSha,
                ImmutableArray<Diagnostic>.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException)
        {
            return new SkyrimNpcFinishCoreProposalResult(
                false,
                null,
                null,
                null,
                [new Diagnostic(
                    "finish-core-proposal-write",
                    DiagnosticSeverity.Error,
                    exception.Message)]);
        }
    }
}
