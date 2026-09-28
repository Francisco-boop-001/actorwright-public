using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class MaterialSwapProposalCommandHandler(IMaterialSwapProposalService service, TextWriter output, TextWriter error)
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
        output.WriteLine(command.Json ? JsonSerializer.Serialize(Response.From(result), JsonOptions) : result.Written ? "material-swap propose: PASS" : "material-swap propose: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out MaterialSwapProposalRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition)) { message = "material-swap propose requires --edition fallout4."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) || !command.Options.TryGetValue("patch", out var patch) || !command.Options.TryGetValue("output", out var output)) { message = "material-swap propose requires --plugin, --source, --patch, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0) { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            using var document = JsonDocument.Parse(ReadPatchText(patch), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--patch must be a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!seen.Add(property.Name)) throw new FormatException($"--patch contains duplicate property '{property.Name}'.");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "mode", "editorId", "targetFormId", "treeFolder", "entries" };
            foreach (var property in document.RootElement.EnumerateObject()) if (!allowed.Contains(property.Name)) throw new FormatException($"--patch contains unsupported property '{property.Name}'.");
            var mode = RequiredString(document.RootElement, "mode").ToLowerInvariant() switch
            {
                "new" => MaterialSwapProposalMode.New,
                "override" => MaterialSwapProposalMode.Override,
                _ => throw new FormatException("Patch mode must be new or override.")
            };
            if (!document.RootElement.TryGetProperty("entries", out var entriesValue) || entriesValue.ValueKind != JsonValueKind.Array) throw new FormatException("Patch requires an entries array.");
            var entries = ImmutableArray.CreateBuilder<MaterialSwapEntryProposal>();
            foreach (var item in entriesValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each material-swap entry must be an object.");
                var entrySeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!entrySeen.Add(property.Name)) throw new FormatException("Material-swap entries may not contain duplicate properties.");
                    if (property.Name is not ("originalMaterial" or "replacementMaterial" or "colorRemapIndex" or "treeFolder")) throw new FormatException($"Material-swap entries contain unsupported property '{property.Name}'.");
                }
                entries.Add(new MaterialSwapEntryProposal(OptionalString(item, "originalMaterial") ?? string.Empty, OptionalString(item, "replacementMaterial") ?? string.Empty, OptionalDouble(item, "colorRemapIndex"), OptionalString(item, "treeFolder")));
            }
            request = new MaterialSwapProposalRequest(edition, new WorkspacePath(plugin), formId, mode,
                new MaterialSwapProposalPatch(OptionalEditorId(document.RootElement), OptionalString(document.RootElement, "treeFolder"), entries.ToImmutable()), new WorkspacePath(output), OptionalTargetFormId(document.RootElement));
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException) { message = exception.Message; return false; }
    }

    private static EditorId? OptionalEditorId(JsonElement root) { var value = OptionalString(root, "editorId"); return value is null ? null : new EditorId(value); }
    private static FormId? OptionalTargetFormId(JsonElement root) { var value = OptionalString(root, "targetFormId"); return value is null ? null : FormId.TryParse(value, out var formId) && formId.Value != 0 ? formId : throw new FormatException("Patch property 'targetFormId' must be a non-null hexadecimal FormID."); }
    private static string RequiredString(JsonElement root, string name) => OptionalString(root, name) ?? throw new FormatException($"Patch requires string property '{name}'.");
    private static string? OptionalString(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value)) return null; if (value.ValueKind != JsonValueKind.String) throw new FormatException($"Patch property '{name}' must be a string."); return value.GetString(); }
    private static double? OptionalDouble(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value)) return null; if (!value.TryGetDouble(out var parsed) || !double.IsFinite(parsed)) throw new FormatException($"Patch property '{name}' must be a finite number."); return parsed; }
    private static string ReadPatchText(string value)
    {
        if (!value.StartsWith('@')) return value;
        var path = new WorkspacePath(value[1..]);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0) throw new FormatException("A material-swap patch file must exist under the K-only workspace root and may not be a reparse point.");
        if (new FileInfo(path.Value).Length > 131_072) throw new FormatException("Material-swap patch JSON may not exceed 131072 bytes.");
        return File.ReadAllText(path.Value);
    }

    private CommandExitCode Usage(bool json, string message) { var response = new ErrorResponse("usage-error", message); error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}"); return CommandExitCode.UsageError; }
    private sealed record ErrorResponse(string Code, string Message);
    private sealed record Response(bool Written, string? ArtifactKind, string? Edition, MaterialSwapProposalMode? Mode, string? SourcePlugin, string? SourceFormId, string? EditorId, string? InputSha256, string? PatchSha256, ImmutableArray<MaterialSwapEntryArtifact> Entries, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static Response From(MaterialSwapProposalResult result) => new(result.Written, result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.Mode, result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId, result.Artifact?.EditorId, result.Artifact?.InputSha256, result.Artifact?.PatchSha256, result.Artifact?.Entries ?? [], result.Diagnostics);
    }
}
