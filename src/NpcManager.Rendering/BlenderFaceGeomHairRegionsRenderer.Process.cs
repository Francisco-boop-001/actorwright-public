using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using NpcManager.Domain;

namespace NpcManager.Rendering;
internal sealed record FaceGeomHairRegionsProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    ImmutableArray<int> SurvivingProcessIds);

internal interface IFaceGeomHairRegionsProcessRunner
{
    ValueTask<FaceGeomHairRegionsProcessResult> RunAsync(
        WorkspacePath executable,
        ImmutableArray<string> arguments,
        WorkspacePath? blenderProfile,
        WorkspacePath? pythonCachePrefix,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<FaceGeomHairRegionsProcessResult> RunAsync(
        WorkspacePath executable,
        ImmutableArray<string> arguments,
        WorkspacePath? blenderProfile,
        WorkspacePath? pythonCachePrefix,
        string standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunAsync(
            executable,
            arguments,
            blenderProfile,
            pythonCachePrefix,
            timeout,
            cancellationToken);
}

internal interface IFaceGeomHairRegionsProcessHost
{
    IFaceGeomHairRegionsRunningProcess Start(
        ProcessStartInfo startInfo);
}

internal interface IFaceGeomHairRegionsRunningProcess :
    IDisposable
{
    int Id
    {
        get;
    }

    bool HasExited
    {
        get;
    }

    int ExitCode
    {
        get;
    }

    StreamReader StandardOutput
    {
        get;
    }

    StreamReader StandardError
    {
        get;
    }

    TextWriter StandardInput => TextWriter.Null;

    void Kill(bool entireProcessTree);

    Task WaitForExitAsync(
        CancellationToken cancellationToken);
}

internal sealed class SystemFaceGeomHairRegionsProcessRunner :
    IFaceGeomHairRegionsProcessRunner
{
    private readonly IFaceGeomHairRegionsProcessHost processHost;

    public SystemFaceGeomHairRegionsProcessRunner()
        : this(
            new SystemFaceGeomHairRegionsProcessHost())
    {
    }

    internal SystemFaceGeomHairRegionsProcessRunner(
        IFaceGeomHairRegionsProcessHost processHost)
    {
        this.processHost = processHost ??
            throw new ArgumentNullException(
                nameof(processHost));
    }

    public async ValueTask<FaceGeomHairRegionsProcessResult>
        RunAsync(
            WorkspacePath executable,
            ImmutableArray<string> arguments,
            WorkspacePath? blenderProfile,
            WorkspacePath? pythonCachePrefix,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
        await RunAsync(
            executable,
            arguments,
            blenderProfile,
            pythonCachePrefix,
            standardInput: null,
            timeout,
            cancellationToken);

    public async ValueTask<FaceGeomHairRegionsProcessResult>
        RunAsync(
            WorkspacePath executable,
            ImmutableArray<string> arguments,
            WorkspacePath? blenderProfile,
            WorkspacePath? pythonCachePrefix,
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
        if (pythonCachePrefix is not null)
        {
            foreach (string key in start.Environment.Keys
                         .Where(item => item.StartsWith(
                             "PYTHON",
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                start.Environment.Remove(key);
            start.ArgumentList.Add("--python-use-system-env");
        }
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
        if (pythonCachePrefix is { } cache)
        {
            start.Environment["PYTHONPYCACHEPREFIX"] =
                cache.Value;
            start.Environment["PYTHONDONTWRITEBYTECODE"] =
                "1";
            start.Environment["PYTHONNOUSERSITE"] =
                "1";
        }
        IFaceGeomHairRegionsRunningProcess? started;
        try
        {
            started = processHost.Start(start);
        }
        catch (Exception exception) when (
            exception is
                Win32Exception or
                IOException or
                UnauthorizedAccessException or
                SecurityException or
                InvalidOperationException)
        {
            throw new FaceGeomHairRegionsProcessStartException(
                executable,
                exception);
        }
        if (started is null)
            throw new FaceGeomHairRegionsProcessStartException(
                executable,
                new IOException(
                    "The process host returned no process."));
        using IFaceGeomHairRegionsRunningProcess process =
            started;
        using var budget =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            Task<string> stdout =
                process.StandardOutput.ReadToEndAsync(
                    budget.Token);
            Task<string> stderr =
                process.StandardError.ReadToEndAsync(
                    budget.Token);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(
                    standardInput.AsMemory(),
                    budget.Token);
                await process.StandardInput.FlushAsync(
                    budget.Token);
                process.StandardInput.Dispose();
            }
            await process.WaitForExitAsync(
                budget.Token);
            string output = await stdout;
            string error = await stderr;
            return new FaceGeomHairRegionsProcessResult(
                process.ExitCode,
                output,
                error,
                []);
        }
        catch (OperationCanceledException exception)
        {
            ProcessTerminationEvidence termination =
                await TerminateOrProveExitedAsync(
                    process);
            throw new FaceGeomHairRegionsProcessCanceledException(
                termination.SurvivingProcessIds,
                termination.Failure,
                exception);
        }
        catch (Exception exception) when (
            exception is
                Win32Exception or
                IOException or
                UnauthorizedAccessException or
                SecurityException or
                InvalidOperationException)
        {
            ProcessTerminationEvidence termination =
                await TerminateOrProveExitedAsync(
                    process);
            throw new FaceGeomHairRegionsProcessRenderException(
                termination.ProcessId,
                termination.SurvivingProcessIds,
                termination.Failure,
                exception);
        }
    }

    private static async ValueTask<ProcessTerminationEvidence>
        TerminateOrProveExitedAsync(
            IFaceGeomHairRegionsRunningProcess process)
    {
        int processId;
        try
        {
            processId = process.Id;
        }
        catch (Exception exception) when (
            IsExpectedProcessOperationException(
                exception))
        {
            processId = -1;
        }

        try
        {
            if (process.HasExited)
                return new ProcessTerminationEvidence(
                    processId,
                    [],
                    null);
        }
        catch (Exception exception) when (
            IsExpectedProcessOperationException(
                exception))
        {
            _ = exception;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            using var termination =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(
                termination.Token);
            return new ProcessTerminationEvidence(
                processId,
                [],
                null);
        }
        catch (Exception killException) when (
            IsExpectedProcessOperationException(
                killException) ||
            killException is OperationCanceledException)
        {
            bool hasExited;
            try
            {
                hasExited = process.HasExited;
            }
            catch (Exception statusException) when (
                IsExpectedProcessOperationException(
                    statusException))
            {
                hasExited = false;
            }
            ImmutableArray<int> survivors =
                !hasExited && processId > 0
                    ? [processId]
                    : [];
            return new ProcessTerminationEvidence(
                processId,
                survivors,
                new FaceGeomHairRegionsProcessTerminationException(
                    processId,
                    killException));
        }
    }

    private static bool IsExpectedProcessOperationException(
        Exception exception) =>
        exception is
            Win32Exception or
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            InvalidOperationException;

    private sealed record ProcessTerminationEvidence(
        int ProcessId,
        ImmutableArray<int> SurvivingProcessIds,
        FaceGeomHairRegionsProcessTerminationException?
            Failure);
}

internal sealed class SystemFaceGeomHairRegionsProcessHost :
    IFaceGeomHairRegionsProcessHost
{
    public IFaceGeomHairRegionsRunningProcess Start(
        ProcessStartInfo startInfo)
    {
        Process process = Process.Start(startInfo) ??
            throw new IOException(
                "Process.Start returned no process.");
        return new SystemFaceGeomHairRegionsRunningProcess(
            process);
    }
}

internal sealed class SystemFaceGeomHairRegionsRunningProcess(
    Process process) :
    IFaceGeomHairRegionsRunningProcess
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public StreamReader StandardOutput =>
        process.StandardOutput;

    public StreamReader StandardError =>
        process.StandardError;

    public TextWriter StandardInput =>
        process.StandardInput;

    public void Kill(bool entireProcessTree) =>
        process.Kill(entireProcessTree);

    public Task WaitForExitAsync(
        CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public void Dispose() =>
        process.Dispose();
}

internal sealed class FaceGeomHairRegionsProcessStartException :
    IOException
{
    public FaceGeomHairRegionsProcessStartException(
        WorkspacePath executable,
        Exception innerException)
        : base(
            $"Could not start renderer process '{executable.Value}'.",
            innerException)
    {
        Executable = executable;
    }

    public WorkspacePath Executable
    {
        get;
    }
}

internal sealed class FaceGeomHairRegionsProcessTerminationException :
    IOException
{
    public FaceGeomHairRegionsProcessTerminationException(
        int processId,
        Exception innerException)
        : base(
            $"Could not terminate renderer process PID {processId}.",
            innerException)
    {
        ProcessId = processId;
    }

    public int ProcessId
    {
        get;
    }
}

internal sealed class FaceGeomHairRegionsProcessRenderException :
    IOException
{
    public FaceGeomHairRegionsProcessRenderException(
        int processId,
        ImmutableArray<int> survivingProcessIds,
        FaceGeomHairRegionsProcessTerminationException?
            terminationFailure,
        Exception innerException)
        : base(
            $"Renderer process PID {processId} failed during execution or evidence capture.",
            innerException)
    {
        ProcessId = processId;
        SurvivingProcessIds =
            survivingProcessIds;
        TerminationFailure =
            terminationFailure;
    }

    public int ProcessId
    {
        get;
    }

    public ImmutableArray<int> SurvivingProcessIds
    {
        get;
    }

    public FaceGeomHairRegionsProcessTerminationException?
        TerminationFailure
    {
        get;
    }
}

internal sealed class FaceGeomHairRegionsProcessCanceledException :
    OperationCanceledException
{
    public FaceGeomHairRegionsProcessCanceledException(
        ImmutableArray<int> survivingProcessIds,
        FaceGeomHairRegionsProcessTerminationException?
            terminationFailure,
        Exception innerException)
        : base(
            survivingProcessIds.IsDefaultOrEmpty
                ? "Renderer process tree terminated."
                : "Renderer process tree retained PID(s): " +
                  string.Join(
                      ",",
                      survivingProcessIds),
            innerException)
    {
        SurvivingProcessIds =
            survivingProcessIds;
        TerminationFailure =
            terminationFailure;
    }

    public ImmutableArray<int> SurvivingProcessIds
    {
        get;
    }

    public FaceGeomHairRegionsProcessTerminationException?
        TerminationFailure
    {
        get;
    }
}
