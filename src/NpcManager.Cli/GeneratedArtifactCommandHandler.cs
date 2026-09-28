using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

/// <summary>CLI adapter for the read-only generated-artifact inventory use case.</summary>
public sealed class GeneratedArtifactCommandHandler(
    IGeneratedArtifactScanService service, TextWriter output, TextWriter error)
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
            return WriteUsageError(command.Json, "workspace scan-generated requires --game|--edition fallout4|skyrimse.");
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
            return WriteUsageError(command.Json, "workspace scan-generated requires --data-root <path>.");

        try
        {
            var result = await service.ScanAsync(
                new GeneratedArtifactScanRequest(edition, new WorkspacePath(dataRoot)), cancellationToken);
            var response = GeneratedArtifactScanResponse.From(result);
            Write(response, command.Json,
                $"generated-scan: {response.Plugins.Length} plugins, {response.Sidecars.Length} sidecars");
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

    private sealed record GeneratedArtifactScanResponse(string SchemaVersion, string Edition, string MarkerAuthor,
        string DataRoot, bool IsValid, ImmutableArray<GeneratedPluginResponse> Plugins,
        ImmutableArray<GeneratedSidecarResponse> Sidecars, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static GeneratedArtifactScanResponse From(GeneratedArtifactScanResult result) => new(
            "1", result.Edition.ToWireName(), result.MarkerAuthor, result.DataRoot.Value, result.IsValid,
            result.Plugins.Select(GeneratedPluginResponse.From).ToImmutableArray(),
            result.Sidecars.Select(GeneratedSidecarResponse.From).ToImmutableArray(), result.Diagnostics);
    }

    private sealed record GeneratedPluginResponse(string Plugin, string Path, string Author, string? Sha256,
        bool ReadSucceeded, ImmutableArray<string> NpcFormIds)
    {
        public static GeneratedPluginResponse From(GeneratedPluginScanEntry entry) => new(
            entry.Plugin.Value, entry.Path.Value, entry.Author, entry.Sha256?.Value, entry.ReadSucceeded,
            entry.NpcFormIds.Select(formId => formId.ToString()).ToImmutableArray());
    }

    private sealed record GeneratedSidecarResponse(string OriginPlugin, string Kind, string Variant,
        string RelativePath, string Path, string? FormId, long Size, string Sha256)
    {
        public static GeneratedSidecarResponse From(GeneratedSidecarEntry entry) => new(
            entry.OriginPlugin.Value, entry.Kind.ToWireName(), entry.Variant.ToWireName(), entry.RelativePath.Value,
            entry.Path.Value, entry.FormId?.ToString(), entry.Size, entry.Sha256.Value);
    }
}
