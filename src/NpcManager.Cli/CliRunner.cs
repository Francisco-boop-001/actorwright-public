using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed partial class CliRunner(CliRunnerServices services, TextWriter output, TextWriter error)
{
    private readonly WorkspacePath _workspaceRoot = services.workspaceRoot;
    private readonly ActorAssemblyPreflightCommandHandler? _actorAssemblyPreflightCommands =
        services.actorAssemblyPreflightService is null
            ? null
            : new(services.actorAssemblyPreflightService, output, error);
    private readonly SkyrimNpcFinishCoreCommandHandler? _skyrimNpcFinishCoreCommands =
        services.skyrimNpcFinishCoreService is null
            ? null
            : new(services.skyrimNpcFinishCoreService, services.workspaceRoot, output, error);
    private readonly SkyrimInteriorPlacementCommandHandler? _skyrimInteriorPlacementCommands =
        services.skyrimInteriorPlacementService is null
            ? null
            : new(services.skyrimInteriorPlacementService, services.workspaceRoot, output, error);
    private readonly SkyrimNpcVoiceCommandHandler? _skyrimNpcVoiceCommands =
        services.skyrimNpcVoiceService is null ? null : new(services.skyrimNpcVoiceService, output, error);
    private readonly SkyrimNpcDialogueCommandHandler _skyrimNpcDialogueCommands =
        new(services.skyrimNpcDialogueService, output, error);
    private readonly MutationCommandHandler _mutationCommands = new(services.mutationService, output, error);
    private readonly TemplateCommandHandler _templateCommands = new(services.templateMaterializationService, output, error);
    private readonly ResetCommandHandler _resetCommands = new(services.resetService, output, error);
    private readonly FacePatchCommandHandler? _facePatchCommands = services.facePatchService is null ? null : new(services.facePatchService, output, error);
    private readonly FaceGenOptionsCommandHandler? _faceGenOptionsCommands = services.faceGenOptionsService is null ? null : new(services.faceGenOptionsService, output, error);
    private readonly FaceTintPatchCommandHandler? _faceTintPatchCommands = services.faceTintPatchService is null ? null : new(services.faceTintPatchService, output, error);
    private readonly SkyrimFaceTintPatchCommandHandler? _skyrimFaceTintPatchCommands = services.skyrimFaceTintPatchService is null
        ? null : new(services.skyrimFaceTintPatchService, output, error,
            () => services.localOperationJournalFactory is { } factory
                ? factory(services.workspaceRoot) : new LocalOperationJournal(services.workspaceRoot));
    private readonly SkyrimFaceMorphPatchCommandHandler? _skyrimFaceMorphPatchCommands = services.skyrimFaceMorphPatchService is null ? null : new(services.skyrimFaceMorphPatchService, output, error);
    private readonly RaceMenuExtendedMorphPatchCommandHandler? _raceMenuExtendedMorphPatchCommands = services.raceMenuExtendedMorphPatchService is null ? null : new(services.raceMenuExtendedMorphPatchService, output, error);
    private readonly RaceMenuSculptPatchCommandHandler? _raceMenuSculptPatchCommands = services.raceMenuSculptPatchService is null ? null : new(services.raceMenuSculptPatchService, output, error);
    private readonly FacePoseCommandHandler? _facePoseCommands = services.facePoseResolver is null ? null : new(services.facePoseResolver, output, error);
    private readonly FaceResetCommandHandler? _faceResetCommands = services.faceResetService is null ? null : new(services.faceResetService, output, error);
    private readonly BodySlideResolutionCommandHandler? _bodySlideResolutionCommands =
        services.bodySlideResolutionService is null ? null : new(services.bodySlideResolutionService, output, error);
    private readonly SseBodyWeightResolutionCommandHandler? _sseBodyWeightResolutionCommands =
        services.sseBodyWeightResolutionService is null ? null : new(services.sseBodyWeightResolutionService, output, error);
    private readonly BodyOverlayCommandHandler? _bodyOverlayCommands =
        services.bodyOverlayPatchService is null ? null : new(services.bodyOverlayPatchService, output, error);
    private readonly SkyrimOverlayBakeCommandHandler? _skyrimOverlayBakeCommands =
        services.skyrimOverlayFoldService is null ? null : new(services.skyrimOverlayFoldService, output, error);
    private readonly SkyrimBodyTransformCommandHandler? _skyrimBodyTransformCommands =
        services.skyrimBodyTransformService is null ? null : new(services.skyrimBodyTransformService, output, error);
    private readonly BodyResetCommandHandler? _bodyResetCommands =
        services.bodyResetService is null ? null : new(services.bodyResetService, output, error);
    private readonly BodySlideSliderPresetInspectionCommandHandler? _bodySlidePresetInspectionCommands =
        services.bodySlideSliderPresetInspectionService is null
            ? null
            : new(services.bodySlideSliderPresetInspectionService, output, error);
    private readonly FaceGeomBuildCommandHandler? _faceGeomBuildCommands =
        services.faceGeomBuildService is null ? null : new(services.faceGeomBuildService, output, error);
    private readonly FaceGeomBinaryBuildCommandHandler? _faceGeomBinaryBuildCommands =
        services.faceGeomBinaryBuildService is null ? null : new(services.faceGeomBinaryBuildService, output, error);
    private readonly FaceTintBuildCommandHandler? _faceTintBuildCommands =
        services.faceTintBuildService is null ? null : new(services.faceTintBuildService, output, error);
    private readonly FaceGenCorrectionCommandHandler? _faceGenCorrectionCommands =
        services.faceGenCorrectionService is null ? null : new(services.faceGenCorrectionService, output, error);
    private readonly FaceGenBatchCommandHandler? _faceGenBatchCommands =
        services.faceGenBatchService is null ? null : new(services.faceGenBatchService, output, error);
    private readonly FaceGenPluginBuildCommandHandler? _faceGenPluginBuildCommands =
        services.faceGenPluginBuildService is null ? null : new(services.faceGenPluginBuildService, output, error);
    private readonly FaceGenProviderResolutionCommandHandler? _faceGenProviderResolutionCommands =
        services.faceGenProviderResolutionService is null ? null : new(services.faceGenProviderResolutionService, output, error);
    private readonly FaceGenPackPlanCommandHandler? _faceGenPackPlanCommands =
        services.faceGenPackPlanService is null ? null : new(services.faceGenPackPlanService, output, error);
    private readonly FaceTintProviderBoundBuildCommandHandler? _faceTintProviderBoundBuildCommands =
        services.faceTintProviderBoundBuildService is null ? null : new(services.faceTintProviderBoundBuildService, output, error);
    private readonly SkyrimNativeFaceTintCommandHandler? _skyrimNativeFaceTintCommands =
        services.skyrimNativeFaceTintPipelineService is null
            ? null
            : new(services.skyrimNativeFaceTintPipelineService, output, error);
    private readonly NativeFaceGenBatchCommandHandler? _nativeFaceGenBatchCommands =
        services.nativeFaceGenBatchService is null
            ? null
            : new(services.nativeFaceGenBatchService, output, error);
    private readonly FaceGeomProviderBoundBuildCommandHandler? _faceGeomProviderBoundBuildCommands =
        services.faceGeomProviderBoundBuildService is null ? null : new(services.faceGeomProviderBoundBuildService, output, error);
    private readonly PreviewSceneCommandHandler? _previewSceneCommands =
        services.previewSceneService is null ? null : new(services.previewSceneService, output, error);
    private readonly PreviewRerollCommandHandler? _previewRerollCommands =
        services.previewRerollService is null ? null : new(services.previewRerollService, output, error);
    private readonly PreviewNifExportCommandHandler? _previewNifExportCommands =
        services.previewNifExportService is null ? null : new(services.previewNifExportService, services.previewNifBinaryExportService, output, error);
    private readonly NpcVisualPreviewCommandHandler? _npcVisualPreviewCommands =
        services.npcVisualPreviewFactory is null
            ? null
            : new(
                services.npcVisualPreviewFactory,
                new KOnlyWorkspacePolicy(
                    services.workspaceRoot,
                    ActorwrightWorkspace.ResolveProtectedRoot(
                        services.workspaceRoot)),
                services.workspaceRoot,
                new FaceGeomHairRegionsDocumentCodec(
                    services.workspaceRoot),
                output,
                error);
    private readonly PreviewAnimationListCommandHandler? _previewAnimationListCommands =
        services.previewAnimationListService is null ? null : new(services.previewAnimationListService, output, error);
    private readonly PreviewAnimationTreeCommandHandler? _previewAnimationTreeCommands =
        services.previewAnimationTreeService is null ? null : new(services.previewAnimationTreeService, output, error);
    private readonly ProfileScanCommandHandler? _profileScanCommands =
        services.profileScanService is null ? null : new(services.profileScanService, output, error);
    private readonly RecordListCommandHandler? _recordListCommands =
        services.recordListingService is null ? null : new(services.recordListingService, output, error);
    private readonly OutfitChoiceCommandHandler? _outfitChoiceCommands =
        services.outfitChoiceService is null ? null : new(services.outfitChoiceService, output, error);
    private readonly OutfitProposalCommandHandler? _outfitProposalCommands =
        services.outfitProposalService is null ? null : new(services.outfitProposalService, output, error);
    private readonly OutfitBinaryWriteCommandHandler? _outfitBinaryWriteCommands =
        services.outfitBinaryWriteService is null ? null : new(services.outfitBinaryWriteService, output, error);
    private readonly LeveledListBinaryWriteCommandHandler? _leveledListBinaryWriteCommands =
        services.leveledListBinaryWriteService is null ? null : new(services.leveledListBinaryWriteService, output, error);
    private readonly ArmorBinaryWriteCommandHandler? _armorBinaryWriteCommands =
        services.armorBinaryWriteService is null ? null : new(services.armorBinaryWriteService, output, error);
    private readonly ArmorAddonBinaryWriteCommandHandler? _armorAddonBinaryWriteCommands =
        services.armorAddonBinaryWriteService is null ? null : new(services.armorAddonBinaryWriteService, output, error);
    private readonly MaterialSwapBinaryWriteCommandHandler? _materialSwapBinaryWriteCommands =
        services.materialSwapBinaryWriteService is null ? null : new(services.materialSwapBinaryWriteService, output, error);
    private readonly ObjectTemplateBinaryWriteCommandHandler? _objectTemplateBinaryWriteCommands =
        services.objectTemplateBinaryWriteService is null ? null : new(services.objectTemplateBinaryWriteService, output, error);
    private readonly RuntimeScriptBinaryWriteCommandHandler? _runtimeScriptBinaryWriteCommands =
        services.runtimeScriptBinaryWriteService is null ? null : new(services.runtimeScriptBinaryWriteService, output, error);
    private readonly RuntimeScriptPackageCommandHandler? _runtimeScriptPackageCommands =
        services.runtimeScriptPackageService is null ? null : new(services.runtimeScriptPackageService, output, error);
    private readonly RuntimeScriptDeployCommandHandler? _runtimeScriptDeployCommands =
        services.runtimeScriptDeployService is null ? null : new(services.runtimeScriptDeployService, output, error);
    private readonly RuntimeScriptVmadInspectCommandHandler? _runtimeScriptVmadInspectCommands =
        services.runtimeScriptVmadInspectService is null ? null : new(services.runtimeScriptVmadInspectService, output, error);
    private readonly FaceGenPackCommandHandler? _faceGenPackCommands =
        services.faceGenPackService is null ? null : new(services.faceGenPackService, output, error);
    private readonly FaceGenDeployCommandHandler? _faceGenDeployCommands =
        services.faceGenDeployService is null ? null : new(services.faceGenDeployService, output, error);
    private readonly PackageCommandHandler? _packageCommands =
        services.packageBuildService is null ||
        services.packageInspectService is null ||
        services.packageVerifyService is null ||
        services.packageArchiveService is null
            ? null
            : new(
                services.packageBuildService,
                services.packageInspectService,
                services.packageVerifyService,
                services.packageArchiveService,
                services.workspaceRoot,
                output,
                error,
                services.skyrimNpcFinishCoreService);
    private readonly BlankNpcBuildCommandHandler? _blankNpcBuildCommands =
        services.blankNpcBuildService is null ? null : new(services.blankNpcBuildService, output, error);
    private readonly RaceMenuNpcBuildCommandHandler? _raceMenuNpcBuildCommands =
        services.raceMenuNpcBuildService is null
            ? null
            : new(services.raceMenuNpcBuildService, services.workspaceRoot, output, error);
    private readonly RaceMenuJslotNpcBuildCommandHandler? _raceMenuJslotNpcBuildCommands =
        services.raceMenuJslotNpcBuildService is null
            ? null
            : new(services.raceMenuJslotNpcBuildService, services.workspaceRoot, output, error,
                preflightService: services.npcBuildPreflightService);
    private readonly ReferencePresetCommandHandler? _referencePresetCommands =
        services.referencePresetAuthoringTransaction is null ||
        services.referencePresetSessionService is null
            ? null
            : new(
                services.referencePresetAuthoringTransaction,
                services.referencePresetSessionService,
                services.referencePresetExecutionRequestFileLoader ??
                new NpcManager.Infrastructure
                    .RaceMenuNpcExecutionRequestFileLoader(
                        services.workspaceRoot),
                output,
                error);
    private readonly ExistingNpcEditCommandHandler? _existingNpcEditCommands =
        services.existingNpcEditService is null
            ? null
            : new(services.existingNpcEditService, output, error);
    private readonly SkyrimFollowerFinishCommandHandler?
        _skyrimFollowerFinishCommands =
            services.skyrimFollowerFinishService is null ||
            services.skyrimFollowerFinishRequestFileLoader is null
                ? null
                : new(
                    services.skyrimFollowerFinishService,
                    services.skyrimFollowerFinishRequestFileLoader,
                    output,
                    error);
    private readonly SkyrimFollowerFinishPairCommandHandler?
        _skyrimFollowerFinishPairCommands =
            services.skyrimFollowerFinishPairService is null
                ? null
                : new(
                    services.skyrimFollowerFinishPairService,
                    output,
                    error);
    private readonly LeveledListProposalCommandHandler? _leveledListProposalCommands =
        services.leveledListProposalService is null ? null : new(services.leveledListProposalService, output, error);
    private readonly LeveledListResolveCommandHandler? _leveledListResolveCommands =
        services.leveledListResolveService is null ? null : new(services.leveledListResolveService, output, error);
    private readonly ArmorProposalCommandHandler? _armorProposalCommands =
        services.armorProposalService is null ? null : new(services.armorProposalService, output, error);
    private readonly ArmorDamageResistanceCommandHandler? _armorDamageResistanceCommands =
        services.armorDamageResistanceService is null ? null : new(services.armorDamageResistanceService, output, error);
    private readonly ArmorAddonProposalCommandHandler? _armorAddonProposalCommands =
        services.armorAddonProposalService is null ? null : new(services.armorAddonProposalService, services.armorAddonModelProposalService, output, error);
    private readonly MaterialSwapProposalCommandHandler? _materialSwapProposalCommands =
        services.materialSwapProposalService is null ? null : new(services.materialSwapProposalService, output, error);
    private readonly ObjectTemplateProposalCommandHandler? _objectTemplateProposalCommands =
        services.objectTemplateProposalService is null ? null : new(services.objectTemplateProposalService, output, error);
    private readonly ObjectTemplatePropertyProposalCommandHandler? _objectTemplatePropertyProposalCommands =
        services.objectTemplatePropertyProposalService is null ? null : new(services.objectTemplatePropertyProposalService, output, error);
    private readonly ChangeTrackingCommandHandler? _changeTrackingCommands =
        services.changeTrackingService is null ? null : new(services.changeTrackingService, output, error);
    private readonly ChangeActionCommandHandler? _changeActionCommands =
        services.changeActionService is null ? null : new(services.changeActionService, output, error);
    private readonly RecordProposalCommandHandler? _recordProposalCommands =
        services.recordProposalService is null ? null : new(services.recordProposalService, output, error);
    private readonly PluginWriteCommandHandler? _pluginWriteCommands =
        services.pluginWriteService is null ? null : new(services.pluginWriteService, output, error);
    private readonly PluginVerifyCommandHandler? _pluginVerifyCommands =
        services.pluginVerifyService is null ? null : new(services.pluginVerifyService, output, error);
    private readonly PluginSurfaceAuditCommandHandler? _pluginSurfaceAuditCommands =
        services.pluginSurfaceAuditService is null ? null : new(services.pluginSurfaceAuditService, output, error);
    private readonly PluginDeployCommandHandler? _pluginDeployCommands =
        services.pluginDeployService is null ? null : new(services.pluginDeployService, output, error);
    private readonly RuntimeSmokeVerifyCommandHandler? _runtimeSmokeVerifyCommands =
        services.runtimeSmokeVerifyService is null ? null : new(services.runtimeSmokeVerifyService, output, error);
    private readonly RuntimeSmokeVerifyAllCommandHandler? _runtimeSmokeVerifyAllCommands =
        services.runtimeSmokeVerifyService is null ? null : new(services.runtimeSmokeVerifyService, output, error);
    private readonly BodySidecarWriteCommandHandler? _bodySidecarWriteCommands =
        services.bodySidecarWriteService is null ? null : new(services.bodySidecarWriteService, services.workspaceRoot, output, error);
    private readonly RuntimeScriptProposalCommandHandler? _runtimeScriptProposalCommands =
        services.runtimeScriptProposalService is null ? null : new(services.runtimeScriptProposalService, services.workspaceRoot, output, error);
    private readonly RuntimeScriptBuildCommandHandler? _runtimeScriptBuildCommands =
        services.runtimeScriptBuildService is null ? null : new(services.runtimeScriptBuildService, output, error);
    private readonly SchemaExportCommandHandler? _schemaExportCommands =
        services.schemaExportService is null ? null : new(services.schemaExportService, output, error);
    private readonly IDesktopLaunchService? _desktopLaunchService = services.desktopLaunchService;
    private readonly WeightTriangleCommandHandler _weightTriangleCommands = new(output, error);
    private readonly PresetCommandHandler _presetCommands = new(
        services.presetService,
        services.formResolver,
        services.raceMenuPresetCatalogService,
        services.skyrimFaceRecordPluginAuthorityLoader,
        output,
        error);
    private readonly FaceGenCommandHandler _faceGenCommands = new(services.faceGenService, output, error);
    private readonly FaceGeomHairRegionsCommandHandler
        _faceGeomHairRegionsCommands =
        FaceGeomHairRegionsCliComposition.Create(
            services.workspaceRoot,
            services.faceGeomHairRegionsPreviewFactory,
            output,
            error);
    private readonly BodyGenCommandHandler _bodyGenCommands = new(services.bodyGenService, services.workspaceRoot, output, error);
    private readonly PipelineCommandHandler _pipelineCommands = new(services.pipeline, output, error);
    private readonly PluginCommandHandler _pluginCommands = new(services.pluginLoadOrderService, output, error);
    private readonly FormChoiceCommandHandler _formChoiceCommands = new(services.formChoiceService, output, error);
    private readonly AssetChoiceCommandHandler _assetChoiceCommands = new(services.assetChoiceService, output, error);
    private readonly SkyrimHeadPartChoiceCommandHandler? _skyrimHeadPartChoiceCommands =
        services.skyrimHeadPartChoiceService is null
            ? null
            : new(
                services.skyrimHeadPartChoiceService,
                output,
                error);
    private readonly SkyrimRaceMenuPaintChoiceCommandHandler? _skyrimRaceMenuPaintChoiceCommands =
        services.skyrimRaceMenuPaintChoiceService is null
            ? null
            : new(
                services.skyrimRaceMenuPaintChoiceService,
                output,
                error);
    private readonly GeneratedArtifactCommandHandler _generatedArtifactCommands = services.generatedArtifactCommands;
    private readonly BodySidecarCommandHandler _bodySidecarCommands = services.bodySidecarCommands;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

}
