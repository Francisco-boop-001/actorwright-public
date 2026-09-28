using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class LocalOperationJournalTests
{
    private const string RequestDigest =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string ArtifactHash =
        "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const int WindowsErrorAccessDenied = 5;
    private const int WindowsErrorSharingViolation = 32;

    public static async Task RunAsync()
    {
        AssertExactContractSurface();
        await AssertHeldLockHasBoundedWaitWithoutCancellation();
        await AssertExitedWorkerCleansProcessTree();
        await AssertWorkerTimeoutCleansProcessTree();
        await AssertAppendIsRedactedAtomicAndReopened();
        await AssertExistingEntryIsNotOverwritten();
        await AssertEntryCountRetention();
        await AssertByteRetention();
        await AssertRetentionIncludesAllOrdinaryFiles();
        await AssertReturnedEntrySurvivesRetention();
        await AssertRestartConcurrencyAndClockRollback();
        await AssertCrossProcessAllocation();
        await AssertProtocolV2DiagnosticVocabularyIsPersistable();
        await AssertUnsafeStringsAreRefused();
        await AssertEffectPairAndCountBoundaries();
        await AssertPreCreateDirectorySwapCannotEscape();
        await AssertReparseTraversalIsRefused();
        await AssertOrdinaryAccessDeniedIsReportedHonestly();
        await AssertCancellationStopsBeforeWrite();
        await AssertWriterFailureReturnsWarning();
        await AssertReopenParseFailureReturnsWarning();
        await AssertRetentionFailureIsNonfatal();
    }

    public static async Task<int> RunWorkerAsync(string root)
    {
        var journal = new LocalOperationJournal(new WorkspacePath(root));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);
            if (result.Appended)
                return 0;
            if (result.Warning?.Code != "operation-journal-lock-timeout")
            {
                Console.Error.WriteLine(
                    $"{result.Warning?.Code}: {result.Warning?.Message}");
                return 1;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        Console.Error.WriteLine(
            "operation-journal-lock-timeout: retry bound exhausted");
        return 1;
    }

    public static async Task<int> RunHangingWorkerAsync(
        string marker,
        string assignmentGate)
    {
        await WaitForSignalAsync(assignmentGate, TimeSpan.FromSeconds(5));
        string executable = Path.Combine(
            AppContext.BaseDirectory, "NpcManager.Architecture.Tests.exe");
        StartDescendantProcess(
            executable, "--hang-local-operation-journal-descendant", marker);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    public static async Task<int> RunFailingWorkerAsync(
        string marker,
        string assignmentGate)
    {
        await WaitForSignalAsync(assignmentGate, TimeSpan.FromSeconds(5));
        string executable = Path.Combine(
            AppContext.BaseDirectory, "NpcManager.Architecture.Tests.exe");
        StartDescendantProcess(
            executable, "--hang-local-operation-journal-descendant", marker);
        _ = await WaitForProcessIdAsync(
            marker, TimeSpan.FromSeconds(5));
        return 23;
    }

    public static async Task<int> RunHangingDescendantAsync(string marker)
    {
        await File.WriteAllTextAsync(
            marker,
            Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static void AssertExactContractSurface()
    {
        string[] propertyNames = typeof(OperationJournalRecord)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert(propertyNames.SequenceEqual(
            [
                "Command",
                "RequestDigest",
                "Effects",
                "DiagnosticCodes",
                "DiagnosticClasses",
                "ArtifactHashes",
                "DurationMilliseconds",
                "Outcome",
                "ExitCode"
            ],
            StringComparer.Ordinal),
            "journal record exposed raw invocation or correlation fields");

        var append = typeof(ILocalOperationJournal).GetMethod("AppendAsync");
        Assert(append is not null &&
               append.ReturnType ==
                   typeof(ValueTask<OperationJournalAppendResult>),
            "journal append result contract changed");
    }

    private static async Task AssertAppendIsRedactedAtomicAndReopened()
    {
        WorkspacePath root = CreateRoot("append");
        try
        {
            var fileSystem = new TrackingJournalFileSystem();
            var journal = new LocalOperationJournal(root, fileSystem);
            OperationJournalRecord record = CreateRecord();

            OperationJournalAppendResult result = await journal.AppendAsync(
                record, CancellationToken.None);

            Assert(result.Appended && result.Path is not null,
                "journal append failed");
            Assert(result.Warning is null,
                "successful append returned a warning");
            Assert(fileSystem.FlushCount == 1,
                "journal file was not flushed before promotion");
            Assert(fileSystem.OpenReadCount == 1,
                "promoted journal file was not reopened for parsing");

            WorkspacePath persistedPath = result.Path ??
                throw new InvalidOperationException(
                    "successful append omitted its path");
            string path = persistedPath.Value;
            string text = await File.ReadAllTextAsync(path);
            Assert(!text.Contains("secret-value", StringComparison.Ordinal),
                "secret leaked into journal");
            Assert(!text.Contains("correlation-label", StringComparison.Ordinal),
                "correlation leaked into journal");
            Assert(!text.Contains("raw-option-value", StringComparison.Ordinal),
                "raw option value leaked into journal");
            Assert(text.Contains(record.RequestDigest, StringComparison.Ordinal),
                "request digest absent from journal");
            Assert(Path.GetFileName(path).StartsWith(
                    record.RequestDigest + "-", StringComparison.Ordinal) &&
                   path.EndsWith(".json", StringComparison.Ordinal),
                "journal file name is not digest-bound");

            using JsonDocument document = JsonDocument.Parse(text);
            string[] jsonProperties = document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            Assert(jsonProperties.SequenceEqual(
                [
                    "command",
                    "requestDigest",
                    "effects",
                    "diagnosticCodes",
                    "diagnosticClasses",
                    "artifactHashes",
                    "durationMilliseconds",
                    "outcome",
                    "exitCode"
                ],
                StringComparer.Ordinal),
                "journal JSON contains fields outside the digest-only contract");
            Assert(document.RootElement.GetProperty("effects")[0]
                    .GetProperty("kind").GetString() ==
                   "appendLocalOperationJournal",
                "journal effect enum wire value changed");
            JsonElement reviewedWorkspace = document.RootElement
                .GetProperty("effects")[1];
            Assert(reviewedWorkspace.GetProperty("status").GetString() ==
                   "refused" &&
                   reviewedWorkspace.GetProperty("scope").GetString() ==
                   "reviewed-workspace",
                "shared refused/reviewed-workspace vocabulary changed");
            Assert(!Directory.EnumerateFiles(
                    Path.GetDirectoryName(path)!, ".tmp-*",
                    SearchOption.TopDirectoryOnly).Any(),
                "journal append left a temporary file");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertExistingEntryIsNotOverwritten()
    {
        WorkspacePath root = CreateRoot("no-overwrite");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            string occupied = Path.Combine(journalRoot,
                RequestDigest + "-00000000000000000007.json");
            await File.WriteAllTextAsync(occupied, "sentinel");
            var journal = new LocalOperationJournal(
                root, new LocalOperationJournalFileSystem());

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(result.Appended && result.Path is not null,
                "journal did not recover from an occupied sequence");
            WorkspacePath persistedPath = result.Path ??
                throw new InvalidOperationException(
                    "successful append omitted its path");
            Assert(persistedPath.Value.EndsWith(
                    "-00000000000000000008.json", StringComparison.Ordinal),
                "journal sequence did not advance monotonically");
            Assert(await File.ReadAllTextAsync(occupied) == "sentinel",
                "journal overwrote an existing entry");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertEntryCountRetention()
    {
        WorkspacePath root = CreateRoot("count-retention");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            for (var index = 0; index < 256; index++)
                WriteEntry(journalRoot, index, 2);

            var journal = new LocalOperationJournal(root);
            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(result.Appended, "count-retention append failed");
            Assert(Directory.EnumerateFiles(journalRoot, "*.json",
                    SearchOption.TopDirectoryOnly).Count() == 256,
                "journal entry retention exceeded 256 files");
            Assert(!File.Exists(EntryPath(journalRoot, 0)),
                "count retention did not delete the oldest entry");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertByteRetention()
    {
        WorkspacePath root = CreateRoot("byte-retention");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            for (var index = 0; index < 17; index++)
                WriteEntry(journalRoot, index, 1024 * 1024);

            var journal = new LocalOperationJournal(root);
            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            long totalBytes = Directory.EnumerateFiles(journalRoot, "*.json",
                    SearchOption.TopDirectoryOnly)
                .Sum(path => new FileInfo(path).Length);
            Assert(result.Appended, "byte-retention append failed");
            Assert(totalBytes <= 16L * 1024 * 1024,
                "journal retention exceeded 16 MiB");
            Assert(File.Exists(result.Path!.Value.Value),
                "byte retention deleted the newly appended entry");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertRetentionIncludesAllOrdinaryFiles()
    {
        WorkspacePath root = CreateRoot("foreign-retention");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            for (var index = 0; index < 256; index++)
            {
                string name = index switch
                {
                    0 => ".tmp-stale",
                    1 => "malformed.json",
                    _ => $"foreign-{index:D4}.bin"
                };
                await File.WriteAllTextAsync(
                    Path.Combine(journalRoot, name), "x");
            }

            var journal = new LocalOperationJournal(root);
            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            string[] ordinary = Directory.GetFiles(
                journalRoot, "*", SearchOption.TopDirectoryOnly);
            Assert(result.Appended,
                "foreign-file retention append failed");
            Assert(ordinary.Length <= 256,
                "foreign, malformed, or temporary files escaped retention");
            Assert(!File.Exists(Path.Combine(journalRoot, ".tmp-stale")),
                "safe oldest retention did not remove stale temporary data");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertReturnedEntrySurvivesRetention()
    {
        WorkspacePath root = CreateRoot("returned-entry-retention");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            for (var index = 1; index <= 256; index++)
            {
                WriteEntry(journalRoot, index, 2);
                File.SetCreationTimeUtc(
                    EntryPath(journalRoot, index),
                    DateTime.UtcNow.AddDays(1));
            }

            var journal = new LocalOperationJournal(
                root, new LocalOperationJournalFileSystem());
            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            WorkspacePath path = result.Path ??
                throw new InvalidOperationException(
                    "retained append omitted its path");
            Assert(result.Appended && File.Exists(path.Value),
                "retention deleted the newly appended journal entry");
            Assert(Directory.GetFiles(journalRoot).Length <= 256,
                "returned-entry retention remained unbounded");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertRestartConcurrencyAndClockRollback()
    {
        WorkspacePath root = CreateRoot("allocation");
        try
        {
            var first = new LocalOperationJournal(
                root, new LocalOperationJournalFileSystem());
            OperationJournalAppendResult firstResult = await first.AppendAsync(
                CreateRecord(), CancellationToken.None);
            WorkspacePath firstPath = firstResult.Path ??
                throw new InvalidOperationException(
                    "first allocation omitted its path");
            File.SetCreationTimeUtc(
                firstPath.Value, DateTime.UtcNow.AddDays(30));

            var restartedAfterClockRollback = new LocalOperationJournal(
                root, new LocalOperationJournalFileSystem());
            OperationJournalAppendResult restartedResult =
                await restartedAfterClockRollback.AppendAsync(
                    CreateRecord(), CancellationToken.None);
            WorkspacePath restartedPath = restartedResult.Path ??
                throw new InvalidOperationException(
                    "restart allocation omitted its path");
            Assert(SequenceOf(restartedPath.Value) >
                   SequenceOf(firstPath.Value),
                "restart or clock rollback regressed journal allocation");

            Task<OperationJournalAppendResult>[] concurrent =
                Enumerable.Range(0, 16)
                    .Select(_ => new LocalOperationJournal(
                            root,
                            new LocalOperationJournalFileSystem())
                        .AppendAsync(CreateRecord(), CancellationToken.None)
                        .AsTask())
                    .ToArray();
            OperationJournalAppendResult[] results =
                await Task.WhenAll(concurrent);
            long[] sequences = results.Select(result =>
                    SequenceOf((result.Path ??
                        throw new InvalidOperationException(
                            "concurrent allocation omitted its path")).Value))
                .Order()
                .ToArray();
            Assert(results.All(result => result.Appended) &&
                   sequences.Distinct().Count() == results.Length,
                "concurrent journal allocation collided");
            Assert(sequences[0] > SequenceOf(restartedPath.Value),
                "concurrent allocation did not advance journal-wide sequence");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertCrossProcessAllocation()
    {
        WorkspacePath root = CreateRoot("cross-process-allocation");
        try
        {
            string executable = Path.Combine(
                AppContext.BaseDirectory,
                "NpcManager.Architecture.Tests.exe");
            Assert(File.Exists(executable),
                "architecture test apphost was not built");

            JournalWorkerProcess[] workers = Enumerable.Range(0, 6)
                .Select(_ => StartJournalWorker(executable, root))
                .ToArray();
            try
            {
                await Task.WhenAll(workers.Select(worker =>
                    WaitForJournalWorkerAsync(
                        worker, TimeSpan.FromSeconds(30))));
            }
            finally
            {
                foreach (JournalWorkerProcess worker in workers)
                    worker.Dispose();
            }

            long[] sequences = Directory.GetFiles(
                    CreateJournalRoot(root), "*.json",
                    SearchOption.TopDirectoryOnly)
                .Select(SequenceOf)
                .Order()
                .ToArray();
            Assert(sequences.SequenceEqual(
                    Enumerable.Range(0, 6).Select(value => (long)value)),
                "cross-process journal allocation was not monotonic and unique");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static JournalWorkerProcess StartJournalWorker(
        string executable,
        WorkspacePath root) => StartProcess(
        executable, "--append-local-operation-journal-worker", root.Value);

    private static JournalWorkerProcess StartProcess(
        string executable,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return JournalWorkerProcess.Start(start);
    }

    private static void StartDescendantProcess(
        string executable,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException(
                "A journal test descendant did not start.");
    }

    private static async Task WaitForJournalWorkerAsync(
        JournalWorkerProcess worker,
        TimeSpan timeout)
    {
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            await worker.RootExit.WaitAsync(timeout);
            if (worker.Process.ExitCode != 0)
            {
                primaryFailure = ExceptionDispatchInfo.Capture(
                    await worker.CreateExitFailureAsync(
                        "cross-process journal worker failed"));
            }
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }

        if (primaryFailure is null)
            return;

        try
        {
            await worker.TerminateAndWaitAsync();
        }
        catch (Exception cleanupException)
        {
            AttachSecondary(
                primaryFailure.SourceException, cleanupException);
        }
        primaryFailure.Throw();
    }

    private static async Task AssertUnsafeStringsAreRefused()
    {
        WorkspacePath root = CreateRoot("redaction-boundary");
        try
        {
            const string secret = "secret-value";
            OperationJournalRecord valid = CreateRecord();
            OperationJournalRecord[] unsafeRecords =
            [
                valid with { Command = secret },
                valid with
                {
                    RequestDigest = secret.PadRight(64, 'A')
                },
                valid with
                {
                    Effects =
                    [
                        ProtocolEffect.CreateUncheckedForTesting(
                            valid.Effects[0].Kind,
                            secret,
                            valid.Effects[0].Scope)
                    ]
                },
                valid with
                {
                    Effects =
                    [
                        ProtocolEffect.CreateUncheckedForTesting(
                            valid.Effects[0].Kind,
                            valid.Effects[0].Status,
                            secret)
                    ]
                },
                valid with { DiagnosticCodes = [secret] },
                valid with { DiagnosticClasses = [secret] },
                valid with
                {
                    ArtifactHashes = [secret.PadRight(64, 'A')]
                },
                valid with { Outcome = secret }
            ];
            var journal = new LocalOperationJournal(root);

            OperationJournalAppendResult[] results = new
                OperationJournalAppendResult[unsafeRecords.Length];
            for (var index = 0; index < unsafeRecords.Length; index++)
            {
                results[index] = await journal.AppendAsync(
                    unsafeRecords[index], CancellationToken.None);
            }

            Assert(results.All(result =>
                    !result.Appended && result.Path is null),
                "unsafe journal identifiers were persisted");
            Assert(results.All(result => result.Warning is
                    {
                        Code: "operation-journal-record-refused",
                        Severity: DiagnosticSeverity.Warning
                    }), "unsafe journal record warning changed");
            string journalRoot = Path.Combine(
                root.Value, ".actorwright", "operations");
            string persisted = Directory.Exists(journalRoot)
                ? string.Concat(Directory.GetFiles(journalRoot)
                    .Select(File.ReadAllText))
                : string.Empty;
            Assert(!persisted.Contains(secret, StringComparison.Ordinal),
                "a secret from a free-form journal field reached storage");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertEffectPairAndCountBoundaries()
    {
        WorkspacePath root = CreateRoot("effect-boundaries");
        try
        {
            var journal = new LocalOperationJournal(root);
            ProtocolEffect valid = ProtocolEffect.Create(
                AgentEffectKind.AppendLocalOperationJournal,
                ApplicationEffectStatus.Completed,
                ApplicationEffectScope.WorkspaceLocalJournal);
            ProtocolEffect illegalPair =
                ProtocolEffect.CreateUncheckedForTesting(
                AgentEffectKind.ReadWorkspace,
                "completed",
                "k-local-output");

            OperationJournalAppendResult illegal = await journal.AppendAsync(
                CreateRecord() with { Effects = [illegalPair] },
                CancellationToken.None);
            OperationJournalAppendResult thirtyTwo = await journal.AppendAsync(
                CreateRecord() with
                {
                    Effects = Enumerable.Repeat(valid, 32).ToImmutableArray()
                },
                CancellationToken.None);
            OperationJournalAppendResult thirtyThree = await journal.AppendAsync(
                CreateRecord() with
                {
                    Effects = Enumerable.Repeat(valid, 33).ToImmutableArray()
                },
                CancellationToken.None);

            Assert(!illegal.Appended && illegal.Warning?.Code ==
                   "operation-journal-record-refused",
                "journal admitted an illegal kind/scope pair");
            Assert(thirtyTwo.Appended && thirtyTwo.Warning is null,
                "journal refused the shared 32-effect ceiling");
            Assert(!thirtyThree.Appended && thirtyThree.Warning?.Code ==
                   "operation-journal-record-refused",
                "journal admitted 33 persisted effects");
            string persisted = await File.ReadAllTextAsync(
                thirtyTwo.Path?.Value ?? throw new InvalidOperationException(
                    "32-effect append omitted its path"));
            using JsonDocument document = JsonDocument.Parse(persisted);
            JsonElement effect = document.RootElement.GetProperty("effects")[0];
            Assert(effect.GetProperty("kind").GetString() ==
                       "appendLocalOperationJournal" &&
                   effect.GetProperty("status").GetString() == "completed" &&
                   effect.GetProperty("scope").GetString() ==
                       "workspace-local-journal",
                "journal changed exact admitted effect strings");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertProtocolV2DiagnosticVocabularyIsPersistable()
    {
        string[] expectedCodes =
        [
            "alternate-data-stream-refused",
            "finish-core-info",
            "finish-core-validation-failed",
            "finish-core-warning",
            "finish-verification-failed",
            "finish-verification-info",
            "finish-verification-path-refused",
            "finish-verification-warning",
            "finish-verification-write-failed",
            "npc-build-operation-failed",
            "npc-build-preflight-info",
            "npc-build-preflight-validation-failed",
            "npc-build-preflight-warning",
            "npc-build-validation-failed",
            "npc-build-verification-failed",
            "npc-preview-operation-failed",
            "npc-preview-validation-failed",
            "npc-preview-verification-failed",
            "option-conflict",
            "option-duplicate",
            "option-enum-value",
            "option-flag-value",
            "option-required",
            "option-sha256-value",
            "option-unknown",
            "option-value-required",
            "output-root-outside-workspace",
            "path-inspection-failed",
            "positional-unexpected",
            "preset-inspection-canonicality-failed",
            "preset-inspection-info",
            "preset-inspection-input-hash-mismatch",
            "preset-inspection-output-exists",
            "preset-inspection-path-refused",
            "preset-inspection-persistence-failed",
            "preset-inspection-validation-failed",
            "preset-inspection-warning",
            "protected-root-refused",
            "protocol-adapter-duplicate",
            "protocol-adapter-missing",
            "protocol-adapter-result-invalid",
            "protocol-command-legacy",
            "protocol-json-required",
            "protocol-operation-cancelled",
            "protocol-operation-failed",
            "protocol-unsupported",
            "reparse-point-refused",
            "review-receipt-path-refused",
            "review-receipt-persistence-failed",
            "review-receipt-validation-failed",
            "reviewed-intake-info",
            "reviewed-intake-output-exists",
            "reviewed-intake-output-overlap",
            "reviewed-intake-output-parent-missing",
            "reviewed-intake-output-reuse",
            "reviewed-intake-persistence-failed",
            "reviewed-intake-validation-failed",
            "reviewed-intake-warning",
            "schema-command-unknown",
            "schema-output-exists",
            "schema-output-outside-k-drive",
            "schema-output-parent-missing",
            "schema-output-write-failed",
            "unsafe-path-form",
            "workflow-bundle-path-refused",
            "workflow-bundle-persistence-failed",
            "workflow-bundle-validation-failed",
            "workspace-root-outside-lab"
        ];
        WorkspacePath root = CreateRoot("protocol-v2-diagnostic-codes");
        try
        {
            Assert(expectedCodes.SequenceEqual(
                    ProtocolV2DiagnosticCodes.All.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal),
                "the protocol v2 diagnostic vocabulary drifted");
            var journal = new LocalOperationJournal(root);
            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord() with
                {
                    DiagnosticCodes = expectedCodes.ToImmutableArray()
                },
                CancellationToken.None);
            Assert(result.Appended && result.Warning is null,
                "the protocol v2 diagnostic vocabulary was not journaled");

            OperationJournalAppendResult unknown = await journal.AppendAsync(
                CreateRecord() with
                {
                    DiagnosticCodes = ["raw-unregistered-diagnostic"]
                },
                CancellationToken.None);
            Assert(!unknown.Appended && unknown.Warning is
                {
                    Code: "operation-journal-record-refused"
                }, "an unregistered diagnostic code was journaled");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertPreCreateDirectorySwapCannotEscape()
    {
        WorkspacePath root = CreateRoot("pre-create-swap");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            string parked = Path.Combine(root.Value, "parked-operations");
            string outside = Path.Combine(root.Value, "outside-target");
            string attackLink = Path.Combine(root.Value, "attacker-reparse");
            Directory.CreateDirectory(outside);
            if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                    attackLink, outside, root.Value))
            {
                return;
            }
            var fileSystem = new SwappingJournalFileSystem(
                journalRoot, parked, attackLink);
            var journal = new LocalOperationJournal(root, fileSystem);

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(fileSystem.SwapAttempted &&
                   (fileSystem.SwapCreated ^ fileSystem.SwapRefused),
                "swap was neither completed nor refused by the pinned directory handle");
            if (fileSystem.SwapRefused)
            {
                Assert((fileSystem.SwapRefusalErrorCode is
                            WindowsErrorAccessDenied or WindowsErrorSharingViolation) &&
                       result.Warning?.Code == "operation-journal-write-failed",
                    "the pinned-handle rename refusal did not preserve its exact Windows error");
            }
            else
            {
                Assert(result.Warning?.Code == "operation-journal-reparse-refused",
                    "a completed reparse swap did not use the pinned-path refusal");
            }
            Assert(!Directory.EnumerateFiles(
                    outside, "*", SearchOption.TopDirectoryOnly).Any(),
                "journal creation escaped through a swapped operations path");
            Assert(!result.Appended,
                "a completed directory swap was accepted");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertReparseTraversalIsRefused()
    {
        WorkspacePath root = CreateRoot("reparse");
        try
        {
            string target = Path.Combine(root.Value, "reparse-target");
            string actorwright = Path.Combine(root.Value, ".actorwright");
            Directory.CreateDirectory(target);
            if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                    actorwright, target, root.Value))
                return;
            var journal = new LocalOperationJournal(root);

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(!result.Appended && result.Path is null,
                "journal accepted reparse traversal");
            Assert(result.Warning?.Code ==
                   "operation-journal-reparse-refused",
                "reparse refusal warning changed");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertCancellationStopsBeforeWrite()
    {
        WorkspacePath root = CreateRoot("cancellation");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var journal = new LocalOperationJournal(root);

            await AssertThrowsAsync<OperationCanceledException>(() =>
                journal.AppendAsync(CreateRecord(), cancellation.Token)
                    .AsTask());
            Assert(!Directory.Exists(Path.Combine(
                    root.Value, ".actorwright", "operations")),
                "cancelled append created journal storage");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertOrdinaryAccessDeniedIsReportedHonestly()
    {
        WorkspacePath root = CreateRoot("access-denied");
        try
        {
            var journal = new LocalOperationJournal(
                root, new AccessDeniedJournalFileSystem());

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(!result.Appended && result.Path is null,
                "access-denied append reported success");
            Assert(result.Warning is
                {
                    Code: "operation-journal-access-denied",
                    Severity: DiagnosticSeverity.Warning
                }, "ordinary access denial was misclassified");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertHeldLockHasBoundedWaitWithoutCancellation()
    {
        WorkspacePath root = CreateRoot("held-lock-timeout");
        try
        {
            _ = CreateJournalRoot(root);
            string lockPath = Path.Combine(
                root.Value, ".actorwright", "operation-journal.lock");
            var journal = new LocalOperationJournal(root);
            Task<OperationJournalAppendResult> append;
            OperationJournalAppendResult? result = null;
            bool exceededGuard = false;
            var stopwatch = Stopwatch.StartNew();
            using (new FileStream(
                       lockPath, FileMode.OpenOrCreate,
                       FileAccess.ReadWrite, FileShare.None))
            {
                append = journal.AppendAsync(
                    CreateRecord(), CancellationToken.None).AsTask();
                try
                {
                    result = await append.WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    exceededGuard = true;
                }
            }

            if (exceededGuard)
            {
                await append.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException(
                    "held journal lock had no internal bounded deadline");
            }

            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "held journal lock refusal was not prompt");
            Assert(result is
                {
                    Appended: false,
                    Path: null,
                    Warning:
                    {
                        Code: "operation-journal-lock-timeout",
                        Severity: DiagnosticSeverity.Warning
                    }
                }, "held journal lock did not return a stable warning");
            Assert(!Directory.EnumerateFiles(
                    CreateJournalRoot(root), "*.json",
                    SearchOption.TopDirectoryOnly).Any(),
                "timed-out lock append persisted a journal entry");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertWorkerTimeoutCleansProcessTree()
    {
        WorkspacePath root = CreateRoot("worker-timeout-cleanup");
        string marker = Path.Combine(root.Value, "descendant.pid");
        string assignmentGate = Path.Combine(root.Value, "assigned.signal");
        string executable = Path.Combine(
            AppContext.BaseDirectory, "NpcManager.Architecture.Tests.exe");
        JournalWorkerProcess? worker = null;
        int descendantId = 0;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            worker = StartProcess(
                executable, "--hang-local-operation-journal-worker",
                marker, assignmentGate);
            await File.WriteAllTextAsync(assignmentGate, "assigned");
            descendantId = await WaitForProcessIdAsync(
                marker, TimeSpan.FromSeconds(5), worker);

            Exception? failure = null;
            try
            {
                await WaitForJournalWorkerAsync(
                    worker, TimeSpan.FromMilliseconds(250));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(failure is TimeoutException,
                "worker timeout did not retain its original failure");
            Assert(worker.Process.HasExited && !IsProcessRunning(descendantId),
                "worker timeout did not stop and await the process tree");
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            await CompleteWorkerTestCleanupAsync(
                worker, descendantId, root, primaryFailure);
        }
    }

    private static async Task AssertExitedWorkerCleansProcessTree()
    {
        WorkspacePath root = CreateRoot("worker-exit-cleanup");
        string marker = Path.Combine(root.Value, "descendant.pid");
        string assignmentGate = Path.Combine(root.Value, "assigned.signal");
        string executable = Path.Combine(
            AppContext.BaseDirectory, "NpcManager.Architecture.Tests.exe");
        JournalWorkerProcess? worker = null;
        int descendantId = 0;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            worker = StartProcess(
                executable, "--fail-local-operation-journal-worker",
                marker, assignmentGate);
            await File.WriteAllTextAsync(assignmentGate, "assigned");
            descendantId = await WaitForProcessIdAsync(
                marker, TimeSpan.FromSeconds(5), worker);

            Exception? failure = null;
            try
            {
                await WaitForJournalWorkerAsync(
                    worker, TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert(failure is InvalidOperationException &&
                   failure.Message.Contains(
                       "cross-process journal worker failed",
                       StringComparison.Ordinal),
                "nonzero worker did not retain its original failure");
            Assert(worker.Process.HasExited && !IsProcessRunning(descendantId),
                "exited worker left its descendant running");
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            await CompleteWorkerTestCleanupAsync(
                worker, descendantId, root, primaryFailure);
        }
    }

    private static async Task CompleteWorkerTestCleanupAsync(
        JournalWorkerProcess? worker,
        int descendantId,
        WorkspacePath root,
        ExceptionDispatchInfo? primaryFailure)
    {
        ExceptionDispatchInfo? cleanupFailure = null;
        if (worker is not null)
        {
            try
            {
                await worker.TerminateAndWaitAsync();
            }
            catch (Exception exception)
            {
                CaptureCleanupFailure(ref cleanupFailure, exception);
            }
            finally
            {
                try
                {
                    worker.Dispose();
                }
                catch (Exception exception)
                {
                    CaptureCleanupFailure(ref cleanupFailure, exception);
                }
            }
        }

        try
        {
            await StopProcessIfRunningAsync(descendantId);
        }
        catch (Exception exception)
        {
            CaptureCleanupFailure(ref cleanupFailure, exception);
        }

        try
        {
            DeleteRoot(root);
        }
        catch (Exception exception)
        {
            CaptureCleanupFailure(ref cleanupFailure, exception);
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                AttachSecondary(
                    primaryFailure.SourceException,
                    cleanupFailure.SourceException);
            }
            primaryFailure.Throw();
        }
        cleanupFailure?.Throw();
    }

    private static void CaptureCleanupFailure(
        ref ExceptionDispatchInfo? cleanupFailure,
        Exception exception)
    {
        if (cleanupFailure is null)
        {
            cleanupFailure = ExceptionDispatchInfo.Capture(exception);
            return;
        }
        AttachSecondary(cleanupFailure.SourceException, exception);
    }

    private static void AttachSecondary(
        Exception primary,
        Exception secondary)
    {
        const string key = "operation-journal-test-cleanup";
        string detail = secondary.ToString();
        primary.Data[key] = primary.Data[key] is string existing
            ? existing + Environment.NewLine + detail
            : detail;
    }

    private static async Task<int> WaitForProcessIdAsync(
        string path,
        TimeSpan timeout,
        JournalWorkerProcess? worker = null)
    {
        long started = Stopwatch.GetTimestamp();
        Task? workerExit = worker?.RootExit;
        while (true)
        {
            int? processId = await TryReadProcessIdAsync(path);
            if (processId.HasValue)
                return processId.Value;

            if (workerExit is { IsCompleted: true })
            {
                await workerExit;
                processId = await TryReadProcessIdAsync(path);
                if (processId.HasValue)
                    return processId.Value;
                throw await worker!.CreateExitFailureAsync(
                    "cross-process journal worker exited before " +
                    "writing the descendant marker");
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException("The child marker was not written.");

            Task markerPoll = Task.Delay(TimeSpan.FromMilliseconds(25));
            if (workerExit is null)
            {
                await markerPoll;
            }
            else
            {
                _ = await Task.WhenAny(workerExit, markerPoll);
            }
        }
    }

    private static async Task<int?> TryReadProcessIdAsync(string path)
    {
        try
        {
            string text = await File.ReadAllTextAsync(path);
            return int.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int processId)
                ? processId
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WaitForSignalAsync(
        string path,
        TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        while (!File.Exists(path))
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException(
                    "The worker assignment signal was not written.");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
            return false;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task StopProcessIfRunningAsync(int processId)
    {
        if (processId <= 0)
            return;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task AssertWriterFailureReturnsWarning()
    {
        WorkspacePath root = CreateRoot("writer-failure");
        try
        {
            var journal = new LocalOperationJournal(
                root, new WriterFailingJournalFileSystem());

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(!result.Appended && result.Path is null,
                "failed writer reported an append");
            Assert(result.Warning is
                {
                    Code: "operation-journal-write-failed",
                    Severity: DiagnosticSeverity.Warning
                }, "writer failure warning changed");
            Diagnostic warning = result.Warning ??
                throw new InvalidOperationException(
                    "writer failure omitted its warning");
            Assert(!warning.Message.Contains(
                    "secret-value", StringComparison.Ordinal),
                "writer exception details leaked into warning");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertReopenParseFailureReturnsWarning()
    {
        WorkspacePath root = CreateRoot("parse-failure");
        try
        {
            var journal = new LocalOperationJournal(
                root, new ParseFailingJournalFileSystem());

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(!result.Appended && result.Path is null,
                "unparseable promoted journal entry was accepted");
            Assert(result.Warning?.Code ==
                   "operation-journal-write-failed",
                "parse failure warning changed");
            Assert(!Directory.EnumerateFiles(
                    CreateJournalRoot(root), "*.json",
                    SearchOption.TopDirectoryOnly).Any(),
                "unverified promoted journal entry was retained");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertRetentionFailureIsNonfatal()
    {
        WorkspacePath root = CreateRoot("retention-failure");
        try
        {
            string journalRoot = CreateJournalRoot(root);
            for (var index = 0; index < 256; index++)
                WriteEntry(journalRoot, index, 2);
            var journal = new LocalOperationJournal(
                root, new RetentionFailingJournalFileSystem());

            OperationJournalAppendResult result = await journal.AppendAsync(
                CreateRecord(), CancellationToken.None);

            Assert(result.Appended && result.Path is not null &&
                   File.Exists(result.Path.Value.Value),
                "retention failure changed successful append outcome");
            Assert(result.Warning is
                {
                    Code: "operation-journal-retention-failed",
                    Severity: DiagnosticSeverity.Warning
                }, "retention failure warning changed");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static OperationJournalRecord CreateRecord() => new(
        "capabilities",
        RequestDigest,
        [
            ProtocolEffect.Create(
                AgentEffectKind.AppendLocalOperationJournal,
                ApplicationEffectStatus.Completed,
                ApplicationEffectScope.WorkspaceLocalJournal),
            ProtocolEffect.Create(
                AgentEffectKind.ReadWorkspace,
                ApplicationEffectStatus.Refused,
                ApplicationEffectScope.ReviewedWorkspace)
        ],
        ["option-unknown"],
        ["operation"],
        [ArtifactHash],
        17,
        "succeeded",
        0);

    private static WorkspacePath CreateRoot(string name)
    {
        string root = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "tests",
            "local-operation-journal",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspacePath(root);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "The Actorwright repository root was not found from the test binary.");
    }

    private static string CreateJournalRoot(WorkspacePath root)
    {
        string path = Path.Combine(root.Value, ".actorwright", "operations");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteEntry(string journalRoot, int sequence, long size)
    {
        using var stream = new FileStream(
            EntryPath(journalRoot, sequence), FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        stream.SetLength(size);
    }

    private static string EntryPath(string journalRoot, int sequence) =>
        Path.Combine(journalRoot,
            RequestDigest + $"-{sequence:D20}.json");

    private static long SequenceOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return long.Parse(
            name[(RequestDigest.Length + 1)..],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void DeleteRoot(WorkspacePath root)
    {
        if (Directory.Exists(root.Value))
            Directory.Delete(root.Value, recursive: true);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class JournalWorkerProcess : IDisposable
    {
        private const int CapturedOutputCharacterLimit = 8192;
        private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectExtendedLimitInformation = 9;

        private readonly SafeFileHandle job;
        private readonly object outputSync = new();
        private readonly StringBuilder standardOutput = new();
        private readonly StringBuilder standardError = new();
        private readonly TaskCompletionSource<bool> standardOutputClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> standardErrorClosed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> rootExited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool standardOutputTruncated;
        private bool standardErrorTruncated;
        private bool terminationRequested;
        private bool disposed;

        private JournalWorkerProcess(
            Process process,
            SafeFileHandle job)
        {
            Process = process;
            this.job = job;
            process.OutputDataReceived += OnStandardOutput;
            process.ErrorDataReceived += OnStandardError;
            process.Exited += OnProcessExited;
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (process.HasExited)
                    rootExited.TrySetResult(true);
                process.EnableRaisingEvents = true;
                if (process.HasExited)
                    rootExited.TrySetResult(true);
            }
            catch
            {
                DetachProcessEvents();
                throw;
            }
        }

        public Process Process { get; }
        public Task RootExit => rootExited.Task;

        public async Task<InvalidOperationException> CreateExitFailureAsync(
            string message)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!RootExit.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Journal worker diagnostics require an exited process.");
            }

            try
            {
                await Task.WhenAll(
                        standardOutputClosed.Task,
                        standardErrorClosed.Task)
                    .WaitAsync(TimeSpan.FromMilliseconds(250));
            }
            catch (TimeoutException)
            {
                // A live descendant may retain inherited output handles. The
                // bounded snapshot below is diagnostic evidence, not cleanup.
            }

            string diagnostics = SnapshotCapturedOutput();
            return new InvalidOperationException(
                $"{message} with exit code {Process.ExitCode}." + diagnostics);
        }

        private void OnStandardOutput(
            object sender,
            DataReceivedEventArgs args) =>
            CaptureOutputLine(
                standardOutput,
                ref standardOutputTruncated,
                standardOutputClosed,
                args.Data);

        private void OnStandardError(
            object sender,
            DataReceivedEventArgs args) =>
            CaptureOutputLine(
                standardError,
                ref standardErrorTruncated,
                standardErrorClosed,
                args.Data);

        private void OnProcessExited(object? sender, EventArgs args) =>
            rootExited.TrySetResult(true);

        private void CaptureOutputLine(
            StringBuilder buffer,
            ref bool truncated,
            TaskCompletionSource<bool> closed,
            string? line)
        {
            if (line is null)
            {
                closed.TrySetResult(true);
                return;
            }

            lock (outputSync)
            {
                int remaining =
                    CapturedOutputCharacterLimit - buffer.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    return;
                }

                int lineLength = Math.Min(line.Length, remaining);
                buffer.Append(line.AsSpan(0, lineLength));
                remaining -= lineLength;
                if (lineLength != line.Length)
                {
                    truncated = true;
                    return;
                }

                int newlineLength = Math.Min(
                    Environment.NewLine.Length, remaining);
                buffer.Append(Environment.NewLine.AsSpan(0, newlineLength));
                if (newlineLength != Environment.NewLine.Length)
                    truncated = true;
            }
        }

        private string SnapshotCapturedOutput()
        {
            lock (outputSync)
            {
                return FormatCapturedOutput(
                        "stdout", standardOutput, standardOutputTruncated) +
                    FormatCapturedOutput(
                        "stderr", standardError, standardErrorTruncated);
            }
        }

        private static string FormatCapturedOutput(
            string label,
            StringBuilder buffer,
            bool truncated)
        {
            if (buffer.Length == 0 && !truncated)
                return string.Empty;
            return Environment.NewLine + label + ":" +
                Environment.NewLine + buffer +
                (truncated ? "[output truncated]" + Environment.NewLine : "");
        }

        public static JournalWorkerProcess Start(ProcessStartInfo start)
        {
            SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error);
            }

            Process? process = null;
            try
            {
                var limits = new ExtendedLimitInformation
                {
                    BasicLimitInformation = new BasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        ref limits,
                        checked((uint)Marshal.SizeOf<ExtendedLimitInformation>())))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                process = System.Diagnostics.Process.Start(start) ??
                    throw new InvalidOperationException(
                        "A cross-process journal worker did not start.");
                if (!AssignProcessToJobObject(job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new JournalWorkerProcess(process, job);
            }
            catch (Exception exception)
            {
                ExceptionDispatchInfo primaryFailure =
                    ExceptionDispatchInfo.Capture(exception);
                ExceptionDispatchInfo? cleanupFailure = null;
                try
                {
                    if (process is not null)
                    {
                        try
                        {
                            BestEffortTerminateProcess(process);
                        }
                        catch (Exception cleanupException)
                        {
                            CaptureCleanupFailure(
                                ref cleanupFailure, cleanupException);
                        }
                        finally
                        {
                            try
                            {
                                process.Dispose();
                            }
                            catch (Exception cleanupException)
                            {
                                CaptureCleanupFailure(
                                    ref cleanupFailure, cleanupException);
                            }
                        }
                    }
                }
                finally
                {
                    try
                    {
                        job.Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        CaptureCleanupFailure(
                            ref cleanupFailure, cleanupException);
                    }
                }

                if (cleanupFailure is not null)
                {
                    AttachSecondary(
                        primaryFailure.SourceException,
                        cleanupFailure.SourceException);
                }
                primaryFailure.Throw();
                throw new InvalidOperationException(
                    "Unreachable worker assignment failure path.");
            }
        }

        public async Task TerminateAndWaitAsync()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ExceptionDispatchInfo? cleanupFailure = null;
            try
            {
                if (!terminationRequested)
                {
                    if (!TerminateJobObject(job, 137))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    terminationRequested = true;
                }

                long started = Stopwatch.GetTimestamp();
                while (ReadActiveProcessCount(job) != 0)
                {
                    if (Stopwatch.GetElapsedTime(started) >=
                        TimeSpan.FromSeconds(5))
                    {
                        throw new TimeoutException(
                            "The journal worker job did not become empty.");
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
                await RootExit;
            }
            catch (Exception exception)
            {
                cleanupFailure = ExceptionDispatchInfo.Capture(exception);
            }

            if (cleanupFailure is null)
                return;

            try
            {
                await BestEffortTerminateProcessAsync();
            }
            catch (Exception fallbackException)
            {
                AttachSecondary(
                    cleanupFailure.SourceException, fallbackException);
            }
            cleanupFailure.Throw();
        }

        private static void BestEffortTerminateProcess(Process process)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                throw new TimeoutException(
                    "The journal worker process did not exit.");
            }
        }

        private async Task BestEffortTerminateProcessAsync()
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
            try
            {
                await RootExit.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    await RootExit.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    throw;
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            try
            {
                job.Dispose();
            }
            finally
            {
                try
                {
                    DetachProcessEvents();
                    Process.Dispose();
                }
                finally
                {
                    disposed = true;
                }
            }
        }

        private void DetachProcessEvents()
        {
            Process.OutputDataReceived -= OnStandardOutput;
            Process.ErrorDataReceived -= OnStandardError;
            Process.Exited -= OnProcessExited;
        }

        private static uint ReadActiveProcessCount(SafeFileHandle job)
        {
            if (!QueryInformationJobObject(
                    job,
                    JobObjectBasicAccountingInformation,
                    out BasicAccountingInformation information,
                    checked((uint)Marshal.SizeOf<BasicAccountingInformation>()),
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return information.ActiveProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicAccountingInformation
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateJobObjectW(
            IntPtr jobAttributes,
            string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            ref ExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            SafeFileHandle job,
            IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(
            SafeFileHandle job,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            out BasicAccountingInformation information,
            uint informationLength,
            out uint returnLength);
    }

    private sealed class TrackingJournalFileSystem :
        LocalOperationJournalFileSystem
    {
        public int FlushCount { get; private set; }

        public int OpenReadCount { get; private set; }

        public override void AfterDurableFlush()
        {
            FlushCount++;
        }

        public override void AfterReopen()
        {
            OpenReadCount++;
        }
    }

    private sealed class WriterFailingJournalFileSystem :
        LocalOperationJournalFileSystem
    {
        public override void BeforeCreateEntry(string operationsPath) =>
            throw new IOException("secret-value");
    }

    private sealed class AccessDeniedJournalFileSystem :
        LocalOperationJournalFileSystem
    {
        public override void BeforeCreateEntry(string operationsPath) =>
            throw new UnauthorizedAccessException("ordinary denial");
    }

    private sealed class ParseFailingJournalFileSystem :
        LocalOperationJournalFileSystem
    {
        public override void AfterReopen() =>
            throw new JsonException("Synthetic parse failure.");
    }

    private sealed class RetentionFailingJournalFileSystem :
        LocalOperationJournalFileSystem
    {
        public override void BeforeRetentionDelete(string path) =>
            throw new IOException("retention failed");
    }

    private sealed class SwappingJournalFileSystem(
        string journalRoot,
        string parked,
        string attackLink) : LocalOperationJournalFileSystem
    {
        public bool SwapAttempted { get; private set; }

        public bool SwapCreated { get; private set; }

        public bool SwapRefused { get; private set; }

        public int? SwapRefusalErrorCode { get; private set; }

        public override void BeforeCreateEntry(string operationsPath)
        {
            SwapAttempted = true;
            try
            {
                Directory.Move(journalRoot, parked);
            }
            catch (IOException exception) when (
                (exception.HResult & 0xFFFF) is
                    WindowsErrorAccessDenied or WindowsErrorSharingViolation)
            {
                SwapRefused = true;
                SwapRefusalErrorCode = exception.HResult & 0xFFFF;
                throw;
            }
            Directory.Move(attackLink, journalRoot);
            SwapCreated = true;
        }
    }
}
