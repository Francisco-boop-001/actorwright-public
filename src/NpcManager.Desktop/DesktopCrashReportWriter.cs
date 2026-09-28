using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal enum DesktopCrashOrigin
{
    DispatcherUnhandled,
    AppDomainUnhandled,
    UnobservedTask,
    OperationFailure,
    StartupFailure
}

internal sealed record DesktopCrashWriteResult(
    bool Written,
    WorkspacePath? ReportPath,
    string? Error);

internal sealed class DesktopCrashReportWriter
{
    private const int MaximumReportBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath reportRoot;
    private readonly DesktopEvidenceFileStore evidenceStore;

    internal static WorkspacePath DefaultReportRoot { get; } =
        ActorwrightWorkspace.WorkRoot(
            ActorwrightWorkspace.ResolveRoot(),
            "desktop-crash-reports");

    public DesktopCrashReportWriter(
        WorkspacePath labRoot,
        WorkspacePath reportRoot)
    {
        this.labRoot = ActorwrightWorkspace.RequireAdmittedRoot(labRoot);
        this.reportRoot = reportRoot;
        evidenceStore = new DesktopEvidenceFileStore(this.labRoot);
    }

    public DesktopCrashWriteResult Write(
        DesktopCrashOrigin origin,
        Exception exception,
        bool isTerminating)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (!reportRoot.IsUnder(labRoot) || reportRoot == labRoot)
                return Refused(
                    "Desktop crash reports must remain below the admitted K-local lab root.");

            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            var report = new DesktopCrashReport(
                1,
                "npcmanager-desktop-crash-report",
                timestamp,
                ToWireName(origin),
                isTerminating,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ??
                "unknown",
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                new DesktopCrashException(
                    Limit(exception.GetType().FullName ??
                        exception.GetType().Name, 256) ?? string.Empty,
                    Limit(exception.Message, 2048) ?? string.Empty,
                    Limit(exception.StackTrace, 2048),
                    Limit(exception.ToString(), 4096) ?? string.Empty));
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                report,
                JsonOptions);
            if (json.Length >= MaximumReportBytes)
                return Refused(
                    "The desktop crash report exceeded its admitted byte bound.");

            var record = new byte[json.Length + 1];
            json.CopyTo(record, 0);
            record[^1] = (byte)'\n';
            DesktopEvidenceStoreWriteResult stored =
                evidenceStore.WriteCrashReport(reportRoot, record);
            return new DesktopCrashWriteResult(
                stored.Written,
                stored.Path,
                stored.Error);
        }
        catch (Exception)
        {
            Trace.TraceError(
                "NPC Manager could not safely write a desktop crash report.");
            return new DesktopCrashWriteResult(
                false,
                null,
                "The desktop crash report could not be safely written.");
        }
    }

    public WorkspacePath? WriteOperationFailure(Exception exception) =>
        Write(
            DesktopCrashOrigin.OperationFailure,
            exception,
            isTerminating: false).ReportPath;

    private static string? Limit(string? value, int maximumCharacters)
    {
        if (value is null || value.Length == 0)
            return value;
        if (value.Length <= maximumCharacters)
            return value;
        return value[..maximumCharacters];
    }

    private static string ToWireName(DesktopCrashOrigin origin) =>
        origin switch
        {
            DesktopCrashOrigin.DispatcherUnhandled =>
                "dispatcher-unhandled",
            DesktopCrashOrigin.AppDomainUnhandled =>
                "app-domain-unhandled",
            DesktopCrashOrigin.UnobservedTask =>
                "unobserved-task",
            DesktopCrashOrigin.OperationFailure =>
                "operation-failure",
            DesktopCrashOrigin.StartupFailure =>
                "startup",
            _ => throw new ArgumentOutOfRangeException(
                nameof(origin),
                origin,
                "Unknown desktop crash origin.")
        };

    private static DesktopCrashWriteResult Refused(string error)
    {
        Trace.TraceError("NPC Manager desktop crash report refused: {0}", error);
        return new DesktopCrashWriteResult(false, null, error);
    }

    private sealed record DesktopCrashReport(
        int SchemaVersion,
        string ArtifactKind,
        DateTimeOffset TimestampUtc,
        string Origin,
        bool IsTerminating,
        int ProcessId,
        int ThreadId,
        string ApplicationVersion,
        string FrameworkDescription,
        string OsDescription,
        DesktopCrashException Exception);

    private sealed record DesktopCrashException(
        string Type,
        string Message,
        string? StackTrace,
        string Complete);
}
