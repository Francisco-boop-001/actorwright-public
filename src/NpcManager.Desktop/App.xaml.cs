using System.Windows;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

#pragma warning disable CA1001 // WPF Application releases this listener from OnExit.
public partial class App : System.Windows.Application
{
    private readonly Func<WorkspacePath> resolveWorkspaceRoot;
    private DesktopCrashReportWriter? _crashReports;
    private DesktopFailureEvidenceListener? _failureEvidence;

    internal DesktopFailureEvidenceListener? FailureEvidence =>
        _failureEvidence;

    public App()
        : this(ActorwrightWorkspace.ResolveRoot)
    {
    }

    internal App(Func<WorkspacePath> resolveWorkspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(resolveWorkspaceRoot);
        this.resolveWorkspaceRoot = resolveWorkspaceRoot;

        DispatcherUnhandledException += (_, args) =>
            HandleDispatcherUnhandledException(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            HandleAppDomainUnhandledException(args);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            HandleUnobservedTaskException(args);
    }

    internal void HandleAppDomainUnhandledException(
        UnhandledExceptionEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        _crashReports?.Write(
            DesktopCrashOrigin.AppDomainUnhandled,
            args.ExceptionObject as Exception ??
            new InvalidOperationException(
                $"Unhandled non-Exception object: {args.ExceptionObject}"),
            args.IsTerminating);
    }

    internal void HandleDispatcherUnhandledException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _crashReports?.Write(
            DesktopCrashOrigin.DispatcherUnhandled,
            exception,
            isTerminating: true);
    }

    internal void HandleUnobservedTaskException(
        UnobservedTaskExceptionEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        _crashReports?.Write(
            DesktopCrashOrigin.UnobservedTask,
            args.Exception,
            isTerminating: false);
        args.SetObserved();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--resource-extraction-probe"])
        {
            int exitCode = ApplicationResourceExtractionProbe.Run(
                Console.Out,
                Console.Error);
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);
        int startupExitCode = RunDesktopStartup(
            workspaceRoot => new MainWindow(workspaceRoot, e.Args).Show(),
            message => MessageBox.Show(
                message,
                "Actorwright startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error));
        if (startupExitCode != 0)
            Shutdown(startupExitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _failureEvidence?.Dispose();
        _failureEvidence = null;
        base.OnExit(e);
    }

    internal DesktopCrashWriteResult? WriteStartupFailure(
        Exception exception) =>
        _crashReports?.Write(
            DesktopCrashOrigin.StartupFailure,
            exception,
            isTerminating: false);

    internal void NotifyFailureEvidenceUnavailable()
    {
        try
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (base.MainWindow is MainWindow window)
                        window.ReportFailureEvidenceUnavailable();
                }
                catch (Exception)
                {
                }
            }));
        }
        catch (Exception)
        {
        }
    }

    internal int RunDesktopStartup(
        Action<WorkspacePath> start,
        Action<string> presentFailure) =>
        DesktopStartupFailureBoundary.RunStartup(
            () => ActorwrightWorkspace.RequireAdmittedRoot(
                resolveWorkspaceRoot()),
            workspaceRoot =>
            {
                _crashReports = new DesktopCrashReportWriter(
                    workspaceRoot,
                    ActorwrightWorkspace.WorkRoot(
                        workspaceRoot,
                        "desktop-crash-reports"));
                _failureEvidence?.Dispose();
                _failureEvidence = new DesktopFailureEvidenceListener(
                    new DesktopEvidenceFileStore(workspaceRoot));
                start(workspaceRoot);
            },
            presentFailure,
            exception => _crashReports?.Write(
                DesktopCrashOrigin.StartupFailure,
                exception,
                isTerminating: false));
}
#pragma warning restore CA1001
