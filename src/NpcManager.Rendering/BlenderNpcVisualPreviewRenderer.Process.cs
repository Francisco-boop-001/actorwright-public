using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

internal sealed record NpcVisualPreviewCapturedStream(
    string Text,
    bool Truncated);

internal sealed record NpcVisualPreviewProcessResult(
    int ExitCode,
    NpcVisualPreviewCapturedStream StandardOutput,
    NpcVisualPreviewCapturedStream StandardError,
    ImmutableArray<Diagnostic> Diagnostics);

internal interface INpcVisualPreviewProcessRunner
{
    ValueTask<NpcVisualPreviewProcessResult> RunAsync(
        WorkspacePath executable,
        ImmutableArray<string> arguments,
        WorkspacePath? blenderProfile,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class SystemNpcVisualPreviewProcessRunner :
    INpcVisualPreviewProcessRunner
{
    private const int MaximumCapturedCharacters = 16_384;
    private const int ReadBufferCharacters = 4_096;

    public async ValueTask<NpcVisualPreviewProcessResult> RunAsync(
        WorkspacePath executable,
        ImmutableArray<string> arguments,
        WorkspacePath? blenderProfile,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable.Value,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (blenderProfile is { } profile)
        {
            start.Environment["BLENDER_USER_CONFIG"] =
                Path.Combine(profile.Value, "config");
            start.Environment["BLENDER_USER_SCRIPTS"] =
                Path.Combine(profile.Value, "scripts");
            start.Environment["BLENDER_USER_DATA"] =
                Path.Combine(profile.Value, "data");
        }

        using Process process = Process.Start(start) ??
            throw new IOException(
                $"Could not start '{executable.Value}'.");
        using var budget =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        budget.CancelAfter(timeout);
        Task<NpcVisualPreviewCapturedStream> stdout =
            DrainAsync(process.StandardOutput, budget.Token);
        Task<NpcVisualPreviewCapturedStream> stderr =
            DrainAsync(process.StandardError, budget.Token);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(
                standardInput.AsMemory(), budget.Token);
            await process.StandardInput.FlushAsync(budget.Token);
            process.StandardInput.Close();
        }

        var exited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? _, EventArgs __) =>
            exited.TrySetResult();
        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        if (process.HasExited)
            exited.TrySetResult();
        try
        {
            await exited.Task.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        finally
        {
            process.Exited -= OnExited;
        }

        Task outputDrain = Task.WhenAll(stdout, stderr);
        NpcVisualPreviewCapturedStream unavailable =
            new("", true);
        try
        {
            await outputDrain.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            budget.Cancel();
            _ = outputDrain.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            if (process.ExitCode == 0)
                return new NpcVisualPreviewProcessResult(
                    process.ExitCode,
                    unavailable,
                    unavailable,
                    [
                        Warning(
                            "npc-preview-process-output-drain-incomplete",
                            $"'{Path.GetFileName(executable.Value)}' exited successfully, but a descendant retained a redirected output handle; structured artifact verification continued after the bounded drain.")
                    ]);
            return new NpcVisualPreviewProcessResult(
                process.ExitCode,
                unavailable,
                unavailable,
                [
                    Error(
                        "npc-preview-process-output-drain-failed",
                        $"'{Path.GetFileName(executable.Value)}' exited {process.ExitCode}, and its redirected output did not close within the bounded drain.")
                ]);
        }

        catch (Exception exception) when (
            exception is IOException or
                InvalidOperationException)
        {
            budget.Cancel();
            _ = outputDrain.Exception;
            return new NpcVisualPreviewProcessResult(
                process.ExitCode,
                unavailable,
                unavailable,
                [
                    Error(
                        "npc-preview-process-output-drain-failed",
                        $"'{Path.GetFileName(executable.Value)}' exited {process.ExitCode}, but its redirected output could not be drained: {exception.Message}")
                ]);
        }

        NpcVisualPreviewCapturedStream output = await stdout;
        NpcVisualPreviewCapturedStream error = await stderr;
        if (process.ExitCode == 0)
            return new NpcVisualPreviewProcessResult(
                process.ExitCode,
                output,
                error,
                []);
        string detail = string.Join(
                " ",
                new[] { output.Text, error.Text }
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value)))
            .Trim();
        if (detail.Length > 4_096)
            detail = detail[..4_096];
        return new NpcVisualPreviewProcessResult(
            process.ExitCode,
            output,
            error,
            [
                Error(
                    "npc-preview-process-failed",
                    $"'{Path.GetFileName(executable.Value)}' exited {process.ExitCode}: {detail}")
            ]);
    }

    private static async Task<NpcVisualPreviewCapturedStream> DrainAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[ReadBufferCharacters];
        var retained = new StringBuilder(
            MaximumCapturedCharacters);
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            int available =
                MaximumCapturedCharacters - retained.Length;
            int retain = Math.Min(read, Math.Max(0, available));
            if (retain > 0)
                retained.Append(buffer, 0, retain);
            if (retain < read)
                truncated = true;
        }
        return new NpcVisualPreviewCapturedStream(
            retained.ToString(),
            truncated);
    }

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Warning, message);
}
