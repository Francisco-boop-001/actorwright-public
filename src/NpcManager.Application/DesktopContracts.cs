using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record DesktopLaunchRequest(
    WorkspacePath Executable,
    DesktopWorkflowLaunchBinding? WorkflowReview = null);

public sealed record DesktopLaunchResult(bool Launched, WorkspacePath Executable,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IDesktopLaunchService
{
    DesktopLaunchResult Launch(DesktopLaunchRequest request);
}
