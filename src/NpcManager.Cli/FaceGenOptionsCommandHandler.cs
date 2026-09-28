using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenOptionsCommandHandler(IFaceGenOptionsService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);
        var result = await service.ValidateAsync(request, cancellationToken);
        var response = new FaceGenOptionsResponse("1", result.IsValid, result.Applied,
            result.Edition.ToWireName(), result.Input.Value, result.Output?.Value,
            result.InputSha256?.Value, result.OutputSha256?.Value, result.Options, result.Diagnostics);
        Write(response, command.Json, result.Applied ? "facegen options: APPLIED" :
            result.IsValid ? "facegen options: PASS" : "facegen options: REFUSED");
        return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? CommandExitCodeFor(result.Diagnostics) : CommandExitCode.Success;
    }

    private static bool TryBuildRequest(ParsedCommand command, out FaceGenOptionsRequest request,
        out string errorMessage)
    {
        request = default!;
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition))
        {
            errorMessage = "facegen options requires --edition|--game fallout4|skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("input", out var inputText) &&
            !command.Options.TryGetValue("options", out inputText))
        {
            errorMessage = "facegen options requires --input|--options <json>.";
            return false;
        }
        try
        {
            var input = new WorkspacePath(inputText);
            WorkspacePath? output = null;
            if (command.Options.TryGetValue("output", out var outputText)) output = new WorkspacePath(outputText);
            Sha256Hash? expected = null;
            if (command.Options.TryGetValue("expected-sha256", out var expectedText)) expected = new Sha256Hash(expectedText);
            var apply = command.Options.ContainsKey("apply");
            if (apply && command.Options.ContainsKey("dry-run"))
            {
                errorMessage = "--apply and --dry-run cannot be used together.";
                return false;
            }
            request = new FaceGenOptionsRequest(edition, input, output, expected, apply);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private static CommandExitCode CommandExitCodeFor(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticExitCodeClassifier.Classify(diagnostics);

    private sealed record FaceGenOptionsResponse(
        string SchemaVersion,
        bool IsValid,
        bool Applied,
        string Game,
        string Input,
        string? Output,
        string? InputSha256,
        string? OutputSha256,
        CharGenOptions? Options,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(ImmutableArray<Diagnostic> Diagnostics);
}
