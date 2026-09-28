using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Resolves one canonical FaceGeom provider and delegates the actual TRI-to-NIF
/// bake to the admitted exporter. Source and destination are kept in separate
/// K-local trees, and the provider hash is checked again immediately before the
/// exporter is invoked.
/// </summary>
public sealed class FaceGeomProviderBoundBuildService(
    IFaceGenProviderResolutionService providerResolution,
    IFaceGeomBinaryBuildService geomBuilder,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGeomProviderBoundBuildService
{
    public async ValueTask<FaceGeomProviderBoundBuildResult> BuildAsync(
        FaceGeomProviderBoundBuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(ValidateRoots(request));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var providerResult = await providerResolution.ResolveAsync(new FaceGenProviderResolutionRequest(
            request.Edition, request.DataRoot, request.NpcFormId, request.PluginOrder), cancellationToken);
        diagnostics.AddRange(providerResult.Diagnostics);
        if (!providerResult.Resolved || providerResult.Artifact is null)
            return Refused(diagnostics);

        var providerMatches = providerResult.Artifact.Artifacts
            .Where(item => item.Kind == FaceGenProviderArtifactKind.FaceGeom).ToImmutableArray();
        if (providerMatches.Length != 1)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-kind-missing", DiagnosticSeverity.Error,
                $"Expected exactly one canonical FaceGeom provider route for {request.Edition.ToWireName()}, found {providerMatches.Length}."));
            return Refused(diagnostics);
        }
        var provider = providerMatches[0];
        var looseProviders = provider.Providers
            .Where(item => item.Kind == AssetProviderKind.Loose).ToImmutableArray();
        if (looseProviders.Length != 1)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-loose-provider-required", DiagnosticSeverity.Error,
                $"Canonical provider '{provider.CanonicalPath.Value}' must resolve to exactly one copied loose file; found {looseProviders.Length}."));
            return Refused(diagnostics);
        }

        var sourcePath = Path.Combine(request.DataRoot.Value,
            provider.CanonicalPath.Value.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath))
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-source-missing", DiagnosticSeverity.Error,
                $"Canonical provider source '{provider.CanonicalPath.Value}' is missing from the copied Data root."));
            return Refused(diagnostics);
        }
        try
        {
            if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic("facegeom-bound-provider-source-reparse", DiagnosticSeverity.Error,
                    "The canonical FaceGeom provider source may not be a reparse point."));
                return Refused(diagnostics);
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-source-stat-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-source-stat-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        string sourceHash;
        try
        {
            await using var sourceStream = File.OpenRead(sourcePath);
            sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceStream, cancellationToken));
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-source-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-source-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        if (!string.Equals(sourceHash, looseProviders[0].Sha256.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-provider-hash-mismatch", DiagnosticSeverity.Error,
                $"Canonical provider hash changed between resolution and build for '{provider.CanonicalPath.Value}'."));
            return Refused(diagnostics);
        }

        AssetPath outputAssetPath;
        try { outputAssetPath = new AssetPath(provider.CanonicalPath.Value); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-output-path-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        var outputPath = new WorkspacePath(Path.GetFullPath(Path.Combine(request.OutputRoot.Value,
            outputAssetPath.Value.Replace('/', Path.DirectorySeparatorChar))));
        if (!outputPath.IsUnder(request.OutputRoot) || !outputPath.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic("facegeom-bound-output-path-unsafe", DiagnosticSeverity.Error,
                "The derived canonical FaceGeom output escaped the K-local output root."));
            return Refused(diagnostics);
        }

        var build = await geomBuilder.BuildAsync(new FaceGeomBinaryBuildRequest(
            request.Edition, request.DataRoot, new WorkspacePath(sourcePath), outputPath,
            request.Morphs), cancellationToken);
        diagnostics.AddRange(build.Diagnostics);
        if (!build.Written || build.Artifact is null || build.OutputSha256 is null)
            return Refused(diagnostics);
        if (!string.Equals(build.Artifact.InputSourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(build.Artifact.OutputPath, outputPath.Value, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(outputPath.Value);
            diagnostics.Add(new Diagnostic("facegeom-bound-build-binding-mismatch", DiagnosticSeverity.Error,
                "The delegated FaceGeom exporter returned source or output evidence that does not match the resolved provider."));
            return Refused(diagnostics);
        }

        var artifact = new FaceGeomProviderBoundBuildArtifact(
            "1", "facegeom-provider-bound-build", request.Edition.ToWireName(),
            providerResult.Artifact.NpcFormId, providerResult.Artifact.LocalFormId,
            providerResult.Artifact.OriginatingPlugin, providerResult.Artifact.WinningPlugin,
            providerResult.Artifact.OverrideChain, "faceGeom", outputAssetPath, outputAssetPath,
            sourceHash, build.Artifact);
        return new FaceGeomProviderBoundBuildResult(true, artifact, build.OutputSha256,
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateRoots(FaceGeomProviderBoundBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        if (request.PluginOrder.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("facegeom-bound-plugin-order-required", DiagnosticSeverity.Error,
                "Provider-bound FaceGeom builds require an explicit plugin load order."));
        else if (request.PluginOrder.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
            diagnostics.Add(new Diagnostic("facegeom-bound-duplicate-plugin", DiagnosticSeverity.Error,
                "Provider-bound FaceGeom builds reject duplicate plugin names."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(new Diagnostic("facegeom-bound-data-root-missing", DiagnosticSeverity.Error,
                "The copied Data root must already exist."));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("facegeom-bound-output-root-outside-lab", DiagnosticSeverity.Error,
                "Provider-bound FaceGeom outputs must remain under the K-only lab root."));
        if (!Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("facegeom-bound-output-root-missing", DiagnosticSeverity.Error,
                "The provider-bound output root must already exist."));
        if (Overlaps(request.DataRoot, request.OutputRoot))
            diagnostics.Add(new Diagnostic("facegeom-bound-root-overlap", DiagnosticSeverity.Error,
                "Provider-bound source and output roots must be distinct and non-overlapping."));
        return diagnostics.ToImmutable();
    }

    private static bool Overlaps(WorkspacePath left, WorkspacePath right) =>
        left.IsUnder(right) || right.IsUnder(left);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGeomProviderBoundBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
