using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Exact loose or archive winner for one record-declared Skyrim asset.
/// </summary>
public sealed record SkyrimAssetAuthority(
    string ProviderId,
    AssetProviderKind ProviderKind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    AssetPath AssetPath,
    long ContentLength,
    Sha256Hash ContentSha256);

/// <summary>
/// Plans winners from a flattened copied Data root. Loose files win; an
/// archive-only path is accepted only when exactly one BSA provides it.
/// </summary>
public sealed record SkyrimAssetAuthorityPlanRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<AssetPath> RequiredAssets,
    ImmutableArray<AssetPath> OptionalAssets = default);

public sealed record SkyrimAssetAuthorityPlanResult(
    bool Accepted,
    ImmutableArray<SkyrimAssetAuthority> Authorities,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<AssetPath> UnavailableOptionalAssets = default);

public interface ISkyrimAssetAuthorityPlanner
{
    ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
        SkyrimAssetAuthorityPlanRequest request,
        CancellationToken cancellationToken);
}
