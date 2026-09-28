using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

/// <summary>CLI adapter for the bounded Skyrim _0/_1 body-weight resolver.</summary>
public sealed class SseBodyWeightResolutionCommandHandler(
    ISseBodyWeightResolutionService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return WriteUsageError(command.Json, "body weight resolve requires --game|--edition fallout4|skyrimse.");
        if (!command.Options.TryGetValue("input", out var input))
            return WriteUsageError(command.Json, "body weight resolve requires --input <manifest.json>.");

        try
        {
            var result = await service.ResolveAsync(new SseBodyWeightResolutionRequest(
                edition, new WorkspacePath(input)), cancellationToken);
            var response = SseBodyWeightResolutionResponse.From(result);
            Write(response, command.Json, response.IsValid
                ? $"body weight: {(response.Applied ? "APPLIED" : "NO-OP")} ({response.Deltas.Length} deltas)"
                : "body weight: REFUSED");
            return response.IsValid ? CommandExitCode.Success : ExitCodeForDiagnostics(result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return WriteUsageError(command.Json, exception.Message);
        }
    }

    private static CommandExitCode ExitCodeForDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }



    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) :
            $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record SseBodyWeightResolutionResponse(
        string SchemaVersion,
        string Edition,
        string Input,
        string? SourceSha256,
        bool SliderEnabled,
        bool Applied,
        SseBodyWeightGender? Gender,
        float? WeightPercent,
        float? ClampedWeight,
        int? BaseDigit,
        float? ChannelWeight,
        string? ChannelName,
        int BaseVertexCount,
        int TwinVertexCount,
        ImmutableArray<SseBodyWeightDelta> Deltas,
        ImmutableArray<Diagnostic> Diagnostics,
        bool IsValid)
    {
        public static SseBodyWeightResolutionResponse From(SseBodyWeightResolutionResult result) => new(
            "1", result.Edition.ToWireName(), result.InputPath.Value, result.SourceSha256?.Value,
            result.SliderEnabled, result.Applied, result.Gender, result.WeightPercent,
            result.ClampedWeight, result.BaseDigit, result.ChannelWeight, result.ChannelName,
            result.BaseVertexCount, result.TwinVertexCount, result.Deltas,
            result.Diagnostics, result.IsValid);
    }
}
