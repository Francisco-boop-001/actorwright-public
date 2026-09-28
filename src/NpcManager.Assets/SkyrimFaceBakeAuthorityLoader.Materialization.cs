using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

public sealed partial class SkyrimFaceBakeAuthorityLoader
{
    private static SkyrimFaceBakeAuthority? Materialize(
        ParsedAuthority parsed,
        ImmutableArray<ResolvedSkyrimAssetContent> resolved,
        Sha256Hash manifestSha256,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (resolved.IsDefault || resolved.Length != parsed.Assets.Length)
        {
            diagnostics.Add(Error("face-bake-authority-content-count-drift",
                $"Content resolver returned {resolved.Length} of {parsed.Assets.Length} declared assets."));
            return null;
        }

        Dictionary<string, ResolvedSkyrimAssetContent> byPath;
        try
        {
            byPath = resolved.ToDictionary(item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            diagnostics.Add(Error("face-bake-authority-content-path-duplicate",
                "Content resolver returned duplicate canonical asset paths."));
            return null;
        }

        ImmutableArray<SkyrimFaceBakeAuthorityAsset>.Builder assets =
            ImmutableArray.CreateBuilder<SkyrimFaceBakeAuthorityAsset>(parsed.Assets.Length);
        Dictionary<string, SkyrimFaceBakeAuthorityAsset> byId = new(StringComparer.Ordinal);
        foreach (DeclaredAsset declared in parsed.Assets)
        {
            SkyrimAssetContentAuthority expected = declared.Authority;
            if (!byPath.TryGetValue(expected.AssetPath.Value, out ResolvedSkyrimAssetContent? item) ||
                item.ProviderId != expected.ProviderId ||
                item.Kind != expected.Kind ||
                item.ProviderPath != expected.ProviderPath ||
                item.ProviderSha256 != expected.ProviderSha256 ||
                item.AssetPath != expected.AssetPath ||
                item.ContentLength != expected.ContentLength ||
                item.ContentSha256 != expected.ContentSha256 ||
                item.Content.Length != expected.ContentLength)
            {
                diagnostics.Add(Error("face-bake-authority-content-drift",
                    $"Resolved content for '{declared.Id}' does not exactly match its manifest authority."));
                continue;
            }

            SkyrimFaceBakeAuthorityAsset materialized = new(
                declared.Id,
                item.ProviderId,
                item.Kind == SkyrimAssetContentProviderKind.Loose
                    ? SkyrimFaceBakeAuthorityProviderKind.Loose
                    : SkyrimFaceBakeAuthorityProviderKind.Bsa,
                item.ProviderPath,
                item.ProviderSha256,
                item.AssetPath,
                item.ContentLength,
                item.ContentSha256,
                item.Content);
            assets.Add(materialized);
            byId.Add(materialized.Id, materialized);
        }
        if (HasErrors(diagnostics) || assets.Count != parsed.Assets.Length)
        {
            return null;
        }

        ImmutableArray<SkyrimFaceBakeCatalogConfigAuthority> catalog = parsed.Catalog
            .Select(item => new SkyrimFaceBakeCatalogConfigAuthority(
                item.Plugin, byId[item.AssetId]))
            .ToImmutableArray();
        ImmutableArray<SkyrimFaceBakeShapeTriAuthority> triInputs = parsed.TriInputs
            .Select(item => new SkyrimFaceBakeShapeTriAuthority(
                item.CarrierShapeName,
                item.RaceAssetId is null ? null : byId[item.RaceAssetId],
                item.ChargenAssetId is null ? null : byId[item.ChargenAssetId],
                item.MeshAssetId is null ? null : byId[item.MeshAssetId],
                item.ExtendedAssetIds.Select(id => byId[id]).ToImmutableArray()))
            .ToImmutableArray();
        ImmutableArray<SkyrimFaceBakeCarrierShapeAuthority> carriers = parsed.Carriers
            .Select(item => new SkyrimFaceBakeCarrierShapeAuthority(
                item.HeadPart,
                byId[item.ModelAssetId],
                item.ModelShapeName,
                item.CarrierShapeName,
                item.ExpectedModelPositionSha256,
                item.ExpectedModelTopologySha256,
                item.ExpectedCarrierTopologySha256))
            .ToImmutableArray();

        return new SkyrimFaceBakeAuthority(
            parsed.AuthorityId,
            parsed.LoadedPlugins,
            parsed.RecordPlugins,
            assets.ToImmutable(),
            catalog,
            triInputs,
            carriers,
            parsed.RecordOnlyMappedHeadParts,
            parsed.OptionalUnavailableExtensions,
            manifestSha256);
    }
}
