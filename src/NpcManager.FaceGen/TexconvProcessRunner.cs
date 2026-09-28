using System.Collections.Immutable;
using System.Diagnostics;
using NpcManager.Application;
using NpcManager.Domain;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;
using DesktopProcessIdentityId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopProcessIdentityId;

namespace NpcManager.FaceGen;

internal static class TexconvProcessRunner
{
    private static readonly TimeSpan ExecutionBudget = TimeSpan.FromMinutes(2);

    public static async ValueTask<TexconvProcessResult> RunAsync(
        TexconvCodecSession session,
        string pixelFormat,
        string diagnosticPrefix,
        DesktopFailureOperationId operationId,
        Sha256Hash expectedExecutableSha256,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = session.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(session.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-nologo", "-f", pixelFormat, "-m", "1", "-y", "-o",
                     session.OutputDirectory, session.SourcePath
                 })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The DirectXTex process could not be started.");
        session.MarkChildStarted();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ExecutionBudget);
        var token = budget.Token;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the termination request.
            }
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode == 0) return new TexconvProcessResult(true, []);

        ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
            Activity.Current,
            operationId,
            DesktopProcessIdentityId.Texconv,
            expectedExecutableSha256.Value,
            process.ExitCode,
            diagnosticPrefix + "-process-failed");

        var details = string.Join(" ", new[] { output, error }
            .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        if (details.Length > 2048) details = details[..2048];
        return new TexconvProcessResult(false,
            [new Diagnostic(diagnosticPrefix + "-process-failed", DiagnosticSeverity.Error,
                $"DirectXTex exited with code {process.ExitCode}: {details}")]);
    }
}

internal sealed record TexconvProcessResult(
    bool Completed,
    ImmutableArray<Diagnostic> Diagnostics);
