using NpcManager.Application;

namespace NpcManager.Infrastructure;

public sealed class WorkspacePreflightService(IWorkspacePolicy policy) : IWorkspacePreflightService
{
    public ValueTask<WorkspacePreflightResult> EvaluateAsync(WorkspacePreflightRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = policy.Evaluate(request.WorkspaceRoot, request.OutputRoot);
        return ValueTask.FromResult(new WorkspacePreflightResult(
            !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
            request.WorkspaceRoot,
            request.OutputRoot,
            diagnostics));
    }
}
