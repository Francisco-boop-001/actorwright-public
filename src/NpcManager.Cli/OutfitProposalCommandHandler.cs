using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class OutfitProposalCommandHandler(IOutfitProposalService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return WriteUsageError(command.Json, errorMessage);
        var result = await service.ProposeAsync(request, cancellationToken);
        var response = OutfitProposalResponse.From(result);
        Write(response, command.Json, result.Written ? "outfit propose: PASS" : "outfit propose: REFUSED");
        return ExitCodeForDiagnostics(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command,
        out OutfitProposalRequest request, out string errorMessage)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "outfit propose requires --edition fallout4|skyrimse.";
            return false;
        }
        if (!command.Options.TryGetValue("plugin", out var pluginValue) ||
            !command.Options.TryGetValue("source", out var sourceValue) ||
            !command.Options.TryGetValue("items", out var itemsValue) ||
            !command.Options.TryGetValue("output", out var outputValue))
        {
            errorMessage = "outfit propose requires --plugin, --source, --items, and --output.";
            return false;
        }
        if (!TryWorkspacePath("plugin", pluginValue, out var pluginPath, out errorMessage) ||
            !TryWorkspacePath("output", outputValue, out var outputPath, out errorMessage))
            return false;
        if (!FormId.TryParse(sourceValue, out var sourceFormId) || sourceFormId.Value == 0)
        {
            errorMessage = "--source must be a non-null hexadecimal FormID.";
            return false;
        }
        if (!Enum.TryParse<OutfitProposalMode>(command.Options.GetValueOrDefault("mode"), true, out var mode))
        {
            errorMessage = "outfit propose requires --mode new|override.";
            return false;
        }
        FormId? targetFormId = null;
        if (command.Options.TryGetValue("target-form", out var targetValue) ||
            command.Options.TryGetValue("target-form-id", out targetValue))
        {
            if (!FormId.TryParse(targetValue, out var parsedTarget) || parsedTarget.Value == 0 || parsedTarget.Value > 0x00FF_FFFF)
            {
                errorMessage = "--target-form must be a nonzero plugin-local 24-bit hexadecimal FormID.";
                return false;
            }
            targetFormId = parsedTarget;
        }
        if (mode == OutfitProposalMode.New && targetFormId is null)
        {
            errorMessage = "outfit propose --mode new requires --target-form <plugin-local FormID>.";
            return false;
        }
        EditorId? editorId = null;
        if (command.Options.TryGetValue("editor-id", out var editorValue))
        {
            try { editorId = new EditorId(editorValue); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        ImmutableArray<FormReference> items;
        try { items = ParseItems(itemsValue); }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            errorMessage = exception.Message;
            return false;
        }
        FormReference? actorRace = null;
        if (command.Options.TryGetValue("actor-race", out var raceValue))
        {
            if (edition != GameEdition.SkyrimSpecialEdition || !FormReference.TryParse(raceValue, out var parsedRace) ||
                parsedRace.FormId.Value is 0 or > 0xFFFFFF)
            {
                errorMessage = "--actor-race requires Skyrim SE and a nonzero plugin-local 24-bit FormReference.";
                return false;
            }
            actorRace = parsedRace;
        }
        request = new OutfitProposalRequest(edition, pluginPath!.Value, sourceFormId, mode,
            editorId, items, outputPath!.Value, targetFormId, actorRace);
        errorMessage = string.Empty;
        return true;
    }

    internal static bool TryWorkspacePath(
        string option,
        string raw,
        out WorkspacePath? path,
        out string error)
    {
        try
        {
            path = new WorkspacePath(raw);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            path = null;
            error = $"Option --{option} must be one valid workspace path.";
            return false;
        }
        catch (PathTooLongException)
        {
            path = null;
            error = $"Option --{option} must be one valid workspace path.";
            return false;
        }
    }

    private static ImmutableArray<FormReference> ParseItems(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--items must not be empty.");
        var trimmed = value.TrimStart();
        var tokens = trimmed.Length > 0 && trimmed[0] == '['
            ? JsonSerializer.Deserialize<string[]>(value, JsonOptions) ?? throw new JsonException("--items JSON array is null.")
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is 0 or > 4096) throw new ArgumentException("--items must contain 1 to 4096 references.");
        var items = ImmutableArray.CreateBuilder<FormReference>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!FormReference.TryParse(token, out var item))
                throw new ArgumentException($"Invalid outfit item reference '{token}'.");
            items.Add(item);
        }
        return items.ToImmutable();
    }

    private static CommandExitCode ExitCodeForDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }



    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record OutfitProposalResponse(bool Written, string? ArtifactKind, string? Edition,
        string? Mode, string? SourcePlugin, string? SourceFormId, string? EditorId, string? InputSha256,
        ImmutableArray<string> Items, ImmutableArray<string> MasterDependencies, bool? NoUnrelatedRecords,
        string? TargetFormId, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static OutfitProposalResponse From(OutfitProposalResult result) => new(result.Written,
            result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.Mode.ToString(),
            result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId,
            result.Artifact?.EditorId, result.Artifact?.InputSha256,
            result.Artifact?.Items ?? [], result.Artifact?.MasterDependencies ?? [],
            result.Artifact?.NoUnrelatedRecords, result.Artifact?.TargetFormId,
            result.OutputSha256?.Value, result.Diagnostics);
    }
}
