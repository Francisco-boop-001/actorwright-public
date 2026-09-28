using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Performs the approved K-local two-pass preview-NIF write through the pinned
/// Blender/PyNifly adapter. The output is sandbox evidence, not deployment authority.
/// </summary>
public sealed partial class BlenderPreviewNifExporter(
    WorkspacePath executablePath,
    WorkspacePath profileRoot,
    EmbeddedBlenderScriptId scriptId,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Sha256Hash expectedExecutableSha256,
    INifGeometryReadbackService geometryReadback,
    Func<string, string, CancellationToken, ValueTask<ImmutableArray<Diagnostic>>>? processRunner = null) : IPreviewNifBinaryExportService
{
    private const int MaximumAssets = 64;
    private const int MaximumMorphs = 256;
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private const long MaximumOutputBytes = 512L * 1024 * 1024;
    private const long MaximumStatusBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<PreviewNifBinaryExportResult> ExportAsync(
        PreviewNifBinaryExportRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] sceneBytes;
        try
        {
            var info = new FileInfo(request.ScenePath.Value);
            if (info.Length > PreviewSceneService.MaxBytes)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-scene-size", DiagnosticSeverity.Error,
                    $"Preview scene artifacts may not exceed {PreviewSceneService.MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            sceneBytes = await File.ReadAllBytesAsync(request.ScenePath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-read", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        var sceneHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(sceneBytes)));
        PreviewSceneArtifact? scene;
        try
        {
            using var document = JsonDocument.Parse(sceneBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            PreviewSceneService.ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            scene = JsonSerializer.Deserialize<PreviewSceneArtifact>(sceneBytes, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-json", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics, sceneHash);
        }
        if (scene is null)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-empty", DiagnosticSeverity.Error,
                "Binary NIF export requires a preview scene artifact."));
            return Refused(diagnostics, sceneHash);
        }
        if (!string.Equals(scene.ArtifactKind, "preview-scene-semantic-build", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-kind", DiagnosticSeverity.Error,
                "Binary NIF export requires a preview-scene-semantic-build artifact."));
        if (!string.Equals(scene.Edition, request.Edition.ToWireName(), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-edition", DiagnosticSeverity.Error,
                "Preview scene edition must match the binary NIF export request."));
        if (!FormId.TryParse(scene.NpcFormId, out _))
            diagnostics.Add(new Diagnostic("preview-nif-binary-scene-formid", DiagnosticSeverity.Error,
                "Preview scene NPC FormID is invalid."));
        var assets = scene.Assets.Where(item => item.Included)
            .Select(item => new PreviewNifExportAsset(item.Category, item.Path, item.Provider, item.Sha256))
            .ToImmutableArray();
        if (assets.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("preview-nif-binary-no-assets", DiagnosticSeverity.Error,
                "Binary NIF export requires at least one included scene asset."));
        if (assets.Length > MaximumAssets)
            diagnostics.Add(new Diagnostic("preview-nif-binary-asset-count", DiagnosticSeverity.Error,
                $"Binary NIF export accepts at most {MaximumAssets} included assets."));

        var morphs = scene.Morphs
            .Where(item => item.Applied && item.Included)
            .Select(item => new PreviewNifExportMorph(item.Name, item.Value))
            .ToImmutableArray();
        if (morphs.Length > MaximumMorphs)
            diagnostics.Add(new Diagnostic("preview-nif-binary-morph-count", DiagnosticSeverity.Error,
                $"Binary NIF export accepts at most {MaximumMorphs} included morphs."));
        ValidateMorphs(scene, morphs, diagnostics);

        var resolved = ImmutableArray.CreateBuilder<ResolvedAsset>();
        foreach (var asset in assets)
        {
            var path = ResolveAsset(request.AssetRoot, asset.Path, diagnostics);
            if (path is null) continue;
            try
            {
                var actual = await HashFileAsync(new WorkspacePath(path), cancellationToken);
                if (!string.Equals(actual.Value, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(new Diagnostic("preview-nif-binary-source-hash", DiagnosticSeverity.Error,
                        $"Preview asset '{asset.Path}' does not match its scene hash."));
                else
                {
                    var triFiles = await DiscoverTriFilesAsync(new WorkspacePath(path), request.AssetRoot,
                        diagnostics, cancellationToken);
                    resolved.Add(new ResolvedAsset(path, asset.Category, asset.Sha256, triFiles));
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-source-read", DiagnosticSeverity.Error,
                    exception.Message));
            }
        }
        if (!morphs.IsDefaultOrEmpty && resolved.All(item => item.TriFiles.IsDefaultOrEmpty))
            diagnostics.Add(new Diagnostic("preview-nif-binary-morph-tri-missing", DiagnosticSeverity.Error,
                "Included vertex morphs require at least one adjacent TRI dependency."));
        if (HasErrors(diagnostics)) return Refused(diagnostics, sceneHash);

        var sourceGeometry = ImmutableArray.CreateBuilder<NifGeometryReadbackDocument>(
            resolved.Count);
        foreach (var asset in resolved)
        {
            NifGeometryReadbackResult readback = await geometryReadback.ReadAsync(
                new NifGeometryReadbackRequest(request.Edition,
                    new WorkspacePath(asset.Path), new Sha256Hash(asset.Sha256)),
                cancellationToken);
            diagnostics.AddRange(readback.Diagnostics);
            if (!readback.Accepted || readback.Document is null)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-source-geometry-readback",
                    DiagnosticSeverity.Error,
                    $"Preview asset '{asset.Path}' did not produce admitted independent geometry evidence."));
                continue;
            }
            sourceGeometry.Add(readback.Document);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, sceneHash);

        var finalPath = request.OutputPath.Value;
        var parent = Path.GetDirectoryName(finalPath)!;
        var temporaryOutput = Path.Combine(parent, ".preview-nif-" + Guid.NewGuid().ToString("N") + ".nif");
        var requestPath = Path.Combine(parent, ".preview-nif-request-" + Guid.NewGuid().ToString("N") + ".json");
        var statusPath = Path.Combine(parent, ".preview-nif-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var payload = new
            {
                root = labRoot.Value,
                output = temporaryOutput,
                targetGame = request.Edition == GameEdition.Fallout4 ? "FO4" : "SKYRIMSE",
                morphs,
                assets = resolved.Select(asset => new
                {
                    path = asset.Path,
                    sha256 = asset.Sha256,
                    category = asset.Category,
                    triFiles = asset.TriFiles.Select(tri => new { path = tri.Path, sha256 = tri.Sha256 })
                }).ToImmutableArray(),
                hairZap = scene.HairZap is null ? null : new
                {
                    renderHeadwear = scene.HairZap.RenderHeadwear,
                    coveredSlots = scene.HairZap.CoveredSlots,
                    top = scene.HairZap.TopCovered,
                    @long = scene.HairZap.LongCovered,
                    faceCull = scene.HairZap.FaceGenHeadCovered
                }
            };
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
            var beforeHash = await HashFileAsync(executablePath, cancellationToken);
            if (beforeHash != expectedExecutableSha256)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-tool-hash", DiagnosticSeverity.Error,
                    "The pinned Blender executable hash does not match its admitted manifest."));
                return Refused(diagnostics, sceneHash);
            }
            var processResult = processRunner is null
                ? await RunBlenderAsync(requestPath, statusPath, cancellationToken)
                : new ProcessResult(await processRunner(
                    requestPath, statusPath, cancellationToken));
            diagnostics.AddRange(processResult.Diagnostics);
            var afterHash = await HashFileAsync(executablePath, cancellationToken);
            if (afterHash != expectedExecutableSha256)
                diagnostics.Add(new Diagnostic("preview-nif-binary-tool-changed", DiagnosticSeverity.Error,
                    "The Blender executable changed while exporting the NIF."));
            if (HasErrors(diagnostics)) return Refused(diagnostics, sceneHash);

            var status = await ReadStatusAsync(statusPath, diagnostics, cancellationToken);
            if (status is null || !status.Exported)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-export-failed", DiagnosticSeverity.Error,
                    status?.Error ?? "The Blender exporter did not produce a successful status."));
                return Refused(diagnostics, sceneHash);
            }
            if (!string.Equals(status.Output, temporaryOutput, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(temporaryOutput))
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-output-binding", DiagnosticSeverity.Error,
                    "The Blender status did not bind the temporary NIF output."));
                return Refused(diagnostics, sceneHash);
            }
            var outputInfo = new FileInfo(temporaryOutput);
            if (outputInfo.Length <= 32 || outputInfo.Length > MaximumOutputBytes)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-output-size", DiagnosticSeverity.Error,
                    "The exported NIF size is outside the accepted bound."));
                return Refused(diagnostics, sceneHash);
            }
            var outputHash = await HashFileAsync(new WorkspacePath(temporaryOutput), cancellationToken);
            var expectedImportMode = morphs.IsDefaultOrEmpty ? "mesh-only-import" : "mesh-plus-tri-bake";
            bool morphStatusMatches = MorphStatusMatches(status, morphs, resolved);
            bool hairZapStatusMatches = HairZapStatusMatches(status, scene.HairZap);
            if (!string.Equals(outputHash.Value, status.Sha256, StringComparison.OrdinalIgnoreCase) ||
                status.Bytes != outputInfo.Length || status.MeshCount <= 0 ||
                !string.Equals(status.TargetGame, request.Edition == GameEdition.Fallout4 ? "FO4" : "SKYRIMSE",
                    StringComparison.OrdinalIgnoreCase) ||
                status.ArmatureCount < 0 ||
                status.DeformationMode is not ("nif-skinned-evaluated" or "nif-mesh-evaluated") ||
                !string.Equals(status.ImportMode, expectedImportMode, StringComparison.Ordinal) ||
                !morphStatusMatches || !hairZapStatusMatches)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-status-mismatch", DiagnosticSeverity.Error,
                    "The Blender NIF status does not match the written output or requested game."));
                return Refused(diagnostics, sceneHash);
            }
            if (!await HasSupportedNifHeaderAsync(temporaryOutput, cancellationToken))
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-header-invalid", DiagnosticSeverity.Error,
                    "The exported file does not have the admitted Gamebryo 20.2.0.7 NIF header."));
                return Refused(diagnostics, sceneHash);
            }
            int? statusVertexCount = await ReadStatusVertexCountAsync(
                statusPath, diagnostics, cancellationToken);
            if (statusVertexCount is null)
                return Refused(diagnostics, sceneHash);
            NifGeometryReadbackResult outputReadback = await geometryReadback.ReadAsync(
                new NifGeometryReadbackRequest(request.Edition,
                    new WorkspacePath(temporaryOutput), outputHash), cancellationToken);
            diagnostics.AddRange(outputReadback.Diagnostics);
            if (!outputReadback.Accepted || outputReadback.Document is null)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-output-geometry-readback",
                    DiagnosticSeverity.Error,
                    "The preview temporary output did not produce admitted independent geometry evidence."));
                return Refused(diagnostics, sceneHash);
            }
            NifGeometryReadbackDocument outputGeometry = outputReadback.Document;
            int outputVertexCount = outputGeometry.Shapes.Sum(shape => shape.VertexCount);
            Sha256Hash? sourceAggregate = sourceGeometry.Count == 1
                ? sourceGeometry[0].AggregateGeometrySha256
                : null;
            if (outputGeometry.Edition != request.Edition ||
                statusVertexCount.Value != outputVertexCount ||
                (!string.IsNullOrWhiteSpace(status.BakedVertexSha256) &&
                 !string.Equals(status.BakedVertexSha256,
                     outputGeometry.AggregateGeometrySha256.Value,
                     StringComparison.OrdinalIgnoreCase)) ||
                (!morphs.IsDefaultOrEmpty &&
                 sourceAggregate is { } sourceHash &&
                 sourceHash == outputGeometry.AggregateGeometrySha256) ||
                (!morphs.IsDefaultOrEmpty && sourceAggregate is { } expectedBase &&
                 !string.Equals(status.BaseVertexSha256, expectedBase.Value,
                     StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-output-geometry-mismatch",
                    DiagnosticSeverity.Error,
                    "The preview writer geometry assertion does not match independently read output geometry."));
                return Refused(diagnostics, sceneHash);
            }
            File.Move(temporaryOutput, finalPath, overwrite: false);
            var morphDependencies = morphs.IsDefaultOrEmpty
                ? ImmutableArray<PreviewNifExportDependency>.Empty
                : resolved.SelectMany(item => item.TriFiles)
                    .Select(item => new PreviewNifExportDependency(item.Path, item.Sha256))
                    .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();
            var artifact = new PreviewNifBinaryExportArtifact("1", "preview-nif-binary-export",
                request.Edition.ToWireName(), sceneHash.Value, finalPath, outputHash.Value,
                outputInfo.Length, status.MeshCount, assets, "Blender/PyNifly 27.4.0",
                status.ImportMode ?? (morphs.IsDefaultOrEmpty ? "mesh-only-import" : "mesh-plus-tri-bake"),
                morphs, status.MorphDeformed,
                sourceAggregate?.Value,
                outputGeometry.AggregateGeometrySha256.Value,
                morphDependencies, status.ArmatureCount, status.DeformationMode, scene.HairZap,
                status.HairZapApplied, status.HairZapAffectedMeshCount, status.HairZapRemovedFaceCount,
                status.FaceCullApplied, status.FaceCullAffectedMeshCount);
            return new PreviewNifBinaryExportResult(true, artifact, outputHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-io", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics, sceneHash);
        }
        finally
        {
            TryDelete(temporaryOutput);
            TryDelete(requestPath);
            TryDelete(statusPath);
        }
    }

}
