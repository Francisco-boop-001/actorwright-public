using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimOverlayBakeCommandHandler(ISkyrimOverlayFoldService service, TextWriter output, TextWriter error)
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly WorkspacePath LabRoot = ActorwrightWorkspace.ResolveRoot();

    public ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildRequest(command, out var request, out var outputPath, out var errorMessage))
            return ValueTask.FromResult(WriteUsageError(command.Json, errorMessage));

        var result = service.Fold(request, cancellationToken);
        if (!result.IsSuccess)
        {
            WriteResponse(command.Json, result, outputPath, written: false);
            return ValueTask.FromResult(CommandExitCode.ValidationFailure);
        }

        if (!TryWriteNewOutput(outputPath, result.OutputDds!, cancellationToken, out errorMessage))
        {
            WriteWriteError(command.Json, errorMessage, result, outputPath);
            return ValueTask.FromResult(DiagnosticExitCodeClassifier.KnownSecurityRefusal);
        }

        WriteResponse(command.Json, result, outputPath, written: true);
        return ValueTask.FromResult(CommandExitCode.Success);
    }

    private static bool TryBuildRequest(ParsedCommand command, out SkyrimOverlayFoldRequest request,
        out WorkspacePath outputPath, out string errorMessage)
    {
        request = default!;
        outputPath = default;
        var game = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (!string.Equals(game, "skyrimse", StringComparison.OrdinalIgnoreCase))
            return Fail(out errorMessage, "body overlay bake supports --game skyrimse only.");
        if (!command.Options.TryGetValue("layers", out var layersText))
            return Fail(out errorMessage, "The command requires --layers JSON or --layers @K:\\...\\layers.json.");
        if (!command.Options.TryGetValue("output", out var outputText))
            return Fail(out errorMessage, "The command requires --output <new K-local .dds path>.");
        try
        {
            var sourceBytes = ReadInputBytes(layersText);
            if (sourceBytes.Length > MaxBytes) throw new FormatException("The layers manifest exceeds the 8 MiB safety limit.");
            outputPath = ParseOutputPath(outputText);
            using var document = JsonDocument.Parse(sourceBytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 64
            });
            request = ParseRequest(document.RootElement, new Sha256Hash(Convert.ToHexString(SHA256.HashData(sourceBytes))));
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(out errorMessage, exception.Message);
        }
    }

    private static SkyrimOverlayFoldRequest ParseRequest(JsonElement root, Sha256Hash sourceHash)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("--layers must be a JSON object.");
        RejectUnknown(root, ["base", "facetint", "detail", "layers"]);
        var baseRaster = ParseRaster(Required(root, "base"), "base");
        var facetint = Optional(root, "facetint") is { } facet ? ParseRaster(facet, "facetint") : null;
        var detail = Optional(root, "detail") is { } detailElement ? ParseRaster(detailElement, "detail") : null;
        var layersElement = Required(root, "layers");
        if (layersElement.ValueKind != JsonValueKind.Array) throw new FormatException("layers must be an array.");
        var layers = ImmutableArray.CreateBuilder<SkyrimOverlayLayerInput>();
        foreach (var (element, index) in layersElement.EnumerateArray().Select((value, position) => (value, position)))
        {
            if (index >= 256) throw new FormatException("A Skyrim overlay fold may contain at most 256 layers.");
            layers.Add(ParseLayer(element, index));
        }
        return new SkyrimOverlayFoldRequest(baseRaster, layers.ToImmutable(), facetint, detail, sourceHash);
    }

    private static SkyrimOverlayLayerInput ParseLayer(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException($"Layer {index} must be an object.");
        RejectUnknown(element, ["source", "node", "layerType", "blend", "color", "opacity", "texture"]);
        var source = ParseEnum<SkyrimOverlaySource>(RequiredString(element, "source", index), $"layers[{index}].source");
        var node = OptionalString(element, "node", index);
        var layerType = OptionalInt(element, "layerType", source == SkyrimOverlaySource.FaceOverlay ? 0 : -1, index);
        var blend = ParseEnum<SkyrimOverlayBlendMode>(OptionalString(element, "blend", index) ?? "normal", $"layers[{index}].blend");
        var color = ParseVector(element, "color", index);
        var opacity = OptionalDouble(element, "opacity", 1d, index);
        var texture = Optional(element, "texture") is { } textureElement ? ParseRaster(textureElement, $"layers[{index}].texture") : null;
        return new SkyrimOverlayLayerInput(source, node, layerType, blend, color, opacity, texture, index);
    }

    private static SkyrimRgbaRaster ParseRaster(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException($"{path} must be an object.");
        RejectUnknown(element, ["width", "height", "pixels"]);
        var width = RequiredInt(element, "width", path);
        var height = RequiredInt(element, "height", path);
        var pixels = Required(element, "pixels");
        if (pixels.ValueKind != JsonValueKind.Array) throw new FormatException($"{path}.pixels must be an array.");
        var values = ImmutableArray.CreateBuilder<double>();
        foreach (var value in pixels.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var parsed) || !double.IsFinite(parsed))
                throw new FormatException($"{path}.pixels must contain finite numbers.");
            values.Add(parsed);
        }
        return new SkyrimRgbaRaster(width, height, values.ToImmutable());
    }

    private static WorkspacePath ParseOutputPath(string text)
    {
        var path = new WorkspacePath(text);
        if (!path.IsUnder(LabRoot)) throw new FormatException("The output must remain under the configured Actorwright workspace.");
        if (HasAlternateDataStream(path.Value)) throw new FormatException("The output may not address an alternate data stream.");
        if (!string.Equals(Path.GetExtension(path.Value), ".dds", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The output must use the .dds extension.");
        return path;
    }

    private static bool TryWriteNewOutput(WorkspacePath path, byte[] bytes, CancellationToken cancellationToken, out string errorMessage)
    {
        errorMessage = string.Empty;
        var parent = Directory.GetParent(path.Value)?.FullName;
        if (parent is null) return Fail(out errorMessage, "The output parent is invalid.");
        try
        {
            Directory.CreateDirectory(parent);
            if (HasReparsePath(parent) || File.Exists(path.Value) || Directory.Exists(path.Value))
                return Fail(out errorMessage, "The output must be a new non-reparse K-local file.");
            var temporary = path.Value + $".tmp-{Guid.NewGuid():N}";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           64 * 1024, FileOptions.SequentialScan))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path.Value, overwrite: false);
                return true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(out errorMessage, $"The output was refused: {exception.Message}");
        }
    }

    private static byte[] ReadInputBytes(string text)
    {
        if (!text.StartsWith('@')) return Encoding.UTF8.GetBytes(text);
        var path = new WorkspacePath(text[1..]);
        if (!path.IsUnder(LabRoot)) throw new FormatException("A layers manifest must remain under the K-only workspace root.");
        if (HasAlternateDataStream(path.Value)) throw new FormatException("A layers manifest may not address an alternate data stream.");
        if (!File.Exists(path.Value) || HasReparsePath(path.Value)) throw new FormatException("The layers manifest is missing or traverses a reparse point.");
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

    private static bool HasAlternateDataStream(string path) => path.Length > 2 && path[2..].Contains(':');

    private void WriteResponse(bool json, SkyrimOverlayFoldResult result, WorkspacePath outputPath, bool written)
    {
        var response = new
        {
            schemaVersion = 1,
            game = "skyrimse",
            isValid = result.IsValid,
            written,
            output = written ? outputPath.Value : null,
            sourceSha256 = result.SourceSha256?.ToString(),
            canonicalSha256 = result.CanonicalSha256.ToString(),
            outputSha256 = result.OutputSha256?.ToString(),
            width = result.Output?.Width,
            height = result.Output?.Height,
            orderedLayers = result.OrderedLayers.Select(layer => new { source = layer.Source.ToString(), layer.Node, layer.SourceIndex }).ToArray(),
            diagnostics = result.Diagnostics
        };
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(written ? $"body overlay bake: WROTE {outputPath.Value}" : "body overlay bake: REFUSED");
    }

    private void WriteWriteError(bool json, string message, SkyrimOverlayFoldResult result, WorkspacePath outputPath)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "output-refused", message, output = outputPath.Value, canonicalSha256 = result.CanonicalSha256.ToString() }, JsonOptions));
        else error.WriteLine($"ERROR output-refused: {message}");
    }

    private CommandExitCode WriteUsageError(bool json, string message)
    { if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message }, JsonOptions)); else error.WriteLine($"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new FormatException($"Missing required field '{name}'.");

    private static JsonElement? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : null;

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"Duplicate field '{property.Name}'.");
            if (!allowed.Contains(property.Name)) throw new FormatException($"Unknown field '{property.Name}'.");
        }
    }

    private static ImmutableArray<double> ParseVector(JsonElement element, string name, int index)
    {
        var value = Required(element, name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
            throw new FormatException($"Layer {index} {name} must contain exactly four numbers.");
        var values = ImmutableArray.CreateBuilder<double>(4);
        foreach (var number in value.EnumerateArray())
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var parsed) || !double.IsFinite(parsed))
                throw new FormatException($"Layer {index} {name} must contain finite numbers.");
            else values.Add(parsed);
        return values.ToImmutable();
    }

    private static string? OptionalString(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"Layer {index} {name} must be a non-empty string when present.");
        return value.GetString();
    }

    private static string RequiredString(JsonElement element, string name, int index) =>
        OptionalString(element, name, index) ?? throw new FormatException($"Layer {index} requires '{name}'.");

    private static int RequiredInt(JsonElement element, string name, string path) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed : throw new FormatException($"{path}.{name} must be a 32-bit integer.");

    private static int OptionalInt(JsonElement element, string name, int fallback, int index) =>
        !element.TryGetProperty(name, out var value) ? fallback : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed : throw new FormatException($"Layer {index} {name} must be a 32-bit integer.");

    private static double OptionalDouble(JsonElement element, string name, double fallback, int index) =>
        !element.TryGetProperty(name, out var value) ? fallback : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) && double.IsFinite(parsed)
            ? parsed : throw new FormatException($"Layer {index} {name} must be a finite number.");

    private static T ParseEnum<T>(string value, string path) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new FormatException($"{path} has an unsupported value '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<T>())}.");

    private static bool Fail(out string errorMessage, string message)
    { errorMessage = message; return false; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
