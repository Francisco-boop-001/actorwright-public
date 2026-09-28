using System.Collections.Immutable;
using System.Diagnostics;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class DesktopLaunchService(IWorkspacePolicy policy, WorkspacePath labRoot) : IDesktopLaunchService
{
    public DesktopLaunchResult Launch(DesktopLaunchRequest request)
    {
        if (request is null)
        {
            return new DesktopLaunchResult(false, default,
                [new Diagnostic("desktop-launch-request-invalid", DiagnosticSeverity.Error,
                    "The desktop launch request must be supplied.")]);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var executable = request.Executable;
        var workflowDiagnostic = ValidateWorkflowReview(request.WorkflowReview);
        if (workflowDiagnostic is not null)
        {
            diagnostics.Add(workflowDiagnostic);
            return new DesktopLaunchResult(false, executable, diagnostics.ToImmutable());
        }

        var parent = Path.GetDirectoryName(executable.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(new Diagnostic("desktop-executable-parent-missing", DiagnosticSeverity.Error,
                "The desktop executable directory must already exist."));
        }
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        }

        if (!File.Exists(executable.Value))
        {
            diagnostics.Add(new Diagnostic("desktop-executable-missing", DiagnosticSeverity.Error,
                "The requested desktop executable does not exist."));
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new DesktopLaunchResult(false, executable, diagnostics.ToImmutable());

        try
        {
            var startInfo = CreateStartInfo(request, labRoot);
            var started = Process.Start(startInfo);
            if (started is null)
            {
                diagnostics.Add(new Diagnostic("desktop-launch-failed", DiagnosticSeverity.Error,
                    "The operating system did not create a desktop process."));
                return new DesktopLaunchResult(false, executable, diagnostics.ToImmutable());
            }

            started.Dispose();
            return new DesktopLaunchResult(true, executable, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("desktop-launch-failed", DiagnosticSeverity.Error,
                $"Desktop launch failed: {exception.Message}"));
            return new DesktopLaunchResult(false, executable, diagnostics.ToImmutable());
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        DesktopLaunchRequest request,
        WorkspacePath labRoot)
    {
        var workflowDiagnostic = ValidateWorkflowReview(request.WorkflowReview);
        if (workflowDiagnostic is not null)
            throw new ArgumentException(workflowDiagnostic.Message, nameof(request));

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable.Value,
            WorkingDirectory = Path.GetDirectoryName(request.Executable.Value)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable] =
            labRoot.Value;
        if (request.WorkflowReview is not { } workflowReview)
            return startInfo;

        startInfo.ArgumentList.Add("--workflow-bundle");
        startInfo.ArgumentList.Add(workflowReview.Bundle.Value);
        startInfo.ArgumentList.Add("--workflow-bundle-sha256");
        startInfo.ArgumentList.Add(workflowReview.BundleSha256);
        return startInfo;
    }

    private static Diagnostic? ValidateWorkflowReview(
        DesktopWorkflowLaunchBinding? workflowReview)
    {
        if (workflowReview is null)
            return null;
        if (string.IsNullOrWhiteSpace(workflowReview.Bundle.Value) ||
            !Path.IsPathFullyQualified(workflowReview.Bundle.Value))
        {
            return new Diagnostic("desktop-workflow-bundle-invalid", DiagnosticSeverity.Error,
                "The workflow bundle must be a fully qualified path.");
        }
        if (!IsUppercaseSha256(workflowReview.BundleSha256))
        {
            return new Diagnostic("desktop-workflow-bundle-sha256-invalid", DiagnosticSeverity.Error,
                "The workflow bundle SHA-256 must be 64 uppercase hexadecimal characters.");
        }

        return null;
    }

    private static bool IsUppercaseSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
