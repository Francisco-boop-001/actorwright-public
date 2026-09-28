using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RuntimeScriptBuildCommandHandler(IRuntimeScriptBuildService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition) ||
            !command.Options.TryGetValue("source-root", out var source) || !command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "runtime-script build requires --game|--edition, --source-root, and --output.");
        try
        {
            var result = await service.BuildAsync(new RuntimeScriptBuildRequest(edition, new WorkspacePath(source), new WorkspacePath(outputPath)), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); else output.WriteLine(result.Written ? "runtime-script build: evidence written" : "runtime-script build: refused");
            return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics) : CommandExitCode.Success;
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Security(command.Json, exception.Message); }
        catch (IOException exception) { return Usage(command.Json, exception.Message); }
    }

    private CommandExitCode Usage(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private CommandExitCode Security(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("security-refusal", message), JsonOptions) : $"ERROR security-refusal: {message}"); return DiagnosticExitCodeClassifier.KnownSecurityRefusal; }
    private sealed record ErrorResponse(string Code, string Message);
}
