using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PluginDeployCommandHandler(IPluginDeployService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        var input = command.Options.GetValueOrDefault("plugin") ?? command.Options.GetValueOrDefault("input-plugin");
        var dataRoot = command.Options.GetValueOrDefault("data-root");
        var expected = command.Options.GetValueOrDefault("expected-sha256") ?? command.Options.GetValueOrDefault("sha256");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition) ||
            input is null || dataRoot is null || expected is null)
            return Usage(command.Json,
                "plugin deploy requires --game|--edition, --plugin|--input-plugin, --data-root, and --expected-sha256.");

        try
        {
            var result = await service.DeployAsync(new PluginDeployRequest(edition,
                new WorkspacePath(input), new WorkspacePath(dataRoot), new Sha256Hash(expected)), cancellationToken);
            if (command.Json)
                output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else
                output.WriteLine(result.Deployed ? "plugin deploy: deployed" :
                    result.AlreadyPresent ? "plugin deploy: already present" : "plugin deploy: refused");

            return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
                ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
                : CommandExitCode.Success;
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Security(command.Json, exception.Message); }
        catch (IOException exception) { return Usage(command.Json, exception.Message); }
    }

    private CommandExitCode Usage(bool json, string message)
    {
        error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) :
            $"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private CommandExitCode Security(bool json, string message)
    {
        error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("security-refusal", message), JsonOptions) :
            $"ERROR security-refusal: {message}");
        return DiagnosticExitCodeClassifier.KnownSecurityRefusal;
    }

    private sealed record ErrorResponse(string Code, string Message);
}
