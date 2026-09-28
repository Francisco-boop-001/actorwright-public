using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class Program
{
    private static readonly ImmutableHashSet<string> Preview261V1SuccessCommands =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "capabilities",
            "version",
            "diagnose",
            "gui");

    private static async Task TestPreview261V1CommandVerification()
    {
        string[] catalogNames = CommandCatalog.All
            .Select(command => command.Name)
            .ToArray();
        string[] registryNames = AgentCommandRegistry.All
            .Select(command => command.Name)
            .ToArray();

        RequireExactCommandSet("CommandCatalog.All", catalogNames);
        RequireExactCommandSet("AgentCommandRegistry.All", registryNames);
        Assert(
            catalogNames.ToHashSet(StringComparer.Ordinal)
                .SetEquals(registryNames),
            "The V1 command catalog and agent command registry diverged.");
        Assert(
            Preview261V1SuccessCommands.Count == 4 &&
            registryNames.Count(name =>
                !Preview261V1SuccessCommands.Contains(name) && name != "npc voice discover") == 137,
            "The pinned V1 outcome partition is not 4 successes, 1 discovery result, and 137 usage errors.");

        string workspaceRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"preview261-v1-command-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        string? priorWorkspaceRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        string[] before = SnapshotWorkspace(workspaceRoot);
        var journalFactoryCalls = 0;

        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                workspaceRoot);

            CliBoundaryResult unknown = await InvokeV1Async(
                "preview261 definitely-unknown",
                () => journalFactoryCalls++);
            Assert(
                unknown.ExitCode == (int)CommandExitCode.UsageError &&
                CombinedEvidence(unknown).Contains(
                    "Unknown command",
                    StringComparison.Ordinal),
                "The unknown-command negative control did not reach the outer fallback.");

            foreach (string commandName in registryNames.Order(StringComparer.Ordinal))
            {
                CliBoundaryResult result = await InvokeV1Async(
                    commandName,
                    () => journalFactoryCalls++);
                CommandExitCode expected = Preview261V1SuccessCommands.Contains(commandName)
                    ? CommandExitCode.Success
                    : commandName == "npc voice discover"
                        ? CommandExitCode.ValidationFailure
                        : CommandExitCode.UsageError;
                string evidence = CombinedEvidence(result);

                Assert(
                    result.ExitCode == (int)expected,
                    $"V1 command '{commandName}' returned {result.ExitCode}; expected {(int)expected} ({expected}). Evidence: {evidence}");
                Assert(
                    !string.IsNullOrWhiteSpace(result.StandardOutput) ||
                    !string.IsNullOrWhiteSpace(result.StandardError),
                    $"V1 command '{commandName}' returned no output or error evidence.");
                Assert(
                    !evidence.Contains("Unknown command", StringComparison.Ordinal) &&
                    !evidence.Contains(
                        "unavailable in this runner configuration",
                        StringComparison.Ordinal),
                    $"V1 command '{commandName}' reached a fallback route. Evidence: {evidence}");
                Assert(
                    !evidence.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase),
                    $"V1 command '{commandName}' reported an unhandled exception. Evidence: {evidence}");
            }

            Assert(
                journalFactoryCalls == 0,
                $"Protocol-1 verification constructed an operation journal {journalFactoryCalls} time(s).");
            string[] after = SnapshotWorkspace(workspaceRoot);
            Assert(
                before.SequenceEqual(after, StringComparer.Ordinal),
                "Protocol-1 command verification changed the fresh workspace. " +
                $"Before: [{string.Join(", ", before)}]; after: [{string.Join(", ", after)}].");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                priorWorkspaceRoot);
            if (Directory.Exists(workspaceRoot))
                Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static void RequireExactCommandSet(
        string authority,
        string[] names)
    {
        var unique = names.ToHashSet(StringComparer.Ordinal);
        Assert(
            names.Length == 142 &&
            unique.Count == 142 &&
            unique.SetEquals(Preview231CommandNames),
            $"{authority} is not the exact unique 142-command set.");
    }

    private static async Task<CliBoundaryResult> InvokeV1Async(
        string commandName,
        Action journalFactoryInvoked)
    {
        string[] args =
        [
            .. commandName.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            "--protocol",
            "1",
            "--json"
        ];
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;

        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            CommandExitCode exitCode = await NpcManager.Cli.Program.RunAsync(
                    args,
                    output,
                    error,
                    root =>
                    {
                        journalFactoryInvoked();
                        throw new InvalidOperationException(
                            $"Protocol 1 unexpectedly requested a journal for '{root.Value}'.");
                    },
                    cancellation.Token)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15));
            return new CliBoundaryResult(
                (int)exitCode,
                output.ToString(),
                error.ToString());
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"V1 command '{commandName}' exceeded its 15-second timeout.",
                exception);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"V1 command '{commandName}' threw {exception.GetType().Name}: {exception.Message}",
                exception);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static string CombinedEvidence(CliBoundaryResult result) =>
        $"stdout: {result.StandardOutput.Trim()} | stderr: {result.StandardError.Trim()}";

    private static string[] SnapshotWorkspace(string workspaceRoot) =>
        Directory.EnumerateFileSystemEntries(
                workspaceRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path =>
                $"{(Directory.Exists(path) ? "directory" : "file")}:{Path.GetRelativePath(workspaceRoot, path)}")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
