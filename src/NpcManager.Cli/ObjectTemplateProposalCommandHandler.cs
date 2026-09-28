using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ObjectTemplateProposalCommandHandler(IObjectTemplateProposalService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var message)) return Usage(command.Json, message);
        var result = await service.ProposeAsync(request, cancellationToken);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(Response.From(result), JsonOptions) : result.Written ? "object-template propose: PASS" : "object-template propose: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out ObjectTemplateProposalRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition)) { message = "object-template propose requires --edition fallout4."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) ||
            !command.Options.TryGetValue("combinations", out var combinations) || !command.Options.TryGetValue("includes", out var includes) ||
            !command.Options.TryGetValue("output", out var output)) { message = "object-template propose requires --plugin, --source, --combinations, --includes, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0) { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            var combinationRows = ParseCombinations(ReadJson(combinations, "combinations"));
            var includeRows = ParseIncludes(ReadJson(includes, "includes"));
            var combined = combinationRows.Select(_ => new List<ObjectTemplateIncludeProposal>()).ToArray();
            foreach (var include in includeRows)
            {
                if (include.Index >= combined.Length) throw new FormatException("Include combinationIndex must refer to an existing combination.");
                combined[include.Index].Add(include.Value);
            }
            var final = combinationRows.Select((item, index) => item with { Includes = combined[index].ToImmutableArray() }).ToImmutableArray();
            var root = ReadJsonObject(combinations, "combinations", allowMissing: true);
            request = new ObjectTemplateProposalRequest(edition, new WorkspacePath(plugin), formId, ParseMode(root),
                new ObjectTemplateProposalPatch(ParseEditorId(root), final, ParseTargetFormId(root)), new WorkspacePath(output));
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException)
        { message = exception.Message; return false; }
    }

    private static ObjectTemplateProposalMode ParseMode(JsonElement root)
    {
        var text = root.TryGetProperty("mode", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text?.ToLowerInvariant() switch { "new" => ObjectTemplateProposalMode.New, "override" => ObjectTemplateProposalMode.Override, _ => throw new FormatException("The combinations JSON requires mode: new or override.") };
    }

    private static EditorId? ParseEditorId(JsonElement root)
    {
        if (!root.TryGetProperty("editorId", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException("The combinations JSON editorId must be a string.");
        return new EditorId(value.GetString()!);
    }

    private static FormId? ParseTargetFormId(JsonElement root)
    {
        if (!root.TryGetProperty("targetFormId", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || !FormId.TryParse(value.GetString()!, out var formId) || formId.Value == 0)
            throw new FormatException("The combinations JSON targetFormId must be a non-null hexadecimal FormID.");
        return formId;
    }

    private static JsonElement ReadJsonObject(string value, string label, bool allowMissing)
    {
        using var document = JsonDocument.Parse(ReadJson(value, label), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object && !allowMissing) throw new FormatException($"--{label} must be a JSON object.");
        return document.RootElement.Clone();
    }

    private static string ReadJson(string value, string label)
    {
        if (!value.StartsWith('@')) return value;
        var path = new WorkspacePath(value[1..]);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0) throw new FormatException($"A {label} file must exist under the K-only workspace root and may not be a reparse point.");
        if (new FileInfo(path.Value).Length > 262_144) throw new FormatException($"{label} JSON may not exceed 262144 bytes.");
        return File.ReadAllText(path.Value);
    }

    private static ImmutableArray<ObjectTemplateCombinationProposal> ParseCombinations(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--combinations must be a JSON object containing mode, editorId, and items.");
        var root = document.RootElement;
        var allowedRoot = new HashSet<string>(StringComparer.Ordinal) { "mode", "editorId", "targetFormId", "items" };
        ValidateProperties(root, allowedRoot, "combinations");
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) throw new FormatException("Combinations JSON requires an items array.");
        var builder = ImmutableArray.CreateBuilder<ObjectTemplateCombinationProposal>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each combination must be an object.");
            ValidateProperties(item, new HashSet<string>(StringComparer.Ordinal) { "displayName", "isDefault", "isEditorOnly", "parentCombinationIndex", "levelMin", "levelMax", "minLevelForRanks", "altLevelsPerTier", "keywords" }, "combination");
            builder.Add(new ObjectTemplateCombinationProposal(OptionalString(item, "displayName"), OptionalBool(item, "isDefault"), OptionalBool(item, "isEditorOnly"), OptionalNullableShort(item, "parentCombinationIndex"), OptionalByte(item, "levelMin"), OptionalByte(item, "levelMax"), OptionalByte(item, "minLevelForRanks"), OptionalByte(item, "altLevelsPerTier"), ParseReferences(item, "keywords"), []));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<(int Index, ObjectTemplateIncludeProposal Value)> ParseIncludes(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--includes must be a JSON array.");
        var builder = ImmutableArray.CreateBuilder<(int, ObjectTemplateIncludeProposal)>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each include must be an object.");
            ValidateProperties(item, new HashSet<string>(StringComparer.Ordinal) { "combinationIndex", "mod", "attachPointIndex", "optional", "dontUseAll" }, "include");
            var mod = RequiredReference(item, "mod");
            var index = RequiredInt(item, "combinationIndex");
            if (index < 0) throw new FormatException("Include combinationIndex must be non-negative.");
            builder.Add((index, new ObjectTemplateIncludeProposal(mod, OptionalByte(item, "attachPointIndex"), OptionalBool(item, "optional"), OptionalBool(item, "dontUseAll"))));
        }
        return builder.ToImmutable();
    }

    private static void ValidateProperties(JsonElement root, HashSet<string> allowed, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"{label} contains duplicate property '{property.Name}'.");
            if (!allowed.Contains(property.Name)) throw new FormatException($"{label} contains unsupported property '{property.Name}'.");
        }
    }

    private static ImmutableArray<FormReference> ParseReferences(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException($"Combination property '{name}' must be an array.");
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !FormReference.TryParse(item.GetString()!, out var reference)) throw new FormatException($"Combination property '{name}' contains an invalid FormReference.");
            builder.Add(reference);
        }
        return builder.ToImmutable();
    }

    private static FormReference RequiredReference(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || !FormReference.TryParse(value.GetString()!, out var reference)) throw new FormatException($"Include requires a valid {name} FormReference."); return reference; }
    private static string? OptionalString(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"Property '{name}' must be a string.");
    private static bool OptionalBool(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? false : value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : throw new FormatException($"Property '{name}' must be a boolean.");
    private static byte OptionalByte(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value)) return 0; if (!value.TryGetByte(out var parsed)) throw new FormatException($"Property '{name}' must be an unsigned byte."); return parsed; }
    private static short? OptionalNullableShort(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value)) return null; if (!value.TryGetInt16(out var parsed) || parsed < 0) throw new FormatException($"Property '{name}' must be a non-negative signed 16-bit index."); return parsed; }
    private static int RequiredInt(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var parsed)) throw new FormatException($"Property '{name}' must be an integer."); return parsed; }

    private CommandExitCode Usage(bool json, string message) { var response = new ErrorResponse("usage-error", message); error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}"); return CommandExitCode.UsageError; }
    private sealed record ErrorResponse(string Code, string Message);
    private sealed record Response(bool Written, string? ArtifactKind, string? Edition, ObjectTemplateProposalMode? Mode, string? SourcePlugin, string? SourceFormId, string? EditorId, string? TargetFormId, ImmutableArray<ObjectTemplateCombinationArtifact> Combinations, ImmutableArray<Diagnostic> Diagnostics)
    { public static Response From(ObjectTemplateProposalResult result) => new(result.Written, result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.Mode, result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId, result.Artifact?.EditorId, result.Artifact?.TargetFormId, result.Artifact?.Combinations ?? [], result.Diagnostics); }
}
