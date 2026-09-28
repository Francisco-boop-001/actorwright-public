using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenBatchCommandHandler(IFaceGenBatchService service, TextWriter output, TextWriter error)
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
        if (!command.Options.TryGetValue("manifests", out var manifestsPath) &&
            !command.Options.TryGetValue("batch", out manifestsPath))
            return Usage(command.Json, "facegen bake-all requires --manifests|--batch <json>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "facegen bake-all requires --output <path>.");

        FaceGenBatchResult result;
        try
        {
            result = await service.BuildAsync(new FaceGenBatchRequest(edition,
                new WorkspacePath(manifestsPath), new WorkspacePath(outputPath),
                StrictShapes: !command.Options.ContainsKey("allow-poison")), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new BatchResponse(result.Written, result.HasFailures, result.Artifact?.ArtifactKind,
            result.Artifact?.Attempted, result.Artifact?.Passed, result.Artifact?.Skipped,
            result.Artifact?.Failed, result.OutputSha256?.Value, result.Diagnostics);
        var human = result.HasFailures ? "facegen bake-all: FAILURES REPORTED" :
            result.Written ? "facegen bake-all: PASS" : "facegen bake-all: REFUSED";
        Write(response, command.Json, human);
        if (result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        return CommandExitCode.Success;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { errorMessage = "facegen bake-all requires --edition|--game fallout4|skyrimse."; return false; }
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

    private sealed record BatchResponse(bool Written, bool HasFailures, string? ArtifactKind, int? Attempted,
        int? Passed, int? Skipped, int? Failed, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
