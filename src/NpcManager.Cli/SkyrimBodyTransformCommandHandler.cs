using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

/// <summary>CLI adapter for the typed RaceMenu transform/skin metadata apply use case.</summary>
internal sealed class SkyrimBodyTransformCommandHandler(
    ISkyrimBodyTransformService service,
    TextWriter output,
    TextWriter error)
{
    private const int MaxPatchBytes = 1_048_576;
    private static readonly WorkspacePath LabRoot = ActorwrightWorkspace.ResolveRoot();
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
        var result = await service.ApplyAsync(request, cancellationToken);
        var response = new
        {
            schemaVersion = 1,
            game = result.Edition.ToWireName(),
            npcFormId = result.NpcFormId.ToString(),
            preset = result.PresetPath.Value,
            output = result.OutputPath.Value,
            inputSha256 = result.InputSha256.Value,
            outputSha256 = result.OutputSha256?.Value,
            applied = result.Applied,
            isValid = result.IsValid,
            sourceTransforms = result.SourceTransforms,
            sourceSkinOverrides = result.SourceSkinOverrides,
            effectiveTransforms = result.EffectiveTransforms,
            effectiveSkinOverrides = result.EffectiveSkinOverrides,
            diagnostics = result.Diagnostics
        };
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(result.Applied ? "body transforms: PASS" : "body transforms: REFUSED");
        return Exit(result.Diagnostics, result.Applied);
    }

    private static bool TryBuildRequest(ParsedCommand command, out SkyrimBodyTransformApplyRequest request,
        out string errorMessage)
    {
        request = default!;
        var gameText = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (gameText is null || !GameEditionExtensions.TryParseWireName(gameText, out var edition))
            return Fail(out errorMessage, "The command requires --game|--edition skyrimse.");
        if (edition != GameEdition.SkyrimSpecialEdition)
            return Fail(out errorMessage, "body transforms apply is supported for skyrimse only.");
        var formText = command.Options.GetValueOrDefault("npc") ?? command.Options.GetValueOrDefault("form-id");
        if (formText is null || !FormId.TryParse(formText, out var formId))
            return Fail(out errorMessage, "The command requires --npc|--form-id with a hexadecimal FormID.");
        if (!command.Options.TryGetValue("preset", out var presetText))
            return Fail(out errorMessage, "body transforms apply requires --preset <K-local .jslot>. ");
        if (!command.Options.TryGetValue("output", out var outputText))
            return Fail(out errorMessage, "body transforms apply requires --output <new K-local .jslot>. ");

        try
        {
            var preset = new WorkspacePath(presetText);
            var output = new WorkspacePath(outputText);
            ImmutableArray<SkyrimNodeTransformPatch>? transforms = null;
            ImmutableArray<SkyrimSkinOverridePatch>? skins = null;
            if (command.Options.TryGetValue("transforms", out var transformText))
            {
                var bytes = ReadJsonBytes(transformText);
                transforms = ParseTransforms(bytes);
            }
            if (command.Options.TryGetValue("skin-overrides", out var skinText) || command.Options.TryGetValue("skins", out skinText))
            {
                var bytes = ReadJsonBytes(skinText);
                skins = ParseSkins(bytes);
            }
            Sha256Hash? expected = null;
            if (command.Options.TryGetValue("expected-sha256", out var expectedText)) expected = new Sha256Hash(expectedText);
            request = new SkyrimBodyTransformApplyRequest(edition, formId, preset, output, transforms, skins, expected);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException)
        {
            return Fail(out errorMessage, exception.Message);
        }
    }

    private static ImmutableArray<SkyrimNodeTransformPatch> ParseTransforms(byte[] bytes)
    {
        using var document = ParsePatchDocument(bytes, "transforms");
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--transforms must be a JSON array.");
        var builder = ImmutableArray.CreateBuilder<SkyrimNodeTransformPatch>();
        foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"transform {index} must be an object.");
            CheckFields(item, ["node", "firstPerson", "scale", "scaleMode", "position", "rotation"], $"transform {index}");
            var node = RequiredString(item, "node", $"transform {index}", 256);
            var firstPerson = OptionalBool(item, "firstPerson", false, $"transform {index}");
            var scale = OptionalFloat(item, "scale", $"transform {index}");
            var scaleMode = OptionalInt(item, "scaleMode", $"transform {index}");
            var position = OptionalVector(item, "position", 3, $"transform {index}");
            var rotation = OptionalVector(item, "rotation", 9, $"transform {index}");
            if (scale is null && scaleMode is null && position.IsDefaultOrEmpty && rotation.IsDefaultOrEmpty)
                throw new FormatException($"transform {index} must provide a transform component.");
            builder.Add(new SkyrimNodeTransformPatch(node, firstPerson, scale, scaleMode, position, rotation));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<SkyrimSkinOverridePatch> ParseSkins(byte[] bytes)
    {
        using var document = ParsePatchDocument(bytes, "skin-overrides");
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--skin-overrides must be a JSON array.");
        var builder = ImmutableArray.CreateBuilder<SkyrimSkinOverridePatch>();
        foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"skin override {index} must be an object.");
            CheckFields(item, ["slotMask", "firstPerson", "textures", "diffuse", "normal", "tint", "alpha"], $"skin override {index}");
            if (!item.TryGetProperty("slotMask", out var slotMaskElement) || !slotMaskElement.TryGetUInt32(out var slotMask))
                throw new FormatException($"skin override {index}.slotMask must be an unsigned 32-bit integer.");
            var firstPerson = OptionalBool(item, "firstPerson", false, $"skin override {index}");
            var textures = ParseTextures(item, index);
            var tint = OptionalVector(item, "tint", 4, $"skin override {index}");
            var alpha = OptionalFloat(item, "alpha", $"skin override {index}");
            if (textures.Count == 0 && tint.IsDefaultOrEmpty && alpha is null)
                throw new FormatException($"skin override {index} must provide a texture, tint, or alpha.");
            builder.Add(new SkyrimSkinOverridePatch(slotMask, firstPerson, textures, tint, alpha));
        }
        return builder.ToImmutable();
    }

    private static ImmutableDictionary<int, string> ParseTextures(JsonElement item, int index)
    {
        var result = ImmutableDictionary.CreateBuilder<int, string>();
        if (item.TryGetProperty("diffuse", out var diffuse)) AddTexture(result, 0, diffuse, $"skin override {index}.diffuse");
        if (item.TryGetProperty("normal", out var normal)) AddTexture(result, 1, normal, $"skin override {index}.normal");
        if (!item.TryGetProperty("textures", out var textures)) return result.ToImmutable();
        if (textures.ValueKind != JsonValueKind.Object) throw new FormatException($"skin override {index}.textures must be an object.");
        foreach (var property in textures.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot is < 0 or >= 512)
                throw new FormatException($"skin override {index}.textures contains an invalid slot.");
            AddTexture(result, slot, property.Value, $"skin override {index}.textures.{property.Name}");
        }
        return result.ToImmutable();
    }

    private static void AddTexture(ImmutableDictionary<int, string>.Builder result, int slot, JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new FormatException($"{path} must be a non-empty relative DDS path.");
        if (!result.TryAdd(slot, element.GetString()!)) throw new FormatException($"{path} duplicates texture slot {slot}.");
    }

    private static JsonDocument ParsePatchDocument(byte[] bytes, string name)
    {
        if (bytes.Length > MaxPatchBytes) throw new FormatException($"--{name} exceeds the {MaxPatchBytes} byte safety limit.");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
    }

    private static byte[] ReadJsonBytes(string text)
    {
        if (!text.StartsWith('@')) return Encoding.UTF8.GetBytes(text);
        var path = new WorkspacePath(text[1..]);
        if (!path.IsUnder(LabRoot)) throw new FormatException("JSON patch files must remain under the K-only workspace root.");
        if (!File.Exists(path.Value)) throw new FileNotFoundException("JSON patch file does not exist.");
        if (HasReparsePath(path.Value)) throw new FormatException("JSON patch files may not traverse reparse points.");
        return File.ReadAllBytes(path.Value);
    }

    private static void CheckFields(JsonElement item, string[] allowed, string path)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"{path} contains duplicate field '{property.Name}'.");
            if (!set.Contains(property.Name)) throw new FormatException($"{path} contains unknown field '{property.Name}'.");
        }
    }

    private static string RequiredString(JsonElement item, string name, string path, int maxLength)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > maxLength || value.GetString()!.Contains('\0')) throw new FormatException($"{path}.{name} is invalid.");
        return value.GetString()!.Trim();
    }

    private static bool OptionalBool(JsonElement item, string name, bool fallback, string path)
    {
        if (!item.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"{path}.{name} must be boolean.");
        return value.GetBoolean();
    }

    private static float? OptionalFloat(JsonElement item, string name, string path)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var parsed) || !float.IsFinite(parsed)) throw new FormatException($"{path}.{name} must be finite.");
        return parsed;
    }

    private static int? OptionalInt(JsonElement item, string name, string path)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt32(out var parsed)) throw new FormatException($"{path}.{name} must be a 32-bit integer.");
        return parsed;
    }

    private static ImmutableArray<float> OptionalVector(JsonElement item, string name, int length, string path)
    {
        if (!item.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length) throw new FormatException($"{path}.{name} must contain exactly {length} numbers.");
        var result = ImmutableArray.CreateBuilder<float>(length);
        foreach (var element in value.EnumerateArray())
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var parsed) || !float.IsFinite(parsed)) throw new FormatException($"{path}.{name} must contain finite numbers.");
            else result.Add(parsed);
        return result.ToImmutable();
    }

    private static bool HasReparsePath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", diagnostics }, JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private static CommandExitCode Exit(ImmutableArray<Diagnostic> diagnostics, bool applied)
    {
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(diagnostics);
        return applied ? CommandExitCode.Success : CommandExitCode.GeneralFailure;
    }

    private static bool Fail(out string errorMessage, string message)
    { errorMessage = message; return false; }
}
