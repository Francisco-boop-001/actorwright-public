using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimFaceTintPatchCommandHandler(ISkyrimFaceTintPatchService service, TextWriter output,
    TextWriter error, Func<ILocalOperationJournal> journalFactory)
{
    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        if (!TryBuildRequest(command, out var request, out var errorMessage, out var errorOption))
            return await WriteUsageErrorAsync(command, errorMessage, errorOption, elapsed.ElapsedMilliseconds, cancellationToken);
        var apply = command.Options.TryGetValue("apply", out var applyValue) && IsTrue(applyValue);
        var dryRun = command.Options.TryGetValue("dry-run", out var dryRunValue) && IsTrue(dryRunValue);
        if (apply && dryRun)
            return await WriteUsageErrorAsync(command, "--apply and --dry-run cannot be used together.",
                "apply", elapsed.ElapsedMilliseconds, cancellationToken);
        var proposal = await service.AnalyzeAsync(request with { DryRun = false }, cancellationToken);
        SkyrimFaceTintPatchResult? result = null;
        if (apply) result = await service.ApplyAsync(request with { DryRun = false }, proposal, cancellationToken);
        var response = FaceTintResponse.From(proposal, request.Patch, result);
        var message = response.Applied ? "face tint patch: APPLIED" : response.IsApplicable ? "face tint patch: PROPOSAL" :
            response.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? "face tint patch: REFUSED" :
            "face tint patch: NO-OP";
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(message);
        return ExitFor(response.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out SkyrimFaceTintPatchRequest request,
        out string errorMessage, out string errorOption)
    {
        request = default!;
        errorOption = command.Options.ContainsKey("game") ? "game" : "edition";
        if (!TryEdition(command, out var edition, out errorMessage)) return false;
        if (edition != GameEdition.SkyrimSpecialEdition)
        {
            errorMessage = "The Skyrim face-tint command requires --game skyrimse.";
            return false;
        }
        errorOption = "plugin";
        if (!TryPath(command, "plugin", out var plugin, out errorMessage)) return false;
        errorOption = "output";
        if (!TryPath(command, "output", out var output, out errorMessage)) return false;
        errorOption = command.Options.ContainsKey("form-id") ? "form-id" : "npc";
        if (!TryFormId(command, out var formId, out errorMessage)) return false;
        errorOption = "layers";
        if (!TryLayers(command, out var layers, out errorMessage)) return false;
        Sha256Hash? hash = null;
        if (command.Options.TryGetValue("expected-sha256", out var hashText))
        {
            errorOption = "expected-sha256";
            try { hash = new Sha256Hash(hashText); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        WorkspacePath? proposal = null;
        if (command.Options.TryGetValue("proposal", out var proposalText))
        {
            errorOption = "proposal";
            try { proposal = new WorkspacePath(proposalText); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        request = new SkyrimFaceTintPatchRequest(edition, plugin, output, formId,
            new SkyrimFaceTintPatch(layers), hash,
            command.Options.TryGetValue("dry-run", out var dryRun) && IsTrue(dryRun), proposal);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryLayers(ParsedCommand command, out ImmutableArray<SkyrimFaceTintLayer> layers,
        out string errorMessage)
    {
        layers = [];
        if (!command.Options.TryGetValue("layers", out var text))
        {
            errorMessage = "The command requires --layers JSON or --layers @K:\\...\\layers.json.";
            return false;
        }
        try
        {
            if (text.StartsWith('@'))
            {
                var sourcePath = new WorkspacePath(text[1..]);
                WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
                if (!sourcePath.IsUnder(root) || !File.Exists(sourcePath.Value))
                    throw new FormatException("A layers file must exist under the K-only workspace root.");
                if (HasReparsePath(sourcePath.Value))
                    throw new FormatException("A layers file may not traverse a reparse point.");
                text = File.ReadAllText(sourcePath.Value);
            }
            if (text.Length > 1_048_576) throw new FormatException("The layers JSON exceeds the 1 MiB safety limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 16
            });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("--layers must be a JSON array.");
            var builder = ImmutableArray.CreateBuilder<SkyrimFaceTintLayer>();
            foreach (var (item, index) in document.RootElement.EnumerateArray().Select((value, position) => (value, position)))
            {
                if (index >= 256) throw new FormatException("A Skyrim face-tint patch may contain at most 256 layers.");
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"Layer {index} must be a JSON object.");
                var fields = item.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in fields) if (!names.Add(field.Name)) throw new FormatException($"Layer {index} contains duplicate field '{field.Name}'.");
                var allowed = new HashSet<string>(["index", "red", "green", "blue", "alpha", "coverage", "presetIndex"], StringComparer.Ordinal);
                var unknown = fields.Select(field => field.Name).FirstOrDefault(name => !allowed.Contains(name));
                if (unknown is not null)
                {
                    if (string.Equals(unknown, "texture", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException("texture is not persisted by an NPC record; use a RaceMenu tint sidecar for custom warpaint paths.");
                    throw new FormatException($"Layer {index} contains unknown field '{unknown}'.");
                }
                builder.Add(new SkyrimFaceTintLayer(
                    RequiredUInt16(item, "index", index),
                    RequiredByte(item, "red", index),
                    RequiredByte(item, "green", index),
                    RequiredByte(item, "blue", index),
                    RequiredByte(item, "alpha", index),
                    RequiredUInt32(item, "coverage", index),
                    RequiredInt16(item, "presetIndex", index)));
            }
            layers = builder.ToImmutable();
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static ushort RequiredUInt16(JsonElement element, string name, int index) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt16(out var parsed)
            ? parsed : throw new FormatException($"Layer {index} requires unsigned 16-bit '{name}'.");

    private static uint RequiredUInt32(JsonElement element, string name, int index) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var parsed)
            ? parsed : throw new FormatException($"Layer {index} requires unsigned 32-bit '{name}'.");

    private static short RequiredInt16(JsonElement element, string name, int index) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt16(out var parsed)
            ? parsed : throw new FormatException($"Layer {index} requires signed 16-bit '{name}'.");

    private static byte RequiredByte(JsonElement element, string name, int index) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetByte(out var parsed)
            ? parsed : throw new FormatException($"Layer {index} requires byte '{name}'.");

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

    private static bool TryFormId(ParsedCommand command, out FormId formId, out string errorMessage)
    {
        formId = default;
        var value = command.Options.GetValueOrDefault("form-id") ?? command.Options.GetValueOrDefault("npc");
        if (value is null || !FormId.TryParse(value, out formId))
        { errorMessage = "The command requires --npc|--form-id with a hexadecimal FormID."; return false; }
        errorMessage = string.Empty;
        return true;
    }

    private async ValueTask<CommandExitCode> WriteUsageErrorAsync(ParsedCommand command, string message,
        string option, long durationMilliseconds, CancellationToken cancellationToken)
    {
        var diagnostic = new Diagnostic("usage-error", DiagnosticSeverity.Error, message)
        {
            Recovery = new DiagnosticRecovery(RecoveryAction.CorrectInput, option, null, message, false)
        };
        OperationJournalAppendResult append;
        try
        {
            append = await journalFactory().AppendAsync(new OperationJournalRecord(
                command.Name, ProtocolRequestDigest.Compute(command),
                [ProtocolEffect.Create(AgentEffectKind.AppendLocalOperationJournal,
                    ApplicationEffectStatus.Attempted, ApplicationEffectScope.WorkspaceLocalJournal)],
                [diagnostic.Code], ["usage"], [], durationMilliseconds, "refused", (int)CommandExitCode.UsageError),
                cancellationToken);
        }
        catch (Exception)
        {
            append = new OperationJournalAppendResult(false, null,
                new Diagnostic("operation-journal-write-failed", DiagnosticSeverity.Warning,
                    "The local operation journal could not append a redacted record."));
        }
        if (command.Json)
            error.WriteLine(JsonSerializer.Serialize(new
            {
                diagnostic.Code, diagnostic.Message, diagnostic.Severity, diagnostic.Recovery,
                journal = new { append.Appended, append.Warning }
            }, JsonOptions));
        else
        {
            error.WriteLine($"ERROR usage-error: {message}");
            if (append.Warning is { } warning)
                error.WriteLine($"WARNING {warning.Code}: {warning.Message}");
        }
        return CommandExitCode.UsageError;
    }

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static CommandExitCode ExitFor(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? DiagnosticExitCodeClassifier.Classify(diagnostics)
            : CommandExitCode.Success;

    private sealed record FaceTintResponse(bool IsApplicable, bool Applied, string Edition, string Plugin, string Output,
        string FormId, Sha256Hash InputSha256, Sha256Hash? OutputSha256, ImmutableArray<SkyrimFaceTintLayer> Layers,
        ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static FaceTintResponse From(SkyrimFaceTintPatchProposal proposal, SkyrimFaceTintPatch patch,
            SkyrimFaceTintPatchResult? result) => new(proposal.IsApplicable, result?.Applied == true,
            proposal.Edition.ToWireName(), proposal.InputPlugin.Value, proposal.OutputPlugin.Value,
            proposal.TargetFormId.ToString(), proposal.InputHash, result?.OutputHash, patch.Layers,
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
