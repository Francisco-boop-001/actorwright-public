using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGeomProviderBoundBuildCommandHandler(
    IFaceGeomProviderBoundBuildService service, TextWriter output, TextWriter error)
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
        if (!TryParseEdition(command, out var edition, out var editionError))
            return Usage(command.Json, editionError);
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
            return Usage(command.Json, "facegen build-geom-bound requires --data-root <copied-Data-root>.");
        if (!command.Options.TryGetValue("output-root", out var outputRoot))
            return Usage(command.Json, "facegen build-geom-bound requires --output-root <K-local-output-root>.");
        if (!command.Options.TryGetValue("npc", out var npcText) || !FormId.TryParse(npcText, out var npc))
            return Usage(command.Json, "facegen build-geom-bound requires --npc <hex-form-id>.");
        if (!command.Options.TryGetValue("plugins", out var pluginText) || string.IsNullOrWhiteSpace(pluginText))
            return Usage(command.Json, "facegen build-geom-bound requires --plugins PluginA.esp,PluginB.esp.");
        if (!command.Options.TryGetValue("morphs", out var morphText))
            return Usage(command.Json, "facegen build-geom-bound requires --morphs <JSON-array|@K-local-file>.");
        if (!FaceGeomBinaryBuildCommandHandler.TryReadMorphs(morphText, out var morphs, out var morphError))
            return Usage(command.Json, morphError);

        ImmutableArray<PluginName> plugins;
        try
        {
            var builder = ImmutableArray.CreateBuilder<PluginName>();
            foreach (var value in pluginText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                builder.Add(new PluginName(value));
            plugins = builder.ToImmutable();
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        FaceGeomProviderBoundBuildResult result;
        try
        {
            result = await service.BuildAsync(new FaceGeomProviderBoundBuildRequest(
                edition, new WorkspacePath(dataRoot), npc, plugins, new WorkspacePath(outputRoot), morphs),
                cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new BoundResponse(result.Written, result.Artifact, result.OutputSha256?.Value,
            result.Diagnostics);
        Write(response, command.Json,
            result.Written ? "facegen build-geom-bound: PASS" : "facegen build-geom-bound: REFUSED");
        if (result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        return result.Written ? CommandExitCode.Success : CommandExitCode.ValidationFailure;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { message = "facegen build-geom-bound requires --edition|--game fallout4|skyrimse."; return false; }
        message = string.Empty;
        return true;
    }

    private void Write<T>(T value, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(value, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record BoundResponse(bool Written, FaceGeomProviderBoundBuildArtifact? Artifact,
        string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
