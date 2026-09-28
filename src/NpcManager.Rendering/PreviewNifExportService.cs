using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>Writes a deterministic, sandbox-only plan for a future NIF writer.</summary>
public sealed class PreviewNifExportService(IWorkspacePolicy policy, WorkspacePath labRoot) : IPreviewNifExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<PreviewNifExportResult> ExportPlanAsync(PreviewNifExportRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDestination(request.OutputPath).ToBuilder();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ScenePath));
        if (!string.Equals(Path.GetExtension(request.ScenePath.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-nif-scene-extension", DiagnosticSeverity.Error,
                "Preview NIF export scenes must use the .json extension."));
        if (!File.Exists(request.ScenePath.Value))
            diagnostics.Add(new Diagnostic("preview-nif-scene-missing", DiagnosticSeverity.Error,
                "The preview scene artifact does not exist."));
        else if (File.GetAttributes(request.ScenePath.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("preview-nif-scene-reparse-refused", DiagnosticSeverity.Error,
                "Preview scene artifacts may not be reparse points."));
        if (PreviewSceneService.HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ScenePath.Value);
            if (info.Length > PreviewSceneService.MaxBytes)
            {
                diagnostics.Add(new Diagnostic("preview-nif-scene-size-limit", DiagnosticSeverity.Error,
                    $"Preview scene artifacts may not exceed {PreviewSceneService.MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ScenePath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-scene-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        PreviewSceneArtifact? scene;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            PreviewSceneService.ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            scene = JsonSerializer.Deserialize<PreviewSceneArtifact>(bytes, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-scene-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics, inputHash);
        }
        if (scene is null)
        {
            diagnostics.Add(new Diagnostic("preview-nif-scene-empty", DiagnosticSeverity.Error,
                "Preview NIF export requires a scene artifact object."));
            return Refused(diagnostics, inputHash);
        }
        if (!string.Equals(scene.ArtifactKind, "preview-scene-semantic-build", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("preview-nif-scene-kind", DiagnosticSeverity.Error,
                "Preview NIF export requires a preview-scene-semantic-build artifact."));
        if (!string.Equals(scene.Edition, request.Edition.ToWireName(), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-nif-scene-edition", DiagnosticSeverity.Error,
                "Preview scene edition must match the export request."));
        if (!FormId.TryParse(scene.NpcFormId, out _))
            diagnostics.Add(new Diagnostic("preview-nif-scene-formid", DiagnosticSeverity.Error,
                "Preview scene NPC FormID is invalid."));
        if (scene.Assets.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("preview-nif-scene-assets", DiagnosticSeverity.Error,
                "Preview NIF export requires scene assets."));
        var assets = scene.Assets.Where(item => item.Included).Select(item =>
            new PreviewNifExportAsset(item.Category, item.Path, item.Provider, item.Sha256)).ToImmutableArray();
        if (assets.Length == 0)
            diagnostics.Add(new Diagnostic("preview-nif-scene-no-included-assets", DiagnosticSeverity.Error,
                "Preview NIF export requires at least one included scene asset."));
        foreach (var asset in assets)
        {
            try { _ = new AssetPath(asset.Path); _ = new Sha256Hash(asset.Sha256); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("preview-nif-scene-asset-invalid", DiagnosticSeverity.Error, exception.Message));
            }
        }
        if (PreviewSceneService.HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var artifact = new PreviewNifExportArtifact("1", "preview-nif-export-plan",
            scene.Edition, scene.NpcFormId, inputHash.Value, request.ScenePath.Value, "nif",
            assets, scene.Camera, scene.Lighting, scene.Animation);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        try
        {
            await PreviewArtifactFileWriter.WriteAsync(request.OutputPath, outputBytes, cancellationToken);
            return new PreviewNifExportResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-plan-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new PreviewNifExportResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preview-nif-output-outside-lab",
            DiagnosticSeverity.Error, "Preview NIF export plans must remain under the K-only lab root."));
        if (!output.Value.EndsWith(".nif.plan.json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-nif-output-extension", DiagnosticSeverity.Error,
                "Sandboxed preview NIF export must use the .nif.plan.json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("preview-nif-output-exists",
            DiagnosticSeverity.Error, "Preview NIF export plans never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("preview-nif-output-parent-missing",
            DiagnosticSeverity.Error, "Preview NIF export plan directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static PreviewNifExportResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, null, null, diagnostics.ToImmutable());

}
