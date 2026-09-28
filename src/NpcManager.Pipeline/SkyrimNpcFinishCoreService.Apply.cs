using System.Collections.Immutable;
using System.IO.Compression;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    private const int MaximumProviderArchiveDepth = 3;
    private const int MaximumProviderArchiveMembers = 4096;
    private const long MaximumProviderArchiveExpandedBytes = 64L * 1024 * 1024;

    private sealed class FinishCoreApplyTransactionState
    {
        public string? StagingRoot { get; set; }
        public string? StagingParent { get; set; }
        public ISkyrimNpcFinishCoreOwnedDirectoryLease? StagingLease { get; set; }
        public bool StagingOwned { get; set; }
        public bool Promoted { get; set; }
        public bool ArchivePromoted { get; set; }
        public bool ArchiveTemporaryOwned { get; set; }
        public ISkyrimNpcFinishCoreOwnedFileLease? ArchiveLease { get; set; }
    }
    private async ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyExternalAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        ExternalHeadPartInstallVerificationContext? installContext,
        CancellationToken cancellationToken)
    {
        if (installContext is null)
            return RefusedApply(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                "External Finish Core apply requires a complete ephemeral install context.");
        if (proposal.ExternalHeadParts is null ||
            proposal.Status != SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite)
            return RefusedApply(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "Only an install-authorized external Finish Core proposal may be applied.");

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ExternalFinishCoreState? state = await VerifyExternalInstallAsync(
            request,
            installContext,
            requireCurrentAuthority: true,
            diagnostics,
            cancellationToken);
        if (state is null || HasErrors(diagnostics))
            return RefusedApply(diagnostics);
        if (state.ProposalAuthority.ContextFingerprint is not
                ExternalHeadPartInstallContextFingerprint current ||
            proposal.ExternalHeadParts.ContextFingerprint is not
                ExternalHeadPartInstallContextFingerprint proposed ||
            !FingerprintsEqual(current, proposed))
        {
            diagnostics.Add(new Diagnostic(
                ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                DiagnosticSeverity.Error,
                "The fresh external install fingerprint differs from the analyze-bound proposal."));
            return RefusedApply(diagnostics);
        }

        SkyrimNpcFinishCoreApplyResult result = await ApplyTransactionAsync(
            request,
            requestSha256,
            proposal,
            proposalSha256,
            cancellationToken,
            state,
            installContext);
        return result with
        {
            Diagnostics = diagnostics.Concat(result.Diagnostics).ToImmutableArray()
        };
    }

    private static bool FingerprintsEqual(
        ExternalHeadPartInstallContextFingerprint left,
        ExternalHeadPartInstallContextFingerprint right) =>
        left.Sha256 == right.Sha256 &&
        left.Observations.SequenceEqual(right.Observations);

    private async ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyTransactionAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken,
        ExternalFinishCoreState? externalState = null,
        ExternalHeadPartInstallVerificationContext? installContext = null)
    {
        var state = new FinishCoreApplyTransactionState();
        SkyrimNpcFinishCoreApplyResult result;
        try
        {
            result = await ApplyTransactionCoreAsync(
                request,
                requestSha256,
                proposal,
                proposalSha256,
                externalState,
                installContext,
                state,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation)
        {
            ImmutableArray<Diagnostic> cancellationCleanup = CleanupApplyState(
                request,
                state,
                out ImmutableArray<string> survivors);
            if (!cancellationCleanup.IsDefaultOrEmpty)
                cancellation.Data["finish-core-apply-cleanup"] = cancellationCleanup
                    .Select(item => item.Message)
                    .ToArray();
            if (!survivors.IsDefaultOrEmpty)
                cancellation.Data["finish-core-apply-cleanup-survivors"] =
                    survivors.ToArray();
            throw;
        }

        ImmutableArray<Diagnostic> cleanupDiagnostics = CleanupApplyState(
            request,
            state,
            out _);
        return cleanupDiagnostics.IsDefaultOrEmpty
            ? result
            : result with
            {
                Diagnostics = result.Diagnostics.Concat(cleanupDiagnostics)
                    .ToImmutableArray()
            };
    }

    private async ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyTransactionCoreAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        ExternalFinishCoreState? externalState,
        ExternalHeadPartInstallVerificationContext? installContext,
        FinishCoreApplyTransactionState state,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (proposal.Request is null || proposal.RequestSha256 != requestSha256 ||
                proposalSha256 != HashProposal(proposal))
                return RefusedApply(
                    "finish-core-apply-proposal-hash",
                    "The supplied proposal is not bound to the request and canonical proposal hash.");
            if (proposal.Status == SkyrimNpcFinishCoreStatus.NoChanges)
                return new SkyrimNpcFinishCoreApplyResult(
                    false, null, null, null,
                    [new Diagnostic(
                        "finish-core-no-changes",
                        DiagnosticSeverity.Info,
                        "The source already satisfies the admitted Finish Core contract; no package was published.")]);
            if (proposal.Status != SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite)
                return RefusedApply(
                    "finish-core-apply-status",
                    "Only ReadyForReviewedWrite proposals may be applied.");

            if (externalState is not null &&
                (proposal.ExternalHeadParts is null ||
                 proposal.ExternalHeadParts.Verification.CurrentInstallDependencyState !=
                     ExternalInstallDependencyState.Verified ||
                 !proposal.ExternalHeadParts.Verification.InstallReady ||
                 !proposal.ExternalHeadParts.Verification.InstallDependencyAuthority))
                return RefusedApply(
                    ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                    "External Finish Core apply requires a current verified install dependency proposal.");

            SkyrimNpcFinishCoreSourceReadResult source = await inspectSource(
                request, cancellationToken);
            diagnostics.AddRange(source.Diagnostics);
            if (!source.Admitted)
                return RefusedApply(diagnostics);
            ImmutableArray<Diagnostic> requestDiagnostics = ValidateSourceAndRequest(request, source);
            diagnostics.AddRange(requestDiagnostics);
            if (HasErrors(requestDiagnostics))
                return RefusedApply(diagnostics);

            SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(request, source);
            diagnostics.AddRange(masterPlan.Diagnostics);
            if (!masterPlan.Admitted)
                return RefusedApply(diagnostics);

            SkyrimNpcFinishCoreProposal expected = DeriveProposal(
                request,
                requestSha256,
                source,
                masterPlan,
                externalState?.ProposalAuthority);
            byte[] expectedBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                expected with { ProposalSha256 = null }, projectRoot);
            byte[] actualBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal with { ProposalSha256 = null }, projectRoot);
            if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
                return RefusedApply(
                    "finish-core-apply-stale-proposal",
                    "The proposal no longer matches a fresh source admission.");

            ImmutableArray<Diagnostic> pathDiagnostics = ValidateTransactionPaths(request);
            diagnostics.AddRange(pathDiagnostics);
            if (HasErrors(pathDiagnostics))
                return RefusedApply(diagnostics);

            string sourceRoot = request.Source.PackageRoot!.Value.Value;
            string outputRoot = request.Output.Root!.Value.Value;
            string archivePath = request.Output.Archive!.Value.Value;
            string parent = Path.GetDirectoryName(outputRoot)!;
            state.StagingParent = parent;
            state.StagingRoot = Path.Combine(
                parent,
                ".finish-core-transaction-" + Guid.NewGuid().ToString("N"));
            if (createOwnedDirectory is not null &&
                !createOwnedDirectory(state.StagingRoot))
                return RefusedApply(
                    "finish-core-transaction-staging-raced",
                    "Finish Core could not acquire exclusive ownership of its transaction staging path; the path was left untouched.");
            state.StagingLease = ownedLeaseFactory.CreateDirectory(
                projectRoot,
                new WorkspacePath(state.StagingRoot),
                "Finish Core transaction staging directory");
            state.StagingOwned = true;
            EnsureStagingLeaseCurrent(state);
            ImmutableArray<Diagnostic> preCopyPaths = ValidateTransactionPaths(request);
            if (HasErrors(preCopyPaths) || reparseAncestorProbe(state.StagingRoot))
                return RefusedApply(preCopyPaths.Add(new Diagnostic(
                    "finish-core-apply-precopy-paths",
                    DiagnosticSeverity.Error,
                    "Finish source/output paths changed or became unsafe before copy.")));
            await CopyPackageTreeAsync(sourceRoot, state.StagingRoot, cancellationToken);
            EnsureStagingLeaseCurrent(state);
            if (request.Source.PackageTreeSha256 is not { } sourceTreeAuthority ||
                SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(
                    new WorkspacePath(state.StagingRoot)) != sourceTreeAuthority)
                return RefusedApply(
                    "finish-core-apply-source-package-tree-toctou",
                    "The staged source package tree differs from its request-bound authority.");
            if (externalState is not null)
            {
                string sourceSelectedPath = Path.Combine(
                    sourceRoot,
                    RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                        .Replace('/', Path.DirectorySeparatorChar));
                byte[] sourceSelectedBytes = await File.ReadAllBytesAsync(
                    sourceSelectedPath, cancellationToken);
                if (HashBytes(sourceSelectedBytes) != externalState.SelectedManifest.ManifestSha256)
                    return RefusedApply(
                        "finish-core-apply-source-selected-toctou",
                        "The source selected dependency manifest changed after analysis.");
            }
            if (externalState is not null)
                EnsureExternalProviderBytesAbsent(
                    state.StagingRoot,
                    externalState.Result.Artifact,
                    externalState.Descriptors);

            string pluginName = request.Source.Plugin!.Value.Value;
            string pluginRelativePath = RequirePackageRelativePluginPath(request);
            string pluginRelativeNative = pluginRelativePath.Replace(
                '/', Path.DirectorySeparatorChar);
            Sha256Hash stagedSourceTreeHash = externalState is null
                ? ComputeTreeHash(
                    state.StagingRoot,
                    relative => string.Equals(
                        relative,
                        pluginRelativePath,
                        StringComparison.OrdinalIgnoreCase))
                : request.Source.PackageTreeSha256!.Value;
            string stagedPlugin = Path.Combine(state.StagingRoot, pluginRelativeNative);
            string privateSourceRoot = Path.Combine(state.StagingRoot, ".finish-core-source");
            Directory.CreateDirectory(privateSourceRoot);
            string privateSourcePlugin = Path.Combine(privateSourceRoot, pluginName);
            File.Move(stagedPlugin, privateSourcePlugin);

            string outputPlugin = Path.Combine(state.StagingRoot, pluginRelativeNative);
            if (!string.Equals(request.Output.PluginFileName, pluginName,
                    StringComparison.OrdinalIgnoreCase))
                return RefusedApply(
                    "finish-core-output-plugin-name",
                    "Finish Core preserves the source plugin filename exactly.");
            new BethesdaSkyrimNpcFinishCoreWriter().WriteToPath(
                new WorkspacePath(privateSourcePlugin),
                new WorkspacePath(outputPlugin),
                proposal,
                request.SandboxAuthority.CopiedMaster!.Value,
                cancellationToken);

            BethesdaSkyrimNpcFinishCoreVerification binaryVerification =
                new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                    new WorkspacePath(privateSourcePlugin),
                    new WorkspacePath(outputPlugin),
                    proposal,
                    request.SandboxAuthority.CopiedMaster!.Value,
                    cancellationToken);
            diagnostics.AddRange(binaryVerification.Diagnostics);
            if (!binaryVerification.Verified)
                return RefusedApply(diagnostics);

            if (externalState is not null)
            {
                Sha256Hash outputPluginHash = await HashFileAsync(
                    outputPlugin, cancellationToken);
                string stagedSelectedPath = Path.Combine(
                    state.StagingRoot,
                    RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                        .Replace('/', Path.DirectorySeparatorChar));
                ExternalHeadPartVerifiedInstallSnapshot sourceSnapshot =
                    externalState.Result.Artifact.VerifiedInstallSnapshot ??
                    throw new InvalidDataException(
                        "External Finish Core apply did not retain a verified install snapshot.");
                byte[] selectedBytes = await File.ReadAllBytesAsync(
                    stagedSelectedPath, cancellationToken);
                Sha256Hash selectedHash = HashBytes(selectedBytes);
                if (selectedHash != externalState.SelectedManifest.ManifestSha256)
                    return RefusedApply(
                        "finish-core-apply-selected-manifest-drift",
                        "The copied canonical selected dependency manifest changed during Finish.");
                long outputPluginLength = new FileInfo(outputPlugin).Length;
                SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding promotedBinding =
                    BuildPromotedOutputBinding(
                        externalState.SelectedManifest,
                        request.Source.Plugin!.Value,
                        outputPluginHash,
                        outputPluginLength,
                        new WorkspacePath(outputPlugin),
                        request.Actor.FormId!.Value,
                        proposal.MasterOrder.Select(item => new PluginName(item)).ToImmutableArray());
                string bindingPath = Path.Combine(
                    state.StagingRoot,
                    SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath
                        .Replace('/', Path.DirectorySeparatorChar));
                await WriteNewBytesAsync(
                    bindingPath,
                    SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(promotedBinding),
                    cancellationToken);
                Sha256Hash bindingHash = ReadStableFile(bindingPath).Sha256;
                ExternalHeadPartInstallVerificationResult promotedVerification =
                    externalPromotedOutputVerifier is null
                        ? throw new InvalidDataException(
                            "External Finish Core promoted output verification is unavailable.")
                        : await externalPromotedOutputVerifier.VerifyPromotedOutputAsync(
                            new ExternalHeadPartPromotedOutputVerificationRequest(
                                new WorkspacePath(state.StagingRoot),
                                new WorkspacePath(outputPlugin),
                                request.Source.Plugin!.Value,
                                outputPluginHash,
                                new WorkspacePath(stagedSelectedPath),
                                selectedHash,
                                new WorkspacePath(bindingPath),
                                bindingHash,
                                installContext,
                                RequireCurrentAuthority: true,
                                TargetActorFormId: request.Actor.FormId),
                            cancellationToken);
                diagnostics.AddRange(promotedVerification.Diagnostics);
                if (!promotedVerification.DescriptorClosureValid ||
                    promotedVerification.Artifact.CurrentInstallDependencyState !=
                        ExternalInstallDependencyState.Verified ||
                    promotedVerification.Artifact.VerifiedInstallSnapshot is null)
                    return RefusedApply(diagnostics);
                ImmutableArray<Diagnostic> externalOutputDiagnostics =
                    BethesdaSkyrimNpcFinishCoreVerifier
                        .VerifyExternalHeadPartOutputClosure(
                            new WorkspacePath(outputPlugin),
                            request.Source.Plugin!.Value,
                            request.Actor.FormId!.Value,
                            promotedVerification.Artifact.ProviderObservations,
                            externalState.Descriptors.SelectMany(item => item.Members)
                                .ToImmutableArray(),
                            cancellationToken,
                            promotedBinding.MasterOrder,
                            promotedBinding.DeclaredExternalPnam);
                diagnostics.AddRange(externalOutputDiagnostics);
                if (HasErrors(externalOutputDiagnostics))
                    return RefusedApply(diagnostics);

                externalState = externalState with
                {
                    Result = promotedVerification,
                    ProposalAuthority = externalState.ProposalAuthority with
                    {
                        Verification = promotedVerification.Artifact
                    }
                };
            }

            File.Delete(privateSourcePlugin);
            Directory.Delete(privateSourceRoot, false);
            if (reparseAncestorProbe(state.StagingRoot) ||
                reparseDescendantProbe(state.StagingRoot) ||
                reparseAncestorProbe(outputRoot) ||
                reparseAncestorProbe(Path.GetDirectoryName(archivePath)!))
                return RefusedApply(
                    "finish-core-transaction-paths",
                    "Finish promotion paths became unsafe after writing the staged output.");
            SkyrimNpcFinishCoreProposal packageProposal = proposal;
            Sha256Hash packageProposalSha = proposalSha256;
            if (externalState?.Result.Artifact.VerifiedInstallSnapshot is
                    ExternalHeadPartVerifiedInstallSnapshot finalSnapshot &&
                proposal.ExternalHeadParts is
                    SkyrimNpcFinishCoreExternalHeadPartProposalAuthority proposalExternal)
            {
                packageProposal = proposal with
                {
                    ExternalHeadParts = proposalExternal with
                    {
                        Verification = externalState.Result.Artifact with
                        {
                            HistoricalSnapshotValid = null,
                            VerifiedInstallSnapshot = null
                        },
                        ContextFingerprint = finalSnapshot.ContextFingerprint
                    }
                };
                packageProposalSha = HashProposal(packageProposal);
                packageProposal = packageProposal with
                {
                    ProposalSha256 = packageProposalSha
                };
            }
            SkyrimNpcFinishCoreManifest manifest = await PublishPackageAsync(
                request, packageProposal, requestSha256, packageProposalSha,
                new WorkspacePath(state.StagingRoot), new WorkspacePath(outputPlugin),
                binaryVerification, stagedSourceTreeHash,
                new WorkspacePath(archivePath),
                externalState,
                lease =>
                {
                    state.ArchiveLease = lease;
                    state.ArchiveTemporaryOwned = true;
                },
                cancellationToken);

            // PublishPackageAsync writes the archive before this directory move.
            EnsureStagingLeaseCurrent(state);
            EnsureArchiveLeaseCurrent(state);
            if (HasReparsePoint(archivePath + ".tmp") ||
                reparseAncestorProbe(state.StagingRoot) ||
                reparseAncestorProbe(archivePath + ".tmp") ||
                reparseAncestorProbe(Path.GetDirectoryName(archivePath)!) ||
                reparseDescendantProbe(state.StagingRoot))
                return RefusedApply(
                    "finish-core-transaction-paths",
                    "The staged archive path became unsafe before promotion.");
            await VerifyArchiveAsync(
                archivePath + ".tmp",
                state.StagingRoot,
                cancellationToken,
                state.ArchiveLease);
            EnsureStagingLeaseCurrent(state);
            EnsureArchiveLeaseCurrent(state);
            if (HasReparsePoint(archivePath + ".tmp") ||
                reparseAncestorProbe(state.StagingRoot) ||
                reparseAncestorProbe(archivePath + ".tmp") ||
                reparseAncestorProbe(Path.GetDirectoryName(archivePath)!) ||
                reparseDescendantProbe(state.StagingRoot))
                return RefusedApply(
                    "finish-core-transaction-paths",
                    "The staged archive path became unsafe after readback and before archive promotion.");
            state.ArchiveLease!.PromoteNoOverwrite();
            state.ArchivePromoted = true;
            EnsureArchiveLeaseCurrent(state);
            if (reparseAncestorProbe(archivePath) ||
                reparseAncestorProbe(state.StagingRoot) ||
                reparseAncestorProbe(outputRoot) ||
                reparseDescendantProbe(state.StagingRoot))
                return RefusedApply(
                    "finish-core-transaction-paths",
                    "The promoted archive or staged package became unsafe before package promotion.");
            EnsureStagingLeaseCurrent(state);
            state.StagingLease.PromoteNoOverwrite(new WorkspacePath(outputRoot));
            state.Promoted = true;
            state.StagingRoot = null;
            return new SkyrimNpcFinishCoreApplyResult(
                true, manifest, new WorkspacePath(outputRoot),
                new WorkspacePath(archivePath), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic(
                exception.Message.StartsWith("finish-core-master-reindex:", StringComparison.Ordinal)
                    ? "finish-core-master-reindex"
                    : exception.Message.StartsWith("finish-core-apply-evidence-closure:", StringComparison.Ordinal)
                        ? "finish-core-apply-evidence-closure"
                        : exception.Message.StartsWith("finish-core-apply-inherited-evidence-", StringComparison.Ordinal)
                            ? exception.Message[..exception.Message.IndexOf(':')]
                            : "finish-core-apply-exception",
                DiagnosticSeverity.Error,
                exception.Message));
            return RefusedApply(diagnostics);
        }
    }

    private ImmutableArray<Diagnostic> CleanupApplyState(
        SkyrimNpcFinishCoreRequest request,
        FinishCoreApplyTransactionState state,
        out ImmutableArray<string> survivors)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var retained = ImmutableArray.CreateBuilder<string>();
        if (!state.Promoted && state.StagingOwned && state.StagingRoot is not null)
        {
            string stagingPath = state.StagingLease?.Path.Value ?? state.StagingRoot;
            string? stagingFailure = null;
            bool stagingDeleted = false;
            if (state.StagingLease is not null)
            {
                try
                {
                    string? expectedParent = state.StagingParent;
                    string? actualParent = Path.GetDirectoryName(stagingPath);
                    if (expectedParent is null || actualParent is null ||
                        !string.Equals(
                            Path.GetFullPath(actualParent),
                            Path.GetFullPath(expectedParent),
                            StringComparison.OrdinalIgnoreCase) ||
                        reparseAncestorProbe(stagingPath) ||
                        reparseDescendantProbe(stagingPath))
                    {
                        stagingFailure =
                            $"Refused unsafe Finish transaction cleanup path '{stagingPath}'.";
                    }
                    else
                    {
                        EnsureStagingLeaseCurrent(state);
                        ImmutableArray<WorkspacePath> survivorsFromLease =
                            state.StagingLease.DeleteTree();
                        if (!survivorsFromLease.IsDefaultOrEmpty)
                        {
                            stagingFailure =
                                $"The owned Finish transaction cleanup left survivors: " +
                                string.Join(", ", survivorsFromLease.Select(item => item.Value));
                        }
                        else
                        {
                            stagingDeleted = true;
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    stagingFailure =
                        $"Could not safely clean Finish transaction path '{stagingPath}': {exception.Message}";
                }
                finally
                {
                    state.StagingLease.Release();
                    state.StagingLease = null;
                }
            }
            else
            {
                stagingDeleted = TryDeleteDirectory(
                    projectRoot.Value,
                    stagingPath,
                    state.StagingParent ?? projectRoot.Value,
                    reparseAncestorProbe,
                    reparseDescendantProbe,
                    out stagingFailure);
            }
            if (!stagingDeleted)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-apply-cleanup",
                    DiagnosticSeverity.Error,
                    stagingFailure ??
                    $"Could not safely clean Finish transaction path '{stagingPath}'."));
                retained.Add(stagingPath);
            }
        }
        bool archiveAtDestination = state.ArchivePromoted ||
            state.ArchiveLease?.IsPromoted == true;
        if (!state.Promoted && archiveAtDestination)
        {
            string? archive = state.ArchiveLease?.Path.Value ??
                (request.Output.Archive is { } archivePathValue
                    ? archivePathValue.Value
                    : null);
            bool deleted = false;
            string? archiveFailure = null;
            if (state.ArchiveLease is not null)
            {
                ISkyrimNpcFinishCoreOwnedFileLease lease = state.ArchiveLease;
                try
                {
                    if (archive is null || reparseAncestorProbe(archive))
                    {
                        archiveFailure =
                            $"Refused unsafe Finish archive cleanup path '{archive ?? lease.Path.Value}'.";
                    }
                    else
                    {
                        EnsureArchiveLeaseCurrent(state);
                        deleted = lease.TryDelete(out archiveFailure);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    archiveFailure =
                        $"Could not safely clean Finish archive path '{lease.Path.Value}': {exception.Message}";
                }
                state.ArchiveLease.Dispose();
                state.ArchiveLease = null;
            }
            else
            {
                deleted = TryDeleteFile(
                    projectRoot.Value,
                    archive,
                    reparseAncestorProbe,
                    out archiveFailure);
            }
            if (!deleted)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-apply-cleanup",
                    DiagnosticSeverity.Error,
                    archiveFailure!));
                if (archive is not null) retained.Add(archive);
            }
        }
        if (!state.Promoted && state.ArchiveTemporaryOwned &&
            !archiveAtDestination)
        {
            string? temporaryArchive = state.ArchiveLease?.Path.Value ??
                (request.Output.Archive is { } archive
                    ? archive.Value + ".tmp"
                    : null);
            bool deleted = false;
            string? archiveFailure = null;
            if (state.ArchiveLease is not null)
            {
                ISkyrimNpcFinishCoreOwnedFileLease lease = state.ArchiveLease;
                try
                {
                    if (temporaryArchive is null ||
                        reparseAncestorProbe(temporaryArchive))
                    {
                        archiveFailure =
                            $"Refused unsafe Finish archive cleanup path '{temporaryArchive ?? lease.Path.Value}'.";
                    }
                    else
                    {
                        EnsureArchiveLeaseCurrent(state);
                        deleted = lease.TryDelete(out archiveFailure);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        InvalidDataException or ArgumentException or InvalidOperationException)
                {
                    archiveFailure =
                        $"Could not safely clean Finish archive path '{lease.Path.Value}': {exception.Message}";
                }
                state.ArchiveLease.Dispose();
                state.ArchiveLease = null;
            }
            else
            {
                deleted = TryDeleteFile(
                    projectRoot.Value,
                    temporaryArchive,
                    reparseAncestorProbe,
                    out archiveFailure);
            }
            if (!deleted)
            {
                diagnostics.Add(new Diagnostic(
                    "finish-core-apply-cleanup",
                    DiagnosticSeverity.Error,
                    archiveFailure!));
                if (temporaryArchive is not null) retained.Add(temporaryArchive);
            }
        }
        state.StagingLease?.Release();
        state.StagingLease = null;
        state.ArchiveLease?.Dispose();
        state.ArchiveLease = null;
        survivors = retained.ToImmutable();
        return diagnostics.ToImmutable();
    }

    private static void EnsureStagingLeaseCurrent(
        FinishCoreApplyTransactionState state)
    {
        ISkyrimNpcFinishCoreOwnedDirectoryLease lease =
            state.StagingLease ??
            throw new InvalidOperationException(
                "Finish Core staging ownership was not retained.");
        lease.EnsureCurrent();
    }

    private static void EnsureArchiveLeaseCurrent(
        FinishCoreApplyTransactionState state)
    {
        ISkyrimNpcFinishCoreOwnedFileLease lease =
            state.ArchiveLease ??
            throw new InvalidOperationException(
                "Finish Core archive ownership was not retained.");
        lease.EnsureCurrent();
    }

    private Sha256Hash HashProposal(SkyrimNpcFinishCoreProposal proposal) =>
        SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with { ProposalSha256 = null }, projectRoot));

    private static void EnsureExternalProviderBytesAbsent(
        string stagingRoot,
        ExternalHeadPartInstallVerificationArtifact artifact,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors)
    {
        HashSet<string> providerNames = artifact.ProviderObservations
            .Select(item => item.ProviderPlugin.Value)
            .Concat(descriptors.Select(item => item.Provider.Plugin.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> providerPaths = descriptors
            .SelectMany(ExternalProviderAssetPaths)
            .SelectMany(PackageRelativeCandidates)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long expandedArchiveBytes = 0;
        foreach (string file in EnumerateOrdinaryFiles(stagingRoot))
        {
            string name = Path.GetFileName(file);
            string relative = Path.GetRelativePath(stagingRoot, file)
                .Replace('\\', '/');
            if (providerNames.Contains(name) || providerPaths.Contains(relative))
                throw new InvalidDataException(
                    $"External provider bytes must remain outside the Finish package: '{relative}'.");
            if (string.Equals(Path.GetExtension(file), ".zip",
                    StringComparison.OrdinalIgnoreCase))
            {
                FileInfo info = new(file);
                if (info.Length > MaximumProviderArchiveExpandedBytes)
                    throw new InvalidDataException(
                        $"External provider archive '{relative}' exceeds the bounded archive size.");
                InspectProviderArchive(
                    File.ReadAllBytes(file), relative, 0, providerNames, providerPaths,
                    ref expandedArchiveBytes);
            }
        }
    }

    private static void InspectProviderArchive(
        byte[] archiveBytes,
        string archiveIdentity,
        int depth,
        HashSet<string> providerNames,
        HashSet<string> providerPaths,
        ref long expandedBytes)
    {
        if (depth > MaximumProviderArchiveDepth)
            throw new InvalidDataException(
                $"External provider archive nesting exceeds the bounded depth: '{archiveIdentity}'.");
        using var input = new MemoryStream(archiveBytes, writable: false);
        using ZipArchive archive = new(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumProviderArchiveMembers)
            throw new InvalidDataException(
                $"External provider archive member count exceeds the bounded limit: '{archiveIdentity}'.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                throw new InvalidDataException(
                    $"External provider archive contains a directory member: '{archiveIdentity}:{entry.FullName}'.");
            string entryRelative = CanonicalArchiveMemberName(entry.FullName);
            if (!names.Add(entryRelative))
                throw new InvalidDataException(
                    $"External provider archive contains a duplicate or aliased member: '{archiveIdentity}:{entryRelative}'.");
            string dataRelative = entryRelative.StartsWith("Data/",
                StringComparison.OrdinalIgnoreCase) ? entryRelative : "Data/" + entryRelative;
            if (providerNames.Contains(Path.GetFileName(entryRelative)) ||
                providerPaths.Contains(dataRelative))
                throw new InvalidDataException(
                    $"External provider bytes must remain outside Finish archives: '{archiveIdentity}:{entryRelative}'.");
            if (entry.Length > MaximumProviderArchiveExpandedBytes - expandedBytes)
                throw new InvalidDataException(
                    $"External provider archive expansion exceeds the bounded byte limit: '{archiveIdentity}'.");
            expandedBytes += entry.Length;
            if (entryRelative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using Stream entryStream = entry.Open();
                using var nested = new MemoryStream();
                entryStream.CopyTo(nested);
                InspectProviderArchive(nested.ToArray(),
                    archiveIdentity + ":" + entryRelative, depth + 1,
                    providerNames, providerPaths, ref expandedBytes);
            }
        }
    }

    private static string CanonicalArchiveMemberName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\\') ||
            value.StartsWith('/') ||
            value.Contains(':') || value.Any(char.IsControl))
            throw new InvalidDataException("Archive member path is not canonical.");
        string[] segments = value.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Archive member path contains an alias segment.");
        return value;
    }

    private static IEnumerable<AssetPath> ExternalProviderAssetPaths(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        foreach (ExternalHeadPartAssetDependency asset in descriptor.Assets)
        {
            yield return asset.Path;
            if (asset.ArchiveMember is { } archive)
            {
                yield return archive.ArchivePath;
                yield return archive.MemberPath;
            }
        }
        foreach (ExternalHeadPartRecordDependency member in descriptor.Members)
        {
            if (member.ModelNif is { } model)
                yield return model;
        }
        foreach (ExternalHeadPartPhysicsShapeBinding shape in descriptor.Physics.Shapes)
        {
            yield return shape.ModelNif;
            yield return shape.XmlPath;
        }
        if (descriptor.Physics.MappingAuthority is { } mapping)
            yield return mapping.Path;
    }

    private static IEnumerable<string> PackageRelativeCandidates(AssetPath path)
    {
        string value = path.Value.Replace('\\', '/');
        if (value.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            yield return value;
        else
            yield return "Data/" + value;
    }

    private static SkyrimNpcFinishCoreApplyResult RefusedApply(
        string code,
        string message) =>
        new(false, null, null, null,
            [new Diagnostic(code, DiagnosticSeverity.Error, message)]);

    private static SkyrimNpcFinishCoreApplyResult RefusedApply(
        IEnumerable<Diagnostic> diagnostics) =>
        new(false, null, null, null, diagnostics.ToImmutableArray());
}
