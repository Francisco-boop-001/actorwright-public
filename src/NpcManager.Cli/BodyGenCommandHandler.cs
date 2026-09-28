using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class BodyGenCommandHandler(IBodyGenService service, WorkspacePath labRoot, TextWriter output, TextWriter error)
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

        var result = await service.BuildAsync(request, cancellationToken);
        var response = new BodyGenResponse(result.Written, result.Edition.ToWireName(), result.Plugin.Value,
            result.NpcFormId.ToString(), result.TemplateName, result.SourceHash?.Value,
            result.Files.Select(file => new FileResponse(file.RelativePath.Value, file.AbsolutePath.Value,
                file.ByteLength, file.Sha256.Value)).ToImmutableArray(), result.Diagnostics);
        Write(response, command.Json, result.Written ? "bodygen: PASS" : "bodygen: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    internal async ValueTask<CommandExitCode> RunWriteAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryGetEdition(command, out var edition, out var errorMessage) ||
            !command.Options.TryGetValue("assignments", out var assignments) ||
            !command.Options.TryGetValue("output", out var outputRoot))
            return Usage(command.Json, errorMessage ?? "bodygen write requires --game|--edition, --assignments, and --output.");
        try
        {
            var document = await ReadAssignmentsAsync(assignments, cancellationToken);
            if (!TryBuildTypedRequest(document, edition, new WorkspacePath(outputRoot), out var request, out errorMessage))
                return Usage(command.Json, errorMessage);
            var result = await service.BuildTypedAsync(request, cancellationToken);
            var response = new BodyGenResponse(result.Written, result.Edition.ToWireName(), result.Plugin.Value,
                result.NpcFormId.ToString(), result.TemplateName, result.SourceHash?.Value,
                result.Files.Select(file => new FileResponse(file.RelativePath.Value, file.AbsolutePath.Value, file.ByteLength, file.Sha256.Value)).ToImmutableArray(), result.Diagnostics);
            Write(response, command.Json, result.Written ? "bodygen write: PASS" : "bodygen write: REFUSED");
            if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        catch (JsonException exception) { return Usage(command.Json, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Security(command.Json, exception.Message); }
    }

    private static bool TryGetEdition(ParsedCommand command, out GameEdition edition, out string? errorMessage)
    {
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is not null && GameEditionExtensions.TryParseWireName(value, out edition)) { errorMessage = null; return true; }
        edition = default; errorMessage = "bodygen requires --edition|--game fallout4|skyrimse."; return false;
    }

    private async ValueTask<JsonElement> ReadAssignmentsAsync(string value, CancellationToken cancellationToken)
    {
        var path = value.StartsWith('@') ? new WorkspacePath(value[1..]).Value : null;
        if (path is not null && (!new WorkspacePath(path).IsUnder(labRoot) || !File.Exists(path))) throw new UnauthorizedAccessException("The @assignments input must be an existing K-local file.");
        var json = path is null ? value : await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        return document.RootElement.Clone();
    }

    private static bool TryBuildTypedRequest(JsonElement root, GameEdition edition, WorkspacePath outputRoot, out BodyGenTypedBuildRequest request, out string errorMessage)
    {
        request = default!;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("plugin", out var pluginValue) || pluginValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("npc", out var npcValue) || npcValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("modName", out var modValue) || modValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("morphs", out var morphs) || morphs.ValueKind != JsonValueKind.Array)
        { errorMessage = "Assignments require schemaVersion 1, plugin, npc, modName, and a morphs array."; return false; }
        if (!FormId.TryParse(npcValue.GetString()!, out var npc)) { errorMessage = "Assignment npc must be a hexadecimal FormID."; return false; }
        var builder = ImmutableArray.CreateBuilder<BodyGenMorph>();
        foreach (var item in morphs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || !item.TryGetProperty("value", out var value) || name.ValueKind != JsonValueKind.String || !value.TryGetSingle(out var scalar))
            { errorMessage = "Each assignment morph requires a string name and numeric value."; return false; }
            builder.Add(new BodyGenMorph(name.GetString()!, scalar));
        }
        EditorId? editorId = null;
        if (root.TryGetProperty("editorId", out var editorIdValue))
        {
            if (editorIdValue.ValueKind != JsonValueKind.String)
            {
                errorMessage = "Optional assignment editorId must be a string.";
                return false;
            }
            try { editorId = new EditorId(editorIdValue.GetString()!); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        try
        {
            request = new BodyGenTypedBuildRequest(edition, new PluginName(pluginValue.GetString()!), npc,
                modValue.GetString()!, builder.ToImmutable(), outputRoot)
            {
                EditorId = editorId
            };
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }

    private static bool TryBuildRequest(ParsedCommand command, out BodyGenBuildRequest request, out string errorMessage)
    {
        request = default!;
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition))
        { errorMessage = "bodygen requires --edition|--game fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("plugin", out var pluginText) || !command.Options.TryGetValue("npc", out var npcText) ||
            !command.Options.TryGetValue("mod-name", out var modName) || !command.Options.TryGetValue("morphs", out var morphsText) ||
            !command.Options.TryGetValue("output-root", out var outputText))
        { errorMessage = "bodygen build requires --plugin, --npc, --mod-name, --morphs, and --output-root."; return false; }
        if (!FormId.TryParse(npcText, out var npcFormId))
        { errorMessage = "--npc must be a hexadecimal FormID."; return false; }
        try
        {
            request = new BodyGenBuildRequest(edition, new PluginName(pluginText), npcFormId, modName,
                new WorkspacePath(morphsText), new WorkspacePath(outputText));
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        { errorMessage = exception.Message; return false; }
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostic = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(diagnostic), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private CommandExitCode Security(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse(ImmutableArray.Create(new Diagnostic("security-refusal", DiagnosticSeverity.Error, message))), JsonOptions) : $"ERROR security-refusal: {message}"); return DiagnosticExitCodeClassifier.KnownSecurityRefusal; }

    private sealed record BodyGenResponse(bool Written, string Edition, string Plugin, string NpcFormId,
        string TemplateName, string? SourceHash, ImmutableArray<FileResponse> Files, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record FileResponse(string RelativePath, string AbsolutePath, int ByteLength, string Sha256);
    private sealed record ErrorResponse(ImmutableArray<Diagnostic> Diagnostics);
}
