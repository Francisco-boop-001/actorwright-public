using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class FaceGenCommandHandler(IFaceGenService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var edition, out var manifestPath, out var npcFormId, out var errorMessage))
            return Usage(command.Json, errorMessage);
        var result = command.Name == "facegen verify"
            ? await service.VerifyAsync(new FaceGenVerifyRequest(edition, manifestPath, npcFormId,
                command.Options.ContainsKey("strict-shapes")), cancellationToken)
            : await service.DiagnoseAsync(new FaceGenDiagnoseRequest(edition, manifestPath, npcFormId), cancellationToken);
        var response = new FaceGenResponse(result.IsApplicable, result.Manifest?.Edition.ToWireName(),
            result.Manifest?.NpcFormId.ToString(), result.ValidHeadShapeCount,
            result.AcceptedShapes.Select(shape => new ShapeResponse(shape.Name, shape.Role.ToWireName(), shape.VertexCount,
                shape.SourcePath.Value)).ToImmutableArray(), result.Diagnostics);
        Write(response, command.Json, result.IsApplicable ? "facegen: PASS" : "facegen: REFUSED");
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out GameEdition edition, out WorkspacePath manifestPath,
        out FormId? npcFormId, out string errorMessage)
    {
        edition = default; manifestPath = default; npcFormId = null;
        var editionText = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionText is null || !GameEditionExtensions.TryParseWireName(editionText, out edition))
        { errorMessage = "facegen requires --edition|--game fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("manifest", out var manifestText))
        { errorMessage = "facegen requires --manifest <path>."; return false; }
        try { manifestPath = new WorkspacePath(manifestText); }
        catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        if (command.Options.TryGetValue("npc", out var npcText))
        {
            if (!FormId.TryParse(npcText, out var parsed))
            { errorMessage = "--npc must be a hexadecimal FormID."; return false; }
            npcFormId = parsed;
        }
        errorMessage = string.Empty; return true;
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostic = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(diagnostic), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record FaceGenResponse(bool IsApplicable, string? Edition, string? NpcFormId, int ValidHeadShapeCount,
        ImmutableArray<ShapeResponse> AcceptedShapes, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record ShapeResponse(string Name, string Role, int VertexCount, string SourcePath);
    private sealed record ErrorResponse(ImmutableArray<Diagnostic> Diagnostics);
}
