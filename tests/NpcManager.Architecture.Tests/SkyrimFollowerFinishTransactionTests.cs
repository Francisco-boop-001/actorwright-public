using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly string[] FollowerFinishEvidenceAdditions =
    [
        "evidence/follower-finish-request.json",
        "evidence/follower-finish-proposal.json",
        "evidence/follower-finish-verification.json",
        "evidence/source-package-binding.json",
        "evidence/world-conflict-audit.json",
        "evidence/runtime-test-instructions.json"
    ];

    private static readonly string[] FollowerFinishRootDocuments =
    [
        "BUILD_INFO.txt",
        "README-NPCMANAGER-RUNTIME-TEST.txt",
        "RUNTIME-TEST-INSTRUCTIONS.md"
    ];

    private static readonly string[] FollowerFinishFailureStages =
    [
        "source-extracted",
        "plugin-written",
        "plugin-verified",
        "manifest-written",
        "destination-promoted",
        "final-verified",
        "archive-written"
    ];
    private static byte[]? followerFinishVerifiedPluginReplay;

    private static async Task TestSkyrimFollowerFinishTransaction()
    {
        await TestFollowerFinishSuccessfulTransactionAsync();
        await TestFollowerFinishFreshFinalVerificationAsync();
        await TestFollowerFinishSourceSwapFailsClosedAsync();
        await TestFollowerFinishArchiveServiceFailureAsync(1);
        await TestFollowerFinishArchiveServiceFailureAsync(2);
        await TestFollowerFinishReplacementProtectionAsync(
            "destination-promoted");
        await TestFollowerFinishReplacementProtectionAsync(
            "archive-written");
        await TestFollowerFinishCleanupFailureIsDiagnosedAsync();
        await TestFollowerFinishCancellationMatrixAsync();
        await TestFollowerFinishArchiveCommitCancellationAsync();
        await TestFollowerFinishFailureCleanupMatrixAsync();
        await TestFollowerFinishArchiveRootDocumentCollisionAsync();
    }

    private static async Task
        TestFollowerFinishSuccessfulTransactionAsync()
    {
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync("success");
        SkyrimFollowerFinishResult legacyRefusal =
            await transaction.AnalysisService.ApplyAsync(
                transaction.Request,
                transaction.Proposal,
                transaction.ProposalSha256,
                progress: null,
                CancellationToken.None);
        Assert(
            !legacyRefusal.Completed &&
            legacyRefusal.Diagnostics.Any(diagnostic =>
                diagnostic.Code ==
                "follower-finish-plugin-service-unavailable"),
            "A legacy analyze-only service silently admitted a follower-finish write.");
        AssertFollowerFinishTransactionClean(transaction);
        byte[] sourceZipBefore =
            await File.ReadAllBytesAsync(
                transaction.Request.Source.Zip.Value);
        SkyrimFollowerFinishResult result =
            await transaction.WriteService.ApplyAsync(
                transaction.Request,
                transaction.Proposal,
                transaction.ProposalSha256,
                progress: null,
                CancellationToken.None);

        Assert(
            result.Completed &&
            result.PluginWrite?.Written == true &&
            result.PackageVerification?.Verified == true &&
            result.PackageArchive?.Written == true &&
            !result.RuntimeAuthority,
            "The complete follower-finish transaction did not return STATIC_PASS_RUNTIME_REQUIRED: " +
            string.Join(
                " | ",
                result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert(
            Directory.Exists(transaction.Request.OutputRoot.Value) &&
            File.Exists(transaction.Request.OutputZip.Value),
            "A successful follower-finish transaction omitted its promoted root or archive.");
        byte[] sourceZipAfter =
            await File.ReadAllBytesAsync(
                transaction.Request.Source.Zip.Value);
        Assert(
            sourceZipBefore.SequenceEqual(sourceZipAfter),
            "The successful transaction changed the immutable source ZIP.");

        Dictionary<string, byte[]> sourceEntries =
            ReadFollowerFinishArchive(
                transaction.Request.Source.Zip.Value);
        Dictionary<string, byte[]> outputEntries =
            EnumerateFollowerFinishTree(
                transaction.Request.OutputRoot.Value);
        string plugin = transaction.Request.Source.Plugin.Value;
        string diagnostic =
            "diag-" +
            Path.GetFileNameWithoutExtension(plugin)
                .ToLowerInvariant() +
            ".txt";

        var expectedOutputPaths = new HashSet<string>(
            sourceEntries.Keys.Select(path =>
                FollowerFinishRootDocuments.Contains(
                    path,
                    StringComparer.OrdinalIgnoreCase)
                    ? path
                    : "Data/" + path),
            StringComparer.OrdinalIgnoreCase);
        expectedOutputPaths.UnionWith(FollowerFinishEvidenceAdditions);
        expectedOutputPaths.Add("Data/" + diagnostic);
        expectedOutputPaths.Add("npcmanager-package.json");
        Assert(
            expectedOutputPaths.SetEquals(outputEntries.Keys),
            "The promoted package file closure widened or dropped an admitted source/evidence path.");

        foreach ((string sourcePath, byte[] sourceBytes) in sourceEntries)
        {
            string outputPath = FollowerFinishRootDocuments.Contains(
                sourcePath,
                StringComparer.OrdinalIgnoreCase)
                ? sourcePath
                : "Data/" + sourcePath;
            if (string.Equals(
                    sourcePath,
                    plugin,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    sourcePath,
                    diagnostic,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            Assert(
                sourceBytes.SequenceEqual(outputEntries[outputPath]),
                $"Source-preserved payload '{sourcePath}' changed.");
        }
        Assert(
            !sourceEntries[plugin].SequenceEqual(
                outputEntries["Data/" + plugin]),
            "The promoted plugin was not replaced by the verified follower-finish candidate.");
        byte[] expectedDiagnostic =
            FollowerFinishExpectedDiagnostic(transaction.Request);
        Assert(
            outputEntries["Data/" + diagnostic]
                .SequenceEqual(expectedDiagnostic),
            "The follower-finish diagnostic was not the exact request-bound generic batch.");

        Dictionary<string, byte[]> archiveEntries =
            ReadFollowerFinishArchive(
                transaction.Request.OutputZip.Value);
        Assert(
            new HashSet<string>(
                    sourceEntries.Keys.Append(diagnostic),
                    StringComparer.OrdinalIgnoreCase)
                .SetEquals(archiveEntries.Keys),
            "The deterministic no-wrapper archive did not retain the exact admitted path closure.");
        foreach (string document in FollowerFinishRootDocuments)
        {
            Assert(
                sourceEntries[document].SequenceEqual(
                    archiveEntries[document]),
                $"Declared root document '{document}' was regenerated instead of byte-preserved.");
        }

        var manifestPath = new WorkspacePath(Path.Combine(
            transaction.Request.OutputRoot.Value,
            "npcmanager-package.json"));
        SkyrimFollowerFinishVerificationResult verification =
            await transaction.WriteService.VerifyAsync(
                transaction.Request,
                transaction.Proposal,
                transaction.ProposalSha256,
                manifestPath,
                CancellationToken.None);
        Assert(
            verification.Verified &&
            verification.PluginVerification?.Verified == true &&
            verification.PackageVerification?.Verified == true,
            "Read-only final verification did not close the promoted package.");
        await File.WriteAllTextAsync(
            Path.Combine(
                transaction.Request.OutputRoot.Value,
                "Data",
                diagnostic),
            "unbound diagnostic mutation");
        SkyrimFollowerFinishVerificationResult
            mutatedDiagnosticVerification =
                await transaction.WriteService.VerifyAsync(
                    transaction.Request,
                    transaction.Proposal,
                    transaction.ProposalSha256,
                    manifestPath,
                    CancellationToken.None);
        Assert(
            !mutatedDiagnosticVerification.Verified,
            "A mutated diagnostic batch retained package authority.");
        followerFinishVerifiedPluginReplay =
            outputEntries["Data/" + plugin].ToArray();
        Assert(
            !Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(
                        transaction.Request.OutputRoot.Value)!,
                    Path.GetFileName(
                        transaction.Request.OutputRoot.Value) +
                    ".stage-*")
                .Any() &&
            !Directory.EnumerateFiles(
                    Path.GetDirectoryName(
                        transaction.Request.OutputZip.Value)!,
                    Path.GetFileNameWithoutExtension(
                        transaction.Request.OutputZip.Value) +
                    ".verify-*.zip")
                .Any(),
            "The successful transaction retained a unique stage or repeat archive.");
    }

    private static async Task
        TestFollowerFinishFailureCleanupMatrixAsync()
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The real successful transaction did not retain a verified plugin replay for cleanup-only injections.");
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync(
                "failure-matrix",
                new ReplayFollowerFinishPluginService(
                    followerFinishVerifiedPluginReplay!));
        byte[] sourceZipBefore =
            await File.ReadAllBytesAsync(
                transaction.Request.Source.Zip.Value);
        foreach (string failureStage in
                 FollowerFinishFailureStages)
        {
            string unrelatedRoot =
                transaction.Request.OutputRoot.Value +
                ".unrelated-" +
                failureStage;
            string unrelatedArchive =
                transaction.Request.OutputZip.Value +
                ".unrelated-" +
                failureStage;
            Directory.CreateDirectory(unrelatedRoot);
            await File.WriteAllTextAsync(
                Path.Combine(unrelatedRoot, "keep.txt"),
                "keep");
            await File.WriteAllTextAsync(
                unrelatedArchive,
                "keep");

            SkyrimFollowerFinishResult result =
                await transaction.WriteService.ApplyAsync(
                    transaction.Request,
                    transaction.Proposal,
                    transaction.ProposalSha256,
                    new ThrowAtFollowerFinishStage(
                        failureStage),
                    CancellationToken.None);

            Assert(
                !result.Completed &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code ==
                    "follower-finish-transaction-failed"),
                $"Injected failure after '{failureStage}' did not fail closed.");
            AssertFollowerFinishTransactionClean(transaction);
            byte[] sourceZipAfter =
                await File.ReadAllBytesAsync(
                    transaction.Request.Source.Zip.Value);
            Assert(
                sourceZipBefore.SequenceEqual(sourceZipAfter),
                $"Injected failure after '{failureStage}' changed the immutable source ZIP.");
            Assert(
                Directory.Exists(unrelatedRoot) &&
                File.Exists(
                    Path.Combine(
                        unrelatedRoot,
                        "keep.txt")) &&
                File.Exists(unrelatedArchive) &&
                await File.ReadAllTextAsync(
                    unrelatedArchive) == "keep",
                $"Injected failure after '{failureStage}' cleaned an unrelated sibling.");
            Assert(
                !Directory.EnumerateFileSystemEntries(
                        Path.GetDirectoryName(
                            transaction.Request.OutputRoot.Value)!,
                        Path.GetFileName(
                            transaction.Request.OutputRoot.Value) +
                        ".stage-*")
                    .Any() &&
                !Directory.EnumerateFiles(
                        Path.GetDirectoryName(
                            transaction.Request.OutputZip.Value)!,
                        Path.GetFileNameWithoutExtension(
                            transaction.Request.OutputZip.Value) +
                        ".verify-*.zip")
                    .Any(),
                $"Injected failure after '{failureStage}' retained a unique stage or repeat archive.");
        }
    }

    private static async Task
        TestFollowerFinishFreshFinalVerificationAsync()
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The successful transaction did not retain a verified plugin replay.");
        var pluginService =
            new RejectSecondFollowerFinishVerificationService(
                followerFinishVerifiedPluginReplay!);
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync(
                "fresh-final-verification",
                pluginService);

        SkyrimFollowerFinishResult result =
            await transaction.WriteService.ApplyAsync(
                transaction.Request,
                transaction.Proposal,
                transaction.ProposalSha256,
                progress: null,
                CancellationToken.None);

        Assert(
            !result.Completed &&
            pluginService.VerifyCalls >= 2,
            "Final verification trusted persisted JSON instead of rerunning the plugin verifier against the promoted plugin.");
        AssertFollowerFinishTransactionClean(transaction);
    }

    private static async Task
        TestFollowerFinishSourceSwapFailsClosedAsync()
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The successful transaction did not retain a verified plugin replay.");
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync(
                "source-swap",
                new ReplayFollowerFinishPluginService(
                    followerFinishVerifiedPluginReplay!));
        string source = transaction.Request.Source.Zip.Value;
        string retainedSource = source + ".retained";
        byte[] replacement = Encoding.UTF8.GetBytes(
            "foreign source replacement");
        try
        {
            SkyrimFollowerFinishResult result =
                await transaction.WriteService.ApplyAsync(
                    transaction.Request,
                    transaction.Proposal,
                    transaction.ProposalSha256,
                    new CallbackFollowerFinishProgress(value =>
                    {
                        if (!string.Equals(
                                value.Stage,
                                "source-extracted",
                                StringComparison.Ordinal))
                            return;
                        File.Move(source, retainedSource);
                        File.WriteAllBytes(source, replacement);
                    }),
                    CancellationToken.None);

            Assert(
                !result.Completed &&
                File.ReadAllBytes(source).SequenceEqual(
                    replacement),
                "A source ZIP swap after extraction was not detected or the replacement was mutated.");
            AssertFollowerFinishTransactionClean(transaction);
        }
        finally
        {
            if (File.Exists(source))
                File.Delete(source);
            if (File.Exists(retainedSource))
                File.Move(retainedSource, source);
        }
    }

    private static async Task
        TestFollowerFinishArchiveServiceFailureAsync(
            int failureCall)
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The successful transaction did not retain a verified plugin replay.");
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync(
                "archive-failure-" + failureCall,
                new ReplayFollowerFinishPluginService(
                    followerFinishVerifiedPluginReplay!),
                archiveFailureCall: failureCall);

        SkyrimFollowerFinishResult result =
            await transaction.WriteService.ApplyAsync(
                transaction.Request,
                transaction.Proposal,
                transaction.ProposalSha256,
                progress: null,
                CancellationToken.None);

        Assert(
            !result.Completed &&
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code ==
                "hostile-archive-failure"),
            $"Archive service failure on call {failureCall} did not fail closed.");
        AssertFollowerFinishTransactionClean(transaction);
    }

    private static async Task
        TestFollowerFinishReplacementProtectionAsync(
            string failureStage)
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The successful transaction did not retain a verified plugin replay.");
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync(
                "replacement-" + failureStage,
                new ReplayFollowerFinishPluginService(
                    followerFinishVerifiedPluginReplay!));
        string movedOwnedRoot =
            transaction.Request.OutputRoot.Value + ".owned";
        string movedOwnedArchive =
            transaction.Request.OutputZip.Value + ".owned";
        string foreignMarker = Path.Combine(
            transaction.Request.OutputRoot.Value,
            "foreign.txt");
        try
        {
            SkyrimFollowerFinishResult result =
                await transaction.WriteService.ApplyAsync(
                    transaction.Request,
                    transaction.Proposal,
                    transaction.ProposalSha256,
                    new CallbackFollowerFinishProgress(value =>
                    {
                        if (!string.Equals(
                                value.Stage,
                                failureStage,
                                StringComparison.Ordinal))
                            return;
                        if (string.Equals(
                                failureStage,
                                "destination-promoted",
                                StringComparison.Ordinal))
                        {
                            Directory.Move(
                                transaction.Request.OutputRoot.Value,
                                movedOwnedRoot);
                            Directory.CreateDirectory(
                                transaction.Request.OutputRoot.Value);
                            File.WriteAllText(
                                foreignMarker,
                                "foreign-root");
                        }
                        else
                        {
                            File.Move(
                                transaction.Request.OutputZip.Value,
                                movedOwnedArchive);
                            File.WriteAllText(
                                transaction.Request.OutputZip.Value,
                                "foreign-archive");
                        }

                        throw new InvalidOperationException(
                            "Injected replacement after " +
                            failureStage + ".");
                    }),
                    CancellationToken.None);

            bool foreignPreserved = string.Equals(
                    failureStage,
                    "destination-promoted",
                    StringComparison.Ordinal)
                ? File.Exists(foreignMarker) &&
                  File.ReadAllText(foreignMarker) ==
                  "foreign-root"
                : File.Exists(
                      transaction.Request.OutputZip.Value) &&
                  File.ReadAllText(
                      transaction.Request.OutputZip.Value) ==
                  "foreign-archive";
            bool replacementPrevented = string.Equals(
                    failureStage,
                    "destination-promoted",
                    StringComparison.Ordinal)
                ? !Directory.Exists(movedOwnedRoot) &&
                  !Directory.Exists(
                      transaction.Request.OutputRoot.Value)
                : !File.Exists(movedOwnedArchive) &&
                  !File.Exists(
                      transaction.Request.OutputZip.Value);
            Assert(
                !result.Completed &&
                (replacementPrevented || foreignPreserved),
                $"Rollback after '{failureStage}' deleted a replacement path it did not own.");
        }
        finally
        {
            if (File.Exists(transaction.Request.OutputZip.Value))
                File.Delete(transaction.Request.OutputZip.Value);
            if (File.Exists(movedOwnedArchive))
                File.Delete(movedOwnedArchive);
            if (Directory.Exists(transaction.Request.OutputRoot.Value))
                Directory.Delete(
                    transaction.Request.OutputRoot.Value,
                    recursive: true);
            if (Directory.Exists(movedOwnedRoot))
                Directory.Delete(movedOwnedRoot, recursive: true);
        }
    }

    private static async Task
        TestFollowerFinishCleanupFailureIsDiagnosedAsync()
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The successful transaction did not retain a verified plugin replay.");
        await using SkyrimFollowerFinishTransactionCase transaction =
            await CreateFollowerFinishTransactionCaseAsync(
                "cleanup-diagnostic",
                new ReplayFollowerFinishPluginService(
                    followerFinishVerifiedPluginReplay!));
        FileStream? retainedLock = null;
        string? stageRoot = null;
        try
        {
            SkyrimFollowerFinishResult result =
                await transaction.WriteService.ApplyAsync(
                    transaction.Request,
                    transaction.Proposal,
                    transaction.ProposalSha256,
                    new CallbackFollowerFinishProgress(value =>
                    {
                        if (!string.Equals(
                                value.Stage,
                                "source-extracted",
                                StringComparison.Ordinal))
                            return;
                        stageRoot = Directory
                            .EnumerateDirectories(
                                Path.GetDirectoryName(
                                    transaction.Request
                                        .OutputRoot.Value)!,
                                Path.GetFileName(
                                    transaction.Request
                                        .OutputRoot.Value) +
                                ".stage-*")
                            .Single();
                        string lockedFile = Directory
                            .EnumerateFiles(
                                stageRoot,
                                "*",
                                SearchOption.AllDirectories)
                            .First();
                        retainedLock = new FileStream(
                            lockedFile,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.None);
                        throw new InvalidOperationException(
                            "Injected cleanup failure.");
                    }),
                    CancellationToken.None);

            Assert(
                !result.Completed &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code ==
                    "follower-finish-cleanup-failed"),
                "Rollback swallowed a cleanup failure instead of diagnosing the retained owned path.");
        }
        finally
        {
            retainedLock?.Dispose();
            if (stageRoot is not null &&
                Directory.Exists(stageRoot))
                Directory.Delete(stageRoot, recursive: true);
        }
    }

    private static async Task
        TestFollowerFinishCancellationMatrixAsync()
    {
        Assert(
            followerFinishVerifiedPluginReplay is not null,
            "The successful transaction did not retain a verified plugin replay.");
        foreach (string stage in FollowerFinishFailureStages)
        {
            await using SkyrimFollowerFinishTransactionCase transaction =
                await CreateFollowerFinishTransactionCaseAsync(
                    "cancel-" + stage,
                    new ReplayFollowerFinishPluginService(
                        followerFinishVerifiedPluginReplay!));
            using var cancellation = new CancellationTokenSource();
            bool canceled = false;
            try
            {
                await transaction.WriteService.ApplyAsync(
                    transaction.Request,
                    transaction.Proposal,
                    transaction.ProposalSha256,
                    new CallbackFollowerFinishProgress(value =>
                    {
                        if (string.Equals(
                                value.Stage,
                                stage,
                                StringComparison.Ordinal))
                        {
                            cancellation.Cancel();
                            cancellation.Token
                                .ThrowIfCancellationRequested();
                        }
                    }),
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert(
                canceled,
                $"Cancellation after '{stage}' did not propagate.");
            AssertFollowerFinishTransactionClean(transaction);
        }
    }

    private static async Task
        TestFollowerFinishArchiveCommitCancellationAsync()
    {
        string root = Path.Combine(
            @"K:\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "follower-finish-archive-cancel-" +
            Guid.NewGuid().ToString("N"));
        string output = root + ".zip";
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        try
        {
            byte[] plugin = [0x45, 0x53, 0x50, 0x01];
            byte[] payload = new byte[16 * 1024 * 1024];
            RandomNumberGenerator.Fill(payload);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "Data", "Cancel.esp"),
                plugin);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "Data", "payload.bin"),
                payload);
            byte[] manifest = FollowerFinishPackageManifest(
                "Cancel.esp",
                new FormId(0x800),
                [
                    ("plugin", "Data/Cancel.esp", plugin),
                    ("payload", "Data/payload.bin", payload)
                ]);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "npcmanager-package.json"),
                manifest);

            var workspaceRoot =
                new WorkspacePath(@"K:\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                workspaceRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var verifier = new PackageVerifyService(
                new PackageManifestReader(policy, workspaceRoot));
            var archive = new PackageArchiveService(
                verifier,
                policy,
                workspaceRoot);
            using var cancellation = new CancellationTokenSource();
            using var watcher = new FileSystemWatcher(
                Path.GetDirectoryName(output)!,
                Path.GetFileName(output))
            {
                NotifyFilter =
                    NotifyFilters.FileName |
                    NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => cancellation.Cancel();
            watcher.Renamed += (_, _) => cancellation.Cancel();

            PackageArchiveResult? result = null;
            bool canceled = false;
            try
            {
                result = await archive.ArchiveAsync(
                    new PackageArchiveRequest(
                        new WorkspacePath(root),
                        new WorkspacePath(output)),
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Assert(
                result?.Written == true ||
                (canceled && !File.Exists(output)),
                "Cancellation after final archive placement stranded an unowned ZIP.");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task
        TestFollowerFinishArchiveRootDocumentCollisionAsync()
    {
        string root = Path.Combine(
            @"K:\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "follower-finish-archive-collision",
            Guid.NewGuid().ToString("N"));
        string output = root + ".zip";
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        try
        {
            byte[] plugin = [0x45, 0x53, 0x50, 0x01];
            byte[] rootBuild = Encoding.UTF8.GetBytes("root-build");
            byte[] dataBuild = Encoding.UTF8.GetBytes("data-build");
            await File.WriteAllBytesAsync(
                Path.Combine(root, "Data", "Collision.esp"),
                plugin);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "BUILD_INFO.txt"),
                rootBuild);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "Data", "BUILD_INFO.txt"),
                dataBuild);
            byte[] manifest = FollowerFinishPackageManifest(
                "Collision.esp",
                new FormId(0x800),
                [
                    ("plugin", "Data/Collision.esp", plugin),
                    ("source-root-document", "BUILD_INFO.txt", rootBuild),
                    ("payload", "Data/BUILD_INFO.txt", dataBuild)
                ]);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "npcmanager-package.json"),
                manifest);

            var workspaceRoot =
                new WorkspacePath(@"K:\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                workspaceRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var verifier = new PackageVerifyService(
                new PackageManifestReader(policy, workspaceRoot));
            var archive = new PackageArchiveService(
                verifier,
                policy,
                workspaceRoot);
            PackageArchiveResult result =
                await archive.ArchiveAsync(
                    new PackageArchiveRequest(
                        new WorkspacePath(root),
                        new WorkspacePath(output)),
                    CancellationToken.None);
            Assert(
                !result.Written &&
                !File.Exists(output) &&
                result.Diagnostics.Any(diagnostic =>
                    diagnostic.Code ==
                    "package-archive-entry-collision"),
                "A declared root document alias collided silently with a Data-root archive entry.");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SkyrimFollowerFinishTransactionCase>
        CreateFollowerFinishTransactionCaseAsync(
            string name,
            ISkyrimFollowerFinishPluginService? writePluginService =
                null,
            int archiveFailureCall = 0)
    {
        SkyrimFollowerFinishProposalFixture proposalFixture =
            await SkyrimFollowerFinishProposalFixture.CreateAsync();
        SkyrimFollowerFinishCoreFixture coreFixture =
            await SkyrimFollowerFinishCoreFixture.CreateAsync();
        try
        {
            FollowerFinishCase sourceCase =
                await proposalFixture.CreateCaseAsync(name);
            string providerPath =
                WriteFollowerFinishPlacementProvider(
                    coreFixture,
                    "transaction-provider-" + name,
                    includeCanonicalMarker: true,
                    mutateCanonicalMarker: false);
            string placementEvidence =
                WriteFollowerFinishPlacementEvidence(
                    coreFixture,
                    sourceCase.Request,
                    "synthetic-skyrim-exterior-coordinate-audit",
                    "Skyrim.esm|0x00ABCDEF",
                    "transaction-placement-" + name + ".json");
            SkyrimFollowerFinishExternalAuthorities authorities =
                new(
                    new SkyrimFollowerFinishFileAuthority(
                        new WorkspacePath(placementEvidence),
                        new FileInfo(placementEvidence).Length,
                        HashFollowerFinishTransactionFile(
                            placementEvidence)),
                    [
                        new SkyrimFollowerFinishPluginProviderAuthority(
                            new PluginName("Skyrim.esm"),
                            new WorkspacePath(providerPath),
                            new FileInfo(providerPath).Length,
                            HashFollowerFinishTransactionFile(
                                providerPath))
                    ]);
            SkyrimFollowerFinishRequest request =
                sourceCase.Request with
                {
                    ExternalAuthorities = authorities,
                    Sandbox = sourceCase.Request.Sandbox with
                    {
                        Procedure = "Sandbox"
                    }
                };
            SkyrimFollowerFinishProposalResult analysis =
                await proposalFixture.Service.AnalyzeAsync(
                    request,
                    FollowerFinishRequestFileSha256,
                    sourceCase.ProposalPath,
                    CancellationToken.None);
            if (!analysis.Proposed ||
                analysis.Proposal is null ||
                analysis.ProposalSha256 is null)
            {
                throw new InvalidOperationException(
                    "Transaction fixture analysis refused: " +
                    string.Join(
                        " | ",
                        analysis.Diagnostics.Select(diagnostic =>
                            $"{diagnostic.Code}: {diagnostic.Message}")));
            }

            SkyrimFollowerFinishService writeService =
                CreateFollowerFinishWriteService(
                    proposalFixture.WorkspaceRoot,
                    writePluginService,
                    writePluginService is null
                        ? null
                        : analysis.Proposal.SourceSnapshot,
                    archiveFailureCall);
            return new SkyrimFollowerFinishTransactionCase(
                proposalFixture,
                coreFixture,
                proposalFixture.Service,
                writeService,
                request,
                analysis.Proposal,
                analysis.ProposalSha256.Value);
        }
        catch
        {
            await proposalFixture.DisposeAsync();
            await coreFixture.DisposeAsync();
            throw;
        }
    }

    private static SkyrimFollowerFinishService
        CreateFollowerFinishWriteService(
            WorkspacePath workspaceRoot,
            ISkyrimFollowerFinishPluginService? writePluginService =
                null,
            SkyrimFollowerFinishPluginSnapshot? cachedSnapshot =
                null,
            int archiveFailureCall = 0)
    {
        var policy = new KOnlyWorkspacePolicy(
            workspaceRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var manifestReader =
            new PackageManifestReader(policy, workspaceRoot);
        var verifier = new PackageVerifyService(manifestReader);
        var pluginService =
            new BethesdaSkyrimFollowerFinishPluginService(
                policy,
                workspaceRoot);
        var sourceReader =
            new SkyrimFollowerFinishSourcePackageReader(
                workspaceRoot,
                policy,
                manifestReader,
                verifier,
                pluginService);
        var archive = new PackageArchiveService(
            verifier,
            policy,
            workspaceRoot);
        return new SkyrimFollowerFinishService(
            cachedSnapshot is null
                ? sourceReader.InspectAsync
                : (_, cancellationToken) =>
                {
                    cancellationToken
                        .ThrowIfCancellationRequested();
                    return ValueTask.FromResult(
                        cachedSnapshot);
                },
            new SkyrimFollowerFinishRequestFileLoader(
                workspaceRoot),
            writePluginService ?? pluginService,
            verifier,
            policy,
            archiveFailureCall == 0
                ? archive
                : new FailFollowerFinishArchiveCallService(
                    archive,
                    archiveFailureCall),
            workspaceRoot);
    }

    private static void AssertFollowerFinishTransactionClean(
        SkyrimFollowerFinishTransactionCase transaction)
    {
        Assert(
            !Directory.Exists(
                transaction.Request.OutputRoot.Value),
            "A failed finish transaction left a promoted package.");
        Assert(
            !File.Exists(transaction.Request.OutputZip.Value),
            "A failed finish transaction left an archive.");
        Assert(
            File.Exists(transaction.Request.Source.Zip.Value),
            "The immutable source ZIP was changed or removed.");
    }

    private static Dictionary<string, byte[]>
        ReadFollowerFinishArchive(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        var result = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            Assert(
                !entry.FullName.Contains('\\') &&
                !entry.FullName.StartsWith('/') &&
                !entry.FullName.EndsWith('/'),
                $"Archive entry '{entry.FullName}' is not a safe no-wrapper file.");
            Assert(
                result.TryAdd(
                    entry.FullName,
                    ReadFollowerFinishEntry(entry)),
                $"Archive entry '{entry.FullName}' is a duplicate/case alias.");
        }
        return result;
    }

    private static byte[] ReadFollowerFinishEntry(
        ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Dictionary<string, byte[]>
        EnumerateFollowerFinishTree(string root)
    {
        var result = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            Assert(
                result.TryAdd(relative, File.ReadAllBytes(path)),
                $"Package tree path '{relative}' is a duplicate/case alias.");
        }
        return result;
    }

    private static byte[] FollowerFinishPackageManifest(
        string plugin,
        FormId target,
        IEnumerable<(string Kind, string Path, byte[] Bytes)>
            artifacts)
    {
        var rows = new JsonArray();
        foreach ((string kind, string path, byte[] bytes) in artifacts)
        {
            rows.Add(
                new JsonObject
                {
                    ["kind"] = kind,
                    ["relativePath"] = path,
                    ["byteLength"] = bytes.LongLength,
                    ["sha256"] = Convert.ToHexString(
                            SHA256.HashData(bytes))
                        .ToLowerInvariant()
                });
        }
        return JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["edition"] = "skyrimse",
                ["presetFormat"] =
                    "skyrim-simple-follower-finish",
                ["sourcePreset"] =
                    "evidence/follower-finish-request.json",
                ["sourcePresetSha256"] =
                    new string('1', 64),
                ["sourcePlugin"] = plugin,
                ["sourcePluginSha256"] =
                    new string('2', 64),
                ["outputPlugin"] = plugin,
                ["targetFormId"] = target.ToString(),
                ["artifacts"] = rows
            });
    }

    private static byte[] FollowerFinishExpectedDiagnostic(
        SkyrimFollowerFinishRequest request) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(
                string.Join(
                    "\n",
                    "; NPC Manager follower-finish diagnostic",
                    "; status=STATIC_PASS_RUNTIME_REQUIRED",
                    "; plugin=" + request.Source.Plugin.Value,
                    "; editorId=" + request.NpcEditorId.Value,
                    "; formId=" + request.NpcFormId,
                    $"help \"{request.NpcEditorId.Value}\" 4",
                    string.Empty));

    private static Sha256Hash
        HashFollowerFinishTransactionFile(string path) =>
        new(
            Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(path))));

    private sealed class ThrowAtFollowerFinishStage(
        string stage) :
        IProgress<SkyrimFollowerFinishProgress>
    {
        public void Report(SkyrimFollowerFinishProgress value)
        {
            if (string.Equals(
                    value.Stage,
                    stage,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Injected failure after {stage}.");
        }
    }

    private sealed class CallbackFollowerFinishProgress(
        Action<SkyrimFollowerFinishProgress> callback) :
        IProgress<SkyrimFollowerFinishProgress>
    {
        public void Report(SkyrimFollowerFinishProgress value) =>
            callback(value);
    }

    private sealed class
        RejectSecondFollowerFinishVerificationService(
            byte[] verifiedPlugin) :
        ISkyrimFollowerFinishPluginService
    {
        public int VerifyCalls { get; private set; }

        public ValueTask<SkyrimFollowerFinishPluginSnapshot>
            InspectAsync(
                SkyrimFollowerFinishRequest request,
                WorkspacePath extractedPlugin,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The hostile service is write/verify-only.");

        public async ValueTask<SkyrimFollowerFinishPluginWriteResult>
            WriteAsync(
                SkyrimFollowerFinishRequest request,
                SkyrimFollowerFinishProposal proposal,
                WorkspacePath extractedPlugin,
                WorkspacePath outputPlugin,
                CancellationToken cancellationToken)
        {
            await File.WriteAllBytesAsync(
                outputPlugin.Value,
                verifiedPlugin,
                cancellationToken);
            return new SkyrimFollowerFinishPluginWriteResult(
                true,
                outputPlugin,
                HashFollowerFinishBytes(verifiedPlugin),
                []);
        }

        public async ValueTask<SkyrimFollowerFinishPluginVerification>
            VerifyAsync(
                SkyrimFollowerFinishRequest request,
                SkyrimFollowerFinishProposal proposal,
                WorkspacePath sourcePlugin,
                WorkspacePath outputPlugin,
                CancellationToken cancellationToken)
        {
            VerifyCalls++;
            byte[] source = await File.ReadAllBytesAsync(
                sourcePlugin.Value,
                cancellationToken);
            byte[] output = await File.ReadAllBytesAsync(
                outputPlugin.Value,
                cancellationToken);
            bool verified =
                VerifyCalls == 1 &&
                output.SequenceEqual(verifiedPlugin);
            return new SkyrimFollowerFinishPluginVerification(
                verified,
                HashFollowerFinishBytes(source),
                HashFollowerFinishBytes(output),
                proposal.ExistingRecordChanges,
                proposal.NewRecords,
                ["PACK", "REFR", "ACHR"],
                false,
                verified
                    ? []
                    :
                    [
                        new Diagnostic(
                            "hostile-fresh-verification-rejected",
                            DiagnosticSeverity.Error,
                            "The promoted plugin failed fresh verification.")
                    ]);
        }

        private static Sha256Hash HashFollowerFinishBytes(
            byte[] bytes) =>
            new(
                Convert.ToHexString(
                    SHA256.HashData(bytes)));
    }

    private sealed class FailFollowerFinishArchiveCallService(
        IPackageArchiveService inner,
        int failureCall) :
        IPackageArchiveService
    {
        private int calls;

        public ValueTask<RuntimeArchiveVerificationResult> VerifyRuntimeAsync(
            RuntimeArchiveVerificationRequest request, CancellationToken cancellationToken) =>
            inner.VerifyRuntimeAsync(request, cancellationToken);

        public ValueTask<PackageArchiveResult> ArchiveAsync(
            PackageArchiveRequest request,
            CancellationToken cancellationToken)
        {
            calls++;
            if (calls != failureCall)
                return inner.ArchiveAsync(
                    request,
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new PackageArchiveResult(
                    false,
                    null,
                    [
                        new Diagnostic(
                            "hostile-archive-failure",
                            DiagnosticSeverity.Error,
                            $"Injected archive service failure on call {failureCall}.")
                    ]));
        }
    }

    private sealed class ReplayFollowerFinishPluginService(
        byte[] verifiedPlugin) :
        ISkyrimFollowerFinishPluginService
    {
        public ValueTask<SkyrimFollowerFinishPluginSnapshot>
            InspectAsync(
                SkyrimFollowerFinishRequest request,
                WorkspacePath extractedPlugin,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The replay service is write/verify-only; source inspection retains the real reader.");

        public async ValueTask<SkyrimFollowerFinishPluginWriteResult>
            WriteAsync(
                SkyrimFollowerFinishRequest request,
                SkyrimFollowerFinishProposal proposal,
                WorkspacePath extractedPlugin,
                WorkspacePath outputPlugin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(
                outputPlugin.Value,
                verifiedPlugin,
                cancellationToken);
            return new SkyrimFollowerFinishPluginWriteResult(
                true,
                outputPlugin,
                HashFollowerFinishBytesForReplay(
                    verifiedPlugin),
                []);
        }

        public async ValueTask<SkyrimFollowerFinishPluginVerification>
            VerifyAsync(
                SkyrimFollowerFinishRequest request,
                SkyrimFollowerFinishProposal proposal,
                WorkspacePath sourcePlugin,
                WorkspacePath outputPlugin,
                CancellationToken cancellationToken)
        {
            byte[] source = await File.ReadAllBytesAsync(
                sourcePlugin.Value,
                cancellationToken);
            byte[] output = await File.ReadAllBytesAsync(
                outputPlugin.Value,
                cancellationToken);
            return new SkyrimFollowerFinishPluginVerification(
                output.SequenceEqual(verifiedPlugin),
                HashFollowerFinishBytesForReplay(source),
                HashFollowerFinishBytesForReplay(output),
                proposal.ExistingRecordChanges,
                proposal.NewRecords,
                ["PACK", "REFR", "ACHR"],
                false,
                []);
        }

        private static Sha256Hash
            HashFollowerFinishBytesForReplay(byte[] bytes) =>
            new(
                Convert.ToHexString(
                    SHA256.HashData(bytes)));
    }

    private sealed record SkyrimFollowerFinishTransactionCase(
        SkyrimFollowerFinishProposalFixture ProposalFixture,
        SkyrimFollowerFinishCoreFixture CoreFixture,
        SkyrimFollowerFinishService AnalysisService,
        SkyrimFollowerFinishService WriteService,
        SkyrimFollowerFinishRequest Request,
        SkyrimFollowerFinishProposal Proposal,
        Sha256Hash ProposalSha256) :
        IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await ProposalFixture.DisposeAsync();
            await CoreFixture.DisposeAsync();
        }
    }
}
