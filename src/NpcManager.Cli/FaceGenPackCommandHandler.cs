using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenPackCommandHandler(
    IFaceGenPackService service, TextWriter output, TextWriter error)
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
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return Usage(command.Json, "facegen pack requires --edition|--game fallout4|skyrimse.");
        if (!command.Options.TryGetValue("data-root", out var dataRoot) ||
            !command.Options.TryGetValue("output-root", out var outputRoot))
            return Usage(command.Json, "facegen pack requires --data-root and --output-root.");
        if (!command.Options.TryGetValue("npc", out var npcValue) || !FormId.TryParse(npcValue, out var npcFormId))
            return Usage(command.Json, "facegen pack requires --npc <hex-form-id>.");
        var pluginValue = command.Options.GetValueOrDefault("plugins");
        if (string.IsNullOrWhiteSpace(pluginValue))
            return Usage(command.Json, "facegen pack requires --plugins PluginA.esp,PluginB.esp in load order.");
        if (!command.Options.TryGetValue("anchor-plugin", out var anchorValue))
            return Usage(command.Json, "facegen pack requires --anchor-plugin SavePlugin.esp.");

        try
        {
            var plugins = pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => new PluginName(item)).ToImmutableArray();
            var request = new FaceGenPackRequest(
                new FaceGenPackPlanRequest(edition, new WorkspacePath(dataRoot), npcFormId, plugins,
                    new PluginName(anchorValue), command.Options.ContainsKey("debug-sandbox"),
                    command.Options.ContainsKey("shared-neutral-detail")),
                new WorkspacePath(outputRoot));
            var result = await service.PackAsync(request, cancellationToken);
            var response = new PackResponse(result.Written, result.OutputRoot, result.Artifact,
                result.ManifestSha256, result.Diagnostics);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            else output.WriteLine(result.Written ? "facegen pack: PASS" : "facegen pack: REFUSED");
            return result.Written && !result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
                ? CommandExitCode.Success
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record PackResponse(bool Written, WorkspacePath OutputRoot,
        FaceGenPackArtifact? Artifact, Sha256Hash? ManifestSha256, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
