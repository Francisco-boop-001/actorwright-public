using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal sealed record DesktopFailureEvidenceRecord(
    int SchemaVersion,
    string ArtifactKind,
    DateTimeOffset TimestampUtc,
    string TraceId,
    int ProcessId,
    int ThreadId,
    ActorwrightObservabilityEventSource.DesktopFailureOperationId Operation,
    ActorwrightObservabilityEventSource.DesktopFailureStageId Stage,
    ActorwrightObservabilityEventSource.DesktopFailureKindId FailureKind,
    ImmutableArray<string> DiagnosticCodes,
    ActorwrightObservabilityEventSource.DesktopProcessIdentityId? ProcessIdentity,
    string? ExecutableSha256,
    int? ExitCode);

internal sealed class DesktopFailureEvidenceListener : EventListener
{
    private const int ProcessFailureEventId = 7;
    private const int MaximumDiagnosticCodes = 16;
    private const int MaximumDiagnosticCodeLength = 80;
    private const int MaximumDiagnosticCodesLength = 1_024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private DesktopEvidenceFileStore? store;
    private int initialized;

    public DesktopFailureEvidenceListener(WorkspacePath workspaceRoot)
        : this(new DesktopEvidenceFileStore(workspaceRoot))
    {
    }

    internal DesktopFailureEvidenceListener(
        DesktopEvidenceFileStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        Volatile.Write(ref initialized, 1);
        foreach (EventSource source in EventSource.GetSources())
            EnableFailureEvents(source);
    }

    internal static DesktopEvidenceStoreWriteResult? RecordCurrentOperationFailure(
        Activity? activity,
        ActorwrightObservabilityEventSource.DesktopFailureOperationId operation,
        ActorwrightObservabilityEventSource.DesktopFailureKindId failureKind,
        IEnumerable<string> diagnosticCodes)
    {
        if (System.Windows.Application.Current is not App app)
            return null;
        return app.FailureEvidence?.WriteOperationFailure(
            activity,
            operation,
            failureKind,
            diagnosticCodes);
    }

    internal DesktopEvidenceStoreWriteResult WriteOperationFailure(
        Activity? activity,
        ActorwrightObservabilityEventSource.DesktopFailureOperationId operation,
        ActorwrightObservabilityEventSource.DesktopFailureKindId failureKind,
        IEnumerable<string> diagnosticCodes)
    {
        try
        {
            if (!Enum.IsDefined(operation) ||
                failureKind is not
                    (ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult or
                     ActorwrightObservabilityEventSource.DesktopFailureKindId.Cancelled or
                     ActorwrightObservabilityEventSource.DesktopFailureKindId.Exception))
                return Refused();

            return Write(CreateRecord(
                GetTraceId(activity),
                operation,
                ActorwrightObservabilityEventSource.DesktopFailureStageId.Build,
                failureKind,
                SanitizeDiagnosticCodes(diagnosticCodes),
                null,
                null,
                null));
        }
        catch (Exception)
        {
            return Refused();
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (Volatile.Read(ref initialized) != 0)
            EnableFailureEvents(eventSource);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            if (Volatile.Read(ref initialized) == 0 ||
                eventData.EventSource.Name != "Actorwright-Observability" ||
                eventData.EventId != ProcessFailureEventId ||
                eventData.Payload is not { Count: 7 } payload ||
                payload[0] is not string traceId ||
                payload[1] is not int operationValue ||
                payload[2] is not int stageValue ||
                payload[3] is not int processValue ||
                payload[4] is not string executableSha256 ||
                payload[5] is not int exitCode ||
                payload[6] is not string diagnosticCode)
                return;

            var operation = (ActorwrightObservabilityEventSource.DesktopFailureOperationId)operationValue;
            var stage = (ActorwrightObservabilityEventSource.DesktopFailureStageId)stageValue;
            var processIdentity = (ActorwrightObservabilityEventSource.DesktopProcessIdentityId)processValue;
            if (!IsTraceId(traceId) ||
                !Enum.IsDefined(operation) ||
                stage != ActorwrightObservabilityEventSource.DesktopFailureStageId.Process ||
                !Enum.IsDefined(processIdentity) ||
                !IsSha256(executableSha256) ||
                !IsSafeDiagnosticCode(diagnosticCode))
                return;

            DesktopEvidenceStoreWriteResult written = Write(CreateRecord(
                traceId,
                operation,
                stage,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.ProcessExit,
                [diagnosticCode],
                processIdentity,
                executableSha256,
                exitCode));
            if (!written.Written)
                NotifyEvidenceWriteFailure();
        }
        catch (Exception)
        {
            NotifyEvidenceWriteFailure();
        }
    }

