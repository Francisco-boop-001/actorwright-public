using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenProviderResolutionCommandHandler(
    IFaceGenProviderResolutionService service, TextWriter output, TextWriter error)
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
            return Usage(command.Json, "facegen resolve-providers requires --edition|--game fallout4|skyrimse.");
        if (!command.Options.TryGetValue("data-root", out var dataRoot))
            return Usage(command.Json, "facegen resolve-providers requires --data-root <copied-Data-root>.");
        if (!command.Options.TryGetValue("npc", out var npcValue) || !FormId.TryParse(npcValue, out var npcFormId))
            return Usage(command.Json, "facegen resolve-providers requires --npc <hex-form-id>.");
        var pluginValue = command.Options.GetValueOrDefault("plugins");
        if (string.IsNullOrWhiteSpace(pluginValue))
            return Usage(command.Json, "facegen resolve-providers requires --plugins PluginA.esp,PluginB.esp in load order.");

        ImmutableArray<PluginName> plugins;
        try
        {
            var builder = ImmutableArray.CreateBuilder<PluginName>();
            foreach (var value in pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                builder.Add(new PluginName(value));
            plugins = builder.ToImmutable();
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var includeNeutral = command.Options.ContainsKey("shared-neutral-detail");
        FaceGenProviderResolutionResult result;
        try
        {
            result = await service.ResolveAsync(new FaceGenProviderResolutionRequest(
                edition, new WorkspacePath(dataRoot), npcFormId, plugins, includeNeutral), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new ProviderResponse(result.Resolved, result.Artifact, result.Diagnostics);
        Write(response, command.Json, result.Resolved ? "facegen resolve-providers: PASS" : "facegen resolve-providers: REFUSED");
        return result.Resolved
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

    private sealed record ProviderResponse(bool Resolved,
        FaceGenProviderResolutionArtifact? Artifact, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
