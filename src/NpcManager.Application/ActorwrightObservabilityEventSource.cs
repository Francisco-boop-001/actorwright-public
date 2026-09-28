using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace NpcManager.Application;

[EventSource(Name = "Actorwright-Observability")]
public sealed class ActorwrightObservabilityEventSource : EventSource
{
    public static readonly ActorwrightObservabilityEventSource Log = new();

    public const int UnknownWorkflowPhase = -1;

    public static class Keywords
    {
        public const EventKeywords Dispatch = (EventKeywords)1;
        public const EventKeywords Policy = (EventKeywords)2;
        public const EventKeywords Workflow = (EventKeywords)4;
        public const EventKeywords PolicyShadow = (EventKeywords)8;
        public const EventKeywords DesktopFailureEvidence = (EventKeywords)16;
    }

    private ActorwrightObservabilityEventSource()
    {
    }

    public enum DispatchRouteId
    {
        Legacy = 1,
        Protocol2 = 2,
        Unsupported = 3
    }

    public enum DispatchFaultKindId
    {
        Cancelled = 1,
        Unexpected = 2
    }

    public enum PolicyKindId
    {
        Output = 1,
        Read = 2
    }

    public enum PolicyDecisionId
    {
        Allowed = 1,
        Refused = 2
    }

    public enum PolicyShadowStatusId
    {
        Match = 1,
        Mismatch = 2,
        Failed = 3
    }

    [Flags]
    public enum PolicyReason
    {
        None = 0,
        UnsafePathForm = 1 << 0,
        AlternateDataStreamRefused = 1 << 1,
        WorkspaceRootOutsideLab = 1 << 2,
        OutputRootOutsideWorkspace = 1 << 3,
        ProtectedRootRefused = 1 << 4,
        ReparsePointRefused = 1 << 5,
        DataRootOutsideWorkspace = 1 << 6,
        PathInspectionFailed = 1 << 7,
        Unknown = 1 << 30
    }

    public enum WorkflowOperationId
    {
        Initial = 1,
        Advance = 2,
        AdvanceReviewed = 3,
        AdvanceReviewedReceipt = 4
    }

    public enum WorkflowOutcomeId
    {
        Succeeded = 1,
        Refused = 2,
        Failed = 3
    }

    public enum WorkflowFailureKindId
    {
        Unknown = -1,
        None = 0,
        Refusal = 1,
        IO = 2,
        Access = 3,
        Cancellation = 4,
        Unexpected = 5
    }

    public enum DesktopFailureOperationId
    {
        BlankNpcBuild = 1,
        RaceMenuNpcBuild = 2,
        BlenderNpcVisualPreview = 3,
        BlenderPreviewImage = 4,
        BlenderPreviewNifExport = 5,
        FaceGeomHairRegions = 6,
        FaceTintEncode = 7,
        FaceTintDecode = 8
    }

    public enum DesktopFailureStageId
    {
        Build = 1,
        Process = 2
    }

    public enum DesktopFailureKindId
    {
        HandledResult = 1,
        ProcessExit = 2,
        Cancelled = 3,
        Exception = 4
    }

    public enum DesktopProcessIdentityId
    {
        Blender = 1,
        Texconv = 2
    }

    public bool IsDispatchEnabled => SafeIsEnabled(Keywords.Dispatch);
    public bool IsPolicyEnabled => SafeIsEnabled(Keywords.Policy);
    public bool IsPolicyShadowEnabled => SafeIsEnabled(Keywords.PolicyShadow);
    public bool IsWorkflowEnabled => SafeIsEnabled(Keywords.Workflow);

    [NonEvent]
    public void RecordDispatchStart(
        Guid invocationId,
        DispatchRouteId routeId,
        string commandName)
    {
        if (!IsDispatchEnabled)
            return;
        try
        {
            DispatchStart(invocationId, (int)routeId, commandName);
        }
        catch (Exception)
        {
        }
    }

    [NonEvent]
    public void RecordDispatchStop(
        Guid invocationId,
        DispatchRouteId routeId,
        string commandName,
        CommandExitCode exitCode,
        long elapsedMilliseconds)
    {
        if (!IsDispatchEnabled)
            return;
        try
        {
            DispatchStop(
                invocationId,
                (int)routeId,
                commandName,
                (int)exitCode,
                elapsedMilliseconds);
        }
        catch (Exception)
        {
        }
    }

    [NonEvent]
    public void RecordDispatchFault(
        Guid invocationId,
        DispatchRouteId routeId,
        string commandName,
        DispatchFaultKindId faultKindId)
    {
        if (!IsDispatchEnabled)
            return;
        try
        {
            DispatchFault(
                invocationId,
                (int)routeId,
                commandName,
                (int)faultKindId);
        }
        catch (Exception)
        {
        }
    }

    [NonEvent]
    public void RecordPolicyDecision(
        PolicyKindId policyKindId,
        PolicyDecisionId decisionId,
        PolicyReason reasonFlags)
    {
        if (!IsPolicyEnabled)
            return;
        try
        {
            PolicyDecision(
                (int)policyKindId,
                (int)decisionId,
                (int)reasonFlags);
        }
        catch (Exception)
        {
        }
    }

