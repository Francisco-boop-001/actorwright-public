using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.FaceGen;
using NpcManager.BodyGen;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;

namespace NpcManager.Cli;

internal static class ProtocolV2GoldenWorkflowComposition
{
    public static ImmutableArray<IProtocolV2CommandAdapter> Create(
        WorkspacePath workspaceRoot)
    {
        var policy = new KOnlyWorkspacePolicy(
            workspaceRoot,
            ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
        var pluginReader = new BethesdaPluginReader();
        var assetIndexer = new BatchScopedAssetIndexer(
            new BethesdaAssetIndexer());
        var reviewedIntake = new ReviewedGameIntakeService(
            new GameRootPreflightService(policy),
            new PluginLoadOrderService(pluginReader, policy, workspaceRoot),
            new BodySidecarInspectionService(policy, workspaceRoot),
            new GeneratedArtifactScanService(
                policy,
                pluginReader,
                workspaceRoot),
            assetIndexer);
        var codec = new FaceGeomHairRegionsDocumentCodec(workspaceRoot);
        var store = new ReviewedGameIntakeArtifactStore(workspaceRoot, codec);
        var workflowLifecycle = new AgentWorkflowBundleTransitionService(
            new AgentWorkflowBundleCodec(policy, workspaceRoot));
        var reviewReceipts = new AgentReviewReceiptService(
            policy,
            workspaceRoot);
        var presetService = new PresetService(policy, workspaceRoot);
        var presetStore = new PresetInspectionArtifactStore(workspaceRoot);
        var jslotComposition = CreateJslotBuildComposition(workspaceRoot);
        var requestLoader = jslotComposition.RequestLoader;
        var preflightService = jslotComposition.PreflightService;
        var preflightDocuments = new NpcBuildPreflightDocumentCodec(
            workspaceRoot);
        var packageManifestReader = new PackageManifestReader(
            policy, workspaceRoot);
        var packageVerifyService = new PackageVerifyService(
            packageManifestReader);
        var actorAssemblyLoader = new ActorAssemblyPreflightDocumentLoader(
            workspaceRoot);
        var actorAssemblyPreflightService = new ActorAssemblyPreflightService(
            actorAssemblyLoader,
            actorAssemblyLoader,
            packageVerifyService,
            new BethesdaActorAssemblyIdentityReader(policy, workspaceRoot),
            new BodySlideSliderPresetInspectionService(policy, workspaceRoot),
            new BodySlideTriInspectionService(policy, workspaceRoot),
            workspaceRoot);
        var actorAssemblyResultStore = new ActorAssemblyPreflightResultStore(
            policy,
            workspaceRoot);
        var finishSourceReader = new SkyrimNpcFinishCoreSourcePackageReader(
            workspaceRoot,
            policy,
            packageManifestReader,
            packageVerifyService,
            new BethesdaSkyrimNpcFinishCoreSourceReader());
        var finishService = new SkyrimNpcFinishCoreService(
            finishSourceReader.InspectAsync,
            workspaceRoot);
        var finishVerificationStore =
            new SkyrimNpcFinishCoreVerificationArtifactStore(
                policy,
                workspaceRoot);
        return
        [
            new ProtocolV2GoldenWorkflowAdapter(
                workspaceRoot,
                reviewedIntake,
                store,
                workflowLifecycle),
            new ProtocolV2PresetInspectAdapter(
                workspaceRoot,
                presetService,
                presetStore,
                workflowLifecycle),
            new ProtocolV2NpcCreateFromJslotAdapter(
                workspaceRoot,
                codec,
                requestLoader,
                preflightService,
                preflightDocuments,
                new RaceMenuJslotNpcBuildCommandExecutor(
                    Program.RunLegacyAsync),
                packageVerifyService,
                packageManifestReader,
                workflowLifecycle,
                typedBuildBridge: jslotComposition.CommandBridge,
                standaloneAuthorityReader:
                    jslotComposition.StandaloneAuthorityReader),
            new ProtocolV2NpcVisualPreviewAdapter(
                workspaceRoot,
                policy,
                codec,
                packageVerifyService,
                packageManifestReader,
                new NpcVisualPreviewCliComposition(policy, workspaceRoot),
                new NpcVisualPreviewArtifactReader(workspaceRoot),
                workflowLifecycle),
            new ProtocolV2ReviewReceiptAdapter(
                workspaceRoot,
                reviewReceipts,
                workflowLifecycle),
            new ProtocolV2FinishVerifyAdapter(
                workspaceRoot,
                finishService,
                finishVerificationStore,
                workflowLifecycle,
                reviewReceipts),
            new ProtocolV2FinishCoreAdapter(workspaceRoot, finishService, workflowLifecycle, reviewReceipts),
            new ProtocolV2ActorAssemblyPreflightAdapter(
                workspaceRoot,
                actorAssemblyPreflightService,
                actorAssemblyResultStore)
        ];
    }

    /// <summary>
    /// Builds the one production JSlot graph used by both the legacy CLI
    /// runner and Protocol-v2.  Keep external discovery, route, exclusion,
    /// output binding, and strict verification instances inside this graph;
    /// callers must not reconstruct a second Golden graph.
    /// </summary>
    internal static RaceMenuJslotNpcBuildCliComposition
        CreateJslotBuildComposition(WorkspacePath workspaceRoot)
    {
        var policy = new KOnlyWorkspacePolicy(
            workspaceRoot,
            ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
        var pluginReader = new BethesdaPluginReader();
        var assetIndexer = new BatchScopedAssetIndexer(
            new BethesdaAssetIndexer());
        var presetService = new PresetService(policy, workspaceRoot);
        var requestLoader = new RaceMenuNpcExecutionRequestFileLoader(
            workspaceRoot,
            new ApplicationProviderResourceRegistry(
                new ApplicationResourcePath(AppContext.BaseDirectory)));
        var pluginAuthorityLoader =
            new SkyrimFaceRecordPluginAuthorityLoader(policy, workspaceRoot);
        var standaloneAuthorityReader =
            new RaceMenuNpcStandaloneAuthorityReader(
                assetIndexer,
                policy,
                workspaceRoot);
        var faceMorphSnapshotService =
            new BethesdaSkyrimFaceMorphSnapshotService(policy, workspaceRoot);
        var blankNpcProviderService = new BlankNpcProviderService(
            policy,
            workspaceRoot,
            assetIndexer);
        var appearancePlanner = new RaceMenuNpcAppearancePlanService(
            presetService,
            blankNpcProviderService,
            policy,
            workspaceRoot);
        var preflightDocuments = new NpcBuildPreflightDocumentCodec(
            workspaceRoot);
        var packageManifestReader = new PackageManifestReader(
            policy,
            workspaceRoot);
        var packageVerifyService = new PackageVerifyService(
            packageManifestReader);
        var qualifiedFaceGeomCarrierService =
            new QualifiedFaceGeomCarrierService(policy, workspaceRoot);
        var npcCreationService = NpcCreationComposition.Create(
            policy,
            workspaceRoot);
        var creationFaceTintDecoder =
            new InProcessDdsTextureDecoder(workspaceRoot);
        var presetBlankNpcBuildService = new BlankNpcBuildService(
            npcCreationService,
            blankNpcProviderService,
            qualifiedFaceGeomCarrierService,
            Program.CreatePresetFaceTintBuildGuard(),
            creationFaceTintDecoder,
            packageVerifyService,
            policy,
            workspaceRoot);
        var faceBakeContentResolver =
            new SkyrimAssetContentResolver(policy, workspaceRoot);
        var nativeAssetAuthorityPlanner =
            new SkyrimAssetAuthorityPlanner(assetIndexer, policy, workspaceRoot);
        var wholeSkinResolver =
            new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
                pluginAuthorityLoader,
                nativeAssetAuthorityPlanner);
        var sharedFaceRecordRouteResolver =
            new BethesdaSkyrimFaceRecordRouteResolver(policy, workspaceRoot);
        var externalHeadPartPhysicsBindingResolver =
            new ExternalHeadPartPhysicsBindingResolver(policy, workspaceRoot);
        var externalHeadPartDependencyDiscovery =
            new ExternalHeadPartDependencyDiscoveryService(
                nativeAssetAuthorityPlanner,
                faceBakeContentResolver,
                externalHeadPartPhysicsBindingResolver);
        var externalHeadPartFaceGeomExclusionVerifier =
            new ExternalHeadPartFaceGeomExclusionVerifier();
        var raceMenuJslotExternalHeadPartPrecheck =
            new RaceMenuJslotExternalHeadPartPrecheckService(
                presetService,
                pluginAuthorityLoader,
                sharedFaceRecordRouteResolver,
                externalHeadPartDependencyDiscovery);
        var raceMenuSelectedDependencyManifestWriter =
            new RaceMenuSelectedDependencyManifestWriter(policy, workspaceRoot);
        var raceMenuJslotOutputPluginBindingReader =
            new RaceMenuJslotOutputPluginBindingReader(pluginReader);
        var raceMenuSelectedDependencyManifestReader =
            new RaceMenuSelectedDependencyManifestReader();
        var externalHeadPartInstallVerifier =
            new BethesdaExternalHeadPartInstallVerifier(
                raceMenuSelectedDependencyManifestReader,
                externalHeadPartPhysicsBindingResolver);
        var nativeDdsDecoder = new InProcessDdsTextureDecoder(workspaceRoot);
        var nativeFaceTintRecordResolver =
            new BethesdaSkyrimNativeFaceTintRecordResolver(policy, workspaceRoot);
        var nativeFaceTintMaterializer =
            new SkyrimNativeFaceTintMaterializationService(
                nativeFaceTintRecordResolver,
                faceBakeContentResolver,
                new SkyrimNativeFaceTintBuildService(
                    policy,
                    workspaceRoot,
                    nativeDdsDecoder));
        var nativeFaceTintPipeline = new SkyrimNativeFaceTintPipelineService(
            pluginAuthorityLoader,
            nativeFaceTintRecordResolver,
            new SkyrimNativeFaceTintAuthorityPlanner(
                assetIndexer,
                policy,
                workspaceRoot),
            nativeFaceTintMaterializer);
        var nativeFaceGeomMaterializer =
            new SseFaceGeomCarrierMaterializationService(
                new SseFaceGeomCarrierAssembler(),
                policy,
                workspaceRoot);
        var raceMenuCatalogParser = new RaceMenuSliderCatalogParserCore();
        var preflightService = new NpcBuildPreflightService(
            requestLoader,
            appearancePlanner,
            standaloneAuthorityReader,
            faceMorphSnapshotService,
            presetService,
            pluginAuthorityLoader,
            () => NpcVisualPreviewCliComposition.ProbeDependencies(workspaceRoot),
            preflightDocuments,
            policy,
            workspaceRoot,
            new NpcBuildPreflightDependencyClosureService(
                assetIndexer, sharedFaceRecordRouteResolver,
                nativeAssetAuthorityPlanner, faceBakeContentResolver,
                new BethesdaSkyrimRaceTintAuthorityReader(pluginAuthorityLoader),
                raceMenuCatalogParser, new SseSelectedHeadpartNifGeometryReader(),
                new SseTriHeadReader(), new SseFaceMorphPlanBuilder(),
                externalHeadPartPhysicsBindingResolver, wholeSkinResolver),
            new SkyrimFaceBakeAuthorityDerivationService(
                presetService, pluginAuthorityLoader, sharedFaceRecordRouteResolver,
                nativeAssetAuthorityPlanner, faceBakeContentResolver,
                new SkyrimRaceMenuCatalogAuthorityLoader(assetIndexer, nativeAssetAuthorityPlanner, faceBakeContentResolver),
                raceMenuCatalogParser, new SseSelectedHeadpartNifGeometryReader(),
                new SkyrimFaceBakeCarrierGeometryReader(),
                new SkyrimFaceBakeAuthorityLoader(policy, workspaceRoot, faceBakeContentResolver),
                policy, externalHeadPartDependencyDiscovery, externalHeadPartFaceGeomExclusionVerifier));
        var nativeRaceMenuCatalogLoader =
            new SkyrimRaceMenuCatalogAuthorityLoader(
                assetIndexer,
                nativeAssetAuthorityPlanner,
                faceBakeContentResolver);
        var nativeFaceGeomPipeline = new SkyrimNativeFaceGeomBuildService(
            pluginAuthorityLoader,
            sharedFaceRecordRouteResolver,
            faceMorphSnapshotService,
            nativeAssetAuthorityPlanner,
            faceBakeContentResolver,
            nativeRaceMenuCatalogLoader,
            raceMenuCatalogParser,
            new SseSelectedHeadpartNifGeometryReader(),
            new SseRaceMenuFaceBakeService(
                new SseTriHeadReader(),
                raceMenuCatalogParser,
                new SseFaceMorphPlanBuilder(),
                new SseFaceMorphEvaluator()),
            nativeFaceGeomMaterializer,
            externalHeadPartProviderSidecarResolver:
                new ExternalHeadPartProviderSidecarAuthorityResolver(
                    policy,
                    workspaceRoot),
            externalHeadPartDependencyDiscovery:
                externalHeadPartDependencyDiscovery,
            externalHeadPartFaceGeomExclusionVerifier:
                externalHeadPartFaceGeomExclusionVerifier);
        var nativeFaceGenNpcBakeService = new SkyrimFaceGenNpcBakeService(
            nativeFaceGeomPipeline,
            nativeFaceTintPipeline,
            nativeFaceGeomMaterializer,
            nativeDdsDecoder,
            policy,
            workspaceRoot);
        var raceMenuFaceGeomMergeService =
            new RaceMenuCharGenFaceGeomMergeService(policy, workspaceRoot);
        var raceMenuFaceGeomBuildService =
            new RaceMenuNpcFaceGeomBuildService(
                new SkyrimFaceBakeAuthorityLoader(
                    policy,
                    workspaceRoot,
                    faceBakeContentResolver),
                sharedFaceRecordRouteResolver,
                new SseSelectedHeadpartNifGeometryReader(),
                new SseRaceMenuFaceBakeService(),
                raceMenuFaceGeomMergeService);
        var directCharGenFaceGeomBuildService =
            new RaceMenuDirectCharGenFaceGeomBuildService(
                raceMenuFaceGeomMergeService,
                policy,
                workspaceRoot);
        var raceMenuFaceTextureBuildService =
            new RaceMenuNpcFaceTextureBuildService(
                policy,
                workspaceRoot,
                new InProcessDdsTextureDecoder(workspaceRoot),
                creationFaceTintDecoder);
        var bodyGenService = new BodyGenService(policy, workspaceRoot);
        var existingNpcAppearanceBuildService =
            new ExistingNpcAppearanceBuildService(
                new NpcAppearanceOverrideService(policy, workspaceRoot),
                bodyGenService,
                qualifiedFaceGeomCarrierService,
                creationFaceTintDecoder,
                packageVerifyService,
                policy,
                workspaceRoot);
        var wholeSkinReader = new RaceMenuNpcWholeSkinAuthorityReader(
            wholeSkinResolver,
            policy,
            workspaceRoot);
        var wholeSkinWriter = new RaceMenuNpcWholeSkinAuthorityWriter(
            policy,
            workspaceRoot);
        var raceMenuNpcBuildService = new RaceMenuNpcBuildService(
            appearancePlanner,
            faceMorphSnapshotService,
            bodyGenService,
            presetBlankNpcBuildService,
            assetIndexer,
            policy,
            workspaceRoot,
            raceMenuFaceGeomBuildService,
            raceMenuFaceTextureBuildService,
            qualifiedFaceGeomCarrierService,
            creationFaceTintDecoder,
            existingNpcAppearanceBuildService,
            directCharGenFaceGeomBuildService,
            wholeSkinReader);
        var raceMenuRecordBuilder = new RaceMenuPresetRecordAuthorityBuilder(
            pluginAuthorityLoader,
            new BethesdaSkyrimMajorRecordBindingReader(
                pluginAuthorityLoader),
            new BethesdaSkyrimRaceTintAuthorityReader(
                pluginAuthorityLoader),
            new BethesdaSkyrimFaceTextureSetAuthorityReader(
                pluginAuthorityLoader),
            new RaceMenuPresetTintAuthorityMapper(),
            new BethesdaSkyrimFaceTextureSetMatchResolver(
                pluginAuthorityLoader));
        var raceMenuSelectionTransaction =
            new RaceMenuPresetSelectionTransactionService(
                raceMenuRecordBuilder,
                new RaceMenuPresetRecordAuthorityWriter(policy, workspaceRoot),
                new RaceMenuPresetBundleAuthorityWriter(policy, workspaceRoot),
                appearancePlanner,
                raceMenuNpcBuildService,
                new RaceMenuPresetStandaloneAuthorityWriter(
                    faceMorphSnapshotService,
                    nativeAssetAuthorityPlanner,
                    creationFaceTintDecoder,
                    policy,
                    workspaceRoot),
                raceMenuSelectedDependencyManifestWriter,
                wholeSkinResolver,
                wholeSkinWriter,
                policy,
                workspaceRoot);
        var raceMenuJslotCompanionBuildService =
            new RaceMenuJslotCompanionBuildService(
                raceMenuRecordBuilder,
                raceMenuNpcBuildService,
                faceMorphSnapshotService,
                npcCreationService,
                nativeFaceGenNpcBakeService,
                policy,
                workspaceRoot);
        return RaceMenuJslotNpcBuildCliComposition.Create(
            presetService,
            pluginAuthorityLoader,
            raceMenuJslotCompanionBuildService,
            raceMenuSelectionTransaction,
            raceMenuNpcBuildService,
            policy,
            workspaceRoot,
            requestLoader,
            preflightService,
            raceMenuJslotExternalHeadPartPrecheck,
            raceMenuJslotOutputPluginBindingReader,
            externalHeadPartInstallVerifier,
            standaloneAuthorityReader);
    }
}
