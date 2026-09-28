using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class LeveledListProposalCommandHandler(ILeveledListProposalService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage)) return Usage(command.Json, errorMessage);
        var result = await service.ProposeAsync(request, cancellationToken);
        var response = LeveledListResponse.From(result);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(response, JsonOptions) : result.Written ? "leveled-list propose: PASS" : "leveled-list propose: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out LeveledListProposalRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "leveled-list propose requires --edition fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("list", out var list) ||
            !command.Options.TryGetValue("entries", out var entries) || !command.Options.TryGetValue("output", out var output))
        { message = "leveled-list propose requires --plugin, --list, --entries, and --output."; return false; }
        if (!FormId.TryParse(list, out var formId) || formId.Value == 0)
        { message = "--list must be a non-null hexadecimal FormID."; return false; }
        try
        {
            var parsed = ParseEntries(entries);
            request = new LeveledListProposalRequest(edition, new WorkspacePath(plugin), formId,
                ParseEditorId(command.Options.GetValueOrDefault("editor-id")),
                ParseByte(command.Options.GetValueOrDefault("chance-none"), "--chance-none"),
                ParseByte(command.Options.GetValueOrDefault("max-count"), "--max-count"),
                ParseFlag(command.Options, "calc-all-levels"), ParseFlag(command.Options, "calc-each-in-count"),
                ParseFlag(command.Options, "use-all"), parsed, new WorkspacePath(output));
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        { message = exception.Message; return false; }
    }

    private static EditorId? ParseEditorId(string? value) => string.IsNullOrWhiteSpace(value) ? null : new EditorId(value);
    private static bool ParseFlag(ImmutableDictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var value)) return false;
        if (!bool.TryParse(value, out var parsed)) throw new ArgumentException($"--{key} must be true or false.");
        return parsed;
    }
    private static byte ParseByte(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (!byte.TryParse(value, out var parsed)) throw new ArgumentException($"{option} must be an unsigned byte.");
        return parsed;
    }

    private static ImmutableArray<LeveledListEntryProposal> ParseEntries(string value)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is 0 or > 4096)
            throw new ArgumentException("--entries must be a JSON array with 1 to 4096 objects.");
        var entries = ImmutableArray.CreateBuilder<LeveledListEntryProposal>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("item", out var itemValue) ||
                itemValue.ValueKind != JsonValueKind.String || !FormReference.TryParse(itemValue.GetString()!, out var item))
                throw new ArgumentException("Each leveled-list entry requires a valid item FormReference.");
            if (!element.TryGetProperty("level", out var levelValue) || !levelValue.TryGetUInt16(out var level) || level == 0 ||
                !element.TryGetProperty("count", out var countValue) || !countValue.TryGetUInt16(out var count) || count == 0 ||
                !element.TryGetProperty("chanceNone", out var chanceValue) || !chanceValue.TryGetByte(out var chance))
                throw new ArgumentException("Each leveled-list entry requires non-zero level/count and byte chanceNone.");
            entries.Add(new LeveledListEntryProposal(item, level, count, chance));
        }
        return entries.ToImmutable();
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record LeveledListResponse(bool Written, string? ArtifactKind, string? Edition, string? SourcePlugin,
        string? ListFormId, string? EditorId, byte? ChanceNone, byte? MaxCount, bool? CalculateAllLevels,
        bool? CalculateEachInCount, bool? UseAll, ImmutableArray<LeveledListEntryArtifact> Entries,
        ImmutableArray<string> MasterDependencies, bool? NoUnrelatedRecords, string? InputSha256,
        string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static LeveledListResponse From(LeveledListProposalResult result) => new(result.Written,
            result.Artifact?.ArtifactKind, result.Artifact?.Edition, result.Artifact?.SourcePlugin,
            result.Artifact?.ListFormId, result.Artifact?.EditorId, result.Artifact?.ChanceNone,
            result.Artifact?.MaxCount, result.Artifact?.CalculateAllLevels, result.Artifact?.CalculateEachInCount,
            result.Artifact?.UseAll, result.Artifact?.Entries ?? [], result.Artifact?.MasterDependencies ?? [],
            result.Artifact?.NoUnrelatedRecords, result.Artifact?.InputSha256, result.OutputSha256?.Value, result.Diagnostics);
    }
}
