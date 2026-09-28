using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static int _passed;
    private const string Selector_TestSkyrimVoiceSample = "--test-skyrim-voice-sample";
    private const string Selector_TestSkyrimDialogueLip = "--test-skyrim-dialogue-lip";
    private const string Selector_TestSkyrimDialogue = "--test-skyrim-dialogue";
    private const string Selector_TestSkyrimMainWorkspaceRawNames = "--test-skyrim-main-workspace-raw-names";
    private const string Selector_TestNpcPreviewPhysicsHelpers = "--test-npc-preview-physics-helpers";
    private const string Selector_TestDialogueCoverageTemplate = "--test-dialogue-coverage-template";
    private const string Selector_TestQuestAliasPlacement = "--test-quest-alias-placement";
    private const string Selector_TestRacemenuPrivateHeadTexturePolicy = "--test-racemenu-private-head-texture-policy";
    private const string Selector_TestExternalFacegeomStructuredExclusion = "--test-external-facegeom-structured-exclusion";
    private const string Selector_TestNpcFinishCoreOutfitRace = "--test-npc-finish-core-outfit-race";
    private const string Selector_TestNpcFinishCoreCombatPolicy = "--test-npc-finish-core-combat-policy";
    private const string Selector_TestOutputOwnedHdptBinary = "--test-output-owned-hdpt-binary";
    private const string Selector_TestAgentWorkflowContracts = "--test-agent-workflow-contracts";
    private const string Selector_AppendLocalOperationJournalWorker = "--append-local-operation-journal-worker";
    private const string Selector_HangLocalOperationJournalWorker = "--hang-local-operation-journal-worker";
    private const string Selector_FailLocalOperationJournalWorker = "--fail-local-operation-journal-worker";
    private const string Selector_HangLocalOperationJournalDescendant = "--hang-local-operation-journal-descendant";
    private const string Selector_TestLocalOperationJournal = "--test-local-operation-journal";
    private const string Selector_TestAgentProtocolRegistry = "--test-agent-protocol-registry";
    private const string Selector_TestProtocolDiagnostics = "--test-protocol-diagnostics";
    private const string Selector_TestBoundedReadinessArchitecture = "--test-bounded-readiness-architecture";
    private const string Selector_EmitNpcPreviewProcessStreams = "--emit-npc-preview-process-streams";
    private const string Selector_TestNpcPreviewProcessEvidence = "--test-npc-preview-process-evidence";
    private const string Selector_TestNpcBuildPreflight = "--test-npc-build-preflight";
    private const string Selector_TestRacemenuRequestLoader = "--test-racemenu-request-loader";
    private const string Selector_TestFacegeomRecordAppearance = "--test-facegeom-record-appearance";
    private const string Selector_TestFacegeomPackedNormals = "--test-facegeom-packed-normals";
    private const string Selector_TestEmbeddedRendererScripts = "--test-embedded-renderer-scripts";
    private const string Selector_TestOptionalRuntimeRoot = "--test-optional-runtime-root";
    private const string Selector_TestPreviewArtifactFileWriter = "--test-preview-artifact-file-writer";
    private const string Selector_TestSelectedDependencyManifestSchema = "--test-selected-dependency-manifest-schema";
    private const string Selector_TestSkyrimPluginTopology = "--test-skyrim-plugin-topology";
    private const string Selector_TestInteriorPlacementFormatSpike = "--test-interior-placement-format-spike";
    private const string Selector_TestInteriorPlacementTransaction = "--test-interior-placement-transaction";
    private const string Selector_TestBsaStreaming = "--test-bsa-streaming";
    private const string Selector_TestBsaProduction = "--test-bsa-production";
    private const string Selector_TestRubyNarrowAdmission = "--test-ruby-narrow-admission";
    private const string Selector_TestRubyCveo = "--test-ruby-cveo";
    private const string Selector_TestFollowerFinishTransaction = "--test-follower-finish-transaction";
    private const string Selector_TestFollowerFinishPrivateBody = "--test-follower-finish-private-body";
    private const string Selector_TestNpcFinishCoreSandbox = "--test-npc-finish-core-sandbox";
    private const string Selector_TestNpcFinishCoreSandboxAuthentic = "--test-npc-finish-core-sandbox-authentic";
    private const string Selector_TestNpcFinishCoreContracts = "--test-npc-finish-core-contracts";
    private const string Selector_TestNpcFinishCoreAuthority = "--test-npc-finish-core-authority";
    private const string Selector_TestPreview251FinishAuthority = "--test-preview251-finish-authority";
    private const string Selector_TestPreview251FinishIntegration = "--test-preview251-finish-integration";
    private const string Selector_TestNpcFinishCoreProposal = "--test-npc-finish-core-proposal";
    private const string Selector_TestNpcFinishCoreBinary = "--test-npc-finish-core-binary";
    private const string Selector_TestNpcFinishCoreTransaction = "--test-npc-finish-core-transaction";
    private const string Selector_TestFollowerFinishPlacement = "--test-follower-finish-placement";
    private const string Selector_TestFollowerFinishPairContracts = "--test-follower-finish-pair-contracts";
    private const string Selector_TestFollowerFinishPairArchiveLayout = "--test-follower-finish-pair-archive-layout";
    private const string Selector_TestFacegeomHairRegions = "--test-facegeom-hair-regions";
    private const string Selector_TestOwnedDirectoryPromotionBoundary = "--test-owned-directory-promotion-boundary";
    private const string Selector_TestHairRegionPreviewSource = "--test-hair-region-preview-source";
    private const string Selector_TestHairRegionSelectedSource = "--test-hair-region-selected-source";
    private const string Selector_EmitHairRegionReviewedIntake = "--emit-hair-region-reviewed-intake";
    private const string Selector_EmitAuthenticHairRegionRequest = "--emit-authentic-hair-region-request";
    private const string Selector_TestNpcVisualSource = "--test-npc-visual-source";
    private const string Selector_TestNpcVisualSourceSophia = "--test-npc-visual-source-sophia";
    private const string Selector_TestNpcVisualSourceBrigitteV01 = "--test-npc-visual-source-brigitte-v01";
    private const string Selector_TestNpcVisualSourceBrigittePaired = "--test-npc-visual-source-brigitte-paired";
    private const string Selector_TestNpcVisualSourceBrigitteRepair = "--test-npc-visual-source-brigitte-repair";
    private const string Selector_TestNpcVisualRenderSophia = "--test-npc-visual-render-sophia";
    private const string Selector_TestNpcVisualRenderBrigitteV01 = "--test-npc-visual-render-brigitte-v01";
    private const string Selector_TestNpcVisualRenderBrigittePaired = "--test-npc-visual-render-brigitte-paired";
    private const string Selector_TestNpcVisualValidate = "--test-npc-visual-validate";
    private const string Selector_TestNpcVisualComparison = "--test-npc-visual-comparison";
    private const string Selector_TestFacegeomHairRegionsRenderer = "--test-facegeom-hair-regions-renderer";
    private const string Selector_HashPyniflyProfile = "--hash-pynifly-profile";
    private const string Selector_TestNpcVisualCompareAuthentic = "--test-npc-visual-compare-authentic";
    private const string Selector_EmitOutfitProductionFixture = "--emit-outfit-production-fixture";
    private const string Selector_EmitLeveledListProductionFixture = "--emit-leveled-list-production-fixture";
    private const string Selector_EmitArmorProductionFixture = "--emit-armor-production-fixture";
    private const string Selector_EmitArmorAddonReferenceProductionFixture = "--emit-armor-addon-reference-production-fixture";
    private const string Selector_EmitSavePackageProductionFixture = "--emit-save-package-production-fixture";
    private const string Selector_ReadSkyrimMainWorkspacePlugin = "--read-skyrim-main-workspace-plugin";
    private const string Selector_InspectNpcVisualMaterials = "--inspect-npc-visual-materials";
    private const string Selector_InspectSophiaSkinGraph = "--inspect-sophia-skin-graph";
    private const string Selector_TestActorAssembly = "--test-actor-assembly";


    public static async Task<int> Main(string[] args)
    {
        if (StandaloneSelectorInventory.TryList(
                args,
                typeof(Program),
                Preview254ExternalSmpArchitectureTestRegistry.Selectors))
            return 0;
        if (args is [Selector_TestSkyrimVoiceSample])
        {
            TestSkyrimVoiceSampleCodec();
            await XttsClientTests.RunAsync();
            Console.WriteLine("PASS Skyrim voice sample codec and XTTS workflow");
            return 0;
        }
        if (SkyrimDialogueLipSyncTests.RunTool(args) is { } toolExit) return toolExit;
        if (args is [Selector_TestSkyrimDialogueLip])
        {
            await SkyrimDialogueLipSyncTests.RunAsync();
            return 0;
        }
        if (args is [Selector_TestSkyrimDialogue])
        {
            await SkyrimDialogueWriterTests.RunAsync();
            return 0;
        }
        if (args is [Selector_TestSkyrimMainWorkspaceRawNames])
        {
            await TestSkyrimMainWorkspaceRawNames();
            Console.WriteLine(
                "PASS Skyrim main workspace raw names and repeated leveled entries");
            return 0;
        }
        if (args is [Selector_TestNpcPreviewPhysicsHelpers])
        {
            await NpcVisualPreviewProcessEvidenceTests.RunPhysicsHelpersAsync();
            return 0;
        }
        if (args is [Selector_TestDialogueCoverageTemplate]) { SkyrimDialogueCoverageTemplateTests.Run(); return 0; }
        if (args is [Selector_TestQuestAliasPlacement])
        {
            await SkyrimQuestAliasPlacementTests.RunAsync();
            Console.WriteLine("PASS quest-alias-placement");
            return 0;
        }
        if (args is [Selector_TestRacemenuPrivateHeadTexturePolicy])
        {
            await RaceMenuStandaloneSchema8Tests.RunPrivateTexturePolicyAsync(CancellationToken.None);
            Console.WriteLine("PASS RaceMenu private head texture policy");
            return 0;
        }
        if (args is [Selector_TestExternalFacegeomStructuredExclusion])
        {
            await ExternalHeadPartFaceGeomExclusionTests.RunAsync(CancellationToken.None);
            Console.WriteLine("PASS external FaceGeom structured exclusion");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreOutfitRace])
        {
            await TestFinishCoreOutfitRace();
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreCombatPolicy])
        {
            await TestFinishCoreCombatAndPerkPolicy();
            Console.WriteLine("PASS Finish Core combat/perk policy schemas");
            return 0;
        }
        if (args is [Selector_TestOutputOwnedHdptBinary])
        {
            var failures = new List<string>();
            try { HeadPartBinaryTests.Run(); } catch (Exception exception) { failures.Add(exception.Message); }
            try { HeadPartBinaryTests.RunOwnership(); } catch (Exception exception) { failures.Add(exception.Message); }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(" | ", failures));
            return 0;
        }
        if (args is [Selector_TestAgentWorkflowContracts])
        {
            AgentWorkflowContractTests.Run();
            AgentReviewReceiptTests.Run();
            await FinishLifecycleArtifactPersistenceTests.RunAsync();
            Console.WriteLine("PASS immutable agent workflow contracts");
            return 0;
        }

        if (args is [Selector_AppendLocalOperationJournalWorker, var root])
            return await LocalOperationJournalTests.RunWorkerAsync(root);
        if (args is [Selector_HangLocalOperationJournalWorker, var workerMarker, var workerGate])
            return await LocalOperationJournalTests.RunHangingWorkerAsync(workerMarker, workerGate);
        if (args is [Selector_FailLocalOperationJournalWorker, var failureMarker, var failureGate])
            return await LocalOperationJournalTests.RunFailingWorkerAsync(failureMarker, failureGate);
        if (args is [Selector_HangLocalOperationJournalDescendant, var descendantMarker])
            return await LocalOperationJournalTests.RunHangingDescendantAsync(descendantMarker);

        if (args is [Selector_TestLocalOperationJournal])
        {
            await LocalOperationJournalTests.RunAsync();
            Console.WriteLine("PASS bounded local operation journal storage");
            return 0;
        }
        if (args is [Selector_TestAgentProtocolRegistry])
        {
            AgentProtocolContractTests.RunRegistry();
            Console.WriteLine("PASS agent protocol registry");
            return 0;
        }
        if (args is [Selector_TestProtocolDiagnostics])
        {
            ProtocolDiagnosticTests.Run();
            Console.WriteLine(
                "PASS typed protocol diagnostic and recovery semantics");
            return 0;
        }
        if (args is [Selector_TestBoundedReadinessArchitecture])
        {
            await BoundedReadinessArchitectureTests.RunAsync();
            return 0;
        }
        if (args is [Selector_EmitNpcPreviewProcessStreams])
        {
            Console.Out.Write(new string('O', 131_072));
            Console.Error.Write(new string('E', 131_072));
            return 0;
        }
        if (args is [Selector_TestNpcPreviewProcessEvidence])
        {
            await NpcVisualPreviewProcessEvidenceTests.RunAsync();
            Console.WriteLine(
                "PASS NPC visual preview process evidence");
            return 0;
        }
        if (args is [Selector_TestNpcBuildPreflight])
        {
            await NpcBuildPreflightTests.RunAsync();
            return 0;
        }
        if (args is [Selector_TestRacemenuRequestLoader])
        {
            await TestRaceMenuRequestLoaderCompatibility();
            Console.WriteLine(
                "PASS RaceMenu request provider compatibility and migration");
            return 0;
        }
        if (args is [Selector_TestFacegeomRecordAppearance])
        {
            await TestFaceGeomRecordAppearance();
            Console.WriteLine("PASS FaceGeom record appearance authority");
            return 0;
        }
        if (args is [Selector_TestFacegeomPackedNormals])
        {
            await TestSseFaceGeomPackedNormals();
            Console.WriteLine("PASS shared FaceGeom packed-normal extraction and carrier verification");
            return 0;
        }
        if (args is [Selector_TestEmbeddedRendererScripts])
        {
            TestEmbeddedBlenderScriptBundle();
            Console.WriteLine("PASS embedded Blender script bundle");
            return 0;
        }
        if (args is [Selector_TestOptionalRuntimeRoot])
        {
            await TestOptionalRuntimeRootAdmission();
            await TestKOnlyPolicyDecisionObservability();
            await TestWorkspacePolicyShadowObservability();
            Console.WriteLine("PASS optional runtime and K-only policy admission");
            return 0;
        }
        if (args is [Selector_TestPreviewArtifactFileWriter])
        {
            await PreviewArtifactFileWriterTests.TestPreviewArtifactFileWriterAsync();
            Console.WriteLine("PASS preview artifact file writer");
            return 0;
        }
        if (args is [Selector_TestSelectedDependencyManifestSchema])
        {
            await TestSelectedDependencyManifestSchema();
            Console.WriteLine("PASS selected dependency manifest schema");
            return 0;
        }
        if (args is [Selector_TestSkyrimPluginTopology])
        {
            await TestBethesdaSkyrimRawTopologyPreflight();
            Console.WriteLine("PASS Skyrim plugin topology preflight");
            return 0;
        }

        if (args is [Selector_TestInteriorPlacementFormatSpike])
        {
            await TestSkyrimInteriorPlacementFormatSpike();
            Console.WriteLine("PASS Skyrim interior placement format spike");
            return 0;
        }

        if (args is [Selector_TestInteriorPlacementTransaction])
        {
            await TestSkyrimInteriorPlacementTransaction();
            Console.WriteLine("PASS Skyrim interior placement transaction");
            return 0;
        }

        if (args is [Selector_TestBsaStreaming])
        {
            await TestBethesdaArchiveStreamTransfer();
            Console.WriteLine("PASS bounded BSA stream transfer");
            return 0;
        }
        if (args is [Selector_TestBsaProduction])
        {
            await TestSkyrimBsaProduction();
            Console.WriteLine("PASS real Briar BSA production");
            return 0;
        }

        if (args is [Selector_TestRubyNarrowAdmission])
        {
            await TestRubyCveoSentinelAdmission();
            await TestRubyCveoPostMorphSentinelPolicy();
            await TestRubyExternalDintProviderReader();
            await TestRubyExternalDintSidecarAuthority();
            await TestRubyAssetAuthorityPlannerBoundaries();
            await TestRubyDintClosureAndSubstitutionBoundary();
            Console.WriteLine("PASS Ruby narrow CVEO/Dint admission");
            return 0;
        }
        if (args is [Selector_TestRubyCveo])
        {
            await TestRubyCveoSentinelAdmission();
            Console.WriteLine("PASS Ruby CVEO sentinel admission");
            return 0;
        }

        if (args is [Selector_TestFollowerFinishTransaction])
        {
            await TestSkyrimFollowerFinishTransaction();
            Console.WriteLine("PASS Skyrim follower finish transaction");
            return 0;
        }
        if (args is [Selector_TestFollowerFinishPrivateBody])
        {
            await TestSkyrimFollowerFinishPrivateBody();
            Console.WriteLine("PASS Skyrim follower finish private body");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreSandbox])
        {
            await TestSkyrimNpcFinishCoreSandbox();
            Console.WriteLine("PASS Skyrim NPC Finish Core sandbox");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreSandboxAuthentic])
        {
            await TestSkyrimNpcFinishCoreSandboxAuthentic();
            Console.WriteLine("PASS authentic Skyrim NPC Finish Core sandbox");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreContracts])
        {
            await TestSkyrimNpcFinishCoreContracts();
            Console.WriteLine("PASS Skyrim NPC Finish Core contracts");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreAuthority])
        {
            await TestSkyrimNpcFinishCoreAuthority();
            Console.WriteLine("PASS Skyrim NPC Finish Core authority admission");
            return 0;
        }
        if (args is [Selector_TestPreview251FinishAuthority])
        {
            await TestPreview251FinishAuthority();
            Console.WriteLine("PASS Preview.251 Finish Core v2 authority planning");
            return 0;
        }
        if (args is [Selector_TestPreview251FinishIntegration])
        {
            await TestPreview251FinishCoreIntegration();
            Console.WriteLine("PASS Preview.251 Finish Core shared authority integration");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreProposal])
        {
            await TestSkyrimNpcFinishCoreProposal();
            Console.WriteLine("PASS Skyrim NPC Finish Core proposal derivation");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreBinary])
        {
            await TestSkyrimNpcFinishCoreBinary();
            Console.WriteLine("PASS Skyrim NPC Finish Core binary writer/verifier");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreTransaction])
        {
            await TestSkyrimNpcFinishCoreTransaction();
            Console.WriteLine("PASS Skyrim NPC Finish Core transaction/package");
            return 0;
        }
        if (args is [Selector_TestFollowerFinishPlacement])
        {
            await TestSkyrimFollowerFinishPlacement();
            Console.WriteLine("PASS Skyrim follower finish placement");
            return 0;
        }
        if (args is [Selector_TestFollowerFinishPairContracts])
        {
            await TestSkyrimFollowerFinishPairContracts();
            Console.WriteLine(
                "PASS Skyrim paired follower finish contracts");
            return 0;
        }
        if (args is [Selector_TestFollowerFinishPairArchiveLayout])
        {
            await TestSkyrimFollowerFinishPairArchiveLayout();
            Console.WriteLine(
                "PASS Skyrim paired follower archive is directly installable");
            return 0;
        }
        if (args is [Selector_TestFacegeomHairRegions])
        {
            await TestFaceGeomHairRegionsTransaction();
            Console.WriteLine(
                "PASS FaceGeom hair-region transaction");
            return 0;
        }
        if (args is [Selector_TestOwnedDirectoryPromotionBoundary])
        {
            FaceGeomHairRegionsPinnedDirectoryPromotionTests.Run();
            Console.WriteLine(
                "PASS owned-directory promotion boundary");
            return 0;
        }
        if (args is [Selector_TestHairRegionPreviewSource])
        {
            await TestFaceGeomHairRegionsPreviewSource();
            Console.WriteLine(
                "PASS FaceGeom hair-region preview source");
            return 0;
        }
        if (args is [Selector_TestHairRegionSelectedSource])
        {
            await TestFaceGeomHairRegionsSelectedSourceResolver();
            Console.WriteLine(
                "PASS FaceGeom hair-region selected-source resolver");
            return 0;
        }
        if (args is
            [
                Selector_EmitHairRegionReviewedIntake,
                var dataRoot,
                var loadOrder,
                var outputRoot,
                var intakeDocument
            ])
        {
            await EmitFaceGeomHairRegionsReviewedIntake(
                dataRoot,
                loadOrder,
                outputRoot,
                intakeDocument);
            return 0;
        }
        if (args is
            [
                Selector_EmitAuthenticHairRegionRequest,
                var analysis,
                var analysisSha256,
                var request,
                var candidateOutput,
                var manifest
            ])
        {
            await EmitAuthenticFaceGeomHairRegionsRequest(
                analysis,
                analysisSha256,
                request,
                candidateOutput,
                manifest);
            return 0;
        }
        if (args is [Selector_TestNpcVisualSource])
        {
            await TestBethesdaNpcVisualSourceComposer();
            Console.WriteLine(
                "PASS Bethesda NPC visual source graph");
            return 0;
        }
        if (args is [Selector_TestNpcVisualSourceSophia])
        {
            await TestAuthenticSophiaNpcVisualSourceComposer();
            Console.WriteLine(
                "PASS authentic Sophia NPC visual source graph");
            return 0;
        }
        if (args is [Selector_TestNpcVisualSourceBrigitteV01])
        {
            await TestAuthenticBrigitteNpcVisualSourceComposer(
                paired: false,
                repairedCandidate: false);
            Console.WriteLine(
                "PASS authentic Brigitte v0.1 NPC visual source graph");
            return 0;
        }
        if (args is [Selector_TestNpcVisualSourceBrigittePaired])
        {
            await TestAuthenticBrigitteNpcVisualSourceComposer(
                paired: true,
                repairedCandidate: false);
            Console.WriteLine(
                "PASS authentic observed paired Brigitte NPC visual source graph");
            return 0;
        }
        if (args is [Selector_TestNpcVisualSourceBrigitteRepair])
        {
            await TestAuthenticBrigitteNpcVisualSourceComposer(
                paired: true,
                repairedCandidate: true);
            Console.WriteLine(
                "PASS unconfirmed Brigitte repair candidate NPC visual source graph");
            return 0;
        }
        if (args is [Selector_TestNpcVisualRenderSophia])
        {
            string output =
                await TestAuthenticSophiaNpcVisualRenderer();
            Console.WriteLine(
                "PASS authentic Sophia NPC visual renderer");
            Console.WriteLine($"OUTPUT {output}");
            return 0;
        }
        if (args is [Selector_TestNpcVisualRenderBrigitteV01])
        {
            string output =
                await TestAuthenticBrigitteNpcVisualRenderer(
                    paired: false);
            Console.WriteLine(
                "PASS authentic Brigitte v0.1 NPC visual renderer");
            Console.WriteLine($"OUTPUT {output}");
            return 0;
        }
        if (args is [Selector_TestNpcVisualRenderBrigittePaired])
        {
            string output =
                await TestAuthenticBrigitteNpcVisualRenderer(
                    paired: true);
            Console.WriteLine(
                "PASS observed paired Brigitte NPC visual renderer");
            Console.WriteLine($"OUTPUT {output}");
            return 0;
        }
        if (args is [Selector_TestNpcVisualValidate, var image])
        {
            await TestNpcVisualPreviewVisualValidator(image);
            Console.WriteLine(
                "PASS NPC visual face/anchor validator");
            return 0;
        }
        if (args is [Selector_TestNpcVisualComparison])
        {
            await TestNpcVisualComparisonService();
            Console.WriteLine(
                "PASS NPC visual comparison evidence");
            return 0;
        }
        if (args is [Selector_TestFacegeomHairRegionsRenderer])
        {
            await TestFaceGeomHairRegionsRenderer();
            Console.WriteLine(
                "PASS FaceGeom hair-regions renderer");
            return 0;
        }
        if (args is
            [
                Selector_HashPyniflyProfile,
                var pyniflyProfile
            ])
        {
            Console.WriteLine(
                FingerprintProfile(
                    pyniflyProfile).Value);
            return 0;
        }
        if (args is
            [
                Selector_TestNpcVisualCompareAuthentic,
                var preview,
                var comparison,
                var kind
            ])
        {
            string output =
                await TestAuthenticNpcVisualComparison(
                    preview,
                    comparison,
                    kind);
            Console.WriteLine(
                "PASS authentic NPC visual comparison evidence");
            Console.WriteLine($"OUTPUT {output}");
            return 0;
        }
        if (args is [Selector_EmitOutfitProductionFixture, var destination])
            return EmitOutfitProductionFixture(destination);
        if (args is [Selector_EmitLeveledListProductionFixture, var leveledDestination])
            return EmitLeveledListProductionFixture(leveledDestination);
        if (args is [Selector_EmitArmorProductionFixture, var armorDestination])
            return EmitArmorProductionFixture(
                armorDestination, includeArmorAddonProvider: false);
        if (args is [Selector_EmitArmorAddonReferenceProductionFixture,
                var armorAddonDestination])
            return EmitArmorProductionFixture(
                armorAddonDestination, includeArmorAddonProvider: true);
        if (args is [Selector_EmitSavePackageProductionFixture,
                var savePackageDestination])
            return await EmitSavePackageProductionFixtureAsync(
                savePackageDestination);
        if (args is [Selector_ReadSkyrimMainWorkspacePlugin,
                var mainWorkspacePlugin])
            return ReadSkyrimMainWorkspacePlugin(mainWorkspacePlugin);
        if (args is [Selector_InspectNpcVisualMaterials,
                var visualNif])
        {
            foreach (SseNifVisualMaterialDescriptor material in
                     SseNifVisualMaterialReader.Read(
                         File.ReadAllBytes(visualNif)))
                Console.WriteLine(
                    $"{material.Shape}|{material.ShaderType}|" +
                    $"{material.ShaderFlags1:X8}|" +
                    $"{material.ShaderFlags2:X8}|" +
                    $"{material.TintHex ?? "-"}|" +
                    $"{material.Textures.Length}");
            return 0;
        }
        if (args is [Selector_InspectSophiaSkinGraph])
        {
            InspectAuthenticSophiaSkinGraph();
            return 0;
        }
        int? preview254ExternalSmpResult =
            await Preview254ExternalSmpArchitectureTestRegistry.TryRunAsync(
                args, CancellationToken.None);
        if (preview254ExternalSmpResult is int result)
            return result;
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Actor Assembly contract loader enforces closed hash-bound schema-1 admission",
                TestActorAssemblyContractLoader),
            ("Actor Assembly evidence loader admits closed owner documents",
                TestActorAssemblyEvidenceLoader),
            ("Actor Assembly TRI inspector preserves PIRT boundaries",
                TestActorAssemblyTriInspection),
            ("Actor Assembly identity reader separates base NPC and placed ACHR",
                TestActorAssemblyIdentityReader),
            ("Actor Assembly provenance evaluator blocks double morph ownership",
                TestActorAssemblyProvenance),
            ("Actor Assembly preflight service pins check order and static flags",
                TestActorAssemblyPreflightService),
            ("Selected headpart NIF reader exposes exact rest geometry", TestSseSelectedHeadpartNifGeometryReader),
            ("FaceGeom carrier verifies 16-byte and 24-byte packed normals directly",
                TestSseFaceGeomPackedNormals),
            ("Authentic CVEO packed-normal sentinels are narrowly admitted",
                TestRubyCveoSentinelAdmission),
            ("CVEO sentinel triangles pass at rest and refuse post-morph opening",
                TestRubyCveoPostMorphSentinelPolicy),
            ("Dint external provider metadata remains dependency-only and strict",
                TestRubyExternalDintProviderReader),
            ("Dint provider XML sidecar authority is exact and separate",
                TestRubyExternalDintSidecarAuthority),
            ("Ruby external asset planner preserves binary and optional boundaries",
                TestRubyAssetAuthorityPlannerBoundaries),
            ("Dint closure is exact and excludes provider substitutions",
                TestRubyDintClosureAndSubstitutionBoundary),
            ("NPC visual comparisons align and remain human-unreviewed", TestNpcVisualComparisonService),
            ("RaceMenu CharGen XYZ merge preserves complete carrier", TestRaceMenuCharGenFaceGeomMerge),
            ("RaceMenu exported complete carrier preserves Chel UBE closure", TestRaceMenuExportedCompleteCarrier),
            ("RaceMenu execution request loader is shared, migrated, and hash-bound", TestRaceMenuRequestLoaderCompatibility),
            ("NPC build preflight is read-only, reviewed, and stale-safe", NpcBuildPreflightTests.RunAsync),
            ("Game edition wire names are stable", TestGameEdition),
            ("FormID parsing is explicit and lossless", TestFormId),
            ("TES record signatures allow the NPC underscore", TestRecordSignature),
            ("Asset paths reject traversal", TestAssetPath),
            ("Plugin names reject path injection", TestPluginName),
            ("Workspace paths require explicit roots", TestWorkspacePath),
            ("Command catalog is unique and exposes the pipeline", TestCommandCatalog),
            ("FaceGen bake discovery returns ordered winning NPC identities", TestFaceGenBakeTargetDiscovery),
            ("FaceGen bake-all preserves ordered outcomes and cancellation boundaries", TestFaceGenBakeAll),
            ("Skyrim FaceGen sidecars hydrate typed load-order overlays", TestSkyrimFaceGenSidecarOverlays),
            ("Reviewed Skyrim intake binds the selected copied-Data closure", TestReviewedGameIntake),
            ("RaceMenu catalog authority shares one batch asset snapshot", TestRaceMenuCatalogAuthority),
            ("Skyrim head-part choices enforce typed picker compatibility", TestSkyrimHeadPartChoiceCatalog),
            ("Skyrim mesh picker preserves provider-bound prefix-free selection", TestSkyrimMeshPickerCatalog),
            ("Skyrim mesh preview binds exact content and deterministic K-only cache", TestSkyrimMeshPickerPreview),
            ("RaceMenu paint choices parse winning compiled scripts", TestSkyrimRaceMenuPaintChoiceCatalog),
            ("Skyrim face editor document preserves exact section semantics", TestSkyrimFaceEditorDocument),
            ("Skyrim face editor projects only writer-representable deltas", TestSkyrimFaceEditProjection),
            ("Skyrim face edit source reader preserves exact optional writer fields", TestSkyrimFaceEditSourceReader),
            ("Skyrim body editor document preserves lossless section semantics", TestSkyrimBodyEditorDocument),
            ("Skyrim body editor projects only writer-representable deltas", TestSkyrimBodyEditProjection),
            ("Skyrim body edit source loader preserves hash-bound NAM7 authority", TestSkyrimBodyEditSourceLoader),
            ("Skyrim selective appearance paste preserves unchecked target state", TestSkyrimSelectiveAppearancePaste),
            ("Skyrim selective appearance paste writes plugin-only categories through a source-owned override", TestSkyrimSelectiveAppearancePluginPatch),
            ("Skyrim selective appearance paste writes and reopens both exact carriers", TestSkyrimSelectiveAppearanceDualCarrierTransaction),
            ("Skyrim outfit editor preserves qualified order and exact transaction boundaries", TestSkyrimOutfitEditor),
            ("Skyrim outfit production loads reviewed authority and reopens new plus override OTFT writes", TestSkyrimOutfitProductionWorkflow),
            ("Skyrim leveled-list production writes and reopens one new self-owned LVLI", TestSkyrimLeveledListProductionWorkflow),
            ("Skyrim leveled-list editor preserves one typed transactional header", TestSkyrimLeveledListEditor),
            ("Skyrim leveled-entry editor preserves ordered rows and legal repeated references", TestSkyrimLeveledEntryEditor),
            ("Skyrim Armor editor preserves complete documents and binary identity", TestSkyrimArmorEditor),
            ("Skyrim Armor production loads reviewed source and reopens one exact new ARMO", TestSkyrimArmorProductionWorkflow),
            ("Skyrim Armor-addon production reviews, writes, reopens, and rejects stale bindings", TestSkyrimArmorAddonProductionWorkflow),
            ("Skyrim Armor-addon reference editor enforces compatibility and nested rollback", TestSkyrimArmorAddonReferenceEditor),
            ("Skyrim Armor-addon editor preserves complete documents, clears, and nested identity", TestSkyrimArmorAddonEditor),
            ("Skyrim CharGen options preserve derivation, resets, ordering, and Cancel", TestSkyrimCharGenOptionsEditor),
            ("Skyrim CharGen options write canonically and gate native FaceTint consumption", TestSkyrimCharGenOptionsProduction),
            ("Skyrim save-package review binds and promotes one exact verified inventory", TestSkyrimSavePackage),
            ("Skyrim follower finish contracts and strict loaders fail closed",
                TestSkyrimFollowerFinishContracts),
            ("Skyrim follower finish source and proposal analysis is hash-bound and write-free",
                TestSkyrimFollowerFinishProposalAnalysis),
            ("Skyrim follower finish core writes and verifies the closed binary delta",
                TestSkyrimFollowerFinishCoreBinary),
            ("Skyrim follower finish placement writes and rejects nonminimal world surfaces",
                TestSkyrimFollowerFinishPlacement),
            ("Skyrim follower finish packaging is atomic and independently verified",
                TestSkyrimFollowerFinishTransaction),
            ("Skyrim paired follower finish contracts keep one closed schema-2 surface",
                TestSkyrimFollowerFinishPairContracts),
            ("Skyrim paired follower archive projects Data contents to ZIP root",
                TestSkyrimFollowerFinishPairArchiveLayout),
            ("FaceGeom hair regions analyze, propose, apply, and independently verify exact fields",
                TestFaceGeomHairRegionsTransaction),
            ("FaceGeom hair-region rendering binds one structural PyNifly import",
                TestFaceGeomHairRegionsRenderer),
            ("Skyrim main workspace preserves exact selection, draft, route, and preview semantics",
                TestSkyrimMainWorkspaceRules),
            ("Skyrim main workspace resolves winning NPC and LVLN records from exact binaries",
                TestSkyrimMainWorkspaceCatalog),
            ("Skyrim main workspace persists exact state and renders hash-bound copied assets",
                TestSkyrimMainWorkspacePersistenceAndPreview),
            ("NPC visual preview composes a truthful schema-2 bundle and refuses UBE",
                TestNpcVisualPreviewComposer),
            ("NPC visual skin TXST routing preserves non-skin outfit materials",
                TestNpcVisualSkinTextureSetRouting),
            ("Bethesda NPC visual source resolves the real record and asset graph",
                TestBethesdaNpcVisualSourceComposer),
            ("Skyrim lighting editor preserves bounded rigs and atomic K-only settings", TestSkyrimLightingEditor),
            ("Skyrim outfit writer round-trips new and cross-plugin override identities", TestSkyrimOutfitBinaryRoundTrip),
            ("Proposal writers report locked temporary cleanup failures", TestProposalCleanupFailureDiagnostics),
            ("SSE FaceGeom carrier assembly combines real headpart NIF closures", TestSseFaceGeomCarrierAssembler),
            ("native Skyrim FaceGeom resolves loose headparts and writes one reopened carrier", TestSkyrimNativeFaceGeomBuild),
            ("optional native runtimes must remain under the configured Actorwright workspace", TestOptionalRuntimeRootAdmission),
            ("K-only policy observations are opt-in and preserve decisions", TestKOnlyPolicyDecisionObservability),
            ("workspace policy shadow comparisons preserve both K-only evaluations", TestWorkspacePolicyShadowObservability),
            ("K-only policy accepts in-root output", TestAllowedRoot),
            ("K-only policy refuses protected output", TestProtectedRoot),
            ("K-only policy refuses ambiguous path forms", TestAmbiguousPath),
            ("Qualified carrier refuses alternate data streams", TestQualifiedCarrierAlternateDataStream),
            ("Qualified carrier requires complete owned topology", TestQualifiedCarrierTopology),
            ("Qualified carrier admits the proven seven-shape workspace fixture", TestProvenQualifiedCarrierFixture),
            ("Qualified carrier admits corrected Manager assembly and rejects historical slot-0 FaceTint", TestManagerAssembledQualifiedCarrierFixture),
            ("Qualified carrier evidence survives source cleanup and relocation", TestQualifiedCarrierDurableEvidence),
            ("Qualified carrier preserves the admitted alpha topology", TestQualifiedCarrierAlphaTopology),
            ("Qualified carrier rewrite verification guards block-count drift", TestQualifiedCarrierRewriteBlockCountDrift),
            ("Skyrim raw topology preflight diagnoses orphan interior cell children",
                TestBethesdaSkyrimRawTopologyPreflight),
            ("Skyrim interior placement three-owner format spike",
                TestSkyrimInteriorPlacementFormatSpike),
            ("Preflight honors cancellation", TestCancellation)
        };

        if (args is [Selector_TestActorAssembly])
            tests = tests.Where(test => test.Name.Contains(
                "Actor Assembly", StringComparison.Ordinal)).ToArray();

        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                _passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"RESULT PASS {_passed}/{tests.Length}");
        return 0;
    }

    private static Task TestGameEdition()
    {
        Assert(GameEdition.Fallout4.ToWireName() == "fallout4", "FO4 wire name changed.");
        Assert(GameEditionExtensions.TryParseWireName("SkyrimSE", out var edition) && edition == GameEdition.SkyrimSpecialEdition,
            "SSE wire parsing failed.");
        return Task.CompletedTask;
    }

    private static Task TestFormId()
    {
        Assert(FormId.TryParse("0x0000D63", out var formId) && formId.Value == 0xD63, "FormID parse failed.");
        Assert(formId.ToString() == "0x00000D63", "FormID formatting is unstable.");
        return Task.CompletedTask;
    }

    private static Task TestRecordSignature()
    {
        Assert(new RecordSignature("NPC_").Value == "NPC_", "NPC_ is a valid TES record signature.");
        AssertThrows<ArgumentException>(() => _ = new RecordSignature("npc_"));
        return Task.CompletedTask;
    }

    private static Task TestAssetPath()
    {
        var validPath = new AssetPath("meshes/actors/head.nif");
        Assert(validPath.Value == "meshes/actors/head.nif", "Asset path normalization changed.");
        AssertThrows<ArgumentException>(() =>
        {
            var invalid = new AssetPath("../outside.nif");
            Assert(invalid.Value.Length > 0, "Invalid asset path unexpectedly constructed.");
        });
        return Task.CompletedTask;
    }

    private static Task TestPluginName()
    {
        var validName = new PluginName("NpcManager.esp");
        Assert(validName.Value == "NpcManager.esp", "Plugin name value changed.");
        AssertThrows<ArgumentException>(() =>
        {
            var invalid = new PluginName("..\\evil.esp");
            Assert(invalid.Value.Length > 0, "Invalid plugin name unexpectedly constructed.");
        });
        return Task.CompletedTask;
    }

    private static Task TestCommandCatalog()
    {
        var names = CommandCatalog.All.Select(command => command.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(names.Count == CommandCatalog.All.Length, "Command names are duplicated.");
        Assert(names.Contains("pipeline preset-to-npc"), "Preset pipeline command is missing.");
        Assert(names.Contains("bodygen build"), "BodyGen build command is missing.");
        Assert(names.Contains("forms search"), "FormID search command is missing.");
        Assert(names.Contains("assets search"), "Asset choice search command is missing.");
        Assert(names.Contains("headpart choices"), "Typed head-part choice command is missing.");
        return Task.CompletedTask;
    }

    private static Task TestWorkspacePath()
    {
        AssertThrows<ArgumentException>(() =>
        {
            var invalid = new WorkspacePath("relative-output");
            Assert(invalid.Value.Length > 0, "Relative workspace path unexpectedly constructed.");
        });
        return Task.CompletedTask;
    }

    private static Task TestOptionalRuntimeRootAdmission()
    {
        var workspaceRoot = new WorkspacePath("K:\\ActorwrightWork");
        Assert(
            ActorwrightWorkspace.IsAdmittedRuntimeRoot(
                workspaceRoot,
                new WorkspacePath("K:\\ActorwrightWork\\.actorwright\\runtime\\reference-preset")),
            "An in-workspace optional runtime was refused.");
        Assert(
            !ActorwrightWorkspace.IsAdmittedRuntimeRoot(
                workspaceRoot,
                new WorkspacePath("K:\\ActorwrightBundleExtract\\runtime\\reference-preset")),
            "An out-of-workspace optional runtime was admitted.");
        return Task.CompletedTask;
    }

    private static Task TestAllowedRoot()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var diagnostics = policy.Evaluate(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds"));
        Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error), "Valid K-local output was refused.");
        return Task.CompletedTask;
    }

    private static Task TestKOnlyPolicyDecisionObservability()
    {
        const string privatePath = "policy-observation-secret-sentinel";
        var labRoot = new WorkspacePath($@"K:\{privatePath}\lab");
        var allowedWorkspaceRoot = new WorkspacePath(
            Path.Combine(labRoot.Value, "workspace"));
        var refusedWorkspaceRoot = new WorkspacePath(
            $@"K:\{privatePath}\workspace");
        var protectedLiveRoot = new WorkspacePath(@"F:\ExampleGame");
        var policy = new KOnlyWorkspacePolicy(labRoot, protectedLiveRoot);
        var outputRoot = new WorkspacePath(
            Path.Combine(allowedWorkspaceRoot.Value, "output"));
        var outsideRoot = new WorkspacePath($@"K:\{privatePath}\outside");

        using (var disabledListener = new PolicyDecisionListener(
                   ActorwrightObservabilityEventSource.Keywords.Dispatch))
        {
            ImmutableArray<ImmutableArray<Diagnostic>> baseline =
            [
                policy.Evaluate(allowedWorkspaceRoot, outputRoot),
                policy.Evaluate(refusedWorkspaceRoot, outsideRoot),
                policy.EvaluateReadRoot(allowedWorkspaceRoot, outputRoot),
                policy.EvaluateReadRoot(refusedWorkspaceRoot, outsideRoot)
            ];
            Assert(disabledListener.Events.Length == 0,
                "disabled policy observation emitted an event");
            Assert(baseline[0].All(item =>
                       item.Severity != DiagnosticSeverity.Error) &&
                   baseline[1].Any(item =>
                       item.Code == ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab) &&
                   baseline[1].Any(item =>
                       item.Code == ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace) &&
                   baseline[2].All(item =>
                       item.Severity != DiagnosticSeverity.Error) &&
                   baseline[3].Any(item =>
                       item.Code == ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab) &&
                   baseline[3].Any(item =>
                       item.Code == "data-root-outside-workspace"),
                "policy evaluations did not retain expected allow/refusal diagnostics");

            using var enabledListener = new PolicyDecisionListener(
                ActorwrightObservabilityEventSource.Keywords.Policy);
            ImmutableArray<ImmutableArray<Diagnostic>> observed =
            [
                policy.Evaluate(allowedWorkspaceRoot, outputRoot),
                policy.Evaluate(refusedWorkspaceRoot, outsideRoot),
                policy.EvaluateReadRoot(allowedWorkspaceRoot, outputRoot),
                policy.EvaluateReadRoot(refusedWorkspaceRoot, outsideRoot)
            ];

            Assert(baseline.Length == observed.Length &&
                   baseline.Zip(observed).All(pair =>
                       pair.First.SequenceEqual(pair.Second)),
                "enabling policy observation changed diagnostics or order");

            PolicyDecisionEvent[] events = enabledListener.Events.ToArray();
            Assert(events.Length == 4 && events.All(item => item.EventId == 4),
                "the policy listener did not receive one event per completed evaluation");
            AssertPolicyDecision(events[0], policyKindId: 1, decisionId: 1,
                reasonFlags: 0);
            AssertPolicyDecision(events[1], policyKindId: 1, decisionId: 2,
                reasonFlags:
                    (int)(ActorwrightObservabilityEventSource.PolicyReason.WorkspaceRootOutsideLab |
                          ActorwrightObservabilityEventSource.PolicyReason.OutputRootOutsideWorkspace));
            AssertPolicyDecision(events[2], policyKindId: 2, decisionId: 1,
                reasonFlags: 0);
            AssertPolicyDecision(events[3], policyKindId: 2, decisionId: 2,
                reasonFlags:
                    (int)(ActorwrightObservabilityEventSource.PolicyReason.WorkspaceRootOutsideLab |
                          ActorwrightObservabilityEventSource.PolicyReason.DataRootOutsideWorkspace));
            Assert(!EventsContain(events, privatePath),
                "policy observation exposed a workspace path component");
        }

        return Task.CompletedTask;
    }

    private static void AssertPolicyDecision(
        PolicyDecisionEvent item,
        int policyKindId,
        int decisionId,
        int reasonFlags)
    {
        Assert(item.PayloadNames.SequenceEqual(
                   ["policyKindId", "decisionId", "reasonFlags"],
                   StringComparer.Ordinal),
            "policy event payload contained fields outside its closed schema");
        Assert(item.Payload.Length == 3 &&
               Equals(item.Payload[0], policyKindId) &&
               Equals(item.Payload[1], decisionId) &&
               Equals(item.Payload[2], reasonFlags),
            "policy event did not contain the expected closed decision reasons");
        Assert(item.Payload.All(value => value is int),
            "policy event exposed a non-numeric field");
    }

    private static bool EventsContain(
        IEnumerable<PolicyDecisionEvent> events,
        string value) => events.Any(item =>
        item.Payload.Any(payload =>
            payload?.ToString()?.Contains(value, StringComparison.Ordinal) == true));

    private sealed record PolicyDecisionEvent(
        int EventId,
        string[] PayloadNames,
        object?[] Payload);

    private sealed class PolicyDecisionListener(EventKeywords keywords)
        : EventListener
    {
        private readonly ConcurrentQueue<PolicyDecisionEvent> events = new();

        public PolicyDecisionEvent[] Events => events.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (string.Equals(eventSource.Name, "Actorwright-Observability",
                    StringComparison.Ordinal))
                EnableEvents(
                    eventSource,
                    EventLevel.Informational,
                    keywords);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            events.Enqueue(new PolicyDecisionEvent(
                eventData.EventId,
                eventData.PayloadNames?.ToArray() ?? [],
                eventData.Payload?.ToArray() ?? []));
        }
    }

    private static Task TestProtectedRoot()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var diagnostics = policy.Evaluate(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame\\Data"));
        Assert(diagnostics.Any(diagnostic => diagnostic.Code == "output-root-outside-workspace"), "Protected output did not fail closed.");
        return Task.CompletedTask;
    }

    private static Task TestAmbiguousPath()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var diagnostics = policy.Evaluate(
            new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\output:stream"));
        Assert(diagnostics.Any(diagnostic => diagnostic.Code == "alternate-data-stream-refused"),
            "Alternate-data-stream path was not refused.");
        return Task.CompletedTask;
    }

    private static async Task TestCancellation()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var service = new WorkspacePreflightService(policy);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => service.EvaluateAsync(
            new WorkspacePreflightRequest(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("K:\\ExampleWorkspace\\03-builds")), source.Token).AsTask());
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
