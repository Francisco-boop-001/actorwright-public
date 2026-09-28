using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ObjectTemplatePropertyProposalCommandHandler(IObjectTemplatePropertyProposalService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuild(command, out var request, out var message)) { error.WriteLine(command.Json ? JsonSerializer.Serialize(new { code = "usage-error", message }, JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
        var result = await service.ProposeAsync(request, cancellationToken);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(Response.From(result), JsonOptions) : result.Written ? "object-template properties: PASS" : "object-template properties: REFUSED");
        return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics) : CommandExitCode.Success;
    }
    private static bool TryBuild(ParsedCommand command, out ObjectTemplatePropertyProposalRequest request, out string message)
    {
        request = default!; var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition)) { message = "object-template properties requires --edition fallout4."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) || !command.Options.TryGetValue("properties", out var properties) || !command.Options.TryGetValue("output", out var output)) { message = "object-template properties requires --plugin, --source, --properties, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0) { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            using var document = JsonDocument.Parse(Read(properties), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--properties must be a JSON array.");
            var rows = ImmutableArray.CreateBuilder<ObjectTemplatePropertyProposal>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each property must be an object.");
                var allowed = new HashSet<string>(StringComparer.Ordinal) { "valueType", "functionType", "propertyIndex", "value1Integer", "value1Float", "value1FormId", "value2Integer", "value2Float", "stepValue", "combinationIndex" }; var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject()) { if (!seen.Add(property.Name)) throw new FormatException("Properties may not contain duplicate fields."); if (!allowed.Contains(property.Name)) throw new FormatException($"Unsupported property field '{property.Name}'."); }
                var valueType = RequiredString(item, "valueType");
                var functionType = OptionalByte(item, "functionType"); var propertyIndex = OptionalUShort(item, "propertyIndex");
                FormReference? value1FormId = null;
                if (item.TryGetProperty("value1FormId", out var form))
                {
                    if (form.ValueKind != JsonValueKind.String || !FormReference.TryParse(form.GetString()!, out var parsedReference)) throw new FormatException("value1FormId must be a valid FormReference.");
                    value1FormId = parsedReference;
                }
                rows.Add(new ObjectTemplatePropertyProposal(valueType, functionType, propertyIndex, OptionalInt(item, "value1Integer"), OptionalDouble(item, "value1Float"), value1FormId, OptionalInt(item, "value2Integer"), OptionalDouble(item, "value2Float"), OptionalDouble(item, "stepValue"), OptionalShort(item, "combinationIndex")));
            }
            request = new ObjectTemplatePropertyProposalRequest(edition, new WorkspacePath(plugin), formId, rows.ToImmutable(), new WorkspacePath(output)); message = string.Empty; return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException) { message = exception.Message; return false; }
    }
    private static string Read(string value) { if (!value.StartsWith('@')) return value; var path = new WorkspacePath(value[1..]); WorkspacePath root = ActorwrightWorkspace.ResolveRoot(); if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0) throw new FormatException("A properties file must exist under the configured workspace root and may not be a reparse point."); if (new FileInfo(path.Value).Length > 262144) throw new FormatException("Properties JSON may not exceed 262144 bytes."); return File.ReadAllText(path.Value); }
    private static string RequiredString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException($"Property '{name}' must be a string.");
    private static byte OptionalByte(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? (byte)0 : value.TryGetByte(out var parsed) ? parsed : throw new FormatException($"Property '{name}' must be an unsigned byte.");
    private static ushort OptionalUShort(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? (ushort)0 : value.TryGetUInt16(out var parsed) ? parsed : throw new FormatException($"Property '{name}' must be an unsigned short.");
    private static int OptionalInt(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? 0 : value.TryGetInt32(out var parsed) ? parsed : throw new FormatException($"Property '{name}' must be an integer.");
    private static double OptionalDouble(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? 0 : value.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : throw new FormatException($"Property '{name}' must be a finite number.");
    private static short OptionalShort(JsonElement root, string name) => !root.TryGetProperty(name, out var value) ? (short)0 : value.TryGetInt16(out var parsed) && parsed >= 0 ? parsed : throw new FormatException($"Property '{name}' must be a non-negative signed 16-bit index.");
    private sealed record Response(bool Written, string? ArtifactKind, string? Edition, string? SourcePlugin, string? SourceFormId, string? EditorId, ImmutableArray<ObjectTemplatePropertyArtifact> Properties, ImmutableArray<Diagnostic> Diagnostics)
    { public static Response From(ObjectTemplatePropertyProposalResult result) => new(result.Written, result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId, result.Artifact?.EditorId, result.Artifact?.Properties ?? [], result.Diagnostics); }
}
