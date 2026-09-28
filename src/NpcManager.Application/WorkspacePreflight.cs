using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record WorkspacePreflightRequest(WorkspacePath WorkspaceRoot, WorkspacePath OutputRoot);

public sealed record WorkspacePreflightResult(
    bool IsAllowed,
    WorkspacePath WorkspaceRoot,
    WorkspacePath OutputRoot,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record GameRootPreflightRequest(
    GameEdition Edition,
    WorkspacePath WorkspaceRoot,
    WorkspacePath DataRoot,
    WorkspacePath OutputRoot);

public sealed record GameRootPreflightResult(
    GameEdition Edition,
    WorkspacePath WorkspaceRoot,
    WorkspacePath DataRoot,
    WorkspacePath OutputRoot,
    bool IsAllowed,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IWorkspacePolicy
{
    ImmutableArray<Diagnostic> Evaluate(WorkspacePath workspaceRoot, WorkspacePath outputRoot);

    ImmutableArray<Diagnostic> EvaluateReadRoot(WorkspacePath workspaceRoot, WorkspacePath readRoot);
}

public interface IWorkspacePreflightService
{
    ValueTask<WorkspacePreflightResult> EvaluateAsync(WorkspacePreflightRequest request, CancellationToken cancellationToken);
}

public interface IGameRootPreflightService
{
    ValueTask<GameRootPreflightResult> EvaluateAsync(GameRootPreflightRequest request, CancellationToken cancellationToken);
}
