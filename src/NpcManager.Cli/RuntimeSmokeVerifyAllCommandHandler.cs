using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RuntimeSmokeVerifyAllCommandHandler(
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
        if (!command.Options.TryGetValue("fallout4-report", out var fallout4Report) ||
            !command.Options.TryGetValue("skyrimse-report", out var skyrimSeReport) ||
            !command.Options.TryGetValue("package-acceptance", out var packageAcceptance))
        {
            return Usage(command.Json,
                "runtime smoke verify-all requires --fallout4-report, --skyrimse-report, and --package-acceptance.");
        }

        try
        {
            var fallout4 = await service.VerifyAsync(
                new RuntimeSmokeVerifyRequest(
                    GameEdition.Fallout4,
                    new WorkspacePath(fallout4Report),
                    new WorkspacePath(packageAcceptance)),
                cancellationToken);
            var skyrimSe = await service.VerifyAsync(
                new RuntimeSmokeVerifyRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(skyrimSeReport),
                    new WorkspacePath(packageAcceptance)),
                cancellationToken);
            var result = new RuntimeSmokeVerifyAllResult(
                fallout4.IsValid && skyrimSe.IsValid,
                fallout4,
                skyrimSe,
                fallout4.Diagnostics.AddRange(skyrimSe.Diagnostics));

            if (command.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }
            else
            {
                output.WriteLine($"runtime smoke verify-all: {(result.IsValid ? "PASS" : "FAIL")}");
                output.WriteLine($"  fallout4: {(fallout4.IsValid ? "PASS" : "FAIL")} screenshots={fallout4.ValidatedScreenshots.Length}");
                output.WriteLine($"  skyrimse: {(skyrimSe.IsValid ? "PASS" : "FAIL")} screenshots={skyrimSe.ValidatedScreenshots.Length}");
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
