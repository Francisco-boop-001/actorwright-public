using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

/// <summary>
/// Identifies how one declared Skyrim asset is physically provided.
/// </summary>
public enum SkyrimAssetContentProviderKind
{
    Loose,
    Bsa
}

/// <summary>
/// Binds one canonical game asset to an exact K-local provider and content.
/// For loose assets, <paramref name="ProviderPath"/> is the file itself and the
/// provider and content hashes must be identical. For BSA assets, it is the
/// archive file and the hashes independently bind the archive and member.
/// </summary>
public sealed record SkyrimAssetContentAuthority(
    string ProviderId,
    SkyrimAssetContentProviderKind Kind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    AssetPath AssetPath,
    long ContentLength,
    Sha256Hash ContentSha256);

/// <summary>
/// Requests an all-or-nothing resolution of declared Skyrim asset content.
/// Every provider must be physically contained by <paramref name="AllowedRoot"/>.
/// </summary>
public sealed record SkyrimAssetContentResolutionRequest(
    WorkspacePath AllowedRoot,
    ImmutableArray<SkyrimAssetContentAuthority> Authorities);

/// <summary>
/// Immutable content returned only after provider and member authority pass.
/// </summary>
public sealed record ResolvedSkyrimAssetContent(
    string ProviderId,
    SkyrimAssetContentProviderKind Kind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    AssetPath AssetPath,
    long ContentLength,
    Sha256Hash ContentSha256,
    ImmutableArray<byte> Content);

public sealed record SkyrimAssetContentResolutionResult(
    bool Resolved,
    ImmutableArray<ResolvedSkyrimAssetContent> Assets,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimAssetContentResolver
{
    ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
        SkyrimAssetContentResolutionRequest request,
        CancellationToken cancellationToken);
}
