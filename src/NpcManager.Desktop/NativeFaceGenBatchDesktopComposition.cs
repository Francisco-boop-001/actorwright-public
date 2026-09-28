using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Desktop;

internal static class NativeFaceGenBatchDesktopComposition
{
    internal static IFaceGenBakeAllService Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var assetIndexer = new BatchScopedAssetIndexer(
            new BethesdaAssetIndexer());
        var contentResolver = new SkyrimAssetContentResolver(policy, labRoot);
        var pluginAuthorityLoader =
            new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var assetAuthorityPlanner =
            new SkyrimAssetAuthorityPlanner(assetIndexer, policy, labRoot);
        var externalPhysicsBindingResolver =
            new ExternalHeadPartPhysicsBindingResolver(policy, labRoot);
        var externalDiscovery = new ExternalHeadPartDependencyDiscoveryService(
            assetAuthorityPlanner,
            contentResolver,
            externalPhysicsBindingResolver);
        var externalFaceGeomExclusionVerifier =
            new ExternalHeadPartFaceGeomExclusionVerifier();
        var ddsDecoder = new InProcessDdsTextureDecoder(labRoot);
        var tintRecordResolver =
            new BethesdaSkyrimNativeFaceTintRecordResolver(policy, labRoot);
        var tintPipeline = new SkyrimNativeFaceTintPipelineService(
            pluginAuthorityLoader,
            tintRecordResolver,
            new SkyrimNativeFaceTintAuthorityPlanner(
                assetIndexer, policy, labRoot),
            new SkyrimNativeFaceTintMaterializationService(
                tintRecordResolver,
                contentResolver,
                new SkyrimNativeFaceTintBuildService(
                    policy, labRoot, ddsDecoder)));
        var carrierMaterializer =
            new SseFaceGeomCarrierMaterializationService(
                new SseFaceGeomCarrierAssembler(), policy, labRoot);
        var raceMenuCatalogParser = new RaceMenuSliderCatalogParserCore();
        var raceMenuCatalogLoader = new SkyrimRaceMenuCatalogAuthorityLoader(
            assetIndexer, assetAuthorityPlanner, contentResolver);
        var geometryPipeline = new SkyrimNativeFaceGeomBuildService(
            pluginAuthorityLoader,
            new BethesdaSkyrimFaceRecordRouteResolver(policy, labRoot),
            new BethesdaSkyrimFaceMorphSnapshotService(policy, labRoot),
            assetAuthorityPlanner,
            contentResolver,
            raceMenuCatalogLoader,
            raceMenuCatalogParser,
            new SseSelectedHeadpartNifGeometryReader(),
                new SseRaceMenuFaceBakeService(
                    new SseTriHeadReader(), raceMenuCatalogParser,
                    new SseFaceMorphPlanBuilder(),
                    new SseFaceMorphEvaluator()),
            carrierMaterializer,
            externalHeadPartProviderSidecarResolver:
                new ExternalHeadPartProviderSidecarAuthorityResolver(
                    policy, labRoot),
            externalHeadPartDependencyDiscovery: externalDiscovery,
            externalHeadPartFaceGeomExclusionVerifier:
                externalFaceGeomExclusionVerifier);
        var npcBaker = new SkyrimFaceGenNpcBakeService(
            geometryPipeline,
            tintPipeline,
            carrierMaterializer,
            ddsDecoder,
            policy,
            labRoot);
        var inventory = new GameInventoryService(
            new BethesdaPluginReader(), assetIndexer, policy, labRoot);
        return new FaceGenBakeAllService(
            new FaceGenBakeTargetDiscoveryService(
                inventory, policy, labRoot),
            npcBaker,
            policy,
            labRoot,
            assetIndexer,
            new SkyrimFaceGenSidecarOverlayLoader(
                new BodySidecarInspectionService(policy, labRoot)));
    }
}
