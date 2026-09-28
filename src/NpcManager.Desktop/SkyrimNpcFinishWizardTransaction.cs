using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

public sealed class SkyrimNpcFinishWizardTransaction(
    ISkyrimNpcFinishCoreService service,
    WorkspacePath workspaceRoot)
{
    public async ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
        => await AnalyzeAsync(
            requestPath,
            requestSha256,
            proposalPath,
            installContext: null,
            cancellationToken);

    public async ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        ExternalHeadPartInstallVerificationContext? installContext,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreRequest request = await LoadRequestAsync(
            requestPath, requestSha256, cancellationToken);
        if (installContext is { } context)
        {
            if (service is not ISkyrimNpcFinishCoreInstallContextService
                contextService)
                throw new InvalidOperationException(
                    "Finish Core install-context analysis requires the contextful service surface.");
            return await contextService.AnalyzeWithInstallContextAsync(
                request,
                requestSha256,
                proposalPath,
                context,
                cancellationToken);
        }
        return await service.AnalyzeAsync(
            request, requestSha256, proposalPath, cancellationToken);
    }

    public async ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
        => await ApplyAsync(
            requestPath,
            requestSha256,
            proposalPath,
            proposalSha256,
            installContext: null,
            cancellationToken);

    public async ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        ExternalHeadPartInstallVerificationContext? installContext,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreRequest request = await LoadRequestAsync(
            requestPath, requestSha256, cancellationToken);
        byte[] proposalBytes = await ReadBoundAsync(
            proposalPath, proposalSha256, proposalHash: true, cancellationToken);
        SkyrimNpcFinishCoreProposal proposal = SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            proposalBytes, workspaceRoot);
        if (proposal.RequestSha256 != requestSha256 || proposal.ProposalSha256 != proposalSha256)
            throw new InvalidDataException("The Finish Core proposal is not bound to the reviewed request.");
        if (installContext is { } context)
        {
            if (service is not ISkyrimNpcFinishCoreInstallContextService
                contextService)
                throw new InvalidOperationException(
                    "Finish Core install-context apply requires the contextful service surface.");
            return await contextService.ApplyWithInstallContextAsync(
                request,
                requestSha256,
                proposal,
                proposalSha256,
                context,
                cancellationToken);
        }
        return await service.ApplyAsync(
            request, requestSha256, proposal, proposalSha256, cancellationToken);
    }

    public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken) =>
        VerifyAsync(
            manifestPath,
            manifestSha256,
            installContext: null,
            cancellationToken);

    public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        ExternalHeadPartInstallVerificationContext? installContext,
        CancellationToken cancellationToken)
    {
        if (installContext is { } context)
        {
            if (service is not ISkyrimNpcFinishCoreInstallContextService
                contextService)
                throw new InvalidOperationException(
                    "Finish Core install-context verification requires the contextful service surface.");
            return contextService.VerifyWithInstallContextAsync(
                manifestPath,
                manifestSha256,
                context,
                cancellationToken);
        }
        return service.VerifyAsync(manifestPath, manifestSha256, cancellationToken);
    }

    private async ValueTask<SkyrimNpcFinishCoreRequest> LoadRequestAsync(
        WorkspacePath path,
        Sha256Hash hash,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundAsync(path, hash, proposalHash: false, cancellationToken);
        return SkyrimNpcFinishCoreDocumentCodec.ParseRequest(bytes, workspaceRoot);
    }

    private static async ValueTask<byte[]> ReadBoundAsync(
        WorkspacePath path,
        Sha256Hash expected,
        bool proposalHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Value) || Directory.Exists(path.Value) ||
            File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Finish Core documents must be ordinary files.");
        byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
        Sha256Hash actual = proposalHash
            ? SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(bytes)
            : new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        return actual == expected
            ? bytes
            : throw new InvalidDataException("Finish Core document hash binding failed.");
    }
}
