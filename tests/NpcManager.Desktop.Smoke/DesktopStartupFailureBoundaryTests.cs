using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop.Smoke;

internal static class DesktopStartupFailureBoundaryTests
{
    public static void Run()
    {
        string root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "test-work",
            "desktop-startup-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var labRoot = new WorkspacePath(root);
        WorkspacePath resolvedRoot = labRoot;
        App? app = null;
        MainWindow? window = null;
        try
        {
            Exception? resolutionFailure = new InvalidDataException(
                "invalid workspace fixture");
            app = new App(() => resolutionFailure is null
                ? resolvedRoot
                : throw resolutionFailure)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.InitializeComponent();
            Require(app.StartupUri is null,
                "bounded-startup-is-the-only-window-creation-path");
            TestStartupResolutionFailure(app);
            resolutionFailure = null;
            TestAdmittedRootStartup(app, labRoot, value => resolvedRoot = value);
            TestRejectedRootStartup(app, labRoot, value => resolvedRoot = value);
            TestProtectedRootConfigurationResolution();
            TestMainWindowDefaultResolverRejectsNonKRoot();
            TestInitializationFailureAsync().GetAwaiter().GetResult();
            TestInitializationFatalExceptionPropagationAsync()
                .GetAwaiter()
                .GetResult();
#pragma warning disable CA2201
            resolutionFailure = new OutOfMemoryException(
                "fatal fixture");
#pragma warning restore CA2201
            TestFatalExceptionPropagation(app);
            resolutionFailure = null;
            TestWindowConstructionFailure(app, labRoot);
            TestSuccessfulStartup(app, labRoot);
            DesktopFailureEvidenceTests.Run(app, labRoot);
            DesktopCrashReporterTests.Run();
            DesktopCrashReporterTests.VerifyDispatcherWiring(app, labRoot);
            DesktopCrashReporterTests.VerifyAppDomainWiring(app, labRoot);
            DesktopCrashReporterTests.VerifyUnobservedTaskWiring(app, labRoot);

            window = new MainWindow(labRoot, ["--race-menu-request"]);
            window.InitializeWorkspaceCoreAsync().GetAwaiter().GetResult();
            var shell = (WorkspaceShellViewModel)window.DataContext;
            Require(shell.RaceMenuNpc.Verdict == "Startup request invalid" &&
                shell.RaceMenuNpc.Diagnostics.Any(item =>
                    item.StartsWith("startup:", StringComparison.Ordinal)),
                "invalid-startup-request-visible-state");
        }
        finally
        {
            window?.Close();
            app?.Shutdown();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void TestStartupResolutionFailure(App app)
    {
        string? visibleMessage = null;
        var started = false;
        int exitCode = app.RunDesktopStartup(
            _ => started = true,
            message => visibleMessage = message);
        Require(exitCode == 1 && !started &&
            visibleMessage?.Contains(
                "invalid workspace fixture",
                StringComparison.Ordinal) == true,
            "startup-resolution-visible-failure");
    }

    private static void TestAdmittedRootStartup(
        App app,
        WorkspacePath expectedRoot,
        Action<WorkspacePath> setRoot)
    {
        WorkspacePath lowerCaseRoot = new(
            @"k:\Actorwright-startup-admission-test\" +
            Guid.NewGuid().ToString("N"));
        WorkspacePath? observedRoot = null;
        setRoot(lowerCaseRoot);
        try
        {
            int exitCode = app.RunDesktopStartup(
                workspaceRoot => observedRoot = workspaceRoot,
                message => throw new InvalidOperationException(message));
            Require(exitCode == 0 && observedRoot == lowerCaseRoot,
                "lowercase-K-root-startup-remains-admitted");
        }
        finally
        {
            setRoot(expectedRoot);
        }
    }

    private static void TestRejectedRootStartup(
        App app,
        WorkspacePath expectedRoot,
        Action<WorkspacePath> setRoot)
    {
        WorkspacePath cRoot = new(Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory)!,
            "actorwright-startup-root-refusal-" + Guid.NewGuid().ToString("N")));
        WorkspacePath uncRoot = new(
            $@"\\actorwright.invalid\workspace\{Guid.NewGuid():N}");
        Require(!Directory.Exists(cRoot.Value),
            "C-root-startup-fixture-does-not-exist");
        try
        {
            foreach (WorkspacePath rejectedRoot in new[] { cRoot, uncRoot })
            {
                setRoot(rejectedRoot);
                string? visibleMessage = null;
                var started = false;
                int exitCode = app.RunDesktopStartup(
                    _ => started = true,
                    message => visibleMessage = message);
                Require(exitCode == 1 && !started &&
                        visibleMessage?.Contains(
                            "workspace-root-not-k-local",
                            StringComparison.Ordinal) == true,
                    "non-K-root-startup-is-refused-before-window-start");

                if (rejectedRoot == cRoot)
                {
                    WorkspacePath reports = ActorwrightWorkspace.WorkRoot(
                        cRoot,
                        "desktop-crash-reports");
                    Require(!Directory.Exists(cRoot.Value) &&
                            !Directory.Exists(reports.Value),
                        "C-root-refusal-creates-no-directory");
                }
            }
        }
        finally
        {
            setRoot(expectedRoot);
        }
    }

