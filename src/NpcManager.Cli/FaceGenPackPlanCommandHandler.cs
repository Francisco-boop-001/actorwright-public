using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenPackPlanCommandHandler(
    IFaceGenPackPlanService service, TextWriter output, TextWriter error)
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
            return Usage(command.Json, "facegen plan-pack requires --edition|--game fallout4|skyrimse.");
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
            return Usage(command.Json, "facegen plan-pack requires --data-root <copied-Data-root>.");
        if (!command.Options.TryGetValue("npc", out var npcValue) || !FormId.TryParse(npcValue, out var npcFormId))
            return Usage(command.Json, "facegen plan-pack requires --npc <hex-form-id>.");
        var pluginValue = command.Options.GetValueOrDefault("plugins");
        if (string.IsNullOrWhiteSpace(pluginValue))
            return Usage(command.Json, "facegen plan-pack requires --plugins PluginA.esp,PluginB.esp in load order.");
        if (!command.Options.TryGetValue("anchor-plugin", out var anchorValue))
            return Usage(command.Json, "facegen plan-pack requires --anchor-plugin SavePlugin.esp.");

        ImmutableArray<PluginName> plugins;
        PluginName anchor;
        try
        {
            var builder = ImmutableArray.CreateBuilder<PluginName>();
            foreach (var value in pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                builder.Add(new PluginName(value));
            plugins = builder.ToImmutable();
            anchor = new PluginName(anchorValue);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        FaceGenPackPlanResult result;
        try
        {
            result = await service.PlanAsync(new FaceGenPackPlanRequest(
                edition, new WorkspacePath(dataRoot), npcFormId, plugins, anchor,
                command.Options.ContainsKey("debug-sandbox"), command.Options.ContainsKey("shared-neutral-detail")),
                cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new PackPlanResponse(result.Planned, result.Artifact, result.Diagnostics);
        Write(response, command.Json, result.Planned ? "facegen plan-pack: PASS" : "facegen plan-pack: REFUSED");
        return result.Planned
            ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics);
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

    private sealed record PackPlanResponse(bool Planned,
        FaceGenPackPlanArtifact? Artifact, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
