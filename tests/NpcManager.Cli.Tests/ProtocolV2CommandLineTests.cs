using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Cli;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2CommandLineTests
{
    public static async Task RunAsync()
    {
        var legacy = CommandLine.Parse(["capabilities", "--json"]);
        Require(legacy.RequestedProtocol is null && legacy.Correlation is null,
            "v1 transport metadata defaults changed");
        Require(legacy.Name == "capabilities" && legacy.Json && !legacy.Help &&
                legacy.Positionals.SequenceEqual(["capabilities"]) &&
                legacy.Options.Count == 0 && legacy.DuplicateOptions.Length == 0,
            "v1 parsed fields changed");

        var selected = CommandLine.Parse(
            ["capabilities", "--protocol", "2", "--correlation", "request-7", "--json"]);
        Require(selected.RequestedProtocol == "2" &&
                selected.Correlation == "request-7",
            "global transport metadata was not parsed");
        Require(!selected.Options.ContainsKey("protocol") &&
                !selected.Options.ContainsKey("correlation"),
            "global transport options leaked into command options");
        Require(ProtocolV2CommandLine.Select(selected) == ProtocolSelection.Version2 &&
                ProtocolV2CommandLine.IsRequested(selected),
            "explicit protocol 2 was not selected");
        Require(ProtocolV2CommandLine.Select(legacy) == ProtocolSelection.Legacy &&
                !ProtocolV2CommandLine.IsRequested(legacy),
            "no-option invocation stopped selecting protocol v1");

        var explicitLegacy = CommandLine.Parse(
            ["version", "--protocol", "1", "--json"]);
        Require(explicitLegacy.RequestedProtocol == "1" &&
                !explicitLegacy.Options.ContainsKey("protocol") &&
                ProtocolV2CommandLine.Select(explicitLegacy) ==
                    ProtocolSelection.Legacy &&
                !ProtocolV2CommandLine.IsRequested(explicitLegacy),
            "explicit protocol 1 did not select legacy without leaking transport metadata");

        var legacyCalls = 0;
        var legacyError = new StringWriter();
        var legacyExit = await NpcManager.Cli.Program.DispatchAsync(
            legacy,
            _ =>
            {
                legacyCalls++;
                return ValueTask.FromResult(CommandExitCode.Success);
            },
            legacyError);
        Require(legacyExit == CommandExitCode.Success && legacyCalls == 1 &&
                legacyError.ToString().Length == 0,
            "unselected protocol v1 did not use the legacy dispatcher exactly once");

        var bareV1 = CommandLine.Parse(["capabilities", "--dry-run"]);
        var bareV1Calls = 0;
        var bareV1Error = new StringWriter();
        var bareV1Exit = await NpcManager.Cli.Program.DispatchAsync(
            bareV1,
            _ =>
            {
                bareV1Calls++;
                return ValueTask.FromResult(CommandExitCode.Success);
            },
            bareV1Error);
        Require(bareV1.Options["dry-run"] == "true" &&
                bareV1Exit == CommandExitCode.Success &&
                bareV1Calls == 1 && bareV1Error.ToString().Length == 0,
            "unselected protocol v1 bare option did not retain legacy parsing and dispatch");

        var v2Capabilities = Ready("capabilities", []);
        var v2SchemaExport = Ready(
            "schema export",
            [Option("command", AgentValueKind.String)]);

        var bareBoolean = CommandLine.Parse(
            ["capabilities", "--protocol", "2", "--json", "--dry-run"]);
        var bareBooleanLegacyCalls = 0;
        var bareBooleanError = new StringWriter();
        var bareBooleanExit = await NpcManager.Cli.Program.DispatchAsync(
            bareBoolean,
            _ =>
            {
                bareBooleanLegacyCalls++;
                return ValueTask.FromResult(CommandExitCode.Success);
            },
            bareBooleanError,
            [Ready("capabilities", [Option("dry-run", AgentValueKind.Boolean)])]);
        Require(bareBooleanExit == CommandExitCode.Success &&
                bareBooleanError.ToString().Length == 0 &&
                bareBooleanLegacyCalls == 0,
            "bare protocol v2 Boolean option was not accepted before legacy dispatch");

        await AssertInvalid(
            ["capabilities", "--protocol", "3", "--json"],
            "protocol-unsupported", LegacyDispatcher);
        await AssertInvalid(
            ["capabilities", "--protocol", "2"],
            "protocol-json-required", LegacyDispatcher,
            [v2Capabilities]);
        await AssertInvalid(
            ["capabilities", "--protocol", "2", "--json", "--bogus"],
            "option-unknown", LegacyDispatcher,
            [v2Capabilities]);
        await AssertInvalid(
            ["capabilities", "extra", "--protocol", "2", "--json"],
            "positional-unexpected", LegacyDispatcher,
            [v2Capabilities]);
        await AssertInvalid(
            ["schema", "export", "extra", "--protocol", "2", "--json"],
            "positional-unexpected", LegacyDispatcher,
            [v2SchemaExport]);
        await AssertInvalid(
            ["schema", "export", "--protocol", "2", "--json", "--command", "version", "--command", "capabilities"],
            "option-duplicate", LegacyDispatcher,
            [v2SchemaExport]);
        await AssertInvalid(
            ["schema", "export", "--protocol", "2", "--json", "--command"],
            "option-value-required", LegacyDispatcher,
            [v2SchemaExport]);
        await AssertLegacyRefusal(
            ["npc", "create", "--protocol", "2", "--json"],
            "protocol-command-legacy", LegacyDispatcher);

        await AssertInvalid(
            ["capabilities", "--protocol", "2", "--json", "--dry-run", "false"],
            "option-flag-value", LegacyDispatcher,
            [Ready("capabilities", [Option("dry-run", AgentValueKind.Boolean)])]);
        await AssertInvalid(
            ["capabilities", "--protocol", "2", "--json"],
            "option-required", LegacyDispatcher,
            [Ready("capabilities", [Option("input", AgentValueKind.String, required: true)])]);
        await AssertInvalid(
            ["capabilities", "--protocol", "2", "--json", "--game", "oblivion"],
            "option-enum-value", LegacyDispatcher,
            [Ready("capabilities", [Option("game", AgentValueKind.Enum, allowedValues: ["fallout4", "skyrimse"])])]);
        await AssertInvalid(
            ["capabilities", "--protocol", "2", "--json", "--sha", "abc"],
            "option-sha256-value", LegacyDispatcher,
            [Ready("capabilities", [Option("sha", AgentValueKind.Sha256)])]);
        await AssertInvalid(
            ["capabilities", "--protocol", "2", "--json", "--left", "a", "--right", "b"],
            "option-conflict", LegacyDispatcher,
            [Ready("capabilities",
            [
                Option("left", AgentValueKind.String, conflictsWith: ["right"]),
                Option("right", AgentValueKind.String)
            ])]);

        var valid = ProtocolV2CommandLine.Validate(selected, [v2Capabilities]);
        Require(valid.Selected && valid.Contract == v2Capabilities &&
                valid.Diagnostics.Length == 0,
            "valid explicit protocol v2 invocation was rejected");
    }

    private static async Task AssertInvalid(
        string[] args,
        string diagnosticCode,
        Func<ParsedCommand, ValueTask<CommandExitCode>> legacyDispatcher,
        ImmutableArray<AgentCommandContract>? contracts = null)
    {
        var legacyCalls = 0;
        ValueTask<CommandExitCode> CountingDispatcher(ParsedCommand command)
        {
            legacyCalls++;
            return legacyDispatcher(command);
        }

        var command = CommandLine.Parse(args);
        var error = new StringWriter();
        var exit = await NpcManager.Cli.Program.DispatchAsync(
            command,
            CountingDispatcher,
            error,
            contracts);

        Require(exit == CommandExitCode.UsageError &&
                error.ToString().Contains(diagnosticCode, StringComparison.Ordinal),
            $"production dispatch did not refuse with {diagnosticCode}");
        Require(legacyCalls == 0,
            $"legacy dispatcher ran for invalid protocol invocation: {diagnosticCode}");
    }

    private static Task AssertLegacyRefusal(
        string[] args,
        string diagnosticCode,
        Func<ParsedCommand, ValueTask<CommandExitCode>> legacyDispatcher) =>
        AssertInvalid(args, diagnosticCode, legacyDispatcher);

    private static ValueTask<CommandExitCode> LegacyDispatcher(
        ParsedCommand command) =>
        ValueTask.FromResult(CommandExitCode.Success);

    private static AgentCommandContract Ready(
        string name,
        ImmutableArray<AgentOptionContract> options) =>
        AgentCommandRegistry.GetRequired(name) with
        {
            Readiness = ProtocolReadiness.V2,
            Options = options
        };

    private static AgentOptionContract Option(
        string name,
        AgentValueKind kind,
        bool required = false,
        ImmutableArray<string> allowedValues = default,
        ImmutableArray<string> conflictsWith = default) =>
        new(
            name,
            name,
            kind,
            required,
            allowedValues.IsDefault ? [] : allowedValues,
            conflictsWith.IsDefault ? [] : conflictsWith,
            false);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