    private static void TestMainWindowDefaultResolverRejectsNonKRoot()
    {
        WorkspacePath cRoot = new(Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory)!,
            "actorwright-main-window-root-refusal-" + Guid.NewGuid().ToString("N")));
        string? priorRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        MainWindow? unexpectedlyConstructed = null;
        Exception? refusal = null;
        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                cRoot.Value);
            try
            {
                unexpectedlyConstructed = new MainWindow();
            }
            catch (Exception exception)
            {
                refusal = exception;
            }
        }
        finally
        {
            unexpectedlyConstructed?.Close();
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                priorRoot);
        }

        Require(refusal?.Message.Contains(
                    "workspace-root-not-k-local",
                    StringComparison.Ordinal) == true,
            "default-MainWindow-resolver-enforces-root-admission");
        Require(!Directory.Exists(cRoot.Value),
            "default-MainWindow-root-refusal-creates-no-directory");
    }

    private static void TestProtectedRootConfigurationResolution()
    {
        string? prior = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.ProtectedRootEnvironmentVariable);
        WorkspacePath labRoot = new(Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "reassessment-work",
            "protected-config-" + Guid.NewGuid().ToString("N")));
        WorkspacePath syntheticProtectedRoot = new(
            Path.Combine(labRoot.Value, ".protected-test"));
        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                null);
            Require(ActorwrightWorkspace.ResolveProtectedRoot(labRoot).Value ==
                    @"F:\ExampleGame",
                "the-private-default-protected-root-remains-configured");

            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                @".protected-test");
            Require(ActorwrightWorkspace.ResolveProtectedRoot(labRoot) ==
                    syntheticProtectedRoot,
                "relative-protected-root-resolves-under-workspace");

            Require(!Directory.Exists(syntheticProtectedRoot.Value),
                "synthetic-protected-root-fixture-starts-absent");
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                syntheticProtectedRoot.Value);
            WorkspacePath absolute = ActorwrightWorkspace.ResolveProtectedRoot(
                labRoot);
            var diagnostics = new KOnlyWorkspacePolicy(labRoot, absolute).Evaluate(
                labRoot,
                new WorkspacePath(Path.Combine(absolute.Value, "Data")));
            Require(absolute == syntheticProtectedRoot &&
                    diagnostics.Any(item =>
                        item.Code == ProtocolV2DiagnosticCodes.ProtectedRootRefused &&
                        item.Severity == DiagnosticSeverity.Error) &&
                    !Directory.Exists(syntheticProtectedRoot.Value),
                "absolute-synthetic-K-protected-root-is-refused-without-creation");

            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                @"K:drive-relative");
            bool malformedRefused = false;
            try
            {
                ActorwrightWorkspace.ResolveProtectedRoot(labRoot);
            }
            catch (ArgumentException)
            {
                malformedRefused = true;
            }
            Require(malformedRefused,
                "malformed-protected-root-setting-does-not-fall-back");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                prior);
        }
    }

    private static async Task TestInitializationFailureAsync()
    {
        string? visibleMessage = null;
        await DesktopStartupFailureBoundary.RunInitializationAsync(
            () => throw new InvalidDataException(
                "invalid settings fixture"),
            message => visibleMessage = message);
        Require(visibleMessage?.Contains(
                "invalid settings fixture",
                StringComparison.Ordinal) == true,
            "startup-initialization-visible-failure");
    }

    private static async Task TestInitializationFatalExceptionPropagationAsync()
    {
#pragma warning disable CA2201
        await RequireThrowsAsync<OutOfMemoryException>(() =>
            DesktopStartupFailureBoundary.RunInitializationAsync(
                () => throw new OutOfMemoryException(
                    "fatal initialization fixture"),
                _ => throw new InvalidOperationException(
                    "Fatal initialization failure was presented.")));
#pragma warning restore CA2201
    }

    private static void TestWindowConstructionFailure(
        App app,
        WorkspacePath labRoot)
    {
        var windowFactoryInvocations = 0;
        string? visibleMessage = null;
        WorkspacePath reportRoot = ActorwrightWorkspace.WorkRoot(
            labRoot,
            "desktop-crash-reports");
        HashSet<string> before = Directory.Exists(reportRoot.Value)
            ? Directory.GetFiles(reportRoot.Value).ToHashSet(
                StringComparer.OrdinalIgnoreCase)
            : [];
        int exitCode = app.RunDesktopStartup(
            _ =>
            {
                windowFactoryInvocations++;
                throw new InvalidOperationException(
                    "window construction fixture");
            },
            message => visibleMessage = message);
        string[] created = Directory.Exists(reportRoot.Value)
            ? Directory.GetFiles(reportRoot.Value)
                .Where(path => !before.Contains(path))
                .ToArray()
            : [];
        Require(exitCode == 1 &&
                windowFactoryInvocations == 1 &&
                visibleMessage?.Contains(
                    "window construction fixture",
                    StringComparison.Ordinal) == true &&
                created.Length == 1,
            $"window-construction-visible-failure exit={exitCode}; " +
            $"factoryCalls={windowFactoryInvocations}; " +
            $"message={visibleMessage ?? "<null>"}; " +
            $"created={created.Length}");
        try
        {
            using JsonDocument report = JsonDocument.Parse(
                File.ReadAllBytes(created[0]));
            Require(report.RootElement.GetProperty("origin").GetString() ==
                    "startup" &&
                    visibleMessage?.Contains(
                        $"Crash report: {created[0]}",
                        StringComparison.Ordinal) == true,
                "startup-failure-persists-and-presents-report-path");
        }
        finally
        {
            foreach (string path in created)
                File.Delete(path);
        }
    }

    private static void TestSuccessfulStartup(
        App app,
        WorkspacePath expectedRoot)
    {
        var windowFactoryInvocations = 0;
        WorkspacePath? observedRoot = null;
        int exitCode = app.RunDesktopStartup(
            root =>
            {
                windowFactoryInvocations++;
                observedRoot = root;
            },
            message => throw new InvalidOperationException(message));
        Require(exitCode == 0 &&
                windowFactoryInvocations == 1 &&
                observedRoot == expectedRoot,
            "successful-startup-exactly-one-window-factory-invocation");
    }

    private static void TestFatalExceptionPropagation(App app)
    {
        RequireThrows<OutOfMemoryException>(() =>
            app.RunDesktopStartup(
                _ => { },
                _ => { }));
    }

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action)
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
            $"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"Desktop startup boundary assertion failed: {name}.");
    }
}