    public override void Dispose()
    {
        Volatile.Write(ref initialized, 0);
        store = null;
        base.Dispose();
    }

    private static void NotifyEvidenceWriteFailure()
    {
        try
        {
            (System.Windows.Application.Current as App)?
                .NotifyFailureEvidenceUnavailable();
        }
        catch (Exception)
        {
        }
    }

    private void EnableFailureEvents(EventSource source)
    {
        if (source.Name != "Actorwright-Observability" ||
            Volatile.Read(ref initialized) == 0)
            return;
        try
        {
            EnableEvents(
                source,
                EventLevel.Error,
                ActorwrightObservabilityEventSource.Keywords.DesktopFailureEvidence);
        }
        catch (Exception)
        {
        }
    }

    private static DesktopFailureEvidenceRecord CreateRecord(
        string traceId,
        ActorwrightObservabilityEventSource.DesktopFailureOperationId operation,
        ActorwrightObservabilityEventSource.DesktopFailureStageId stage,
        ActorwrightObservabilityEventSource.DesktopFailureKindId failureKind,
        ImmutableArray<string> diagnosticCodes,
        ActorwrightObservabilityEventSource.DesktopProcessIdentityId? processIdentity,
        string? executableSha256,
        int? exitCode) =>
        new(
            1,
            "npcmanager-desktop-failure-evidence",
            DateTimeOffset.UtcNow,
            traceId,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            operation,
            stage,
            failureKind,
            diagnosticCodes,
            processIdentity,
            executableSha256,
            exitCode);

    private DesktopEvidenceStoreWriteResult Write(
        DesktopFailureEvidenceRecord record)
    {
        DesktopEvidenceFileStore? currentStore = Volatile.Read(ref store);
        if (currentStore is null)
            return Refused();
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                record,
                JsonOptions);
            return currentStore.WriteFailureEvidence(json);
        }
        catch (Exception)
        {
            return Refused();
        }
    }

    private static ImmutableArray<string> SanitizeDiagnosticCodes(
        IEnumerable<string> diagnosticCodes)
    {
        ArgumentNullException.ThrowIfNull(diagnosticCodes);
        var codes = ImmutableArray.CreateBuilder<string>();
        int combinedLength = 0;
        foreach (string code in diagnosticCodes)
        {
            if (codes.Count >= MaximumDiagnosticCodes)
                break;
            if (!IsSafeDiagnosticCode(code) || codes.Contains(code))
                continue;
            int nextLength = combinedLength + code.Length +
                (codes.Count == 0 ? 0 : 1);
            if (nextLength > MaximumDiagnosticCodesLength)
                break;
            codes.Add(code);
            combinedLength = nextLength;
        }
        return codes.ToImmutable();
    }

    private static string GetTraceId(Activity? activity)
    {
        string traceId = activity?.TraceId.ToString() ?? string.Empty;
        return IsTraceId(traceId)
            ? traceId
            : ActivityTraceId.CreateRandom().ToString();
    }

    private static bool IsTraceId(string? value) =>
        value is { Length: 32 } &&
        value.Any(character => character != '0') &&
        value.All(IsHex);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(IsHex);

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or
            >= 'A' and <= 'F' or
            >= 'a' and <= 'f';

    private static bool IsSafeDiagnosticCode(string? value) =>
        value is { Length: > 0 and <= MaximumDiagnosticCodeLength } &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '-' or '_');

    private static DesktopEvidenceStoreWriteResult Refused() =>
        new(
            false,
            null,
            "The desktop failure evidence could not be safely written.");
}
