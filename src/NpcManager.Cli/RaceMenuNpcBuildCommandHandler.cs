using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed class RaceMenuNpcBuildCommandHandler(
    IRaceMenuNpcBuildService service,
    WorkspacePath workspaceRoot,
    TextWriter output,
    TextWriter error,
    IRaceMenuNpcExecutionRequestFileLoader? requestFileLoader = null)
{
    private readonly IRaceMenuNpcExecutionRequestFileLoader requestLoader =
        requestFileLoader ?? new RaceMenuNpcExecutionRequestFileLoader(workspaceRoot);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("request", out var requestText) ||
            string.IsNullOrWhiteSpace(requestText) ||
            !command.Options.TryGetValue("request-sha256", out var requestHashText) ||
            string.IsNullOrWhiteSpace(requestHashText))
            return Usage(command.Json,
                "npc create-from-preset requires --request @<K-local-json> and --request-sha256 <SHA256>.");

        RaceMenuNpcExecutionRequest request;
        try
        {
            var rawPath = requestText.StartsWith('@') ? requestText[1..] : requestText;
            var load = await requestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    new WorkspacePath(rawPath), new Sha256Hash(requestHashText)),
                cancellationToken);
            if (!load.Loaded || load.Request is null)
            {
                var security = load.Status ==
                               RaceMenuNpcExecutionRequestFileLoadStatus.SecurityRefused;
                return Failure(command.Json,
                    security ? "preset-npc-request-security-refused" :
                    "preset-npc-request-invalid",
                    string.Join(" | ", load.Diagnostics.Select(item => item.Message)),
                    DiagnosticExitCodeClassifier.ClassifyFailure(
                        load.Diagnostics));
            }
            request = load.Request;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return Failure(command.Json, "preset-npc-request-invalid", exception.Message,
                CommandExitCode.ValidationFailure);
        }

        RaceMenuNpcExecutionResult result;
        try
        {
            result = await service.ExecuteAsync(request, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure(command.Json, "cancelled", "Preset NPC creation was cancelled.",
                CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(command.Json, "preset-npc-io-failed", exception.Message,
                CommandExitCode.ValidationFailure);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(command.Json, "preset-npc-unexpected-failure",
                $"{exception.GetType().Name}: {exception.Message}",
                CommandExitCode.ValidationFailure);
        }

        var artifact = result.Build?.Artifact;
        var existingArtifact = result.ExistingNpcBuild?.Artifact;
        var targetMode = existingArtifact is null ? "new-npc" : "existing-npc";
        var response = new BuildResponse(
            result.Completed,
            targetMode,
            artifact?.Verdict ?? existingArtifact?.Verdict,
            artifact?.AllocatedFormId.ToString() ??
            existingArtifact?.SourceOwnerFormId.ToString(),
            artifact?.Plugin.Value ?? existingArtifact?.Plugin.Value,
            artifact?.PluginSha256.Value ?? existingArtifact?.PluginSha256.Value,
            artifact?.FaceGeom.Value ?? existingArtifact?.FaceGeom.Value,
            artifact?.FaceGeomSha256.Value ?? existingArtifact?.FaceGeomSha256.Value,
            artifact?.FaceTint.Value ?? existingArtifact?.FaceTint.Value,
            artifact?.FaceTintSha256.Value ?? existingArtifact?.FaceTintSha256.Value,
            artifact?.Manifest.Value ?? existingArtifact?.Manifest.Value,
            artifact?.ManifestSha256.Value ?? existingArtifact?.ManifestSha256.Value,
            result.BodyGen?.TemplateName,
            result.BodyGen?.Files.Select(item => new BodyGenResponse(
                    item.RelativePath.Value, item.Sha256.Value, item.ByteLength))
                .ToImmutableArray() ?? [],
            result.RuntimeVmad is null ? 0 : SkyrimNpcApplySseContract.PropertyCount,
            artifact?.RuntimeAuthority ?? existingArtifact?.RuntimeAuthority ?? false,
            result.Diagnostics);
        if (command.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else if (result.Completed && (artifact is not null || existingArtifact is not null))
        {
            output.WriteLine($"npc create-from-preset ({targetMode}): {response.Verdict}");
            output.WriteLine($"NPC {response.NpcFormId} -> {response.Plugin}");
            output.WriteLine($"FaceGeom -> {response.FaceGeom}");
            output.WriteLine($"FaceTint -> {response.FaceTint}");
            output.WriteLine($"Package -> {response.Manifest}");
            output.WriteLine($"BodyGen -> {result.BodyGen?.Files.Length ?? 0} files; VMAD -> {response.RuntimePropertyCount} properties");
        }
        else
        {
            output.WriteLine("npc create-from-preset: REFUSED");
            foreach (var diagnostic in result.Diagnostics
                         .Where(item => item.Severity != DiagnosticSeverity.Info))
                error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
        }
        return result.Completed ? CommandExitCode.Success : ExitCode(result.Diagnostics);
    }

    private CommandExitCode Usage(bool json, string message) =>
        Failure(json, "usage-error", message, CommandExitCode.UsageError);

    private CommandExitCode Failure(
        bool json,
        string code,
        string message,
        CommandExitCode exitCode)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(code, message), JsonOptions));
        else error.WriteLine($"ERROR {code}: {message}");
        return exitCode;
    }

    private static CommandExitCode ExitCode(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticExitCodeClassifier.Classify(diagnostics);

    private sealed record BuildResponse(
        bool Completed,
        string TargetMode,
        string? Verdict,
        string? NpcFormId,
        string? Plugin,
        string? PluginSha256,
        string? FaceGeom,
        string? FaceGeomSha256,
        string? FaceTint,
        string? FaceTintSha256,
        string? Manifest,
        string? ManifestSha256,
        string? BodyGenTemplate,
        ImmutableArray<BodyGenResponse> BodyGenFiles,
        int RuntimePropertyCount,
        bool RuntimeAuthority,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record BodyGenResponse(string Path, string Sha256, int ByteLength);
    private sealed record ErrorResponse(string Code, string Message);
}
