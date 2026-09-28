using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimNpcVoiceCommandHandler(ISkyrimNpcVoiceService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken token)
    {
        try
        {
            return command.Name switch
            {
                "npc voice discover" => await DiscoverAsync(command, token),
                "npc voice import" => await ImportAsync(command, token),
                "npc voice synthesize" => await SynthesizeAsync(command, token),
                _ => Usage(command, "Unsupported voice command.")
            };
        }
        catch (OperationCanceledException) { return Failure(command, SkyrimNpcVoiceDiagnosticCodes.Cancelled, "Voice work was cancelled.", CommandExitCode.Cancelled); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or JsonException)
        { return Failure(command, SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, exception.Message, CommandExitCode.ValidationFailure); }
    }

    private async ValueTask<CommandExitCode> DiscoverAsync(ParsedCommand command, CancellationToken token)
    {
        if (Validate(command, ["endpoint", "timeout-seconds", "output"], []) is { } invalid) return invalid;
        int timeout = 3;
        if (command.Options.TryGetValue("timeout-seconds", out string? value) &&
            (!int.TryParse(value, out timeout) || timeout is < 1 or > 30))
            return Usage(command, "--timeout-seconds must be an integer from 1 through 30.");
        WorkspacePath? path = command.Options.TryGetValue("output", out string? outputPath) ? new WorkspacePath(outputPath) : null;
        SkyrimVoiceServiceInventory result = await service.DiscoverAsync(command.Options.GetValueOrDefault("endpoint"), timeout, path, token);
        return Write(command, result, result.SelectedEndpoint is not null ? CommandExitCode.Success : DiagnosticExitCodeClassifier.ClassifyFailure(result.Diagnostics));
    }

    private async ValueTask<CommandExitCode> ImportAsync(ParsedCommand command, CancellationToken token)
    {
        string[] required = ["sample", "plugin", "form-id", "voice-prefix", "output"];
        if (Validate(command, [.. required, "editor-id"], required) is { } invalid) return invalid;
        if (!FormId.TryParse(command.Options["form-id"], out FormId formId)) return Usage(command, "npc voice import requires hexadecimal --form-id.");
        var request = new SkyrimVoiceSampleImportRequest(new WorkspacePath(command.Options["sample"]), new PluginName(command.Options["plugin"]), formId,
            command.Options.TryGetValue("editor-id", out string? editor) ? new EditorId(editor) : null, command.Options["voice-prefix"], new WorkspacePath(command.Options["output"]));
        SkyrimVoiceSampleImportResult result = await service.ImportSampleAsync(request, token);
        return Write(command, result, result.Imported ? CommandExitCode.Success : DiagnosticExitCodeClassifier.ClassifyFailure(result.Diagnostics));
    }

    private async ValueTask<CommandExitCode> SynthesizeAsync(ParsedCommand command, CancellationToken token)
    {
        string[] required = ["manifest", "manifest-sha256", "sample-authority", "sample-authority-sha256", "output"];
        if (Validate(command, [.. required, "endpoint", "language", "max-lines", "resume"], required) is { } invalid) return invalid;
        int? maxLines = null;
        if (command.Options.TryGetValue("max-lines", out string? maxText) && (!int.TryParse(maxText, out int max) || max <= 0)) return Usage(command, "--max-lines must be a positive integer.");
        else if (maxText is not null) maxLines = int.Parse(maxText, System.Globalization.CultureInfo.InvariantCulture);
        bool resume = false;
        if (command.Options.TryGetValue("resume", out string? resumeText) && !TryBoolean(resumeText, out resume))
            return Usage(command, "--resume must be true, false, or 1.");
        var request = new SkyrimVoiceSynthesisRequest(new WorkspacePath(command.Options["manifest"]), new Sha256Hash(command.Options["manifest-sha256"]),
            new WorkspacePath(command.Options["sample-authority"]), new Sha256Hash(command.Options["sample-authority-sha256"]), new WorkspacePath(command.Options["output"]),
            command.Options.GetValueOrDefault("endpoint"), command.Options.GetValueOrDefault("language"), maxLines, resume);
        SkyrimVoiceSynthesisResult result = await service.SynthesizeAsync(request, null, token);
        CommandExitCode code = result.Completed ? CommandExitCode.Success : result.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.Cancelled) ? CommandExitCode.Cancelled : DiagnosticExitCodeClassifier.ClassifyFailure(result.Diagnostics);
        return Write(command, result, code);
    }

    private CommandExitCode? Validate(ParsedCommand command, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> required)
    {
        if (command.DuplicateOptions.Length > 0) return Usage(command, "Voice options may not be repeated.");
        string? unknown = command.Options.Keys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null) return Usage(command, $"Voice commands do not support --{unknown}.");
        string? missing = required.FirstOrDefault(key => !command.Options.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) || value == "true");
        return missing is null ? null : Usage(command, $"Voice command requires --{missing}.");
    }
    private CommandExitCode Write(ParsedCommand command, object result, CommandExitCode code) { output.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); return code; }
    private CommandExitCode Usage(ParsedCommand command, string message) => Failure(command, "usage", message, CommandExitCode.UsageError);
    private CommandExitCode Failure(ParsedCommand command, string code, string message, CommandExitCode exit) { if (command.Json) output.WriteLine(JsonSerializer.Serialize(new { code, message }, JsonOptions)); else error.WriteLine($"{code}: {message}"); return exit; }
    private static bool TryBoolean(string value, out bool parsed)
    {
        if (value == "1") { parsed = true; return true; }
        return bool.TryParse(value, out parsed);
    }
}
