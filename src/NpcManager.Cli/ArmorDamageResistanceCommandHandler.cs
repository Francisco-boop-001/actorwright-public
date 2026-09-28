using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ArmorDamageResistanceCommandHandler(IArmorDamageResistanceService service, TextWriter output, TextWriter error)
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
        output.WriteLine(command.Json ? JsonSerializer.Serialize(Response.From(result), JsonOptions) : result.Written ? "armor damage-resist: PASS" : "armor damage-resist: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out ArmorDamageResistanceRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "armor damage-resist requires --edition fallout4."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) ||
            !command.Options.TryGetValue("damage-resist", out var entries) || !command.Options.TryGetValue("output", out var output))
        { message = "armor damage-resist requires --plugin, --source, --damage-resist, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0)
        { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            using var document = JsonDocument.Parse(ReadJson(entries), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--damage-resist must be a JSON array.");
            var builder = ImmutableArray.CreateBuilder<ArmorDamageResistanceEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each damage resistance must be a JSON object.");
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!properties.Add(property.Name)) throw new FormatException("Damage resistance entries may not contain duplicate properties.");
                    if (property.Name is not ("damageType" or "value"))
                        throw new FormatException($"Damage resistance contains unsupported property '{property.Name}'.");
                }
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("damageType", out var type) ||
                    type.ValueKind != JsonValueKind.String || !FormReference.TryParse(type.GetString() ?? string.Empty, out var reference) ||
                    !item.TryGetProperty("value", out var value) || !value.TryGetUInt32(out var parsedValue))
                    throw new FormatException("Each damage resistance requires damageType FormReference and unsigned value.");
                builder.Add(new ArmorDamageResistanceEntry(reference, parsedValue));
            }
            request = new ArmorDamageResistanceRequest(edition, new WorkspacePath(plugin), formId,
                builder.ToImmutable(), new WorkspacePath(output));
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException)
        { message = exception.Message; return false; }
    }

    private static string ReadJson(string value)
    {
        if (!value.StartsWith('@')) return value;
        var path = new WorkspacePath(value[1..]);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("A damage resistance file must exist under the K-only workspace root and may not be a reparse point.");
        if (new FileInfo(path.Value).Length > 65_536) throw new FormatException("Damage resistance JSON may not exceed 65536 bytes.");
        return File.ReadAllText(path.Value);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record Response(bool Written, string? ArtifactKind, string? Edition, string? SourcePlugin,
        string? SourceFormId, string? InputSha256, ImmutableArray<ArmorDamageResistanceEntryArtifact> Entries,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static Response From(ArmorDamageResistanceResult result) => new(result.Written,
            result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.SourcePlugin,
            result.Artifact?.SourceFormId, result.Artifact?.InputSha256, result.Artifact?.Entries ?? [], result.Diagnostics);
    }
}
