using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PipelineCommandHandler(IPresetToNpcPipeline pipeline, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);

        var result = await pipeline.ExecuteAsync(request, cancellationToken);
        var response = new PipelineResponse(result.Completed, result.ManifestPath?.Value, result.ManifestHash?.Value,
            result.MutationResult?.Proposal.OutputPlugin.Value, result.BodyGenResult?.Files.Select(file =>
                new FileResponse(file.RelativePath.Value, file.AbsolutePath.Value, file.ByteLength, file.Sha256.Value))
                .ToImmutableArray() ?? ImmutableArray<FileResponse>.Empty,
            result.PackageArtifacts.Select(file => new PackageFileResponse(file.Kind, file.RelativePath.Value,
                result.ManifestPath is null ? file.RelativePath.Value : Path.Combine(
                    Path.GetDirectoryName(result.ManifestPath.Value.Value)!, file.RelativePath.Value),
                file.ByteLength, file.Sha256.Value)).ToImmutableArray(), result.Diagnostics);
        Write(response, command.Json, result.Completed ? "pipeline preset-to-npc: PASS" : "pipeline preset-to-npc: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out PresetToNpcPipelineRequest request, out string errorMessage)
    {
        request = default!;
        var formatText = command.Options.GetValueOrDefault("format");
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (formatText is null || !PresetFormatExtensions.TryParseWireName(formatText, out var format))
        { errorMessage = "pipeline preset-to-npc requires --format looksmenu|racemenu-jslot."; return false; }
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out var edition))
        { errorMessage = "pipeline preset-to-npc requires --edition|--game fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("preset", out var preset) ||
            !command.Options.TryGetValue("source-plugin", out var sourcePlugin) ||
            !command.Options.TryGetValue("plugin", out var outputPlugin) ||
            !command.Options.TryGetValue("npc", out var npc) ||
            !command.Options.TryGetValue("mod-name", out var modName) ||
            !command.Options.TryGetValue("output-root", out var outputRoot))
        { errorMessage = "pipeline preset-to-npc requires --preset, --source-plugin, --plugin, --npc, --mod-name, and --output-root."; return false; }
        if (!FormId.TryParse(npc, out var formId))
        { errorMessage = "--npc must be a hexadecimal FormID."; return false; }
        try
        {
            EditorId? editorId = null;
            if (command.Options.TryGetValue("editor-id", out var editorText)) editorId = new EditorId(editorText);
            NpcName? name = null;
            if (command.Options.TryGetValue("name", out var nameText)) name = new NpcName(nameText);
            WorkspacePath? faceGeom = command.Options.TryGetValue("facegeom-manifest", out var faceGeomText)
                ? new WorkspacePath(faceGeomText) : null;
            WorkspacePath? faceTint = command.Options.TryGetValue("facetint-manifest", out var faceTintText)
                ? new WorkspacePath(faceTintText) : null;
            WorkspacePath? runtimeScript = command.Options.TryGetValue("runtime-script-build", out var scriptText)
                ? new WorkspacePath(scriptText) : null;
            WorkspacePath? runtimeScriptPackage = command.Options.TryGetValue("runtime-script-package", out var packageText)
                ? new WorkspacePath(packageText) : null;
            request = new PresetToNpcPipelineRequest(format, edition, new WorkspacePath(preset),
                new WorkspacePath(sourcePlugin), new PluginName(outputPlugin), formId, modName,
                new WorkspacePath(outputRoot), editorId, name, faceGeom, faceTint, runtimeScript, runtimeScriptPackage);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        { errorMessage = exception.Message; return false; }
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        if (json) error.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else error.WriteLine($"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }



    private sealed record PipelineResponse(bool Completed, string? ManifestPath, string? ManifestSha256, string? OutputPlugin,
        ImmutableArray<FileResponse> BodyGenFiles, ImmutableArray<PackageFileResponse> PackageArtifacts,
        ImmutableArray<Diagnostic> Diagnostics);
    private sealed record FileResponse(string RelativePath, string AbsolutePath, int ByteLength, string Sha256);
    private sealed record PackageFileResponse(string Kind, string RelativePath, string AbsolutePath, int ByteLength, string Sha256);
    private sealed record ErrorResponse(string Code, string Message);
}
