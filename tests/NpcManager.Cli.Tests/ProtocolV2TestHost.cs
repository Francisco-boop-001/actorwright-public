using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal sealed record CliBoundaryResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal static class ProtocolV2TestHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<CliBoundaryResult> RunAsync(string[] args)
        => await RunAsync(args, false, null, null);

    public static async Task<CliBoundaryResult> RunWithWorkspaceRootAsync(
        string[] args,
        string? workspaceRoot)
        => await RunAsync(args, true, workspaceRoot, null);

    public static async Task<CliBoundaryResult> RunWithJournalAsync(
        string[] args,
        ILocalOperationJournal journal)
        => await RunWithJournalFactoryAsync(args, _ => journal);

    public static async Task<CliBoundaryResult> RunWithJournalFactoryAsync(
        string[] args,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory)
        => await RunAsync(args, false, null, journalFactory);

    private static async Task<CliBoundaryResult> RunAsync(
        string[] args,
        bool overrideWorkspaceRoot,
        string? workspaceRoot,
        Func<WorkspacePath, ILocalOperationJournal>? journalFactory)
    {
        await Gate.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalWorkspaceRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            if (overrideWorkspaceRoot)
            {
                Environment.SetEnvironmentVariable(
                    ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                    workspaceRoot);
            }
            Console.SetOut(output);
            Console.SetError(error);
            var exit = journalFactory is null
                ? await NpcManager.Cli.Program.Main(args)
                : (int)await NpcManager.Cli.Program.RunAsync(
                    args,
                    Console.Out,
                    Console.Error,
                    journalFactory,
                    CancellationToken.None);
            return new CliBoundaryResult(
                exit,
                output.ToString(),
                error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                originalWorkspaceRoot);
            Gate.Release();
        }
    }
}
