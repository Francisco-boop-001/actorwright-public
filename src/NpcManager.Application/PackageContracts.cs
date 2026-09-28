using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record PackageManifestFile(
    string Kind,
    AssetPath RelativePath,
    long ByteLength,
    Sha256Hash Sha256);

public sealed record PackageManifestIdentity(
    int SchemaVersion,
    string Edition,
    string PresetFormat,
    string SourcePreset,
    Sha256Hash SourcePresetSha256,
    string SourcePlugin,
    Sha256Hash SourcePluginSha256,
    string OutputPlugin,
    FormId TargetFormId,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    ImmutableArray<PackageManifestFile> Files);

public sealed record PackageInspectRequest(WorkspacePath ManifestPath);

public sealed record PackageInspectResult(
    bool Valid,
    PackageManifestIdentity? Identity,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPackageInspectService
{
    ValueTask<PackageInspectResult> InspectAsync(
        PackageInspectRequest request, CancellationToken cancellationToken);
}

public sealed record PackageVerifyRequest(WorkspacePath ManifestPath)
{
    public ExternalHeadPartInstallVerificationContext? InstallContext { get; init; }

    public bool RequireInstallDependencyAuthority { get; init; }
}

public sealed record PackageFileVerification(
    string Kind,
    AssetPath RelativePath,
    long ExpectedByteLength,
    long ActualByteLength,
    Sha256Hash ExpectedSha256,
    Sha256Hash ActualSha256,
    bool Matches);

public sealed record PackageVerificationArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string PresetFormat,
    string OutputPlugin,
    FormId TargetFormId,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    ImmutableArray<PackageFileVerification> Files,
    bool NoUndeclaredFiles,
    bool NoWrite,
    bool RuntimeProof);

public sealed record PackageVerifyResult(
    bool Verified,
    PackageVerificationArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExternalHeadPartInstallVerificationArtifact?
        ExternalInstallDependencyVerification { get; init; }
}

public interface IPackageVerifyService
{
    ValueTask<PackageVerifyResult> VerifyAsync(
        PackageVerifyRequest request, CancellationToken cancellationToken);
}

public sealed record PackageBuildRequest(
    WorkspacePath SourceRoot,
    WorkspacePath OutputRoot);

public sealed record PackageBuildArtifact(
    string SchemaVersion,
    string ArtifactKind,
    WorkspacePath SourceRoot,
    WorkspacePath OutputRoot,
    ImmutableArray<PackageManifestFile> Files,
    bool NoWriteToSource,
    bool RuntimeProof);

public sealed record PackageBuildResult(
    bool Written,
    PackageBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPackageBuildService
{
    ValueTask<PackageBuildResult> BuildAsync(
        PackageBuildRequest request, CancellationToken cancellationToken);
}

public enum PackageArchivePayload { Full, RuntimeOnly }

public sealed record PackageArchiveRequest(
    WorkspacePath SourceRoot,
    WorkspacePath OutputArchive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PackageArchivePayload Payload = PackageArchivePayload.Full,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AssetPath? IncludeRuntimePreset = null);

public sealed record PackageArchiveEntry(
    string Kind,
    AssetPath ArchivePath,
    AssetPath? SourceRelativePath,
    long ByteLength,
    Sha256Hash Sha256);

public sealed record PackageArchiveArtifact(
    string SchemaVersion,
    string ArtifactKind,
    WorkspacePath SourceRoot,
    WorkspacePath SourceManifest,
    Sha256Hash SourceManifestSha256,
    WorkspacePath Archive,
    Sha256Hash ArchiveSha256,
    ImmutableArray<PackageArchiveEntry> Entries,
    bool ForwardSlashEntries,
    bool NoWrapperDirectory,
    bool IndependentlyReopened,
    bool NoWriteToSource,
    bool RuntimeProof)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; init; }
}

public sealed record PackageArchiveResult(
    bool Written,
    PackageArchiveArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPackageArchiveService
{
    ValueTask<PackageArchiveResult> ArchiveAsync(
        PackageArchiveRequest request, CancellationToken cancellationToken);

    ValueTask<RuntimeArchiveVerificationResult> VerifyRuntimeAsync(
        RuntimeArchiveVerificationRequest request, CancellationToken cancellationToken);
}

public sealed record RuntimeArchiveVerificationRequest(WorkspacePath Archive, AssetPath? IncludeRuntimePreset = null);

public sealed record RuntimeArchiveVerificationArtifact(
    string SchemaVersion,
    string ArtifactKind,
    WorkspacePath Archive,
    Sha256Hash ArchiveSha256,
    ImmutableArray<PackageArchiveEntry> Entries,
    bool ForwardSlashEntries,
    bool NoWrapperDirectory,
    bool IndependentlyReopened,
    bool SourceManifestBound,
    bool InstallDependencyAuthority,
    bool RuntimeProof);

public sealed record RuntimeArchiveVerificationResult(bool Verified,
    RuntimeArchiveVerificationArtifact? Artifact, ImmutableArray<Diagnostic> Diagnostics);
