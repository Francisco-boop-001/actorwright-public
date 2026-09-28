using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RuntimeScriptVmadInspectCommandHandler(
    IRuntimeScriptVmadInspectService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition) ||
            !command.Options.TryGetValue("plugin", out var plugin) ||
            !command.Options.TryGetValue("npc", out var npcText) || !FormId.TryParse(npcText, out var npcFormId))
            return Usage(command.Json, "runtime-script inspect-vmad requires --game|--edition, --plugin, and --npc.");
        var script = command.Options.GetValueOrDefault("script") ??
            (edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE");
        try
        {
            var result = await service.InspectAsync(new RuntimeScriptVmadInspectRequest(
                edition, new WorkspacePath(plugin), npcFormId, script), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else output.WriteLine(result.Resolved ? "runtime-script inspect-vmad: PASS" : "runtime-script inspect-vmad: REFUSED");
            return result.Resolved && !result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
                ? CommandExitCode.Success
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
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
