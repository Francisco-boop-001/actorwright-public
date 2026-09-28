using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimNpcDialogueCommandHandler(ISkyrimNpcDialogueService? service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken token)
    {
        try
        {
            return command.Name switch
            {
                "npc dialogue analyze" => await AnalyzeAsync(command, token),
                "npc dialogue apply" => await ApplyAsync(command, token),
                "npc dialogue verify" => await VerifyAsync(command, token),
                _ => Usage(command, "Unsupported dialogue command.")
            };
        }
        catch (OperationCanceledException) { return Failure(command, SkyrimNpcVoiceDiagnosticCodes.Cancelled, "Dialogue work was cancelled.", CommandExitCode.Cancelled); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or JsonException)
        { return Failure(command, SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, exception.Message, CommandExitCode.ValidationFailure); }
    }

    private async ValueTask<CommandExitCode> AnalyzeAsync(ParsedCommand command, CancellationToken token)
    {
        string[] template = ["template", "profile", "manifest-output", "npc-plugin", "form-id", "voice-prefix"];
        string[] normal = ["manifest", "manifest-sha256", "plugin", "plugin-sha256", "data-root", "plugins", "sample-authority", "sample-authority-sha256", "output"];
        string[] allowed = [.. template, .. normal, "female", "editor-id", "language"];
        if (ValidateAllowed(command, allowed) is { } invalid) return invalid;
        if (command.Options.TryGetValue("template", out string? templateName))
        {
            if (Missing(command, template) is { } missing) return Usage(command, $"Template mode requires --{missing}.");
            if (!command.Options.TryGetValue("female", out string? female)) return Usage(command, "Template mode requires --female true, false, or 1.");
            if (!TryBoolean(female, out bool isFemale)) return Usage(command, "--female must be true, false, or 1.");
            if (normal.Any(command.Options.ContainsKey)) return Usage(command, "Template mode cannot be combined with normal analyze options.");
            if (!FormId.TryParse(command.Options["form-id"], out FormId formId)) return Usage(command, "Template mode requires hexadecimal --form-id.");
            if (service is null) return Usage(command, "npc dialogue requires its application service.");
            var request = new SkyrimDialogueTemplateRequest(templateName, new WorkspacePath(command.Options["profile"]),
                new SkyrimDialogueNpcIdentity(new PluginName(command.Options["npc-plugin"]), formId, command.Options.TryGetValue("editor-id", out string? editor) ? new EditorId(editor) : null,
                    command.Options["voice-prefix"], isFemale), command.Options.GetValueOrDefault("language") ?? "en", new WorkspacePath(command.Options["manifest-output"]));
            return Stage(command, await service.CreateTemplateAsync(request, token));
        }
        if (Missing(command, normal) is { } normalMissing) return Usage(command, $"Analyze mode requires --{normalMissing}.");
        if (template.Concat(["female", "editor-id", "language"]).Any(command.Options.ContainsKey)) return Usage(command, "Normal analyze mode cannot be combined with template options.");
        if (service is null) return Usage(command, "npc dialogue requires its application service.");
        var analyze = new SkyrimDialogueAnalyzeRequest(new WorkspacePath(command.Options["manifest"]), new Sha256Hash(command.Options["manifest-sha256"]),
            new WorkspacePath(command.Options["plugin"]), new Sha256Hash(command.Options["plugin-sha256"]), new WorkspacePath(command.Options["data-root"]),
            command.Options["plugins"].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => new PluginName(x)).ToImmutableArray(),
            new WorkspacePath(command.Options["sample-authority"]), new Sha256Hash(command.Options["sample-authority-sha256"]), new WorkspacePath(command.Options["output"]));
        return Stage(command, await service.AnalyzeAsync(analyze, token));
    }

    private async ValueTask<CommandExitCode> ApplyAsync(ParsedCommand command, CancellationToken token)
    {
        string[] required = ["proposal", "proposal-sha256", "synthesis", "synthesis-sha256", "output"];
        if (Validate(command, [.. required, "lip-tools"], required) is { } invalid) return invalid;
        if (service is null) return Usage(command, "npc dialogue requires its application service.");
        var request = new SkyrimDialogueApplyRequest(new WorkspacePath(command.Options["proposal"]), new Sha256Hash(command.Options["proposal-sha256"]),
            new WorkspacePath(command.Options["synthesis"]), new Sha256Hash(command.Options["synthesis-sha256"]), new WorkspacePath(command.Options["output"]),
            command.Options.TryGetValue("lip-tools", out string? lip) ? new WorkspacePath(lip) : null);
        return Stage(command, await service.ApplyAsync(request, null, token));
    }

    private async ValueTask<CommandExitCode> VerifyAsync(ParsedCommand command, CancellationToken token)
    {
        string[] required = ["manifest", "manifest-sha256"];
        if (Validate(command, required, required) is { } invalid) return invalid;
        if (service is null) return Usage(command, "npc dialogue requires its application service.");
        return Stage(command, await service.VerifyAsync(new SkyrimDialogueVerifyRequest(new WorkspacePath(command.Options["manifest"]), new Sha256Hash(command.Options["manifest-sha256"])), token));
    }

    private CommandExitCode Stage<T>(ParsedCommand command, SkyrimDialogueStageResult<T> result) where T : class
    { output.WriteLine(JsonSerializer.Serialize(new { command = command.Name, succeeded = result.Succeeded, document = result.Document, documentPath = result.DocumentPath?.Value, documentSha256 = result.DocumentSha256?.Value, diagnostics = result.Diagnostics }, JsonOptions)); return result.Succeeded ? CommandExitCode.Success : DiagnosticExitCodeClassifier.ClassifyFailure(result.Diagnostics); }
    private CommandExitCode? Validate(ParsedCommand c, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> required) => ValidateAllowed(c, allowed) ?? (Missing(c, required) is { } missing ? Usage(c, $"Dialogue command requires --{missing}.") : null);
    private CommandExitCode? ValidateAllowed(ParsedCommand c, IReadOnlyCollection<string> allowed) { if (c.DuplicateOptions.Length > 0) return Usage(c, "Dialogue options may not be repeated."); string? unknown = c.Options.Keys.FirstOrDefault(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase)); return unknown is null ? null : Usage(c, $"Dialogue commands do not support --{unknown}."); }
    private static string? Missing(ParsedCommand c, IEnumerable<string> required) => required.FirstOrDefault(k => !c.Options.TryGetValue(k, out string? v) || string.IsNullOrWhiteSpace(v) || v == "true");
    private CommandExitCode Usage(ParsedCommand c, string message) => Failure(c, "usage", message, CommandExitCode.UsageError);
    private CommandExitCode Failure(ParsedCommand c, string code, string message, CommandExitCode exit) { if (c.Json) output.WriteLine(JsonSerializer.Serialize(new { code, message }, JsonOptions)); else error.WriteLine($"{code}: {message}"); return exit; }
    private static bool TryBoolean(string value, out bool parsed)
    {
        if (value == "1") { parsed = true; return true; }
        return bool.TryParse(value, out parsed);
    }
}
