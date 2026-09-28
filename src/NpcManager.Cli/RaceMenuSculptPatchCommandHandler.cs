using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RaceMenuSculptPatchCommandHandler(IRaceMenuSculptPatchService service, TextWriter output, TextWriter error)
{
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage)) return Usage(command.Json, errorMessage);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun) return Usage(command.Json, "--apply and --dry-run cannot be used together.");
        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        RaceMenuSculptPatchResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = SculptResponse.From(proposal, request.Patch, result);
        var message = response.Applied ? "face sculpt: APPLIED" : response.IsApplicable ? "face sculpt: PROPOSAL" :
            response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "face sculpt: REFUSED" : "face sculpt: NO-OP";
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out RaceMenuSculptPatchRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "input", out var input, out errorMessage) ||
            !TryPath(command, "output", out var output, out errorMessage) ||
            !TryPatch(command, out var patch, out errorMessage)) return false;
        Sha256Hash? hash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText))
        {
            try { hash = new Sha256Hash(hashText); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText))
        {
            try { proposal = new WorkspacePath(proposalText); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        request = new RaceMenuSculptPatchRequest(edition, input, output, patch, hash,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryPatch(ParsedCommand command, out RaceMenuSculptPatch patch, out string errorMessage)
    {
        patch = default!;
        var text = command.Options.GetValueOrDefault("sculpt");
        if (text is null) { errorMessage = "The command requires --sculpt JSON or --sculpt @K:\\...\\sculpt.json."; return false; }
        try
        {
            if (text.StartsWith('@'))
            {
                var path = new WorkspacePath(text[1..]);
                WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
                if (!path.IsUnder(root) || !File.Exists(path.Value))
                    throw new FormatException("A sculpt file must exist under the K-only workspace root.");
                if (HasReparsePath(path.Value)) throw new FormatException("A sculpt file may not traverse a reparse point.");
                text = File.ReadAllText(path.Value);
            }
            if (text.Length > 16 * 1024 * 1024) throw new FormatException("The sculpt JSON exceeds the 16 MiB safety limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 16
            });
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--sculpt must be an object.");
            RejectUnknown(rootElement, ["divisor", "parts"], "--sculpt");
            var divisor = ReadInt32(rootElement, "divisor", "--sculpt.divisor");
            if (!rootElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                throw new FormatException("--sculpt.parts must be an array.");
            var partBuilder = ImmutableArray.CreateBuilder<RaceMenuSculptPart>();
            foreach (var (part, partIndex) in parts.EnumerateArray().Select((value, index) => (value, index)))
            {
                var path = $"--sculpt.parts[{partIndex}]";
                if (part.ValueKind != JsonValueKind.Object) throw new FormatException($"{path} must be an object.");
                RejectUnknown(part, ["host", "vertices", "verts"], path);
                if (!part.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String)
                    throw new FormatException($"{path}.host must be a string.");
                var vertexCount = ReadInt64(part, "vertices", $"{path}.vertices");
                if (!part.TryGetProperty("verts", out var verts) || verts.ValueKind != JsonValueKind.Array)
                    throw new FormatException($"{path}.verts must be an array.");
                var vertices = ImmutableArray.CreateBuilder<RaceMenuSculptVertex>();
                foreach (var (vertex, vertexIndex) in verts.EnumerateArray().Select((value, index) => (value, index)))
                {
                    var vertexPath = $"{path}.verts[{vertexIndex}]";
                    if (vertex.ValueKind != JsonValueKind.Object) throw new FormatException($"{vertexPath} must be an object.");
                    RejectUnknown(vertex, ["index", "dx", "dy", "dz"], vertexPath);
                    var index = ReadInt32(vertex, "index", $"{vertexPath}.index");
                    var dx = ReadFloat(vertex, "dx", $"{vertexPath}.dx");
                    var dy = ReadFloat(vertex, "dy", $"{vertexPath}.dy");
                    var dz = ReadFloat(vertex, "dz", $"{vertexPath}.dz");
                    vertices.Add(new RaceMenuSculptVertex(index, dx, dy, dz));
                }
                partBuilder.Add(new RaceMenuSculptPart(host.GetString()!, vertexCount, vertices.ToImmutable()));
            }
            patch = new RaceMenuSculptPatch(divisor, partBuilder.ToImmutable());
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static int ReadInt32(JsonElement value, string property, string path)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var result))
            throw new FormatException($"{path} must be a signed 32-bit integer.");
        return result;
    }

    private static long ReadInt64(JsonElement value, string property, string path)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var result))
            throw new FormatException($"{path} must be a signed 64-bit integer.");
        return result;
    }

    private static float ReadFloat(JsonElement value, string property, string path)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var result) || !float.IsFinite(result))
            throw new FormatException($"{path} must be a finite number.");
        return result;
    }

    private static void RejectUnknown(JsonElement value, string[] known, string path)
    {
        var allowed = new HashSet<string>(known, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new FormatException($"{path} contains unknown field '{property.Name}'.");
    }

    private static bool TryEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        {
            errorMessage = "The command requires --game|--edition skyrimse.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; }
        try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; }
        catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }

    private CommandExitCode Usage(bool json, string message)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new { code = "usage-error", message }));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
        ? DiagnosticExitCodeClassifier.Classify(diagnostics)
        : CommandExitCode.Success;

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

    private sealed record SculptResponse(bool IsApplicable, bool Applied, string Edition, string Input, string Output,
        Sha256Hash InputSha256, Sha256Hash? OutputSha256, RaceMenuSculptPatch Patch,
        ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static SculptResponse From(RaceMenuSculptPatchProposal proposal, RaceMenuSculptPatch patch,
            RaceMenuSculptPatchResult? result) => new(proposal.IsApplicable, result?.Applied == true,
                proposal.Edition.ToWireName(), proposal.InputPreset.Value, proposal.OutputPreset.Value,
                proposal.InputHash, result?.OutputHash, patch, proposal.Changes, proposal.PreservedFields,
                result?.Diagnostics ?? proposal.Diagnostics);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
