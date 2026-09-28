using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RuntimeSmokeVerifyCommandHandler(
    IRuntimeSmokeVerifyService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition) ||
            !command.Options.TryGetValue("runtime-report", out var runtimeReport) ||
            !command.Options.TryGetValue("package-acceptance", out var packageAcceptance))
        {
            return Usage(command.Json,
                "runtime smoke verify requires --edition|--game, --runtime-report, and --package-acceptance.");
        }

        try
        {
            var result = await service.VerifyAsync(
                new RuntimeSmokeVerifyRequest(
                    edition,
                    new WorkspacePath(runtimeReport),
                    new WorkspacePath(packageAcceptance)),
                cancellationToken);
            if (command.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                output.WriteLine($"runtime smoke verify: {(result.IsValid ? "PASS" : "FAIL")} " +
                    $"edition={result.Edition} screenshots={result.ValidatedScreenshots.Length}");
                foreach (var diagnostic in result.Diagnostics)
                    output.WriteLine($"  {diagnostic.Severity}: {diagnostic.Code} - {diagnostic.Message}");
            }

            if (result.IsValid) return CommandExitCode.Success;
            return DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return Usage(command.Json, exception.Message);
        }
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
}
