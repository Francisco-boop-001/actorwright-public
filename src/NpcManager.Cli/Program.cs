using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Formats.Bethesda;
using NpcManager.Presets;
using NpcManager.FaceGen;
using NpcManager.BodyGen;
using NpcManager.Pipeline;
using NpcManager.Rendering;

namespace NpcManager.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
        => (int)await RunAsync(
            args,
            Console.Out,
            Console.Error,
            root => new LocalOperationJournal(root),
            CancellationToken.None);

    internal static async ValueTask<CommandExitCode> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(journalFactory);

        if (args is ["--resource-extraction-probe"])
            return (CommandExitCode)ApplicationResourceExtractionProbe.Run(
                output,
                error);

        var command = CommandLine.Parse(args);
        ActorwrightObservabilityEventSource? eventSource = null;
        Stopwatch? stopwatch = null;
        Guid invocationId = Guid.Empty;
        ActorwrightObservabilityEventSource.DispatchRouteId routeId = default;
        string commandName = string.Empty;
        try
        {
            eventSource = ActorwrightObservabilityEventSource.Log;
            if (eventSource.IsDispatchEnabled)
            {
                routeId = ProtocolV2CommandLine.Select(command) switch
                {
                    ProtocolSelection.Legacy =>
                        ActorwrightObservabilityEventSource.DispatchRouteId.Legacy,
                    ProtocolSelection.Version2 =>
                        ActorwrightObservabilityEventSource.DispatchRouteId.Protocol2,
                    _ => ActorwrightObservabilityEventSource.DispatchRouteId.Unsupported
                };
                commandName = ObservabilityCommandName(command);
                invocationId = Guid.NewGuid();
                stopwatch = Stopwatch.StartNew();
                eventSource.RecordDispatchStart(
                    invocationId,
                    routeId,
                    commandName);
            }
        }
        catch (Exception)
        {
            stopwatch = null;
        }

        CommandExitCode? completedExitCode = null;
        try
        {
            CommandExitCode exitCode;
            if (ProtocolV2CommandLine.IsRequested(command))
            {
                var runner = new ProtocolV2Runner(
                    output,
                    journalFactory,
                    ProtocolV2GoldenWorkflowComposition.Create,
                    AgentCommandRegistry.All);
                exitCode = await runner.RunAsync(
                    command,
                    cancellationToken);
            }
            else
            {
                exitCode = await DispatchAsync(
                    command,
                    parsed => RunLegacyAsync(
                        parsed,
                        output,
                        error,
                        journalFactory,
                        cancellationToken),
                    error);
            }

            completedExitCode = exitCode;
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            if (stopwatch is not null && eventSource is not null)
                eventSource.RecordDispatchFault(
                    invocationId,
                    routeId,
                    commandName,
                    ActorwrightObservabilityEventSource.DispatchFaultKindId.Cancelled);
            throw;
        }
        catch (Exception)
        {
            if (stopwatch is not null && eventSource is not null)
                eventSource.RecordDispatchFault(
                    invocationId,
                    routeId,
                    commandName,
                    ActorwrightObservabilityEventSource.DispatchFaultKindId.Unexpected);
            throw;
        }
        finally
        {
            if (stopwatch is not null && eventSource is not null &&
                completedExitCode is { } exitCode)
                eventSource.RecordDispatchStop(
                    invocationId,
                    routeId,
                    commandName,
                    exitCode,
                    stopwatch.ElapsedMilliseconds);
        }
    }

    private static string ObservabilityCommandName(ParsedCommand command)
    {
        CommandDescriptor? descriptor = CommandCatalog.All.FirstOrDefault(item =>
            string.Equals(item.Name, command.Name,
                StringComparison.OrdinalIgnoreCase));
        if (descriptor is not null)
            return descriptor.Name;

        AgentCommandContract? contract = AgentCommandRegistry.All
            .FirstOrDefault(item =>
                string.Equals(item.Name, command.Name,
                    StringComparison.OrdinalIgnoreCase) ||
                item.Aliases.Contains(
                    command.Name,
                    StringComparer.OrdinalIgnoreCase));
        return contract?.Name ?? "unknown";
    }

    internal static async ValueTask<CommandExitCode> DispatchAsync(
        ParsedCommand command,
        Func<ParsedCommand, ValueTask<CommandExitCode>> legacyDispatcher,
        TextWriter error,
        ImmutableArray<AgentCommandContract>? contracts = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(legacyDispatcher);
        ArgumentNullException.ThrowIfNull(error);

        var validation = contracts.HasValue
            ? ProtocolV2CommandLine.Validate(command, contracts.Value)
            : ProtocolV2CommandLine.Validate(command);
        if (!validation.Selected)
            return await legacyDispatcher(command);

        foreach (var diagnostic in validation.Diagnostics)
            error.WriteLine($"ERROR {diagnostic.Code}: {diagnostic.Message}");
        return ProtocolExitCodeMapper.Map(validation.Diagnostics);
    }

    internal static async ValueTask<CommandExitCode> RunLegacyAsync(
        ParsedCommand command,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) => await RunLegacyAsync(
            command, output, error, root => new LocalOperationJournal(root), cancellationToken);

    private static async ValueTask<CommandExitCode> RunLegacyAsync(
        ParsedCommand command,
        TextWriter output,
        TextWriter error,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        CancellationToken cancellationToken)
    {
        if (CliRunner.RunKernel(command, output, error, cancellationToken) is { } kernelExit)
            return kernelExit;

        WorkspacePath labRoot = ActorwrightWorkspace.ResolveRoot();
        if (ActorwrightWorkspace.GetWorkspaceRootAdmissionFailureMessage(
                labRoot) is { } message)
        {
            if (command.Json)
                output.WriteLine(JsonSerializer.Serialize(
                    new ErrorResponse(WorkspaceDiagnosticCodes.RootNotKLocal, message), CliRunner.JsonOptions));
            else
                error.WriteLine($"ERROR {WorkspaceDiagnosticCodes.RootNotKLocal}: {message}");
            return DiagnosticExitCodeClassifier.KnownSecurityRefusal;
        }

        WorkspacePath protectedRoot;
        try
        {
            protectedRoot = ActorwrightWorkspace.ResolveProtectedRoot(labRoot);
        }
        catch (ProtectedRootConfigurationException exception)
        {
            if (command.Json)
                error.WriteLine(JsonSerializer.Serialize(
                    new ErrorResponse("usage-error", exception.Message),
                    CliRunner.JsonOptions));
            else
                error.WriteLine($"ERROR usage-error: {exception.Message}");
            return CommandExitCode.UsageError;
        }

        var policy = new KOnlyWorkspacePolicy(labRoot, protectedRoot);
        var faceGenService = new FaceGenService(policy, labRoot);
        var faceGeomService = new FaceGeomBuildService(faceGenService, policy, labRoot);
        var faceTintEncoder = new TexconvFaceTintTextureEncoder(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "directxtex-texconv-2026.5.7", "texconv.exe")),
            labRoot,
            new Sha256Hash("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06"));
        var compressedFaceTintDecoder = new TexconvFaceTintTextureDecoder(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "directxtex-texconv-2026.5.7", "texconv.exe")),
            labRoot,
            new Sha256Hash("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06"));
        var faceTintDecoder = new ChainedFaceTintTextureDecoder(
            new Bgra8FaceTintTextureDecoder(labRoot),
            compressedFaceTintDecoder);
        var faceTintService = new FaceTintBuildService(policy, labRoot,
            faceTintEncoder, faceTintDecoder);
        var creationFaceTintDecoder = new InProcessDdsTextureDecoder(labRoot);
        var creationFaceTintService = new FaceTintBuildService(policy, labRoot,
            faceTintEncoder, creationFaceTintDecoder);
        var previewImageRenderer = new BlenderPreviewImageRenderer(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "blender-4.5.1-windows-x64", "blender-4.5.1-windows-x64", "blender.exe")),
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "blender-4.5.1-pynifly-profile")),
            "render_preview_scene",
            policy, labRoot,
            new Sha256Hash("B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"));
        var nifGeometryReadback = new BethesdaNifGeometryReadbackService();
        var previewNifBinaryExporter = new BlenderPreviewNifExporter(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "blender-4.5.1-windows-x64", "blender-4.5.1-windows-x64", "blender.exe")),
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "blender-4.5.1-pynifly-profile")),
            "export_preview_nif",
            policy, labRoot,
            new Sha256Hash("B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"),
            nifGeometryReadback);
        var faceGeomBinaryExporter = new BlenderFaceGeomNifExporter(
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "blender-4.5.1-windows-x64", "blender-4.5.1-windows-x64", "blender.exe")),
            new WorkspacePath(Path.Combine(labRoot.Value, "tools", "external", "blender-4.5.1-pynifly-profile")),
            "export_facegeom_nif",
            policy, labRoot,
            new Sha256Hash("B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"),
            nifGeometryReadback);
        var faceGeomBinaryRouter = new FaceGeomBinaryBuildRouter(
            faceGeomBinaryExporter,
            new BethesdaNifGeometryReadbackService(),
            policy,
            labRoot);
        var pluginReader = new BethesdaPluginReader();
        var assetIndexer = new BatchScopedAssetIndexer(
            new BethesdaAssetIndexer());
        var gameInventory = new GameInventoryService(pluginReader, assetIndexer, policy, labRoot);
        var mutationService = new NpcMutationService(policy, labRoot);
        var presetService = new PresetService(policy, labRoot);
        var skyrimFaceRecordPluginAuthorityLoader =
            new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        using var raceMenuPresetCompatibilityEvaluator =
            new BethesdaRaceMenuPresetCompatibilityEvaluator(
                skyrimFaceRecordPluginAuthorityLoader);
        var raceMenuPresetCatalogService = new RaceMenuPresetCatalogService(
            presetService, policy, labRoot, raceMenuPresetCompatibilityEvaluator);
        var bodyGenService = new BodyGenService(policy, labRoot);
        var bodySidecarWriteService = new BodySidecarWriteService(policy, labRoot);
        var runtimeScriptDeployService = new RuntimeScriptDeployService(policy, labRoot);
        var pluginLoadOrderService = new PluginLoadOrderService(pluginReader, policy, labRoot);
        var bodySidecarInspectionService = new BodySidecarInspectionService(policy, labRoot);
        var generatedArtifactScanService = new GeneratedArtifactScanService(policy, pluginReader, labRoot);
        var reviewedGameIntakeService = new ReviewedGameIntakeService(
            new GameRootPreflightService(policy), pluginLoadOrderService,
            bodySidecarInspectionService, generatedArtifactScanService, assetIndexer);
        var profileScanService = new ProfileScanService(pluginLoadOrderService, policy, labRoot);
        var recordListingService = new RecordListingService(pluginReader, policy, labRoot);
        var faceGenProviderResolution = new FaceGenProviderResolutionService(
            gameInventory, assetIndexer, policy, labRoot,
            new BethesdaFaceTintProviderBindingReader());
        var faceTintProviderBoundBuild = new FaceTintProviderBoundBuildService(
            faceGenProviderResolution, faceTintService, policy, labRoot);
        var faceGeomProviderBoundBuild = new FaceGeomProviderBoundBuildService(
            faceGenProviderResolution, faceGeomBinaryExporter, policy, labRoot);
        var packPlanService = new FaceGenPackPlanService(faceGenProviderResolution, policy, labRoot);
        var packageManifestReader = new PackageManifestReader(policy, labRoot);
        var packageVerifyService = new PackageVerifyService(packageManifestReader);
        var bodySlideTriInspectionService = new BodySlideTriInspectionService(policy, labRoot);
        var bodySlidePresetInspectionService = new BodySlideSliderPresetInspectionService(policy, labRoot);
        var actorAssemblyLoader = new ActorAssemblyPreflightDocumentLoader(labRoot);
        var actorAssemblyPreflightService = new ActorAssemblyPreflightService(
            actorAssemblyLoader,
            actorAssemblyLoader,
            packageVerifyService,
            new BethesdaActorAssemblyIdentityReader(policy, labRoot),
            bodySlidePresetInspectionService,
            bodySlideTriInspectionService);
        var finishCoreSourcePackageReader =
            new SkyrimNpcFinishCoreSourcePackageReader(
                labRoot,
                policy,
                packageManifestReader,
                packageVerifyService,
                new BethesdaSkyrimNpcFinishCoreSourceReader());
        var finishCoreService = new SkyrimNpcFinishCoreService(
            finishCoreSourcePackageReader.InspectAsync,
            labRoot);
        var interiorPlacementService = new SkyrimInteriorPlacementService(labRoot);
        using var voiceService = new SkyrimNpcVoiceService(labRoot);
        var blankNpcProviderService = new BlankNpcProviderService(policy, labRoot, assetIndexer);
        var qualifiedFaceGeomCarrierService = new QualifiedFaceGeomCarrierService(
            policy, labRoot);
        var npcCreationService = NpcCreationComposition.Create(policy, labRoot);
        var blankNpcBuildService = new BlankNpcBuildService(
            npcCreationService,
            blankNpcProviderService,
            qualifiedFaceGeomCarrierService,
            creationFaceTintService,
            creationFaceTintDecoder,
            packageVerifyService,
            policy,
            labRoot);
        var presetFaceTintBuildGuard = CreatePresetFaceTintBuildGuard();
        var presetBlankNpcBuildService = new BlankNpcBuildService(
            npcCreationService,
            blankNpcProviderService,
            qualifiedFaceGeomCarrierService,
            presetFaceTintBuildGuard,
            creationFaceTintDecoder,
            packageVerifyService,
            policy,
            labRoot);
        var faceBakeContentResolver = new SkyrimAssetContentResolver(policy, labRoot);
        var nativeAssetAuthorityPlanner =
            new SkyrimAssetAuthorityPlanner(assetIndexer, policy, labRoot);
        var faceMorphSnapshotService =
            new BethesdaSkyrimFaceMorphSnapshotService(policy, labRoot);
        var sharedFaceRecordRouteResolver =
            new BethesdaSkyrimFaceRecordRouteResolver(policy, labRoot);
        var externalHeadPartPhysicsBindingResolver =
            new ExternalHeadPartPhysicsBindingResolver(policy, labRoot);
        var externalHeadPartDependencyDiscovery =
            new ExternalHeadPartDependencyDiscoveryService(
                nativeAssetAuthorityPlanner,
                faceBakeContentResolver,
                externalHeadPartPhysicsBindingResolver);
        var externalHeadPartFaceGeomExclusionVerifier =
            new ExternalHeadPartFaceGeomExclusionVerifier();
        var raceMenuSelectedDependencyManifestWriter =
            new RaceMenuSelectedDependencyManifestWriter(policy, labRoot);
        var nativeDdsDecoder = new InProcessDdsTextureDecoder(labRoot);
        var nativeFaceTintRecordResolver =
            new BethesdaSkyrimNativeFaceTintRecordResolver(policy, labRoot);
        var nativeFaceTintMaterializer = new SkyrimNativeFaceTintMaterializationService(
            nativeFaceTintRecordResolver,
            faceBakeContentResolver,
            new SkyrimNativeFaceTintBuildService(
                policy, labRoot, nativeDdsDecoder));
        var nativeFaceTintPipeline = new SkyrimNativeFaceTintPipelineService(
            skyrimFaceRecordPluginAuthorityLoader,
            nativeFaceTintRecordResolver,
            new SkyrimNativeFaceTintAuthorityPlanner(
                assetIndexer, policy, labRoot),
            nativeFaceTintMaterializer);
        var nativeFaceGeomMaterializer =
            new SseFaceGeomCarrierMaterializationService(
                new SseFaceGeomCarrierAssembler(), policy, labRoot);
        var raceMenuCatalogParser = new RaceMenuSliderCatalogParserCore();
        var nativeRaceMenuCatalogLoader =
            new SkyrimRaceMenuCatalogAuthorityLoader(
                assetIndexer, nativeAssetAuthorityPlanner,
                faceBakeContentResolver);
        var nativeFaceGeomPipeline = new SkyrimNativeFaceGeomBuildService(
            skyrimFaceRecordPluginAuthorityLoader,
            sharedFaceRecordRouteResolver,
            faceMorphSnapshotService,
            nativeAssetAuthorityPlanner,
            faceBakeContentResolver,
            nativeRaceMenuCatalogLoader,
            raceMenuCatalogParser,
            new SseSelectedHeadpartNifGeometryReader(),
            new SseRaceMenuFaceBakeService(
                new SseTriHeadReader(), raceMenuCatalogParser,
                new SseFaceMorphPlanBuilder(),
                new SseFaceMorphEvaluator()),
            nativeFaceGeomMaterializer,
            externalHeadPartProviderSidecarResolver:
                new ExternalHeadPartProviderSidecarAuthorityResolver(
                    policy, labRoot),
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
            labRoot);
        var nativeFaceGenBatch = new FaceGenBakeAllService(
            new FaceGenBakeTargetDiscoveryService(
                gameInventory, policy, labRoot),
            nativeFaceGenNpcBakeService,
            policy,
            labRoot,
            assetIndexer,
            new SkyrimFaceGenSidecarOverlayLoader(
                new BodySidecarInspectionService(policy, labRoot)));
        var raceMenuFaceGeomMergeService = new RaceMenuCharGenFaceGeomMergeService(
            policy, labRoot);
        var raceMenuFaceGeomBuildService = new RaceMenuNpcFaceGeomBuildService(
            new SkyrimFaceBakeAuthorityLoader(
                policy, labRoot, faceBakeContentResolver),
            sharedFaceRecordRouteResolver,
            new SseSelectedHeadpartNifGeometryReader(),
            new SseRaceMenuFaceBakeService(),
            raceMenuFaceGeomMergeService);
        var directCharGenFaceGeomBuildService =
            new RaceMenuDirectCharGenFaceGeomBuildService(
                raceMenuFaceGeomMergeService, policy, labRoot);
        var raceMenuFaceTextureBuildService = new RaceMenuNpcFaceTextureBuildService(
            policy,
            labRoot,
            new InProcessDdsTextureDecoder(labRoot),
            creationFaceTintDecoder);
        var existingNpcAppearanceBuildService = new ExistingNpcAppearanceBuildService(
            new NpcAppearanceOverrideService(policy, labRoot),
            bodyGenService,
            qualifiedFaceGeomCarrierService,
            creationFaceTintDecoder,
            packageVerifyService,
            policy,
            labRoot);
        var raceMenuNpcAppearancePlanService = new RaceMenuNpcAppearancePlanService(
            presetService, blankNpcProviderService, policy, labRoot);
        var wholeSkinResolver = new BethesdaSkyrimNpcWholeSkinAuthorityResolver(
            skyrimFaceRecordPluginAuthorityLoader,
            nativeAssetAuthorityPlanner);
        var wholeSkinReader = new RaceMenuNpcWholeSkinAuthorityReader(
            wholeSkinResolver, policy, labRoot);
        var wholeSkinWriter = new RaceMenuNpcWholeSkinAuthorityWriter(
            policy, labRoot);
        var raceMenuNpcBuildService = new RaceMenuNpcBuildService(
            raceMenuNpcAppearancePlanService,
            faceMorphSnapshotService,
            bodyGenService,
            presetBlankNpcBuildService,
            assetIndexer,
            policy,
            labRoot,
            raceMenuFaceGeomBuildService,
            raceMenuFaceTextureBuildService,
            qualifiedFaceGeomCarrierService,
            creationFaceTintDecoder,
            existingNpcAppearanceBuildService,
            directCharGenFaceGeomBuildService,
            wholeSkinReader);
        var raceMenuRecordBuilder = new RaceMenuPresetRecordAuthorityBuilder(
            skyrimFaceRecordPluginAuthorityLoader,
            new BethesdaSkyrimMajorRecordBindingReader(
                skyrimFaceRecordPluginAuthorityLoader),
            new BethesdaSkyrimRaceTintAuthorityReader(
                skyrimFaceRecordPluginAuthorityLoader),
            new BethesdaSkyrimFaceTextureSetAuthorityReader(
                skyrimFaceRecordPluginAuthorityLoader),
            new RaceMenuPresetTintAuthorityMapper(),
            new BethesdaSkyrimFaceTextureSetMatchResolver(
                skyrimFaceRecordPluginAuthorityLoader));
        var raceMenuSelectionTransaction =
            new RaceMenuPresetSelectionTransactionService(
                raceMenuRecordBuilder,
                new RaceMenuPresetRecordAuthorityWriter(policy, labRoot),
                new RaceMenuPresetBundleAuthorityWriter(policy, labRoot),
                raceMenuNpcAppearancePlanService,
                raceMenuNpcBuildService,
                new RaceMenuPresetStandaloneAuthorityWriter(
                    faceMorphSnapshotService,
                    nativeAssetAuthorityPlanner,
                    creationFaceTintDecoder,
                    policy,
                    labRoot),
                raceMenuSelectedDependencyManifestWriter,
                wholeSkinResolver,
                wholeSkinWriter,
                policy,
                labRoot);
        var raceMenuJslotNpcBuildComposition =
            ProtocolV2GoldenWorkflowComposition
                .CreateJslotBuildComposition(labRoot);
        var referencePresetSessionService =
            new ReferencePresetSessionService(
                policy, labRoot);
        IReferencePresetAuthoringTransaction
            referencePresetTransaction =
                ReferencePresetCliComposition.Create(
                    policy,
                    labRoot,
                    referencePresetSessionService,
                    presetService,
                    raceMenuJslotNpcBuildComposition.BuildService);
        var previewAnimationListService = new PreviewAnimationListService(policy, labRoot);
        var existingNpcEditService = new ExistingNpcEditService(
            new NpcOverrideService(policy, labRoot),
            packageVerifyService,
            policy,
            labRoot);
        var packageArchiveService =
            new PackageArchiveService(
                packageVerifyService,
                policy,
                labRoot);
        var followerFinishLoader =
            new SkyrimFollowerFinishRequestFileLoader(labRoot);
        var followerFinishPluginService =
            new BethesdaSkyrimFollowerFinishPluginService(
                policy,
                labRoot);
        var followerFinishSourceReader =
            new SkyrimFollowerFinishSourcePackageReader(
                labRoot,
                policy,
                packageManifestReader,
                packageVerifyService,
                followerFinishPluginService);
        var followerFinishService =
            new SkyrimFollowerFinishService(
                followerFinishSourceReader.InspectAsync,
                followerFinishLoader,
                followerFinishPluginService,
                packageVerifyService,
                policy,
                packageArchiveService,
                labRoot);
        var npcVisualPreviewFactory =
            new NpcVisualPreviewCliComposition(
                policy,
                labRoot);
        var hairRegionsPreviewFactory =
            new FaceGeomHairRegionsPreviewCliComposition(
                policy,
                labRoot);
        var runner = new CliRunner(new CliRunnerServices(new WorkspacePreflightService(policy),
            gameInventory,
            new FormChoiceService(pluginReader, policy, labRoot),
            new AssetChoiceService(pluginReader, assetIndexer, policy, labRoot),
            new GameRootPreflightService(policy),
            new ArchiveConsistencyService(policy, labRoot),
            new GeneratedArtifactCommandHandler(generatedArtifactScanService, Console.Out, Console.Error),
            new BodySidecarCommandHandler(bodySidecarInspectionService, Console.Out, Console.Error),
            new AssetIndexExportService(assetIndexer, policy, labRoot),
            mutationService,
            new NpcTemplateMaterializationService(policy, labRoot),
            new NpcResetService(policy, labRoot, mutationService),
            presetService,
            new PresetFormResolver(policy, labRoot),
            faceGenService,
            bodyGenService,
            new PresetToNpcPipeline(presetService, mutationService, bodyGenService, policy, labRoot, faceGeomService,
                faceTintService, bodySidecarWriteService, runtimeScriptDeployService),
            pluginLoadOrderService, labRoot,
            new NpcFacePatchService(policy, labRoot),
            new FaceGenOptionsService(policy, labRoot),
            new NpcFaceTintPatchService(policy, labRoot),
            new SkyrimFaceMorphPatchService(policy, labRoot),
            new RaceMenuExtendedMorphPatchService(policy, labRoot),
            new SkyrimFaceTintPatchService(policy, labRoot),
            new RaceMenuSculptPatchService(policy, labRoot),
            new FacePoseResolverService(policy, labRoot),
            new FaceSectionResetService(policy, labRoot),
            new BodySlideResolutionService(presetService, bodySlideTriInspectionService, policy, labRoot),
            new SseBodyWeightResolutionService(policy, labRoot),
            new BodyOverlayPatchService(),
            new SkyrimOverlayFoldService(),
            new SkyrimBodyTransformService(policy, labRoot),
            new BodySectionResetService(policy, labRoot),
            faceGeomService,
            faceTintService,
            new FaceGenCorrectionService(faceGenService, policy, labRoot),
            new FaceGenBatchService(faceGenService, policy, labRoot),
            new FaceGenPluginBuildService(faceGenService, policy, labRoot),
            new PreviewSceneService(policy, labRoot, previewImageRenderer),
            new PreviewRerollService(policy, labRoot),
            new PreviewNifExportService(policy, labRoot),
            new OutfitChoiceService(pluginReader, policy, labRoot),
            new OutfitProposalService(pluginReader, policy, labRoot),
            new LeveledListProposalService(pluginReader, policy, labRoot),
            new LeveledListResolveService(policy, labRoot),
            new ArmorProposalService(pluginReader, policy, labRoot),
            new ArmorDamageResistanceService(pluginReader, policy, labRoot),
            new ArmorAddonProposalService(pluginReader, policy, labRoot),
            new ArmorAddonModelProposalService(pluginReader, policy, labRoot),
            new MaterialSwapProposalService(pluginReader, policy, labRoot),
            new ObjectTemplateProposalService(pluginReader, policy, labRoot),
            new ObjectTemplatePropertyProposalService(pluginReader, policy, labRoot),
            new ChangeTrackingService(policy, labRoot),
            new ChangeActionService(policy, labRoot),
            new RecordProposalService(policy, labRoot),
            new PluginWriteService(policy, labRoot, mutationService),
            new PluginVerifyService(policy, labRoot),
            bodySidecarWriteService,
            new RuntimeScriptProposalService(policy, labRoot),
            new RuntimeScriptBuildService(policy, labRoot),
            new SchemaExportService(policy, labRoot),
            new DesktopLaunchService(policy, labRoot),
            new BethesdaOutfitBinaryWriteService(policy, labRoot),
            new BethesdaLeveledListBinaryWriteService(policy, labRoot),
            new BethesdaArmorBinaryWriteService(policy, labRoot),
            new BethesdaArmorAddonBinaryWriteService(policy, labRoot),
            new BethesdaMaterialSwapBinaryWriteService(policy, labRoot),
            new BethesdaObjectTemplateBinaryWriteService(policy, labRoot),
            new BethesdaRuntimeScriptBinaryWriteService(policy, labRoot),
            new RuntimeScriptPackageService(policy, labRoot),
            runtimeScriptDeployService,
            faceGeomBinaryRouter,
            previewNifBinaryExporter,
            faceGenProviderResolution,
            packPlanService,
            faceTintProviderBoundBuild,
            faceGeomProviderBoundBuild,
            new BethesdaRuntimeScriptVmadInspectService(policy, labRoot),
            new FaceGenPackService(packPlanService, policy, labRoot),
            new PackageBuildService(packageVerifyService, policy, labRoot),
            new PackageInspectService(packageManifestReader),
            packageVerifyService,
            packageArchiveService,
            new PluginSurfaceAuditService(pluginReader, policy, labRoot),
            new RuntimeSmokeVerifyService(policy, labRoot),
            pluginDeployService: new PluginDeployService(policy, labRoot),
            faceGenDeployService: new FaceGenDeployService(policy, labRoot),
            previewAnimationListService: previewAnimationListService,
            previewAnimationTreeService: new PreviewAnimationTreeService(previewAnimationListService),
            profileScanService: profileScanService,
            recordListingService: recordListingService,
            blankNpcBuildService: blankNpcBuildService,
            raceMenuNpcBuildService: raceMenuNpcBuildService,
            existingNpcEditService: existingNpcEditService,
            skyrimNativeFaceTintPipelineService: nativeFaceTintPipeline,
            nativeFaceGenBatchService: nativeFaceGenBatch,
            reviewedGameIntakeService: reviewedGameIntakeService,
            raceMenuPresetCatalogService: raceMenuPresetCatalogService,
            skyrimFaceRecordPluginAuthorityLoader:
                skyrimFaceRecordPluginAuthorityLoader,
            skyrimHeadPartChoiceService:
                new BethesdaSkyrimHeadPartChoiceService(
                    skyrimFaceRecordPluginAuthorityLoader),
            skyrimRaceMenuPaintChoiceService:
                new BethesdaSkyrimRaceMenuPaintChoiceService(
                    policy,
                    labRoot),
            raceMenuJslotNpcBuildService:
                raceMenuJslotNpcBuildComposition.BuildService,
            bodySlideSliderPresetInspectionService:
                bodySlidePresetInspectionService,
            referencePresetAuthoringTransaction:
                referencePresetTransaction,
            referencePresetSessionService:
                referencePresetSessionService,
            referencePresetExecutionRequestFileLoader:
                raceMenuJslotNpcBuildComposition.RequestLoader,
            skyrimFollowerFinishService:
                followerFinishService,
            skyrimFollowerFinishRequestFileLoader:
                followerFinishLoader,
            skyrimFollowerFinishPairService:
                new BethesdaSkyrimFollowerFinishPairService(
                    labRoot,
                    new SkyrimFollowerFinishPairFaceGeomService()),
            npcVisualPreviewFactory:
                npcVisualPreviewFactory,
            faceGeomHairRegionsPreviewFactory:
                hairRegionsPreviewFactory,
            actorAssemblyPreflightService:
                actorAssemblyPreflightService,
            skyrimNpcFinishCoreService:
                finishCoreService,
            skyrimInteriorPlacementService:
                interiorPlacementService,
            npcBuildPreflightService:
                raceMenuJslotNpcBuildComposition.PreflightService,
            localOperationJournalFactory: journalFactory,
            skyrimNpcVoiceService: voiceService,
            skyrimNpcDialogueService: new SkyrimNpcDialogueService(
                labRoot, SkyrimDialogueCoverageTemplates.All, journalFactory: () => journalFactory(labRoot))),
            output,
            error);
        return await runner.RunAsync(command, cancellationToken);
    }

    internal static IFaceTintBuildService CreatePresetFaceTintBuildGuard() =>
        new ExactOnlyFaceTintBuildService();
}
