using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenPluginBuildCommandHandler(IFaceGenPluginBuildService service, TextWriter output,
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
        if (!TryParseEdition(command, out var edition, out var editionError)) return Usage(command.Json, editionError);
        if (!command.Options.TryGetValue("manifest", out var manifestPath) &&
            !command.Options.TryGetValue("target", out manifestPath))
            return Usage(command.Json, "facegen build-plugin requires --manifest|--target <json>.");
        var pluginText = command.Options.GetValueOrDefault("plugin") ?? command.Options.GetValueOrDefault("target-plugin");
        if (pluginText is null) return Usage(command.Json, "facegen build-plugin requires --plugin|--target-plugin <name>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "facegen build-plugin requires --output <path>.");

        PluginName plugin;
        try { plugin = new PluginName(pluginText); }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        FaceGenPluginBuildResult result;
        try
        {
            result = await service.BuildAsync(new FaceGenPluginBuildRequest(edition, plugin,
                new WorkspacePath(manifestPath), new WorkspacePath(outputPath),
                StrictShapes: !command.Options.ContainsKey("allow-poison")), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new PluginResponse(result.Written, result.HasFailures, result.Artifact?.ArtifactKind,
            result.Artifact?.TargetPlugin, result.Artifact?.Attempted, result.Artifact?.Selected,
            result.Artifact?.Excluded, result.Artifact?.Passed, result.Artifact?.Skipped,
            result.Artifact?.Failed, result.OutputSha256?.Value, result.Diagnostics);
        var human = result.HasFailures ? "facegen build-plugin: FAILURES REPORTED" :
            result.Written ? "facegen build-plugin: PASS" : "facegen build-plugin: REFUSED";
        Write(response, command.Json, human);
        if (result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        return CommandExitCode.Success;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { message = "facegen build-plugin requires --edition|--game fallout4|skyrimse."; return false; }
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

    private sealed record PluginResponse(bool Written, bool HasFailures, string? ArtifactKind,
        string? TargetPlugin, int? Attempted, int? Selected, int? Excluded, int? Passed, int? Skipped,
        int? Failed, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
