using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceTintProviderBoundBuildCommandHandler(
    IFaceTintProviderBoundBuildService service, TextWriter output, TextWriter error)
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
            return Usage(command.Json, "facegen build-tint-bound requires --data-root <copied-Data-root>.");
        if (!command.Options.TryGetValue("manifest", out var manifest))
            return Usage(command.Json, "facegen build-tint-bound requires --manifest <json>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "facegen build-tint-bound requires --output <semantic-json>.");
        if (!command.Options.TryGetValue("output-root", out var outputRoot))
            return Usage(command.Json, "facegen build-tint-bound requires --output-root <K-local-output-root>.");
        if (!command.Options.TryGetValue("npc", out var npcText) || !FormId.TryParse(npcText, out var npc))
            return Usage(command.Json, "facegen build-tint-bound requires --npc <hex-form-id>.");
        if (!command.Options.TryGetValue("plugins", out var pluginText) || string.IsNullOrWhiteSpace(pluginText))
            return Usage(command.Json, "facegen build-tint-bound requires --plugins PluginA.esp,PluginB.esp.");

        ImmutableArray<PluginName> plugins;
        try
        {
            var builder = ImmutableArray.CreateBuilder<PluginName>();
            foreach (var value in pluginText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                builder.Add(new PluginName(value));
            plugins = builder.ToImmutable();
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        FaceTintOutputFormat? format = null;
        if (command.Options.TryGetValue("format", out var formatText))
        {
            if (formatText.Equals("bgra8", StringComparison.OrdinalIgnoreCase) ||
                formatText.Equals("uncompressed", StringComparison.OrdinalIgnoreCase)) format = FaceTintOutputFormat.Bgra8;
            else if (formatText.Equals("bc3", StringComparison.OrdinalIgnoreCase)) format = FaceTintOutputFormat.Bc3;
            else if (formatText.Equals("bc7", StringComparison.OrdinalIgnoreCase)) format = FaceTintOutputFormat.Bc7;
            else return Usage(command.Json, "--format must be bgra8|bc3|bc7|uncompressed.");
        }
        int? mips = null;
        if (command.Options.TryGetValue("mips", out var mipsText))
        {
            if (!int.TryParse(mipsText, out var parsed)) return Usage(command.Json, "--mips must be an integer.");
            mips = parsed;
        }
        FaceTintAlphaMode? alpha = null;
        if (command.Options.TryGetValue("alpha", out var alphaText))
        {
            if (!Enum.TryParse<FaceTintAlphaMode>(alphaText, true, out var parsed) || !Enum.IsDefined(parsed))
                return Usage(command.Json, "--alpha must be preserve|opaque.");
            alpha = parsed;
        }

        FaceTintProviderBoundBuildResult result;
        try
        {
            result = await service.BuildAsync(new FaceTintProviderBoundBuildRequest(
                edition, new WorkspacePath(dataRoot), npc, plugins, new WorkspacePath(manifest),
                new WorkspacePath(outputPath), new WorkspacePath(outputRoot), format, mips, alpha), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new BoundResponse(result.Written, result.Artifact, result.OutputSha256?.Value,
            result.TextureOutputSha256?.Value, result.Diagnostics);
        Write(response, command.Json, result.Written ? "facegen build-tint-bound: PASS" : "facegen build-tint-bound: REFUSED");
        if (result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        return result.Written ? CommandExitCode.Success : CommandExitCode.ValidationFailure;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { message = "facegen build-tint-bound requires --edition|--game fallout4|skyrimse."; return false; }
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

    private sealed record BoundResponse(bool Written, FaceTintProviderBoundBuildArtifact? Artifact,
        string? OutputSha256, string? TextureOutputSha256, ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
