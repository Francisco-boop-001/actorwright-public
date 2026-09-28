using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

public enum ProtocolSelection
{
    Legacy,
    Version2,
    Unsupported
}

public sealed record ProtocolValidationResult(
    bool Selected,
    AgentCommandContract? Contract,
    ImmutableArray<ProtocolDiagnostic> Diagnostics);

public static class ProtocolV2CommandLine
{
    public static ProtocolSelection Select(ParsedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RequestedProtocol is null ||
            string.Equals(command.RequestedProtocol, "1", StringComparison.Ordinal))
            return ProtocolSelection.Legacy;
        return string.Equals(command.RequestedProtocol, "2", StringComparison.Ordinal)
            ? ProtocolSelection.Version2
            : ProtocolSelection.Unsupported;
    }

    public static bool IsRequested(ParsedCommand command) =>
        Select(command) != ProtocolSelection.Legacy;

    public static ProtocolValidationResult Validate(ParsedCommand command) =>
        Validate(command, AgentCommandRegistry.All);

    internal static ProtocolValidationResult Validate(
        ParsedCommand command,
        ImmutableArray<AgentCommandContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(command);
        var selection = Select(command);
        if (selection == ProtocolSelection.Legacy)
            return new ProtocolValidationResult(false, null, []);

        if (selection == ProtocolSelection.Unsupported)
            return Failure(null, Diagnostic(
                ProtocolV2DiagnosticCodes.ProtocolUnsupported,
                $"Protocol '{command.RequestedProtocol}' is not supported."));

        if (!command.Json)
            return Failure(null, Diagnostic(
                ProtocolV2DiagnosticCodes.ProtocolJsonRequired,
                "Protocol 2 requires --json."));

        var contract = contracts.FirstOrDefault(item =>
            string.Equals(item.Name, command.Name, StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Contains(command.Name, StringComparer.OrdinalIgnoreCase));

        if (contract is null ||
            contract.Readiness != ProtocolReadiness.V2 && !command.Help)
            return Failure(contract, Diagnostic(
                ProtocolV2DiagnosticCodes.ProtocolCommandLegacy,
                $"Command '{command.Name}' is not ready for protocol 2."));

        // Scoped help is discovery only. It may describe a rich legacy
        // contract without making that command protocol-2 callable.
        if (command.Help)
            return new ProtocolValidationResult(true, contract, []);

        var diagnostics = ImmutableArray.CreateBuilder<ProtocolDiagnostic>();
        var commandWordCount = contract.Name.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries).Length;
        if (command.Positionals.Length > commandWordCount)
        {
            diagnostics.Add(Diagnostic(
                ProtocolV2DiagnosticCodes.PositionalUnexpected,
                $"Command '{contract.Name}' does not accept positional arguments."));
        }

        foreach (var duplicate in command.DuplicateOptions)
        {
            diagnostics.Add(Diagnostic(
                ProtocolV2DiagnosticCodes.OptionDuplicate,
                $"Option '--{duplicate}' may be supplied only once."));
        }

        var optionContracts = contract.Options.ToDictionary(
            item => item.CliName,
            StringComparer.OrdinalIgnoreCase);
        foreach (var option in command.Options)
        {
            if (!optionContracts.TryGetValue(option.Key, out var optionContract))
            {
                diagnostics.Add(Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionUnknown,
                    $"Option '--{option.Key}' is not declared for '{contract.Name}'."));
                continue;
            }

            if (command.ValuelessOptions.Contains(
                    option.Key,
                    StringComparer.OrdinalIgnoreCase) &&
                optionContract.ValueKind != AgentValueKind.Boolean)
            {
                diagnostics.Add(Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionValueRequired,
                    $"Option '--{option.Key}' requires a value."));
                continue;
            }

            ValidateValue(optionContract, option.Value, diagnostics);
        }

        foreach (var optionContract in contract.Options)
        {
            if (optionContract.Required &&
                !command.Options.ContainsKey(optionContract.CliName))
            {
                diagnostics.Add(Diagnostic(
                    ProtocolV2DiagnosticCodes.OptionRequired,
                    $"Required option '--{optionContract.CliName}' is missing."));
            }

            if (!command.Options.ContainsKey(optionContract.CliName))
                continue;

            foreach (var conflict in optionContract.ConflictsWith)
            {
                if (command.Options.ContainsKey(conflict))
                {
                    diagnostics.Add(Diagnostic(
                        ProtocolV2DiagnosticCodes.OptionConflict,
                        $"Option '--{optionContract.CliName}' conflicts with '--{conflict}'."));
                }
            }
        }

        return new ProtocolValidationResult(
            true,
            contract,
            diagnostics.ToImmutable());
    }

    private static void ValidateValue(
        AgentOptionContract option,
        string value,
        ImmutableArray<ProtocolDiagnostic>.Builder diagnostics)
    {
        if (option.ValueKind == AgentValueKind.Boolean &&
            !(option.AllowedValues.IsDefaultOrEmpty
                ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                : option.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Diagnostic(
                ProtocolV2DiagnosticCodes.OptionFlagValue,
                option.AllowedValues.IsDefaultOrEmpty
                    ? $"Flag '--{option.CliName}' accepts only 'true'."
                    : $"Flag '--{option.CliName}' accepts only: {string.Join(", ", option.AllowedValues)}."));
        }

        if (option.ValueKind == AgentValueKind.Enum &&
            !option.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic(
                ProtocolV2DiagnosticCodes.OptionEnumValue,
                $"Value '{value}' is not allowed for '--{option.CliName}'."));
        }

        if (option.ValueKind == AgentValueKind.EnumList &&
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(item => !option.AllowedValues.Contains(item, StringComparer.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Diagnostic(
                ProtocolV2DiagnosticCodes.OptionEnumValue,
                $"Value '{value}' contains an item that is not allowed for '--{option.CliName}'."));
        }

        if (option.ValueKind == AgentValueKind.Sha256 && !IsSha256(value))
        {
            diagnostics.Add(Diagnostic(
                ProtocolV2DiagnosticCodes.OptionSha256Value,
                $"Option '--{option.CliName}' requires a 64-character hexadecimal SHA-256 value."));
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    private static ProtocolValidationResult Failure(
        AgentCommandContract? contract,
        ProtocolDiagnostic diagnostic) =>
        new(true, contract, [diagnostic]);

    private static ProtocolDiagnostic Diagnostic(
        string code,
        string message) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            DiagnosticClass.Usage,
            new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                message,
                false));
}
