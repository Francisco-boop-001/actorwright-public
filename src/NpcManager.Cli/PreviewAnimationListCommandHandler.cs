using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;

namespace NpcManager.Cli;

internal sealed class PreviewAnimationListCommandHandler(
    IPreviewAnimationListService service,
    TextWriter output,
    TextWriter error)
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
        if (!PreviewAnimationCommandOptions.TryParseRequest(command, "animation list", out var request, out var message))
            return Usage(command.Json, message);

        PreviewAnimationListResult result;
        try
        {
            result = await service.ListAsync(request, cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new PreviewAnimationListResponse(result.Succeeded, result.Artifact, result.Diagnostics);
        Write(response, command.Json, result.Succeeded
            ? $"animation list: PASS ({result.Artifact!.VisibleCount}/{result.Artifact.TotalCount} visible)"
            : "animation list: REFUSED");
        return result.Succeeded
            ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics);
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record PreviewAnimationListResponse(
        bool Succeeded,
        PreviewAnimationListArtifact? Artifact,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
