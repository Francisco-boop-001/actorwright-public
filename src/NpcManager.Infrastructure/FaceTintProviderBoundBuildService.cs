using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Orchestrates canonical provider resolution and the existing FaceTint raster
/// builder. It reads a copied Data root, requires a loose canonical provider,
/// and writes only to a distinct K-local output root.
/// </summary>
public sealed class FaceTintProviderBoundBuildService(
    IFaceGenProviderResolutionService providerResolution,
    IFaceTintBuildService tintBuilder,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceTintProviderBoundBuildService
{
    private const int MaxManifestBytes = 4 * 1024 * 1024;

    public async ValueTask<FaceTintProviderBoundBuildResult> BuildAsync(
        FaceTintProviderBoundBuildRequest request, CancellationToken cancellationToken)
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

        var expectedKind = request.Edition == GameEdition.Fallout4
            ? FaceGenProviderArtifactKind.FaceCustomizationDiffuse
            : FaceGenProviderArtifactKind.FaceTint;
        var providerMatches = providerResult.Artifact.Artifacts
            .Where(item => item.Kind == expectedKind).ToImmutableArray();
        if (providerMatches.Length != 1)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-provider-kind-missing", DiagnosticSeverity.Error,
                $"Expected exactly one canonical {expectedKind} provider route for {request.Edition.ToWireName()}, found {providerMatches.Length}."));
            return Refused(diagnostics);
        }
        var provider = providerMatches[0];

        var looseProviders = provider.Providers
            .Where(item => item.Kind == AssetProviderKind.Loose)
            .ToImmutableArray();
        if (looseProviders.Length != 1)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-loose-provider-required", DiagnosticSeverity.Error,
                $"Canonical provider '{provider.CanonicalPath.Value}' must resolve to exactly one copied loose file; found {looseProviders.Length}."));
            return Refused(diagnostics);
        }

        var sourcePath = Path.Combine(request.DataRoot.Value,
            provider.CanonicalPath.Value.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath))
        {
            diagnostics.Add(new Diagnostic("facetint-bound-provider-source-missing", DiagnosticSeverity.Error,
                $"Canonical provider source '{provider.CanonicalPath.Value}' is missing from the copied Data root."));
            return Refused(diagnostics);
        }
        try
        {
            if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic("facetint-bound-provider-source-reparse", DiagnosticSeverity.Error,
                    "The canonical FaceTint provider source may not be a reparse point."));
                return Refused(diagnostics);
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-provider-source-stat-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-provider-source-stat-denied", DiagnosticSeverity.Error,
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
            diagnostics.Add(new Diagnostic("facetint-bound-provider-source-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-provider-source-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        if (!string.Equals(sourceHash, looseProviders[0].Sha256.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("facetint-bound-provider-hash-mismatch", DiagnosticSeverity.Error,
                $"Canonical provider hash changed between resolution and build for '{provider.CanonicalPath.Value}'."));
            return Refused(diagnostics);
        }

        var manifestBinding = await ValidateManifestBindingAsync(request.ManifestPath,
            request.NpcFormId, provider.CanonicalPath, diagnostics, cancellationToken);
        if (!manifestBinding) return Refused(diagnostics);

        AssetPath outputAssetPath;
        try { outputAssetPath = new AssetPath(provider.CanonicalPath.Value); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-output-path-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        var outputTexturePath = new WorkspacePath(Path.GetFullPath(Path.Combine(request.OutputRoot.Value,
            outputAssetPath.Value.Replace('/', Path.DirectorySeparatorChar))));
        if (!outputTexturePath.IsUnder(request.OutputRoot) || !outputTexturePath.IsUnder(labRoot))
        {
            diagnostics.Add(new Diagnostic("facetint-bound-output-path-unsafe", DiagnosticSeverity.Error,
                "The derived canonical FaceTint output escaped the K-local output root."));
            return Refused(diagnostics);
        }

        var build = await tintBuilder.BuildAsync(new FaceTintBuildRequest(
            request.Edition, request.ManifestPath, request.OutputPath, request.NpcFormId,
            null, request.Format, request.MipCount, request.AlphaMode,
            outputTexturePath, request.DataRoot), cancellationToken);
        diagnostics.AddRange(build.Diagnostics);
        if (!build.Written || build.Artifact is null || build.OutputSha256 is null)
            return new FaceTintProviderBoundBuildResult(false, null, null, build.TextureOutputSha256,
                diagnostics.ToImmutable());
        var boundSources = build.Artifact.ProviderSources is { } sources
            ? sources.Where(item => string.Equals(NormalizeAsset(item.Source), provider.CanonicalPath.Value,
                StringComparison.OrdinalIgnoreCase)).ToImmutableArray()
            : ImmutableArray<FaceTintProviderBinding>.Empty;
        if (boundSources.IsDefaultOrEmpty || boundSources.Any(item =>
                !string.Equals(item.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(request.OutputPath.Value);
            TryDelete(outputTexturePath.Value);
            diagnostics.Add(new Diagnostic("facetint-bound-build-binding-mismatch", DiagnosticSeverity.Error,
                "The delegated FaceTint builder returned provider evidence that does not match the resolved source."));
            return new FaceTintProviderBoundBuildResult(false, null, null, null, diagnostics.ToImmutable());
        }

        var artifact = new FaceTintProviderBoundBuildArtifact(
            "1", "facetint-provider-bound-build", request.Edition.ToWireName(),
            providerResult.Artifact.NpcFormId, providerResult.Artifact.LocalFormId,
            providerResult.Artifact.OriginatingPlugin, providerResult.Artifact.WinningPlugin,
            providerResult.Artifact.OverrideChain, ProviderKindName(expectedKind), outputAssetPath,
            outputAssetPath, sourceHash, build.Artifact);
        return new FaceTintProviderBoundBuildResult(true, artifact, build.OutputSha256,
            build.TextureOutputSha256, diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateRoots(FaceTintProviderBoundBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ManifestPath));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        if (request.PluginOrder.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("facetint-bound-plugin-order-required", DiagnosticSeverity.Error,
                "Provider-bound FaceTint builds require an explicit plugin load order."));
        else if (request.PluginOrder.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
            diagnostics.Add(new Diagnostic("facetint-bound-duplicate-plugin", DiagnosticSeverity.Error,
                "Provider-bound FaceTint builds reject duplicate plugin names."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(new Diagnostic("facetint-bound-data-root-missing", DiagnosticSeverity.Error,
                "The copied Data root must already exist."));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("facetint-bound-output-root-outside-lab", DiagnosticSeverity.Error,
                "Provider-bound FaceTint outputs must remain under the K-only lab root."));
        if (!Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("facetint-bound-output-root-missing", DiagnosticSeverity.Error,
                "The provider-bound output root must already exist."));
        if (Overlaps(request.DataRoot, request.OutputRoot))
            diagnostics.Add(new Diagnostic("facetint-bound-root-overlap", DiagnosticSeverity.Error,
                "Provider-bound source and output roots must be distinct and non-overlapping."));
        return diagnostics.ToImmutable();
    }

    private static async ValueTask<bool> ValidateManifestBindingAsync(
        WorkspacePath manifestPath, FormId expectedNpc, AssetPath canonicalPath,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(manifestPath.Value);
            if (info.Length > MaxManifestBytes)
            {
                diagnostics.Add(new Diagnostic("facetint-bound-manifest-size-limit", DiagnosticSeverity.Error,
                    $"FaceTint manifests may not exceed {MaxManifestBytes} bytes."));
                return false;
            }
            var bytes = await File.ReadAllBytesAsync(manifestPath.Value, cancellationToken);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("facetint-bound-manifest-root", DiagnosticSeverity.Error,
                    "A provider-bound FaceTint manifest must contain an object."));
                return false;
            }
            if (!TryGetProperty(document.RootElement, "npcFormId", out var npcElement))
            {
                diagnostics.Add(new Diagnostic("facetint-bound-manifest-npc-required", DiagnosticSeverity.Error,
                    $"The manifest must declare npcFormId matching the requested NPC {expectedNpc}."));
                return false;
            }
            if (npcElement.ValueKind != JsonValueKind.String ||
                !FormId.TryParse(npcElement.GetString() ?? string.Empty, out var manifestNpc) ||
                manifestNpc != expectedNpc)
            {
                diagnostics.Add(new Diagnostic("facetint-bound-manifest-npc-mismatch", DiagnosticSeverity.Error,
                    $"The manifest npcFormId must match the requested NPC {expectedNpc}."));
                return false;
            }
            if (!TryGetProperty(document.RootElement, "layers", out var layers) ||
                layers.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new Diagnostic("facetint-bound-manifest-layers-required", DiagnosticSeverity.Error,
                    "A provider-bound FaceTint manifest must contain a layers array."));
                return false;
            }
            var found = layers.EnumerateArray().Any(layer =>
                layer.ValueKind == JsonValueKind.Object &&
                TryGetProperty(layer, "source", out var source) &&
                source.ValueKind == JsonValueKind.String &&
                string.Equals(NormalizeAsset(source.GetString() ?? string.Empty), canonicalPath.Value,
                    StringComparison.OrdinalIgnoreCase));
            if (!found)
            {
                diagnostics.Add(new Diagnostic("facetint-bound-manifest-provider-source-missing", DiagnosticSeverity.Error,
                    $"The manifest must reference the resolved canonical provider path '{canonicalPath.Value}'."));
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-manifest-json-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return false;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facetint-bound-manifest-read-denied", DiagnosticSeverity.Error,
                exception.Message));
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string NormalizeAsset(string value) => value.Trim().Replace('\\', '/');

    private static string ProviderKindName(FaceGenProviderArtifactKind kind) => kind switch
    {
        FaceGenProviderArtifactKind.FaceCustomizationDiffuse => "faceCustomizationDiffuse",
        FaceGenProviderArtifactKind.FaceTint => "faceTint",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported provider-bound tint kind.")
    };

    private static bool Overlaps(WorkspacePath left, WorkspacePath right) =>
        left.IsUnder(right) || right.IsUnder(left);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceTintProviderBoundBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
