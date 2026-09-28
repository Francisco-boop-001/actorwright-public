using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimBsaMemberArtifact(
    AssetPath Path,
    long ByteLength,
    Sha256Hash Sha256,
    bool Matches);

public sealed record SkyrimBsaBuildRequest(
    WorkspacePath DataRoot,
    WorkspacePath OutputArchive,
    ImmutableArray<AssetPath> Members);

public sealed record SkyrimBsaBuildArtifact(
    WorkspacePath OutputArchive,
    Sha256Hash ArchiveSha256,
    int Version,
    bool Compressed,
    ImmutableArray<SkyrimBsaMemberArtifact> Members,
    bool RuntimeAuthority);

public sealed record SkyrimBsaBuildResult(
    bool Written,
    SkyrimBsaBuildArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimBsaVerifyRequest(
    WorkspacePath Archive,
    ImmutableArray<SkyrimBsaMemberArtifact> ExpectedMembers);

public sealed record SkyrimBsaVerifyResult(
    bool Verified,
    Sha256Hash? ArchiveSha256,
    ImmutableArray<SkyrimBsaMemberArtifact> Members,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimBsaExtractRequest(
    WorkspacePath Archive,
    WorkspacePath OutputRoot);

public sealed record SkyrimBsaExtractArtifact(
    WorkspacePath Archive,
    WorkspacePath OutputRoot,
    Sha256Hash ArchiveSha256,
    ImmutableArray<SkyrimBsaMemberArtifact> Members,
    bool RuntimeAuthority);

public sealed record SkyrimBsaExtractResult(
    bool Extracted,
    SkyrimBsaExtractArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimBsaService
{
    ValueTask<SkyrimBsaBuildResult> BuildAsync(
        SkyrimBsaBuildRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimBsaVerifyResult> VerifyAsync(
        SkyrimBsaVerifyRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimBsaExtractResult> ExtractAsync(
        SkyrimBsaExtractRequest request,
        CancellationToken cancellationToken);
}
