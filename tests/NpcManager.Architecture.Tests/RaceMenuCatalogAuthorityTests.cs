using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestRaceMenuCatalogAuthority()
    {
        string ownedRoot = Path.Combine(AppContext.BaseDirectory,
            "racemenu-catalog-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(ownedRoot, "Data");
        string catalogRoot = Path.Combine(dataRoot, "meshes", "actors",
            "character", "FaceGenMorphs", "RaceMenu.esp");
        Directory.CreateDirectory(catalogRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(catalogRoot, "races.ini"),
                "NordRace = Trixter.ini\n");
            await File.WriteAllTextAsync(Path.Combine(catalogRoot, "Trixter.ini"),
                "[Female]\nSmile = 16, Slider, SmileDown, SmileUp\n");
            await File.WriteAllTextAsync(Path.Combine(catalogRoot, "morphs.ini"),
                "extension = actors/character/character assets/femaleheadchargen.tri, EFM.tri\n");

            var counting = new CountingAssetIndexer(new BethesdaAssetIndexer());
            var scoped = new BatchScopedAssetIndexer(counting);
            var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var planner = new SkyrimAssetAuthorityPlanner(
                scoped, policy, labRoot);
            var loader = new SkyrimRaceMenuCatalogAuthorityLoader(
                scoped, planner,
                new SkyrimAssetContentResolver(policy, labRoot));

            SkyrimRaceMenuCatalogAuthorityResult result;
            using (scoped.BeginSnapshot())
            {
                result = await loader.LoadAsync(
                    new SkyrimRaceMenuCatalogAuthorityRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(dataRoot),
                        [new PluginName("RaceMenu.esp")]),
                    CancellationToken.None);
            }

            Assert(result.Accepted && result.CatalogRequest is not null &&
                   result.Authorities.Length == 3 && counting.Calls == 1,
                $"RaceMenu authority did not retain the three exact assets in one index snapshot: {GeometryDiagnostics(result.Diagnostics)}");
            SkyrimRaceMenuCatalogParseResult parsed =
                new RaceMenuSliderCatalogParserCore().Parse(
                    result.CatalogRequest!);
            Assert(parsed.Accepted && parsed.Catalog is not null &&
                   parsed.Catalog.Sliders.Length == 1 &&
                   parsed.Catalog.MorphExtensions.Length == 1 &&
                   parsed.Catalog.MorphExtensions[0].ExtendedTriPaths[0].Value
                       .EndsWith("/morphs/EFM.tri",
                           StringComparison.OrdinalIgnoreCase),
                "Winner-resolved RaceMenu catalog bytes did not parse into slider and extension semantics.");

            await scoped.IndexAsync(new AssetIndexRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(dataRoot)), CancellationToken.None);
            Assert(counting.Calls == 2,
                "Disposed asset snapshot leaked into a later operation.");
        }
        finally
        {
            DeleteOwnedDirectory(ownedRoot);
        }
    }

    private sealed class CountingAssetIndexer(IAssetIndexer inner) : IAssetIndexer
    {
        public int Calls { get; private set; }

        public ValueTask<AssetIndex> IndexAsync(
            AssetIndexRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return inner.IndexAsync(request, cancellationToken);
        }
    }
}
