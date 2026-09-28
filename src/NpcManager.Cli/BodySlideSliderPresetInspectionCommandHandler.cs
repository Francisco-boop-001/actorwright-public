using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

/// <summary>CLI adapter for read-only BodySlide SliderPreset XML inspection.</summary>
public sealed class BodySlideSliderPresetInspectionCommandHandler(
    IBodySlideSliderPresetInspectionService service,
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
        var editionValue = command.Options.GetValueOrDefault("edition") ??
                           command.Options.GetValueOrDefault("game");
        if (editionValue is null ||
            !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            return WriteUsageError(command.Json,
                "body sliders inspect-preset requires --game|--edition skyrimse.");
        }
        if (!command.Options.TryGetValue("preset-xml", out var presetXml))
        {
            return WriteUsageError(command.Json,
                "body sliders inspect-preset requires --preset-xml <K-local.xml>.");
        }

        try
        {
            BodySlideSliderPresetInspectionResult result =
                await service.InspectAsync(
                    new BodySlideSliderPresetInspectionRequest(
                        edition, new WorkspacePath(presetXml)),
                    cancellationToken).ConfigureAwait(false);
            var response = BodySlideSliderPresetInspectionResponse.From(result);
            Write(response, command.Json, response.IsValid
                ? $"BodySlide preset '{response.PresetName}' -> {response.SliderSet}, {response.Sliders.Length} sliders"
                : "BodySlide preset: REFUSED");
            return response.IsValid
                ? CommandExitCode.Success
                : ExitCodeForDiagnostics(result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return WriteUsageError(command.Json, exception.Message);
        }
    }

    private static CommandExitCode ExitCodeForDiagnostics(
        ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return CommandExitCode.Success;
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
        output.WriteLine(json
            ? JsonSerializer.Serialize(response, JsonOptions)
            : humanMessage);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record BodySlideSliderPresetInspectionResponse(
        string SchemaVersion,
        string Edition,
        string PresetXml,
        string? SourceSha256,
        string? PresetName,
        string? SliderSet,
        ImmutableArray<string> Groups,
        ImmutableArray<BodySlideSliderPresetRow> Sliders,
        ImmutableArray<Diagnostic> Diagnostics,
        bool IsValid)
    {
        public static BodySlideSliderPresetInspectionResponse From(
            BodySlideSliderPresetInspectionResult result) => new(
            "1",
            result.Edition.ToWireName(),
            result.PresetXml.Value,
            result.SourceSha256?.Value,
            result.PresetName,
            result.SliderSet,
            result.Groups,
            result.Sliders,
            result.Diagnostics,
            result.IsValid);
    }
}
