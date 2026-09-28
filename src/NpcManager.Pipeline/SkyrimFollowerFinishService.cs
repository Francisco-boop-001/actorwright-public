using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Coordinates the bounded simple-follower finish transaction. The legacy
/// constructor remains analyze-only; callers must explicitly supply the typed
/// plugin service to admit mutation.
/// </summary>
public sealed partial class SkyrimFollowerFinishService :
    ISkyrimFollowerFinishService
{
    private readonly Func<
        SkyrimFollowerFinishRequest,
        CancellationToken,
        ValueTask<SkyrimFollowerFinishPluginSnapshot>> inspectSource;
    private readonly ISkyrimFollowerFinishRequestFileLoader requestLoader;
    private readonly ISkyrimFollowerFinishPluginService? pluginService;
    private readonly IPackageVerifyService packageVerifier;
    private readonly IWorkspacePolicy policy;
    private readonly IPackageArchiveService archiveService;
    private readonly WorkspacePath workspaceRoot;

    public SkyrimFollowerFinishService(
        Func<
            SkyrimFollowerFinishRequest,
            CancellationToken,
            ValueTask<SkyrimFollowerFinishPluginSnapshot>> inspectSource,
        ISkyrimFollowerFinishRequestFileLoader requestLoader,
        IPackageVerifyService packageVerifier,
        IWorkspacePolicy policy,
        IPackageArchiveService archiveService,
        WorkspacePath workspaceRoot)
        : this(
            inspectSource,
            requestLoader,
            pluginService: null,
            packageVerifier,
            policy,
            archiveService,
            workspaceRoot,
            analyzeOnly: true)
    {
    }

    public SkyrimFollowerFinishService(
        Func<
            SkyrimFollowerFinishRequest,
            CancellationToken,
            ValueTask<SkyrimFollowerFinishPluginSnapshot>> inspectSource,
        ISkyrimFollowerFinishRequestFileLoader requestLoader,
        ISkyrimFollowerFinishPluginService pluginService,
        IPackageVerifyService packageVerifier,
        IWorkspacePolicy policy,
        IPackageArchiveService archiveService,
        WorkspacePath workspaceRoot)
        : this(
            inspectSource,
            requestLoader,
            pluginService ?? throw new ArgumentNullException(nameof(pluginService)),
            packageVerifier,
            policy,
            archiveService,
            workspaceRoot,
            analyzeOnly: false)
    {
    }

    private SkyrimFollowerFinishService(
        Func<
            SkyrimFollowerFinishRequest,
            CancellationToken,
            ValueTask<SkyrimFollowerFinishPluginSnapshot>> inspectSource,
        ISkyrimFollowerFinishRequestFileLoader requestLoader,
        ISkyrimFollowerFinishPluginService? pluginService,
        IPackageVerifyService packageVerifier,
        IWorkspacePolicy policy,
        IPackageArchiveService archiveService,
        WorkspacePath workspaceRoot,
        bool analyzeOnly)
    {
        this.inspectSource = inspectSource ??
            throw new ArgumentNullException(nameof(inspectSource));
        this.requestLoader = requestLoader ??
            throw new ArgumentNullException(nameof(requestLoader));
        this.pluginService = analyzeOnly ? null : pluginService;
        this.packageVerifier = packageVerifier ??
            throw new ArgumentNullException(nameof(packageVerifier));
        this.policy = policy ??
            throw new ArgumentNullException(nameof(policy));
        this.archiveService = archiveService ??
            throw new ArgumentNullException(nameof(archiveService));
        this.workspaceRoot = workspaceRoot;
    }

    public ValueTask<SkyrimFollowerFinishResult> ApplyAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        IProgress<SkyrimFollowerFinishProgress>? progress,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            request,
            proposal.RequestSha256,
            proposal,
            expectedProposalSha256,
            progress,
            cancellationToken);

    public ValueTask<SkyrimFollowerFinishResult> ApplyAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        IProgress<SkyrimFollowerFinishProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        return ApplyTransactionAsync(
            request,
            verifiedRequestFileSha256,
            proposal,
            expectedProposalSha256,
            progress,
            cancellationToken);
    }

    public ValueTask<SkyrimFollowerFinishVerificationResult> VerifyAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        WorkspacePath manifest,
        CancellationToken cancellationToken) =>
        VerifyAsync(
            request,
            proposal.RequestSha256,
            proposal,
            expectedProposalSha256,
            manifest,
            cancellationToken);

    public ValueTask<SkyrimFollowerFinishVerificationResult> VerifyAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        WorkspacePath manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        return VerifyTransactionAsync(
            request,
            verifiedRequestFileSha256,
            proposal,
            expectedProposalSha256,
            manifest,
            cancellationToken);
    }

    private partial ValueTask<SkyrimFollowerFinishResult>
        ApplyTransactionAsync(
            SkyrimFollowerFinishRequest request,
            Sha256Hash verifiedRequestFileSha256,
            SkyrimFollowerFinishProposal proposal,
            Sha256Hash expectedProposalSha256,
            IProgress<SkyrimFollowerFinishProgress>? progress,
            CancellationToken cancellationToken);

    private partial ValueTask<SkyrimFollowerFinishVerificationResult>
        VerifyTransactionAsync(
            SkyrimFollowerFinishRequest request,
            Sha256Hash verifiedRequestFileSha256,
            SkyrimFollowerFinishProposal proposal,
            Sha256Hash expectedProposalSha256,
            WorkspacePath manifest,
            CancellationToken cancellationToken);

    private static ImmutableArray<Diagnostic> Refusal(
        string code,
        string message) =>
        [
            new Diagnostic(
                code,
                DiagnosticSeverity.Error,
                message)
        ];
}
