using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimFaceMorphPatchCommandHandler(ISkyrimFaceMorphPatchService service, TextWriter output, TextWriter error)
{
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage)) return WriteUsageError(command.Json, errorMessage);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return WriteUsageError(command.Json, "--apply and --dry-run cannot be used together.");
        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        SkyrimFaceMorphPatchResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = FaceMorphResponse.From(proposal, request.Patch, result);
        var message = response.Applied ? "face morph patch: APPLIED" : response.IsApplicable ? "face morph patch: PROPOSAL" : response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "face morph patch: REFUSED" : "face morph patch: NO-OP";
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out SkyrimFaceMorphPatchRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) || !TryPath(command, "plugin", out var plugin, out errorMessage) || !TryPath(command, "output", out var output, out errorMessage) || !TryFormId(command, out var formId, out errorMessage) || !TryPatch(command, out var patch, out errorMessage)) return false;
        Sha256Hash? hash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText)) { try { hash = new Sha256Hash(hashText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; } }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText)) { try { proposal = new WorkspacePath(proposalText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; } }
        request = new SkyrimFaceMorphPatchRequest(edition, plugin, output, formId, patch, hash, command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty; return true;
    }

    private static bool TryPatch(ParsedCommand command, out SkyrimFaceMorphPatch patch, out string errorMessage)
    {
        patch = default!;
        var text = command.Options.GetValueOrDefault("vanilla") ?? command.Options.GetValueOrDefault("morphs");
        if (text is null) { errorMessage = "The command requires --vanilla JSON or --vanilla @K:\\...\\morphs.json."; return false; }
        try
        {
            if (text.StartsWith('@'))
            {
                var path = new WorkspacePath(text[1..]); WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
                if (!path.IsUnder(root) || !File.Exists(path.Value)) throw new FormatException("A vanilla morph file must exist under the K-only workspace root.");
                if (HasReparsePath(path.Value)) throw new FormatException("A vanilla morph file may not traverse a reparse point.");
                text = File.ReadAllText(path.Value);
            }
            if (text.Length > 1_048_576) throw new FormatException("The vanilla morph JSON exceeds the 1 MiB safety limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--vanilla must be a JSON object.");
            var fields = document.RootElement.EnumerateObject().ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields) if (!names.Add(field.Name)) throw new FormatException($"The vanilla morph document contains duplicate field '{field.Name}'.");
            var allowed = new HashSet<string>(["nam9", "nam9Trailing", "nama"], StringComparer.Ordinal);
            var unknown = fields.Select(field => field.Name).FirstOrDefault(name => !allowed.Contains(name)); if (unknown is not null) throw new FormatException($"The vanilla morph document contains unknown field '{unknown}'.");
            var nam9Element = Required(document.RootElement, "nam9"); var namaElement = Required(document.RootElement, "nama");
            if (nam9Element.ValueKind != JsonValueKind.Array || namaElement.ValueKind != JsonValueKind.Array) throw new FormatException("nam9 and nama must be JSON arrays.");
            var nam9 = ImmutableArray.CreateBuilder<float>(); foreach (var value in nam9Element.EnumerateArray()) { if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var parsed)) throw new FormatException("Every nam9 entry must be a finite JSON number."); nam9.Add(parsed); }
            var nama = ImmutableArray.CreateBuilder<uint>(); foreach (var value in namaElement.EnumerateArray()) { if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var parsed)) throw new FormatException("Every nama entry must be an unsigned 32-bit integer."); nama.Add(parsed); }
            var trailingElement = Required(document.RootElement, "nam9Trailing"); if (trailingElement.ValueKind != JsonValueKind.Number || !trailingElement.TryGetSingle(out var trailing)) throw new FormatException("nam9Trailing must be a finite JSON number.");
            patch = new SkyrimFaceMorphPatch(nam9.ToImmutable(), trailing, nama.ToImmutable()); errorMessage = string.Empty; return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException) { errorMessage = exception.Message; return false; }
    }

    private static JsonElement Required(JsonElement objectElement, string name) => objectElement.TryGetProperty(name, out var value) ? value : throw new FormatException($"The vanilla morph document requires '{name}'.");
    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage) { edition = default; var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition"); if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition)) { errorMessage = "The command requires --game|--edition fallout4|skyrimse."; return false; } errorMessage = string.Empty; return true; }
    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage) { path = default; if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; } try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; } catch (ArgumentException exception) { errorMessage = exception.Message; return false; } }
    private static bool TryFormId(ParsedCommand command, out FormId formId, out string errorMessage) { formId = default; var value = command.Options.GetValueOrDefault("form-id") ?? command.Options.GetValueOrDefault("npc"); if (value is null || !FormId.TryParse(value, out formId)) { errorMessage = "The command requires --npc|--form-id with a hexadecimal FormID."; return false; } errorMessage = string.Empty; return true; }
    private CommandExitCode WriteUsageError(bool json, string message) { if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message })); else error.WriteLine($"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? (DiagnosticExitCodeClassifier.Classify(diagnostics)) : CommandExitCode.Success;
    private static bool HasReparsePath(string path) { var current = Path.GetFullPath(path); while (!string.IsNullOrEmpty(current)) { if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true; var parent = Directory.GetParent(current)?.FullName; if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break; current = parent ?? string.Empty; } return false; }
    private sealed record FaceMorphResponse(bool IsApplicable, bool Applied, string Edition, string Plugin, string Output, string FormId, Sha256Hash InputSha256, Sha256Hash? OutputSha256, SkyrimFaceMorphPatch Patch, ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    { public static FaceMorphResponse From(SkyrimFaceMorphPatchProposal proposal, SkyrimFaceMorphPatch patch, SkyrimFaceMorphPatchResult? result) => new(proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.InputPlugin.Value, proposal.OutputPlugin.Value, proposal.TargetFormId.ToString(), proposal.InputHash, result?.OutputHash, patch, proposal.Changes, proposal.PreservedFields, result?.Diagnostics ?? proposal.Diagnostics); }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
