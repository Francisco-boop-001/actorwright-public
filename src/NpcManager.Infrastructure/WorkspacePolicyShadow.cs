using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal enum WorkspacePolicyShadowKind
{
    Output,
    ReadRoot
}

internal enum WorkspacePolicyPathInspection
{
    Clear,
    ReparsePoint,
    Failed
}

internal readonly record struct WorkspacePolicyShadowRequest(
    WorkspacePolicyShadowKind Kind,
    WorkspacePath LabRoot,
    WorkspacePath ProtectedLiveRoot,
    WorkspacePath WorkspaceRoot,
    WorkspacePath TargetRoot,
    WorkspacePolicyPathInspection WorkspaceInspection,
    WorkspacePolicyPathInspection TargetInspection);

internal readonly record struct WorkspacePolicyShadowFinding(
    string Code,
    DiagnosticSeverity Severity);

internal readonly record struct WorkspacePolicyShadowDecision(
    bool IsAllowed,
    ImmutableArray<WorkspacePolicyShadowFinding> Findings);

internal static class WorkspacePolicyShadow
{
    internal static WorkspacePolicyShadowDecision Evaluate(
        WorkspacePolicyShadowRequest request)
    {
        var findings = ImmutableArray.CreateBuilder<WorkspacePolicyShadowFinding>();
        AddUnsafePathFindings(findings, request.WorkspaceRoot);
        AddUnsafePathFindings(findings, request.TargetRoot);

        if (!request.WorkspaceRoot.IsUnder(request.LabRoot))
        {
            AddFinding(
                findings,
                ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab);
        }

        if (!request.TargetRoot.IsUnder(request.WorkspaceRoot))
        {
            AddFinding(
                findings,
                request.Kind == WorkspacePolicyShadowKind.Output
                    ? ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace
                    : "data-root-outside-workspace");
        }

        if (request.TargetRoot.IsUnder(request.ProtectedLiveRoot) ||
            request.ProtectedLiveRoot.IsUnder(request.TargetRoot) ||
            request.WorkspaceRoot.IsUnder(request.ProtectedLiveRoot))
        {
            AddFinding(
                findings,
                ProtocolV2DiagnosticCodes.ProtectedRootRefused);
        }

        AddInspectionFinding(findings, request.WorkspaceInspection);
        AddInspectionFinding(findings, request.TargetInspection);

        ImmutableArray<WorkspacePolicyShadowFinding> result = findings.ToImmutable();
        bool isAllowed = true;
        foreach (WorkspacePolicyShadowFinding finding in result)
        {
            if (finding.Severity == DiagnosticSeverity.Error)
            {
                isAllowed = false;
                break;
            }
        }

        return new WorkspacePolicyShadowDecision(isAllowed, result);
    }

    internal static bool Matches(
        ImmutableArray<Diagnostic> authoritativeDiagnostics,
        WorkspacePolicyShadowDecision decision)
    {
        bool authoritativeIsAllowed = true;
        foreach (Diagnostic diagnostic in authoritativeDiagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
                authoritativeIsAllowed = false;
        }

        if (authoritativeIsAllowed != decision.IsAllowed ||
            authoritativeDiagnostics.Length != decision.Findings.Length)
            return false;

        for (var index = 0; index < authoritativeDiagnostics.Length; index++)
        {
            Diagnostic authoritative = authoritativeDiagnostics[index];
            WorkspacePolicyShadowFinding shadow = decision.Findings[index];
            if (!string.Equals(authoritative.Code, shadow.Code, StringComparison.Ordinal) ||
                authoritative.Severity != shadow.Severity)
                return false;
        }

        return true;
    }

    private static void AddUnsafePathFindings(
        ImmutableArray<WorkspacePolicyShadowFinding>.Builder findings,
        WorkspacePath path)
    {
        string value = path.Value;
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            AddFinding(findings, ProtocolV2DiagnosticCodes.UnsafePathForm);
        }

        if (value.IndexOf(':', 2) >= 0)
            AddFinding(findings, ProtocolV2DiagnosticCodes.AlternateDataStreamRefused);
    }

    private static void AddInspectionFinding(
        ImmutableArray<WorkspacePolicyShadowFinding>.Builder findings,
        WorkspacePolicyPathInspection inspection)
    {
        switch (inspection)
        {
            case WorkspacePolicyPathInspection.ReparsePoint:
                AddFinding(findings, ProtocolV2DiagnosticCodes.ReparsePointRefused);
                break;
            case WorkspacePolicyPathInspection.Failed:
                AddFinding(findings, ProtocolV2DiagnosticCodes.PathInspectionFailed);
                break;
        }
    }

    private static void AddFinding(
        ImmutableArray<WorkspacePolicyShadowFinding>.Builder findings,
        string code) =>
        findings.Add(new WorkspacePolicyShadowFinding(code, DiagnosticSeverity.Error));
}
