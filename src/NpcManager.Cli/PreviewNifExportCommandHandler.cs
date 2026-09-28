using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PreviewNifExportCommandHandler(
    IPreviewNifExportService service,
    IPreviewNifBinaryExportService? binaryService,
    TextWriter output,
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
        if (!command.Options.TryGetValue("scene", out var scenePath))
            return Usage(command.Json, "preview export-nif requires --scene <preview-json>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "preview export-nif requires --output <*.nif.plan.json> or a new .nif binary output.");
        var binaryMode = command.Options.TryGetValue("asset-root", out var assetRoot);
        if (binaryMode && binaryService is null)
            return Usage(command.Json, "preview export-nif binary mode is unavailable in this runner configuration.");
        if (binaryMode && !outputPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            return Usage(command.Json, "preview export-nif binary mode requires --output <new-*.nif>.");
        PreviewNifExportResult? planResult = null;
        PreviewNifBinaryExportResult? binaryResult = null;
        try
        {
            if (binaryMode)
            {
                binaryResult = await binaryService!.ExportAsync(new PreviewNifBinaryExportRequest(edition,
                    new WorkspacePath(scenePath), new WorkspacePath(assetRoot!), new WorkspacePath(outputPath)), cancellationToken);
            }
            else
            {
                planResult = await service.ExportPlanAsync(new PreviewNifExportRequest(edition,
                    new WorkspacePath(scenePath), new WorkspacePath(outputPath)), cancellationToken);
            }
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        var diagnostics = binaryMode ? binaryResult!.Diagnostics : planResult!.Diagnostics;
        var response = binaryMode
            ? new PreviewNifResponse(binaryResult!.Written, binaryResult.Artifact?.ArtifactKind,
                null, null, binaryResult.Artifact?.SourceAssets.Length,
                binaryResult.Artifact?.SceneSha256, binaryResult.OutputSha256?.Value,
                binaryResult.Artifact?.OutputPath, binaryResult.Artifact?.ByteLength,
                binaryResult.Artifact?.MeshCount, binaryResult.Artifact?.Exporter,
                binaryResult.Artifact?.ImportMode, binaryResult.Artifact?.SourceAssets, diagnostics,
                binaryResult.Artifact?.Morphs, binaryResult.Artifact?.MorphDeformed,
                binaryResult.Artifact?.BaseVertexSha256, binaryResult.Artifact?.BakedVertexSha256,
                binaryResult.Artifact?.MorphDependencies, binaryResult.Artifact?.ArmatureCount,
                binaryResult.Artifact?.DeformationMode, binaryResult.Artifact?.HairZap,
                binaryResult.Artifact?.HairZapApplied, binaryResult.Artifact?.HairZapAffectedMeshCount,
                binaryResult.Artifact?.HairZapRemovedFaceCount,
                binaryResult.Artifact?.FaceCullApplied, binaryResult.Artifact?.FaceCullAffectedMeshCount)
            : new PreviewNifResponse(planResult!.Written, planResult.Artifact?.ArtifactKind,
                planResult.Artifact?.NpcFormId, planResult.Artifact?.TargetFormat, planResult.Artifact?.Assets.Length,
                planResult.Artifact?.InputSceneSha256, planResult.OutputSha256?.Value,
                null, null, null, null, null, planResult.Artifact?.Assets, diagnostics);
        Write(response, command.Json, binaryResult?.Written == true || planResult?.Written == true
            ? "preview export-nif: PASS" : "preview export-nif: REFUSED");
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(diagnostics);
        return CommandExitCode.Success;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { message = "preview export-nif requires --edition|--game fallout4|skyrimse."; return false; }
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

    private sealed record PreviewNifResponse(bool Written, string? ArtifactKind, string? NpcFormId,
        string? TargetFormat, int? AssetCount, string? InputSceneSha256, string? OutputSha256,
        string? OutputPath, long? ByteLength, int? MeshCount, string? Exporter, string? ImportMode,
        ImmutableArray<PreviewNifExportAsset>? SourceAssets,
        ImmutableArray<Diagnostic> Diagnostics,
        ImmutableArray<PreviewNifExportMorph>? Morphs = null,
        bool? MorphDeformed = null,
        string? BaseVertexSha256 = null,
        string? BakedVertexSha256 = null,
        ImmutableArray<PreviewNifExportDependency>? MorphDependencies = null,
        int? ArmatureCount = null,
        string? DeformationMode = null,
        PreviewHairZapPlan? HairZap = null,
        bool? HairZapApplied = null,
        int? HairZapAffectedMeshCount = null,
        int? HairZapRemovedFaceCount = null,
        bool? FaceCullApplied = null,
        int? FaceCullAffectedMeshCount = null);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
