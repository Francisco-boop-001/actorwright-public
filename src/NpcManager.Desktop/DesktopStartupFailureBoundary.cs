using NpcManager.Domain;

namespace NpcManager.Desktop;

internal static class DesktopStartupFailureBoundary
{
    public static int RunStartup(
        Func<WorkspacePath> resolveRoot,
        Action<WorkspacePath> start,
        Action<string> presentFailure,
        Func<Exception, DesktopCrashWriteResult?>? writeFailureReport = null)
    {
        ArgumentNullException.ThrowIfNull(resolveRoot);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(presentFailure);
        try
        {
            start(resolveRoot());
            return 0;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            DesktopCrashWriteResult? report =
                TryWriteFailureReport(writeFailureReport, exception);
            presentFailure(FormatFailureMessage(
                "Actorwright could not start",
                exception,
                report,
                writeFailureReport is not null));
            return 1;
        }
    }

    private static DesktopCrashWriteResult? TryWriteFailureReport(
        Func<Exception, DesktopCrashWriteResult?>? writeFailureReport,
        Exception exception)
    {
        if (writeFailureReport is null)
            return null;
        try
        {
            return writeFailureReport(exception);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatFailureMessage(
        string heading,
        Exception exception,
        DesktopCrashWriteResult? report,
        bool reportingRequested)
    {
        string message = $"{heading}: {exception.Message}";
        if (!reportingRequested)
            return message;
        return report is { Written: true, ReportPath: { } path }
            ? message + Environment.NewLine + $"Crash report: {path.Value}"
            : message + Environment.NewLine +
              "A crash report could not be safely written.";
    }

    public static async Task RunInitializationAsync(
        Func<Task> initialize,
        Action<string> presentFailure,
        Func<Exception, DesktopCrashWriteResult?>? writeFailureReport = null)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        ArgumentNullException.ThrowIfNull(presentFailure);
        try
        {
            await initialize();
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            DesktopCrashWriteResult? report =
                TryWriteFailureReport(writeFailureReport, exception);
            presentFailure(FormatFailureMessage(
                "Desktop initialization failed",
                exception,
                report,
                writeFailureReport is not null));
        }
    }
}
