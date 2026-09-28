using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RaceMenuExtendedMorphPatchCommandHandler(IRaceMenuExtendedMorphPatchService service, TextWriter output, TextWriter error)
{
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage)) return Usage(command.Json, errorMessage);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return Usage(command.Json, "--apply and --dry-run cannot be used together.");
        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        RaceMenuExtendedMorphPatchResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = ExtendedMorphResponse.From(proposal, request.Patch, result);
        var message = response.Applied ? "face morph extended: APPLIED" : response.IsApplicable ? "face morph extended: PROPOSAL" : response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "face morph extended: REFUSED" : "face morph extended: NO-OP";
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out RaceMenuExtendedMorphPatchRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) || !TryPath(command, "input", out var input, out errorMessage) ||
            !TryPath(command, "output", out var output, out errorMessage) || !TryPatch(command, out var patch, out errorMessage)) return false;
        Sha256Hash? hash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText)) { try { hash = new Sha256Hash(hashText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; } }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText)) { try { proposal = new WorkspacePath(proposalText); } catch (ArgumentException exception) { errorMessage = exception.Message; return false; } }
        request = new RaceMenuExtendedMorphPatchRequest(edition, input, output, patch, hash,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty; return true;
    }

    private static bool TryPatch(ParsedCommand command, out RaceMenuExtendedMorphPatch patch, out string errorMessage)
    {
        patch = default!;
        var text = command.Options.GetValueOrDefault("extended");
        if (text is null) { errorMessage = "The command requires --extended JSON or --extended @K:\\...\\morphs.json."; return false; }
        try
        {
            if (text.StartsWith('@'))
            {
                var path = new WorkspacePath(text[1..]); WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
                if (!path.IsUnder(root) || !File.Exists(path.Value)) throw new FormatException("An extended morph file must exist under the K-only workspace root.");
                if (HasReparsePath(path.Value)) throw new FormatException("An extended morph file may not traverse a reparse point.");
                text = File.ReadAllText(path.Value);
            }
            if (text.Length > 1_048_576) throw new FormatException("The extended morph JSON exceeds the 1 MiB safety limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("morphs", out var morphs) || morphs.ValueKind != JsonValueKind.Array)
                throw new FormatException("--extended must be an object containing a morphs array.");
            var fields = document.RootElement.EnumerateObject().ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields) if (!names.Add(field.Name)) throw new FormatException($"The extended morph document contains duplicate field '{field.Name}'.");
            if (fields.Any(field => !string.Equals(field.Name, "morphs", StringComparison.Ordinal))) throw new FormatException("The extended morph document contains unknown fields.");
            var values = ImmutableArray.CreateBuilder<RaceMenuExtendedMorph>();
            foreach (var item in morphs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var parsed))
                    throw new FormatException("Every extended morph entry needs a string name and finite numeric value.");
                values.Add(new RaceMenuExtendedMorph(name.GetString()!, parsed));
            }
            patch = new RaceMenuExtendedMorphPatch(values.ToImmutable()); errorMessage = string.Empty; return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { errorMessage = exception.Message; return false; }
    }

    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage) { edition = default; var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition"); if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition)) { errorMessage = "The command requires --game|--edition skyrimse."; return false; } errorMessage = string.Empty; return true; }
    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage) { path = default; if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; } try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; } catch (ArgumentException exception) { errorMessage = exception.Message; return false; } }
    private CommandExitCode Usage(bool json, string message) { if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message })); else error.WriteLine($"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? (DiagnosticExitCodeClassifier.Classify(diagnostics)) : CommandExitCode.Success;
    private static bool HasReparsePath(string path) { var current = Path.GetFullPath(path); while (!string.IsNullOrEmpty(current)) { if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true; var parent = Directory.GetParent(current)?.FullName; if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break; current = parent ?? string.Empty; } return false; }
    private sealed record ExtendedMorphResponse(bool IsApplicable, bool Applied, string Edition, string Input, string Output, Sha256Hash InputSha256, Sha256Hash? OutputSha256, RaceMenuExtendedMorphPatch Patch, ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    { public static ExtendedMorphResponse From(RaceMenuExtendedMorphPatchProposal proposal, RaceMenuExtendedMorphPatch patch, RaceMenuExtendedMorphPatchResult? result) => new(proposal.IsApplicable, result?.Applied == true, proposal.Edition.ToWireName(), proposal.InputPreset.Value, proposal.OutputPreset.Value, proposal.InputHash, result?.OutputHash, patch, proposal.Changes, proposal.PreservedFields, result?.Diagnostics ?? proposal.Diagnostics); }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
