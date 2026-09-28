using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Exact Manager-observed provider closure for one selected preset. Provider
/// files remain external; this request writes evidence only.
/// </summary>
public sealed record RaceMenuSelectedDependencyManifestWriteRequest(
    Sha256Hash PresetSha256,
    RaceMenuPresetRecordAuthorityDraft RecordDraft,
    ImmutableArray<SkyrimAssetAuthority> FaceGenDependencies,
    ImmutableArray<RaceMenuNpcExternalTextureAuthority> ExternalTextures,
    WorkspacePath Destination)
{
    public ImmutableArray<SkyrimAssetAuthority>
        ExternalProviderSidecars
    { get; init; } = [];

    /// <summary>
    /// Manager-owned, hash-bound external dependency groups. An empty value
    /// retains the exact schema-1/schema-2 writer lanes.
    /// </summary>
    public ImmutableArray<
        RaceMenuSelectedDependencyManifestExternalInstallDependency>
        ExternalInstallDependencies { get; init; } = [];
}

public sealed record RaceMenuSelectedDependencyManifestOutputPluginBinding(
    PluginName Plugin,
    Sha256Hash Sha256,
    long ByteLength,
    ImmutableArray<PluginName> Masters,
    ImmutableArray<FormReference> PnamBindings);

public sealed record RaceMenuSelectedDependencyManifestExternalInstallDependency(
    ExternalHeadPartDependencyDescriptor Descriptor,
    ExternalHeadPartFaceGeomExclusionAttestation Attestation,
    RaceMenuSelectedDependencyManifestOutputPluginBinding OutputPlugin,
    AssetPath FaceGeomPath,
    Sha256Hash FaceGeomSha256,
    long FaceGeomByteLength);

public sealed record RaceMenuSelectedDependencyManifestArtifact(
    string DependencyId,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    int HeadPartCount,
    int LooseAssetCount,
    int ArchiveCount,
    bool RuntimeAuthority)
{
    public int SchemaVersion { get; init; } = 1;

    public Sha256Hash PresetSha256 { get; init; }

    public ImmutableArray<
        RaceMenuSelectedDependencyManifestExternalInstallDependency>
        ExternalInstallDependencies { get; init; } = [];
}

public static class RaceMenuSelectedDependencyManifestPaths
{
    public const string CanonicalRelativePath =
        "Data/NPCManager/Evidence/selected-preset-dependencies.json";
}

public static class RaceMenuSelectedDependencyManifestAssetRules
{
    public static bool IsSupported(AssetPath path) =>
        path.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) &&
        (path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
         path.Value.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)) ||
        path.Value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) &&
        path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
}

public sealed record RaceMenuSelectedDependencyManifestWriteResult(
    bool Written,
    RaceMenuSelectedDependencyManifestArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuSelectedDependencyManifestWriter
{
    ValueTask<RaceMenuSelectedDependencyManifestWriteResult> WriteAsync(
        RaceMenuSelectedDependencyManifestWriteRequest request,
        CancellationToken cancellationToken);
}
