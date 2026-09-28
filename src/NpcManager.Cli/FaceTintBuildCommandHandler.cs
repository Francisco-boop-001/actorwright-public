using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceTintBuildCommandHandler(IFaceTintBuildService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryParseEdition(command, out var edition, out var errorMessage)) return Usage(command.Json, errorMessage);
        if (!command.Options.TryGetValue("manifest", out var manifestPath))
            return Usage(command.Json, "facegen build-tint requires --manifest <path>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "facegen build-tint requires --output <path>.");

        FormId? npc = null;
        if (command.Options.TryGetValue("npc", out var npcText))
        {
            if (!FormId.TryParse(npcText, out var parsed)) return Usage(command.Json, "--npc must be a hexadecimal FormID.");
            npc = parsed;
        }
        int? resolution = null;
        if (command.Options.TryGetValue("resolution", out var resolutionText))
        {
            if (!int.TryParse(resolutionText, out var parsed)) return Usage(command.Json, "--resolution must be an integer.");
            resolution = parsed;
        }
        FaceTintOutputFormat? format = null;
        if (command.Options.TryGetValue("format", out var formatText))
        {
            if (formatText.Equals("bgra8", StringComparison.OrdinalIgnoreCase) ||
                formatText.Equals("uncompressed", StringComparison.OrdinalIgnoreCase))
                format = FaceTintOutputFormat.Bgra8;
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

        WorkspacePath? textureOutputPath = null;
        if (command.Options.TryGetValue("dds-output", out var texturePathText))
        {
            try { textureOutputPath = new WorkspacePath(texturePathText); }
            catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        }
        WorkspacePath? providerRoot = null;
        if (command.Options.TryGetValue("provider-root", out var providerRootText))
        {
            try { providerRoot = new WorkspacePath(providerRootText); }
            catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        }

        FaceTintBuildResult result;
        try
        {
            result = await service.BuildAsync(new FaceTintBuildRequest(edition,
                new WorkspacePath(manifestPath), new WorkspacePath(outputPath), npc,
                resolution, format, mips, alpha, textureOutputPath, providerRoot), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Usage(command.Json, exception.Message);
        }
        var response = new BuildResponse(result.Written, result.Artifact?.ArtifactKind,
            result.Artifact?.Edition, result.Artifact?.NpcFormId, result.Artifact?.Width,
            result.Artifact?.Height, result.Artifact?.Format, result.Artifact?.MipCount,
            result.OutputSha256?.Value, result.Artifact?.TextureOutputPath,
            result.TextureOutputSha256?.Value, result.Diagnostics);
        Write(response, command.Json, result.Written ? $"facegen build-tint: PASS {outputPath}" :
            "facegen build-tint: REFUSED");
        return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics)
            : CommandExitCode.Success;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { errorMessage = "facegen build-tint requires --edition|--game fallout4|skyrimse."; return false; }
        errorMessage = string.Empty;
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

    private sealed record BuildResponse(bool Written, string? ArtifactKind, string? Edition, string? NpcFormId,
        int? Width, int? Height, string? Format, int? MipCount, string? OutputSha256,
        string? TextureOutputPath, string? TextureOutputSha256,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
