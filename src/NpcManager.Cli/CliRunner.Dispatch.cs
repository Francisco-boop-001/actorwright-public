using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

public sealed partial class CliRunner
{
    internal static CommandExitCode? RunKernel(
        ParsedCommand command, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (command.Help && TryWriteCommandHelp(command, output))
            return CommandExitCode.Success;

        if (command.Help || command.Name == "help")
        {
            WriteHelp(output);
            return CommandExitCode.Success;
        }

        return command.Name switch
        {
            "version" => WriteVersion(command.Json, output),
            "capabilities" => WriteCapabilities(command.Json, output),
            "diagnose" => WriteDiagnosis(command.Json, output),
            _ => null
        };
    }

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (RunKernel(command, output, error, cancellationToken) is { } kernelExit)
            return kernelExit;

        return command.Name switch
        {
            "schema export" => _schemaExportCommands is null
                ? WriteUsageError(command.Json, "schema export is unavailable in this runner configuration.")
                : await _schemaExportCommands.RunAsync(command, cancellationToken),
            "workspace preflight" => await RunPreflightAsync(command, cancellationToken),
            "workspace scan-generated" => await _generatedArtifactCommands.RunAsync(command, cancellationToken),
            "body sidecar inspect" => await _bodySidecarCommands.RunAsync(command, cancellationToken),
            "body sidecar write" => _bodySidecarWriteCommands is null
                ? WriteUsageError(command.Json, "body sidecar write is unavailable in this runner configuration.")
                : await _bodySidecarWriteCommands.RunAsync(command, cancellationToken),
            "body sliders resolve" => _bodySlideResolutionCommands is null
                ? WriteUsageError(command.Json, "body sliders resolve is unavailable in this runner configuration.")
                : await _bodySlideResolutionCommands.RunAsync(command, cancellationToken),
            "body sliders inspect-preset" => _bodySlidePresetInspectionCommands is null
                ? WriteUsageError(command.Json, "body sliders inspect-preset is unavailable in this runner configuration.")
                : await _bodySlidePresetInspectionCommands.RunAsync(command, cancellationToken),
            "body weight resolve" => _sseBodyWeightResolutionCommands is null
                ? WriteUsageError(command.Json, "body weight resolve is unavailable in this runner configuration.")
                : await _sseBodyWeightResolutionCommands.RunAsync(command, cancellationToken),
            "body overlay patch" => _bodyOverlayCommands is null
                ? WriteUsageError(command.Json, "body overlay patch is unavailable in this runner configuration.")
                : await _bodyOverlayCommands.RunAsync(command, cancellationToken),
            "body overlay bake" => _skyrimOverlayBakeCommands is null
                ? WriteUsageError(command.Json, "body overlay bake is unavailable in this runner configuration.")
                : await _skyrimOverlayBakeCommands.RunAsync(command, cancellationToken),
            "body transforms apply" => _skyrimBodyTransformCommands is null
                ? WriteUsageError(command.Json, "body transforms apply is unavailable in this runner configuration.")
                : await _skyrimBodyTransformCommands.RunAsync(command, cancellationToken),
            "body reset" => _bodyResetCommands is null
                ? WriteUsageError(command.Json, "body reset is unavailable in this runner configuration.")
                : await _bodyResetCommands.RunAsync(command, cancellationToken),
            "profile scan" => _profileScanCommands is null
                ? WriteUsageError(command.Json, "profile scan is unavailable in this runner configuration.")
                : await _profileScanCommands.RunAsync(command, cancellationToken),
            "npc create" => _blankNpcBuildCommands is null
                ? WriteUsageError(command.Json, "npc create is unavailable in this runner configuration.")
                : await _blankNpcBuildCommands.RunAsync(command, cancellationToken),
            "npc create-from-preset" => _raceMenuNpcBuildCommands is null
                ? WriteUsageError(command.Json, "npc create-from-preset is unavailable in this runner configuration.")
                : await _raceMenuNpcBuildCommands.RunAsync(command, cancellationToken),
            "npc create-from-jslot" => _raceMenuJslotNpcBuildCommands is null
                ? WriteUsageError(command.Json, "npc create-from-jslot is unavailable in this runner configuration.")
                : await _raceMenuJslotNpcBuildCommands.RunAsync(command, cancellationToken),
            "npc assembly preflight" => _actorAssemblyPreflightCommands is null
                ? WriteUsageError(command.Json, "npc assembly preflight is unavailable in this runner configuration.")
                : await _actorAssemblyPreflightCommands.RunAsync(command, cancellationToken),
            "npc finish analyze" or
            "npc finish apply" or
            "npc finish verify" => _skyrimNpcFinishCoreCommands is null
                ? WriteUsageError(command.Json, "npc finish is unavailable in this runner configuration.")
                : await _skyrimNpcFinishCoreCommands.RunAsync(command, cancellationToken),
            "npc placement interior analyze" or
            "npc placement interior apply" or
            "npc placement interior verify" => _skyrimInteriorPlacementCommands is null
                ? WriteUsageError(command.Json, "npc placement interior is unavailable in this runner configuration.")
                : await _skyrimInteriorPlacementCommands.RunAsync(command, cancellationToken),
            "npc voice discover" or "npc voice import" or "npc voice synthesize" =>
                _skyrimNpcVoiceCommands is null
                    ? WriteUsageError(command.Json, "npc voice is unavailable in this runner configuration.")
                    : await _skyrimNpcVoiceCommands.RunAsync(command, cancellationToken),
            "npc dialogue analyze" or "npc dialogue apply" or "npc dialogue verify" =>
                await _skyrimNpcDialogueCommands.RunAsync(command, cancellationToken),
            "preset design-propose" or
            "preset create-from-reference" or
            "npc create-from-reference" =>
                _referencePresetCommands is null
                    ? WriteUsageError(
                        command.Json,
                        "reference preset authoring is unavailable in this runner configuration.")
                    : await _referencePresetCommands.RunAsync(
                        command, cancellationToken),
            "npc edit-package" => _existingNpcEditCommands is null
                ? WriteUsageError(command.Json, "npc edit-package is unavailable in this runner configuration.")
                : await _existingNpcEditCommands.RunAsync(command, cancellationToken),
            "npc follower-finish analyze" or
            "npc follower-finish apply" or
            "npc follower-finish verify" =>
                _skyrimFollowerFinishCommands is null
                    ? WriteUsageError(
                        command.Json,
                        "npc follower-finish is unavailable in this runner configuration.")
                    : await _skyrimFollowerFinishCommands.RunAsync(
                        command,
                        cancellationToken),
            "npc follower-finish pair-analyze" or
            "npc follower-finish pair-apply" or
            "npc follower-finish pair-verify" =>
                _skyrimFollowerFinishPairCommands is null
                    ? WriteUsageError(
                        command.Json,
                        "paired npc follower-finish is unavailable in this runner configuration.")
                    : await _skyrimFollowerFinishPairCommands.RunAsync(
                        command,
                        cancellationToken),
            "npc list" => await RunNpcListAsync(command, cancellationToken),
            "npc search" => await RunNpcListAsync(command, cancellationToken),
            "records list" => _recordListCommands is null
                ? WriteUsageError(command.Json, "records list is unavailable in this runner configuration.")
                : await _recordListCommands.RunAsync(command, cancellationToken),
            "npc inspect" => await RunNpcInspectAsync(command, cancellationToken),
            "forms search" => await _formChoiceCommands.RunAsync(command, cancellationToken),
            "assets search" => await _assetChoiceCommands.RunAsync(command, cancellationToken),
            "headpart choices" => _skyrimHeadPartChoiceCommands is null
                ? WriteUsageError(command.Json, "headpart choices is unavailable in this runner configuration.")
                : await _skyrimHeadPartChoiceCommands.RunAsync(command, cancellationToken),
            "paint choices" => _skyrimRaceMenuPaintChoiceCommands is null
                ? WriteUsageError(command.Json, "paint choices is unavailable in this runner configuration.")
                : await _skyrimRaceMenuPaintChoiceCommands.RunAsync(command, cancellationToken),
            "npc patch" => await _mutationCommands.RunPatchAsync(command, cancellationToken),
            "body patch" => await _mutationCommands.RunPatchAsync(command, cancellationToken),
            "npc reset" => await _resetCommands.RunAsync(command, cancellationToken),
            "npc face-patch" => _facePatchCommands is null ? WriteUsageError(command.Json, "npc face-patch is unavailable in this runner configuration.") : await _facePatchCommands.RunAsync(command, cancellationToken),
            "facegen options" => _faceGenOptionsCommands is null ? WriteUsageError(command.Json, "facegen options is unavailable in this runner configuration.") : await _faceGenOptionsCommands.RunAsync(command, cancellationToken),
            "face tint patch" => await RunFaceTintPatchAsync(command, cancellationToken),
            "face morph patch" => _skyrimFaceMorphPatchCommands is null ? WriteUsageError(command.Json, "face morph patch is unavailable in this runner configuration.") : await _skyrimFaceMorphPatchCommands.RunAsync(command, cancellationToken),
            "face morph extended" => _raceMenuExtendedMorphPatchCommands is null ? WriteUsageError(command.Json, "face morph extended is unavailable in this runner configuration.") : await _raceMenuExtendedMorphPatchCommands.RunAsync(command, cancellationToken),
            "face sculpt patch" => _raceMenuSculptPatchCommands is null ? WriteUsageError(command.Json, "face sculpt patch is unavailable in this runner configuration.") : await _raceMenuSculptPatchCommands.RunAsync(command, cancellationToken),
            "face pose resolve" => _facePoseCommands is null ? WriteUsageError(command.Json, "face pose resolve is unavailable in this runner configuration.") : await _facePoseCommands.RunAsync(command, cancellationToken),
            "face reset" => _faceResetCommands is null ? WriteUsageError(command.Json, "face reset is unavailable in this runner configuration.") : await _faceResetCommands.RunAsync(command, cancellationToken),
            "body weight normalize" or "body weight redistribute" => _weightTriangleCommands.Run(command),
            "npc materialize-template" => await _templateCommands.RunAsync(command, cancellationToken),
            "preset inspect" or "preset catalog" or "preset export" or "preset diff" or "preset resolve" or "appearance copy" => await _presetCommands.RunAsync(command, cancellationToken),
            "facegen analyze" or "facegen diagnose" or "facegen verify" => await _faceGenCommands.RunAsync(
                command with { Name = command.Name == "facegen analyze" ? "facegen diagnose" : command.Name }, cancellationToken),
            "facegen hair-regions analyze" or
            "facegen hair-regions propose" or
            "facegen hair-regions preview" or
            "facegen hair-regions apply" or
            "facegen hair-regions verify" =>
                await _faceGeomHairRegionsCommands.RunAsync(
                    command,
                    cancellationToken),
            "facegen build-geom" => _faceGeomBuildCommands is null
                ? WriteUsageError(command.Json, "facegen build-geom is unavailable in this runner configuration.")
                : await _faceGeomBuildCommands.RunAsync(command, cancellationToken),
            "facegen build-geom-nif" => _faceGeomBinaryBuildCommands is null
                ? WriteUsageError(command.Json, "facegen build-geom-nif is unavailable in this runner configuration.")
                : await _faceGeomBinaryBuildCommands.RunAsync(command, cancellationToken),
            "facegen build-tint" => _faceTintBuildCommands is null
                ? WriteUsageError(command.Json, "facegen build-tint is unavailable in this runner configuration.")
                : await _faceTintBuildCommands.RunAsync(command, cancellationToken),
            "facegen build-tint-bound" => _faceTintProviderBoundBuildCommands is null
                ? WriteUsageError(command.Json, "facegen build-tint-bound is unavailable in this runner configuration.")
                : await _faceTintProviderBoundBuildCommands.RunAsync(command, cancellationToken),
            "facegen build-tint-native" => _skyrimNativeFaceTintCommands is null
                ? WriteUsageError(command.Json, "facegen build-tint-native is unavailable in this runner configuration.")
                : await _skyrimNativeFaceTintCommands.RunAsync(command, cancellationToken),
            "facegen build-geom-bound" => _faceGeomProviderBoundBuildCommands is null
                ? WriteUsageError(command.Json, "facegen build-geom-bound is unavailable in this runner configuration.")
                : await _faceGeomProviderBoundBuildCommands.RunAsync(command, cancellationToken),
            "facegen build" => _faceGenCorrectionCommands is null
                ? WriteUsageError(command.Json, "facegen build is unavailable in this runner configuration.")
                : await _faceGenCorrectionCommands.RunAsync(command, cancellationToken),
            "facegen bake-all" => _faceGenBatchCommands is null
                ? WriteUsageError(command.Json, "facegen bake-all is unavailable in this runner configuration.")
                : await _faceGenBatchCommands.RunAsync(command, cancellationToken),
            "facegen bake-all-native" => _nativeFaceGenBatchCommands is null
                ? WriteUsageError(command.Json, "facegen bake-all-native is unavailable in this runner configuration.")
                : await _nativeFaceGenBatchCommands.RunAsync(command, cancellationToken),
            "facegen build-plugin" => _faceGenPluginBuildCommands is null
                ? WriteUsageError(command.Json, "facegen build-plugin is unavailable in this runner configuration.")
                : await _faceGenPluginBuildCommands.RunAsync(command, cancellationToken),
            "facegen resolve-providers" => _faceGenProviderResolutionCommands is null
                ? WriteUsageError(command.Json, "facegen resolve-providers is unavailable in this runner configuration.")
                : await _faceGenProviderResolutionCommands.RunAsync(command, cancellationToken),
            "facegen plan-pack" => _faceGenPackPlanCommands is null
                ? WriteUsageError(command.Json, "facegen plan-pack is unavailable in this runner configuration.")
                : await _faceGenPackPlanCommands.RunAsync(command, cancellationToken),
            "facegen pack" => _faceGenPackCommands is null
                ? WriteUsageError(command.Json, "facegen pack is unavailable in this runner configuration.")
                : await _faceGenPackCommands.RunAsync(command, cancellationToken),
            "facegen deploy" => _faceGenDeployCommands is null
                ? WriteUsageError(command.Json, "facegen deploy is unavailable in this runner configuration.")
                : await _faceGenDeployCommands.RunAsync(command, cancellationToken),
            "package build" or "package inspect" or "package verify" or "package archive" => _packageCommands is null
                ? WriteUsageError(command.Json, "package commands are unavailable in this runner configuration.")
                : await _packageCommands.RunAsync(command, cancellationToken),
            "preview render" or "render npc" => _previewSceneCommands is null
                ? WriteUsageError(command.Json, "preview render is unavailable in this runner configuration.")
                : await _previewSceneCommands.RunAsync(command with { Name = "preview render" }, cancellationToken),
            "preview npc" => _npcVisualPreviewCommands is null
                ? WriteUsageError(command.Json, "preview npc is unavailable in this runner configuration.")
                : await _npcVisualPreviewCommands.RunAsync(command, cancellationToken),
            "preview reroll" => _previewRerollCommands is null
                ? WriteUsageError(command.Json, "preview reroll is unavailable in this runner configuration.")
                : await _previewRerollCommands.RunAsync(command, cancellationToken),
            "preview export-nif" => _previewNifExportCommands is null
                ? WriteUsageError(command.Json, "preview export-nif is unavailable in this runner configuration.")
                : await _previewNifExportCommands.RunAsync(command, cancellationToken),
            "animation list" => _previewAnimationListCommands is null
                ? WriteUsageError(command.Json, "animation list is unavailable in this runner configuration.")
                : await _previewAnimationListCommands.RunAsync(command, cancellationToken),
            "animation tree" => _previewAnimationTreeCommands is null
                ? WriteUsageError(command.Json, "animation tree is unavailable in this runner configuration.")
                : await _previewAnimationTreeCommands.RunAsync(command, cancellationToken),
            "outfit list" => _outfitChoiceCommands is null
                ? WriteUsageError(command.Json, "outfit list is unavailable in this runner configuration.")
                : await _outfitChoiceCommands.RunAsync(command, cancellationToken),
            "outfit propose" or "outfit create" => _outfitProposalCommands is null
                ? WriteUsageError(command.Json, "outfit propose is unavailable in this runner configuration.")
                : await _outfitProposalCommands.RunAsync(command with { Name = "outfit propose" }, cancellationToken),
            "outfit write" => _outfitBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "outfit write is unavailable in this runner configuration.")
                : await _outfitBinaryWriteCommands.RunAsync(command, cancellationToken),
            "leveled-list write" => _leveledListBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "leveled-list write is unavailable in this runner configuration.")
                : await _leveledListBinaryWriteCommands.RunAsync(command, cancellationToken),
            "leveled-list propose" => _leveledListProposalCommands is null
                ? WriteUsageError(command.Json, "leveled-list propose is unavailable in this runner configuration.")
                : await _leveledListProposalCommands.RunAsync(command, cancellationToken),
            "leveled-list resolve" => _leveledListResolveCommands is null
                ? WriteUsageError(command.Json, "leveled-list resolve is unavailable in this runner configuration.")
                : await _leveledListResolveCommands.RunAsync(command, cancellationToken),
            "armor write" => _armorBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "armor write is unavailable in this runner configuration.")
                : await _armorBinaryWriteCommands.RunAsync(command, cancellationToken),
            "armor-addon write" => _armorAddonBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "armor-addon write is unavailable in this runner configuration.")
                : await _armorAddonBinaryWriteCommands.RunAsync(command, cancellationToken),
            "armor propose" => _armorProposalCommands is null
                ? WriteUsageError(command.Json, "armor propose is unavailable in this runner configuration.")
                : await _armorProposalCommands.RunAsync(command, cancellationToken),
            "armor damage-resist" => _armorDamageResistanceCommands is null
                ? WriteUsageError(command.Json, "armor damage-resist is unavailable in this runner configuration.")
                : await _armorDamageResistanceCommands.RunAsync(command, cancellationToken),
            "armor-addon propose" => _armorAddonProposalCommands is null
                ? WriteUsageError(command.Json, "armor-addon propose is unavailable in this runner configuration.")
                : await _armorAddonProposalCommands.RunAsync(command, cancellationToken),
            "material-swap propose" => _materialSwapProposalCommands is null
                ? WriteUsageError(command.Json, "material-swap propose is unavailable in this runner configuration.")
                : await _materialSwapProposalCommands.RunAsync(command, cancellationToken),
            "material-swap write" => _materialSwapBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "material-swap write is unavailable in this runner configuration.")
                : await _materialSwapBinaryWriteCommands.RunAsync(command, cancellationToken),
            "object-template write" => _objectTemplateBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "object-template write is unavailable in this runner configuration.")
                : await _objectTemplateBinaryWriteCommands.RunAsync(command, cancellationToken),
            "object-template propose" => _objectTemplateProposalCommands is null
                ? WriteUsageError(command.Json, "object-template propose is unavailable in this runner configuration.")
                : command.Options.ContainsKey("properties")
                    ? _objectTemplatePropertyProposalCommands is null
                        ? WriteUsageError(command.Json, "object-template properties are unavailable in this runner configuration.")
                        : await _objectTemplatePropertyProposalCommands.RunAsync(command, cancellationToken)
                    : await _objectTemplateProposalCommands.RunAsync(command, cancellationToken),
            "changes list" => _changeTrackingCommands is null
                ? WriteUsageError(command.Json, "changes list is unavailable in this runner configuration.")
                : await _changeTrackingCommands.RunAsync(command, cancellationToken),
            "changes update" => _changeActionCommands is null
                ? WriteUsageError(command.Json, "changes update is unavailable in this runner configuration.")
                : await _changeActionCommands.RunAsync(command, cancellationToken),
            "records propose" => _recordProposalCommands is null
                ? WriteUsageError(command.Json, "records propose is unavailable in this runner configuration.")
                : await _recordProposalCommands.RunAsync(command, cancellationToken),
            "plugin write" => _pluginWriteCommands is null
                ? WriteUsageError(command.Json, "plugin write is unavailable in this runner configuration.")
                : await _pluginWriteCommands.RunAsync(command, cancellationToken),
            "bodygen build" => await _bodyGenCommands.RunAsync(command, cancellationToken),
            "bodygen write" => await _bodyGenCommands.RunWriteAsync(command, cancellationToken),
            "runtime-script propose" => _runtimeScriptProposalCommands is null
                ? WriteUsageError(command.Json, "runtime-script propose is unavailable in this runner configuration.")
                : await _runtimeScriptProposalCommands.RunAsync(command, cancellationToken),
            "runtime-script build" => _runtimeScriptBuildCommands is null
                ? WriteUsageError(command.Json, "runtime-script build is unavailable in this runner configuration.")
                : await _runtimeScriptBuildCommands.RunAsync(command, cancellationToken),
            "runtime-script write" => _runtimeScriptBinaryWriteCommands is null
                ? WriteUsageError(command.Json, "runtime-script write is unavailable in this runner configuration.")
                : await _runtimeScriptBinaryWriteCommands.RunAsync(command, cancellationToken),
            "runtime-script package" => _runtimeScriptPackageCommands is null
                ? WriteUsageError(command.Json, "runtime-script package is unavailable in this runner configuration.")
                : await _runtimeScriptPackageCommands.RunAsync(command, cancellationToken),
            "runtime-script deploy" => _runtimeScriptDeployCommands is null
                ? WriteUsageError(command.Json, "runtime-script deploy is unavailable in this runner configuration.")
                : await _runtimeScriptDeployCommands.RunAsync(command, cancellationToken),
            "runtime-script inspect-vmad" => _runtimeScriptVmadInspectCommands is null
                ? WriteUsageError(command.Json, "runtime-script inspect-vmad is unavailable in this runner configuration.")
                : await _runtimeScriptVmadInspectCommands.RunAsync(command, cancellationToken),
            "pipeline preset-to-npc" => await _pipelineCommands.RunAsync(command, cancellationToken),
            "plugins resolve-load-order" => await _pluginCommands.RunResolveAsync(command, cancellationToken),
            "plugins validate" => await _pluginCommands.RunValidateAsync(command, cancellationToken),
            "load-order validate" => await _pluginCommands.RunValidateAsync(command, cancellationToken),
            "assets index" => await RunAssetIndexAsync(command, cancellationToken),
            "plugin verify" => command.Options.ContainsKey("proposal")
                ? _pluginVerifyCommands is null
                    ? WriteUsageError(command.Json, "proposal-bound plugin verify is unavailable in this runner configuration.")
                    : await _pluginVerifyCommands.RunAsync(command, cancellationToken)
                : await _mutationCommands.RunVerifyAsync(command, cancellationToken),
            "plugin audit" => _pluginSurfaceAuditCommands is null
                ? WriteUsageError(command.Json, "plugin audit is unavailable in this runner configuration.")
                : await _pluginSurfaceAuditCommands.RunAsync(command, cancellationToken),
            "plugin deploy" => _pluginDeployCommands is null
                ? WriteUsageError(command.Json, "plugin deploy is unavailable in this runner configuration.")
                : await _pluginDeployCommands.RunAsync(command, cancellationToken),
            "runtime smoke verify" => _runtimeSmokeVerifyCommands is null
                ? WriteUsageError(command.Json, "runtime smoke verify is unavailable in this runner configuration.")
                : await _runtimeSmokeVerifyCommands.RunAsync(command, cancellationToken),
            "runtime smoke verify-all" => _runtimeSmokeVerifyAllCommands is null
                ? WriteUsageError(command.Json, "runtime smoke verify-all is unavailable in this runner configuration.")
                : await _runtimeSmokeVerifyAllCommands.RunAsync(command, cancellationToken),
            "gui" => RunGui(command),
            _ => WriteUsageError(command.Json, $"Unknown command '{command.Name}'.")
        };
    }

}
