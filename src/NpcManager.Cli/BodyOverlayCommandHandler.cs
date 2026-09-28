using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class BodyOverlayCommandHandler(IBodyOverlayPatchService service, TextWriter output, TextWriter error)
{
    private const int MaxBytes = 1_048_576;
    private static readonly WorkspacePath LabRoot = ActorwrightWorkspace.ResolveRoot();

    public ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return ValueTask.FromResult(WriteUsageError(command.Json, errorMessage));

        var result = service.Resolve(request);
        var response = new
        {
            schemaVersion = 1,
            game = result.Edition.ToWireName(),
            npcFormId = result.NpcFormId.ToString(),
            sourceSha256 = result.SourceSha256?.ToString(),
            canonicalSha256 = result.CanonicalSha256.ToString(),
            isValid = result.IsValid,
            applied = false,
            layers = result.Layers,
            diagnostics = result.Diagnostics
        };
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(result.IsValid ? "body overlay patch: PROPOSAL" : "body overlay patch: REFUSED");
        return ValueTask.FromResult(
            DiagnosticExitCodeClassifier.Classify(result.Diagnostics));
    }

    private static bool TryBuildRequest(ParsedCommand command, out BodyOverlayPatchRequest request, out string errorMessage)
    {
        request = default!;
        var gameText = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (gameText is null || !GameEditionExtensions.TryParseWireName(gameText, out var edition))
            return Fail(out errorMessage, "The command requires --game|--edition fallout4|skyrimse.");
        var formText = command.Options.GetValueOrDefault("npc") ?? command.Options.GetValueOrDefault("form-id");
        if (formText is null || !FormId.TryParse(formText, out var formId))
            return Fail(out errorMessage, "The command requires --npc|--form-id with a hexadecimal FormID.");
        if (!command.Options.TryGetValue("layers", out var layersText))
            return Fail(out errorMessage, "The command requires --layers JSON or --layers @K:\\...\\layers.json.");

        try
        {
            var bytes = ReadLayerBytes(layersText);
            if (bytes.Length > MaxBytes) throw new FormatException("The layers JSON exceeds the 1 MiB safety limit.");
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("--layers must be a JSON array.");
            var layers = ImmutableArray.CreateBuilder<BodyOverlayLayerInput>();
            foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
            {
                if (index >= 256) throw new FormatException("A body-overlay patch may contain at most 256 layers.");
                layers.Add(ParseLayer(item, index, edition));
            }
            request = new BodyOverlayPatchRequest(edition, formId, layers.ToImmutable(), sourceHash);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(out errorMessage, exception.Message);
        }
    }

    private static BodyOverlayLayerInput ParseLayer(JsonElement item, int index, GameEdition edition)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"Layer {index} must be a JSON object.");
        var fields = item.EnumerateObject().ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
            if (!names.Add(field.Name)) throw new FormatException($"Layer {index} contains duplicate field '{field.Name}'.");

        var allowed = edition == GameEdition.Fallout4
            ? new HashSet<string>(["template", "priority", "tint", "offsetUV", "scaleUV", "slots"], StringComparer.Ordinal)
            : new HashSet<string>(["node", "diffuse", "normal", "tint", "alpha"], StringComparer.Ordinal);
        var unknown = fields.Select(field => field.Name).FirstOrDefault(name => !allowed.Contains(name));
        if (unknown is not null) throw new FormatException($"Layer {index} contains unknown field '{unknown}'.");

        if (edition == GameEdition.Fallout4)
        {
            var template = RequiredString(item, "template", index);
            var priority = OptionalInt(item, "priority", 0, index);
            return new BodyOverlayLayerInput(template, priority,
                OptionalVector(item, "tint", 4, index), OptionalVector(item, "offsetUV", 2, index),
                OptionalVector(item, "scaleUV", 2, index), OptionalSlots(item, index),
                null, null, null, [], null, index);
        }

        return new BodyOverlayLayerInput(null, 0, [], [], [], [],
            RequiredString(item, "node", index), RequiredString(item, "diffuse", index),
            OptionalString(item, "normal", index), OptionalVector(item, "tint", 4, index),
            OptionalFloat(item, "alpha", index), index);
    }

    private static ImmutableArray<Fo4OverlaySlot> OptionalSlots(JsonElement item, int index)
    {
        if (!item.TryGetProperty("slots", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException($"Layer {index} slots must be an array.");
        var result = ImmutableArray.CreateBuilder<Fo4OverlaySlot>();
        foreach (var (slot, slotIndex) in value.EnumerateArray().Select((entry, position) => (entry, position)))
        {
            if (slot.ValueKind != JsonValueKind.Object) throw new FormatException($"Layer {index} slot {slotIndex} must be an object.");
            result.Add(new Fo4OverlaySlot(RequiredInt(slot, "slot", $"{index}.{slotIndex}"),
                RequiredString(slot, "material", index)));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<float> OptionalVector(JsonElement item, string name, int size, int index)
    {
        if (!item.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != size)
            throw new FormatException($"Layer {index} {name} must contain exactly {size} numbers.");
        var result = ImmutableArray.CreateBuilder<float>(size);
        foreach (var number in value.EnumerateArray())
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetSingle(out var parsed) || !float.IsFinite(parsed))
                throw new FormatException($"Layer {index} {name} must contain finite numbers.");
            else result.Add(parsed);
        return result.ToImmutable();
    }

    private static float? OptionalFloat(JsonElement item, string name, int index)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var parsed) || !float.IsFinite(parsed))
            throw new FormatException($"Layer {index} {name} must be a finite number.");
        return parsed;
    }

    private static int OptionalInt(JsonElement item, string name, int fallback, int index) =>
        !item.TryGetProperty(name, out var value) ? fallback : ReadIntValue(value, name, index.ToString(CultureInfo.InvariantCulture));

    private static int RequiredInt(JsonElement item, string name, string index)
    {
        if (!item.TryGetProperty(name, out var value)) throw new FormatException($"Layer {index} requires '{name}'.");
        return ReadIntValue(value, name, index);
    }

    private static int ReadIntValue(JsonElement value, string name, string index) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed : throw new FormatException($"Layer {index} '{name}' must be a 32-bit integer.");

    private static string RequiredString(JsonElement item, string name, int index) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
            ? throw new FormatException($"Layer {index} requires non-empty string '{name}'.") : value.GetString()!;

    private static string? OptionalString(JsonElement item, string name, int index)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"Layer {index} '{name}' must be a non-empty string when present.");
        return value.GetString();
    }

    private static byte[] ReadLayerBytes(string text)
    {
        if (!text.StartsWith('@')) return System.Text.Encoding.UTF8.GetBytes(text);
        var path = new WorkspacePath(text[1..]);
        if (!path.IsUnder(LabRoot)) throw new FormatException("A layers file must remain under the K-only workspace root.");
        if (!File.Exists(path.Value)) throw new FormatException("The layers file does not exist.");
        if (HasReparsePath(path.Value)) throw new FormatException("A layers file may not traverse a reparse point.");
        return File.ReadAllBytes(path.Value);
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

    private static bool Fail(out string errorMessage, string message)
    { errorMessage = message; return false; }

    private CommandExitCode WriteUsageError(bool json, string message)
    { if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message })); else error.WriteLine($"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
