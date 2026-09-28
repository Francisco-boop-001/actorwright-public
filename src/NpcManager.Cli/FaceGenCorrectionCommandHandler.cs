using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenCorrectionCommandHandler(IFaceGenCorrectionService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseEdition(command, out var edition, out var errorMessage)) return Usage(command.Json, errorMessage);
        if (!command.Options.TryGetValue("manifest", out var manifestPath))
            return Usage(command.Json, "facegen build requires --manifest <path>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "facegen build requires --output <path>.");
        var mode = command.Options.GetValueOrDefault("corrections");
        if (mode is not null && !mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Usage(command.Json, "--corrections currently supports only auto.");
        FormId? npc = null;
        if (command.Options.TryGetValue("npc", out var npcText))
        {
            if (!FormId.TryParse(npcText, out var parsed)) return Usage(command.Json, "--npc must be a hexadecimal FormID.");
            npc = parsed;
        }

        FaceGenCorrectionResult result;
        try
        {
            result = await service.BuildAsync(new FaceGenCorrectionRequest(edition,
                new WorkspacePath(manifestPath), new WorkspacePath(outputPath), npc,
                StrictShapes: !command.Options.ContainsKey("allow-poison")), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new BuildResponse(result.Written, result.Artifact?.ArtifactKind,
            result.Artifact?.Edition, result.Artifact?.NpcFormId, result.Artifact?.Corrections.Length,
            result.OutputSha256?.Value, result.Diagnostics);
        Write(response, command.Json, result.Written ? $"facegen build: PASS {outputPath}" : "facegen build: REFUSED");
        return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
            : CommandExitCode.Success;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { errorMessage = "facegen build requires --edition|--game fallout4|skyrimse."; return false; }
        errorMessage = string.Empty; return true;
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

    private sealed record BuildResponse(bool Written, string? ArtifactKind, string? Edition, string? NpcFormId,
        int? CorrectionCount, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
