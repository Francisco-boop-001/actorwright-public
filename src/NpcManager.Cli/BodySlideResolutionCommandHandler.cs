using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

/// <summary>CLI adapter for bounded, read-only BodySlide PIRT slider resolution.</summary>
public sealed class BodySlideResolutionCommandHandler(
    IBodySlideResolutionService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return WriteUsageError(command.Json, "body sliders resolve requires --game|--edition fallout4|skyrimse.");
        if (!command.Options.TryGetValue("tri", out var tri))
            return WriteUsageError(command.Json, "body sliders resolve requires --tri <path>.");
        if (!command.Options.TryGetValue("preset", out var preset))
            return WriteUsageError(command.Json, "body sliders resolve requires --preset <path>.");

        try
        {
            var result = await service.ResolveAsync(new BodySlideResolutionRequest(
                edition, new WorkspacePath(tri), new WorkspacePath(preset)), cancellationToken);
            var response = BodySlideResolutionResponse.From(result);
            Write(response, command.Json, response.IsValid
                ? $"body sliders: {response.Channels.Length} channels, {response.MissingSliders.Length} missing"
                : "body sliders: REFUSED");
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

    private sealed record BodySlideResolutionResponse(
        string SchemaVersion,
        string Edition,
        string Tri,
        string Preset,
        string? TriSha256,
        ImmutableArray<BodySlideTriShapeSummaryResponse> Shapes,
        ImmutableArray<BodySlideSliderValueResponse> Requested,
        ImmutableArray<BodySlideResolvedChannelResponse> Channels,
        ImmutableArray<string> MissingSliders,
        ImmutableArray<string> ExcludedSliders,
        ImmutableArray<Diagnostic> Diagnostics,
        bool IsValid)
    {
        public static BodySlideResolutionResponse From(BodySlideResolutionResult result) => new(
            "1", result.Edition.ToWireName(), result.TriPath.Value, result.PresetPath.Value,
            result.TriSha256?.Value,
            result.Shapes.Select(BodySlideTriShapeSummaryResponse.From).ToImmutableArray(),
            result.Requested.Select(value => new BodySlideSliderValueResponse(value.Name, value.Value)).ToImmutableArray(),
            result.Channels.Select(BodySlideResolvedChannelResponse.From).ToImmutableArray(),
            result.MissingSliders, result.ExcludedSliders, result.Diagnostics, result.IsValid);
    }

    private sealed record BodySlideTriShapeSummaryResponse(string Name, ImmutableArray<string> MorphNames)
    {
        public static BodySlideTriShapeSummaryResponse From(BodySlideTriShapeSummary value) =>
            new(value.Name, value.MorphNames);
    }

    private sealed record BodySlideSliderValueResponse(string Name, float Value);

    private sealed record BodySlideResolvedChannelResponse(
        string Shape,
        string Slider,
        BodySlideTriMorphType Type,
        float Weight,
        ImmutableArray<BodySlideTriOffset> Offsets)
    {
        public static BodySlideResolvedChannelResponse From(BodySlideResolvedChannel value) =>
            new(value.Shape, value.Slider, value.Type, value.Weight, value.Offsets);
    }
}
