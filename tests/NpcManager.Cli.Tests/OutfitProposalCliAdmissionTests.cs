using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class Program
{
    private static async Task TestPreview251OutfitAdmission()
    {
        var fake = new CapturingOutfitProposalService();
        var (runner, output, error) = CreateRunner(
            outfitProposalService: fake);
        var validPlugin = @"K:\ExampleWorkspace\IronSource.esp";
        var validOutput = @"K:\ExampleWorkspace\iron.outfit-proposal.json";
        var validItems = "IronBoots.esp|0x00000101,IronArmor.esp|0x00000102";

        var barePlugin = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", "--source", "0x801", "--mode", "override",
            "--items", validItems, "--output", validOutput]));
        Assert(
            barePlugin.Exception is null &&
            barePlugin.Exit == CommandExitCode.UsageError &&
            fake.CallCount == 0 &&
            error.ToString().Contains("Option --plugin", StringComparison.Ordinal),
            "Bare --plugin did not return a typed Usage diagnostic without throwing.");

        error.GetStringBuilder().Clear();
        var malformedPlugin = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", "relative.esp", "--source", "0x801",
            "--mode", "override", "--items", validItems,
            "--output", validOutput]));
        Assert(
            malformedPlugin.Exception is null &&
            malformedPlugin.Exit == CommandExitCode.UsageError &&
            fake.CallCount == 0 &&
            error.ToString().Contains(
                "Option --plugin must be one valid workspace path.",
                StringComparison.Ordinal),
            "Malformed --plugin did not name the exact option in its typed Usage diagnostic.");

        error.GetStringBuilder().Clear();
        var malformedOutput = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", validPlugin, "--source", "0x801",
            "--mode", "override", "--items", validItems,
            "--output", "relative.outfit-proposal.json"]));
        Assert(
            malformedOutput.Exception is null &&
            malformedOutput.Exit == CommandExitCode.UsageError &&
            fake.CallCount == 0 &&
            error.ToString().Contains(
                "Option --output must be one valid workspace path.",
                StringComparison.Ordinal),
            "Malformed --output did not name the exact option in its typed Usage diagnostic.");

        error.GetStringBuilder().Clear();
        var whitespacePlugin = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", "   ", "--source", "0x801",
            "--mode", "override", "--items", validItems,
            "--output", validOutput]));
        Assert(
            whitespacePlugin.Exception is null &&
            whitespacePlugin.Exit == CommandExitCode.UsageError &&
            fake.CallCount == 0 &&
            error.ToString().Contains(
                "Option --plugin must be one valid workspace path.",
                StringComparison.Ordinal),
            "Whitespace --plugin did not return the exact typed Usage diagnostic.");

        error.GetStringBuilder().Clear();
        var whitespaceOutput = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", validPlugin, "--source", "0x801",
            "--mode", "override", "--items", validItems,
            "--output", " \t "]));
        Assert(
            whitespaceOutput.Exception is null &&
            whitespaceOutput.Exit == CommandExitCode.UsageError &&
            fake.CallCount == 0 &&
            error.ToString().Contains(
                "Option --output must be one valid workspace path.",
                StringComparison.Ordinal),
            "Whitespace --output did not return the exact typed Usage diagnostic.");

        error.GetStringBuilder().Clear();
        var overlongPlugin = @"K:\" + new string('a', 33_000) + ".esp";
        var overlong = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", overlongPlugin, "--source", "0x801",
            "--mode", "override", "--items", validItems,
            "--output", validOutput]));
        Assert(
            overlong.Exception is null &&
            overlong.Exit == CommandExitCode.UsageError &&
            fake.CallCount == 0 &&
            error.ToString().Contains(
                "Option --plugin must be one valid workspace path.",
                StringComparison.Ordinal),
            $"Overlong --plugin did not return typed Usage without escaping; exception={overlong.Exception?.GetType().FullName ?? "<none>"}.");

        error.GetStringBuilder().Clear();
        output.GetStringBuilder().Clear();
        var valid = await InvokeWithoutThrow(runner, CommandLine.Parse([
            "outfit", "propose", "--edition", "fallout4",
            "--plugin", validPlugin, "--source", "0x801",
            "--mode", "override", "--items", validItems,
            "--output", validOutput]));
        Assert(
            valid.Exception is null && valid.Exit == CommandExitCode.Success &&
            fake.CallCount == 1 &&
            fake.Request?.Items.Select(item => item.ToString()).SequenceEqual([
                "IronBoots.esp|0x00000101", "IronArmor.esp|0x00000102"]) == true,
            "Comma-separated outfit items did not reach the fake service in order.");

        output.GetStringBuilder().Clear();
        var help = await runner.RunAsync(
            CommandLine.Parse(["help"]), CancellationToken.None);
        Assert(
            help == CommandExitCode.Success &&
            output.ToString().Contains(
                "JSON array or comma-separated list", StringComparison.Ordinal),
            "CLI help did not publish the admitted JSON array or comma-separated list syntax.");

        var contract = FindRepositoryFile("docs", "cli-contract.md");
        var contractText = await File.ReadAllTextAsync(contract);
        Assert(
            contractText.Contains(
                "JSON array or comma-separated list", StringComparison.Ordinal),
            "CLI contract did not publish the admitted JSON array or comma-separated list syntax.");
    }

    private static async Task<(CommandExitCode? Exit, Exception? Exception)> InvokeWithoutThrow(
        CliRunner runner, ParsedCommand command)
    {
        try
        {
            return (await runner.RunAsync(command, CancellationToken.None), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repository file '{Path.Combine(relativePath)}'.");
    }

    private sealed class CapturingOutfitProposalService : IOutfitProposalService
    {
        public OutfitProposalRequest? Request { get; private set; }
        public int CallCount { get; private set; }

        public ValueTask<OutfitProposalResult> ProposeAsync(
            OutfitProposalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Request = request;
            return ValueTask.FromResult(new OutfitProposalResult(
                false, null, null, []));
        }
    }
}
