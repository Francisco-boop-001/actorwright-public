using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static class DesktopCrashReporterTests
{
    private static readonly WorkspacePath LabRoot =
        CreateLabRoot();

    private static WorkspacePath CreateLabRoot()
    {
        WorkspacePath workspaceRoot = ActorwrightWorkspace.RequireAdmittedRoot(
            new WorkspacePath(FindRepositoryRoot()));
        var labRoot = new WorkspacePath(Path.Combine(
            workspaceRoot.Value,
            "artifacts",
            "reassessment-work",
            "desktop-crash-reporter-tests-" + Guid.NewGuid().ToString("N")));
        if (!labRoot.IsUnder(workspaceRoot))
            throw new InvalidOperationException(
                "Desktop crash report fixtures must remain under the assigned K-local worktree.");
        return labRoot;
    }

    private static string FindRepositoryRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "Actorwright.sln")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        throw new DirectoryNotFoundException(
            "Actorwright repository root was not found.");
    }

    public static void Run()
    {
        Directory.CreateDirectory(LabRoot.Value);
        string rootValue = Path.Combine(
            LabRoot.Value,
            "desktop-crash-reporter-test-" + Guid.NewGuid().ToString("N"));
        var reportRoot = new WorkspacePath(rootValue);
        try
        {
            var writer = new DesktopCrashReportWriter(LabRoot, reportRoot);
            var failure = new InvalidOperationException(
                "outer crash fixture",
                new IOException("inner crash fixture"));

            DesktopCrashWriteResult first = writer.Write(
                DesktopCrashOrigin.DispatcherUnhandled,
                failure,
                isTerminating: false);
            DesktopCrashWriteResult second = writer.Write(
                DesktopCrashOrigin.AppDomainUnhandled,
                failure,
                isTerminating: true);

            Require(first.Written && first.ReportPath is not null &&
                    second.Written && second.ReportPath is not null,
                "desktop-crash-report-write");
            WorkspacePath firstPath = first.ReportPath ??
                throw new InvalidOperationException(
                    "The first crash report path was not returned.");
            WorkspacePath secondPath = second.ReportPath ??
                throw new InvalidOperationException(
                    "The second crash report path was not returned.");
            Require(firstPath != secondPath &&
                    File.Exists(firstPath.Value) &&
                    File.Exists(secondPath.Value),
                "desktop-crash-report-unique-create-new");

            using JsonDocument report = JsonDocument.Parse(
                File.ReadAllBytes(firstPath.Value));
            JsonElement root = report.RootElement;
            Require(
                root.GetProperty("schemaVersion").GetInt32() == 1 &&
                root.GetProperty("artifactKind").GetString() ==
                "npcmanager-desktop-crash-report" &&
                root.GetProperty("origin").GetString() ==
                "dispatcher-unhandled" &&
                !root.GetProperty("isTerminating").GetBoolean() &&
                root.GetProperty("processId").GetInt32() > 0 &&
                root.GetProperty("threadId").GetInt32() > 0 &&
                !string.IsNullOrWhiteSpace(
                    root.GetProperty("applicationVersion").GetString()) &&
                !string.IsNullOrWhiteSpace(
                    root.GetProperty("frameworkDescription").GetString()) &&
                root.GetProperty("exception").GetProperty("type").GetString() ==
                typeof(InvalidOperationException).FullName &&
                root.GetProperty("exception").GetProperty("message").GetString() ==
                "outer crash fixture" &&
                root.GetProperty("exception").GetProperty("complete").GetString()!
                    .Contains("inner crash fixture", StringComparison.Ordinal),
                "desktop-crash-report-json-contract");

            var refused = new DesktopCrashReportWriter(
                LabRoot,
                new WorkspacePath(
                    @"C:\outside-npc-manager-desktop-crash-reports"));
            DesktopCrashWriteResult refusedResult = refused.Write(
                DesktopCrashOrigin.UnobservedTask,
                failure,
                isTerminating: false);
            Require(!refusedResult.Written &&
                    refusedResult.ReportPath is null &&
                    !string.IsNullOrWhiteSpace(refusedResult.Error),
                "desktop-crash-report-failure-never-throws");

            string? priorProtectedRoot = Environment.GetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable);
            string protectedRootValue = Path.Combine(
                LabRoot.Value,
                "protected-report-root-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable(
                    ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                    protectedRootValue);
                var protectedWriter = new DesktopCrashReportWriter(
                    LabRoot,
                    new WorkspacePath(protectedRootValue));
                DesktopCrashWriteResult protectedResult = protectedWriter.Write(
                    DesktopCrashOrigin.UnobservedTask,
                    failure,
                    isTerminating: false);
                Require(!protectedResult.Written &&
                        protectedResult.ReportPath is null &&
                        !Directory.Exists(protectedRootValue),
                    "desktop-crash-report-refuses-configured-protected-root");
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                    priorProtectedRoot);
                if (Directory.Exists(protectedRootValue))
                    Directory.Delete(protectedRootValue, recursive: true);
            }

            string blockedRootValue = Path.Combine(
                rootValue,
                "blocked-report-root");
            File.WriteAllText(
                blockedRootValue,
                "This file intentionally occupies the report-root path.");
            var blocked = new DesktopCrashReportWriter(
                LabRoot,
                new WorkspacePath(blockedRootValue));
            DesktopCrashWriteResult blockedResult = blocked.Write(
                DesktopCrashOrigin.UnobservedTask,
                failure,
                isTerminating: false);
            Require(!blockedResult.Written &&
                    blockedResult.ReportPath is null &&
                    blockedResult.Error?.Contains(
                        "occupied by a file",
                        StringComparison.Ordinal) == true,
                "desktop-crash-report-unwritable-destination-never-throws");

            string boundedRootValue = Path.Combine(
                rootValue,
                "bounded-report-root");
            var boundedWriter = new DesktopCrashReportWriter(
                LabRoot,
                new WorkspacePath(boundedRootValue));
            DesktopCrashWriteResult? lastBoundedResult = null;
            for (int index = 0; index < 36; index++)
            {
                lastBoundedResult = boundedWriter.Write(
                    DesktopCrashOrigin.DispatcherUnhandled,
                    new InvalidOperationException(
                        new string('x', 100_000)),
                    isTerminating: false);
                Require(lastBoundedResult.Written,
                    "desktop-crash-report-retention-write");
            }
            string[] retained = Directory.GetFiles(
                boundedRootValue,
                "crash-*.json");
            Require(retained.Length <= 32 &&
                    retained.Sum(path => new FileInfo(path).Length) <=
                        2 * 1024 * 1024 &&
                    retained.All(path => new FileInfo(path).Length <=
                        64 * 1024),
                "desktop-crash-report-retention-and-size-bounds");
            Require(lastBoundedResult?.ReportPath is { } lastPath &&
                    File.Exists(lastPath.Value),
                "desktop-crash-report-retention-keeps-newest");

            var concurrentPaths = new ConcurrentBag<string>();
            var concurrentWriter1 = new DesktopCrashReportWriter(
                LabRoot,
                new WorkspacePath(boundedRootValue));
            var concurrentWriter2 = new DesktopCrashReportWriter(
                new WorkspacePath(
                    Path.TrimEndingDirectorySeparator(LabRoot.Value) +
                    Path.DirectorySeparatorChar),
                new WorkspacePath(
                    Path.TrimEndingDirectorySeparator(boundedRootValue) +
                    Path.DirectorySeparatorChar));
            Parallel.For(0, 8, index =>
            {
                DesktopCrashReportWriter concurrentWriter = index % 2 == 0
                    ? concurrentWriter1
                    : concurrentWriter2;
                DesktopCrashWriteResult concurrentResult =
                    concurrentWriter.Write(
                        DesktopCrashOrigin.DispatcherUnhandled,
                        new InvalidOperationException(
                            $"concurrent crash fixture {index}"),
                        isTerminating: false);
                Require(concurrentResult.Written &&
                        concurrentResult.ReportPath is not null,
                    "desktop-crash-report-concurrent-write");
                concurrentPaths.Add(concurrentResult.ReportPath!.Value.Value);
            });
            retained = Directory.GetFiles(
                boundedRootValue,
                "crash-*.json");
            string newestConcurrentPath = concurrentPaths
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Last();
            Require(retained.Length == 32 &&
                    retained.Contains(
                        newestConcurrentPath,
                        StringComparer.OrdinalIgnoreCase) &&
                    retained.Sum(path => new FileInfo(path).Length) <=
                        2 * 1024 * 1024,
                "desktop-crash-report-retention-is-shared-across-store-instances");
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }

        Console.WriteLine(
            "PASS desktop crash reports are unique, complete, K-local, and fail-safe.");
        VerifyOperationFailureReport();
    }

    public static void VerifyOperationFailureReport()
    {
        Directory.CreateDirectory(LabRoot.Value);
        string rootValue = Path.Combine(
            LabRoot.Value,
            "desktop-operation-failure-test-" + Guid.NewGuid().ToString("N"));
        var reportRoot = new WorkspacePath(rootValue);
        try
        {
            var writer = new DesktopCrashReportWriter(LabRoot, reportRoot);
            Exception failure = CaptureOperationFailure();

            DesktopCrashWriteResult writeResult = writer.Write(
                DesktopCrashOrigin.OperationFailure,
                failure,
                isTerminating: false);
            WorkspacePath? reportPath = writeResult.ReportPath;

            Require(reportPath is not null,
                $"desktop-operation-failure-report-path: {writeResult.Error ?? "<no error supplied>"}");
            WorkspacePath exactReportPath = reportPath ??
                throw new InvalidOperationException(
                    "The operation failure report path was not returned.");
            Require(File.Exists(exactReportPath.Value),
                "desktop-operation-failure-report-exists");
            using JsonDocument report = JsonDocument.Parse(
                File.ReadAllBytes(exactReportPath.Value));
            JsonElement root = report.RootElement;
            JsonElement exception = root.GetProperty("exception");
            Require(
                root.GetProperty("origin").GetString() ==
                    "operation-failure" &&
                !root.GetProperty("isTerminating").GetBoolean() &&
                exception.GetProperty("stackTrace").GetString()!
                    .Contains(
                        nameof(CaptureOperationFailure),
                        StringComparison.Ordinal) &&
                exception.GetProperty("complete").GetString()!
                    .Contains(
                        "inner operation failure fixture",
                        StringComparison.Ordinal) &&
                exception.GetProperty("complete").GetString()!
                    .Contains(
                        nameof(ThrowOperationFailureInner),
                        StringComparison.Ordinal),
                "desktop-operation-failure-original-stack-and-inner");
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static Exception CaptureOperationFailure()
    {
        try
        {
            try
            {
                ThrowOperationFailureInner();
                throw new InvalidOperationException(
                    "The inner failure fixture unexpectedly returned.");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException(
                    "outer operation failure fixture",
                    inner);
            }
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void ThrowOperationFailureInner() =>
        throw new IOException("inner operation failure fixture");

    public static void VerifyDispatcherWiring(
        App app,
        WorkspacePath labRoot)
    {
        ArgumentNullException.ThrowIfNull(app);
        WorkspacePath reportRoot = ActorwrightWorkspace.WorkRoot(
            labRoot,
            "desktop-crash-reports");
        HashSet<string> before = Directory.Exists(reportRoot.Value)
            ? Directory.GetFiles(reportRoot.Value).ToHashSet(
                StringComparer.OrdinalIgnoreCase)
            : [];
        var fixtureException = new InvalidOperationException(
            "dispatcher crash wiring fixture");
        app.HandleDispatcherUnhandledException(fixtureException);

        string[] created = Directory.GetFiles(reportRoot.Value)
            .Where(path => !before.Contains(path))
            .ToArray();
        string wiringEvidence =
            "desktop-crash-report-dispatcher-wiring " +
            $"created={created.Length}; reportRoot={reportRoot.Value}";
        Require(created.Length == 1,
            wiringEvidence);
        try
        {
            using JsonDocument report = JsonDocument.Parse(
                File.ReadAllBytes(created[0]));
            Require(
                report.RootElement.GetProperty("origin").GetString() ==
                "dispatcher-unhandled" &&
                report.RootElement.GetProperty("isTerminating").GetBoolean() &&
                report.RootElement.GetProperty("exception")
                    .GetProperty("message").GetString() ==
                "dispatcher crash wiring fixture",
                "desktop-crash-report-dispatcher-origin");
        }
        finally
        {
            File.Delete(created[0]);
        }
    }

    public static void VerifyAppDomainWiring(
        App app,
        WorkspacePath labRoot)
    {
        ArgumentNullException.ThrowIfNull(app);
        WorkspacePath reportRoot = ActorwrightWorkspace.WorkRoot(
            labRoot,
            "desktop-crash-reports");
        HashSet<string> before = SnapshotReports(reportRoot);
        app.HandleAppDomainUnhandledException(
            new UnhandledExceptionEventArgs(
                new InvalidOperationException(
                    "app-domain nonterminating fixture"),
                isTerminating: false));
        app.HandleAppDomainUnhandledException(
            new UnhandledExceptionEventArgs(
                new InvalidOperationException(
                    "app-domain terminating fixture"),
                isTerminating: true));

        string[] created = CreatedReports(reportRoot, before);
        try
        {
            var terminatingByMessage = new Dictionary<string, bool>(
                StringComparer.Ordinal);
            foreach (string path in created)
            {
                using JsonDocument report = JsonDocument.Parse(
                    File.ReadAllBytes(path));
                JsonElement root = report.RootElement;
                Require(root.GetProperty("origin").GetString() ==
                        "app-domain-unhandled",
                    "desktop-crash-report-app-domain-origin");
                terminatingByMessage.Add(
                    root.GetProperty("exception")
                        .GetProperty("message").GetString() ?? string.Empty,
                    root.GetProperty("isTerminating").GetBoolean());
            }

            Require(created.Length == 2 &&
                    terminatingByMessage.TryGetValue(
                        "app-domain nonterminating fixture",
                        out bool nonterminating) &&
                    !nonterminating &&
                    terminatingByMessage.TryGetValue(
                        "app-domain terminating fixture",
                        out bool terminating) &&
                    terminating,
                "desktop-crash-report-app-domain-termination-forwarding");
        }
        finally
        {
            foreach (string path in created)
                File.Delete(path);
        }
    }

    public static void VerifyUnobservedTaskWiring(
        App app,
        WorkspacePath labRoot)
    {
        ArgumentNullException.ThrowIfNull(app);
        WorkspacePath reportRoot = ActorwrightWorkspace.WorkRoot(
            labRoot,
            "desktop-crash-reports");
        HashSet<string> before = SnapshotReports(reportRoot);
        var args = new UnobservedTaskExceptionEventArgs(
            new AggregateException(
                new InvalidOperationException(
                    "unobserved task wiring fixture")));
        Require(!args.Observed,
            "unobserved-task-fixture-starts-unobserved");

        app.HandleUnobservedTaskException(args);

        string[] created = CreatedReports(reportRoot, before);
        try
        {
            Require(args.Observed && created.Length == 1,
                "unobserved-task-reported-and-observed");
            using JsonDocument report = JsonDocument.Parse(
                File.ReadAllBytes(created[0]));
            JsonElement root = report.RootElement;
            Require(root.GetProperty("origin").GetString() ==
                    "unobserved-task" &&
                    !root.GetProperty("isTerminating").GetBoolean() &&
                    root.GetProperty("exception").GetProperty("complete")
                        .GetString()!.Contains(
                            "unobserved task wiring fixture",
                            StringComparison.Ordinal),
                "desktop-crash-report-unobserved-task-origin");
        }
        finally
        {
            foreach (string path in created)
                File.Delete(path);
        }
    }

    private static HashSet<string> SnapshotReports(WorkspacePath reportRoot) =>
        Directory.Exists(reportRoot.Value)
            ? Directory.GetFiles(reportRoot.Value).ToHashSet(
                StringComparer.OrdinalIgnoreCase)
            : [];

    private static string[] CreatedReports(
        WorkspacePath reportRoot,
        HashSet<string> before) =>
        Directory.Exists(reportRoot.Value)
            ? Directory.GetFiles(reportRoot.Value)
                .Where(path => !before.Contains(path))
                .ToArray()
            : [];

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"Desktop crash reporter assertion failed: {name}.");
    }
}
