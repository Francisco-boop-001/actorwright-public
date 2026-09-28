using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Validates explicit copied game roots before any read or write workflow.</summary>
public sealed class GameRootPreflightService(
    IWorkspacePolicy policy) : IGameRootPreflightService
{
    public ValueTask<GameRootPreflightResult> EvaluateAsync(
        GameRootPreflightRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.Evaluate(request.WorkspaceRoot, request.OutputRoot));
        diagnostics.AddRange(policy.EvaluateReadRoot(request.WorkspaceRoot, request.DataRoot));

        if (!Directory.Exists(request.WorkspaceRoot.Value))
        {
            diagnostics.Add(new Diagnostic("workspace-root-missing", DiagnosticSeverity.Error,
                "The explicit workspace root does not exist."));
        }
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("data-root-missing", DiagnosticSeverity.Error,
                "The explicit copied Data root does not exist."));
        }

        if (request.OutputRoot.IsUnder(request.DataRoot) || request.DataRoot.IsUnder(request.OutputRoot))
        {
            diagnostics.Add(new Diagnostic("read-write-overlap", DiagnosticSeverity.Error,
                "The output root and copied Data root may not contain one another."));
        }

        return ValueTask.FromResult(new GameRootPreflightResult(request.Edition,
            request.WorkspaceRoot, request.DataRoot, request.OutputRoot,
            !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error), diagnostics.ToImmutable()));
    }
}
