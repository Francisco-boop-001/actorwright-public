using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;

namespace NpcManager.Cli.Tests;

internal static partial class Program
{
    private static async Task TestAgentProtocolCompatibility()
    {
        var names = CommandCatalog.All.Select(item => item.Name).ToArray();
        Assert(names.Length == 142, "legacy command count changed");
        Assert(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 142,
            "legacy command names are not unique");

        var (helpRunner, helpOutput, helpError) = CreateRunner();
        var helpExit = await helpRunner.RunAsync(
            CommandLine.Parse(["--help"]), CancellationToken.None);
        var helpNames = helpOutput.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => names.Contains(line, StringComparer.Ordinal))
            .ToArray();
        Assert(helpExit == CommandExitCode.Success && helpError.ToString().Length == 0,
            "v1 help transport changed");
        Assert(helpNames.SequenceEqual(names, StringComparer.Ordinal),
            "help command order/content differs from CommandCatalog");

        var (v1Runner, v1Output, v1Error) = CreateRunner();
        var v1Exit = await v1Runner.RunAsync(
            CommandLine.Parse(["capabilities", "--json"]), CancellationToken.None);
        Assert(v1Exit == CommandExitCode.Success && v1Error.ToString().Length == 0,
            "v1 capabilities transport changed");
        using var json = JsonDocument.Parse(v1Output.ToString());
        Assert(json.RootElement.GetProperty("protocol").GetString() == "1",
            "v1 protocol version changed");
    }
}
