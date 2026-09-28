using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

internal sealed record PipelineArtifactWriteResult(
    bool Completed,
    ImmutableArray<PresetToNpcPackageArtifact> Artifacts,
    ImmutableArray<WorkspacePath> GeneratedPaths,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed class PresetToNpcArtifactWriter(
    IFaceGeomBuildService? faceGeomBuildService,
    IFaceTintBuildService? faceTintBuildService,
    IBodySidecarWriteService? bodySidecarWriteService,
    IRuntimeScriptDeployService? runtimeScriptDeployService)
{
    private static readonly JsonSerializerOptions RuntimeKitJsonOptions = new() { WriteIndented = true };

    public async ValueTask<PipelineArtifactWriteResult> WriteAsync(
        PresetToNpcPipelineRequest request,
        PresetDocument preset,
        BodyGenBuildResult? bodyGen,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var artifacts = ImmutableArray.CreateBuilder<PresetToNpcPackageArtifact>();
        var generatedPaths = new List<WorkspacePath>();

        if (bodyGen is not null)
        {
            artifacts.AddRange(bodyGen.Files.Select(file => new PresetToNpcPackageArtifact(
                "bodygen", file.RelativePath, file.ByteLength, file.Sha256)));
        }

        if (!preset.Appearance.BodyMorphs.IsEmpty)
        {
            if (bodySidecarWriteService is null)
            {
                diagnostics.Add(new Diagnostic("pipeline-body-sidecar-service-unavailable", DiagnosticSeverity.Error,
                    "BodySlide sidecar generation was requested but its writer is unavailable."));
                return Failed(artifacts, generatedPaths, diagnostics);
            }

            var sidecarPath = new WorkspacePath(Path.Combine(request.OutputRoot.Value,
                Path.GetFileNameWithoutExtension(request.OutputPlugin.Value) + ".bssliders"));
            var sidecar = await bodySidecarWriteService.WriteAsync(new BodySidecarWriteRequest(
                request.Edition, request.OutputPlugin, request.TargetFormId, request.OutputEditorId,
                preset.Appearance.BodyMorphs, sidecarPath), cancellationToken);
            diagnostics.AddRange(sidecar.Diagnostics);
            if (!sidecar.Written || sidecar.OutputHash is null)
                return Failed(artifacts, generatedPaths, diagnostics);

            generatedPaths.Add(sidecarPath);
            var sidecarInfo = new FileInfo(sidecarPath.Value);
            artifacts.Add(new PresetToNpcPackageArtifact(
                "bodyslide",
                RelativeArtifact(request.OutputRoot, sidecarPath),
                checked((int)sidecarInfo.Length),
                sidecar.OutputHash.Value));
        }

        if (request.FaceGeomManifest is not null)
        {
            if (faceGeomBuildService is null)
            {
                diagnostics.Add(new Diagnostic("pipeline-facegeom-service-unavailable", DiagnosticSeverity.Error,
                    "FaceGeom generation was requested but its builder is unavailable."));
                return Failed(artifacts, generatedPaths, diagnostics);
            }

            var faceGeomPath = NewAppearanceArtifactPath(request, "FaceGeom", ".facegeom.json");
            var faceGeom = await faceGeomBuildService.BuildAsync(new FaceGeomBuildRequest(
                request.Edition, request.FaceGeomManifest.Value, faceGeomPath, request.TargetFormId), cancellationToken);
            diagnostics.AddRange(faceGeom.Diagnostics);
            if (!faceGeom.Written || faceGeom.OutputSha256 is null)
                return Failed(artifacts, generatedPaths, diagnostics);

            generatedPaths.Add(faceGeomPath);
            artifacts.Add(new PresetToNpcPackageArtifact(
                "facegeom", RelativeArtifact(request.OutputRoot, faceGeomPath),
                checked((int)new FileInfo(faceGeomPath.Value).Length), faceGeom.OutputSha256.Value));
        }

        if (request.FaceTintManifest is not null)
        {
            if (faceTintBuildService is null)
            {
                diagnostics.Add(new Diagnostic("pipeline-facetint-service-unavailable", DiagnosticSeverity.Error,
                    "FaceTint generation was requested but its builder is unavailable."));
                return Failed(artifacts, generatedPaths, diagnostics);
            }

            var faceTintPath = NewAppearanceArtifactPath(request, "FaceTint", ".facetint.json");
            var faceTint = await faceTintBuildService.BuildAsync(new FaceTintBuildRequest(
                request.Edition, request.FaceTintManifest.Value, faceTintPath, request.TargetFormId,
                null, null, null, null), cancellationToken);
            diagnostics.AddRange(faceTint.Diagnostics);
            if (!faceTint.Written || faceTint.OutputSha256 is null)
                return Failed(artifacts, generatedPaths, diagnostics);

            generatedPaths.Add(faceTintPath);
            artifacts.Add(new PresetToNpcPackageArtifact(
                "facetint", RelativeArtifact(request.OutputRoot, faceTintPath),
                checked((int)new FileInfo(faceTintPath.Value).Length), faceTint.OutputSha256.Value));
        }

        if (request.RuntimeScriptBuild is not null)
        {
            var source = request.RuntimeScriptBuild.Value;
            if (!File.Exists(source.Value))
            {
                diagnostics.Add(new Diagnostic("pipeline-runtime-script-build-invalid", DiagnosticSeverity.Error,
                    "Runtime script evidence must be an existing K-local file."));
                return Failed(artifacts, generatedPaths, diagnostics);
            }

            var scriptDirectory = Path.Combine(request.OutputRoot.Value, "Scripts");
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = new WorkspacePath(Path.Combine(scriptDirectory, Path.GetFileName(source.Value)));
            CopyAtomically(source, scriptPath);
            generatedPaths.Add(scriptPath);
            artifacts.Add(new PresetToNpcPackageArtifact(
                "runtime-script-build", RelativeArtifact(request.OutputRoot, scriptPath),
                checked((int)new FileInfo(scriptPath.Value).Length), HashFile(scriptPath.Value)));
        }

        if (request.RuntimeScriptPackage is not null)
        {
            if (runtimeScriptDeployService is null)
            {
                diagnostics.Add(new Diagnostic("pipeline-runtime-script-package-service-unavailable", DiagnosticSeverity.Error,
                    "Runtime script package deployment was requested but its service is unavailable."));
                return Failed(artifacts, generatedPaths, diagnostics);
            }

            var dataRoot = new WorkspacePath(Path.Combine(request.OutputRoot.Value, "Data"));
            Directory.CreateDirectory(dataRoot.Value);
            var deployment = await runtimeScriptDeployService.DeployAsync(new RuntimeScriptDeployRequest(
                request.Edition, request.RuntimeScriptPackage.Value, dataRoot), cancellationToken);
            diagnostics.AddRange(deployment.Diagnostics);
            if ((!deployment.Installed && !deployment.AlreadyPresent) || deployment.OutputSha256 is null)
                return Failed(artifacts, generatedPaths, diagnostics);

            generatedPaths.Add(deployment.Output);
            artifacts.Add(new PresetToNpcPackageArtifact(
                "runtime-script", RelativeArtifact(request.OutputRoot, deployment.Output),
                checked((int)new FileInfo(deployment.Output.Value).Length), deployment.OutputSha256.Value));
        }

        var runtimeKitPath = new WorkspacePath(Path.Combine(request.OutputRoot.Value, "runtime-test-instructions.json"));
        var runtimeKit = new
        {
            schemaVersion = "1",
            edition = request.Edition.ToWireName(),
            plugin = request.OutputPlugin.Value,
            npcFormId = request.TargetFormId.ToString(),
            runtimeProof = false,
            instructions = new[]
            {
                "Confirm the active plugin provider and FaceGeom/FaceTint hashes before testing.",
                "Run the supplied diagnostic batch for the target NPC and capture face, neck, body, hands, eyes, and outfit screenshots.",
                "Include a known-good control NPC in the same frame and lighting."
            }
        };
        var runtimeKitBytes = JsonSerializer.SerializeToUtf8Bytes(runtimeKit, RuntimeKitJsonOptions);
        WriteAtomically(runtimeKitPath, runtimeKitBytes, cancellationToken);
        generatedPaths.Add(runtimeKitPath);
        artifacts.Add(new PresetToNpcPackageArtifact(
            "runtime-kit", RelativeArtifact(request.OutputRoot, runtimeKitPath), runtimeKitBytes.Length,
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(runtimeKitBytes)))));

        return new PipelineArtifactWriteResult(true, artifacts.ToImmutable(), generatedPaths.ToImmutableArray(),
            diagnostics.ToImmutable());
    }

    private static PipelineArtifactWriteResult Failed(
        ImmutableArray<PresetToNpcPackageArtifact>.Builder artifacts,
        List<WorkspacePath> generatedPaths,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, artifacts.ToImmutable(), generatedPaths.ToImmutableArray(), diagnostics.ToImmutable());

    private static AssetPath RelativeArtifact(WorkspacePath root, WorkspacePath path) =>
        new(Path.GetRelativePath(root.Value, path.Value));

    private static WorkspacePath NewAppearanceArtifactPath(
        PresetToNpcPipelineRequest request, string kind, string suffix)
    {
        var directory = Path.Combine(request.OutputRoot.Value, "FaceGen", kind, request.OutputPlugin.Value);
        Directory.CreateDirectory(directory);
        return new WorkspacePath(Path.Combine(directory, $"{request.TargetFormId.Value:X6}{suffix}"));
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void CopyAtomically(WorkspacePath source, WorkspacePath destination)
    {
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source.Value, temporary, overwrite: false);
            File.Move(temporary, destination.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void WriteAtomically(WorkspacePath destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
