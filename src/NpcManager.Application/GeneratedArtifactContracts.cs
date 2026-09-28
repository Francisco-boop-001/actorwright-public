using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>The TES4 author marker used by the pinned reference application's generated plugins.</summary>
public static class GeneratedArtifactMarkers
{
    public const string ReferenceAuthor = "NPC Manager";
}

public sealed record GeneratedArtifactScanRequest(GameEdition Edition, WorkspacePath DataRoot);

public sealed record GeneratedPluginScanEntry(
    PluginName Plugin,
    WorkspacePath Path,
    string Author,
    Sha256Hash? Sha256,
    bool ReadSucceeded,
    ImmutableArray<FormId> NpcFormIds);

public enum GeneratedSidecarKind
{
    FaceGeom,
    FaceCustomizationDiffuse,
    FaceCustomizationNormal,
    FaceCustomizationSpecular,
    FaceTint,
    FaceDiffuse,
    FaceNormal,
    FaceDetailNeutral
}

public enum GeneratedSidecarVariant
{
    Canonical,
    DebugSandbox
}

public sealed record GeneratedSidecarEntry(
    PluginName OriginPlugin,
    GeneratedSidecarKind Kind,
    GeneratedSidecarVariant Variant,
    AssetPath RelativePath,
    WorkspacePath Path,
    FormId? FormId,
    long Size,
    Sha256Hash Sha256);

public sealed record GeneratedArtifactScanResult(
    GameEdition Edition,
    WorkspacePath DataRoot,
    string MarkerAuthor,
    ImmutableArray<GeneratedPluginScanEntry> Plugins,
    ImmutableArray<GeneratedSidecarEntry> Sidecars,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IGeneratedArtifactScanService
{
    ValueTask<GeneratedArtifactScanResult> ScanAsync(
        GeneratedArtifactScanRequest request, CancellationToken cancellationToken);
}

public static class GeneratedSidecarKindExtensions
{
    public static string ToWireName(this GeneratedSidecarKind kind) => kind switch
    {
        GeneratedSidecarKind.FaceGeom => "faceGeom",
        GeneratedSidecarKind.FaceCustomizationDiffuse => "faceCustomizationDiffuse",
        GeneratedSidecarKind.FaceCustomizationNormal => "faceCustomizationNormal",
        GeneratedSidecarKind.FaceCustomizationSpecular => "faceCustomizationSpecular",
        GeneratedSidecarKind.FaceTint => "faceTint",
        GeneratedSidecarKind.FaceDiffuse => "faceDiffuse",
        GeneratedSidecarKind.FaceNormal => "faceNormal",
        GeneratedSidecarKind.FaceDetailNeutral => "faceDetailNeutral",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported generated sidecar kind.")
    };

    public static string ToWireName(this GeneratedSidecarVariant variant) => variant switch
    {
        GeneratedSidecarVariant.Canonical => "canonical",
        GeneratedSidecarVariant.DebugSandbox => "debugSandbox",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unsupported generated sidecar variant.")
    };
}
