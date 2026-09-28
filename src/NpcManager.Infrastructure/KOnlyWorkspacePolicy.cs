using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class KOnlyWorkspacePolicy : IWorkspacePolicy
{
    private readonly WorkspacePath _labRoot;
    private readonly WorkspacePath _protectedLiveRoot;
    private readonly Func<WorkspacePolicyShadowRequest, WorkspacePolicyShadowDecision> _shadowEvaluator;

    public KOnlyWorkspacePolicy(WorkspacePath labRoot, WorkspacePath protectedLiveRoot)
        : this(labRoot, protectedLiveRoot, WorkspacePolicyShadow.Evaluate)
    {
    }

    internal KOnlyWorkspacePolicy(
        WorkspacePath labRoot,
        WorkspacePath protectedLiveRoot,
        Func<WorkspacePolicyShadowRequest, WorkspacePolicyShadowDecision> shadowEvaluator)
    {
        ArgumentNullException.ThrowIfNull(shadowEvaluator);
        _labRoot = labRoot;
        _protectedLiveRoot = protectedLiveRoot;
        _shadowEvaluator = shadowEvaluator;
    }

    public ImmutableArray<Diagnostic> Evaluate(WorkspacePath workspaceRoot, WorkspacePath outputRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddUnsafePathDiagnostic(diagnostics, workspaceRoot, "workspace-root");
        AddUnsafePathDiagnostic(diagnostics, outputRoot, "output-root");

        if (!workspaceRoot.IsUnder(_labRoot))
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab, DiagnosticSeverity.Error,
                "The workspace root must remain under the configured K-only lab root."));
        }

        if (!outputRoot.IsUnder(workspaceRoot))
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace, DiagnosticSeverity.Error,
                "The output root must remain under the explicit workspace root."));
        }

        if (outputRoot.IsUnder(_protectedLiveRoot) ||
            _protectedLiveRoot.IsUnder(outputRoot) ||
            workspaceRoot.IsUnder(_protectedLiveRoot))
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.ProtectedRootRefused, DiagnosticSeverity.Error,
                "The protected live game root can never be a workspace, output, temporary, cache, or log root."));
        }

        WorkspacePolicyPathInspection workspaceInspection =
            AddReparsePointDiagnostic(diagnostics, workspaceRoot, "workspace-root");
        WorkspacePolicyPathInspection outputInspection =
            AddReparsePointDiagnostic(diagnostics, outputRoot, "output-root");
        ImmutableArray<Diagnostic> result = diagnostics.ToImmutable();
        ObservePolicyDecision(
            ActorwrightObservabilityEventSource.PolicyKindId.Output,
            result);
        ObservePolicyShadowComparison(
            ActorwrightObservabilityEventSource.PolicyKindId.Output,
            workspaceRoot,
            outputRoot,
            workspaceInspection,
            outputInspection,
            result);
        return result;
    }

    public ImmutableArray<Diagnostic> EvaluateReadRoot(WorkspacePath workspaceRoot, WorkspacePath readRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddUnsafePathDiagnostic(diagnostics, workspaceRoot, "workspace-root");
        AddUnsafePathDiagnostic(diagnostics, readRoot, "data-root");

        if (!workspaceRoot.IsUnder(_labRoot))
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab, DiagnosticSeverity.Error,
                "The workspace root must remain under the configured K-only lab root."));
        }

        if (!readRoot.IsUnder(workspaceRoot))
        {
            diagnostics.Add(new Diagnostic("data-root-outside-workspace", DiagnosticSeverity.Error,
                "The copied Data root must remain under the explicit workspace root."));
        }

        if (readRoot.IsUnder(_protectedLiveRoot) ||
            _protectedLiveRoot.IsUnder(readRoot) ||
            workspaceRoot.IsUnder(_protectedLiveRoot))
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.ProtectedRootRefused, DiagnosticSeverity.Error,
                "The protected live game root can never be a workspace, Data, temporary, cache, or log root."));
        }

        WorkspacePolicyPathInspection workspaceInspection =
            AddReparsePointDiagnostic(diagnostics, workspaceRoot, "workspace-root");
        WorkspacePolicyPathInspection readInspection =
            AddReparsePointDiagnostic(diagnostics, readRoot, "data-root");
        ImmutableArray<Diagnostic> result = diagnostics.ToImmutable();
        ObservePolicyDecision(
            ActorwrightObservabilityEventSource.PolicyKindId.Read,
            result);
        ObservePolicyShadowComparison(
            ActorwrightObservabilityEventSource.PolicyKindId.Read,
            workspaceRoot,
            readRoot,
            workspaceInspection,
            readInspection,
            result);
        return result;
    }

    private static void ObservePolicyDecision(
        ActorwrightObservabilityEventSource.PolicyKindId policyKindId,
        ImmutableArray<Diagnostic> diagnostics)
    {
        try
        {
            ActorwrightObservabilityEventSource eventSource =
                ActorwrightObservabilityEventSource.Log;
            if (!eventSource.IsPolicyEnabled)
                return;

            var reasons = ActorwrightObservabilityEventSource.PolicyReason.None;
            bool refused = false;
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    refused = true;

                reasons |= diagnostic.Code switch
                {
                    ProtocolV2DiagnosticCodes.UnsafePathForm =>
                        ActorwrightObservabilityEventSource.PolicyReason.UnsafePathForm,
                    ProtocolV2DiagnosticCodes.AlternateDataStreamRefused =>
                        ActorwrightObservabilityEventSource.PolicyReason.AlternateDataStreamRefused,
                    ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab =>
                        ActorwrightObservabilityEventSource.PolicyReason.WorkspaceRootOutsideLab,
                    ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace =>
                        ActorwrightObservabilityEventSource.PolicyReason.OutputRootOutsideWorkspace,
                    ProtocolV2DiagnosticCodes.ProtectedRootRefused =>
                        ActorwrightObservabilityEventSource.PolicyReason.ProtectedRootRefused,
                    ProtocolV2DiagnosticCodes.ReparsePointRefused =>
                        ActorwrightObservabilityEventSource.PolicyReason.ReparsePointRefused,
                    "data-root-outside-workspace" =>
                        ActorwrightObservabilityEventSource.PolicyReason.DataRootOutsideWorkspace,
                    ProtocolV2DiagnosticCodes.PathInspectionFailed =>
                        ActorwrightObservabilityEventSource.PolicyReason.PathInspectionFailed,
                    _ => ActorwrightObservabilityEventSource.PolicyReason.Unknown
                };
            }

            eventSource.RecordPolicyDecision(
                policyKindId,
                refused
                    ? ActorwrightObservabilityEventSource.PolicyDecisionId.Refused
                    : ActorwrightObservabilityEventSource.PolicyDecisionId.Allowed,
                reasons);
        }
        catch (Exception)
        {
            // Observation must not change the authoritative policy result.
        }
    }

    private void ObservePolicyShadowComparison(
        ActorwrightObservabilityEventSource.PolicyKindId policyKindId,
        WorkspacePath workspaceRoot,
        WorkspacePath targetRoot,
        WorkspacePolicyPathInspection workspaceInspection,
        WorkspacePolicyPathInspection targetInspection,
        ImmutableArray<Diagnostic> authoritativeDiagnostics)
    {
        ActorwrightObservabilityEventSource eventSource;
        try
        {
            eventSource = ActorwrightObservabilityEventSource.Log;
            if (!eventSource.IsPolicyShadowEnabled)
                return;
        }
        catch (Exception)
        {
            return;
        }

        var status = ActorwrightObservabilityEventSource.PolicyShadowStatusId.Failed;
        try
        {
            var request = new WorkspacePolicyShadowRequest(
                policyKindId == ActorwrightObservabilityEventSource.PolicyKindId.Output
                    ? WorkspacePolicyShadowKind.Output
                    : WorkspacePolicyShadowKind.ReadRoot,
                _labRoot,
                _protectedLiveRoot,
                workspaceRoot,
                targetRoot,
                workspaceInspection,
                targetInspection);
            WorkspacePolicyShadowDecision decision = _shadowEvaluator(request);
            status = WorkspacePolicyShadow.Matches(authoritativeDiagnostics, decision)
                ? ActorwrightObservabilityEventSource.PolicyShadowStatusId.Match
                : ActorwrightObservabilityEventSource.PolicyShadowStatusId.Mismatch;
        }
        catch (Exception)
        {
            // Shadow failures must not change the authoritative policy result.
        }

        try
        {
            eventSource.RecordPolicyShadowComparison(policyKindId, status);
        }
        catch (Exception)
        {
            // Observation must not change the authoritative policy result.
        }
    }

    private static void AddUnsafePathDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, WorkspacePath path, string role)
    {
        var value = path.Value;
        if (value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.UnsafePathForm, DiagnosticSeverity.Error,
                $"The {role} uses a device or UNC path, which is not accepted."));
        }

        var colonAfterDrive = value.IndexOf(':', 2);
        if (colonAfterDrive >= 0)
        {
            diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.AlternateDataStreamRefused, DiagnosticSeverity.Error,
                $"The {role} contains an alternate-data-stream delimiter, which is not accepted."));
        }
    }

    private static WorkspacePolicyPathInspection AddReparsePointDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        WorkspacePath path,
        string role)
    {
        var current = Path.GetFullPath(path.Value);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.ReparsePointRefused, DiagnosticSeverity.Error,
                        $"The {role} traverses the reparse point '{current}'; only ordinary K-local files and directories are accepted."));
                    return WorkspacePolicyPathInspection.ReparsePoint;
                }
            }
            catch (FileNotFoundException)
            {
                // Planned output leaves may not exist yet. Their existing parents still
                // have to be inspected, so continue walking toward the volume root.
            }
            catch (DirectoryNotFoundException)
            {
                // Planned output leaves may not exist yet. Their existing parents still
                // have to be inspected, so continue walking toward the volume root.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(ProtocolV2DiagnosticCodes.PathInspectionFailed, DiagnosticSeverity.Error,
                    $"The {role} ancestry could not be inspected at '{current}': {exception.Message}"));
                return WorkspacePolicyPathInspection.Failed;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return WorkspacePolicyPathInspection.Clear;
            current = parent;
        }
        return WorkspacePolicyPathInspection.Clear;
    }
}
