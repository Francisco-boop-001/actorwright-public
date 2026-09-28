using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RuntimeScriptProposalCommandHandler(IRuntimeScriptProposalService service, WorkspacePath labRoot, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition) ||
            !command.Options.TryGetValue("npc", out var npcText) || !FormId.TryParse(npcText, out var npc) ||
            !command.Options.TryGetValue("appearance", out var appearance) || !command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "runtime-script propose requires --game|--edition, --npc, --appearance, and --output.");
        try
        {
            var document = await ReadAppearanceAsync(appearance, cancellationToken);
            var result = await service.ProposeAsync(new RuntimeScriptProposalRequest(edition, npc, document.ScriptName,
                document.Properties, document.ObjectReferences, document.Fragments, new WorkspacePath(outputPath),
                command.Options.TryGetValue("plugin", out var plugin) ? new WorkspacePath(plugin) : null), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); else output.WriteLine(result.Written ? "runtime-script propose: proposal written" : "runtime-script propose: refused");
            return ExitCode(result.Diagnostics);
        }
        catch (JsonException exception) { return Usage(command.Json, exception.Message); }
        catch (IOException exception) { return Usage(command.Json, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Security(command.Json, exception.Message); }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private async ValueTask<AppearanceDocument> ReadAppearanceAsync(string value, CancellationToken cancellationToken)
    {
        var json = value;
        if (value.StartsWith('@'))
        {
            var path = new WorkspacePath(value[1..]);
            if (!path.IsUnder(labRoot) || !File.Exists(path.Value)) throw new UnauthorizedAccessException("The @appearance input must be an existing K-local file.");
            if ((File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("The @appearance input may not be a reparse point.");
            json = await File.ReadAllTextAsync(path.Value, cancellationToken);
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
            !root.TryGetProperty("scriptName", out var script) || script.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
            throw new JsonException("Appearance input requires schemaVersion 1, scriptName, and a properties array.");
        var propertyBuilder = ImmutableArray.CreateBuilder<RuntimeScriptInputProperty>();
        foreach (var item in properties.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !item.TryGetProperty("value", out var propertyValue) ||
                !TryParseType(type.GetString()!, out var parsedType)) throw new JsonException("Each runtime-script property requires name, type, and value.");
            propertyBuilder.Add(new RuntimeScriptInputProperty(name.GetString()!, parsedType, propertyValue.Clone()));
        }
        var references = ImmutableArray.CreateBuilder<RuntimeScriptObjectReference>();
        if (root.TryGetProperty("objectReferences", out var refs))
        {
            if (refs.ValueKind != JsonValueKind.Array) throw new JsonException("objectReferences must be an array.");
            foreach (var item in refs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || !item.TryGetProperty("reference", out var reference) ||
                    name.ValueKind != JsonValueKind.String || reference.ValueKind != JsonValueKind.String || !FormReference.TryParse(reference.GetString()!, out var parsed))
                    throw new JsonException("Each object reference requires a name and Plugin|FormID reference.");
                references.Add(new RuntimeScriptObjectReference(name.GetString()!, parsed));
            }
        }
        var fragments = ImmutableArray.CreateBuilder<RuntimeScriptFragment>();
        if (root.TryGetProperty("fragments", out var fragmentList))
        {
            if (fragmentList.ValueKind != JsonValueKind.Array) throw new JsonException("fragments must be an array.");
            foreach (var item in fragmentList.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index) || !item.TryGetProperty("startInstruction", out var start) || !item.TryGetProperty("endInstruction", out var end))
                    throw new JsonException("Each fragment requires index, startInstruction, and endInstruction.");
                fragments.Add(new RuntimeScriptFragment(index.GetInt32(), start.GetInt32(), end.GetInt32()));
            }
        }
        return new AppearanceDocument(script.GetString()!, propertyBuilder.ToImmutable(), references.ToImmutable(), fragments.ToImmutable());
    }

    private static bool TryParseType(string value, out RuntimeScriptPropertyType type) => Enum.TryParse(value, ignoreCase: true, out type);
    private static CommandExitCode ExitCode(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? DiagnosticExitCodeClassifier.Classify(diagnostics) : CommandExitCode.Success;
    private CommandExitCode Usage(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private CommandExitCode Security(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("security-refusal", message), JsonOptions) : $"ERROR security-refusal: {message}"); return DiagnosticExitCodeClassifier.KnownSecurityRefusal; }
    private sealed record AppearanceDocument(string ScriptName, ImmutableArray<RuntimeScriptInputProperty> Properties, ImmutableArray<RuntimeScriptObjectReference> ObjectReferences, ImmutableArray<RuntimeScriptFragment> Fragments);
    private sealed record ErrorResponse(string Code, string Message);
}
