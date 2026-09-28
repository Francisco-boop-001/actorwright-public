using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class OutfitBinaryWriteCommandHandler(
    IOutfitBinaryWriteService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return WriteUsageError(command.Json, errorMessage);

        var result = await service.WriteAsync(request, cancellationToken);
        var response = new OutfitBinaryWriteResponse(result.Written, result.Output.Value,
            result.TargetFormId?.ToString(), result.OutputSha256?.Value, result.Diagnostics);
        Write(response, command.Json, result.Written ? "outfit write: PASS" : "outfit write: REFUSED");
        return ExitCodeForDiagnostics(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out OutfitBinaryWriteRequest request, out string errorMessage)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "outfit write requires --edition fallout4|skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("proposal", out var proposalValue) ||
            !command.Options.TryGetValue("output", out var outputValue))
        {
            errorMessage = "outfit write requires --proposal and --output.";
            return false;
        }
        try
        {
            request = new OutfitBinaryWriteRequest(edition,
                new WorkspacePath(proposalValue), new WorkspacePath(outputValue));
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static CommandExitCode ExitCodeForDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }



    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) :
            $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record OutfitBinaryWriteResponse(bool Written, string Output, string? TargetFormId,
        string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);
}
