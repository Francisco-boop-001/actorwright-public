using NpcManager.Application;

namespace NpcManager.Cli;

internal sealed record RaceMenuJslotNpcBuildCommandExecution(
    CommandExitCode ExitCode,
    string Output,
    string Error)
{
    /// <summary>
    /// Typed in-memory result for the external-SMP Protocol path. Ordinary
    /// command execution keeps the legacy stdout/stderr projection and leaves
    /// this null.
    /// </summary>
    public RaceMenuJslotNpcBuildResult? TypedResult { get; init; }

    /// <summary>
    /// Set only by the typed bridge after it has admitted an external-SMP
    /// standalone authority. This keeps the Protocol gate independent from
    /// command stdout and from caller-provided JSON.
    /// </summary>
    public bool TypedExternalSmp { get; init; }
}

internal interface IRaceMenuJslotNpcBuildCommandExecutor
{
    ValueTask<RaceMenuJslotNpcBuildCommandExecution> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken);
}

internal sealed class RaceMenuJslotNpcBuildCommandExecutor(
    Func<ParsedCommand, TextWriter, TextWriter, CancellationToken,
        ValueTask<CommandExitCode>> execute) :
    IRaceMenuJslotNpcBuildCommandExecutor
{
    public async ValueTask<RaceMenuJslotNpcBuildCommandExecution> ExecuteAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var output = new StringWriter();
        using var error = new StringWriter();
        CommandExitCode exit = await execute(
            command,
            output,
            error,
            cancellationToken).ConfigureAwait(false);
        return new RaceMenuJslotNpcBuildCommandExecution(
            exit,
            output.ToString(),
            error.ToString());
    }
}

/// <summary>
/// Executes a JSlot build from an already admitted in-memory request. This
/// bridge deliberately returns the service result instead of reparsing the
/// legacy command's JSON output, so Manager-only external-SMP evidence cannot
/// be manufactured through command input.
/// </summary>
internal sealed class RaceMenuJslotNpcBuildCommandExecutionBridge(
    IRaceMenuJslotNpcBuildService service)
{
    private readonly IRaceMenuJslotNpcBuildService buildService =
        service ?? throw new ArgumentNullException(nameof(service));

    public async ValueTask<RaceMenuJslotNpcBuildCommandExecution> ExecuteAsync(
        ParsedCommand command,
        RaceMenuJslotNpcBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(request);
        RaceMenuJslotNpcBuildResult result = await buildService.ExecuteAsync(
            request,
            progress: null,
            cancellationToken).ConfigureAwait(false);
        return new RaceMenuJslotNpcBuildCommandExecution(
            result.Completed
                ? CommandExitCode.Success
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics),
            string.Empty,
            string.Empty)
        {
            TypedResult = result,
            TypedExternalSmp = RequiresExternalSmp(result)
        };
    }

    internal static bool RequiresExternalSmp(
        RaceMenuJslotNpcBuildResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RaceMenuNpcStandaloneAssets? assets = result.Execution?.Assets;
        if (assets is null)
            return false;
        return RequiresExternalSmp(assets);
    }

    internal static bool RequiresExternalSmp(
        RaceMenuNpcStandaloneAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return assets.SchemaVersion == 8;
    }
}
