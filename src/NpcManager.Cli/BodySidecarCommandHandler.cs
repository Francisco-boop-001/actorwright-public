using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

/// <summary>CLI adapter for the read-only, typed BodySlide sidecar inspector.</summary>
public sealed class BodySidecarCommandHandler(
    IBodySidecarInspectionService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return WriteUsageError(command.Json, "body sidecar inspect requires --game|--edition fallout4|skyrimse.");
        if (!command.Options.TryGetValue("file", out var file))
            return WriteUsageError(command.Json, "body sidecar inspect requires --file <path>.");

        try
        {
            var result = await service.InspectAsync(new BodySidecarInspectRequest(edition,
                new WorkspacePath(file)), cancellationToken);
            var response = BodySidecarInspectResponse.From(result);
            Write(response, command.Json, response.IsValid
                ? $"body sidecar: {response.Plugin} ({response.Npcs.Length} NPC entries)"
                : "body sidecar: REFUSED");
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
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage) =>
        output.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : humanMessage);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record BodySidecarInspectResponse(string SchemaVersion, string Edition, string File,
        bool IsValid, int? Version, string? Plugin, string? SourceSha256, string? CanonicalSha256,
        bool RoundTripPreserved, ImmutableArray<BodySidecarNpcResponse> Npcs,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static BodySidecarInspectResponse From(BodySidecarInspectResult result)
        {
            var document = result.Document;
            return new BodySidecarInspectResponse("1", result.Edition.ToWireName(), result.File.Value, result.IsValid,
                document?.Version, document?.Plugin.Value, document?.SourceSha256.Value, document?.CanonicalSha256.Value,
                document?.RoundTripPreserved ?? false,
                document?.Npcs.Select(BodySidecarNpcResponse.From).ToImmutableArray() ?? [], result.Diagnostics);
        }
    }

    private sealed record BodySidecarNpcResponse(string Identifier, string? EditorId,
        ImmutableArray<BodyMorphResponse> BodyMorphs, ImmutableArray<BodyMorphKeyedResponse> BodyMorphsKeyed,
        string? SkinTemplateId, string? Gender, int OverlayCount, int SseBodyOverlayCount,
        int SseNodeTransformCount, int SseSkinOverrideCount, int SseCustomMorphCount,
        int SseSculptVertexCount, int SseSculptPartCount, int SseTintTextureCount)
    {
        public static BodySidecarNpcResponse From(BodySidecarNpcSummary value) => new(value.Identifier, value.EditorId,
            value.BodyMorphs.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new BodyMorphResponse(item.Key, item.Value)).ToImmutableArray(),
            value.BodyMorphsKeyed.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new BodyMorphKeyedResponse(item.Key,
                    item.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new BodyMorphResponse(pair.Key, pair.Value)).ToImmutableArray()))
                .ToImmutableArray(), value.SkinTemplateId, value.Gender, value.OverlayCount,
            value.SseBodyOverlayCount, value.SseNodeTransformCount, value.SseSkinOverrideCount,
            value.SseCustomMorphCount, value.SseSculptVertexCount, value.SseSculptPartCount,
            value.SseTintTextureCount);
    }

    private sealed record BodyMorphResponse(string Name, float Value);

    private sealed record BodyMorphKeyedResponse(string Name, ImmutableArray<BodyMorphResponse> Keys);
}
