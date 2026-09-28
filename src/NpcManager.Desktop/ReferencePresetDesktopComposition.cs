using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

/// <summary>
/// Packaged composition for reference authoring. Native/model bytes are
/// discovered beside the executable and admitted before any inference or
/// transaction service is constructed.
/// </summary>
internal static class ReferencePresetDesktopComposition
{
    public static ReferencePresetAuthoringViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IRaceMenuJslotNpcBuildService jslotNpcBuildService,
        Func<ReferencePresetNpcDesktopHandoff, bool>
            continueToNpc)
    {
        var assetIndexer = new BethesdaAssetIndexer();
        var contentResolver =
            new SkyrimAssetContentResolver(
                policy,
                labRoot);
        var assetPlanner = new SkyrimAssetAuthorityPlanner(
            assetIndexer,
            policy,
            labRoot);
        var authorityLoader =
            new SkyrimFaceRecordPluginAuthorityLoader(
                policy,
                labRoot);
        var compatibility =
            new BethesdaRaceMenuPresetCompatibilityEvaluator(
                authorityLoader);
        var sliderParser =
            new RaceMenuSliderCatalogParserCore();
        var catalogLoader =
            new SkyrimRaceMenuCatalogAuthorityLoader(
                assetIndexer,
                assetPlanner,
                contentResolver);
        var presetService = new PresetService(
            policy,
            labRoot);
        var sessionService =
            new ReferencePresetSessionService(
                policy,
                labRoot);
        var resourceService =
            new ReferencePresetResourceSnapshotService(
                presetService,
                compatibility,
                new BethesdaReferencePresetCatalogReader(
                    authorityLoader,
                    new BethesdaSkyrimRaceTintAuthorityReader(
                        authorityLoader)),
                assetPlanner,
                new ReferencePresetAssetMaterializer(
                    contentResolver),
                catalogLoader,
                sliderParser,
                new SseSelectedHeadpartNifGeometryReader(),
                new SseTriHeadReader(),
                new InProcessDdsTextureDecoder(
                    labRoot,
                    InProcessDdsTextureDecodeProfile.ReferencePreview));
        var renderInputBuilder =
            new ReferencePresetRenderInputBuilder();
        var facePlanBuilder =
            new SseFaceMorphPlanBuilder();
        var faceEvaluator =
            new SseFaceMorphEvaluator();
        IReferencePresetAuthoringTransaction transaction =
            new LazyReferencePresetAuthoringTransaction(
                new ReferencePresetTransactionFactory(
                    policy,
                    labRoot,
                    sessionService,
                    renderInputBuilder,
                    facePlanBuilder,
                    faceEvaluator,
                    presetService,
                    jslotNpcBuildService));
        WorkspacePath sessionParent = new(Path.Combine(
            labRoot.Value,
            ".actorwright",
            "work",
            "reference-preset-desktop"));
        Directory.CreateDirectory(sessionParent.Value);
        return new ReferencePresetAuthoringViewModel(
            transaction,
            sessionService,
            resourceService,
            renderInputBuilder,
            new ReferencePresetMeshAnchorBinder(),
            new ReferenceSemanticLandmarkProjector(),
            labRoot,
            sessionParent,
            continueToNpc,
            serviceLifetime: null,
            new ReferencePresetCpuRenderer());
    }

    private sealed class ReferencePresetTransactionFactory(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IReferencePresetSessionService sessionService,
        IReferencePresetRenderInputBuilder renderInputBuilder,
        SseFaceMorphPlanBuilder facePlanBuilder,
        SseFaceMorphEvaluator faceEvaluator,
        IPresetService presetService,
        IRaceMenuJslotNpcBuildService jslotNpcBuildService) :
        IPreviewServiceFactory<IReferencePresetAuthoringTransaction>
    {
        public ValueTask<PreviewServiceLease<
            IReferencePresetAuthoringTransaction>> CreateAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationResourceRuntimeAdmissionResult resourceAdmission =
                new ApplicationResourceRuntimeLocator(
                    new ApplicationResourcePath(
                        AppContext.BaseDirectory))
                    .AdmitReferencePresetRuntime();
            if (!resourceAdmission.Accepted ||
                resourceAdmission.Authority is null)
                return ValueTask.FromResult(
                    new PreviewServiceLease<
                        IReferencePresetAuthoringTransaction>(
                            new UnavailableReferencePresetAuthoringTransaction(
                                resourceAdmission.Diagnostics)));

            MediaPipeNativeApi? nativeApi = null;
            try
            {
                nativeApi = new MediaPipeNativeApi(
                    resourceAdmission.Authority);
                ReferencePresetRuntimeAdmissionResult admission =
                    nativeApi.AdmitRuntime();
                if (!admission.Accepted ||
                    admission.ManifestSha256 is null)
                {
                    ImmutableArray<Diagnostic> diagnostics =
                        admission.Diagnostics.Add(new Diagnostic(
                            "reference-runtime-unavailable",
                            DiagnosticSeverity.Error,
                            "The reference runtime drifted after application-resource admission."));
                    nativeApi.Dispose();
                    return ValueTask.FromResult(
                        new PreviewServiceLease<
                            IReferencePresetAuthoringTransaction>(
                                new UnavailableReferencePresetAuthoringTransaction(
                                    diagnostics)));
                }

                var transaction =
                    new ReferencePresetAuthoringTransaction(
                        policy,
                        labRoot,
                        admission.ManifestSha256.Value,
                        sessionService,
                        new SkiaReferenceImageDecoder(labRoot),
                        new MediaPipeFaceLandmarkInferenceService(
                            nativeApi),
                        new ReferenceDescriptionInterpreter(),
                        new ReferenceSemanticLandmarkProjector(),
                        renderInputBuilder,
                        new RaceMenuTriResponseMatrixBuilder(
                            facePlanBuilder,
                            faceEvaluator),
                        new ReferenceRaceMenuPresetSolver(),
                        new ReferencePresetComparisonService(
                            policy,
                            labRoot,
                            facePlanBuilder,
                            faceEvaluator,
                            new ReferencePresetCpuRenderer()),
                        new ReferenceRaceMenuPresetWriter(
                            policy,
                            labRoot,
                            presetService),
                        jslotNpcBuildService);
                return ValueTask.FromResult(
                    new PreviewServiceLease<
                        IReferencePresetAuthoringTransaction>(
                            transaction,
                            [nativeApi]));
            }
            catch
            {
                nativeApi?.Dispose();
                throw;
            }
        }
    }

    private sealed class LazyReferencePresetAuthoringTransaction(
        IPreviewServiceFactory<IReferencePresetAuthoringTransaction>
            factory) : IReferencePresetAuthoringTransaction
    {
        public async ValueTask<ReferencePresetDesignProposalResult>
            ProposeDesignAsync(
                ReferencePresetDesignProposalRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            await using PreviewServiceLease<
                IReferencePresetAuthoringTransaction> lease =
                await factory.CreateAsync(cancellationToken);
            return await lease.Service.ProposeDesignAsync(
                request,
                progress,
                cancellationToken);
        }

        public async ValueTask<ReferencePresetWriteResult>
            WritePresetAsync(
                ReferencePresetWriteRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            await using PreviewServiceLease<
                IReferencePresetAuthoringTransaction> lease =
                await factory.CreateAsync(cancellationToken);
            return await lease.Service.WritePresetAsync(
                request,
                progress,
                cancellationToken);
        }

        public async ValueTask<ReferencePresetNpcBuildResult>
            WritePresetAndBuildNpcAsync(
                ReferencePresetNpcBuildRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            await using PreviewServiceLease<
                IReferencePresetAuthoringTransaction> lease =
                await factory.CreateAsync(cancellationToken);
            return await lease.Service.WritePresetAndBuildNpcAsync(
                request,
                progress,
                cancellationToken);
        }
    }

    private sealed class UnavailableReferencePresetAuthoringTransaction(
        ImmutableArray<Diagnostic> diagnostics) :
        IReferencePresetAuthoringTransaction
    {
        public ValueTask<ReferencePresetDesignProposalResult>
            ProposeDesignAsync(
                ReferencePresetDesignProposalRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReferencePresetDesignProposalResult(
                false,
                null,
                null,
                null,
                null,
                diagnostics));

        public ValueTask<ReferencePresetWriteResult> WritePresetAsync(
            ReferencePresetWriteRequest request,
            IProgress<ReferencePresetProgress>? progress,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReferencePresetWriteResult(
                false,
                null,
                null,
                null,
                diagnostics));

        public ValueTask<ReferencePresetNpcBuildResult>
            WritePresetAndBuildNpcAsync(
                ReferencePresetNpcBuildRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReferencePresetNpcBuildResult(
                false,
                null,
                null,
                diagnostics));
    }
}
