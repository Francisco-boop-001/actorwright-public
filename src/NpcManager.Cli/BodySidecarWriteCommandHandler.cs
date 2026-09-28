using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class BodySidecarWriteCommandHandler(IBodySidecarWriteService service, WorkspacePath labRoot, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var game = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        var morphs = command.Options.GetValueOrDefault("sliders") ?? command.Options.GetValueOrDefault("morphs");
        if (game is null || !GameEditionExtensions.TryParseWireName(game, out var edition) ||
            !command.Options.TryGetValue("plugin", out var pluginText) || !command.Options.TryGetValue("npc", out var npcText) ||
            !command.Options.TryGetValue("output", out var outputText) || morphs is null || !FormId.TryParse(npcText, out var npc))
            return Usage(command.Json, "body sidecar write requires --game|--edition, --plugin, --npc, --sliders, and --output.");
        try
        {
            var bodyMorphs = await ReadMorphsAsync(morphs, labRoot, cancellationToken);
            EditorId? editorId = null;
            if (command.Options.TryGetValue("editor-id", out var editorText)) editorId = new EditorId(editorText);
            var result = await service.WriteAsync(new BodySidecarWriteRequest(edition, new PluginName(pluginText), npc, editorId, bodyMorphs, new WorkspacePath(outputText)), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(result with { OutputHash = result.OutputHash }, JsonOptions)); else output.WriteLine(result.Written ? "body sidecar write: PASS" : "body sidecar write: REFUSED");
            if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        catch (JsonException exception) { return Usage(command.Json, exception.Message); }
        catch (UnauthorizedAccessException exception) { return WriteSecurityRefusal(command.Json, exception.Message); }
    }

    private static async ValueTask<ImmutableDictionary<string, float>> ReadMorphsAsync(string value, WorkspacePath labRoot, CancellationToken cancellationToken)
    {
        var path = value.StartsWith('@') ? new WorkspacePath(value[1..]).Value : null;
        if (path is not null && (!new WorkspacePath(path).IsUnder(labRoot) || !File.Exists(path)))
            throw new UnauthorizedAccessException("The @sliders input must be an existing K-local file.");
        var json = path is null ? value : await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("morphs", out var nested) ? nested : document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Body morphs must be a JSON object or {\"morphs\":{...}}.");
        var builder = ImmutableDictionary.CreateBuilder<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject()) builder.Add(property.Name, property.Value.GetSingle());
        return builder.ToImmutable();
    }

    private CommandExitCode Usage(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private CommandExitCode WriteSecurityRefusal(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("security-refusal", message), JsonOptions) : $"ERROR security-refusal: {message}"); return DiagnosticExitCodeClassifier.KnownSecurityRefusal; }
    private sealed record ErrorResponse(string Code, string Message);
}
