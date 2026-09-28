using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.BodyGen;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

internal sealed record RaceMenuNpcDesktopContext(
    IRaceMenuNpcExecutionRequestFileLoader RequestLoader,
    IRaceMenuNpcBuildService BuildService,
    IRaceMenuPresetCatalogService PresetCatalogService,
    IRaceMenuPresetPreviewService PresetPreviewService,
    IRaceMenuPresetSelectionTransactionService PresetSelectionTransactionService,
    IRaceMenuJslotNpcBuildService JslotBuildService,
    INpcBuildPreflightService PreflightService,
    INpcVisualPreviewComposer PreviewComposer,
    IDisposable PresetCatalogLifetime,
    IFaceTintBuildService FaceTintBuildGuard,
    WorkspacePath InitialRequestFile,
    string InitialRequestSha256,
    WorkspacePath OutputParent);

/// <summary>Composition root for the shared Gate 2 product service.</summary>
internal static class RaceMenuNpcDesktopComposition
{
    public static RaceMenuNpcDesktopContext Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IQualifiedFaceGeomCarrierService qualifiedFaceGeomCarrierService)
    {
        var assetIndexer = new BatchScopedAssetIndexer(new BethesdaAssetIndexer());
        var assetAuthorityPlanner =
            new SkyrimAssetAuthorityPlanner(assetIndexer, policy, labRoot);
        var providerService = new BlankNpcProviderService(policy, labRoot, assetIndexer);
        var contentResolver = new SkyrimAssetContentResolver(policy, labRoot);
        var faceRecordRouteResolver =
            new BethesdaSkyrimFaceRecordRouteResolver(policy, labRoot);
        var faceGeomMergeService = new RaceMenuCharGenFaceGeomMergeService(policy, labRoot);
        var faceGeomBuildService = new RaceMenuNpcFaceGeomBuildService(
            new SkyrimFaceBakeAuthorityLoader(policy, labRoot, contentResolver),
            faceRecordRouteResolver,
            new SseSelectedHeadpartNifGeometryReader(),
            new SseRaceMenuFaceBakeService(),
            faceGeomMergeService);
        var directCharGenFaceGeomBuildService =
            new RaceMenuDirectCharGenFaceGeomBuildService(
                faceGeomMergeService, policy, labRoot);
        var exactFaceTintEvidenceDecoder = new Bgra8FaceTintTextureDecoder(labRoot);
        var faceTintBuildGuard = new ExactOnlyFaceTintBuildService();
        var npcCreationService =
            NpcCreationComposition.Create(policy, labRoot);
        var exactOnlyBlankNpcBuildService = new BlankNpcBuildService(
            npcCreationService,
            providerService,
            qualifiedFaceGeomCarrierService,
            faceTintBuildGuard,
            exactFaceTintEvidenceDecoder,
            new PackageVerifyService(new PackageManifestReader(policy, labRoot)),
            policy,
            labRoot);
        var inProcessFaceTintDecoder = new InProcessDdsTextureDecoder(labRoot);
        var faceTextureBuildService = new RaceMenuNpcFaceTextureBuildService(
            policy,
            labRoot,
            inProcessFaceTintDecoder,
            exactFaceTintEvidenceDecoder);
        var bodyGenService = new BodyGenService(policy, labRoot);
        var faceRecordAuthorityLoader =
            new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var externalPhysicsBindingResolver =
            new ExternalHeadPartPhysicsBindingResolver(policy, labRoot);
        var externalDiscovery = new ExternalHeadPartDependencyDiscoveryService(
            assetAuthorityPlanner,
            contentResolver,
            externalPhysicsBindingResolver);
        var externalFaceGeomExclusionVerifier =
            new ExternalHeadPartFaceGeomExclusionVerifier();
        var selectedDependencyManifestReader =
            new RaceMenuSelectedDependencyManifestReader();
        var externalInstallVerifier = new BethesdaExternalHeadPartInstallVerifier(
            selectedDependencyManifestReader,
            externalPhysicsBindingResolver);
        var jslotOutputPluginBindingReader =
            new RaceMenuJslotOutputPluginBindingReader();
        var externalPrecheck = new RaceMenuJslotExternalHeadPartPrecheckService(
            new PresetService(policy, labRoot),
            faceRecordAuthorityLoader,
            faceRecordRouteResolver,
            externalDiscovery);
        var planService = new RaceMenuNpcAppearancePlanService(
            new PresetService(policy, labRoot), providerService, policy, labRoot);
        var wholeSkinResolver = new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
            faceRecordAuthorityLoader,
            assetAuthorityPlanner);
        var wholeSkinReader = new RaceMenuNpcWholeSkinAuthorityReader(
            wholeSkinResolver, policy, labRoot);
        var wholeSkinWriter = new RaceMenuNpcWholeSkinAuthorityWriter(
            policy, labRoot);
        var faceMorphSnapshotService =
            new BethesdaSkyrimFaceMorphSnapshotService(policy, labRoot);
        var presetCompatibilityEvaluator =
            new BethesdaRaceMenuPresetCompatibilityEvaluator(
                faceRecordAuthorityLoader);
        var presetCatalogService = new RaceMenuPresetCatalogService(
            new PresetService(policy, labRoot), policy, labRoot,
            presetCompatibilityEvaluator);
        var previewImageRenderer = new BlenderPreviewImageRenderer(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external",
                "blender-4.5.1-windows-x64", "blender-4.5.1-windows-x64", "blender.exe")),
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external",
                "blender-4.5.1-pynifly-profile")),
            "render_preview_scene",
            policy,
            labRoot,
            new Sha256Hash(
                "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"));
        var presetPreviewService = new RaceMenuPresetPreviewService(
            previewImageRenderer,
            policy,
            labRoot,
            new WorkspacePath(Path.Combine(labRoot.Value, ".actorwright", "work",
                "racemenu-preset-preview-cache")));
        var packageVerifier = new PackageVerifyService(
            new PackageManifestReader(policy, labRoot));
        var existingNpcBuildService = new ExistingNpcAppearanceBuildService(
            new NpcAppearanceOverrideService(policy, labRoot),
            bodyGenService,
            qualifiedFaceGeomCarrierService,
            exactFaceTintEvidenceDecoder,
            packageVerifier,
            policy,
            labRoot);
        var buildService = new RaceMenuNpcBuildService(
            planService,
            faceMorphSnapshotService,
            bodyGenService,
            exactOnlyBlankNpcBuildService,
            assetIndexer,
            policy,
            labRoot,
            faceGeomBuildService,
            faceTextureBuildService,
            qualifiedFaceGeomCarrierService,
            exactFaceTintEvidenceDecoder,
            existingNpcBuildService,
            directCharGenFaceGeomBuildService,
            wholeSkinReader);
        var recordBuilder = new RaceMenuPresetRecordAuthorityBuilder(
            faceRecordAuthorityLoader,
            new BethesdaSkyrimMajorRecordBindingReader(faceRecordAuthorityLoader),
            new BethesdaSkyrimRaceTintAuthorityReader(faceRecordAuthorityLoader),
            new BethesdaSkyrimFaceTextureSetAuthorityReader(faceRecordAuthorityLoader),
            new RaceMenuPresetTintAuthorityMapper(),
            new BethesdaSkyrimFaceTextureSetMatchResolver(faceRecordAuthorityLoader));
        var recordWriter = new RaceMenuPresetRecordAuthorityWriter(policy, labRoot);
        var bundleWriter = new RaceMenuPresetBundleAuthorityWriter(policy, labRoot);
        var standaloneWriter = new RaceMenuPresetStandaloneAuthorityWriter(
            faceMorphSnapshotService,
            assetAuthorityPlanner,
            inProcessFaceTintDecoder,
            policy,
            labRoot);
        var selectionTransaction = new RaceMenuPresetSelectionTransactionService(
            recordBuilder,
            recordWriter,
            bundleWriter,
            planService,
            buildService,
                standaloneWriter,
                new RaceMenuSelectedDependencyManifestWriter(
                    policy,
                    labRoot),
                wholeSkinResolver,
                wholeSkinWriter,
                policy,
                labRoot);
        var nativeFaceTintRecordResolver =
            new BethesdaSkyrimNativeFaceTintRecordResolver(
                policy, labRoot);
        var nativeFaceTintMaterializer =
            new SkyrimNativeFaceTintMaterializationService(
                nativeFaceTintRecordResolver,
                contentResolver,
                new SkyrimNativeFaceTintBuildService(
                    policy,
                    labRoot,
                    inProcessFaceTintDecoder));
        var nativeFaceTintPipeline =
            new SkyrimNativeFaceTintPipelineService(
                faceRecordAuthorityLoader,
                nativeFaceTintRecordResolver,
                new SkyrimNativeFaceTintAuthorityPlanner(
                    assetIndexer,
                    policy,
                    labRoot),
                nativeFaceTintMaterializer);
        var nativeFaceGeomMaterializer =
            new SseFaceGeomCarrierMaterializationService(
                new SseFaceGeomCarrierAssembler(),
                policy,
                labRoot);
        var nativeCatalogParser =
            new RaceMenuSliderCatalogParserCore();
        var nativeCatalogLoader =
            new SkyrimRaceMenuCatalogAuthorityLoader(
                assetIndexer,
                assetAuthorityPlanner,
                contentResolver);
        var nativeFaceGeomPipeline =
            new SkyrimNativeFaceGeomBuildService(
                faceRecordAuthorityLoader,
                faceRecordRouteResolver,
                faceMorphSnapshotService,
                assetAuthorityPlanner,
                contentResolver,
                nativeCatalogLoader,
                nativeCatalogParser,
                new SseSelectedHeadpartNifGeometryReader(),
                    new SseRaceMenuFaceBakeService(
                        new SseTriHeadReader(),
                        nativeCatalogParser,
                        new SseFaceMorphPlanBuilder(),
                        new SseFaceMorphEvaluator()),
                nativeFaceGeomMaterializer,
                externalHeadPartProviderSidecarResolver:
                    new ExternalHeadPartProviderSidecarAuthorityResolver(
                        policy, labRoot),
                externalHeadPartDependencyDiscovery: externalDiscovery,
                externalHeadPartFaceGeomExclusionVerifier:
                    externalFaceGeomExclusionVerifier);
        var nativeFaceGenNpcBakeService =
            new SkyrimFaceGenNpcBakeService(
                nativeFaceGeomPipeline,
                nativeFaceTintPipeline,
                nativeFaceGeomMaterializer,
                inProcessFaceTintDecoder,
                policy,
                labRoot);
        var jslotCompanionBuildService =
            new RaceMenuJslotCompanionBuildService(
                recordBuilder,
                buildService,
                faceMorphSnapshotService,
                npcCreationService,
                nativeFaceGenNpcBakeService,
                policy,
                labRoot);
        var requestLoader = new RaceMenuNpcExecutionRequestFileLoader(
            labRoot,
            new ApplicationProviderResourceRegistry(
                new ApplicationResourcePath(
                    AppContext.BaseDirectory)));
        var preflightService = new NpcBuildPreflightService(
            requestLoader,
            planService,
            buildService,
            faceMorphSnapshotService,
            new PresetService(policy, labRoot),
            faceRecordAuthorityLoader,
            () => SkyrimMainWorkspaceDesktopComposition
                .ProbeNpcVisualPreviewDependencies(labRoot),
            new NpcBuildPreflightDocumentCodec(labRoot),
            policy,
            labRoot,
            new NpcBuildPreflightDependencyClosureService(
                assetIndexer, faceRecordRouteResolver, assetAuthorityPlanner,
                contentResolver,
                new BethesdaSkyrimRaceTintAuthorityReader(faceRecordAuthorityLoader),
                nativeCatalogParser, new SseSelectedHeadpartNifGeometryReader(),
                new SseTriHeadReader(), new SseFaceMorphPlanBuilder(),
                externalPhysicsBindingResolver,
                wholeSkinResolver));
        var jslotBuildService =
            new RaceMenuJslotNpcBuildService(
                new PresetService(policy, labRoot),
                faceRecordAuthorityLoader,
                jslotCompanionBuildService,
                selectionTransaction,
                buildService,
                policy,
                labRoot,
                preflightService,
                externalPrecheck,
                jslotOutputPluginBindingReader,
                externalInstallVerifier);
        var request = new WorkspacePath(Path.Combine(labRoot.Value, ".actorwright",
            "inputs",
            "execution-request-v5.json"));
        var outputParent = ActorwrightWorkspace.WorkRoot(labRoot);
        return new RaceMenuNpcDesktopContext(
            requestLoader,
            buildService,
            presetCatalogService,
            presetPreviewService,
            selectionTransaction,
            jslotBuildService,
            preflightService,
            SkyrimMainWorkspaceDesktopComposition
                .CreateNpcVisualPreviewComposer(policy, labRoot),
            presetCompatibilityEvaluator,
            faceTintBuildGuard,
            request,
            TryHash(request),
            outputParent);
    }

    private static string TryHash(WorkspacePath path)
    {
        try
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 1 * 1024 * 1024) return string.Empty;
            return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream))).Value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
