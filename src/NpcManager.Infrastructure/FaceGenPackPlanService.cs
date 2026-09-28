using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Builds a K-local, read-only bundle plan for the pinned FaceGen packer contract.
/// It mirrors the upstream source/entry split and required/optional rules without
/// writing archives or deleting loose bake outputs.
/// </summary>
public sealed class FaceGenPackPlanService(
    IFaceGenProviderResolutionService providerResolution,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGenPackPlanService
{
    public async ValueTask<FaceGenPackPlanResult> PlanAsync(
        FaceGenPackPlanRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (request.PluginOrder.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("facegen-pack-plugin-order-required", DiagnosticSeverity.Error,
                "FaceGen pack planning requires an explicit load order."));
        }
        else if (request.PluginOrder.Select(item => item.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(new Diagnostic("facegen-pack-duplicate-plugin", DiagnosticSeverity.Error,
                "FaceGen pack planning requires a load order without duplicate plugin names."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var anchorPath = Path.Combine(request.DataRoot.Value, request.AnchorPlugin.Value);
        if (!File.Exists(anchorPath))
        {
            diagnostics.Add(new Diagnostic("facegen-pack-anchor-missing", DiagnosticSeverity.Error,
                $"Anchor plugin '{request.AnchorPlugin.Value}' does not exist under the copied Data root."));
        }
        else if (File.GetAttributes(anchorPath).HasFlag(FileAttributes.ReparsePoint))
        {
            diagnostics.Add(new Diagnostic("facegen-pack-anchor-reparse", DiagnosticSeverity.Error,
                $"Anchor plugin '{request.AnchorPlugin.Value}' is a reparse point."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var providerResult = await providerResolution.ResolveAsync(new FaceGenProviderResolutionRequest(
            request.Edition, request.DataRoot, request.NpcFormId, request.PluginOrder,
            request.UsesSharedNeutralDetail), cancellationToken);
        diagnostics.AddRange(providerResult.Diagnostics);
        if (providerResult.Artifact is null || !providerResult.Resolved)
            return Refused(diagnostics);

        var entries = ImmutableArray.CreateBuilder<FaceGenPackPlanEntry>();
        foreach (var provider in providerResult.Artifact.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = request.DebugSandbox
                ? ToDebugSourcePath(provider.CanonicalPath)
                : provider.CanonicalPath;
            var source = InspectSource(request.DataRoot, sourcePath, provider.Requiredness,
                provider.Kind, diagnostics);
            entries.Add(new FaceGenPackPlanEntry(provider.Kind.ToWireName(), sourcePath,
                provider.CanonicalPath, ArchiveRole(provider.Kind),
                provider.Requiredness == FaceGenProviderRequiredness.Required, source.Status,
                source.Size, source.Sha256));
        }

        var anchorBaseName = Path.GetFileNameWithoutExtension(request.AnchorPlugin.Value);
        var artifact = new FaceGenPackPlanArtifact(
            "1", "facegen-pack-plan", request.Edition.ToWireName(),
            providerResult.Artifact.NpcFormId, providerResult.Artifact.LocalFormId,
            providerResult.Artifact.OriginatingPlugin, providerResult.Artifact.WinningPlugin,
            request.AnchorPlugin.Value, anchorBaseName, request.DebugSandbox,
            request.UsesSharedNeutralDetail,
            entries.All(item => item.Status is FaceGenPackSourceStatus.Present or FaceGenPackSourceStatus.MissingOptional),
            providerResult.Artifact.OverrideChain, entries.ToImmutable());
        return new FaceGenPackPlanResult(true, artifact, diagnostics.ToImmutable());
    }

    private static AssetPath ToDebugSourcePath(AssetPath canonical)
    {
        var slash = canonical.Value.LastIndexOf('/');
        var directory = slash >= 0 ? canonical.Value[..(slash + 1)] : string.Empty;
        var file = slash >= 0 ? canonical.Value[(slash + 1)..] : canonical.Value;
        var extension = Path.GetExtension(file);
        var stem = extension.Length == 0 ? file : file[..^extension.Length];
        return new AssetPath(directory + stem + "_2" + extension);
    }

    private static SourceInspection InspectSource(WorkspacePath dataRoot, AssetPath sourcePath,
        FaceGenProviderRequiredness requiredness, FaceGenProviderArtifactKind kind,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var full = Path.GetFullPath(Path.Combine(dataRoot.Value,
            sourcePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(full, dataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("facegen-pack-source-outside-data", DiagnosticSeverity.Error,
                $"FaceGen source '{sourcePath.Value}' escaped the copied Data root."));
            return new SourceInspection(FaceGenPackSourceStatus.MissingRequired, null, null);
        }
        if (!File.Exists(full))
        {
            var status = requiredness == FaceGenProviderRequiredness.Required
                ? FaceGenPackSourceStatus.MissingRequired
                : FaceGenPackSourceStatus.MissingOptional;
            diagnostics.Add(new Diagnostic("facegen-pack-source-missing",
                requiredness == FaceGenProviderRequiredness.Required ? DiagnosticSeverity.Error : DiagnosticSeverity.Info,
                $"FaceGen {kind.ToWireName()} source '{sourcePath.Value}' is missing from the copied Data root."));
            return new SourceInspection(status, null, null);
        }
        try
        {
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic("facegen-pack-source-reparse", DiagnosticSeverity.Error,
                    $"FaceGen source '{sourcePath.Value}' is a reparse point."));
                return new SourceInspection(FaceGenPackSourceStatus.MissingRequired, null, null);
            }
            using var stream = File.OpenRead(full);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return new SourceInspection(FaceGenPackSourceStatus.Present, stream.Length, new Sha256Hash(hash));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("facegen-pack-source-unreadable", DiagnosticSeverity.Error,
                $"FaceGen source '{sourcePath.Value}' could not be read: {exception.Message}"));
            return new SourceInspection(FaceGenPackSourceStatus.MissingRequired, null, null);
        }
    }

    private static FaceGenPackArchiveRole ArchiveRole(FaceGenProviderArtifactKind kind) =>
        kind == FaceGenProviderArtifactKind.FaceGeom ? FaceGenPackArchiveRole.Main : FaceGenPackArchiveRole.Textures;

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGenPackPlanResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record SourceInspection(FaceGenPackSourceStatus Status, long? Size, Sha256Hash? Sha256);
}

internal static class FaceGenPackPlanWireExtensions
{
    internal static string ToWireName(this FaceGenProviderArtifactKind kind) => kind switch
    {
        FaceGenProviderArtifactKind.FaceGeom => "faceGeom",
        FaceGenProviderArtifactKind.FaceTint => "faceTint",
        FaceGenProviderArtifactKind.FaceCustomizationDiffuse => "faceCustomizationDiffuse",
        FaceGenProviderArtifactKind.FaceCustomizationNormal => "faceCustomizationNormal",
        FaceGenProviderArtifactKind.FaceCustomizationSpecular => "faceCustomizationSpecular",
        FaceGenProviderArtifactKind.FaceDetailNeutral => "faceDetailNeutral",
        FaceGenProviderArtifactKind.FaceDiffuse => "faceDiffuse",
        FaceGenProviderArtifactKind.FaceNormal => "faceNormal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported FaceGen provider kind.")
    };
}
