using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PreviewRerollCommandHandler(IPreviewRerollService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryParseEdition(command, out var edition, out var editionError)) return Usage(command.Json, editionError);
        if (!command.Options.TryGetValue("manifest", out var manifestPath))
            return Usage(command.Json, "preview reroll requires --manifest <json>.");
        if (!command.Options.TryGetValue("npc", out var npcText) || !FormId.TryParse(npcText, out var npcFormId))
            return Usage(command.Json, "preview reroll requires --npc <hexadecimal FormID>.");
        if (!command.Options.TryGetValue("seed", out var seedText) ||
            !long.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
            return Usage(command.Json, "preview reroll requires --seed <signed 64-bit integer>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "preview reroll requires --output <path>.");

        PreviewRerollResult result;
        try
        {
            result = await service.RerollAsync(new PreviewRerollRequest(edition,
                new WorkspacePath(manifestPath), new WorkspacePath(outputPath), npcFormId, seed), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new PreviewRerollResponse(result.Written, result.Artifact?.ArtifactKind,
            result.Artifact?.NpcFormId, result.Artifact?.Seed, result.Artifact?.CandidateCount,
            result.Artifact?.EligibleCount, result.Artifact?.SelectedVariantId,
            result.Artifact?.SelectedOutfit, result.Artifact?.SelectedIndex,
            result.OutputSha256?.Value, result.Diagnostics);
        Write(response, command.Json, result.Written ? "preview reroll: PASS" : "preview reroll: REFUSED");
        return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
            : CommandExitCode.Success;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { message = "preview reroll requires --edition|--game fallout4|skyrimse."; return false; }
        message = string.Empty;
        return true;
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record PreviewRerollResponse(bool Written, string? ArtifactKind, string? NpcFormId,
        long? Seed, int? CandidateCount, int? EligibleCount, string? SelectedVariantId,
        string? SelectedOutfit, int? SelectedIndex, string? OutputSha256,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
