using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimFollowerFinishService
{
    private partial async ValueTask<SkyrimFollowerFinishResult>
        ApplyTransactionAsync(
            SkyrimFollowerFinishRequest request,
            Sha256Hash verifiedRequestFileSha256,
            SkyrimFollowerFinishProposal proposal,
            Sha256Hash expectedProposalSha256,
            IProgress<SkyrimFollowerFinishProgress>? progress,
            CancellationToken cancellationToken)
    {
        if (pluginService is null)
        {
            return new SkyrimFollowerFinishResult(
                false,
                null,
                null,
                null,
                false,
                Refusal(
                    "follower-finish-plugin-service-unavailable",
                    "This follower-finish service was constructed for analysis only; typed plugin mutation is unavailable."));
        }

        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        string stagePath = string.Empty;
        string repeatArchivePath = string.Empty;
        bool stageCreated = false;
        bool destinationPromoted = false;
        bool archiveWritten = false;
        FollowerFinishOwnedLease? destinationLease = null;
        FollowerFinishOwnedLease? archiveLease = null;
        FollowerFinishOwnedLease? repeatArchiveLease = null;
        Sha256Hash? destinationFingerprint = null;
        Sha256Hash? archiveFingerprint = null;
        Sha256Hash? repeatArchiveFingerprint = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateFollowerFinishApprovalAsync(
                request,
                verifiedRequestFileSha256,
                proposal,
                expectedProposalSha256,
                inspectApprovedSource: true,
                cancellationToken);
            ValidateFollowerFinishApplyPaths(request);

            stagePath =
                request.OutputRoot.Value +
                ".stage-" +
                Guid.NewGuid().ToString("N");
            repeatArchivePath = Path.Combine(
                Path.GetDirectoryName(
                    request.OutputZip.Value)!,
                Path.GetFileNameWithoutExtension(
                    request.OutputZip.Value) +
                ".verify-" +
                Guid.NewGuid().ToString("N") +
                ".zip");
            Directory.CreateDirectory(stagePath);
            stageCreated = true;
            var stageRoot = new WorkspacePath(stagePath);
            var extractedPlugin = new WorkspacePath(
                Path.Combine(
                    stagePath,
                    ".transaction",
                    request.Source.Plugin.Value));

            FollowerFinishSourcePlan sourcePlan =
                await ReadFollowerFinishSourcePlanAsync(
                    request,
                    cancellationToken);
            ImmutableArray<FollowerFinishMappedArtifact>
                sourceArtifacts =
                    await ExtractFollowerFinishSourceAsync(
                        request,
                        sourcePlan,
                        stageRoot,
                        extractedPlugin,
                        cancellationToken);
            ReportFollowerFinishProgress(
                progress,
                "source-extracted",
                15,
                "The exact source ZIP was safely extracted into a unique sibling stage.");

            var outputPlugin = new WorkspacePath(Path.Combine(
                stagePath,
                "Data",
                request.Source.Plugin.Value));
            Directory.CreateDirectory(
                Path.GetDirectoryName(outputPlugin.Value)!);
            SkyrimFollowerFinishPluginWriteResult pluginWrite =
                await pluginService.WriteAsync(
                    request,
                    proposal,
                    extractedPlugin,
                    outputPlugin,
                    cancellationToken);
            diagnostics.AddRange(pluginWrite.Diagnostics);
            if (!pluginWrite.Written ||
                pluginWrite.OutputPlugin is null ||
                pluginWrite.OutputSha256 is null ||
                !string.Equals(
                    Path.GetFullPath(
                        pluginWrite.OutputPlugin.Value.Value),
                    Path.GetFullPath(outputPlugin.Value),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The typed plugin writer did not produce the exact staged output.");
            }
            ReportFollowerFinishProgress(
                progress,
                "plugin-written",
                30,
                "The approved plugin change was written only inside the unique stage.");

            SkyrimFollowerFinishPluginVerification
                pluginVerification =
                    await pluginService.VerifyAsync(
                        request,
                        proposal,
                        extractedPlugin,
                        outputPlugin,
                        cancellationToken);
            diagnostics.AddRange(
                pluginVerification.Diagnostics);
            if (!pluginVerification.Verified ||
                pluginVerification.OutputSha256 !=
                    pluginWrite.OutputSha256 ||
                pluginVerification.RuntimeAuthority)
            {
                throw new InvalidDataException(
                    "The staged plugin failed typed semantic/binary verification.");
            }
            ReportFollowerFinishProgress(
                progress,
                "plugin-verified",
                45,
                "The staged plugin passed typed semantic and binary verification.");

            WorkspacePath stageManifest =
                await WriteFollowerFinishEvidenceAndManifestAsync(
                    request,
                    proposal,
                    expectedProposalSha256,
                    sourcePlan,
                    sourceArtifacts,
                    pluginVerification,
                    stageRoot,
                    cancellationToken);
            string transactionScratch =
                Path.Combine(stagePath, ".transaction");
            if (Directory.Exists(transactionScratch))
                Directory.Delete(
                    transactionScratch,
                    recursive: true);
            ReportFollowerFinishProgress(
                progress,
                "manifest-written",
                60,
                "The exact package manifest and bounded evidence set were written.");

            PackageVerifyResult stageVerification =
                await packageVerifier.VerifyAsync(
                    new PackageVerifyRequest(stageManifest),
                    cancellationToken);
            diagnostics.AddRange(
                stageVerification.Diagnostics);
            ImmutableArray<Diagnostic>
                stageClosureDiagnostics =
                    await VerifyFollowerFinishPackageClosureAsync(
                        request,
                        proposal,
                        expectedProposalSha256,
                        sourcePlan,
                        sourceArtifacts,
                        stageRoot,
                        stageVerification,
                        pluginVerification,
                        cancellationToken);
            diagnostics.AddRange(stageClosureDiagnostics);
            ThrowOnFollowerFinishErrors(
                stageVerification.Diagnostics.Concat(
                    stageClosureDiagnostics));

            destinationFingerprint =
                ComputeFollowerFinishTreeFingerprint(
                    stagePath);
            Directory.Move(
                stagePath,
                request.OutputRoot.Value);
            stageCreated = false;
            destinationPromoted = true;
            destinationLease =
                AcquireFollowerFinishTreeLease(
                    request.OutputRoot.Value);
            ReportFollowerFinishProgress(
                progress,
                "destination-promoted",
                72,
                "The fully verified sibling stage was atomically promoted.");

            var finalManifest = new WorkspacePath(Path.Combine(
                request.OutputRoot.Value,
                "npcmanager-package.json"));
            SkyrimFollowerFinishVerificationResult
                finalVerification =
                    await VerifyTransactionAsync(
                        request,
                        verifiedRequestFileSha256,
                        proposal,
                        expectedProposalSha256,
                        finalManifest,
                        cancellationToken);
            diagnostics.AddRange(
                finalVerification.Diagnostics);
            if (!finalVerification.Verified ||
                finalVerification.PackageVerification is null ||
                finalVerification.PluginVerification is null)
            {
                throw new InvalidDataException(
                    "The promoted package failed independent final verification.");
            }
            ReportFollowerFinishProgress(
                progress,
                "final-verified",
                82,
                "The promoted package passed a fresh read-only verification.");

            PackageArchiveResult packageArchive =
                await archiveService.ArchiveAsync(
                    new PackageArchiveRequest(
                        request.OutputRoot,
                        request.OutputZip),
                    cancellationToken);
            diagnostics.AddRange(
                packageArchive.Diagnostics);
            if (!packageArchive.Written ||
                packageArchive.Artifact is null)
            {
                throw new InvalidDataException(
                    "The deterministic install archive failed verification.");
            }
            archiveFingerprint =
                packageArchive.Artifact.ArchiveSha256;
            archiveWritten = true;
            archiveLease =
                await AcquireFollowerFinishFileLeaseAsync(
                    request.OutputZip.Value,
                    archiveFingerprint.Value,
                    cancellationToken);
            ReportFollowerFinishProgress(
                progress,
                "archive-written",
                92,
                "The no-wrapper archive was written and independently reopened.");

            PackageArchiveResult repeatArchive =
                await archiveService.ArchiveAsync(
                    new PackageArchiveRequest(
                        request.OutputRoot,
                        new WorkspacePath(repeatArchivePath)),
                    cancellationToken);
            diagnostics.AddRange(
                repeatArchive.Diagnostics);
            if (repeatArchive.Written &&
                repeatArchive.Artifact is not null)
            {
                repeatArchiveFingerprint =
                    repeatArchive.Artifact.ArchiveSha256;
                repeatArchiveLease =
                    await AcquireFollowerFinishFileLeaseAsync(
                        repeatArchivePath,
                        repeatArchiveFingerprint.Value,
                        cancellationToken);
            }
            if (!repeatArchive.Written ||
                repeatArchive.Artifact is null ||
                repeatArchiveLease is null ||
                repeatArchiveFingerprint is null ||
                packageArchive.Artifact.ArchiveSha256 !=
                    repeatArchive.Artifact.ArchiveSha256 ||
                !await FollowerFinishFilesEqualAsync(
                    request.OutputZip.Value,
                    repeatArchivePath,
                    cancellationToken))
            {
                throw new InvalidDataException(
                    "A repeat archive write was not byte-identical.");
            }
            repeatArchiveLease.Dispose();
            repeatArchiveLease = null;
            TryDeleteFollowerFinishOwnedFile(
                repeatArchivePath,
                repeatArchiveFingerprint.Value,
                diagnostics);
            ThrowOnFollowerFinishErrors(diagnostics);
            repeatArchivePath = string.Empty;

            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-static-pass-runtime-required",
                    DiagnosticSeverity.Info,
                    "STATIC_PASS_RUNTIME_REQUIRED: static package and archive gates passed; runtime authority remains false."));
            archiveLease.Dispose();
            archiveLease = null;
            destinationLease.Dispose();
            destinationLease = null;
            return new SkyrimFollowerFinishResult(
                true,
                pluginWrite,
                finalVerification.PackageVerification,
                packageArchive,
                false,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            repeatArchiveLease?.Dispose();
            repeatArchiveLease = null;
            archiveLease?.Dispose();
            archiveLease = null;
            destinationLease?.Dispose();
            destinationLease = null;
            CleanupFollowerFinishTransaction(
                request,
                stagePath,
                repeatArchivePath,
                stageCreated,
                destinationPromoted,
                archiveWritten,
                destinationFingerprint,
                archiveFingerprint,
                repeatArchiveFingerprint,
                diagnostics);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                ArgumentException or
                CryptographicException or
                InvalidOperationException)
        {
            repeatArchiveLease?.Dispose();
            repeatArchiveLease = null;
            archiveLease?.Dispose();
            archiveLease = null;
            destinationLease?.Dispose();
            destinationLease = null;
            CleanupFollowerFinishTransaction(
                request,
                stagePath,
                repeatArchivePath,
                stageCreated,
                destinationPromoted,
                archiveWritten,
                destinationFingerprint,
                archiveFingerprint,
                repeatArchiveFingerprint,
                diagnostics);
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-transaction-failed",
                    DiagnosticSeverity.Error,
                    exception.Message));
            return new SkyrimFollowerFinishResult(
                false,
                null,
                null,
                null,
                false,
                diagnostics.ToImmutable());
        }
    }

    private async ValueTask ValidateFollowerFinishApprovalAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        SkyrimFollowerFinishProposal proposal,
        Sha256Hash expectedProposalSha256,
        bool inspectApprovedSource,
        CancellationToken cancellationToken)
    {
        byte[] requestBytes = SerializeRequest(request);
        byte[] proposalBytes = SerializeProposal(proposal);
        if (HashFollowerFinishBytes(proposalBytes) !=
                expectedProposalSha256 ||
            proposal.RequestSha256 != verifiedRequestFileSha256 ||
            !requestBytes.AsSpan().SequenceEqual(
                SerializeRequest(proposal.Request)) ||
            !string.Equals(
                proposal.Operation,
                request.Operation,
                StringComparison.Ordinal) ||
            !proposal.ExistingRecordChanges.SequenceEqual(
                request.AllowedExistingRecordChanges) ||
            !proposal.NewRecords.SequenceEqual(
                request.AllowedNewRecords) ||
            proposal.NextFormId != request.Allocation.NextFormId ||
            !proposal.AllowedPackageFiles.SequenceEqual(
                request.AllowedPackageFiles) ||
            proposal.RuntimeAuthority)
        {
            throw new InvalidDataException(
                "The request/proposal binding changed after approval.");
        }

        ImmutableArray<Diagnostic> authorityDiagnostics =
            await VerifyExternalAuthoritiesAsync(
                request,
                cancellationToken);
        ThrowOnFollowerFinishErrors(authorityDiagnostics);

        if (!inspectApprovedSource)
            return;
        SkyrimFollowerFinishPluginSnapshot snapshot =
            await inspectSource(
                request,
                cancellationToken);
        if (!snapshot.Valid ||
            !Serialize(
                    SnapshotJson(snapshot))
                .AsSpan()
                .SequenceEqual(
                    Serialize(
                        SnapshotJson(
                            proposal.SourceSnapshot))))
        {
            throw new InvalidDataException(
                "The source plugin snapshot changed after proposal approval.");
        }
    }

    private void ValidateFollowerFinishApplyPaths(
        SkyrimFollowerFinishRequest request)
    {
        if (!request.OutputRoot.IsUnder(workspaceRoot) ||
            !request.OutputZip.IsUnder(workspaceRoot) ||
            File.Exists(request.OutputRoot.Value) ||
            Directory.Exists(request.OutputRoot.Value) ||
            File.Exists(request.OutputZip.Value) ||
            Directory.Exists(request.OutputZip.Value) ||
            !string.Equals(
                Path.GetExtension(
                    request.OutputZip.Value),
                ".zip",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Follower-finish outputs are outside the lab, collide, or use an invalid archive extension.");
        }

        string? rootParent =
            Path.GetDirectoryName(request.OutputRoot.Value);
        string? archiveParent =
            Path.GetDirectoryName(request.OutputZip.Value);
        if (rootParent is null ||
            archiveParent is null ||
            !Directory.Exists(rootParent) ||
            !Directory.Exists(archiveParent))
        {
            throw new InvalidDataException(
                "Follower-finish output parents must already exist.");
        }
        ThrowOnFollowerFinishErrors(
            policy.Evaluate(
                workspaceRoot,
                new WorkspacePath(rootParent)));
        ThrowOnFollowerFinishErrors(
            policy.Evaluate(
                workspaceRoot,
                new WorkspacePath(archiveParent)));

        var paths = new List<WorkspacePath>
        {
            request.Source.Zip,
            request.Source.PackageManifest,
            request.OutputRoot,
            request.OutputZip
        };
        if (request.ExternalAuthorities is not null)
        {
            paths.Add(
                request.ExternalAuthorities
                    .PlacementEvidence.Path);
            paths.AddRange(
                request.ExternalAuthorities.Providers.Select(
                    provider => provider.Path));
        }
        for (int first = 0; first < paths.Count; first++)
        {
            for (int second = first + 1;
                 second < paths.Count;
                 second++)
            {
                if (IsSameOrDescendant(
                        paths[first],
                        paths[second]) ||
                    IsSameOrDescendant(
                        paths[second],
                        paths[first]))
                {
                    throw new InvalidDataException(
                        "Source, authority, package, and archive paths must be pairwise disjoint.");
                }
            }
        }
    }

    private static void ReportFollowerFinishProgress(
        IProgress<SkyrimFollowerFinishProgress>? progress,
        string stage,
        int percent,
        string message) =>
        progress?.Report(
            new SkyrimFollowerFinishProgress(
                stage,
                percent,
                message));

    private static async ValueTask<bool>
        FollowerFinishFilesEqualAsync(
            string first,
            string second,
            CancellationToken cancellationToken)
    {
        FileInfo firstInfo = new(first);
        FileInfo secondInfo = new(second);
        if (!firstInfo.Exists ||
            !secondInfo.Exists ||
            firstInfo.Length != secondInfo.Length)
            return false;
        return await HashFollowerFinishFileAsync(
                   first,
                   cancellationToken) ==
               await HashFollowerFinishFileAsync(
                   second,
                   cancellationToken);
    }

    private static void CleanupFollowerFinishTransaction(
        SkyrimFollowerFinishRequest request,
        string stagePath,
        string repeatArchivePath,
        bool stageCreated,
        bool destinationPromoted,
        bool archiveWritten,
        Sha256Hash? destinationFingerprint,
        Sha256Hash? archiveFingerprint,
        Sha256Hash? repeatArchiveFingerprint,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (stageCreated)
            TryDeleteFollowerFinishDirectory(
                stagePath,
                diagnostics);
        if (destinationPromoted)
            TryDeleteFollowerFinishOwnedDirectory(
                request.OutputRoot.Value,
                destinationFingerprint,
                diagnostics);
        if (archiveWritten &&
            archiveFingerprint is not null)
            TryDeleteFollowerFinishOwnedFile(
                request.OutputZip.Value,
                archiveFingerprint.Value,
                diagnostics);
        if (repeatArchiveFingerprint is not null)
        {
            TryDeleteFollowerFinishOwnedFile(
                repeatArchivePath,
                repeatArchiveFingerprint.Value,
                diagnostics);
        }
        else
        {
            TryDeleteFollowerFinishFile(
                repeatArchivePath,
                diagnostics);
        }
    }

    private static void TryDeleteFollowerFinishDirectory(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"Could not remove owned directory '{path}': {exception.Message}"));
        }
    }

    private static void TryDeleteFollowerFinishFile(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"Could not remove owned file '{path}': {exception.Message}"));
        }
    }

    private static void TryDeleteFollowerFinishOwnedDirectory(
        string path,
        Sha256Hash? ownedFingerprint,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrEmpty(path) ||
            !Directory.Exists(path))
            return;
        try
        {
            if (ownedFingerprint is null ||
                ComputeFollowerFinishTreeFingerprint(path) !=
                    ownedFingerprint.Value)
            {
                diagnostics.Add(
                    new Diagnostic(
                        "follower-finish-cleanup-ownership",
                        DiagnosticSeverity.Error,
                        $"Directory '{path}' no longer has the transaction-owned fingerprint and was preserved."));
                return;
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                CryptographicException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"Could not remove owned directory '{path}': {exception.Message}"));
        }
    }

    private static void TryDeleteFollowerFinishOwnedFile(
        string path,
        Sha256Hash ownedFingerprint,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrEmpty(path) ||
            !File.Exists(path))
            return;
        try
        {
            Sha256Hash actual;
            using (var stream = File.OpenRead(path))
                actual = new Sha256Hash(
                    Convert.ToHexString(SHA256.HashData(stream)));
            if (actual != ownedFingerprint)
            {
                diagnostics.Add(
                    new Diagnostic(
                        "follower-finish-cleanup-ownership",
                        DiagnosticSeverity.Error,
                        $"File '{path}' no longer has the transaction-owned fingerprint and was preserved."));
                return;
            }
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                CryptographicException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-cleanup-failed",
                    DiagnosticSeverity.Error,
                    $"Could not remove owned file '{path}': {exception.Message}"));
        }
    }

    private static Sha256Hash
        ComputeFollowerFinishTreeFingerprint(string root)
    {
        using IncrementalHash aggregate =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(
                     value => Path.GetRelativePath(root, value),
                     StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path)
                .Replace(
                    Path.DirectorySeparatorChar,
                    '/');
            aggregate.AppendData(
                Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            aggregate.AppendData(
                SHA256.HashData(stream));
        }
        return new Sha256Hash(
            Convert.ToHexString(
                aggregate.GetHashAndReset()));
    }

    private static FollowerFinishOwnedLease
        AcquireFollowerFinishTreeLease(string root)
    {
        if (!Directory.Exists(root))
            throw new IOException(
                "The promoted package disappeared before ownership could be retained.");
        var streams = new List<FileStream>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                streams.Add(
                    new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read));
            }
            if (streams.Count == 0)
                throw new InvalidDataException(
                    "The promoted package contains no owned files.");
            return new FollowerFinishOwnedLease(streams);
        }
        catch
        {
            foreach (FileStream stream in streams)
                stream.Dispose();
            throw;
        }
    }

    private static async ValueTask<FollowerFinishOwnedLease>
        AcquireFollowerFinishFileLeaseAsync(
            string path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        try
        {
            var actual = new Sha256Hash(
                Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        stream,
                        cancellationToken)));
            if (actual != expectedSha256)
                throw new InvalidDataException(
                    "The final archive changed before ownership could be retained.");
            return new FollowerFinishOwnedLease([stream]);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private sealed class FollowerFinishOwnedLease(
        IEnumerable<FileStream> streams) :
        IDisposable
    {
        private readonly List<FileStream> streams =
            streams.ToList();

        public void Dispose()
        {
            foreach (FileStream stream in streams)
                stream.Dispose();
            streams.Clear();
        }
    }
}