    [NonEvent]
    public void RecordPolicyShadowComparison(
        PolicyKindId policyKindId,
        PolicyShadowStatusId statusId)
    {
        if (!IsPolicyShadowEnabled)
            return;
        try
        {
            PolicyShadowComparison((int)policyKindId, (int)statusId);
        }
        catch (Exception)
        {
        }
    }

    [NonEvent]
    public void RecordWorkflowOutcome(
        WorkflowOperationId operationKindId,
        int phaseId,
        WorkflowOutcomeId outcomeId,
        WorkflowFailureKindId failureKindId)
    {
        if (!IsWorkflowEnabled)
            return;
        try
        {
            WorkflowOutcome(
                (int)operationKindId,
                phaseId,
                (int)outcomeId,
                (int)failureKindId);
        }
        catch (Exception)
        {
        }
    }

    [NonEvent]
    public void RecordDesktopProcessFailure(
        Activity? activity,
        DesktopFailureOperationId operationId,
        DesktopProcessIdentityId processIdentityId,
        string expectedExecutableSha256,
        int exitCode,
        string diagnosticCode)
    {
        if (exitCode == 0 ||
            !SafeIsEnabled(EventLevel.Error, Keywords.DesktopFailureEvidence) ||
            !IsDefined(operationId) ||
            !IsDefined(processIdentityId) ||
            !IsSha256(expectedExecutableSha256) ||
            !IsSafeDiagnosticCode(diagnosticCode))
            return;

        string traceId = activity?.TraceId.ToString() ?? string.Empty;
        if (!IsTraceId(traceId))
            traceId = ActivityTraceId.CreateRandom().ToString();
        try
        {
            DesktopProcessFailure(
                traceId,
                (int)operationId,
                (int)DesktopFailureStageId.Process,
                (int)processIdentityId,
                expectedExecutableSha256,
                exitCode,
                diagnosticCode);
        }
        catch (Exception)
        {
        }
    }

    [Event(1, Level = EventLevel.Informational,
        Keywords = Keywords.Dispatch)]
    private void DispatchStart(Guid invocationId, int routeId, string commandName)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Dispatch))
            WriteEvent(1, invocationId, routeId, commandName);
    }

    [Event(2, Level = EventLevel.Informational,
        Keywords = Keywords.Dispatch)]
    private void DispatchStop(
        Guid invocationId,
        int routeId,
        string commandName,
        int exitCode,
        long elapsedMilliseconds)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Dispatch))
            WriteEvent(
                2,
                invocationId,
                routeId,
                commandName,
                exitCode,
                elapsedMilliseconds);
    }

    [Event(3, Level = EventLevel.Informational,
        Keywords = Keywords.Dispatch)]
    private void DispatchFault(
        Guid invocationId,
        int routeId,
        string commandName,
        int faultKindId)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Dispatch))
            WriteEvent(3, invocationId, routeId, commandName, faultKindId);
    }

    [Event(4, Level = EventLevel.Informational, Keywords = Keywords.Policy)]
    private void PolicyDecision(
        int policyKindId,
        int decisionId,
        int reasonFlags)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Policy))
            WriteEvent(4, policyKindId, decisionId, reasonFlags);
    }

    [Event(5, Level = EventLevel.Informational, Keywords = Keywords.Workflow)]
    private void WorkflowOutcome(
        int operationKindId,
        int phaseId,
        int outcomeId,
        int failureKindId)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Workflow))
            WriteEvent(5, operationKindId, phaseId, outcomeId, failureKindId);
    }

    [Event(6, Level = EventLevel.Informational,
        Keywords = Keywords.PolicyShadow)]
    private void PolicyShadowComparison(int policyKindId, int statusId)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.PolicyShadow))
            WriteEvent(6, policyKindId, statusId);
    }

    [Event(7, Level = EventLevel.Error,
        Keywords = Keywords.DesktopFailureEvidence)]
    private void DesktopProcessFailure(
        string traceId,
        int operationId,
        int stageId,
        int processIdentityId,
        string expectedExecutableSha256,
        int exitCode,
        string diagnosticCode)
    {
        if (IsEnabled(EventLevel.Error, Keywords.DesktopFailureEvidence))
            WriteEvent(
                7,
                traceId,
                operationId,
                stageId,
                processIdentityId,
                expectedExecutableSha256,
                exitCode,
                diagnosticCode);
    }

    private static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum => Enum.IsDefined(value);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(IsHex);

    private static bool IsTraceId(string? value) =>
        value is { Length: 32 } &&
        value.Any(character => character != '0') &&
        value.All(IsHex);

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or
            >= 'A' and <= 'F' or
            >= 'a' and <= 'f';

    private static bool IsSafeDiagnosticCode(string? value) =>
        value is { Length: > 0 and <= 80 } &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '-' or '_');
    [NonEvent]
    private bool SafeIsEnabled(
        EventLevel level,
        EventKeywords keyword)
    {
        try
        {
            return IsEnabled(level, keyword);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [NonEvent]
    private bool SafeIsEnabled(EventKeywords keyword)
    {
        try
        {
            return IsEnabled(EventLevel.Informational, keyword);
        }
        catch (Exception)
        {
            return false;
        }
    }

}
