using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class Program
{
    private static readonly ImmutableHashSet<string> Preview267AnywhereCommands =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "capabilities", "version", "diagnose");

    private static async Task TestPreview267NonKWorkspaceRefusal()
    {
        string nonKRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            $"actorwright-nonk-{Guid.NewGuid():N}"));
        Assert(!string.Equals(Path.GetPathRoot(nonKRoot), @"K:\", StringComparison.OrdinalIgnoreCase),
            "Test host TEMP is on K:; pick a non-K temp root.");
        Directory.CreateDirectory(nonKRoot);
        string? prior = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable, nonKRoot);
            foreach (string name in AgentCommandRegistry.All
                         .Select(command => command.Name).Order(StringComparer.Ordinal))
            {
                string[] command = name.Split(' ');
                await AssertNonKCommandAsync([.. command, "--json"],
                    Preview267AnywhereCommands.Contains(name));
                await AssertNonKCommandAsync([.. command, "--help", "--json"], true);
            }

            await AssertNonKCommandAsync(["help"], true);
            await AssertNonKCommandAsync(["--help"], true);
            await AssertNonKCommandAsync([], true);
            await AssertNonKCommandAsync(["npc", "list"], false);
            await AssertNonKCommandAsync(["npc", "list", "--protocol", "1", "--json"], false);
            await AssertNonKCommandAsync(["schema", "export", "--command", "npc list", "--json"], false);
            await AssertNonKCommandAsync(["schema", "export", "--protocol", "2", "--command", "npc list", "--json"], true);
            await AssertNonKCommandAsync(["schema", "export", "--output",
                Path.Combine(nonKRoot, "schema.json"), "--json"], false);
            Assert(!Directory.EnumerateFileSystemEntries(nonKRoot).Any(),
                "Non-K discovery/refusal created workspace output.");

            await TestMalformedProtectedRootConfiguration();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable, prior);
            Directory.Delete(nonKRoot, recursive: false);
        }
        Console.WriteLine("PASS preview.267 non-K workspace refusal");
    }

    private static async Task TestMalformedProtectedRootConfiguration()
    {
        string? priorWorkspace = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        string? priorProtectedRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.ProtectedRootEnvironmentVariable);
        string workspaceRoot = Path.GetFullPath(Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "reassessment-work",
            $"protected-root-cli-{Guid.NewGuid():N}"));
        var admittedWorkspaceRoot = new WorkspacePath(workspaceRoot);
        Assert(string.Equals(
                Path.GetPathRoot(workspaceRoot),
                @"K:\",
                StringComparison.OrdinalIgnoreCase),
            "Protected-root journal test workspace must remain on K:.");
        Directory.CreateDirectory(workspaceRoot);
        const string invalidProtectedRoot = "K:drive-relative";
        const string expectedMessage =
            "ACTORWRIGHT_PROTECTED_ROOT must not use a drive-relative or root-relative path.";
        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                workspaceRoot);
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                invalidProtectedRoot);

            using var legacyJsonOutput = new StringWriter();
            using var legacyJsonError = new StringWriter();
            CommandExitCode legacyJsonExit = await NpcManager.Cli.Program.RunAsync(
                ["npc", "list", "--json"],
                legacyJsonOutput,
                legacyJsonError,
                _ => throw new InvalidOperationException(
                    "A legacy usage refusal must not create an operation journal."),
                CancellationToken.None);
            using JsonDocument legacyJson = JsonDocument.Parse(
                legacyJsonError.ToString());
            Assert(legacyJsonExit == CommandExitCode.UsageError &&
                   legacyJson.RootElement.GetProperty("code").GetString() ==
                       "usage-error" &&
                   legacyJson.RootElement.GetProperty("message").GetString() ==
                       expectedMessage &&
                   legacyJsonOutput.ToString().Length == 0,
                "Malformed protected-root config did not return the legacy JSON usage error.");

            using var legacyTextOutput = new StringWriter();
            using var legacyTextError = new StringWriter();
            CommandExitCode legacyTextExit = await NpcManager.Cli.Program.RunAsync(
                ["npc", "list"],
                legacyTextOutput,
                legacyTextError,
                _ => throw new InvalidOperationException(
                    "A legacy usage refusal must not create an operation journal."),
                CancellationToken.None);
            Assert(legacyTextExit == CommandExitCode.UsageError &&
                   legacyTextError.ToString() ==
                       $"ERROR usage-error: {expectedMessage}{Environment.NewLine}" &&
                   legacyTextOutput.ToString().Length == 0,
                "Malformed protected-root config did not return the legacy text usage error.");

            await AssertProtocolProtectedRootError(
                ["workspace", "preflight", "--protocol", "2", "--json",
                    "--game", "skyrimse",
                    "--workspace-root", workspaceRoot,
                    "--data-root", Path.Combine(workspaceRoot, "data"),
                    "--output-root", Path.Combine(workspaceRoot, "output"),
                    "--load-order", Path.Combine(workspaceRoot, "loadorder.txt"),
                    "--intake-output", Path.Combine(workspaceRoot, "intake.json"),
                    "--npc-editor-id", "ProtectedRootTest",
                    "--workflow-output", Path.Combine(workspaceRoot, "workflow.json")],
                "unsafe-path-form",
                workspaceRoot);
            await AssertProtocolProtectedRootError(
                ["schema", "export", "--protocol", "2", "--json",
                    "--output", Path.Combine(workspaceRoot, "schema.json"),
                    "--command", "workspace preflight"],
                "unsafe-path-form",
                workspaceRoot);

            await AssertProtectedJournalWriteRefused(
                workspaceRoot,
                workspaceRoot);
            await AssertProtectedJournalWriteRefused(
                workspaceRoot,
                Path.Combine(
                    workspaceRoot, ".actorwright", "operations", "protected"));

            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                invalidProtectedRoot);
            foreach (string[] discovery in new[]
                     {
                         new[] { "capabilities", "--protocol", "2", "--json" },
                         new[] { "npc", "list", "--protocol", "2", "--help", "--json" },
                         new[] { "schema", "export", "--protocol", "2", "--json",
                             "--command", "npc list" }
                     })
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                CommandExitCode exit = await NpcManager.Cli.Program.RunAsync(
                    discovery,
                    output,
                    error,
                    _ => new NoopJournal(),
                    CancellationToken.None);
                Assert(exit == CommandExitCode.Success &&
                       output.ToString().Length > 0 &&
                       error.ToString().Length == 0,
                    $"P2 discovery '{string.Join(' ', discovery)}' resolved malformed protected-root config.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                priorWorkspace);
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                priorProtectedRoot);
            if (Directory.Exists(workspaceRoot) &&
                admittedWorkspaceRoot.IsUnder(new WorkspacePath(
                    Path.Combine(
                        Environment.CurrentDirectory,
                        "artifacts",
                        "reassessment-work"))))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    private static async Task AssertProtocolProtectedRootError(
        string[] args,
        string expectedCode,
        string workspaceRoot)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        CommandExitCode exit = await NpcManager.Cli.Program.RunAsync(
            args,
            output,
            error,
            root => new LocalOperationJournal(root),
            CancellationToken.None);
        using JsonDocument response = JsonDocument.Parse(output.ToString());
        JsonElement diagnostics = response.RootElement.GetProperty("diagnostics");
        bool hasExpectedDiagnostic = diagnostics.EnumerateArray().Any(item =>
            item.GetProperty("code").GetString() == expectedCode &&
            item.GetProperty("message").GetString()?.Contains(
                ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
                StringComparison.Ordinal) == true &&
            !item.GetProperty("message").GetString()!.Contains(
                "K:drive-relative",
                StringComparison.Ordinal));
        bool hasJournalWarning = diagnostics.EnumerateArray().Any(item =>
            item.GetProperty("code").GetString() ==
                "operation-journal-write-failed");
        bool journalTreeAbsent = !Directory.Exists(Path.Combine(
            workspaceRoot, ".actorwright"));
        Assert(exit == CommandExitCode.SecurityRefusal &&
               hasExpectedDiagnostic && hasJournalWarning &&
               journalTreeAbsent && error.ToString().Length == 0,
            $"P2 protected-root refusal or journal boundary failed for '{string.Join(' ', args)}'; " +
            $"exit={exit}, diagnostics={diagnostics.GetRawText()}, " +
            $"journal-tree-absent={journalTreeAbsent}, stderr='{error}'.");
    }

    private static async Task AssertProtectedJournalWriteRefused(
        string workspaceRoot,
        string protectedRoot)
    {
        Environment.SetEnvironmentVariable(
            ActorwrightWorkspace.ProtectedRootEnvironmentVariable,
            protectedRoot);
        using var output = new StringWriter();
        using var error = new StringWriter();
        CommandExitCode exit = await NpcManager.Cli.Program.RunAsync(
            ["capabilities", "--protocol", "2", "--json"],
            output,
            error,
            root => new LocalOperationJournal(root),
            CancellationToken.None);
        using JsonDocument response = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success &&
               response.RootElement.GetProperty("diagnostics")
                   .EnumerateArray().Any(item =>
                       item.GetProperty("code").GetString() ==
                           "operation-journal-write-failed") &&
               !Directory.Exists(Path.Combine(workspaceRoot, ".actorwright")) &&
               error.ToString().Length == 0,
            "The P2 journal wrote where the configured protected root overlaps it.");
    }

    private sealed class NoopJournal : ILocalOperationJournal
    {
        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
    }

    private static async Task AssertNonKCommandAsync(string[] args, bool success)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        CommandExitCode exit;
        try
        {
            exit = await NpcManager.Cli.Program.RunAsync(args, output, error,
                _ => throw new InvalidOperationException("No journal on protocol 1."),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"'{string.Join(' ', args)}' escaped RunAsync with {exception.GetType().Name}: {exception.Message}",
                exception);
        }

        Assert(exit == (success ? CommandExitCode.Success : CommandExitCode.SecurityRefusal),
            $"'{string.Join(' ', args)}' returned {exit}; stdout: {output}; stderr: {error}");
        if (success)
        {
            Assert(output.ToString().Length > 0, "Discovery returned no output.");
            if (args is ["schema", "export", ..] && !args.Contains("--help", StringComparer.Ordinal))
            {
                using var schema = JsonDocument.Parse(output.ToString());
                Assert(args.Contains("--protocol", StringComparer.Ordinal) &&
                       schema.RootElement.GetProperty("result").GetProperty("contract")
                           .GetProperty("name").GetString() == "npc list",
                    "Protocol-2 inline schema did not honor --command.");
            }
            return;
        }

        string root = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable)!;
        string message = $"The workspace root '{root}' is not on drive K:. " +
            "Set ACTORWRIGHT_WORKSPACE_ROOT to a K:\\ path or run from a K:\\ directory.";
        if (args.Contains("--json", StringComparer.Ordinal))
        {
            string expected = string.Join(Environment.NewLine,
                "{",
                "  \"code\": \"workspace-root-not-k-local\",",
                $"  \"message\": {JsonSerializer.Serialize(message)}",
                "}") + Environment.NewLine;
            Assert(output.ToString() == expected && error.ToString().Length == 0,
                "Non-K JSON refusal bytes changed.");
        }
        else
        {
            string expected = $"ERROR workspace-root-not-k-local: {message}{Environment.NewLine}";
            Assert(error.ToString() == expected && output.ToString().Length == 0,
                "Non-K text refusal bytes changed.");
        }
    }
}
