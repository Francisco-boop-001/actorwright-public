using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimRaceMenuCatalogAuthorityRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> LoadedPlugins);

/// <summary>
/// Exact winner-resolved RaceMenu config inputs for one copied load order.
/// The parser request contains races.ini, referenced .slider files, and
/// morphs.ini bytes; authorities retain their loose/BSA provenance.
/// </summary>
public sealed record SkyrimRaceMenuCatalogAuthorityResult(
    bool Accepted,
    SkyrimRaceMenuCatalogParseRequest? CatalogRequest,
    ImmutableArray<SkyrimAssetAuthority> Authorities,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimRaceMenuCatalogAuthorityLoader
{
    ValueTask<SkyrimRaceMenuCatalogAuthorityResult> LoadAsync(
        SkyrimRaceMenuCatalogAuthorityRequest request,
        CancellationToken cancellationToken);
}
