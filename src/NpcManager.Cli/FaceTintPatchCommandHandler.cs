using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceTintPatchCommandHandler(INpcFaceTintPatchService service, TextWriter output, TextWriter error)
{
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage)) return WriteUsageError(command.Json, errorMessage);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");
        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        NpcFaceTintPatchResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = FaceTintPatchResponse.From(proposal, request.Patch, result);
        var message = response.Applied ? "face tint patch: APPLIED" : response.IsApplicable ? "face tint patch: PROPOSAL" :
            response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "face tint patch: REFUSED" : "face tint patch: NO-OP";
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out NpcFaceTintPatchRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "plugin", out var plugin, out errorMessage) ||
            !TryPath(command, "output", out var output, out errorMessage) ||
            !TryFormId(command, out var formId, out errorMessage) ||
            !TryLayers(command, out var layers, out errorMessage)) return false;
        Sha256Hash? hash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText))
        {
            try { hash = new Sha256Hash(hashText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText))
        {
            try { proposal = new WorkspacePath(proposalText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        request = new NpcFaceTintPatchRequest(edition, plugin, output, formId, new NpcFaceTintPatch(layers), hash,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryLayers(ParsedCommand command, out ImmutableArray<NpcFaceTintLayer> layers, out string errorMessage)
    {
        layers = [];
        if (!command.Options.TryGetValue("layers", out var text)) { errorMessage = "The command requires --layers JSON or --layers @K:\\...\\layers.json."; return false; }
        try
        {
            if (text.StartsWith('@'))
            {
                var sourcePath = new WorkspacePath(text[1..]);
                WorkspacePath workspaceRoot = ActorwrightWorkspace.ResolveRoot();
                if (!sourcePath.IsUnder(workspaceRoot)) throw new FormatException("A layers file must remain under the K-only workspace root.");
                if (!File.Exists(sourcePath.Value)) throw new FormatException("The layers file does not exist.");
                if (HasReparsePath(sourcePath.Value)) throw new FormatException("A layers file may not traverse a reparse point.");
                text = File.ReadAllText(sourcePath.Value);
            }
            if (text.Length > 1_048_576) throw new FormatException("The layers JSON exceeds the 1 MiB safety limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--layers must be a JSON array.");
            var builder = ImmutableArray.CreateBuilder<NpcFaceTintLayer>();
            foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
            {
                if (index >= 256) throw new FormatException("A face-tint patch may contain at most 256 layers.");
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"Layer {index} must be a JSON object.");
                var fields = item.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in fields) if (!names.Add(field.Name)) throw new FormatException($"Layer {index} contains duplicate field '{field.Name}'.");
                var allowed = new HashSet<string>(["dataType", "optionIndex", "value", "color", "templateColorIndex", "rawTendBase64"], StringComparer.Ordinal);
                var unknownName = fields.Select(field => field.Name).FirstOrDefault(name => !allowed.Contains(name));
                if (unknownName is not null) throw new FormatException($"Layer {index} contains unknown field '{unknownName}'.");
                var typeText = RequiredString(item, "dataType", index);
                var dataType = typeText.ToLowerInvariant() switch
                {
                    "value-color" or "valuecolor" or "palette" => NpcFaceTintDataType.ValueColor,
                    "texture-set" or "textureset" => NpcFaceTintDataType.TextureSet,
                    _ => throw new FormatException($"Layer {index} dataType must be value-color or texture-set.")
                };
                var option = RequiredUInt16(item, "optionIndex", index);
                var value = RequiredByte(item, "value", index);
                NpcFaceTintColor? color = null;
                if (item.TryGetProperty("color", out var colorElement))
                {
                    if (colorElement.ValueKind != JsonValueKind.Object) throw new FormatException($"Layer {index} color must be an object.");
                    var colorFields = colorElement.EnumerateObject().ToArray();
                    var colorNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var field in colorFields) if (!colorNames.Add(field.Name)) throw new FormatException($"Layer {index} color contains duplicate field '{field.Name}'.");
                    var unknownColor = colorFields.Select(field => field.Name).FirstOrDefault(name => name is not ("red" or "green" or "blue"));
                    if (unknownColor is not null) throw new FormatException($"Layer {index} color contains unknown field '{unknownColor}'.");
                    color = new NpcFaceTintColor(RequiredByte(colorElement, "red", index), RequiredByte(colorElement, "green", index), RequiredByte(colorElement, "blue", index));
                }
                short? template = null;
                if (item.TryGetProperty("templateColorIndex", out var templateElement))
                {
                    if (templateElement.ValueKind != JsonValueKind.Number || !templateElement.TryGetInt32(out var parsed) || parsed < short.MinValue || parsed > short.MaxValue)
                        throw new FormatException($"Layer {index} templateColorIndex must be a signed 16-bit integer.");
                    template = (short)parsed;
                }
                string? raw = null;
                if (item.TryGetProperty("rawTendBase64", out var rawElement))
                {
                    if (rawElement.ValueKind != JsonValueKind.String) throw new FormatException($"Layer {index} rawTendBase64 must be a string.");
                    raw = rawElement.GetString();
                    if (raw is not null && raw.Length > 64) throw new FormatException($"Layer {index} rawTendBase64 exceeds the safety limit.");
                }
                builder.Add(new NpcFaceTintLayer(dataType, option, value, color, template, raw));
            }
            layers = builder.ToImmutable();
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static string RequiredString(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"Layer {index} requires string '{name}'.");
        return value.GetString()!;
    }
    private static ushort RequiredUInt16(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetUInt16(out var parsed))
            throw new FormatException($"Layer {index} requires unsigned 16-bit '{name}'.");
        return parsed;
    }
    private static byte RequiredByte(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetByte(out var parsed))
            throw new FormatException($"Layer {index} requires byte '{name}'.");
        return parsed;
    }
    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition)) { errorMessage = "The command requires --game|--edition fallout4|skyrimse."; return false; }
        errorMessage = string.Empty; return true;
    }
    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; }
        try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; } catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }
    private static bool TryFormId(ParsedCommand command, out FormId formId, out string errorMessage)
    {
        formId = default;
        var value = command.Options.GetValueOrDefault("form-id") ?? command.Options.GetValueOrDefault("npc");
        if (value is null || !FormId.TryParse(value, out formId)) { errorMessage = "The command requires --npc|--form-id with a hexadecimal FormID."; return false; }
        errorMessage = string.Empty; return true;
    }
    private CommandExitCode WriteUsageError(bool json, string message)
    { if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message })); else error.WriteLine($"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? (DiagnosticExitCodeClassifier.Classify(diagnostics)) : CommandExitCode.Success;

    private sealed record FaceTintPatchResponse(bool IsApplicable, bool Applied, string Edition, string Plugin, string Output,
        string FormId, Sha256Hash InputSha256, Sha256Hash? OutputSha256, ImmutableArray<NpcFaceTintLayer> Layers,
        ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static FaceTintPatchResponse From(NpcFaceTintPatchProposal proposal, NpcFaceTintPatch patch, NpcFaceTintPatchResult? result) => new(
            proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.InputPlugin.Value,
            proposal.OutputPlugin.Value, proposal.TargetFormId.ToString(), proposal.InputHash, result?.OutputHash, patch.Layers,
            proposal.Changes, proposal.PreservedFields, result?.Diagnostics ?? proposal.Diagnostics);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
