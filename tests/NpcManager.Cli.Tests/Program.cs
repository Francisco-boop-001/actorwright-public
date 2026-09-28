using System.Collections.Immutable;
using Actorwright.PublicFixtures;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Drawing;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Fallout4;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Noggog;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;
using NpcManager.Formats.Bethesda;
using NpcManager.Presets;
using NpcManager.FaceGen;
using NpcManager.BodyGen;
using NpcManager.Pipeline;
using NpcManager.Rendering;

namespace NpcManager.Cli.Tests;

internal static partial class Program
{
    private static int _passed;
    private const string Selector_TestRuntimeScriptBuildIdentity = "--test-runtime-script-build-identity";
    private const string Selector_TestSkyrimDialogueCli = "--test-skyrim-dialogue-cli";
    private const string Selector_TestSkyrimNpcVoiceCli = "--test-skyrim-npc-voice-cli";
    private const string Selector_TestSkyrimDialogueCommandBinding = "--test-skyrim-dialogue-command-binding";
    private const string Selector_TestHairRacePreflight = "--test-hair-race-preflight";
    private const string Selector_TestNativeReferenceWorkflow = "--test-native-reference-workflow";
    private const string Selector_TestFaceTintRefusalJournal = "--test-face-tint-refusal-journal";
    private const string Selector_TestRacemenuPathAdmission = "--test-racemenu-path-admission";
    private const string Selector_TestPathConventionDiscovery = "--test-path-convention-discovery";
    private const string Selector_TestProtocolV2FinishReviewed = "--test-protocol-v2-finish-reviewed";
    private const string Selector_TestProtocolV2FinishCore = "--test-protocol-v2-finish-core";
    private const string Selector_TestInheritedDefaultsAidt = "--test-inherited-defaults-aidt";
    private const string Selector_TestDiagnosticInputEvidence = "--test-diagnostic-input-evidence";
    private const string Selector_TestSiblingRouteRecovery = "--test-sibling-route-recovery";
    private const string Selector_TestPackageRuntimeArchive = "--test-package-runtime-archive";
    private const string Selector_TestPresetResolveLoadOrder = "--test-preset-resolve-load-order";
    private const string Selector_TestMixedVertexStrides = "--test-mixed-vertex-strides";
    private const string Selector_TestFaceBakeAuthorityDerivation = "--test-face-bake-authority-derivation";
    private const string Selector_TestNpcWholeSkin = "--test-npc-whole-skin";
    private const string Selector_TestExistingNpcWholeSkin = "--test-existing-npc-whole-skin";
    private const string Selector_TestEditPackagePreservation = "--test-edit-package-preservation";
    private const string Selector_TestPluginConsolidation = "--test-plugin-consolidation";
    private const string Selector_TestRacemenuPrivateHeadTextureRefusal = "--test-racemenu-private-head-texture-refusal";
    private const string Selector_TestRacemenuPrivateHeadTextures = "--test-racemenu-private-head-textures";
    private const string Selector_TestPluginAuditWorldParents = "--test-plugin-audit-world-parents";
    private const string Selector_TestPluginAuditNormalization = "--test-plugin-audit-normalization";
    private const string Selector_TestFacegeomMorphAdmission = "--test-facegeom-morph-admission";
    private const string Selector_TestFacegeomSourceAdmission = "--test-facegeom-source-admission";
    private const string Selector_TestFacegeomActualBake = "--test-facegeom-actual-bake";
    private const string Selector_TestFinishPackagePublication = "--test-finish-package-publication";
    private const string Selector_TestWaveBV1Transcript = "--test-wave-b-v1-transcript";
    private const string Selector_TestCompatibilityJourneyBoundaries = "--test-compatibility-journey-boundaries";
    private const string Selector_TestFinishOutfitRace = "--test-finish-outfit-race";
    private const string Selector_TestFinishCanonicalRead = "--test-finish-canonical-read";
    private const string Selector_TestFinishInheritedEvidence = "--test-finish-inherited-evidence";
    private const string Selector_TestOutputOwnedHeadparts = "--test-output-owned-headparts";
    private const string Selector_TestProtocolV2ReviewFinish = "--test-protocol-v2-review-finish";
    private const string Selector_TestProtocolV2NpcPreview = "--test-protocol-v2-npc-preview";
    private const string Selector_TestProtocolV2NpcCreateBuild = "--test-protocol-v2-npc-create-build";
    private const string Selector_TestGoldenSkyrimWorkflowResumption = "--test-golden-skyrim-workflow-resumption";
    private const string Selector_TestGoldenSkyrimPluginTypeParity = "--test-golden-skyrim-plugin-type-parity";
    private const string Selector_TestProtocolV2WorkflowBundles = "--test-protocol-v2-workflow-bundles";
    private const string Selector_TestProtocolV2Adapters = "--test-protocol-v2-adapters";
    private const string Selector_TestProtocolV2ReviewedIntake = "--test-protocol-v2-reviewed-intake";
    private const string Selector_TestProtocolV2FinishVerify = "--test-protocol-v2-finish-verify";
    private const string Selector_TestProtocolV2PresetInspect = "--test-protocol-v2-preset-inspect";
    private const string Selector_TestProtocolV2Cli = "--test-protocol-v2-cli";
    private const string Selector_TestProtocolV2Envelope = "--test-protocol-v2-envelope";
    private const string Selector_TestProtocolV2CommandLine = "--test-protocol-v2-command-line";
    private const string Selector_TestProtocolV2ActorAssembly = "--test-protocol-v2-actor-assembly";
    private const string Selector_TestAgentProtocolCompatibility = "--test-agent-protocol-compatibility";
    private const string Selector_TestDiagnosticExitClassification = "--test-diagnostic-exit-classification";
    private const string Selector_TestActorwrightHelp = "--test-actorwright-help";
    private const string Selector_TestDesktopWorkflowLaunchBinding = "--test-desktop-workflow-launch-binding";
    private const string Selector_TestPreview231CommandSet = "--test-preview231-command-set";
    private const string Selector_TestRichReviewedIntakeResponse = "--test-rich-reviewed-intake-response";
    private const string Selector_TestPublicHairRegionPreviewTransaction = "--test-public-hair-region-preview-transaction";
    private const string Selector_TestHairRegionSourceWarning = "--test-hair-region-source-warning";
    private const string Selector_TestHairRegionPreviewSessionLifecycle = "--test-hair-region-preview-session-lifecycle";
    private const string Selector_TestHairRegionPublicHelp = "--test-hair-region-public-help";
    private const string Selector_TestPreviewRuntimeComposition = "--test-preview-runtime-composition";
    private const string Selector_TestPreview234ConsumerContracts = "--test-preview234-consumer-contracts";
    private const string Selector_TestPreview258ConsumerDefects = "--test-preview258-consumer-defects";
    private const string Selector_TestPreview261LegacyDiscovery = "--test-preview261-legacy-discovery";
    private const string Selector_TestPreview261V1CommandVerification = "--test-preview261-v1-command-verification";
    private const string Selector_TestPreview267NonKWorkspaceRefusal = "--test-preview267-non-k-workspace-refusal";
    private const string Selector_TestPreview261HairIntakeDiscovery = "--test-preview261-hair-intake-discovery";
    private const string Selector_TestProviderAndHairtintCompatibility = "--test-provider-and-hairtint-compatibility";
    private const string Selector_TestNpcExtendedHeadPartType = "--test-npc-extended-head-part-type";
    private const string Selector_TestNpcBuildPreflight = "--test-npc-build-preflight";
    private const string Selector_TestNpcFinishCoreCli = "--test-npc-finish-core-cli";
    private const string Selector_TestNpcFinishSchemaContract = "--test-npc-finish-schema-contract";
    private const string Selector_TestPreview251OutfitAdmission = "--test-preview251-outfit-admission";
    private const string Selector_TestHairRegionStrictPreviewEvidence = "--test-hair-region-strict-preview-evidence";
    private const string Selector_FollowerFinishOnly = "--follower-finish-only";
    private const string Selector_HairRegionsOnly = "--hair-regions-only";
    private const string Selector_PluginAuditOnly = "--plugin-audit-only";
    private const string Selector_ActorAssemblyOnly = "--actor-assembly-only";

    private const string ExampleWorkspaceRoot = @"K:\ExampleWorkspace";
    private static readonly double[] FoldBasePixels = [0.2d, 0.4d, 0.6d, 0.5d];
    private static readonly double[] FoldFacetintPixels = [63d / 255d, 64d / 255d, 63d / 255d, 1d];
    private static readonly double[] FoldSkeeColor = [0d, 1d, 0d, 0.5d];
    private static readonly double[] FoldSkeePixels = [0.5d, 0d, 0d, 1d];
    private static readonly double[] FoldFaceColor = [1d, 1d, 1d, 1d];
    private static readonly double[] FoldRedPixels = [1d, 0d, 0d, 0.5d];
    private static readonly double[] FoldBluePixels = [0d, 0d, 1d, 1d];

    public static async Task<int> Main(string[] args)
    {
        if (StandaloneSelectorInventory.TryList(
                args,
                typeof(Program),
                Preview254ExternalSmpCliTestRegistry.Selectors))
            return 0;
        if (args is [Selector_TestRuntimeScriptBuildIdentity])
        {
            await TestRuntimeScriptBuildExactIdentitySuccess();
            await TestRuntimeScriptBuildRetainsIdentityThroughExecution();
            await TestRuntimeScriptBuildExecutableIdentityRefusal();
            await TestRuntimeScriptBuildManifestIdentityRefusal();
            await TestRuntimeScriptBuildDependencyIdentityRefusal();
            await TestRuntimeScriptBuildWrongPathRefusal();
            TestRuntimeScriptBuildReparseAttributeRefusal();
            TestRuntimeScriptBuildAncestorReparseAttributeRefusal();
            await TestRuntimeScriptBuildAncestorReparseRefusalWhenSupported();
            await TestRuntimeScriptInspectorProcessTreeCancellation();
            await TestRuntimeScriptBuildTimeoutAndCancellation();
            Console.WriteLine("PASS runtime-script-build-identity");
            return 0;
        }
        if (args is [Selector_TestSkyrimDialogueCli])
        {
            await SkyrimNpcDialogueCliTests.RunAsync();
            return 0;
        }
        if (args is [Selector_TestSkyrimNpcVoiceCli])
        {
            await SkyrimNpcVoiceCliTests.RunAsync();
            Console.WriteLine("PASS skyrim-npc-voice-cli");
            return 0;
        }
        if (args is [Selector_TestSkyrimDialogueCommandBinding])
        {
            await SkyrimNpcDialogueCommandBindingTests.RunAsync();
            Console.WriteLine("PASS skyrim-dialogue-command-binding");
            return 0;
        }
        if (args is [Selector_TestHairRacePreflight])
        {
            try
            {
                await GoldenSkyrimWorkflowResumptionTests.RunHairRacePreflightAsync();
                Console.WriteLine("PASS hair-race-preflight");
                return 0;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
        if (args is [Selector_TestNativeReferenceWorkflow])
        {
            try
            {
                await GoldenSkyrimWorkflowResumptionTests.RunNativeReferenceWorkflowAsync();
                Console.WriteLine("PASS native-reference-workflow");
                return 0;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
        if (args is [Selector_TestFaceTintRefusalJournal])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunFaceTintRefusalJournalAsync();
            Console.WriteLine("PASS face-tint typed refusal and local journal");
            return 0;
        }
        if (args is [Selector_TestRacemenuPathAdmission])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPathAdmissionAsync();
            Console.WriteLine("PASS racemenu-path-admission");
            return 0;
        }
        if (args is [Selector_TestPathConventionDiscovery])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPathConventionDiscoveryAsync();
            Console.WriteLine("PASS path-convention-discovery");
            return 0;
        }
        if (args is [Selector_TestProtocolV2FinishReviewed])
        {
            GoldenSkyrimWorkflowResumptionTests.RunOwnedScratchCleanupRegression();
            await GoldenSkyrimWorkflowResumptionTests.RunProtocolV2FinishReviewedAsync();
            Console.WriteLine("PASS protocol-v2-finish-reviewed");
            return 0;
        }
        if (args is [Selector_TestProtocolV2FinishCore])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunProtocolV2FinishCoreAsync();
            Console.WriteLine("PASS protocol-v2-finish-core");
            return 0;
        }
        if (args is [Selector_TestInheritedDefaultsAidt])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunInheritedDefaultsAsync();
            Console.WriteLine("PASS inherited-defaults-aidt");
            return 0;
        }
        if (args is [Selector_TestDiagnosticInputEvidence])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunDiagnosticInputEvidenceAsync();
            Console.WriteLine("PASS diagnostic-input-evidence");
            return 0;
        }
        if (args is [Selector_TestSiblingRouteRecovery])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync(siblingRecovery: true);
            Console.WriteLine("PASS sibling-route-recovery");
            return 0;
        }
        if (args is [Selector_TestPackageRuntimeArchive])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunRuntimeArchiveAsync();
            Console.WriteLine("PASS package-runtime-archive");
            return 0;
        }
        if (args is [Selector_TestPresetResolveLoadOrder])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPresetResolveLoadOrderAsync();
            Console.WriteLine("PASS preset-resolve-load-order");
            return 0;
        }
        if (args is [Selector_TestMixedVertexStrides])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunMixedVertexStridesAsync();
            Console.WriteLine("PASS mixed-vertex-strides");
            return 0;
        }
        if (args is [Selector_TestFaceBakeAuthorityDerivation])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunFaceBakeAuthorityDerivationAsync();
            Console.WriteLine("PASS face-bake-authority-derivation");
            return 0;
        }
        if (args is [Selector_TestNpcWholeSkin])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunWholeSkinPatchAsync();
            Console.WriteLine("PASS npc-whole-skin");
            return 0;
        }
        if (args is [Selector_TestExistingNpcWholeSkin])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunExistingWholeSkinAsync();
            Console.WriteLine("PASS existing-npc-whole-skin");
            return 0;
        }
        if (args is [Selector_TestEditPackagePreservation])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunEditPackagePreservationAsync();
            Console.WriteLine("PASS edit-package-preservation");
            return 0;
        }
        if (args is [Selector_TestPluginConsolidation])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPluginConsolidationAsync();
            Console.WriteLine("PASS plugin-consolidation");
            return 0;
        }
        if (args is [Selector_TestRacemenuPrivateHeadTextureRefusal])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPrivateHeadTextureRefusalAsync();
            Console.WriteLine("PASS RaceMenu private head texture refusal");
            return 0;
        }
        if (args is [Selector_TestRacemenuPrivateHeadTextures])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPrivateHeadTexturesAsync();
            Console.WriteLine("PASS RaceMenu private head textures");
            return 0;
        }
        if (args is [Selector_TestPluginAuditWorldParents])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunAuditWorldParentsAsync();
            Console.WriteLine("PASS plugin audit skeletal world parents");
            return 0;
        }
        if (args is [Selector_TestPluginAuditNormalization])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunAuditNormalizationAsync();
            Console.WriteLine("PASS plugin audit master normalization");
            return 0;
        }
        if (args is [Selector_TestFacegeomMorphAdmission])
        {
            await FaceGeomExtendedMorphTests.RunAdmissionAsync();
            Console.WriteLine("PASS facegeom extended morph admission");
            return 0;
        }
        if (args is [Selector_TestFacegeomSourceAdmission])
        {
            await FaceGeomExtendedMorphTests.RunSourceAdmissionAsync();
            Console.WriteLine("PASS facegeom source admission");
            return 0;
        }
        if (args is [Selector_TestFacegeomActualBake])
        {
            await FaceGeomExtendedMorphTests.RunActualBakeAsync();
            Console.WriteLine("PASS facegeom actual extended bake");
            return 0;
        }
        if (args is [Selector_TestFinishPackagePublication])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync(verifyPackage: true);
            Console.WriteLine("PASS Finish package publication composition");
            return 0;
        }
        if (args is [Selector_TestWaveBV1Transcript])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunWaveBTranscriptAsync();
            Console.WriteLine("PASS Wave B V1 NPC end-to-end transcript");
            return 0;
        }
        if (args is [Selector_TestCompatibilityJourneyBoundaries])
        {
            CompatibilityJourneySupport.RunBoundaryRegression();
            return 0;
        }
        if (args is [Selector_TestFinishOutfitRace])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync(outfitRace: true);
            Console.WriteLine("PASS actual CLI Finish outfit race refusal, clone, and verification");
            return 0;
        }
        if (args is [Selector_TestFinishCanonicalRead])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync(reformatDocuments: true);
            Console.WriteLine("PASS Finish canonical-equivalent document reads");
            return 0;
        }
        if (args is [Selector_TestFinishInheritedEvidence])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunCreateToFinishAsync(inheritedEvidence: true);
            Console.WriteLine("PASS Finish inherited host evidence relocation and verification");
            return 0;
        }
        if (args is [Selector_TestOutputOwnedHeadparts])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunOutputOwnedHeadPartsAsync();
            Console.WriteLine("PASS output-owned HDPT composition");
            return 0;
        }
        if (await Preview254ExternalSmpCliTestRegistry.TryRunAsync(
                args, CancellationToken.None) is { } externalSmpResult)
            return externalSmpResult;

        if (args is [Selector_TestProtocolV2ReviewFinish])
        {
            await ProtocolV2ReviewFinishTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 review and Finish workflow");
            return 0;
        }
        if (args is [Selector_TestProtocolV2NpcPreview])
        {
            await ProtocolV2NpcVisualPreviewTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 NPC visual preview");
            return 0;
        }
        if (args is [Selector_TestProtocolV2NpcCreateBuild])
        {
            await ProtocolV2NpcCreateBuildTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 NPC static build");
            return 0;
        }
        if (args is [Selector_TestGoldenSkyrimWorkflowResumption])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunAsync();
            Console.WriteLine("PASS golden Skyrim workflow artifact resumption");
            return 0;
        }
        if (args is [Selector_TestGoldenSkyrimPluginTypeParity])
        {
            await GoldenSkyrimWorkflowResumptionTests.RunPluginTypeParityAsync();
            Console.WriteLine("PASS golden Skyrim plugin-type parity");
            return 0;
        }
        if (args is [Selector_TestProtocolV2WorkflowBundles])
        {
            await ProtocolV2WorkflowBundleLifecycleTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 workflow bundle lifecycle");
            return 0;
        }
        if (args is [Selector_TestProtocolV2Adapters])
        {
            await ProtocolV2AdapterTests.RunAsync();
            Console.WriteLine("PASS protocol v2 generic adapter boundary");
            return 0;
        }
        if (args is [Selector_TestProtocolV2ReviewedIntake])
        {
            await GoldenSkyrimProtocolV2Tests.TestReviewedIntakePersistence();
            Console.WriteLine("PASS protocol-v2 reviewed-intake persistence");
            return 0;
        }
        if (args is [Selector_TestProtocolV2FinishVerify])
        {
            await ProtocolV2FinishVerifyTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 Finish Verify persistence");
            return 0;
        }
        if (args is [Selector_TestProtocolV2PresetInspect])
        {
            await ProtocolV2PresetInspectTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 preset-inspect persistence");
            return 0;
        }
        if (args is [Selector_TestProtocolV2Cli])
        {
            await ProtocolV2CliTests.RunAsync();
            Console.WriteLine("PASS protocol v2 CLI discovery kernel");
            return 0;
        }
        if (args is [Selector_TestProtocolV2Envelope])
        {
            ProtocolV2EnvelopeTests.Run();
            Console.WriteLine("PASS protocol v2 envelope and request digest");
            return 0;
        }
        if (args is [Selector_TestProtocolV2CommandLine])
        {
            await ProtocolV2CommandLineTests.RunAsync();
            Console.WriteLine("PASS protocol v2 command-line validation");
            return 0;
        }
        if (args is [Selector_TestProtocolV2ActorAssembly])
        {
            await ProtocolV2ActorAssemblyPreflightTests.RunAsync();
            Console.WriteLine("PASS protocol-v2 Actor Assembly preflight");
            return 0;
        }
        if (args is [Selector_TestAgentProtocolCompatibility])
        {
            await TestAgentProtocolCompatibility();
            Console.WriteLine("PASS agent protocol compatibility baseline");
            return 0;
        }
        if (args is [Selector_TestDiagnosticExitClassification])
        {
            DiagnosticExitCodeClassifierTests.Run();
            Console.WriteLine(
                "PASS exact shared CLI diagnostic exit classification");
            return 0;
        }
        if (args is [Selector_TestActorwrightHelp])
        {
            await TestActorwrightHelpIdentity();
            Console.WriteLine("PASS Actorwright help identity");
            return 0;
        }
        if (args is [Selector_TestDesktopWorkflowLaunchBinding])
        {
            await TestGuiProductionPolicyRefusal();
            await TestGuiV1Forwarding();
            await TestGuiWorkflowLaunchUsage();
            await TestGuiWorkflowLaunchBinding();
            TestDesktopWorkflowProcessArguments();
            TestDesktopLaunchServiceInvalidWorkflowBinding();
            Console.WriteLine("PASS desktop workflow launch binding");
            return 0;
        }
        if (args is [Selector_TestPreview231CommandSet])
        {
            await TestCapabilities();
            Console.WriteLine("PASS exact 142-command set");
            return 0;
        }
        if (args is [Selector_TestRichReviewedIntakeResponse])
        {
            await FaceGeomHairRegionsCliTests
                .TestRichReviewedIntakeResponseRoundTrip();
            Console.WriteLine(
                "PASS rich reviewed-intake response round trip");
            return 0;
        }
        if (args is [Selector_TestPublicHairRegionPreviewTransaction])
        {
            await FaceGeomHairRegionsCliTests
                .TestPublicPreviewOwnsWholeBundleTransaction();
            Console.WriteLine(
                "PASS public hair-region preview bundle transaction");
            return 0;
        }
        if (args is [Selector_TestHairRegionSourceWarning])
        {
            await FaceGeomHairRegionsCliTests
                .TestSourceWarningReachesPreviewCliJson();
            Console.WriteLine(
                "PASS hair-region source warning propagation");
            return 0;
        }
        if (args is [Selector_TestHairRegionPreviewSessionLifecycle])
        {
            await FaceGeomHairRegionsCliTests
                .TestPreviewSessionLifecycle();
            Console.WriteLine(
                "PASS hair-region preview session lifecycle");
            return 0;
        }
        if (args is [Selector_TestHairRegionPublicHelp])
        {
            await FaceGeomHairRegionsCliTests
                .TestDiscoveryHelpAndSchema();
            Console.WriteLine(
                "PASS hair-region public help");
            return 0;
        }
        if (args is [Selector_TestPreviewRuntimeComposition])
        {
            await PreviewRuntimeCompositionTests.RunAsync();
            Console.WriteLine(
                "PASS lazy authenticated preview runtime composition");
            return 0;
        }
        if (args is [Selector_TestPreview234ConsumerContracts])
        {
            await Preview234ConsumerContractTests.RunAsync();
            Console.WriteLine(
                "PASS preview.234 producer-to-consumer intake contract");
            return 0;
        }
        if (args is [Selector_TestPreview258ConsumerDefects])
        {
            await Preview258ConsumerDefectTests.RunAsync();
            Console.WriteLine("PASS Preview.258 consumer defect repairs");
            return 0;
        }
        if (args is [Selector_TestPreview261LegacyDiscovery])
        {
            await Preview261LegacyDiscoveryTests.RunAsync();
            Console.WriteLine("PASS Preview.261 legacy discovery metadata");
            return 0;
        }
        if (args is [Selector_TestPreview261V1CommandVerification])
        {
            await TestPreview261V1CommandVerification();
            Console.WriteLine("PASS Preview.261 exhaustive V1 command verification");
            return 0;
        }
        if (args is [Selector_TestPreview267NonKWorkspaceRefusal])
        {
            await TestPreview267NonKWorkspaceRefusal();
            return 0;
        }
        if (args is [Selector_TestPreview261HairIntakeDiscovery])
        {
            await Preview261HairIntakeDiscoveryTests.RunAsync();
            Console.WriteLine("PASS Preview.261 Hair Regions intake discovery");
            return 0;
        }
        if (args is [Selector_TestProviderAndHairtintCompatibility])
        {
            await FaceGeomHairRegionsCliTests
                .TestStrictDocumentLoading();
            Console.WriteLine(
                "PASS provider and HairTint JSON compatibility");
            return 0;
        }
        if (args is [Selector_TestNpcExtendedHeadPartType]) { await GoldenSkyrimWorkflowResumptionTests.RunExtendedHeadPartTypePreflightAsync(); Console.WriteLine("PASS NPC extended HDPT type preflight"); return 0; }
        if (args is [Selector_TestNpcBuildPreflight])
        {
            await NpcBuildPreflightCliTests.RunAsync();
            Console.WriteLine("PASS NPC build preflight CLI");
            return 0;
        }
        if (args is [Selector_TestNpcFinishCoreCli])
        {
            await SkyrimNpcFinishCoreCliTests.TestCatalogAndParsing();
            await SkyrimNpcFinishCoreCliTests.TestHandlerMapsAllModes();
            await SkyrimNpcFinishCoreCliTests
                .TestSchemaExportPublishesDocumentContracts();
            Console.WriteLine("PASS Skyrim NPC Finish Core CLI");
            return 0;
        }
        if (args is [Selector_TestNpcFinishSchemaContract])
        {
            await SkyrimNpcFinishCoreCliTests
                .TestSchemaExportPublishesDocumentContracts();
            await SkyrimNpcFinishCoreCliTests
                .TestEnumRefusalPublishesAdmittedValues();
            Console.WriteLine("PASS Skyrim NPC Finish Core schema contract");
            return 0;
        }
        if (args is [Selector_TestPreview251OutfitAdmission])
        {
            await TestPreview251OutfitAdmission();
            Console.WriteLine("PASS preview.251 outfit proposal admission");
            return 0;
        }
        if (args is [Selector_TestHairRegionStrictPreviewEvidence])
        {
            await FaceGeomHairRegionsCliTests
                .TestStrictPreviewEvidenceLoading();
            Console.WriteLine(
                "PASS hair-region strict preview evidence");
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Actor Assembly preflight emits exact read-only result and error envelopes", TestActorAssemblyPreflightCli),
            ("two-word command parsing is stable", TestParsing),
            ("three-word command parsing is stable", TestThreeWordParsing),
            ("archetype FormReference parsing is typed", TestFormReferenceParsing),
            ("NPC level union validates fixed and multiplier bounds", TestNpcLevelUnion),
            ("NPC flag masks remain game-aware", TestNpcFlagMasks),
            ("NPC keyword list contracts preserve order", TestNpcKeywordContracts),
            ("NPC faction contracts preserve rank and operation order", TestNpcFactionContracts),
            ("NPC inventory contracts preserve signed counts and operation order", TestNpcInventoryContracts),
            ("NPC outfit contracts preserve optional clear semantics", TestNpcOutfitContracts),
            ("NPC perk contracts preserve unsigned ranks and operation order", TestNpcPerkContracts),
            ("NPC actor-effect contracts preserve ordered list operations", TestNpcActorEffectContracts),
            ("NPC property contracts preserve typed float values and operation order", TestNpcPropertyContracts),
            ("NPC template categories preserve pinned bit order and wire names", TestNpcTemplateContracts),
            ("NPC reset sections use stable wire names", TestNpcResetContracts),
            ("NPC face contracts preserve typed headpart and hair-color semantics", TestNpcFaceContracts),
            ("CharGen options defaults and game rules are typed", TestCharGenOptionsContracts),
            ("CharGen options CLI validates and writes canonical JSON", TestCharGenOptionsCli),
            ("FO4 face-tint contracts preserve typed wire fields", TestFaceTintContracts),
            ("FO4 face-tint CLI applies ordered TETI/TEND layers", TestFaceTintCli),
            ("SSE face-morph contracts preserve trailing and unset semantics", TestSseFaceMorphContracts),
            ("SSE face-morph reader resolves plugin-local IDs with eight masters", TestSseFaceMorphPluginLocalFormId),
            ("SSE face-morph CLI applies typed NAM9/NAMA values", TestSseFaceMorphCli),
            ("SSE face-tint contracts preserve RGB alpha coverage and TIAS", TestSseFaceTintContracts),
            ("SSE face-tint CLI applies ordered TINI/TINC/TINV/TIAS layers", TestSseFaceTintCli),
            ("SSE face-tint invalid layers persist typed refusal before plugin access",
                GoldenSkyrimWorkflowResumptionTests.RunFaceTintRefusalJournalAsync),
            ("RaceMenu extended morph contracts preserve name/value semantics", TestRaceMenuExtendedMorphContracts),
            ("RaceMenu extended morph CLI patches only customMorphs", TestRaceMenuExtendedMorphCli),
            ("RaceMenu sculpt contracts preserve host and vertex semantics", TestRaceMenuSculptContracts),
            ("RaceMenu sculpt CLI patches scaled per-shape rows", TestRaceMenuSculptCli),
            ("face pose contracts preserve typed bone and channel semantics", TestFacePoseContracts),
            ("face pose CLI resolves additive FMRS and vertex channels", TestFacePoseCli),
            ("face reset sections preserve pinned tab names", TestFaceResetContracts),
            ("face reset CLI replaces one section and preserves the rest", TestFaceResetCli),
            ("body reset sections preserve pinned tab names", TestBodyResetContracts),
            ("body reset CLI replaces one section and preserves the rest", TestBodyResetCli),
            ("FO4 weight triangle contracts preserve simplex math", TestWeightTriangleContracts),
            ("FO4 weight triangle CLI exposes normalization and redistribution", TestWeightTriangleCli),
            ("NPC skin contracts preserve FO4 set and clear semantics", TestSkinContracts),
            ("body skin CLI routes FO4 WNAM and refuses unsupported paths", TestSkinCli),
            ("FO4 body-region contracts preserve pinned MRSV order", TestBodyMorphContracts),
            ("body-region CLI patches MRSV and refuses unsupported values", TestBodyMorphCli),
            ("CLI runner uses the typed composition boundary", TestCliRunnerCompositionBoundary),
            ("preset CLI composition exposes only the exact FaceTint capability",
                TestPresetFaceTintCompositionGuard),
            ("reference preset commands are uniquely cataloged and versioned",
                ReferencePresetCliTests.TestCommandCatalog),
            ("reference preset CLI maps all three commands to one typed transaction",
                ReferencePresetCliTests.TestHandlerMapsAllThreeCommands),
            ("reference preset CLI refuses unknown missing and cancelled work",
                ReferencePresetCliTests.TestHandlerRefusesUsageAndCancellation),
            ("CLI runner dispatches reference commands through its typed service bundle",
                ReferencePresetCliTests.TestRunnerDispatchesReferenceCommands),
            ("follower-finish commands are uniquely cataloged and parsed",
                SkyrimFollowerFinishCliTests.TestCatalogAndParsing),
            ("follower-finish CLI maps analyze apply and verify without fallback",
                SkyrimFollowerFinishCliTests.TestHandlerMapsAllModes),
            ("follower-finish CLI refuses malformed unsafe and cancelled work",
                SkyrimFollowerFinishCliTests.TestHandlerRefusalsAndCancellation),
            ("CLI runner dispatches follower-finish only through its typed service bundle",
                SkyrimFollowerFinishCliTests.TestRunnerDispatchAndUnavailableService),
            ("Finish Core commands are uniquely catalogued and parsed",
                SkyrimNpcFinishCoreCliTests.TestCatalogAndParsing),
            ("Finish Core CLI maps analyze apply and verify through one service",
                SkyrimNpcFinishCoreCliTests.TestHandlerMapsAllModes),
            ("Finish Core schema export publishes exact request and proposal contracts",
                SkyrimNpcFinishCoreCliTests.TestSchemaExportPublishesDocumentContracts),
            ("Finish Core enum refusals publish exact admitted values",
                SkyrimNpcFinishCoreCliTests.TestEnumRefusalPublishesAdmittedValues),
            ("FaceGeom hair-region commands are unique, parsed, and versioned",
                FaceGeomHairRegionsCliTests.TestCatalogAndParsing),
            ("FaceGeom hair-region documents retain source identity and canonical semantics",
                FaceGeomHairRegionsCliTests.TestStrictDocumentLoading),
            ("FaceGeom hair-region preview evidence uses strict closed JSON authority",
                FaceGeomHairRegionsCliTests.TestStrictPreviewEvidenceLoading),
            ("FaceGeom hair-region paths, intake, and invocation stay fail-closed",
                FaceGeomHairRegionsCliTests.TestPathIntakeAndInvocationRefusals),
            ("Reviewed-intake CLI response preserves nonempty exact auxiliary authority",
                FaceGeomHairRegionsCliTests.TestRichReviewedIntakeResponseRoundTrip),
            ("FaceGeom hair-region public preview publishes one complete sibling-private bundle",
                FaceGeomHairRegionsCliTests.TestPublicPreviewOwnsWholeBundleTransaction),
            ("FaceGeom hair-region source diagnostics remain lossless and fail closed",
                FaceGeomHairRegionsCliTests.TestSourceWarningReachesPreviewCliJson),
            ("FaceGeom hair-region per-render sessions stay live through proof and clean on every exit",
                FaceGeomHairRegionsCliTests.TestPreviewSessionLifecycle),
            ("FaceGeom hair-region preview binds exact documents through one typed seam",
                FaceGeomHairRegionsCliTests.TestPreviewHandlerContract),
            ("FaceGeom hair-region preview requires exact materialization and proof",
                FaceGeomHairRegionsCliTests.TestPreviewAuthorityAndProofRefusals),
            ("FaceGeom hair-region results preserve observed and surviving evidence",
                FaceGeomHairRegionsCliTests.TestResultInvariantCancellationAndRollback),
            ("FaceGeom hair-region CLI completes the hash-bound transaction and refuses drift",
                FaceGeomHairRegionsCliTests.TestTransactionLifecycleAndRefusals),
            ("CliRunner dispatches FaceGeom hair-region preview and production fails closed",
                FaceGeomHairRegionsCliTests.TestRunnerDispatchAndFailClosedPreview),
            ("FaceGeom hair-region capabilities, help, and schema export stay synchronized",
                FaceGeomHairRegionsCliTests.TestDiscoveryHelpAndSchema),
            ("public help uses the Actorwright product identity", TestActorwrightHelpIdentity),
            ("version JSON separates internal version from development source line", TestVersionIdentity),
            ("capabilities JSON is versioned", TestCapabilities),
            ("schema export is versioned, typed, and non-overwriting", TestSchemaExport),
            ("GUI launch preserves the production K-only refusal", TestGuiProductionPolicyRefusal),
            ("GUI launch forwards the unchanged v1 request", TestGuiV1Forwarding),
            ("GUI workflow launch requires a paired uppercase binding", TestGuiWorkflowLaunchUsage),
            ("GUI workflow launch forwards the exact binding", TestGuiWorkflowLaunchBinding),
            ("Desktop workflow launch uses four exact process arguments", () =>
            {
                TestDesktopWorkflowProcessArguments();
                return Task.CompletedTask;
            }),
            ("Desktop launch refuses invalid workflow bindings before path and policy access", () =>
            {
                TestDesktopLaunchServiceInvalidWorkflowBinding();
                return Task.CompletedTask;
            }),
            ("allowed preflight returns success", TestAllowedPreflight),
            ("refused preflight returns security exit", TestRefusedPreflight),
            ("missing preflight roots return usage exit", TestMissingPreflightRoots),
            ("preflight cancellation propagates", TestCancellation),
            ("game/Data preflight accepts both copied fixtures", TestGameRootPreflightBothGames),
            ("game/Data preflight rejects read/write overlap", TestGameRootPreflightOverlap),
            ("game/Data preflight CLI emits a versioned contract", TestGameRootPreflightCli),
            ("reviewed workspace CLI matches the copied Skyrim closure", TestReviewedGameIntakeCli),
            ("high-fidelity NPC preview CLI binds reviewed intake and exact package overlay",
                NpcVisualPreviewCliTests.TestSuccessfulMapping),
            ("high-fidelity NPC preview CLI rejects malformed and partial authority",
                NpcVisualPreviewCliTests.TestRefusals),
            ("high-fidelity NPC preview CLI dispatch is typed and cancellation-safe",
                NpcVisualPreviewCliTests.TestDispatchAndCancellation),
            ("game/Data preflight rejects partial options", TestGameRootPreflightUsage),
            ("game/Data preflight refuses protected roots", TestGameRootPreflightProtectedRoot),
            ("game/Data preflight propagates cancellation", TestGameRootPreflightCancellation),
            ("archive consistency accepts both copied games", TestArchiveConsistencyBothGames),
            ("archive consistency rejects an index/disk mismatch", TestArchiveConsistencyMismatch),
            ("archive consistency rejects wrong edition and protected roots", TestArchiveConsistencySafety),
            ("archive consistency rejects malformed indexes", TestArchiveConsistencyMalformedIndex),
            ("archive consistency requires paired options", TestArchiveConsistencyUsage),
            ("archive consistency propagates cancellation", TestArchiveConsistencyCancellation),
            ("generated artifact scan accepts both copied games", TestGeneratedArtifactScanBothGames),
            ("generated artifact scan diagnoses malformed plugin headers", TestGeneratedArtifactScanMalformedPlugin),
            ("generated artifact scan requires paired options", TestGeneratedArtifactScanUsage),
            ("generated artifact scan refuses protected roots", TestGeneratedArtifactScanProtectedRoot),
            ("generated artifact scan propagates cancellation", TestGeneratedArtifactScanCancellation),
            ("BodySlide sidecar inspection accepts both games and round-trips", TestBodySidecarBothGames),
            ("BodySlide sidecar inspection rejects malformed partial state", TestBodySidecarMalformed),
            ("BodySlide sidecar inspection refuses protected roots", TestBodySidecarProtectedRoot),
            ("BodySlide sidecar inspection propagates cancellation", TestBodySidecarCancellation),
            ("BodySlide sidecar writer emits a reloadable typed NPC entry", TestBodySidecarWrite),
            ("BodySlide SliderPreset XML inspection is bounded and native-percent",
                TestBodySlideSliderPresetInspectionCli),
            ("BodySlide PIRT resolution accepts FO4 and SSE presets", TestBodySlideTriResolution),
            ("BodySlide PIRT resolution rejects malformed and protected inputs", TestBodySlideTriSafety),
            ("Skyrim body-weight resolution applies gated sparse interpolation", TestSseBodyWeightResolution),
            ("Skyrim body-weight resolution rejects unsafe and malformed manifests", TestSseBodyWeightSafety),
            ("body-overlay CLI preserves FO4 and Skyrim layer ordering", TestBodyOverlayResolution),
            ("body-overlay CLI rejects unsafe fields and paths", TestBodyOverlaySafety),
            ("Skyrim body transforms apply typed node and skin replacements", TestSkyrimBodyTransformApply),
            ("Skyrim overlay bake composes order, alpha, color space, and DDS output", TestSkyrimOverlayBake),
            ("Skyrim overlay bake rejects dimensions and existing outputs", TestSkyrimOverlayBakeSafety),
            ("SSE fixture inventory is typed", TestSseInventory),
            ("FO4 fixture inventory is typed", TestFo4Inventory),
            ("dual-game plugin load order resolves explicitly", TestPluginLoadOrderResolve),
            ("dual-game plugin compatibility validates masters", TestPluginCompatibility),
            ("load-order validate routes to plugin compatibility", TestLoadOrderValidateAlias),
            ("profile scan fingerprints an explicit copied profile", TestProfileScan),
            ("records list exposes typed non-NPC records", TestRecordsList),
            ("duplicate load-order entries fail closed", TestDuplicateLoadOrder),
            ("child plugin reparse points fail closed", TestPluginLoadOrderChildReparseRefusal),
            ("plugin master cycles fail closed", TestPluginMasterCycle),
            ("NPC search filters the typed result", TestNpcSearch),
            ("NPC inspection returns versioned metadata", TestNpcInspect),
            ("NPC category filters are deterministic", TestNpcCategoryFilter),
            ("NPC category union selects the exact typed intersection", TestNpcCategoryIntersection),
            ("NPC changed-only filter follows explicit override order", TestNpcChangedFilter),
            ("NPC category filters reject unknown values", TestNpcCategoryFilterUsage),
            ("NPC inventory refuses protected read roots", TestNpcProtectedRoot),
            ("FormID search filters signatures and reports override provenance", TestFormChoiceSearch),
            ("FormID search CLI emits a versioned candidate contract", TestFormChoiceCli),
            ("FormID search rejects malformed signatures", TestFormChoiceUsage),
            ("FormID search refuses protected roots", TestFormChoiceProtectedRoot),
            ("FormID search propagates cancellation", TestFormChoiceCancellation),
            ("mesh search returns normalized provider evidence", TestMeshChoiceCli),
            ("headpart search resolves typed provenance and providers", TestHeadPartChoice),
            ("headpart search reports missing providers explicitly", TestHeadPartMissingProvider),
            ("RaceMenu paint choices expose stable typed JSON", TestRaceMenuPaintChoiceCli),
            ("asset choice search rejects malformed kind", TestAssetChoiceUsage),
            ("asset choice search refuses protected roots", TestAssetChoiceProtectedRoot),
            ("asset choice search propagates cancellation", TestAssetChoiceCancellation),
            ("asset choice search rejects malformed provider evidence", TestAssetChoiceProviderValidation),
            ("asset index export applies deterministic provider precedence", TestAssetIndexExportPrecedence),
            ("asset index export rejects malformed provider evidence", TestAssetIndexExportProviderValidation),
            ("asset index export CLI writes a versioned artifact", TestAssetIndexExportCli),
            ("asset index export refuses existing output", TestAssetIndexExportExisting),
            ("asset index export refuses protected output", TestAssetIndexExportProtectedOutput),
            ("asset index export refuses alternate data stream output", TestAssetIndexExportAlternateDataStream),
            ("asset index export propagates cancellation", TestAssetIndexExportCancellation),
            ("missing plugin is fail-closed", TestMissingPlugin),
            ("malformed archive is diagnosed", TestMalformedArchive),
            ("inventory cancellation propagates", TestInventoryCancellation),
            ("SSE mutation round-trip preserves untouched fields", TestSseMutation),
            ("FO4 mutation round-trip preserves untouched fields", TestFo4Mutation),
            ("existing-NPC package writes one true source-owned override", TestExistingNpcGameplayPackage),
            ("existing-NPC identity package verifies typed names and archetype", TestExistingNpcIdentityPackage),
            ("existing-NPC collection package preserves source-mastered lists", TestExistingNpcCollectionPackage),
            ("external archetype references fail closed", TestExternalArchetypeReference),
            ("malformed archetype references return usage errors", TestMalformedArchetypeReference),
            ("npc patch CLI applies typed sex with game alias", TestSexMutationCli),
            ("sex mutation rejects a no-op", TestSexMutationNoOp),
            ("mutation refuses a stale input hash", TestStaleMutationHash),
            ("mutation refuses an existing destination", TestExistingMutationDestination),
            ("mutation refuses an output outside K", TestUnsafeMutationOutput),
            ("independent verifier rejects malformed output", TestMalformedMutationOutput),
            ("proposal file binds and then applies once", TestProposalApply),
            ("LooksMenu preset inspection is typed and loss-aware", TestLooksMenuPreset),
            ("LooksMenu morph subfields and tint order are typed", TestLooksMenuExtendedFields),
            ("LooksMenu Morphs.Values feeds the FO4 MRSV pipeline", TestLooksMenuBodyRegionPipeline),
            ("RaceMenu jslot inspection is typed", TestRaceMenuPreset),
            ("RaceMenu preset catalog is deterministic, filterable, and hash-bound", TestRaceMenuPresetCatalogCli),
            ("real RaceMenu transform names and integer alpha round-trip", TestRaceMenuRealEncodingVariants),
            ("preset export is deterministic and non-overwriting", TestPresetExport),
            ("LooksMenu export is canonical and reloadable", TestLooksMenuCanonicalExport),
            ("RaceMenu jslot nested metadata is typed and loss-aware", TestRaceMenuNestedMetadata),
            ("RaceMenu jslot export is canonical and reloadable", TestRaceMenuCanonicalExport),
            ("preset duplicate keys fail closed", TestPresetDuplicateKeys),
            ("preset input outside K is refused", TestPresetOutsideK),
            ("preset diff reports typed changes", TestPresetDiff),
            ("preset diff covers nested fields and loss diagnostics", TestPresetDiffNested),
            ("portable form identifiers resolve only through explicit load order", TestPresetResolve),
            ("appearance copy merges only selected preset sections", TestAppearanceCopy),
            ("appearance copy all expands to supported preset sections", TestAppearanceCopyAll),
            ("FO4 FaceGen applicability diagnosis is typed", TestFo4FaceGenDiagnosis),
            ("FaceGeom build writes a deterministic semantic artifact", TestFaceGeomBuild),
            ("FaceTint build writes deterministic dual-game semantic probes", TestFaceTintBuild),
            ("FaceGen corrections preserve non-triggering NPCs", TestFaceGenCorrections),
            ("FaceGen batch reports every attempted NPC", TestFaceGenBatch),
            ("FaceGen plugin target isolates winning plugin outputs", TestFaceGenPluginTarget),
            ("Preview scene evidence preserves ordered asset providers", TestPreviewScene),
            ("Preview variants bind outfits and record included pieces", TestPreviewSceneVariants),
            ("Preview reroll selects a seed-stable validated variant", TestPreviewReroll),
            ("Preview camera and lighting presets are versioned and deterministic", TestPreviewPresets),
            ("Preview animation selection binds frame time skeleton and rate", TestPreviewAnimation),
            ("Animation list mirrors picker taxonomy filters", TestPreviewAnimationList),
            ("Animation tree mirrors picker hierarchy", TestPreviewAnimationTree),
            ("Preview hair partition zaps bind game-specific headwear coverage", TestPreviewHairZap),
            ("Preview NIF export writes a sandboxed verified plan", TestPreviewNifExport),
            ("Outfit list exposes items and winning override provenance", TestOutfitList),
            ("Outfit propose writes a hash-bound create and override plan", TestOutfitProposal),
            ("Outfit binary write CLI exposes independently verified output metadata", TestOutfitBinaryWriteCli),
            ("Leveled-list binary write CLI exposes independently verified output metadata", TestLeveledListBinaryWriteCli),
            ("Armor binary write CLI exposes independently verified output metadata", TestArmorBinaryWriteCli),
            ("Armor-addon binary write CLI exposes independently verified output metadata", TestArmorAddonBinaryWriteCli),
            ("Material-swap binary write CLI exposes independently verified output metadata", TestMaterialSwapBinaryWriteCli),
            ("Object-template binary write CLI exposes independently verified output metadata", TestObjectTemplateBinaryWriteCli),
            ("Leveled-list propose preserves typed entries and flags", TestLeveledListProposal),
            ("Leveled-list resolve is seed-stable and preview-compatible", TestLeveledListResolve),
            ("Armor propose preserves typed supported fields", TestArmorProposal),
            ("Armor damage resistance preserves ordered typed entries", TestArmorDamageResistance),
            ("Armor-addon propose preserves typed model skin and sculpt routes", TestArmorAddonProposal),
            ("Armor-addon model entries preserve ordered indexes and game gates", TestArmorAddonModels),
            ("Material-swap propose preserves ordered substitutions and remap values", TestMaterialSwapProposal),
            ("Object-template propose preserves combination and include order", TestObjectTemplateProposal),
            ("Object-template properties preserve typed value routes", TestObjectTemplateProperties),
            ("Changes list emits only stable field-level differences", TestChangeTracking),
            ("Changes update writes explicit reset and delete proposals", TestChangeAction),
            ("Records propose preserves explicit allocation and mode", TestRecordProposal),
            ("Plugin write consumes a hash-bound scalar proposal", TestPluginWrite),
            ("Plugin verify checks proposal-bound change surfaces", TestPluginVerify),
            ("Plugin audit compares the typed record surface and refuses unsafe roots", TestPluginSurfaceAudit),
            ("Plugin deploy is hash-bound, idempotent, and never overwrites conflicts", TestPluginDeploy),
            ("Runtime smoke verify validates operator evidence and refuses unsafe roots", TestRuntimeSmokeVerify),
            ("SSE FaceGen applicability diagnosis is typed", TestSseFaceGenDiagnosis),
            ("FaceGen zero-shape verification fails closed", TestFaceGenZeroShapes),
            ("FaceGen strict verification rejects hair and collider poison", TestFaceGenPoisonShapes),
            ("FaceGen provider paths apply the ESL-aware local FormID rule", TestFaceGenProviderPathRules),
            ("FaceGen provider context preserves winning NPC identity", TestFaceGenProviderContext),
            ("FO4 FaceTint provider binding resolves RACE tint groups and CLFM data", TestFaceTintProviderBinding),
            ("FaceGen pack CLI exposes a hash-bound canonical package", TestFaceGenPack),
            ("FaceGen pack deployment is hash-bound, idempotent, and conflict-safe", TestFaceGenDeploy),
            ("package commands inspect, verify, and copy a typed package", TestPackageCommands),
            ("FaceGeom provider-bound builds bind canonical paths and reject overlap", TestFaceGeomProviderBoundBuild),
            ("FaceTint provider-bound builds bind canonical paths and reject overlap", TestFaceTintProviderBoundBuild),
            ("native Skyrim FaceTint CLI maps explicit record and provider inputs", TestSkyrimNativeFaceTintCli),
            ("native Skyrim FaceGen batch CLI maps typed roots and preserves static authority", TestNativeFaceGenBatchCli),
            ("FaceGen duplicate keys fail closed", TestFaceGenDuplicateKeys),
            ("FaceGen unsupported schema fails closed", TestFaceGenSchemaVersion),
            ("FaceGen manifest outside K is refused", TestFaceGenOutsideK),
            ("FO4 BodyGen sidecars are deterministic and typed", TestFo4BodyGen),
            ("SSE BodyGen sidecars use the game path", TestSseBodyGen),
            ("BodyGen duplicate morphs fail closed", TestBodyGenDuplicateMorphs),
            ("BodyGen existing output is never overwritten", TestBodyGenExistingOutput),
            ("BodyGen output outside K is refused", TestBodyGenOutsideK),
            ("BodyGen plugin target delimiters fail closed", TestBodyGenPluginDelimiter),
            ("BodyGen unresolved load-order FormIDs fail closed", TestBodyGenLoadOrderFormId),
            ("BodyGen cancellation propagates", TestBodyGenCancellation),
            ("BodyGen write consumes typed assignment documents", TestBodyGenWrite),
            ("runtime-script VMAD proposals are typed and game-bound", TestRuntimeScriptProposal),
            ("runtime-script build records PSC and PEX evidence", TestRuntimeScriptBuild),
            ("runtime-script binary write CLI exposes the bounded VMAD writer", TestRuntimeScriptBinaryWriteCli),
            ("runtime-script VMAD inspection keeps copied-plugin evidence read-only", TestRuntimeScriptVmadInspect),
            ("runtime-script package copies only the game-specific apply PEX", TestRuntimeScriptPackage),
            ("pipeline test output roots are invocation-isolated", TestPipelineOutputRootIsolation),
            ("runtime-script deploy installs and protects copied Data roots", TestRuntimeScriptDeploy),
            ("typed BodyGen model build is deterministic", TestTypedBodyGen),
            ("FO4 preset-to-NPC pipeline writes a verified package", TestFo4Pipeline),
            ("preset-to-NPC pipeline installs the bound apply PEX", TestPipelineRuntimeScriptPackage),
            ("SSE preset-to-NPC pipeline refuses dropped appearance and accepts its bounded subset", TestSsePipeline),
            ("preset-to-NPC pipeline never overwrites output", TestPipelineExistingOutput),
            ("preset-to-NPC pipeline rejects cross-game formats", TestPipelineCrossGame),
            ("preset-to-NPC pipeline rolls back on invalid body morph", TestPipelineRollback),
            ("preset-to-NPC optional artifacts refuse outside-K inputs", TestPipelineOptionalOutsideK)
        };

        if (args is [Selector_FollowerFinishOnly])
        {
            tests = tests.Where(test =>
                    test.Name.Contains(
                        "follower-finish",
                        StringComparison.Ordinal))
                .ToArray();
        }
        else if (args is [Selector_HairRegionsOnly])
        {
            tests = tests.Where(test =>
                    test.Name.Contains(
                        "hair-region",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else if (args is [Selector_PluginAuditOnly])
        {
            tests = tests.Where(test =>
                    test.Name == "Plugin audit compares the typed record surface and refuses unsafe roots")
                .ToArray();
        }
        else if (args is [Selector_ActorAssemblyOnly])
        {
            tests = tests.Where(test => test.Name.Contains("Actor Assembly", StringComparison.Ordinal)).ToArray();
        }

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

    private static Task TestParsing()
    {
        var command = CommandLine.Parse(["workspace", "preflight", "--json", "--output-root", "K:\\output"]);
        Assert(command.Name == "workspace preflight", "Two-word command was not retained.");
        Assert(command.Json, "JSON flag was not parsed.");
        Assert(command.Options["output-root"] == "K:\\output", "Option value was not parsed.");
        return Task.CompletedTask;
    }

    private static Task TestThreeWordParsing()
    {
        var command = CommandLine.Parse(["body", "sidecar", "inspect", "--json", "--game", "sse", "--file", "K:\\sidecar.bssliders"]);
        Assert(command.Name == "body sidecar inspect", "Three-word command was not retained.");
        Assert(command.Json, "JSON flag was not parsed for the three-word command.");
        Assert(command.Options["game"] == "sse", "Game option was not parsed for the three-word command.");
        Assert(command.Options["file"] == "K:\\sidecar.bssliders", "File option was not parsed for the three-word command.");
        return Task.CompletedTask;
    }

    private static Task TestCliRunnerCompositionBoundary()
    {
        var constructors = typeof(CliRunner).GetConstructors();
        Assert(constructors.Length == 1, "CLI runner exposes more than one composition path.");
        var parameters = constructors[0].GetParameters();
        Assert(parameters.Length == 3, "CLI runner constructor grew beyond services plus output streams.");
        Assert(parameters[0].ParameterType == typeof(CliRunnerServices),
            "CLI runner does not receive the typed service bundle.");
        Assert(parameters[1].ParameterType == typeof(TextWriter) && parameters[2].ParameterType == typeof(TextWriter),
            "CLI runner output boundaries are not explicit text writers.");
        return Task.CompletedTask;
    }

    private static Task TestFormReferenceParsing()
    {
        Assert(FormReference.TryParse("M3FixtureFO4.esp|0x00000801", out var reference),
            "A qualified FormReference did not parse.");
        Assert(reference.Plugin.Value == "M3FixtureFO4.esp" && reference.FormId.Value == 0x801,
            "A qualified FormReference lost its typed parts.");
        Assert(reference.ToString() == "M3FixtureFO4.esp|0x00000801",
            "A qualified FormReference did not round-trip canonically.");
        Assert(!FormReference.TryParse("M3FixtureFO4.esp|0x801|extra", out _),
            "A FormReference with duplicate separators was accepted.");
        Assert(!FormReference.TryParse("M3FixtureFO4.esp|not-a-form-id", out _),
            "A malformed FormReference was accepted.");
        return Task.CompletedTask;
    }

    private static Task TestNpcLevelUnion()
    {
        Assert(new NpcLevelValue(NpcLevelMode.Fixed, 42).TryGetRaw(out var fixedRaw) && fixedRaw == 42,
            "Fixed level did not encode as an unsigned raw level.");
        Assert(new NpcLevelValue(NpcLevelMode.Multiplier, 1.234m).TryGetRaw(out var multiplierRaw) && multiplierRaw == 1234,
            "Multiplier did not encode at thousandths precision.");
        Assert(!new NpcLevelValue(NpcLevelMode.Multiplier, 65.536m).TryGetRaw(out _),
            "Multiplier upper bound was not enforced.");
        return Task.CompletedTask;
    }

    private static Task TestNpcFlagMasks()
    {
        Assert(NpcFlagExtensions.TryGetMask(GameEdition.Fallout4, NpcFlag.Fallout4NoActivationOrHellos, out var fo4) && fo4 == 0x800000,
            "FO4-only flag mask changed.");
        Assert(!NpcFlagExtensions.TryGetMask(GameEdition.SkyrimSpecialEdition, NpcFlag.Fallout4NoActivationOrHellos, out _),
            "FO4-only flag leaked into Skyrim.");
        Assert(NpcFlagExtensions.TryGetMask(GameEdition.SkyrimSpecialEdition, NpcFlag.SkyrimUseTemplate, out var sse) && sse == 0x80,
            "Skyrim template flag mask changed.");
        return Task.CompletedTask;
    }

    private static Task TestNpcKeywordContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var first), "Keyword reference did not parse.");
        Assert(FormReference.TryParse("M3.esp|0x00000802", out var second), "Second keyword reference did not parse.");
        var patch = new NpcKeywordPatch(new NpcKeywordListPatch([second, first], [], []), new NpcKeywordListPatch([first], [], []));
        Assert(!patch.IsEmpty && patch.Keywords.Replace!.Value.SequenceEqual([second, first]), "Keyword replacement order was not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcFactionContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var first), "Faction reference did not parse.");
        Assert(FormReference.TryParse("M3.esp|0x00000802", out var second), "Second faction reference did not parse.");
        var patch = new NpcFactionPatch(null,
            [new NpcFactionEntry(first, -128)],
            [new NpcFactionEntry(second, 127)],
            [first]);
        Assert(!patch.IsEmpty && patch.Add[0].Rank == -128 && patch.Update[0].Rank == 127,
            "Faction signed-byte ranks or operation order were not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcInventoryContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var first), "Inventory reference did not parse.");
        Assert(FormReference.TryParse("M3.esp|0x00000802", out var second), "Second inventory reference did not parse.");
        var patch = new NpcInventoryPatch(null,
            [new NpcInventoryEntry(first, int.MaxValue), new NpcInventoryEntry(first, -1)],
            [new NpcInventoryEntry(second, int.MinValue)],
            [first]);
        Assert(!patch.IsEmpty && patch.Add[0].Count == int.MaxValue && patch.Add[1].Count == -1 &&
            patch.Update[0].Count == int.MinValue && patch.Remove[0] == first,
            "Inventory signed 32-bit counts or operation order were not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcOutfitContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var outfit), "Outfit reference did not parse.");
        var patch = new NpcOutfitPatch(OptionalFormReference.Set(outfit), OptionalFormReference.Clear());
        Assert(!patch.IsEmpty && patch.DefaultOutfit.Value == outfit && patch.SleepingOutfit.IsSpecified &&
            patch.SleepingOutfit.Value is null, "Outfit optional set/clear semantics were not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcPerkContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var first), "Perk reference did not parse.");
        Assert(FormReference.TryParse("M3.esp|0x00000802", out var second), "Second perk reference did not parse.");
        var patch = new NpcPerkPatch(null,
            [new NpcPerkEntry(first, 0)],
            [new NpcPerkEntry(second, byte.MaxValue)],
            [first]);
        Assert(!patch.IsEmpty && patch.Add[0].Rank == 0 && patch.Update[0].Rank == byte.MaxValue &&
            patch.Remove[0] == first, "Perk unsigned-byte ranks or operation order were not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcActorEffectContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var first), "Actor-effect reference did not parse.");
        Assert(FormReference.TryParse("M3.esp|0x00000802", out var second), "Second actor-effect reference did not parse.");
        var patch = new NpcActorEffectPatch([second, first], [], [first]);
        Assert(!patch.IsEmpty && patch.Replace!.Value.SequenceEqual([second, first]) && patch.Remove[0] == first,
            "Actor-effect replacement order or remove operation was not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcPropertyContracts()
    {
        Assert(FormReference.TryParse("M3.esp|0x00000801", out var first), "Property actor-value reference did not parse.");
        Assert(FormReference.TryParse("M3.esp|0x00000802", out var second), "Second property actor-value reference did not parse.");
        var patch = new NpcPropertyPatch(null,
            [new NpcPropertyEntry(first, 1.25f)],
            [new NpcPropertyEntry(second, -2.5f)],
            [first]);
        Assert(!patch.IsEmpty && patch.Add[0].Value == 1.25f && patch.Update[0].Value == -2.5f &&
            patch.Remove[0] == first, "Property float values or operation order were not retained.");
        return Task.CompletedTask;
    }

    private static Task TestNpcTemplateContracts()
    {
        Assert(NpcTemplateCategory.Stats.ToWireName() == "stats" && NpcTemplateCategory.Keywords.ToWireName() == "keywords",
            "Template category wire names changed.");
        Assert((int)NpcTemplateCategory.Traits == 0 && (int)NpcTemplateCategory.Keywords == 12,
            "Template category bit order changed.");
        Assert(NpcTemplateCategoryExtensions.TryParseWireName("spell-list", out var category) && category == NpcTemplateCategory.SpellList,
            "Template category parser did not accept the stable spell-list name.");
        return Task.CompletedTask;
    }

    private static Task TestNpcResetContracts()
    {
        Assert(NpcResetSection.Identity.ToWireName() == "identity" &&
               NpcResetSection.ActorEffects.ToWireName() == "actor-effects" &&
               NpcResetSection.Properties.ToWireName() == "properties",
            "NPC reset section wire names changed.");
        Assert(NpcResetSectionExtensions.TryParseWireName("factions", out var section) &&
               section == NpcResetSection.Factions, "NPC reset section parser did not accept factions.");
        return Task.CompletedTask;
    }

    private static Task TestNpcFaceContracts()
    {
        Assert(NpcHeadPartType.Face.ToWireName() == "face" &&
               NpcHeadPartType.FacialHair.ToWireName() == "facial-hair" &&
               NpcHeadPartType.HeadRear.ToWireName() == "head-rear",
            "Headpart wire names changed.");
        Assert(NpcHeadPartTypeExtensions.TryParseWireName("eyebrows", out var type) &&
               type == NpcHeadPartType.Eyebrows,
            "Headpart parser did not accept the stable eyebrows name.");
        Assert(FormReference.TryParse("P04SSE.esp|0x00000811", out var reference),
            "Face FormReference did not parse.");
        var selection = new NpcHeadPartSelection(reference, NpcHeadPartType.Face);
        var patch = new NpcFacePatch([selection], OptionalFormReference.Clear());
        Assert(!patch.IsEmpty && patch.HeadParts!.Value[0] == selection && patch.HairColor.IsSpecified &&
               patch.HairColor.Value is null,
            "Face patch set/clear semantics were not retained.");
        return Task.CompletedTask;
    }

    private static Task TestCharGenOptionsContracts()
    {
        var fo4 = CharGenOptionsDefaults.For(GameEdition.Fallout4);
        var sse = CharGenOptionsDefaults.For(GameEdition.SkyrimSpecialEdition);
        Assert(fo4.Convention.Diffuse.WorkingSpace == FaceTintWorkingSpace.G22 &&
               fo4.ApplyEyebrowsFixedColor && !fo4.BakeSseRaceMenuOverlays,
            "Fallout 4 CharGen defaults drifted from the pinned options form.");
        Assert(sse.Convention.Diffuse.WorkingSpace == FaceTintWorkingSpace.Linear &&
               sse.Convention.Diffuse.MaskChannel == FaceTintMaskChannel.R &&
               sse.NormalCompression == FaceGenNormalSpecularCompression.Uncompressed &&
               sse.BakeSseRaceMenuOverlays && !sse.ApplyEyebrowsFixedColor,
            "Skyrim SE CharGen defaults drifted from the pinned options form.");
        Assert(fo4.TintSort.TintRules[0].Key == (int)FaceTintFo4SortKey.GroupIndex &&
               sse.TintSort.TintRules[0].Key == (int)FaceTintSseSortKey.RaceOrder,
            "Game-specific tint-order defaults were not kept separate.");
        return Task.CompletedTask;
    }

    private static async Task TestCharGenOptionsCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-chargen-options-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var input = new WorkspacePath(Path.Combine(root.Value, "fo4-options.json"));
        var output = new WorkspacePath(Path.Combine(root.Value, "fo4-options-canonical.json"));
        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        await File.WriteAllTextAsync(input.Value, JsonSerializer.Serialize(CharGenOptionsDefaults.For(GameEdition.Fallout4), serializerOptions));
        var (runner, consoleOutput, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "options", "--game", "fallout4", "--input", input.Value,
            "--output", output.Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(consoleOutput.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
            "CharGen options CLI did not write a canonical artifact.");
        var second = await runner.RunAsync(CommandLine.Parse(["facegen", "options", "--game", "fallout4", "--input", input.Value,
            "--output", output.Value, "--apply", "--json"]), CancellationToken.None);
        Assert(second == CommandExitCode.ValidationFailure && consoleOutput.ToString().Contains("facegen-options-output-exists", StringComparison.Ordinal),
            "CharGen options CLI overwrote an existing artifact.");
        var stale = await runner.RunAsync(CommandLine.Parse(["facegen", "options", "--game", "fallout4", "--input", input.Value,
            "--expected-sha256", new string('0', 64), "--json"]), CancellationToken.None);
        Assert(stale == CommandExitCode.ValidationFailure && consoleOutput.ToString().Contains("facegen-options-stale-input", StringComparison.Ordinal),
            "CharGen options CLI accepted a stale input hash.");
        var invalidPath = new WorkspacePath(Path.Combine(root.Value, "sse-invalid.json"));
        var invalid = CharGenOptionsDefaults.For(GameEdition.SkyrimSpecialEdition) with { ApplyMouthVanillaFix = true };
        var invalidJson = JsonSerializer.Serialize(invalid, serializerOptions).Replace("skyrimSpecialEdition", "skyrimse", StringComparison.Ordinal);
        await File.WriteAllTextAsync(invalidPath.Value, invalidJson);
        var invalidExit = await runner.RunAsync(CommandLine.Parse(["facegen", "options", "--game", "skyrimse", "--input", invalidPath.Value, "--json"]), CancellationToken.None);
        Assert(invalidExit == CommandExitCode.ValidationFailure && consoleOutput.ToString().Contains("facegen-options-fo4-only", StringComparison.Ordinal),
            "CharGen options CLI accepted a Fallout 4-only fix for Skyrim SE.");
        DeleteDirectory(root.Value);
    }

    private static Task TestFaceTintContracts()
    {
        var palette = new NpcFaceTintLayer(NpcFaceTintDataType.ValueColor, 0x30, 50,
            new NpcFaceTintColor(1, 2, 3), -1, "MgECAwD//w==");
        var texture = new NpcFaceTintLayer(NpcFaceTintDataType.TextureSet, 0x31, 20, null, null, "FA==");
        var patch = new NpcFaceTintPatch([palette, texture]);
        Assert(!patch.IsEmpty && patch.Layers[0].Color!.Red == 1 && patch.Layers[1].RawTendBase64 == "FA==",
            "Face-tint typed fields did not retain palette and raw TextureSet semantics.");
        Assert(new NpcFaceTintPatch([]).IsEmpty, "An explicit empty face-tint list must represent the upstream Remove action.");
        return Task.CompletedTask;
    }

    private static async Task TestFaceTintCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-face-tint-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "P04TintTest.esp"));
        var output = new WorkspacePath(Path.Combine(root.Value, "out", "P04TintTest.esp"));
        Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
        await File.WriteAllBytesAsync(source.Value, BuildFaceTintPlugin());
        var json = "[{\"dataType\":\"texture-set\",\"optionIndex\":49,\"value\":40,\"rawTendBase64\":\"KA==\"},{\"dataType\":\"value-color\",\"optionIndex\":48,\"value\":75,\"color\":{\"red\":9,\"green\":8,\"blue\":7},\"templateColorIndex\":-1,\"rawTendBase64\":\"SwkIBwD//w==\"}]";
        var (runner, outputText, errorText) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "fallout4", "--plugin", source.Value,
            "--output", output.Value, "--npc", "0x800", "--layers", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(outputText.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
            "FO4 face-tint CLI did not apply the ordered TETI/TEND patch.");
        var existing = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "fallout4", "--plugin", source.Value,
            "--output", output.Value, "--npc", "0x800", "--layers", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(existing == CommandExitCode.ValidationFailure && errorText.ToString().Length == 0 && outputText.ToString().Contains("output-exists", StringComparison.Ordinal),
            "FO4 face-tint CLI overwrote or failed to diagnose an existing output.");
        var sseOutput = new WorkspacePath(Path.Combine(root.Value, "sse", "P04TintTest.esp")); Directory.CreateDirectory(Path.GetDirectoryName(sseOutput.Value)!);
        var sse = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", sseOutput.Value, "--npc", "0x800", "--layers", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(sse != CommandExitCode.Success && !File.Exists(sseOutput.Value) && errorText.ToString().Length > 0,
            "An SSE face-tint request carrying the FO4 layer schema was not refused explicitly.");
        DeleteDirectory(root.Value);
    }

    private static Task TestSseFaceMorphContracts()
    {
        var patch = new SkyrimFaceMorphPatch(
            Enumerable.Range(0, 18).Select(index => (float)index / 18F).ToImmutableArray(),
            float.MaxValue,
            [uint.MaxValue, 3, uint.MaxValue, 15]);
        Assert(patch.Nam9Sliders.Length == 18 && patch.NamaValues.Length == 4,
            "SSE face-morph contracts changed their fixed native shapes.");
        Assert(patch.Nam9Trailing == float.MaxValue && patch.NamaValues[0] == uint.MaxValue && patch.NamaValues[3] == 15,
            "SSE face-morph contracts did not preserve the trailing engine value and unset family sentinel.");
        return Task.CompletedTask;
    }

    private static async Task TestPresetFaceTintCompositionGuard()
    {
        IFaceTintBuildService guard =
            NpcManager.Cli.Program.CreatePresetFaceTintBuildGuard();
        Assert(guard is ExactOnlyFaceTintBuildService,
            "Preset CLI composition exposed a generated or process-capable FaceTint service.");

        string outputRoot = Path.Combine(
            ExampleWorkspaceRoot, "projects", "NpcManagerReimplementation", "03-builds", "work",
            $"cli-exact-only-guard-{Environment.ProcessId}-{Guid.NewGuid():N}");
        FaceTintBuildResult result = await guard.BuildAsync(
            new FaceTintBuildRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(Path.Combine(outputRoot, "generated-facetint.json")),
                new WorkspacePath(Path.Combine(outputRoot, "facetint-evidence.json")),
                new FormId(0x00000800),
                2048,
                FaceTintOutputFormat.Bgra8,
                1,
                FaceTintAlphaMode.Preserve,
                new WorkspacePath(Path.Combine(outputRoot, "00000800.dds")),
                new WorkspacePath(Path.Combine(outputRoot, "providers"))),
            CancellationToken.None);

        Assert(!result.Written && result.Artifact is null &&
               result.OutputSha256 is null && result.TextureOutputSha256 is null &&
               result.Diagnostics.Length == 1 &&
               result.Diagnostics[0] is
               {
                   Code: "facetint-exact-source-required",
                   Severity: DiagnosticSeverity.Error
               } &&
               !Directory.Exists(outputRoot),
            "Preset CLI composition performed or admitted generated FaceTint work.");
    }

    private static async Task TestSseFaceMorphPluginLocalFormId()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-sse-face-morph-formid-regression");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new WorkspacePath(Path.Combine(root.Value, "EightMasterMorphTest.esp"));

        try
        {
            var bytes = BuildSseFaceMorphPluginWithEightMasters();
            await File.WriteAllBytesAsync(plugin.Value, bytes);

            var groupOffset = FindAscii(bytes, "GRUP");
            var masterCount = 0;
            for (var cursor = 24; cursor < groupOffset;)
            {
                var signature = Encoding.ASCII.GetString(bytes, cursor, 4);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 4, 2));
                if (signature == "MAST") masterCount++;
                cursor = checked(cursor + 6 + length);
            }
            var npcOffset = groupOffset + 24;
            Assert(masterCount == 8,
                "Eight-master regression fixture does not contain exactly eight valid MAST subrecords.");
            Assert(groupOffset >= 0 && FindAscii(bytes, "NPC_", npcOffset) == npcOffset,
                "Eight-master regression fixture does not contain the expected NPC group and record.");
            Assert(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(npcOffset + 12, 4)) == 0x08000800U,
                "Eight-master regression fixture lost its raw self-index byte or local 0x800 FormID.");

            var snapshot = BethesdaSkyrimFaceMorphAdapter.Read(
                GameEdition.SkyrimSpecialEdition,
                plugin,
                new FormId(0x800));
            Assert(snapshot.HasNam9 && snapshot.HasNama && snapshot.Nam9Trailing == 42.25F &&
                   snapshot.NamaValues.SequenceEqual([uint.MaxValue, 2U, uint.MaxValue, 4U]),
                "Plugin-local FormID 0x800 did not resolve the exact NAM9 trailing authority from raw 0x08000800.");

            var differentLocalIdRefused = false;
            try
            {
                _ = BethesdaSkyrimFaceMorphAdapter.Read(
                    GameEdition.SkyrimSpecialEdition,
                    plugin,
                    new FormId(0x801));
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("found 0", StringComparison.Ordinal))
            {
                differentLocalIdRefused = true;
            }

            Assert(differentLocalIdRefused,
                "The face-morph reader accepted a different plugin-local FormID than the raw record owns.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestSseFaceMorphCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-sse-face-morph-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "P04SseMorphTest.esp"));
        var output = new WorkspacePath(Path.Combine(root.Value, "out", "P04SseMorphTest.esp"));
        Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
        await File.WriteAllBytesAsync(source.Value, BuildSseFaceMorphPlugin());
        var json = "{\"nam9\":[0.25,-0.25,0,0.75,-0.5,0.1,-0.1,0.2,-0.2,0.3,-0.3,0.4,-0.4,0.5,-0.5,0.6,-0.6,0.7],\"nam9Trailing\":3.4028235E+38,\"nama\":[4294967295,3,4294967295,15]}";
        var (runner, outputText, errorText) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["face", "morph", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", output.Value, "--npc", "0x800", "--vanilla", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(outputText.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
            "SSE face-morph CLI did not apply typed NAM9/NAMA values.");
        var snapshot = BethesdaSkyrimFaceMorphAdapter.Read(GameEdition.SkyrimSpecialEdition, output, new FormId(0x800));
        ImmutableArray<uint> expectedNama = [uint.MaxValue, 3, uint.MaxValue, 15];
        Assert(snapshot.Nam9Trailing == float.MaxValue && snapshot.NamaValues.SequenceEqual(expectedNama),
            "SSE face-morph output did not preserve the trailing value and unset sentinel.");
        var existing = await runner.RunAsync(CommandLine.Parse(["face", "morph", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", output.Value, "--npc", "0x800", "--vanilla", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(existing == CommandExitCode.ValidationFailure && errorText.ToString().Length == 0 && outputText.ToString().Contains("output-exists", StringComparison.Ordinal),
            "SSE face-morph CLI overwrote or failed to diagnose an existing output.");
        var wrongGameOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-game", "P04SseMorphTest.esp"));
        Directory.CreateDirectory(Path.GetDirectoryName(wrongGameOutput.Value)!);
        var wrongGame = await runner.RunAsync(CommandLine.Parse(["face", "morph", "patch", "--game", "fallout4", "--plugin", source.Value,
            "--output", wrongGameOutput.Value, "--npc", "0x800", "--vanilla", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(wrongGame == CommandExitCode.ValidationFailure && !File.Exists(wrongGameOutput.Value) && outputText.ToString().Contains("face-morph-game-unsupported", StringComparison.Ordinal),
            "FO4 face-morph request was not refused explicitly.");
        var malformedAtOutput = new WorkspacePath(Path.Combine(root.Value, "malformed-at", "P04SseMorphTest.esp"));
        Directory.CreateDirectory(Path.GetDirectoryName(malformedAtOutput.Value)!);
        var malformedAt = await runner.RunAsync(CommandLine.Parse(["face", "morph", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", malformedAtOutput.Value, "--npc", "0x800", "--vanilla", "@", "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(malformedAt == CommandExitCode.UsageError && !File.Exists(malformedAtOutput.Value) && errorText.ToString().Contains("usage-error", StringComparison.Ordinal),
            "Malformed @-file morph input did not return a usage error.");
        DeleteDirectory(root.Value);
    }

    private static Task TestSseFaceTintContracts()
    {
        var layer = new SkyrimFaceTintLayer(24, 90, 80, 70, 128, 75, -2);
        var patch = new SkyrimFaceTintPatch([layer]);
        Assert(!patch.IsEmpty && patch.Layers.Length == 1 && patch.Layers[0].Index == 24 &&
            patch.Layers[0].Alpha == 128 && patch.Layers[0].Coverage == 75 && patch.Layers[0].PresetIndex == -2,
            "SSE face-tint contracts did not retain the typed index, RGB/alpha, coverage, and TIAS fields.");
        return Task.CompletedTask;
    }

    private static async Task TestSseFaceTintCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-sse-face-tint-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "P04SseTintTest.esp"));
        var output = new WorkspacePath(Path.Combine(root.Value, "out", "P04SseTintTest.esp"));
        Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
        await File.WriteAllBytesAsync(source.Value, BuildSseFaceTintPlugin());
        var json = "[{\"index\":4,\"red\":12,\"green\":34,\"blue\":56,\"alpha\":255,\"coverage\":25,\"presetIndex\":0},{\"index\":24,\"red\":90,\"green\":80,\"blue\":70,\"alpha\":128,\"coverage\":75,\"presetIndex\":-2}]";
        var (runner, outputText, errorText) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", output.Value, "--npc", "0x800", "--layers", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(outputText.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
            "SSE face-tint CLI did not apply typed TINI/TINC/TINV/TIAS layers.");
        var snapshot = BethesdaSkyrimFaceTintAdapter.Read(GameEdition.SkyrimSpecialEdition, output, new FormId(0x800));
        Assert(snapshot.Layers.SequenceEqual([
            new SkyrimFaceTintLayer(4, 12, 34, 56, 255, 25, 0),
            new SkyrimFaceTintLayer(24, 90, 80, 70, 128, 75, -2)]),
            "SSE face-tint output did not preserve ordered typed layer semantics.");
        var existing = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", output.Value, "--npc", "0x800", "--layers", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(existing == CommandExitCode.ValidationFailure && errorText.ToString().Length == 0 && outputText.ToString().Contains("output-exists", StringComparison.Ordinal),
            "SSE face-tint CLI overwrote or failed to diagnose an existing output.");
        Directory.CreateDirectory(Path.Combine(root.Value, "duplicate"));
        var duplicate = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", Path.Combine(root.Value, "duplicate", "P04SseTintTest.esp"), "--npc", "0x800",
            "--layers", "[{\"index\":4,\"red\":1,\"green\":1,\"blue\":1,\"alpha\":255,\"coverage\":1,\"presetIndex\":0},{\"index\":4,\"red\":2,\"green\":2,\"blue\":2,\"alpha\":255,\"coverage\":2,\"presetIndex\":0}]", "--json"]), CancellationToken.None);
        Assert(duplicate == CommandExitCode.ValidationFailure && outputText.ToString().Contains("sse-face-tint-duplicate", StringComparison.Ordinal),
            "Duplicate SSE face-tint indexes were not rejected.");
        Directory.CreateDirectory(Path.Combine(root.Value, "texture"));
        var texture = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "skyrimse", "--plugin", source.Value,
            "--output", Path.Combine(root.Value, "texture", "P04SseTintTest.esp"), "--npc", "0x800",
            "--layers", "[{\"index\":4,\"red\":1,\"green\":1,\"blue\":1,\"alpha\":255,\"coverage\":1,\"presetIndex\":0,\"texture\":\"warpaint.dds\"}]", "--json"]), CancellationToken.None);
        Assert(texture == CommandExitCode.UsageError && errorText.ToString().Contains("texture is not persisted", StringComparison.Ordinal),
            "Unpersistable RaceMenu tint texture authority was not refused explicitly.");
        var wrongGameOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-game", "P04SseTintTest.esp"));
        Directory.CreateDirectory(Path.GetDirectoryName(wrongGameOutput.Value)!);
        var wrongGame = await runner.RunAsync(CommandLine.Parse(["face", "tint", "patch", "--game", "fallout4", "--plugin", source.Value,
            "--output", wrongGameOutput.Value, "--npc", "0x800", "--layers", json, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(wrongGame == CommandExitCode.UsageError && !File.Exists(wrongGameOutput.Value),
            "FO4 face-tint route did not refuse an SSE-shaped layer document.");
        DeleteDirectory(root.Value);
    }

    private static Task TestRaceMenuExtendedMorphContracts()
    {
        var patch = new RaceMenuExtendedMorphPatch([new RaceMenuExtendedMorph("CME_EyeballUpDown", -0.5F)]);
        Assert(patch.Morphs.Length == 1 && patch.Morphs[0].Name == "CME_EyeballUpDown" && patch.Morphs[0].Value == -0.5F,
            "RaceMenu extended morph contracts did not retain the typed name/value pair.");
        return Task.CompletedTask;
    }

    private static async Task TestRaceMenuExtendedMorphCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-sse-extended-morph-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "P04ExtendedMorphTest.jslot"));
        var output = new WorkspacePath(Path.Combine(root.Value, "out", "P04ExtendedMorphTest.jslot"));
        Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
        await File.WriteAllTextAsync(source.Value, "{\"version\":1,\"unknown\":{\"keep\":[1,2]},\"customMorphs\":[{\"name\":\"Known\",\"value\":0.1},{\"name\":\"Remove\",\"value\":0.25}]}");
        var patchJson = "{\"morphs\":[{\"name\":\"Known\",\"value\":0.5},{\"name\":\"Remove\",\"value\":0},{\"name\":\"Uncatalogued\",\"value\":-0.4}]}";
        var (runner, outputText, errorText) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["face", "morph", "extended", "--game", "skyrimse", "--input", source.Value,
            "--output", output.Value, "--extended", patchJson, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(outputText.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
            "RaceMenu extended morph CLI did not apply the typed customMorphs patch.");
        using var outputDocument = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
        Assert(outputDocument.RootElement.TryGetProperty("unknown", out var unknown) && unknown.GetProperty("keep").GetArrayLength() == 2,
            "RaceMenu extended morph patch dropped an unrelated root JSON field.");
        var custom = outputDocument.RootElement.GetProperty("customMorphs");
        Assert(custom.GetArrayLength() == 2 && custom.EnumerateArray().Any(item => item.GetProperty("name").GetString() == "Known" && item.GetProperty("value").GetSingle() == 0.5F) &&
            custom.EnumerateArray().Any(item => item.GetProperty("name").GetString() == "Uncatalogued" && item.GetProperty("value").GetSingle() == -0.4F) &&
            !custom.EnumerateArray().Any(item => item.GetProperty("name").GetString() == "Remove"),
            "RaceMenu extended morph patch did not update, add, and zero-remove custom channels deterministically.");
        Assert(response.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "extended-morph-uncatalogued"),
            "RaceMenu extended morph patch silently accepted an uncatalogued name.");
        var existing = await runner.RunAsync(CommandLine.Parse(["face", "morph", "extended", "--game", "skyrimse", "--input", source.Value,
            "--output", output.Value, "--extended", patchJson, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(existing == CommandExitCode.ValidationFailure && errorText.ToString().Length == 0 && outputText.ToString().Contains("output-exists", StringComparison.Ordinal),
            "RaceMenu extended morph CLI overwrote or failed to diagnose an existing output.");
        var wrongGameOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-game", "P04ExtendedMorphTest.jslot"));
        Directory.CreateDirectory(Path.GetDirectoryName(wrongGameOutput.Value)!);
        var wrongGame = await runner.RunAsync(CommandLine.Parse(["face", "morph", "extended", "--game", "fallout4", "--input", source.Value,
            "--output", wrongGameOutput.Value, "--extended", patchJson, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(wrongGame == CommandExitCode.ValidationFailure && !File.Exists(wrongGameOutput.Value) && outputText.ToString().Contains("extended-morph-game-unsupported", StringComparison.Ordinal),
            "FO4 RaceMenu extended morph request was not refused explicitly.");
        var duplicate = await runner.RunAsync(CommandLine.Parse(["face", "morph", "extended", "--game", "skyrimse", "--input", source.Value,
            "--output", wrongGameOutput.Value, "--extended", "{\"morphs\":[{\"name\":\"Known\",\"value\":0},{\"name\":\"known\",\"value\":1}]}", "--json"]), CancellationToken.None);
        Assert(duplicate == CommandExitCode.ValidationFailure && outputText.ToString().Contains("extended-morph-duplicate", StringComparison.Ordinal),
            "Duplicate RaceMenu extended morph names were not rejected.");
        DeleteDirectory(root.Value);
    }

    private static Task TestRaceMenuSculptContracts()
    {
        var patch = new RaceMenuSculptPatch(10_000,
            [new RaceMenuSculptPart("FemaleHeadCharGen.tri", 120,
                [new RaceMenuSculptVertex(3, 0.01F, -0.02F, 0.03F)])]);
        Assert(patch.SculptDivisor == 10_000 && patch.Parts.Length == 1 && patch.Parts[0].Host == "FemaleHeadCharGen.tri" &&
            patch.Parts[0].Vertices[0].Index == 3 && patch.Parts[0].Vertices[0].Dz == 0.03F,
            "RaceMenu sculpt contracts did not retain typed host/index/delta semantics.");
        return Task.CompletedTask;
    }

    private static async Task TestRaceMenuSculptCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-sse-sculpt-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "P04SculptTest.jslot"));
        var output = new WorkspacePath(Path.Combine(root.Value, "out", "P04SculptTest.jslot"));
        Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
        await File.WriteAllTextAsync(source.Value,
            "{\"version\":4,\"unknown\":{\"keep\":[1,2]},\"morphs\":{\"default\":{\"morphs\":[0.1]},\"custom\":[{\"name\":\"Keep\",\"value\":0.2}],\"sculptDivisor\":10000,\"sculpt\":[{\"host\":\"FemaleHeadCharGen.tri\",\"vertices\":120,\"data\":[[3,100,-200,300]]}]}}");
        var patchJson = """{"divisor":20000,"parts":[{"host":"FemaleHeadCharGen.tri","vertices":120,"verts":[{"index":3,"dx":0.01,"dy":-0.02,"dz":0.03}]},{"host":"FemaleHeadBrowsCharGen.tri","vertices":80,"verts":[{"index":2,"dx":-0.005,"dy":0.004,"dz":0}]}]}""";
        var (runner, outputText, errorText) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["face", "sculpt", "patch", "--game", "skyrimse", "--input", source.Value,
            "--output", output.Value, "--sculpt", patchJson, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(outputText.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
            "RaceMenu sculpt CLI did not apply the typed sidecar patch.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
        var morphs = document.RootElement.GetProperty("morphs");
        Assert(morphs.GetProperty("sculptDivisor").GetInt32() == 20_000 && morphs.GetProperty("default").GetProperty("morphs")[0].GetSingle() == 0.1F,
            "RaceMenu sculpt patch changed divisor incorrectly or dropped unrelated morphs.");
        var parts = morphs.GetProperty("sculpt");
        Assert(parts.GetArrayLength() == 2 && parts[0].GetProperty("data")[0][1].GetInt32() == 200 &&
            parts[0].GetProperty("data")[0][2].GetInt32() == -400 && parts[0].GetProperty("data")[0][3].GetInt32() == 600 &&
            parts[1].GetProperty("host").GetString() == "FemaleHeadBrowsCharGen.tri" &&
            document.RootElement.GetProperty("unknown").GetProperty("keep").GetArrayLength() == 2,
            "RaceMenu sculpt patch did not write scaled per-shape integer rows or preserve unknown fields.");
        var existing = new WorkspacePath(Path.Combine(root.Value, "existing", "P04SculptTest.jslot"));
        Directory.CreateDirectory(Path.GetDirectoryName(existing.Value)!); File.Copy(output.Value, existing.Value);
        var existingExit = await runner.RunAsync(CommandLine.Parse(["face", "sculpt", "patch", "--game", "skyrimse", "--input", source.Value,
            "--output", existing.Value, "--sculpt", patchJson, "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(existingExit == CommandExitCode.ValidationFailure && outputText.ToString().Contains("output-exists", StringComparison.Ordinal),
            "RaceMenu sculpt CLI overwrote an existing output.");
        Directory.CreateDirectory(Path.Combine(root.Value, "duplicate"));
        var duplicateExit = await runner.RunAsync(CommandLine.Parse(["face", "sculpt", "patch", "--game", "skyrimse", "--input", source.Value,
            "--output", Path.Combine(root.Value, "duplicate", "P04SculptTest.jslot"), "--sculpt",
            "{\"divisor\":10000,\"parts\":[{\"host\":\"Head.tri\",\"vertices\":10,\"verts\":[{\"index\":1,\"dx\":0,\"dy\":0,\"dz\":0},{\"index\":1,\"dx\":0,\"dy\":0,\"dz\":0}]}]}", "--json"]), CancellationToken.None);
        Assert(duplicateExit == CommandExitCode.ValidationFailure && outputText.ToString().Contains("sculpt-duplicate-index", StringComparison.Ordinal),
            "RaceMenu sculpt CLI accepted duplicate vertex indices.");
        Directory.CreateDirectory(Path.Combine(root.Value, "empty"));
        var emptyExit = await runner.RunAsync(CommandLine.Parse(["face", "sculpt", "patch", "--game", "skyrimse", "--input", source.Value,
            "--output", Path.Combine(root.Value, "empty", "P04SculptTest.jslot"), "--sculpt", "{\"divisor\":10000,\"parts\":[]}", "--json"]), CancellationToken.None);
        Assert(emptyExit == CommandExitCode.ValidationFailure && outputText.ToString().Contains("sculpt-no-applicable-shapes", StringComparison.Ordinal),
            "RaceMenu sculpt CLI accepted a patch with zero applicable shapes.");
        var wrongGameExit = await runner.RunAsync(CommandLine.Parse(["face", "sculpt", "patch", "--game", "fallout4", "--input", source.Value,
            "--output", Path.Combine(root.Value, "wrong-game", "P04SculptTest.jslot"), "--sculpt", patchJson, "--json"]), CancellationToken.None);
        Assert(wrongGameExit == CommandExitCode.ValidationFailure && errorText.ToString().Length == 0 && outputText.ToString().Contains("sculpt-game-unsupported", StringComparison.Ordinal),
            "RaceMenu sculpt CLI did not refuse Fallout 4.");
        DeleteDirectory(root.Value);
    }

    private static Task TestFacePoseContracts()
    {
        var bone = new FaceBoneRegionBone("Brow", new FacePoseVector(-1, -2, -3), new FacePoseVector(1, 2, 3),
            new FacePoseVector(0, 0, 0), new FacePoseVector(0, 0, 0), new FacePoseVector(-0.1F, -0.2F, -0.3F),
            new FacePoseVector(0.1F, 0.2F, 0.3F));
        var region = new FaceBoneRegion(4, "Brow", new FacePoseVector(0, 0, 0), new FacePoseVector(0, 0, 0), new FacePoseVector(0, 0, 0), [bone]);
        var channel = new FaceVertexMorphChannel("face", "Smile", 0.5F,
            [new FaceVertexDelta(2, new FacePoseVector(1, 2, 3))]);
        var input = new FacePoseInput(1, GameEdition.Fallout4, new FormId(0x800), 2, [region],
            [new FaceMorphSlider(4, new FacePoseVector(0.5F, 0, 0), new FacePoseVector(0, 0, 0), -0.5F)], [channel]);
        Assert(input.Regions[0].Bones[0].Bone == "Brow" && input.VertexMorphs[0].Weight == 0.5F &&
            input.FaceMorphs[0].RegionId == 4, "Face-pose contracts did not retain typed region/channel semantics.");
        return Task.CompletedTask;
    }

    private static async Task TestFacePoseCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-face-pose-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var preset = new WorkspacePath(Path.Combine(root.Value, "pose.json"));
        await File.WriteAllTextAsync(preset.Value, """
            {
              "version": 1, "game": "fallout4", "npc": "0x00000800", "facialMorphIntensity": 2,
              "regions": [{
                "id": 4, "name": "Brow",
                "default": {"position": [0,0,0], "rotation": [0,0,0], "scale": [0,0,0]},
                "bones": [{"bone": "Brow", "min": {"position": [-1,-2,-3], "rotation": [0,0,0], "scale": [-0.1,-0.2,-0.3]}, "max": {"position": [1,2,3], "rotation": [0,0,0], "scale": [0.1,0.2,0.3]}}]
              }],
              "faceMorphs": [{"regionId": 4, "position": [0.5,0,0], "rotation": [0,0,0], "scale": -0.5}],
              "vertexMorphs": [
                {"resolver": "face", "name": "A", "weight": 0.5, "vertices": [{"index": 1, "delta": [2,0,0]}]},
                {"resolver": "body", "name": "B", "weight": 1, "vertices": [{"index": 1, "delta": [0,3,0]}, {"index": 2, "delta": [1,1,1]}]}
              ]
            }
            """);
        var (runner, output, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["face", "pose", "resolve", "--game", "fallout4", "--npc", "0x800", "--preset", preset.Value, "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(output.ToString());
        var rootElement = response.RootElement;
        Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && rootElement.GetProperty("resolved").GetBoolean(),
            "Face-pose CLI did not resolve the typed input.");
        var bone = rootElement.GetProperty("bonePoses")[0];
        Assert(bone.GetProperty("bone").GetString() == "skin_Brow" &&
            Math.Abs(bone.GetProperty("position").GetProperty("x").GetSingle() - 1.0F) < 0.0001F &&
            Math.Abs(bone.GetProperty("scale").GetProperty("z").GetSingle() - 0.7F) < 0.0001F,
            "FMRS FMIN/additive bone math was not reproduced.");
        var vertices = rootElement.GetProperty("vertexDeltas");
        Assert(vertices.GetArrayLength() == 2 && vertices[0].GetProperty("index").GetInt32() == 1 &&
            Math.Abs(vertices[0].GetProperty("delta").GetProperty("x").GetSingle() - 1.0F) < 0.0001F &&
            Math.Abs(vertices[0].GetProperty("delta").GetProperty("y").GetSingle() - 3.0F) < 0.0001F &&
            rootElement.GetProperty("combinationOrder").GetArrayLength() == 3,
            "MultiMorphResolver declaration-order vertex accumulation was not reproduced.");
        var unresolved = new WorkspacePath(Path.Combine(root.Value, "unresolved.json"));
        await File.WriteAllTextAsync(unresolved.Value, (await File.ReadAllTextAsync(preset.Value)).Replace("\"regionId\": 4", "\"regionId\": 99", StringComparison.Ordinal));
        var unresolvedExit = await runner.RunAsync(CommandLine.Parse(["face", "pose", "resolve", "--game", "fallout4", "--npc", "0x800", "--preset", unresolved.Value, "--json"]), CancellationToken.None);
        Assert(unresolvedExit == CommandExitCode.Success && output.ToString().Contains("face-pose-region-unresolved", StringComparison.Ordinal),
            "Unresolved FMRS regions were not warned and skipped as the pinned resolver does.");
        var duplicate = new WorkspacePath(Path.Combine(root.Value, "duplicate.json"));
        await File.WriteAllTextAsync(duplicate.Value, (await File.ReadAllTextAsync(preset.Value)).Replace("\"index\": 1, \"delta\": [0,3,0]", "\"index\": 1, \"delta\": [0,3,0], \"extra\": 1", StringComparison.Ordinal));
        var duplicateExit = await runner.RunAsync(CommandLine.Parse(["face", "pose", "resolve", "--game", "fallout4", "--npc", "0x800", "--preset", duplicate.Value, "--json"]), CancellationToken.None);
        Assert(duplicateExit == CommandExitCode.ValidationFailure && output.ToString().Contains("face-pose-format-invalid", StringComparison.Ordinal),
            "Malformed face-pose fields were not rejected.");
        DeleteDirectory(root.Value);
    }

    private static Task TestFaceResetContracts()
    {
        Assert(FaceResetSection.FaceParts.ToWireName() == "face-parts" &&
            FaceResetSection.SkyrimMorphs.ToWireName() == "skyrim-morphs" &&
            FaceResetSectionExtensions.TryParseWireName("BONE-REGIONS", out var section) &&
            section == FaceResetSection.BoneRegions,
            "Face-reset section wire names changed.");
        return Task.CompletedTask;
    }

    private static async Task TestFaceResetCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-face-reset-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        try
        {
            var current = new WorkspacePath(Path.Combine(root.Value, "current", "Npc.face.json"));
            var baseline = new WorkspacePath(Path.Combine(root.Value, "baseline", "Npc.face.json"));
            var output = new WorkspacePath(Path.Combine(root.Value, "output", "Npc.face.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(current.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(baseline.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
            await File.WriteAllTextAsync(current.Value, """
                {
                  "schemaVersion": 1, "game": "fallout4", "npcFormId": "0x00000800",
                  "face-parts": {"headParts": ["current-head"], "hairColor": 12},
                  "tints": {"layers": ["current-tint"]},
                  "vertex-morphs": {"Smile": 0.25},
                  "bone-regions": {"4": [1, 2, 3]},
                  "unrelated": {"keep": [1, 2, 3]}
                }
                """);
            await File.WriteAllTextAsync(baseline.Value, """
                {
                  "schemaVersion": 1, "game": "fallout4", "npcFormId": "0x00000800",
                  "face-parts": {"headParts": ["baseline-head"], "hairColor": 20},
                  "tints": {"layers": ["baseline-tint"]},
                  "vertex-morphs": {"Smile": 0.75},
                  "bone-regions": {"4": [9, 8, 7]},
                  "unrelated": {"keep": [1, 2, 3]}
                }
                """);

            var (runner, outputText, errorText) = CreateRunner();
            var apply = await runner.RunAsync(CommandLine.Parse(["face", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", output.Value,
                "--section", "face-parts", "--expected-sha256", Hash(current).Value, "--apply", "--json"]), CancellationToken.None);
            using var applied = JsonDocument.Parse(outputText.ToString());
            var appliedRoot = applied.RootElement;
            Assert(apply == CommandExitCode.Success && errorText.ToString().Length == 0 && appliedRoot.GetProperty("applied").GetBoolean(),
                "Face-reset CLI did not apply the bounded face-parts reset.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            var resultRoot = result.RootElement;
            Assert(resultRoot.GetProperty("face-parts").GetProperty("hairColor").GetInt32() == 20 &&
                resultRoot.GetProperty("tints").GetProperty("layers")[0].GetString() == "current-tint" &&
                resultRoot.GetProperty("vertex-morphs").GetProperty("Smile").GetSingle() == 0.25F &&
                resultRoot.GetProperty("unrelated").GetProperty("keep").GetArrayLength() == 3,
                "Face-reset output changed an unrelated face section or unknown root value.");

            var wrongHashOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-hash", "Npc.face.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(wrongHashOutput.Value)!);
            var wrongHash = await runner.RunAsync(CommandLine.Parse(["face", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", wrongHashOutput.Value,
                "--section", "tints", "--expected-sha256", new string('0', 64), "--apply", "--json"]), CancellationToken.None);
            Assert(wrongHash == CommandExitCode.ValidationFailure && outputText.ToString().Contains("face-reset-input-hash-mismatch", StringComparison.Ordinal),
                "Face-reset CLI accepted a stale expected hash.");

            var wrongSectionOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-section", "Npc.face.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(wrongSectionOutput.Value)!);
            var wrongSection = await runner.RunAsync(CommandLine.Parse(["face", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", wrongSectionOutput.Value,
                "--section", "skyrim-morphs", "--json"]), CancellationToken.None);
            Assert(wrongSection == CommandExitCode.ValidationFailure && outputText.ToString().Contains("face-reset-section-game-mismatch", StringComparison.Ordinal),
                "Face-reset CLI accepted an SSE-only section for Fallout 4.");

            var sseCurrent = new WorkspacePath(Path.Combine(root.Value, "sse-current", "Npc.face.json"));
            var sseBaseline = new WorkspacePath(Path.Combine(root.Value, "sse-baseline", "Npc.face.json"));
            var sseOutput = new WorkspacePath(Path.Combine(root.Value, "sse-output", "Npc.face.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(sseCurrent.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sseBaseline.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sseOutput.Value)!);
            await File.WriteAllTextAsync(sseCurrent.Value, "{\"schemaVersion\":1,\"game\":\"skyrimse\",\"npcFormId\":\"0x800\",\"skyrim-morphs\":{\"Smile\":0.1},\"skyrim-tints\":{\"layers\":[1]},\"keep\":true}");
            await File.WriteAllTextAsync(sseBaseline.Value, "{\"schemaVersion\":1,\"game\":\"skyrimse\",\"npcFormId\":\"0x800\",\"skyrim-morphs\":{\"Smile\":0.9},\"skyrim-tints\":{\"layers\":[2]},\"keep\":true}");
            var sse = await runner.RunAsync(CommandLine.Parse(["face", "reset", "--game", "skyrimse", "--npc", "0x800",
                "--current", sseCurrent.Value, "--baseline", sseBaseline.Value, "--output", sseOutput.Value,
                "--section", "skyrim-morphs", "--expected-sha256", Hash(sseCurrent).Value, "--apply", "--json"]), CancellationToken.None);
            using var sseResult = JsonDocument.Parse(await File.ReadAllTextAsync(sseOutput.Value));
            Assert(sse == CommandExitCode.Success && sseResult.RootElement.GetProperty("skyrim-morphs").GetProperty("Smile").GetSingle() == 0.9F &&
                sseResult.RootElement.GetProperty("skyrim-tints").GetProperty("layers")[0].GetInt32() == 1,
                "Face-reset CLI did not preserve SSE's unrelated tint section.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static Task TestBodyResetContracts()
    {
        Assert(BodyResetSection.SkinOverrides.ToWireName() == "skin-overrides" &&
            BodyResetSectionExtensions.TryParseWireName("TRANSFORMS", out var section) &&
            section == BodyResetSection.Transforms,
            "Body-reset section wire names changed.");
        return Task.CompletedTask;
    }

    private static async Task TestBodyResetCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-body-reset-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        try
        {
            var current = new WorkspacePath(Path.Combine(root.Value, "current", "Npc.body.json"));
            var baseline = new WorkspacePath(Path.Combine(root.Value, "baseline", "Npc.body.json"));
            var output = new WorkspacePath(Path.Combine(root.Value, "output", "Npc.body.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(current.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(baseline.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
            await File.WriteAllTextAsync(current.Value, """
                {
                  "schemaVersion": 1, "game": "fallout4", "npcFormId": "0x00000800",
                  "weight": {"thin": 0.2, "muscular": 0.3, "fat": 0.5},
                  "morphs": {"head": 0.1},
                  "sliders": {"Body": 0.25},
                  "skin": {"formId": "0x0100"},
                  "overlays": [{"priority": 1, "texture": "current.dds"}],
                  "unknownFuture": {"keep": true}
                }
                """);
            await File.WriteAllTextAsync(baseline.Value, """
                {
                  "schemaVersion": 1, "game": "fallout4", "npcFormId": "0x00000800",
                  "weight": {"thin": 0.8, "muscular": 0.1, "fat": 0.1},
                  "morphs": {"head": 0.9},
                  "sliders": {"Body": 0.75},
                  "skin": {"formId": "0x0200"},
                  "overlays": [{"priority": 1, "texture": "baseline.dds"}],
                  "unknownFuture": {"keep": true}
                }
                """);

            var (runner, outputText, errorText) = CreateRunner();
            var apply = await runner.RunAsync(CommandLine.Parse(["body", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", output.Value,
                "--section", "overlays", "--expected-sha256", Hash(current).Value, "--apply", "--json"]), CancellationToken.None);
            using var applied = JsonDocument.Parse(outputText.ToString());
            Assert(apply == CommandExitCode.Success && errorText.ToString().Length == 0 &&
                applied.RootElement.GetProperty("applied").GetBoolean(),
                "Body-reset CLI did not apply the bounded overlay reset.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            var resultRoot = result.RootElement;
            Assert(resultRoot.GetProperty("overlays")[0].GetProperty("texture").GetString() == "baseline.dds" &&
                resultRoot.GetProperty("weight").GetProperty("thin").GetDouble() == 0.2 &&
                resultRoot.GetProperty("morphs").GetProperty("head").GetDouble() == 0.1 &&
                resultRoot.GetProperty("unknownFuture").GetProperty("keep").GetBoolean(),
                "Body-reset output changed an unrelated section or unknown root value.");

            var wrongHashOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-hash", "Npc.body.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(wrongHashOutput.Value)!);
            var wrongHash = await runner.RunAsync(CommandLine.Parse(["body", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", wrongHashOutput.Value,
                "--section", "skin", "--expected-sha256", new string('0', 64), "--apply", "--json"]), CancellationToken.None);
            Assert(wrongHash == CommandExitCode.ValidationFailure &&
                outputText.ToString().Contains("body-reset-input-hash-mismatch", StringComparison.Ordinal),
                "Body-reset CLI accepted a stale expected hash.");

            var wrongSectionOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-section", "Npc.body.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(wrongSectionOutput.Value)!);
            var wrongSection = await runner.RunAsync(CommandLine.Parse(["body", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", wrongSectionOutput.Value,
                "--section", "transforms", "--json"]), CancellationToken.None);
            Assert(wrongSection == CommandExitCode.ValidationFailure &&
                outputText.ToString().Contains("body-reset-section-game-mismatch", StringComparison.Ordinal),
                "Body-reset CLI accepted an SSE-only section for Fallout 4.");

            var malformed = new WorkspacePath(Path.Combine(root.Value, "malformed", "Npc.body.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(malformed.Value)!);
            await File.WriteAllTextAsync(malformed.Value, "{\"schemaVersion\":1,\"game\":\"fallout4\",\"npcFormId\":\"0x800\",\"weight\":42}");
            var malformedOutput = new WorkspacePath(Path.Combine(root.Value, "malformed-output", "Npc.body.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(malformedOutput.Value)!);
            var malformedExit = await runner.RunAsync(CommandLine.Parse(["body", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", malformed.Value, "--baseline", baseline.Value, "--output", malformedOutput.Value,
                "--section", "weight", "--json"]), CancellationToken.None);
            Assert(malformedExit == CommandExitCode.ValidationFailure &&
                outputText.ToString().Contains("body-reset-read-failed", StringComparison.Ordinal),
                "Body-reset CLI accepted a malformed section payload.");

            var collision = await runner.RunAsync(CommandLine.Parse(["body", "reset", "--game", "fallout4", "--npc", "0x800",
                "--current", current.Value, "--baseline", baseline.Value, "--output", output.Value,
                "--section", "skin", "--json"]), CancellationToken.None);
            Assert(collision == CommandExitCode.ValidationFailure &&
                outputText.ToString().Contains("body-reset-output-exists", StringComparison.Ordinal),
                "Body-reset CLI overwrote an existing artifact.");

            var sseCurrent = new WorkspacePath(Path.Combine(root.Value, "sse-current", "Npc.body.json"));
            var sseBaseline = new WorkspacePath(Path.Combine(root.Value, "sse-baseline", "Npc.body.json"));
            var sseOutput = new WorkspacePath(Path.Combine(root.Value, "sse-output", "Npc.body.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(sseCurrent.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sseBaseline.Value)!);
            Directory.CreateDirectory(Path.GetDirectoryName(sseOutput.Value)!);
            await File.WriteAllTextAsync(sseCurrent.Value, "{\"schemaVersion\":1,\"game\":\"skyrimse\",\"npcFormId\":\"0x800\",\"transforms\":[{\"node\":\"NPC Spine\",\"scale\":1.1}],\"sliders\":{\"Body\":0.1},\"keep\":true}");
            await File.WriteAllTextAsync(sseBaseline.Value, "{\"schemaVersion\":1,\"game\":\"skyrimse\",\"npcFormId\":\"0x800\",\"transforms\":[{\"node\":\"NPC Spine\",\"scale\":0.9}],\"sliders\":{\"Body\":0.8},\"keep\":true}");
            var sse = await runner.RunAsync(CommandLine.Parse(["body", "reset", "--game", "skyrimse", "--npc", "0x800",
                "--current", sseCurrent.Value, "--baseline", sseBaseline.Value, "--output", sseOutput.Value,
                "--section", "transforms", "--expected-sha256", Hash(sseCurrent).Value, "--apply", "--json"]), CancellationToken.None);
            using var sseResult = JsonDocument.Parse(await File.ReadAllTextAsync(sseOutput.Value));
            Assert(sse == CommandExitCode.Success &&
                sseResult.RootElement.GetProperty("transforms")[0].GetProperty("scale").GetDouble() == 0.9 &&
                sseResult.RootElement.GetProperty("sliders").GetProperty("Body").GetDouble() == 0.1,
                "Body-reset CLI did not preserve SSE's unrelated slider section.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static Task TestWeightTriangleContracts()
    {
        Assert(WeightTriangleAxis.Thin.ToWireName() == "thin" &&
            WeightTriangleAxisExtensions.TryParseWireName("MUSCULAR", out var axis) &&
            axis == WeightTriangleAxis.Muscular,
            "Weight-triangle axis wire names changed.");
        Assert(WeightTriangleMath.TryNormalize(2, -1, 1, out var normalized, out _) &&
            Math.Abs(normalized.Thin - (2F / 3F)) < 0.0001F && normalized.Muscular == 0 &&
            Math.Abs(normalized.Fat - (1F / 3F)) < 0.0001F,
            "WeightTriangleControl normalization was not reproduced.");
        Assert(WeightTriangleMath.TryNormalize(0, 0, 0, out var fallback, out _) &&
            fallback == new WeightTriangle(0.5F, 0.5F, 0),
            "WeightTriangleControl degenerate fallback changed.");
        Assert(WeightTriangleMath.TryRedistribute(new WeightTriangle(0.2F, 0.3F, 0.5F), WeightTriangleAxis.Thin,
                0.8F, out var redistributed, out _) &&
            Math.Abs(redistributed.Thin - 0.8F) < 0.0001F && Math.Abs(redistributed.Muscular - 0.075F) < 0.0001F &&
            Math.Abs(redistributed.Fat - 0.125F) < 0.0001F,
            "EditBody_Form proportional slider redistribution was not reproduced.");
        return Task.CompletedTask;
    }

    private static async Task TestWeightTriangleCli()
    {
        var (runner, output, error) = CreateRunner();
        var normalizedExit = runner.RunAsync(CommandLine.Parse(["body", "weight", "normalize", "--game", "fallout4",
            "--triangle", "thin=2,muscular=-1,fat=1", "--json"]), CancellationToken.None);
        Assert((await normalizedExit) == CommandExitCode.Success && error.ToString().Length == 0,
            "Weight-triangle normalize CLI did not return success.");
        using var normalized = JsonDocument.Parse(output.ToString());
        Assert(Math.Abs(normalized.RootElement.GetProperty("thin").GetSingle() - (2F / 3F)) < 0.0001F &&
            normalized.RootElement.GetProperty("muscular").GetSingle() == 0 &&
            Math.Abs(normalized.RootElement.GetProperty("fat").GetSingle() - (1F / 3F)) < 0.0001F,
            "Weight-triangle normalize CLI returned the wrong simplex coordinates.");

        var (redistributeRunner, redistributeOutput, redistributeError) = CreateRunner();
        var redistributedExit = redistributeRunner.RunAsync(CommandLine.Parse(["body", "weight", "redistribute", "--game", "fallout4",
            "--current", "thin=0.2,muscular=0.3,fat=0.5", "--axis", "thin", "--value", "0.8", "--json"]), CancellationToken.None);
        Assert((await redistributedExit) == CommandExitCode.Success && redistributeError.ToString().Length == 0,
            "Weight-triangle redistribute CLI did not return success.");
        using var redistributed = JsonDocument.Parse(redistributeOutput.ToString());
        Assert(Math.Abs(redistributed.RootElement.GetProperty("thin").GetSingle() - 0.8F) < 0.0001F &&
            Math.Abs(redistributed.RootElement.GetProperty("muscular").GetSingle() - 0.075F) < 0.0001F &&
            Math.Abs(redistributed.RootElement.GetProperty("fat").GetSingle() - 0.125F) < 0.0001F,
            "Weight-triangle redistribute CLI returned the wrong proportional split.");

        var (negativeRunner, negativeOutput, _) = CreateRunner();
        var negativeExit = negativeRunner.RunAsync(CommandLine.Parse(["body", "weight", "normalize", "--game", "fallout4",
            "--triangle", "thin=NaN,muscular=0,fat=0", "--json"]), CancellationToken.None);
        Assert((await negativeExit) == CommandExitCode.ValidationFailure && negativeOutput.ToString().Contains("weight-triangle-invalid", StringComparison.Ordinal),
            "Weight-triangle CLI accepted a non-finite value.");
    }

    private static Task TestSkinContracts()
    {
        var set = new NpcSkinPatch(OptionalFormReference.Set(
            new FormReference(new PluginName("M2FixtureFO4.esp"), new FormId(0x801))));
        Assert(set.Fallout4Skin.IsSpecified && set.Fallout4Skin.Value?.FormId.Value == 0x801 && !set.IsEmpty,
            "FO4 skin set contract lost its typed FormReference.");
        var clear = new NpcSkinPatch(OptionalFormReference.Clear());
        Assert(clear.Fallout4Skin.IsSpecified && clear.Fallout4Skin.Value is null && !clear.IsEmpty,
            "FO4 race-default clear contract lost its explicit state.");
        Assert(new NpcSkinPatch(default, "Vanilla CBBE").IsEmpty == false,
            "LooksMenu preset-skin intent was not retained for boundary validation.");
        return Task.CompletedTask;
    }

    private static async Task TestSkinCli()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = NewMutationOutput("M5SkinFO4.esp");
        var (runner, text, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "fallout4",
            "--input-plugin", source.Value, "--output", output.Value, "--npc", "0x800",
            "--skin", "M2FixtureFO4.esp|0x801", "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(text.ToString());
        Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && response.RootElement.GetProperty("applied").GetBoolean(),
            $"body patch did not apply the typed Fallout 4 WNAM route: {text} {error}");
        var verify = await runner.RunAsync(CommandLine.Parse(["plugin", "verify", "--game", "fallout4",
            "--source-plugin", source.Value, "--output-plugin", output.Value, "--npc", "0x800",
            "--skin", "M2FixtureFO4.esp|0x801", "--json"]), CancellationToken.None);
        Assert(verify == CommandExitCode.Success && text.ToString().Contains("valid", StringComparison.OrdinalIgnoreCase),
            "Independent plugin verification did not accept the written WNAM route.");

        var (presetRunner, presetOutput, _) = CreateRunner();
        var presetPath = NewMutationOutput("M5SkinPresetRefused.esp");
        var presetExit = await presetRunner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "fallout4",
            "--input-plugin", source.Value, "--output", presetPath.Value, "--npc", "0x800",
            "--preset-skin", "Vanilla CBBE", "--json"]), CancellationToken.None);
        Assert(presetExit == CommandExitCode.ValidationFailure && presetOutput.ToString().Contains("preset-skin-unsupported", StringComparison.Ordinal) && !File.Exists(presetPath.Value),
            "Unverified LooksMenu preset-skin selection was not refused before writing.");

        var sseSource = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var sseOutput = NewMutationOutput("M5SkinSseRefused.esp");
        var (sseRunner, sseText, _) = CreateRunner();
        var sseExit = await sseRunner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "skyrimse",
            "--input-plugin", sseSource.Value, "--output", sseOutput.Value, "--npc", "0x800",
            "--clear-skin", "--json"]), CancellationToken.None);
        Assert(sseExit == CommandExitCode.ValidationFailure && sseText.ToString().Contains("skyrim-skin-unsupported", StringComparison.Ordinal) && !File.Exists(sseOutput.Value),
            "Skyrim WNAM skin routing was not rejected explicitly.");

        var (verifyRunner, _, verifyError) = CreateRunner();
        var verifyExit = await verifyRunner.RunAsync(CommandLine.Parse(["plugin", "verify", "--game", "skyrimse",
            "--source-plugin", sseSource.Value, "--output-plugin", sseOutput.Value, "--form-id", "0x800",
            "--clear-skin", "--json"]), CancellationToken.None);
        Assert(verifyExit == CommandExitCode.UsageError && verifyError.ToString().Contains("WNAM skin routing", StringComparison.Ordinal),
            "Skyrim WNAM expectations were not rejected by plugin verification.");
    }

    private static Task TestBodyMorphContracts()
    {
        var patch = new NpcBodyMorphPatch(ImmutableDictionary<Fallout4BodyRegion, float>.Empty
            .Add(Fallout4BodyRegion.Head, 0.25F)
            .Add(Fallout4BodyRegion.Legs, -0.5F));
        var values = new Fallout4BodyMorphValues(0.1F, 0.2F, 0.3F, 0.4F, 0.5F).Apply(patch.Values);
        Assert(values.Head == 0.25F && values.UpperTorso == 0.2F && values.Arms == 0.3F && values.LowerTorso == 0.4F && values.Legs == -0.5F,
            "FO4 body-region patch did not preserve named values and pinned positional order.");
        Assert(Fallout4BodyRegionCatalog.Ordered.Select(region => region.ToWireName()).SequenceEqual(["head", "upperTorso", "arms", "lowerTorso", "legs"]),
            "FO4 body-region wire catalog order changed.");
        return Task.CompletedTask;
    }

    private static async Task TestBodyMorphCli()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = NewMutationOutput("M5BodyMorphFO4.esp");
        var regions = "{\"head\":0.25,\"upperTorso\":-0.5,\"arms\":0.1,\"lowerTorso\":0,\"legs\":1}";
        var (runner, text, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "fallout4",
            "--input-plugin", source.Value, "--output", output.Value, "--npc", "0x800", "--regions", regions,
            "--expected-sha256", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
        Assert(exit == CommandExitCode.Success && File.Exists(output.Value) && error.ToString().Length == 0,
            "FO4 body-region mutation did not produce a verified output.");

        var (verifyRunner, verifyText, verifyError) = CreateRunner();
        var verifyExit = await verifyRunner.RunAsync(CommandLine.Parse(["plugin", "verify", "--game", "fallout4",
            "--source-plugin", source.Value, "--output-plugin", output.Value, "--form-id", "0x800", "--regions", regions, "--json"]), CancellationToken.None);
        Assert(verifyExit == CommandExitCode.Success && verifyText.ToString().Contains("true", StringComparison.OrdinalIgnoreCase) && verifyError.ToString().Length == 0,
            "FO4 body-region plugin verification did not resolve the partial MRSV expectation.");

        var invalidOutput = NewMutationOutput("M5BodyMorphInvalid.esp");
        var (invalidRunner, invalidText, _) = CreateRunner();
        var invalidExit = await invalidRunner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "fallout4",
            "--input-plugin", source.Value, "--output", invalidOutput.Value, "--npc", "0x800", "--regions", "{\"head\":2}", "--json"]), CancellationToken.None);
        Assert(invalidExit == CommandExitCode.ValidationFailure && invalidText.ToString().Contains("body-region-out-of-range", StringComparison.Ordinal) && !File.Exists(invalidOutput.Value),
            "Out-of-range body-region values were not refused before writing.");

        var duplicateOutput = NewMutationOutput("M5BodyMorphDuplicate.esp");
        var (duplicateRunner, _, duplicateError) = CreateRunner();
        var duplicateExit = await duplicateRunner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "fallout4",
            "--input-plugin", source.Value, "--output", duplicateOutput.Value, "--npc", "0x800", "--regions", "{\"head\":0.1,\"HEAD\":0.2}", "--json"]), CancellationToken.None);
        Assert(duplicateExit == CommandExitCode.UsageError && duplicateError.ToString().Contains("duplicate", StringComparison.OrdinalIgnoreCase) && !File.Exists(duplicateOutput.Value),
            "Duplicate body-region identifiers were not rejected by the CLI parser.");

        var sseSource = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var sseOutput = NewMutationOutput("M5BodyMorphSseRefused.esp");
        var (sseRunner, sseText, _) = CreateRunner();
        var sseExit = await sseRunner.RunAsync(CommandLine.Parse(["body", "patch", "--game", "skyrimse",
            "--input-plugin", sseSource.Value, "--output", sseOutput.Value, "--npc", "0x800", "--regions", "{\"head\":0.2}", "--json"]), CancellationToken.None);
        Assert(sseExit == CommandExitCode.ValidationFailure && sseText.ToString().Contains("skyrim-body-morphs-unsupported", StringComparison.Ordinal) && !File.Exists(sseOutput.Value),
            "Skyrim MRSV body-region routing was not refused explicitly.");
    }

    private static async Task TestCapabilities()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["capabilities", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "Capabilities did not return success.");
        Assert(document.RootElement.GetProperty("protocol").GetString() == "1", "Protocol version changed.");
        Assert(document.RootElement.GetProperty("sourceLine").GetString() == "preview.281-public",
            "Capabilities did not identify the preview.281 public source line.");
        Assert(document.RootElement.GetProperty("commands").GetArrayLength() == CommandCatalog.All.Length,
            "Capabilities command count does not match the catalog.");
        ImmutableHashSet<string> actualCommandNames = document.RootElement
            .GetProperty("commands")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!)
            .ToImmutableHashSet(StringComparer.Ordinal);
        Assert(actualCommandNames.SetEquals(Preview231CommandNames) &&
               actualCommandNames.Count == 142,
            "Capabilities drifted from the exact 142-command set.");
        Assert(document.RootElement.GetProperty("commands").EnumerateArray()
                .Count(item => item.GetProperty("name").GetString() == "npc create-from-jslot") == 1 &&
               CommandLine.Parse(["npc", "create-from-jslot"]).Name == "npc create-from-jslot",
            "Capabilities omitted or ambiguously parsed the supported RaceMenu JSlot NPC authoring command.");
        Assert(document.RootElement.GetProperty("ledgerMappings").EnumerateArray()
            .Any(item => item.GetProperty("command").GetString() == "pipeline preset-to-npc" &&
                         item.GetProperty("ledgerIds").EnumerateArray().Any(id => id.GetString() == "P12-007")),
            "Capabilities did not expose the P12 pipeline ledger mapping.");
    }

    private static async Task TestActorwrightHelpIdentity()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["help"]), CancellationToken.None);
        Assert(exit == CommandExitCode.Success, "Help did not return success.");
        Assert(
            output.ToString().StartsWith(
                "actorwright — typed Actorwright CLI",
                StringComparison.Ordinal),
            "Help did not expose the Actorwright CLI identity.");
        Assert(output.ToString().Contains(
                "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Skyrim.esm\",\"order\":0,\"enabled\":true}]}",
                StringComparison.Ordinal),
            "Help did not expose the compact schema-1 load-order example.");
        Assert(output.ToString().Contains(
                "schema export --command \"npc finish analyze\"",
                StringComparison.Ordinal),
            "Finish Core help did not direct consumers to its document schemas.");
    }

    private static async Task TestVersionIdentity()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(
            CommandLine.Parse(["version", "--json"]),
            CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "Version did not return success.");
        Assert(document.RootElement.GetProperty("version").GetString() == "0.1.0-m1",
            "Internal version changed during source integration.");
        Assert(document.RootElement.GetProperty("sourceLine").GetString() == "preview.281-public",
            "Version did not identify the preview.281 public source line.");
    }

    private static async Task TestSchemaExport()
    {
        var outputPath = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\p12-schema-export.schema.json");
        DeleteIfExists(outputPath.Value);
        var (runner, output, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["schema", "export", "--command", "pipeline preset-to-npc",
            "--output", outputPath.Value, "--json"]), CancellationToken.None);
        Assert(exit == CommandExitCode.Success && File.Exists(outputPath.Value), "Schema export did not write its artifact.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath.Value));
        Assert(document.RootElement.GetProperty("schemaVersion").GetString() == "1" &&
               document.RootElement.GetProperty("commands").GetArrayLength() == 1 &&
               document.RootElement.GetProperty("commands")[0].GetProperty("ledgerIds")[0].GetString() == "P12-007" &&
               document.RootElement.GetProperty("exitCodes").GetArrayLength() == 6,
            "Schema export did not contain the stable command and exit-code contract.");
        var repeat = await runner.RunAsync(CommandLine.Parse(["schema", "export", "--command", "pipeline preset-to-npc",
            "--output", outputPath.Value, "--json"]), CancellationToken.None);
        Assert(repeat == CommandExitCode.ValidationFailure && error.ToString().Contains("schema-output-exists", StringComparison.Ordinal),
            "Schema export overwrote an existing artifact.");
    }

    private static async Task TestGuiProductionPolicyRefusal()
    {
        var (runner, output, error) = CreateRunner();
        var status = await runner.RunAsync(CommandLine.Parse(["gui", "--json"]), CancellationToken.None);
        Assert(status == CommandExitCode.Success && output.ToString().Contains("K-local", StringComparison.Ordinal),
            "GUI status did not describe the explicit launch contract.");
        var refused = await runner.RunAsync(CommandLine.Parse(["gui", "--launch", "--executable",
            "F:\\ExampleGame\\NpcManager.Desktop.exe", "--json"]), CancellationToken.None);
        Assert(refused == CommandExitCode.SecurityRefusal &&
               error.ToString().Contains("protected-root-refused", StringComparison.Ordinal),
            "Production GUI launch did not refuse the protected live root.");
    }

    private static async Task TestGuiV1Forwarding()
    {
        var launcher = new FakeDesktopLaunchService();
        var (runner, _, error) = CreateRunner(desktopLaunchService: launcher);
        var launched = await runner.RunAsync(CommandLine.Parse(["gui", "--launch", "--executable",
            "K:\\Actorwright\\NpcManager.Desktop.exe", "--json"]), CancellationToken.None);
        Assert(launched == CommandExitCode.Success && error.ToString().Length == 0 &&
               launcher.LastRequest == new DesktopLaunchRequest(
                   new WorkspacePath("K:\\Actorwright\\NpcManager.Desktop.exe")) &&
               launcher.LaunchCount == 1,
            "GUI launch changed the v1 request or did not forward it exactly once.");
    }

    private static async Task TestGuiWorkflowLaunchUsage()
    {
        const string bundle = "K:\\Actorwright\\review\\workflow-bundle.json";
        var uppercaseHash = new string('A', 64);
        var launcher = new FakeDesktopLaunchService();
        var (runner, _, error) = CreateRunner(desktopLaunchService: launcher);

        await AssertGuiUsageAsync(runner, error,
            ["gui", "--launch", "--workflow-bundle", bundle],
            "workflow bundle path and SHA-256 must be supplied together");
        await AssertGuiUsageAsync(runner, error,
            ["gui", "--launch", "--workflow-bundle-sha256", uppercaseHash],
            "workflow bundle path and SHA-256 must be supplied together");
        await AssertGuiUsageAsync(runner, error,
            ["gui", "--launch", "--workflow-bundle", bundle,
                "--workflow-bundle-sha256", uppercaseHash.ToLowerInvariant()],
            "workflow bundle SHA-256 must be 64 uppercase hexadecimal characters");
        await AssertGuiUsageAsync(runner, error,
            ["gui", "--launch", "--executable", "K:\\Actorwright\\NpcManager.Desktop.exe",
                "--workflow-bundle", "--workflow-bundle-sha256", uppercaseHash],
            "workflow bundle path must be a fully qualified path");
        await AssertGuiUsageAsync(runner, error,
            ["gui", "--launch", "--executable", "K:\\Actorwright\\NpcManager.Desktop.exe",
                "--workflow-bundle", "review\\workflow-bundle.json",
                "--workflow-bundle-sha256", uppercaseHash],
            "workflow bundle path must be a fully qualified path");
        await AssertGuiUsageAsync(runner, error,
            ["gui", "--launch", "--executable", "K:\\Actorwright\\NpcManager.Desktop.exe",
                "--workflow-bundle", "K:\\Actorwright\\review\\bad\0bundle.json",
                "--workflow-bundle-sha256", uppercaseHash],
            "workflow bundle path must be a fully qualified path");
        Assert(launcher.LaunchCount == 0,
            "Invalid GUI workflow launch input reached the desktop launcher.");
    }

    private static async Task TestGuiWorkflowLaunchBinding()
    {
        const string executable = "K:\\Actorwright\\NpcManager.Desktop.exe";
        const string bundle = "K:\\Actorwright\\review\\workflow-bundle.json";
        var hash = new string('B', 64);
        var launcher = new FakeDesktopLaunchService();
        var (runner, _, error) = CreateRunner(desktopLaunchService: launcher);

        var result = await runner.RunAsync(CommandLine.Parse([
            "gui", "--launch", "--executable", executable,
            "--workflow-bundle", bundle,
            "--workflow-bundle-sha256", hash]), CancellationToken.None);

        Assert(result == CommandExitCode.Success && error.ToString().Length == 0 &&
               launcher.LastRequest == new DesktopLaunchRequest(
                   new WorkspacePath(executable),
                   new DesktopWorkflowLaunchBinding(new WorkspacePath(bundle), hash)) &&
               launcher.LaunchCount == 1,
            "Workflow launch binding was not forwarded exactly once.");
    }

    private static async Task AssertGuiUsageAsync(CliRunner runner, StringWriter error,
        string[] arguments, string expectedMessage)
    {
        error.GetStringBuilder().Clear();
        var result = await runner.RunAsync(CommandLine.Parse(arguments), CancellationToken.None);
        Assert(result == CommandExitCode.UsageError &&
               error.ToString().Contains(expectedMessage, StringComparison.Ordinal),
            $"GUI usage did not report: {expectedMessage}");
    }

    private static void TestDesktopWorkflowProcessArguments()
    {
        const string executable = "K:\\Actorwright\\bin\\NpcManager.Desktop.exe";
        const string admittedRoot = "K:\\Actorwright";
        const string bundle = "K:\\Actorwright\\review\\workflow bundle.json";
        var hash = new string('C', 64);
        string? priorRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                @"C:\caller-root-must-not-be-inherited");
            var startInfo = DesktopLaunchService.CreateStartInfo(new DesktopLaunchRequest(
                new WorkspacePath(executable),
                new DesktopWorkflowLaunchBinding(new WorkspacePath(bundle), hash)),
                new WorkspacePath(admittedRoot));

            Assert(startInfo.FileName == new WorkspacePath(executable).Value &&
                   startInfo.WorkingDirectory == Path.GetDirectoryName(executable) &&
                   startInfo.ArgumentList.SequenceEqual([
                       "--workflow-bundle", new WorkspacePath(bundle).Value,
                       "--workflow-bundle-sha256", hash]),
                "Desktop workflow launch did not use four exact ProcessStartInfo.ArgumentList tokens.");
            Assert(startInfo.Environment.TryGetValue(
                       ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                       out string? childRoot) && childRoot == admittedRoot,
                "Desktop child did not receive the exact admitted workspace root.");

            var v1StartInfo = DesktopLaunchService.CreateStartInfo(
                new DesktopLaunchRequest(new WorkspacePath(executable)),
                new WorkspacePath(admittedRoot));
            Assert(v1StartInfo.ArgumentList.Count == 0 &&
                   v1StartInfo.Environment.TryGetValue(
                       ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                       out string? v1ChildRoot) && v1ChildRoot == admittedRoot,
                "Desktop v1 launch changed arguments or omitted the admitted root.");

            var invalidHashRefused = false;
            try
            {
                DesktopLaunchService.CreateStartInfo(new DesktopLaunchRequest(
                    new WorkspacePath(executable),
                    new DesktopWorkflowLaunchBinding(new WorkspacePath(bundle),
                        hash.ToLowerInvariant())),
                    new WorkspacePath(admittedRoot));
            }
            catch (ArgumentException)
            {
                invalidHashRefused = true;
            }
            Assert(invalidHashRefused,
                "Desktop process startup accepted a non-uppercase workflow SHA-256.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                priorRoot);
        }
    }

    private static void TestDesktopLaunchServiceInvalidWorkflowBinding()
    {
        var hash = new string('D', 64);
        var validBundle = new WorkspacePath("K:\\Actorwright\\review\\workflow-bundle.json");
        var service = new DesktopLaunchService(new FailOnEvaluationWorkspacePolicy(),
            new WorkspacePath("K:\\Actorwright"));

        var nullRequest = service.Launch(null!);
        Assert(!nullRequest.Launched && nullRequest.Diagnostics.Length == 1 &&
               nullRequest.Diagnostics[0].Code == "desktop-launch-request-invalid",
            "Desktop launch did not return a stable diagnostic for a null request.");

        var invalidBundle = service.Launch(new DesktopLaunchRequest(default,
            new DesktopWorkflowLaunchBinding(default, hash)));
        Assert(!invalidBundle.Launched && invalidBundle.Diagnostics.Length == 1 &&
               invalidBundle.Diagnostics[0].Code == "desktop-workflow-bundle-invalid",
            "Desktop launch did not validate the workflow bundle before the executable.");

        foreach (var invalidHash in new string?[] { null, hash.ToLowerInvariant() })
        {
            var invalidHashResult = service.Launch(new DesktopLaunchRequest(
                new WorkspacePath(typeof(Program).Assembly.Location),
                new DesktopWorkflowLaunchBinding(validBundle, invalidHash!)));
            Assert(!invalidHashResult.Launched && invalidHashResult.Diagnostics.Length == 1 &&
                   invalidHashResult.Diagnostics[0].Code == "desktop-workflow-bundle-sha256-invalid",
                "Desktop launch did not validate the workflow SHA-256 before policy evaluation.");
        }
    }

    private static async Task TestAllowedPreflight()
    {
        var (runner, output, _) = CreateRunner();
        var command = CommandLine.Parse([
            "workspace", "preflight", "--json",
            "--workspace-root", "K:\\ExampleWorkspace",
            "--output-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds"]);
        var exit = await runner.RunAsync(command, CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "Allowed preflight did not return success.");
        Assert(document.RootElement.GetProperty("isAllowed").GetBoolean(), "Allowed preflight was refused.");
    }

    private static async Task TestRefusedPreflight()
    {
        var (runner, output, _) = CreateRunner();
        var command = CommandLine.Parse([
            "workspace", "preflight", "--json",
            "--workspace-root", "K:\\ExampleWorkspace",
            "--output-root", "F:\\ExampleGame\\Data"]);
        var exit = await runner.RunAsync(command, CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal, "Refused preflight did not return security exit.");
        Assert(!document.RootElement.GetProperty("isAllowed").GetBoolean(), "Refused preflight was allowed.");
    }

    private static async Task TestMissingPreflightRoots()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError, "Missing roots did not return usage exit.");
        Assert(document.RootElement.GetProperty("code").GetString() == "usage-error", "Usage error code changed.");
    }

    private static async Task TestCancellation()
    {
        var (runner, _, _) = CreateRunner();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            CommandLine.Parse([
                "workspace", "preflight",
                "--workspace-root", "K:\\ExampleWorkspace",
                "--output-root", "K:\\ExampleWorkspace\\03-builds"]), source.Token).AsTask());
    }

    private static async Task TestGameRootPreflightBothGames()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var service = new GameRootPreflightService(policy);
        foreach (var (edition, game) in new[]
        {
            (GameEdition.Fallout4, "fo4"),
            (GameEdition.SkyrimSpecialEdition, "sse")
        })
        {
            var dataRoot = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\{game}\\Data");
            var outputRoot = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preflight-{game}");
            var result = await service.EvaluateAsync(new GameRootPreflightRequest(edition,
                new WorkspacePath("K:\\ExampleWorkspace"), dataRoot, outputRoot), CancellationToken.None);
            Assert(result.IsAllowed && result.Edition == edition && result.Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error),
                $"{edition} copied Data root was refused.");
        }
    }

    private static async Task TestGameRootPreflightOverlap()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var dataRoot = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data");
        var result = await new GameRootPreflightService(policy).EvaluateAsync(new GameRootPreflightRequest(
            GameEdition.Fallout4, new WorkspacePath("K:\\ExampleWorkspace"), dataRoot,
            new WorkspacePath(Path.Combine(dataRoot.Value, "generated"))), CancellationToken.None);
        Assert(!result.IsAllowed && result.Diagnostics.Any(item => item.Code == "read-write-overlap"),
            "Data/output overlap was accepted.");
    }

    private static async Task TestGameRootPreflightCli()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "workspace", "preflight", "--game", "skyrimse", "--data-root",
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data",
            "--output-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preflight-cli", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("schemaVersion").GetString() == "1" &&
               document.RootElement.GetProperty("edition").GetString() == "skyrimse" &&
               document.RootElement.GetProperty("isAllowed").GetBoolean(),
            "Game/Data preflight CLI did not emit the versioned allowed contract.");
    }

    private static async Task TestReviewedGameIntakeCli()
    {
        var responseRoot =
            NewPipelineOutput("reviewed-intake-response");
        var (runner, output, _) = CreateRunner();
        try
        {
            var exit = await runner.RunAsync(CommandLine.Parse([
                "workspace", "preflight",
                "--game", "skyrimse",
                "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data",
                "--load-order", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-sse.json",
                "--output-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\reviewed-intake-cli-output",
                "--json"
            ]), CancellationToken.None);
            using var document =
                JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            string reviewedDocumentPath = Path.Combine(
                responseRoot.Value,
                "reviewed-intake.json");
            await File.WriteAllTextAsync(
                reviewedDocumentPath,
                output.ToString());
            ReviewedGameIntakeDocumentAuthority reloaded =
                await new FaceGeomHairRegionsDocumentCodec()
                    .LoadReviewedIntakeAsync(
                        new WorkspacePath(
                            reviewedDocumentPath),
                        CancellationToken.None);
            Assert(exit == CommandExitCode.Success &&
                   root.GetProperty("schemaVersion").GetString() == "2" &&
                   root.GetProperty("isAccepted").GetBoolean() &&
                   root.GetProperty("plugins").GetArrayLength() == 1 &&
                   root.GetProperty("bodySidecars").GetArrayLength() ==
                       root.GetProperty("bodySidecarCount").GetInt32() &&
                   root.GetProperty("generatedPlugins").GetArrayLength() ==
                       root.GetProperty("generatedPluginCount").GetInt32() &&
                   root.GetProperty("generatedSidecars").GetArrayLength() ==
                       root.GetProperty("generatedSidecarCount").GetInt32() &&
                   root.GetProperty("loadOrderHash").GetString()?.Length == 64 &&
                   root.GetProperty("intakeFingerprint").GetString()?.Length == 64 &&
                   !root.GetProperty("runtimeAuthority").GetBoolean() &&
                   reloaded.Value.IntakeFingerprint ==
                       ReviewedGameIntakeFingerprintAuthority
                           .Fingerprint(reloaded.Value),
                "The reviewed workspace CLI response was not a loadable exact schema-2 non-runtime intake.");
        }
        finally
        {
            DeleteDirectory(responseRoot.Value);
        }
    }

    private static async Task TestGameRootPreflightUsage()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "workspace", "preflight", "--game", "fallout4", "--output-root",
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preflight-usage", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError && document.RootElement.GetProperty("code").GetString() == "usage-error",
            "Partial game/Data preflight options did not fail with usage error.");
    }

    private static async Task TestGameRootPreflightProtectedRoot()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "workspace", "preflight", "--game", "fallout4", "--data-root", "F:\\ExampleGame\\Data",
            "--output-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preflight-protected", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal && !document.RootElement.GetProperty("isAllowed").GetBoolean() &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "Protected Data root was not refused.");
    }

    private static async Task TestGameRootPreflightCancellation()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => new GameRootPreflightService(policy).EvaluateAsync(
            new GameRootPreflightRequest(GameEdition.Fallout4,
                new WorkspacePath("K:\\ExampleWorkspace"),
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preflight-cancel")), source.Token).AsTask());
    }

    private static async Task TestArchiveConsistencyBothGames()
    {
        var (runner, output, _) = CreateRunner();
        foreach (var (edition, game, pluginName, archiveName) in new[]
        {
            (GameEdition.Fallout4, "fo4", "M2FixtureFO4.esp", "M2Fixture - Main.ba2"),
            (GameEdition.SkyrimSpecialEdition, "sse", "M2FixtureSSE.esp", "M2Fixture.bsa")
        })
        {
            var index = await CreateArchiveConsistencyArtifact(edition, game);
            var plugin = $"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\{game}\\Data\\{pluginName}";
            var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", edition.ToWireName(),
                "--plugin", plugin, "--asset-index", index.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            var archive = document.RootElement.GetProperty("archives")[0];
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("schemaVersion").GetString() == "1" &&
                   document.RootElement.GetProperty("isConsistent").GetBoolean() &&
                   archive.GetProperty("archiveName").GetString() == archiveName &&
                   archive.GetProperty("status").GetString() == "matched",
                $"{edition} archive consistency did not accept its copied fixture.");
            output.GetStringBuilder().Clear();
            DeleteIfExists(index.Value);
        }
    }

    private static async Task TestArchiveConsistencyMismatch()
    {
        var (runner, output, _) = CreateRunner();
        var index = await CreateArchiveConsistencyArtifact(GameEdition.Fallout4, "fo4");
        var tampered = (await File.ReadAllTextAsync(index.Value))
            .Replace("M2Fixture - Main.ba2", "M2Fixture - Missing.ba2", StringComparison.Ordinal);
        await File.WriteAllTextAsync(index.Value, tampered);
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--asset-index", index.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        var codes = document.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()).ToImmutableHashSet();
        Assert(exit == CommandExitCode.ValidationFailure && !document.RootElement.GetProperty("isConsistent").GetBoolean() &&
               codes.Contains("archive-consistency-archive-not-indexed") &&
               codes.Contains("archive-consistency-archive-missing-on-disk"),
            "Archive index/disk mismatch was not refused with actionable diagnostics.");
        DeleteIfExists(index.Value);

        index = await CreateArchiveConsistencyArtifact(GameEdition.Fallout4, "fo4");
        var wrongEdition = (await File.ReadAllTextAsync(index.Value))
            .Replace("M2Fixture - Main.ba2", "M2Fixture - Main.bsa", StringComparison.Ordinal);
        await File.WriteAllTextAsync(index.Value, wrongEdition);
        output.GetStringBuilder().Clear();
        exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--asset-index", index.Value, "--json"]), CancellationToken.None);
        using var wrongEditionDocument = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.ValidationFailure &&
               wrongEditionDocument.RootElement.GetProperty("archives").EnumerateArray()
                   .Any(item => item.GetProperty("status").GetString() == "unsupportedEdition") &&
               wrongEditionDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "archive-consistency-archive-edition-mismatch"),
            "Wrong-edition archive extension was not refused.");
        DeleteIfExists(index.Value);
    }

    private static async Task TestArchiveConsistencySafety()
    {
        var (runner, output, _) = CreateRunner();
        var index = await CreateArchiveConsistencyArtifact(GameEdition.Fallout4, "fo4");
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", "skyrimse",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--asset-index", index.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.ValidationFailure &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "archive-consistency-edition-mismatch"),
            "Archive consistency accepted a wrong edition.");
        output.GetStringBuilder().Clear();

        exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", "fallout4",
            "--plugin", "F:\\ExampleGame\\Data\\Blocked.esp", "--asset-index", index.Value, "--json"]), CancellationToken.None);
        using var protectedDocument = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal &&
               protectedDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "Protected plugin root was not refused.");
        DeleteIfExists(index.Value);
    }

    private static async Task TestArchiveConsistencyUsage()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError && document.RootElement.GetProperty("code").GetString() == "usage-error",
            "Archive consistency did not require both paired paths.");
    }

    private static async Task TestArchiveConsistencyMalformedIndex()
    {
        var (runner, output, _) = CreateRunner();
        var index = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-malformed-index.json");
        DeleteIfExists(index.Value);
        await File.WriteAllTextAsync(index.Value, "{\"schemaVersion\":\"1\"}");
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "preflight", "--game", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--asset-index", index.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.ValidationFailure && !document.RootElement.GetProperty("isConsistent").GetBoolean() &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "archive-consistency-index-entries"),
            "Malformed asset-index artifact was not refused with a structured diagnostic.");
        DeleteIfExists(index.Value);
    }

    private static async Task TestArchiveConsistencyCancellation()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => new ArchiveConsistencyService(policy,
            new WorkspacePath("K:\\ExampleWorkspace")).EvaluateAsync(new ArchiveConsistencyRequest(
            GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\missing-index.json")), source.Token).AsTask());
    }

    private static async Task TestGeneratedArtifactScanBothGames()
    {
        foreach (var (edition, game, plugin, expectedSidecars) in new[]
        {
            (GameEdition.Fallout4, "fo4", "GeneratedFixtureFO4.esp", 5),
            (GameEdition.SkyrimSpecialEdition, "sse", "GeneratedFixtureSSE.esp", 6)
        })
        {
            var root = NewGeneratedScanRoot(game);
            try
            {
                WriteGeneratedScanFixture(root.Value, edition, plugin);
                var (runner, output, _) = CreateRunner();
                var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "scan-generated",
                    "--game", edition.ToWireName(), "--data-root", Path.Combine(root.Value, "Data"), "--json"]),
                    CancellationToken.None);
                using var document = JsonDocument.Parse(output.ToString());
                var plugins = document.RootElement.GetProperty("plugins");
                var sidecars = document.RootElement.GetProperty("sidecars");
                Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("schemaVersion").GetString() == "1",
                    $"{edition} generated scan did not return the versioned success contract.");
                Assert(document.RootElement.GetProperty("markerAuthor").GetString() == GeneratedArtifactMarkers.ReferenceAuthor,
                    "Generated marker author changed.");
                Assert(plugins.GetArrayLength() == 1 && plugins[0].GetProperty("plugin").GetString() == plugin,
                    $"{edition} generated plugin was not discovered.");
                Assert(plugins[0].GetProperty("readSucceeded").GetBoolean() &&
                       plugins[0].GetProperty("npcFormIds").GetArrayLength() == 0,
                    $"{edition} generated plugin reader result changed.");
                Assert(sidecars.GetArrayLength() == expectedSidecars &&
                       sidecars.EnumerateArray().All(item => item.GetProperty("sha256").GetString()?.Length == 64),
                    $"{edition} FaceGen sidecar inventory was incomplete or unbound.");
                Assert(document.RootElement.GetProperty("diagnostics").GetArrayLength() == 0,
                    $"{edition} generated scan emitted unexpected diagnostics.");
            }
            finally
            {
                DeleteDirectory(root.Value);
            }
        }
    }

    private static async Task TestGeneratedArtifactScanUsage()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "scan-generated", "--json"]),
            CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError && document.RootElement.GetProperty("code").GetString() == "usage-error",
            "Generated artifact scan did not require edition and Data-root options.");
    }

    private static async Task TestGeneratedArtifactScanMalformedPlugin()
    {
        var root = NewGeneratedScanRoot("malformed");
        try
        {
            File.WriteAllBytes(Path.Combine(root.Value, "Data", "Malformed.esp"), [0x54, 0x45, 0x53]);
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "scan-generated", "--game", "fallout4",
                "--data-root", Path.Combine(root.Value, "Data"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("isValid").GetBoolean() &&
                   document.RootElement.GetProperty("plugins").GetArrayLength() == 0 &&
                   document.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                       item.GetProperty("code").GetString() == "generated-scan-plugin-header-invalid" &&
                       item.GetProperty("severity").GetString() == "warning"),
                "Malformed unmarked plugin header did not produce a bounded warning.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestGeneratedArtifactScanProtectedRoot()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["workspace", "scan-generated", "--game", "fallout4",
            "--data-root", "F:\\ExampleGame\\Data", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal && !document.RootElement.GetProperty("isValid").GetBoolean() &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "Protected generated-artifact scan root was not refused.");
    }

    private static async Task TestGeneratedArtifactScanCancellation()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => new GeneratedArtifactScanService(policy,
            new BethesdaPluginReader(), new WorkspacePath("K:\\ExampleWorkspace")).ScanAsync(
            new GeneratedArtifactScanRequest(GameEdition.Fallout4,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data")),
            source.Token).AsTask());
    }

    private static async Task TestBodySidecarBothGames()
    {
        foreach (var (edition, fileName, json, expectedMorphs, expectedKeyed) in new[]
        {
            (GameEdition.Fallout4, "GeneratedFixtureFO4.bssliders", """
             {
               "version": 1,
               "plugin": "GeneratedFixtureFO4.esp",
               "npcs": {
                 "Fallout4.esm|000800": {
                   "editorId": "GeneratedFo4Npc",
                   "bodyMorphs": { "CBBE Breast": 0.25, "CBBE Butt": -0.1 },
                   "skinTemplateId": "Vanilla CBBE",
                   "gender": "female",
                   "overlays": [{ "template": "Tattoo01", "priority": 0, "tint": [1,0,0,1], "offsetUV": [0,0], "scaleUV": [1,1] }]
                 }
               }
             }
             """, 2, 0),
            (GameEdition.SkyrimSpecialEdition, "GeneratedFixtureSSE.bssliders", """
             {
               "version": 11,
               "plugin": "GeneratedFixtureSSE.esp",
               "npcs": {
                 "Skyrim.esm|000800": {
                   "editorId": "GeneratedSseNpc",
                   "bodyMorphs": { "CBBE Breast": 0.35 },
                   "bodyMorphsKeyed": { "CBBE Breast": { "Base": 0.2, "Outfit": 0.15 } },
                   "gender": "female",
                   "sseBodyOverlays": [{ "node": "NPC Pelvis [Pelv]", "diffuse": "textures\\actors\\character\\tattoo.dds", "alpha": 0.8 }],
                   "sseNodeTransforms": [{ "node": "NPC Spine [Spn0]", "s": 1.02, "sm": 0, "p": [0,0,0], "r": [0,0,0] }],
                   "sseHairColor": 1122867,
                   "sseCustomMorphs": [{ "name": "Smile", "value": 0.2 }],
                   "sseSculpt": [{ "index": 3, "dx": 0.01, "dy": -0.02, "dz": 0.03 }],
                   "sseTintTextures": [{ "index": 1, "texture": "textures\\actors\\character\\tint.dds" }]
                 }
               }
             }
             """, 1, 1)
        })
        {
            var path = NewBodySidecarFile(fileName, json);
            try
            {
                var (runner, output, _) = CreateRunner();
                var exit = await runner.RunAsync(CommandLine.Parse(["body", "sidecar", "inspect",
                    "--game", edition.ToWireName(), "--file", path.Value, "--json"]), CancellationToken.None);
                using var document = JsonDocument.Parse(output.ToString());
                var root = document.RootElement;
                Assert(exit == CommandExitCode.Success && root.GetProperty("isValid").GetBoolean() &&
                       root.GetProperty("roundTripPreserved").GetBoolean() &&
                       root.GetProperty("canonicalSha256").GetString()?.Length == 64,
                    $"{edition} BodySlide sidecar did not pass the typed round-trip contract.");
                var npc = root.GetProperty("npcs")[0];
                Assert(npc.GetProperty("bodyMorphs").GetArrayLength() == expectedMorphs &&
                       npc.GetProperty("bodyMorphsKeyed").GetArrayLength() == expectedKeyed,
                    $"{edition} BodySlide morph maps were not retained.");
                Assert(root.GetProperty("diagnostics").GetArrayLength() == 0,
                    $"{edition} BodySlide sidecar emitted unexpected diagnostics.");
            }
            finally
            {
                DeleteIfExists(path.Value);
            }
        }
    }

    private static async Task TestBodySidecarMalformed()
    {
        string excessiveArray = "[" + string.Join(',',
            Enumerable.Repeat("{}", 100_001)) + "]";
        foreach (var (fileName, json, reason) in new[]
        {
            ("Duplicate.bssliders", """{"version":1,"plugin":"Duplicate.esp","plugin":"Duplicate.esp","npcs":{}}""", "body-sidecar-duplicate-key"),
            ("NonFinite.bssliders", """{"version":1,"plugin":"NonFinite.esp","npcs":{"Fallout4.esm|000800":{"bodyMorphs":{"x":1e999}}}}""", "body-sidecar-number"),
            ("ExcessiveArray.bssliders",
                "{\"version\":11,\"plugin\":\"ExcessiveArray.esp\",\"npcs\":{\"Skyrim.esm|000800\":{\"sseCustomMorphs\":" +
                excessiveArray + "}}}",
                "body-sidecar-array-count-limit")
        })
        {
            var path = NewBodySidecarFile(fileName, json);
            try
            {
                var (runner, output, _) = CreateRunner();
                var exit = await runner.RunAsync(CommandLine.Parse(["body", "sidecar", "inspect",
                    "--game", fileName == "ExcessiveArray.bssliders" ? "skyrimse" : "fallout4",
                    "--file", path.Value, "--json"]), CancellationToken.None);
                using var document = JsonDocument.Parse(output.ToString());
                Assert(exit == CommandExitCode.ValidationFailure && !document.RootElement.GetProperty("isValid").GetBoolean() &&
                       document.RootElement.GetProperty("npcs").GetArrayLength() == 0 &&
                       document.RootElement.GetProperty("diagnostics").EnumerateArray()
                           .Any(item => item.GetProperty("code").GetString() == reason),
                    $"Malformed BodySlide sidecar '{fileName}' did not fail closed without partial state.");
            }
            finally
            {
                DeleteIfExists(path.Value);
            }
        }
    }

    private static async Task TestBodySidecarProtectedRoot()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["body", "sidecar", "inspect", "--game", "fallout4",
            "--file", "F:\\ExampleGame\\Data\\GeneratedFixture.bssliders", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal && !document.RootElement.GetProperty("isValid").GetBoolean() &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "Protected BodySlide sidecar root was not refused.");
    }

    private static async Task TestBodySidecarCancellation()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => new BodySidecarInspectionService(policy,
            new WorkspacePath("K:\\ExampleWorkspace")).InspectAsync(new BodySidecarInspectRequest(
            GameEdition.Fallout4, new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\missing.bssliders")),
            source.Token).AsTask());
    }

    private static async Task TestBodySlideSliderPresetInspectionCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m7-bodyslide-sliderpreset-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var valid = new WorkspacePath(Path.Combine(root.Value,
                "ZenitharMasterpiece.xml"));
            await File.WriteAllTextAsync(valid.Value, """
                <?xml version="1.0" encoding="UTF-8"?>
                <SliderPresets>
                  <Preset name="[DevonixS] - Zenithar's Masterpiece" set="UBE SE 2.0 Release Body">
                    <Group name="UBE" />
                    <Group name="UBE Female" />
                    <SetSlider name="Breasts" size="small" value="25" />
                    <SetSlider name="Breasts" size="big" value="75.5" />
                  </Preset>
                </SliderPresets>
                """);

            var (runner, output, error) = CreateRunner();
            CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
                ["body", "sliders", "inspect-preset", "--game", "skyrimse",
                    "--preset-xml", valid.Value, "--json"]),
                CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            JsonElement body = document.RootElement;
            Assert(exit == CommandExitCode.Success &&
                   error.ToString().Length == 0 &&
                   body.GetProperty("schemaVersion").GetString() == "1" &&
                   body.GetProperty("isValid").GetBoolean() &&
                   body.GetProperty("presetName").GetString() ==
                   "[DevonixS] - Zenithar's Masterpiece" &&
                   body.GetProperty("sliderSet").GetString() ==
                   "UBE SE 2.0 Release Body" &&
                   string.Equals(
                       body.GetProperty("sourceSha256").GetString(),
                       Hash(valid).Value,
                       StringComparison.OrdinalIgnoreCase) &&
                   body.GetProperty("groups").EnumerateArray()
                       .Any(item => item.GetString() == "UBE Female") &&
                   Math.Abs(body.GetProperty("sliders")[1]
                       .GetProperty("value").GetSingle() - 75.5F) < 0.0001F,
                "BodySlide SliderPreset inspection did not preserve the native XML contract.");

            await AssertSliderPresetRefused(
                Path.Combine(root.Value, "wrong-extension.txt"),
                await File.ReadAllTextAsync(valid.Value),
                CommandExitCode.ValidationFailure,
                "bodyslide-preset-extension");
            await AssertSliderPresetRefused(
                Path.Combine(root.Value, "duplicate.xml"),
                """
                <SliderPresets><Preset name="Dup" set="UBE SE 2.0 Release Body">
                  <SetSlider name="Breasts" size="small" value="10" />
                  <SetSlider name="Breasts" size="small" value="11" />
                </Preset></SliderPresets>
                """,
                CommandExitCode.ValidationFailure,
                "occurs more than once");
            await AssertSliderPresetRefused(
                Path.Combine(root.Value, "empty-set.xml"),
                """
                <SliderPresets><Preset name="EmptySet" set="">
                  <SetSlider name="Breasts" size="small" value="10" />
                </Preset></SliderPresets>
                """,
                CommandExitCode.ValidationFailure,
                "Preset set must be non-empty");
            await AssertSliderPresetRefused(
                Path.Combine(root.Value, "malformed.xml"),
                "<SliderPresets><Preset",
                CommandExitCode.ValidationFailure,
                "bodyslide-preset-xml-invalid");

            var (outsideRunner, outsideOutput, _) = CreateRunner();
            CommandExitCode outside = await outsideRunner.RunAsync(
                CommandLine.Parse(["body", "sliders", "inspect-preset",
                    "--game", "skyrimse",
                    "--preset-xml", "L:\\outside-k-bodyslide.xml",
                    "--json"]),
                CancellationToken.None);
            Assert(outside == CommandExitCode.SecurityRefusal &&
                   outsideOutput.ToString().Contains(
                       "bodyslide-preset-outside-lab",
                       StringComparison.Ordinal),
                "BodySlide SliderPreset inspection accepted an outside-K path.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }

        async Task AssertSliderPresetRefused(
            string path,
            string xml,
            CommandExitCode expectedExit,
            string expectedText)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, xml);
            var (runner, output, _) = CreateRunner();
            CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
                ["body", "sliders", "inspect-preset", "--game", "skyrimse",
                    "--preset-xml", path, "--json"]),
                CancellationToken.None);
            Assert(exit == expectedExit &&
                   output.ToString().Contains(expectedText,
                       StringComparison.Ordinal),
                $"BodySlide SliderPreset inspection did not refuse {Path.GetFileName(path)}.");
        }
    }

    private static async Task TestBodySlideTriResolution()
    {
        var tri = NewBodySlideTriFile("resolution.tri", BuildBodySlideTri());
        var fixtures = new[]
        {
            (GameEdition.Fallout4, "fo4-body-sliders.json", "{\"BodyMorphs\":{\"BigBelly\":0.5,\"Missing\":0.25,\"WeightThin\":0.5}}"),
            (GameEdition.SkyrimSpecialEdition, "sse-body-sliders.jslot", "{\"bodyMorphs\":[{\"name\":\"BigBelly\",\"keys\":[{\"key\":\"NPCManager\",\"value\":0.4}]},{\"name\":\"Missing\",\"keys\":[{\"key\":\"NPCManager\",\"value\":0.2}]},{\"name\":\"WeightThin\",\"keys\":[{\"key\":\"NPCManager\",\"value\":0.5}]}]}")
        };
        foreach (var (edition, fileName, content) in fixtures)
        {
            var preset = NewBodySlidePresetFile(fileName, content);
            try
            {
                var (runner, output, _) = CreateRunner();
                var exit = await runner.RunAsync(CommandLine.Parse(["body", "sliders", "resolve",
                    "--game", edition.ToWireName(), "--tri", tri.Value, "--preset", preset.Value, "--json"]),
                    CancellationToken.None);
                using var document = JsonDocument.Parse(output.ToString());
                var root = document.RootElement;
                Assert(exit == CommandExitCode.Success && root.GetProperty("isValid").GetBoolean(),
                    $"{edition} BodySlide resolution did not validate.");
                Assert(root.GetProperty("channels").GetArrayLength() == 1,
                    $"{edition} BodySlide resolution emitted an unexpected channel count.");
                var channel = root.GetProperty("channels")[0];
                Assert(channel.GetProperty("shape").GetString() == "BaseFemaleBody" &&
                       channel.GetProperty("slider").GetString() == "BigBelly" &&
                       channel.GetProperty("offsets")[0].GetProperty("x").GetSingle() == 1F,
                    $"{edition} BodySlide channel was not typed.");
                Assert(root.GetProperty("missingSliders").EnumerateArray().Any(item => item.GetString() == "Missing") &&
                       root.GetProperty("excludedSliders").EnumerateArray().Any(item => item.GetString() == "WeightThin"),
                    $"{edition} BodySlide missing/excluded slider reporting changed.");
                Assert(root.GetProperty("diagnostics").EnumerateArray().Any(item =>
                           item.GetProperty("code").GetString() == "body-tri-duplicate-morph"),
                    $"{edition} duplicate PIRT morph warning was not retained.");
            }
            finally
            {
                DeleteIfExists(preset.Value);
            }
        }
        DeleteIfExists(tri.Value);
    }

    private static async Task TestBodySlideTriSafety()
    {
        foreach (var (name, bytes, expectedMessage) in new[]
        {
            ("malformed.tri", "PIRT"u8.ToArray(), "Malformed PIRT"),
            ("nonfinite.tri", BuildBodySlideTri(float.NaN), "Non-finite PIRT"),
            ("trailing.tri", BuildBodySlideTri().Concat(new byte[] { 0xAA }).ToArray(), "Trailing PIRT")
        })
        {
            var malformed = NewBodySlideTriFile(name, bytes);
            try
            {
                var (runner, output, _) = CreateRunner();
                var preset = NewBodySlidePresetFile($"{name}-preset.json", "{\"BodyMorphs\":{\"BigBelly\":0.5}}");
                try
                {
                    var exit = await runner.RunAsync(CommandLine.Parse(["body", "sliders", "resolve",
                        "--game", "fallout4", "--tri", malformed.Value, "--preset", preset.Value, "--json"]),
                        CancellationToken.None);
                    using var document = JsonDocument.Parse(output.ToString());
                    Assert(exit == CommandExitCode.ValidationFailure && !document.RootElement.GetProperty("isValid").GetBoolean() &&
                           document.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                               item.GetProperty("code").GetString() == "body-tri-invalid"),
                        $"{expectedMessage} was not rejected fail-closed.");
                }
                finally
                {
                    DeleteIfExists(preset.Value);
                }
            }
            finally
            {
                DeleteIfExists(malformed.Value);
            }
        }

        var (protectedRunner, protectedOutput, _) = CreateRunner();
        var protectedExit = await protectedRunner.RunAsync(CommandLine.Parse(["body", "sliders", "resolve",
            "--game", "fallout4", "--tri", "F:\\ExampleGame\\Data\\body.tri",
            "--preset", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json", "--json"]),
            CancellationToken.None);
        using var protectedDocument = JsonDocument.Parse(protectedOutput.ToString());
        Assert(protectedExit == CommandExitCode.SecurityRefusal && !protectedDocument.RootElement.GetProperty("isValid").GetBoolean() &&
               protectedDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                   item.GetProperty("code").GetString() == "body-tri-outside-lab"),
            "Protected/outside-K PIRT was not refused.");
    }

    private static async Task TestSseBodyWeightResolution()
    {
        foreach (var (name, baseDigit, weight, expectedWeight) in new[]
        {
            ("zero-base.json", 0, 50F, 0.5F),
            ("one-base.json", 1, 25F, 0.75F),
            ("clamped.json", 0, 150F, 1F)
        })
        {
            var input = NewBodyWeightManifest(name, $"{{\"version\":1,\"game\":\"skyrimse\",\"gender\":\"female\",\"weightPercent\":{weight.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"weightSliderFlags\":2,\"baseDigit\":{baseDigit},\"baseVertices\":[[0,0,0],[1,1,1]],\"twinVertices\":[[2,0,0],[1,3,1]]}}");
            try
            {
                var (runner, output, _) = CreateRunner();
                var exit = await runner.RunAsync(CommandLine.Parse(["body", "weight", "resolve",
                    "--game", "skyrimse", "--input", input.Value, "--json"]), CancellationToken.None);
                using var document = JsonDocument.Parse(output.ToString());
                var root = document.RootElement;
                Assert(exit == CommandExitCode.Success && root.GetProperty("isValid").GetBoolean() &&
                       root.GetProperty("sliderEnabled").GetBoolean() && root.GetProperty("applied").GetBoolean(),
                    $"SSE body-weight {name} did not apply.");
                Assert(Math.Abs(root.GetProperty("channelWeight").GetSingle() - expectedWeight) < 0.000001F &&
                       root.GetProperty("deltas").GetArrayLength() == 2,
                    $"SSE body-weight {name} produced the wrong channel math.");
                var first = root.GetProperty("deltas")[0];
                Assert(first.GetProperty("index").GetInt32() == 0 &&
                       first.GetProperty("delta").GetProperty("x").GetSingle() == 2F,
                    $"SSE body-weight {name} did not preserve twin-minus-base deltas.");
            }
            finally
            {
                DeleteIfExists(input.Value);
            }
        }

        var disabled = NewBodyWeightManifest("disabled.json", "{\"version\":1,\"game\":\"skyrimse\",\"gender\":\"female\",\"weightPercent\":50,\"weightSliderFlags\":0,\"baseDigit\":0,\"baseVertices\":[[0,0,0]],\"twinVertices\":[[1,0,0]]}");
        try
        {
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["body", "weight", "resolve",
                "--game", "skyrimse", "--input", disabled.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("isValid").GetBoolean() &&
                   !document.RootElement.GetProperty("applied").GetBoolean() &&
                   document.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                       item.GetProperty("code").GetString() == "body-weight-slider-disabled"),
                "Disabled Skyrim ARMA weight-slider flag was not a safe no-op.");
        }
        finally
        {
            DeleteIfExists(disabled.Value);
        }
    }

    private static async Task TestSseBodyWeightSafety()
    {
        var malformed = NewBodyWeightManifest("malformed.json", "{\"version\":1}");
        try
        {
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["body", "weight", "resolve",
                "--game", "skyrimse", "--input", malformed.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.ValidationFailure && !document.RootElement.GetProperty("isValid").GetBoolean() &&
                   document.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                       item.GetProperty("code").GetString() == "body-weight-format-invalid"),
                "Malformed body-weight manifest was not rejected.");
        }
        finally
        {
            DeleteIfExists(malformed.Value);
        }

        var mismatch = NewBodyWeightManifest("mismatch.json", "{\"version\":1,\"game\":\"skyrimse\",\"gender\":\"female\",\"weightPercent\":50,\"weightSliderFlags\":2,\"baseDigit\":0,\"baseVertices\":[[0,0,0]],\"twinVertices\":[]}");
        try
        {
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["body", "weight", "resolve",
                "--game", "skyrimse", "--input", mismatch.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.ValidationFailure &&
                   document.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                       item.GetProperty("code").GetString() == "body-weight-vertex-count-mismatch"),
                "Mismatched body-weight vertex arrays were not rejected.");
        }
        finally
        {
            DeleteIfExists(mismatch.Value);
        }

        var (protectedRunner, protectedOutput, _) = CreateRunner();
        var protectedExit = await protectedRunner.RunAsync(CommandLine.Parse(["body", "weight", "resolve",
            "--game", "skyrimse", "--input", "F:\\ExampleGame\\Data\\body-weight.json", "--json"]),
            CancellationToken.None);
        using var protectedDocument = JsonDocument.Parse(protectedOutput.ToString());
        Assert(protectedExit == CommandExitCode.SecurityRefusal && !protectedDocument.RootElement.GetProperty("isValid").GetBoolean() &&
               protectedDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                   item.GetProperty("code").GetString() == "body-weight-outside-lab"),
            "Protected/outside-K body-weight manifest was not refused.");
    }

    private static WorkspacePath NewBodyWeightManifest(string fileName, string content)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-body-weight-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        DeleteIfExists(path.Value);
        File.WriteAllText(path.Value, content);
        return path;
    }

    private static async Task TestBodyOverlayResolution()
    {
        var fo4 = "[{\"template\":\"Tattoo.High\",\"priority\":10,\"tint\":[1,0.5,0,0.75],\"offsetUV\":[0.1,-0.2],\"scaleUV\":[1.2,0.8],\"slots\":[{\"slot\":3,\"material\":\"Materials\\\\Actors\\\\Tattoo.bgem\"}]},{\"template\":\"Tattoo.Low\",\"priority\":2}]";
        var (fo4Runner, fo4Output, _) = CreateRunner();
        var fo4Exit = await fo4Runner.RunAsync(CommandLine.Parse(["body", "overlay", "patch", "--game", "fallout4", "--npc", "0x800", "--layers", fo4, "--json"]), CancellationToken.None);
        using var fo4Document = JsonDocument.Parse(fo4Output.ToString());
        var fo4Layers = fo4Document.RootElement.GetProperty("layers");
        Assert(fo4Exit == CommandExitCode.Success && fo4Document.RootElement.GetProperty("isValid").GetBoolean() &&
               fo4Layers.GetArrayLength() == 2 && fo4Layers[0].GetProperty("template").GetString() == "Tattoo.Low" &&
               fo4Layers[1].GetProperty("template").GetString() == "Tattoo.High" &&
               fo4Layers[1].GetProperty("slots")[0].GetProperty("slot").GetInt32() == 3 &&
               fo4Layers[1].GetProperty("tint")[3].GetSingle() == 0.75F,
            "FO4 overlays did not preserve typed transforms, template slots, and priority ordering.");

        var sse = "[{\"node\":\"Body [Ovl1]\",\"diffuse\":\"textures\\\\actors\\\\character\\\\overlays\\\\body.dds\",\"normal\":\"textures\\\\actors\\\\character\\\\overlays\\\\body_n.dds\"},{\"node\":\"Hands [Ovl0]\",\"diffuse\":\"textures\\\\actors\\\\character\\\\overlays\\\\hands.dds\",\"tint\":[1,0.5,0.25,1],\"alpha\":0.4}]";
        var (sseRunner, sseOutput, _) = CreateRunner();
        var sseExit = await sseRunner.RunAsync(CommandLine.Parse(["body", "overlay", "patch", "--game", "skyrimse", "--npc", "0x801", "--layers", sse, "--json"]), CancellationToken.None);
        using var sseDocument = JsonDocument.Parse(sseOutput.ToString());
        var sseLayers = sseDocument.RootElement.GetProperty("layers");
        Assert(sseExit == CommandExitCode.Success && sseDocument.RootElement.GetProperty("isValid").GetBoolean() &&
               sseLayers.GetArrayLength() == 2 && sseLayers[0].GetProperty("node").GetString() == "Hands [Ovl0]" &&
               sseLayers[0].GetProperty("target").GetString() == "hands" &&
               Math.Abs(sseLayers[0].GetProperty("alpha").GetSingle() - 0.4F) < 0.000001F,
            "Skyrim overlays did not preserve node-derived target routing, alpha, and Ovl ordering.");
    }

    private static async Task TestBodyOverlaySafety()
    {
        var invalidTexture = "[{\"node\":\"Body [Ovl0]\",\"diffuse\":\"..\\\\escape.dds\"}]";
        var (textureRunner, textureOutput, _) = CreateRunner();
        var textureExit = await textureRunner.RunAsync(CommandLine.Parse(["body", "overlay", "patch", "--game", "skyrimse", "--npc", "0x800", "--layers", invalidTexture, "--json"]), CancellationToken.None);
        using var textureDocument = JsonDocument.Parse(textureOutput.ToString());
        Assert(textureExit == CommandExitCode.ValidationFailure && !textureDocument.RootElement.GetProperty("isValid").GetBoolean() &&
               textureDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "body-overlay-diffuse-invalid"),
            "Traversal texture paths were not refused.");

        var mixed = "[{\"template\":\"Tattoo\",\"node\":\"Body [Ovl0]\",\"diffuse\":\"textures\\\\x.dds\"}]";
        var (mixedRunner, _, mixedError) = CreateRunner();
        var mixedExit = await mixedRunner.RunAsync(CommandLine.Parse(["body", "overlay", "patch", "--game", "fallout4", "--npc", "0x800", "--layers", mixed, "--json"]), CancellationToken.None);
        Assert(mixedExit == CommandExitCode.UsageError && mixedError.ToString().Contains("usage-error", StringComparison.Ordinal),
            "Cross-game overlay fields were not rejected at the CLI boundary.");

        var (protectedRunner, _, protectedError) = CreateRunner();
        var protectedExit = await protectedRunner.RunAsync(CommandLine.Parse(["body", "overlay", "patch", "--game", "skyrimse", "--npc", "0x800", "--layers", "@F:\\ExampleGame\\Data\\overlays.json", "--json"]), CancellationToken.None);
        Assert(protectedExit == CommandExitCode.UsageError && protectedError.ToString().Contains("usage-error", StringComparison.Ordinal),
            "Protected/outside-K overlay layer files were not refused.");
    }

    private static async Task TestSkyrimBodyTransformApply()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-sse-body-transform-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var input = new WorkspacePath(Path.Combine(root.Value, "source.jslot"));
        var output = new WorkspacePath(Path.Combine(root.Value, "applied.jslot"));
        var repeat = new WorkspacePath(Path.Combine(root.Value, "applied-repeat.jslot"));
        var invalid = new WorkspacePath(Path.Combine(root.Value, "invalid-output.jslot"));
        var source = """
            {
              "marker": { "keep": true },
              "transforms": [{ "firstPerson": false, "node": "NPC Spine [Spn0]", "keys": [{ "name": "RSMTransform", "values": [
                { "key": 30, "type": 4, "index": 2, "data": 1.02 },
                { "key": 31, "type": 4, "index": 0, "data": 0.1 }, { "key": 31, "type": 4, "index": 1, "data": 0.2 }, { "key": 31, "type": 4, "index": 2, "data": 0.3 },
                { "key": 32, "type": 4, "index": 0, "data": 1 }, { "key": 32, "type": 4, "index": 1, "data": 0 }, { "key": 32, "type": 4, "index": 2, "data": 0 },
                { "key": 32, "type": 4, "index": 3, "data": 0 }, { "key": 32, "type": 4, "index": 4, "data": 1 }, { "key": 32, "type": 4, "index": 5, "data": 0 },
                { "key": 32, "type": 4, "index": 6, "data": 0 }, { "key": 32, "type": 4, "index": 7, "data": 0 }, { "key": 32, "type": 4, "index": 8, "data": 1 },
                { "key": 33, "type": 3, "index": 3, "data": 0 }
              ] }] }],
              "skinOverrides": [{ "firstPerson": false, "slotMask": 32, "values": [
                { "key": 9, "type": 2, "index": 0, "data": "textures\\actors\\character\\body.dds" },
                { "key": 9, "type": 2, "index": 2, "data": "textures\\actors\\character\\body_detail.dds" },
                { "key": 7, "type": 3, "index": -1, "data": -65536 }, { "key": 8, "type": 4, "index": -1, "data": 0.75 }
              ] }]
            }
            """;
        await File.WriteAllTextAsync(input.Value, source);
        var transforms = "[{\"node\":\"NPC Spine [Spn0]\",\"scale\":1.1,\"scaleMode\":1,\"position\":[0,0.2,0],\"rotation\":[1,0,0,0,1,0,0,0,1]}]";
        var skins = "[{\"slotMask\":32,\"textures\":{\"0\":\"textures\\\\actors\\\\character\\\\body_new.dds\",\"2\":\"textures\\\\actors\\\\character\\\\detail_new.dds\"},\"tint\":[1,0.5,0.25,0.8],\"alpha\":0.6}]";
        try
        {
            var (runner, response, error) = CreateRunner();
            string[] args = ["body", "transforms", "apply", "--game", "skyrimse", "--npc", "0x123", "--preset", input.Value,
                "--transforms", transforms, "--skin-overrides", skins, "--output", output.Value, "--json"];
            var exit = await runner.RunAsync(CommandLine.Parse(args), CancellationToken.None);
            using var document = JsonDocument.Parse(response.ToString());
            var result = document.RootElement;
            Assert(exit == CommandExitCode.Success && result.GetProperty("applied").GetBoolean() && result.GetProperty("isValid").GetBoolean(),
                $"Typed SSE body transform apply failed: {error}");
            var outputJson = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value)).RootElement;
            Assert(outputJson.GetProperty("marker").GetProperty("keep").GetBoolean(), "Unrelated jslot JSON was not preserved.");
            Assert(outputJson.GetProperty("transforms")[0].GetProperty("keys")[0].GetProperty("values")[0].GetProperty("data").GetSingle() == 1.1F,
                "Scale key 30 was not replaced.");
            Assert(outputJson.GetProperty("skinOverrides")[0].GetProperty("values").EnumerateArray()
                .Any(item => item.GetProperty("key").GetInt32() == 9 && item.GetProperty("index").GetInt32() == 2 &&
                             item.GetProperty("data").GetString() == "textures\\actors\\character\\detail_new.dds"),
                "Skin texture key 9/index 2 was not replaced.");

            var (repeatRunner, repeatResponse, _) = CreateRunner();
            var repeatArgs = args[..^3].Append("--output").Append(repeat.Value).Append("--json").ToArray();
            var repeatExit = await repeatRunner.RunAsync(CommandLine.Parse(repeatArgs), CancellationToken.None);
            using var repeatDocument = JsonDocument.Parse(repeatResponse.ToString());
            Assert(repeatExit == CommandExitCode.Success && repeatDocument.RootElement.GetProperty("outputSha256").GetString() ==
                result.GetProperty("outputSha256").GetString() && Convert.ToHexString(await File.ReadAllBytesAsync(output.Value)) ==
                Convert.ToHexString(await File.ReadAllBytesAsync(repeat.Value)), "SSE body transform output was not deterministic.");

            var (existingRunner, existingResponse, _) = CreateRunner();
            var existingExit = await existingRunner.RunAsync(CommandLine.Parse(["body", "transforms", "apply", "--game", "skyrimse", "--npc", "0x123",
                "--preset", input.Value, "--output", output.Value, "--json"]), CancellationToken.None);
            using var existingDocument = JsonDocument.Parse(existingResponse.ToString());
            Assert(existingExit == CommandExitCode.SecurityRefusal && !existingDocument.RootElement.GetProperty("applied").GetBoolean() &&
                existingDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "sse-transform-output-exists"),
                "Existing RaceMenu body-transform output was allowed to overwrite.");

            var (outsideRunner, outsideResponse, _) = CreateRunner();
            var outsideExit = await outsideRunner.RunAsync(CommandLine.Parse(["body", "transforms", "apply", "--game", "skyrimse", "--npc", "0x123",
                "--preset", input.Value, "--output", "F:\\ExampleGame\\body-transform.jslot", "--json"]), CancellationToken.None);
            using var outsideDocument = JsonDocument.Parse(outsideResponse.ToString());
            Assert(outsideExit == CommandExitCode.SecurityRefusal && !outsideDocument.RootElement.GetProperty("applied").GetBoolean(),
                "Outside-K RaceMenu body-transform output was not refused.");

            var badInput = new WorkspacePath(Path.Combine(root.Value, "unsupported.jslot"));
            await File.WriteAllTextAsync(badInput.Value, source.Replace("\"key\": 30", "\"key\": 40", StringComparison.Ordinal));
            var (badRunner, badResponse, _) = CreateRunner();
            var badExit = await badRunner.RunAsync(CommandLine.Parse(["body", "transforms", "apply", "--game", "skyrimse", "--npc", "0x123",
                "--preset", badInput.Value, "--output", invalid.Value, "--json"]), CancellationToken.None);
            using var badDocument = JsonDocument.Parse(badResponse.ToString());
            Assert(badExit == CommandExitCode.ValidationFailure && !badDocument.RootElement.GetProperty("applied").GetBoolean() &&
                badDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "sse-transform-key-unsupported"),
                "Unsupported RaceMenu transform key was not surfaced as a blocking diagnostic.");
            Assert(!File.Exists(invalid.Value), "Invalid RaceMenu body transform input wrote an output.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestSkyrimOverlayBake()
    {
        var outputPath = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-sse-fold-tests\\fold.dds");
        DeleteIfExists(outputPath.Value);
        var manifest = JsonSerializer.Serialize(new
        {
            @base = new { width = 1, height = 1, pixels = FoldBasePixels },
            facetint = new { width = 1, height = 1, pixels = FoldFacetintPixels },
            layers = new object[]
            {
                new { source = "skeeMask", layerType = 1, blend = "normal", color = FoldSkeeColor, opacity = 0.5d,
                    texture = new { width = 1, height = 1, pixels = FoldSkeePixels } },
                new { source = "faceOverlay", node = "Face [Ovl1]", color = FoldFaceColor, opacity = 0.5d,
                    texture = new { width = 1, height = 1, pixels = FoldRedPixels } },
                new { source = "faceOverlay", node = "Face [Ovl0]", color = FoldFaceColor, opacity = 1d,
                    texture = new { width = 1, height = 1, pixels = FoldBluePixels } }
            }
        });
        var (runner, output, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["body", "overlay", "bake", "--game", "skyrimse",
            "--layers", manifest, "--output", outputPath.Value, "--json"]), CancellationToken.None);
        using var response = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && response.RootElement.GetProperty("written").GetBoolean() && File.Exists(outputPath.Value),
            $"Skyrim overlay bake did not write a valid artifact: {error}");
        var bytes = File.ReadAllBytes(outputPath.Value);
        Assert(Encoding.ASCII.GetString(bytes, 0, 4) == "DDS " && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)) == 1 &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)) == 1,
            "Skyrim overlay bake did not emit a deterministic DDS header.");
        // Base -> skee green (coverage .25) = (.15,.55,.45); Ovl0 blue replaces; Ovl1 red coverage .25 overlays it.
        Assert(bytes[128] == 191 && bytes[129] == 0 && bytes[130] == 64 && bytes[131] == 128,
            $"Unexpected folded BGRA bytes: {Convert.ToHexString(bytes.AsSpan(128, 4))}.");
        Assert(response.RootElement.GetProperty("orderedLayers")[0].GetProperty("node").ValueKind == JsonValueKind.Null &&
               response.RootElement.GetProperty("orderedLayers")[1].GetProperty("node").GetString() == "Face [Ovl0]" &&
               response.RootElement.GetProperty("orderedLayers")[2].GetProperty("node").GetString() == "Face [Ovl1]",
            "Skyrim overlay bake did not group skee layers before node-ordered face overlays.");
        DeleteIfExists(outputPath.Value);

        var multiply = new SkyrimOverlayFoldService().Fold(
            new SkyrimOverlayFoldRequest(
                new SkyrimRgbaRaster(1, 1, ImmutableArray.Create(0.8d, 0.8d, 0.8d, 1d)),
                [new SkyrimOverlayLayerInput(SkyrimOverlaySource.SkeeMask, null, 0, SkyrimOverlayBlendMode.Multiply,
                    ImmutableArray.Create(1d, 1d, 1d, 1d), 0.5d,
                    new SkyrimRgbaRaster(1, 1, ImmutableArray.Create(0.2d, 0.2d, 0.2d, 1d)), 0)], null, null),
            CancellationToken.None);
        Assert(multiply.IsSuccess && multiply.OutputDds![128] == 122 && multiply.OutputDds[129] == 122 && multiply.OutputDds[130] == 122,
            "Skee non-normal blend incorrectly folded external opacity into source color.");
    }

    private static async Task TestSkyrimOverlayBakeSafety()
    {
        var outputPath = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-sse-fold-tests\\existing.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath.Value)!);
        File.WriteAllBytes(outputPath.Value, [1, 2, 3]);
        try
        {
            var malformed = "{\"base\":{\"width\":1,\"height\":1,\"pixels\":[0,0,0,1]},\"layers\":[{\"source\":\"faceOverlay\",\"node\":\"Face [Ovl0]\",\"color\":[1,1,1,1],\"texture\":{\"width\":2,\"height\":1,\"pixels\":[1,0,0,1,1,0,0,1]}}]}";
            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["body", "overlay", "bake", "--game", "skyrimse",
                "--layers", malformed, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var response = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.ValidationFailure && !response.RootElement.GetProperty("isValid").GetBoolean() &&
                   response.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "sse-fold-dimension-mismatch") &&
                   Convert.ToHexString(File.ReadAllBytes(outputPath.Value)) == "010203",
                $"Dimension mismatch or existing output was not refused: {error}");
        }
        finally { DeleteIfExists(outputPath.Value); }

        var valid = "{\"base\":{\"width\":1,\"height\":1,\"pixels\":[0,0,0,1]},\"layers\":[]}";
        File.WriteAllBytes(outputPath.Value, [1, 2, 3]);
        try
        {
            var (existingRunner, _, existingError) = CreateRunner();
            var existingExit = await existingRunner.RunAsync(CommandLine.Parse(["body", "overlay", "bake", "--game", "skyrimse",
                "--layers", valid, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            Assert(existingExit == CommandExitCode.SecurityRefusal && existingError.ToString().Contains("output-refused", StringComparison.Ordinal),
                "A valid fold was allowed to overwrite an existing output.");
        }
        finally { DeleteIfExists(outputPath.Value); }

        var outside = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-sse-fold-tests\\outside.dds");
        var (outsideRunner, _, outsideError) = CreateRunner();
        var outsideExit = await outsideRunner.RunAsync(CommandLine.Parse(["body", "overlay", "bake", "--game", "fallout4",
            "--layers", valid, "--output", outside.Value, "--json"]), CancellationToken.None);
        Assert(outsideExit == CommandExitCode.UsageError && outsideError.ToString().Contains("skyrimse", StringComparison.OrdinalIgnoreCase),
            "Cross-game overlay bake was not rejected at the CLI boundary.");

        var (adsRunner, _, adsError) = CreateRunner();
        var adsExit = await adsRunner.RunAsync(CommandLine.Parse(["body", "overlay", "bake", "--game", "skyrimse",
            "--layers", valid, "--output", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-sse-fold-tests\\fold:stream.dds", "--json"]), CancellationToken.None);
        Assert(adsExit == CommandExitCode.UsageError && adsError.ToString().Contains("alternate data stream", StringComparison.OrdinalIgnoreCase),
            "An alternate-data-stream output was not refused.");
    }

    private static byte[] BuildBodySlideTri(float? multiplierOverride = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("PIRT"u8.ToArray());
        writer.Write((ushort)1);
        WriteTriShape(writer, "BaseFemaleBody", ("BigBelly", multiplierOverride ?? 1F, (ushort)3, (short)1, (short)2, (short)3),
            ("WeightThin", 1F, (ushort)4, (short)1, (short)0, (short)0));
        writer.Write((ushort)1);
        WriteTriShapeUv(writer, "BaseFemaleBody", ("BigBelly", 1F, (ushort)3, (short)9, (short)9));
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteTriShape(BinaryWriter writer, string shapeName,
        params (string Name, float Multiplier, ushort Vertex, short X, short Y, short Z)[] morphs)
    {
        writer.Write((byte)Encoding.ASCII.GetByteCount(shapeName));
        writer.Write(Encoding.ASCII.GetBytes(shapeName));
        writer.Write((ushort)morphs.Length);
        foreach (var morph in morphs)
        {
            writer.Write((byte)Encoding.ASCII.GetByteCount(morph.Name));
            writer.Write(Encoding.ASCII.GetBytes(morph.Name));
            writer.Write(morph.Multiplier);
            writer.Write((ushort)1);
            writer.Write(morph.Vertex);
            writer.Write(morph.X);
            writer.Write(morph.Y);
            writer.Write(morph.Z);
        }
    }

    private static void WriteTriShapeUv(BinaryWriter writer, string shapeName,
        params (string Name, float Multiplier, ushort Vertex, short X, short Y)[] morphs)
    {
        writer.Write((byte)Encoding.ASCII.GetByteCount(shapeName));
        writer.Write(Encoding.ASCII.GetBytes(shapeName));
        writer.Write((ushort)morphs.Length);
        foreach (var morph in morphs)
        {
            writer.Write((byte)Encoding.ASCII.GetByteCount(morph.Name));
            writer.Write(Encoding.ASCII.GetBytes(morph.Name));
            writer.Write(morph.Multiplier);
            writer.Write((ushort)1);
            writer.Write(morph.Vertex);
            writer.Write(morph.X);
            writer.Write(morph.Y);
        }
    }

    private static WorkspacePath NewBodySlideTriFile(string fileName, byte[] content)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-bodyslide-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        DeleteIfExists(path.Value);
        File.WriteAllBytes(path.Value, content);
        return path;
    }

    private static WorkspacePath NewBodySlidePresetFile(string fileName, string content)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-bodyslide-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        DeleteIfExists(path.Value);
        File.WriteAllText(path.Value, content);
        return path;
    }

    private static async Task TestSseInventory()
    {
        var service = new GameInventoryService(new BethesdaPluginReader(), new BethesdaAssetIndexer());
        var inventory = await service.ReadAsync(new GameInventoryRequest(GameEdition.SkyrimSpecialEdition,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data"),
            null, null, true, ImmutableArray.Create(new PluginName("M2FixtureSSE.esp"))), CancellationToken.None);
        Assert(inventory.Npcs.Length == 1, "SSE fixture NPC inventory count changed.");
        Assert(inventory.Npcs[0].EditorId == "M2FixtureSseNpc", "SSE fixture EditorID changed.");
        Assert(inventory.Assets?.Providers.Any(provider => provider.Path.Value == "meshes/m2-fixture/head.nif") == true,
            "SSE loose asset was not indexed.");
    }

    private static async Task TestFo4Inventory()
    {
        var service = new GameInventoryService(new BethesdaPluginReader(), new BethesdaAssetIndexer());
        var inventory = await service.ReadAsync(new GameInventoryRequest(GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            null, new FormId(0x800), false, ImmutableArray<PluginName>.Empty), CancellationToken.None);
        Assert(inventory.Npcs.Length == 1 && inventory.Npcs[0].Name == "M2 FO4 Fixture",
            $"FO4 fixture inventory changed: count={inventory.Npcs.Length}, name={inventory.Npcs.FirstOrDefault()?.Name ?? "<null>"}.");
    }

    private static async Task TestPluginLoadOrderResolve()
    {
        var (runner, output, _) = CreateRunner();
        var fo4Exit = await runner.RunAsync(CommandLine.Parse(["plugins", "resolve-load-order", "--edition", "fallout4",
            "--plugins", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data",
            "--loadorder", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-fo4.json", "--json"]), CancellationToken.None);
        using var fo4 = JsonDocument.Parse(output.ToString());
        Assert(fo4Exit == CommandExitCode.Success && fo4.RootElement.GetProperty("isValid").GetBoolean() &&
            fo4.RootElement.GetProperty("entries")[0].GetProperty("exists").GetBoolean(), "FO4 load order did not resolve.");
        output.GetStringBuilder().Clear();
        var sseExit = await runner.RunAsync(CommandLine.Parse(["plugins", "resolve-load-order", "--game", "skyrimse",
            "--plugins", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data",
            "--load-order", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-sse.json", "--json"]), CancellationToken.None);
        using var sse = JsonDocument.Parse(output.ToString());
        Assert(sseExit == CommandExitCode.Success && sse.RootElement.GetProperty("edition").GetString() == "skyrimse" &&
            sse.RootElement.GetProperty("entries")[0].GetProperty("enabled").GetBoolean(), "SSE load order did not resolve.");
    }

    private static async Task TestPluginCompatibility()
    {
        var (runner, output, _) = CreateRunner();
        var fo4Exit = await runner.RunAsync(CommandLine.Parse(["plugins", "validate", "--edition", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--loadorder", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-fo4.json", "--json"]), CancellationToken.None);
        using var fo4 = JsonDocument.Parse(output.ToString());
        Assert(fo4Exit == CommandExitCode.Success && fo4.RootElement.GetProperty("isCompatible").GetBoolean(), "FO4 plugin compatibility failed.");
        output.GetStringBuilder().Clear();
        var sseExit = await runner.RunAsync(CommandLine.Parse(["plugins", "validate", "--game", "skyrimse",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp",
            "--load-order", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-sse.json", "--json"]), CancellationToken.None);
        using var sse = JsonDocument.Parse(output.ToString());
        Assert(sseExit == CommandExitCode.Success && sse.RootElement.GetProperty("isCompatible").GetBoolean(), "SSE plugin compatibility failed.");
    }

    private static async Task TestLoadOrderValidateAlias()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["load-order", "validate", "--edition", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--load-order", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-fo4.json", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("isCompatible").GetBoolean() &&
            document.RootElement.GetProperty("loadOrder").GetProperty("isValid").GetBoolean(),
            "load-order validate did not route to the typed plugin compatibility service.");
    }

    private static async Task TestProfileScan()
    {
        var (runner, output, _) = CreateRunner();
        const string dataRoot = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data";
        const string loadOrder = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\load-order-fo4.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["profile", "scan", "--edition", "fallout4",
            "--data-root", dataRoot, "--load-order", loadOrder, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("plugins").GetArrayLength() == 1 &&
            document.RootElement.GetProperty("loadOrderValid").GetBoolean() &&
            document.RootElement.GetProperty("fingerprint").GetString()?.Length == 64,
            "Profile scan did not fingerprint the copied plugin and load order.");

        output.GetStringBuilder().Clear();
        var refused = await runner.RunAsync(CommandLine.Parse(["profile", "scan", "--game", "fallout4",
            "--data-root", "F:\\ExampleGame\\Data", "--json"]), CancellationToken.None);
        Assert(refused == CommandExitCode.SecurityRefusal &&
            output.ToString().Contains("outside", StringComparison.OrdinalIgnoreCase),
            "Profile scan did not refuse the protected live root.");
    }

    private static async Task TestRecordsList()
    {
        var (runner, output, _) = CreateRunner();
        const string dataRoot = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data";
        var exit = await runner.RunAsync(CommandLine.Parse(["records", "list", "--edition", "fallout4",
            "--data-root", dataRoot, "--signature", "NPC_", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("plugins").GetArrayLength() == 1 &&
            document.RootElement.GetProperty("records").GetArrayLength() > 0 &&
            document.RootElement.GetProperty("records")[0].GetProperty("signature").GetString() == "NPC_",
            "Records list did not expose typed NPC records from the copied plugin set.");

        output.GetStringBuilder().Clear();
        var pluginExit = await runner.RunAsync(CommandLine.Parse(["records", "list", "--game", "fallout4",
            "--plugin", Path.Combine(dataRoot, "M2FixtureFO4.esp"), "--signature", "NPC_", "--json"]), CancellationToken.None);
        using var pluginDocument = JsonDocument.Parse(output.ToString());
        Assert(pluginExit == CommandExitCode.Success && pluginDocument.RootElement.GetProperty("records").GetArrayLength() > 0,
            "Records list did not accept an explicit copied plugin path.");
    }

    private static async Task TestDuplicateLoadOrder()
    {
        var path = NewM2LoadOrderFile("duplicate.json", "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"plugins\":[{\"name\":\"M2FixtureFO4.esp\",\"order\":0,\"enabled\":true},{\"name\":\"M2FixtureFO4.esp\",\"order\":1,\"enabled\":true}]}");
        try
        {
            var result = await CreateLoadOrderService().ResolveAsync(new PluginLoadOrderRequest(GameEdition.Fallout4,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"), path), CancellationToken.None);
            Assert(!result.IsValid && result.Diagnostics.Any(item => item.Code == "load-order-duplicate-plugin"), "Duplicate load-order entries were accepted.");
        }
        finally { DeleteIfExists(path.Value); }
    }

    private static async Task TestPluginLoadOrderChildReparseRefusal()
    {
        var manifest = NewM2LoadOrderFile("child-reparse.json",
            "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"plugins\":[{\"name\":\"M2FixtureFO4.esp\",\"order\":0,\"enabled\":true}]}");
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-load-order-child-reparse");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = Path.Combine(root.Value, "M2FixtureFO4.esp");
        File.Copy("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp", plugin);
        try
        {
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
            var service = new PluginLoadOrderService(new BethesdaPluginReader(), policy,
                new WorkspacePath("K:\\ExampleWorkspace"),
                path => path.EndsWith("M2FixtureFO4.esp", StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.ReparsePoint
                    : FileAttributes.Normal);
            var result = await service.ResolveAsync(new PluginLoadOrderRequest(GameEdition.Fallout4,
                root, manifest), CancellationToken.None);
            Assert(!result.IsValid && result.Diagnostics.Any(item => item.Code == "load-order-plugin-reparse-refused"),
                "A discovered reparse-point plugin was not refused before load-order resolution.");
        }
        finally
        {
            DeleteIfExists(manifest.Value);
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestPluginMasterCycle()
    {
        var root = NewM2LoadOrderRoot("cycle");
        var basePlugin = new PluginName("Base.esp");
        var addonPlugin = new PluginName("Addon.esp");
        File.Copy("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp", Path.Combine(root.Value, basePlugin.Value));
        File.Copy("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp", Path.Combine(root.Value, addonPlugin.Value));
        var loadOrder = NewM2LoadOrderFile("cycle-manifest.json", "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Base.esp\",\"order\":0,\"enabled\":true},{\"name\":\"Addon.esp\",\"order\":1,\"enabled\":true}]}");
        try
        {
            var fake = new FakePluginReader(
                new PluginInspection(GameEdition.SkyrimSpecialEdition, basePlugin, [addonPlugin], [], []),
                new PluginInspection(GameEdition.SkyrimSpecialEdition, addonPlugin, [basePlugin], [], []));
            var service = new PluginLoadOrderService(fake, new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"));
            var result = await service.ValidateAsync(new PluginCompatibilityRequest(GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(Path.Combine(root.Value, addonPlugin.Value)), loadOrder), CancellationToken.None);
            Assert(!result.IsCompatible && result.Diagnostics.Any(item => item.Code == "plugin-master-cycle"), "Plugin master cycle was accepted.");
        }
        finally { DeleteIfExists(loadOrder.Value); DeleteDirectory(root.Value); }
    }

    private static async Task TestNpcSearch()
    {
        var service = new GameInventoryService(new BethesdaPluginReader(), new BethesdaAssetIndexer());
        var inventory = await service.ReadAsync(new GameInventoryRequest(GameEdition.SkyrimSpecialEdition,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data"),
            "does-not-match", null, false, ImmutableArray<PluginName>.Empty), CancellationToken.None);
        Assert(inventory.Npcs.IsEmpty, "NPC search did not filter the result.");
    }

    private static async Task TestNpcInspect()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "npc", "inspect", "--edition", "fallout4",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--npc", "0x00000800", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        var npc = document.RootElement.GetProperty("npc");
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("schemaVersion").GetString() == "1",
            "NPC inspect did not return the versioned success contract.");
        Assert(npc.GetProperty("sex").GetString() == "male" && npc.GetProperty("provenanceKind").GetString() == "Base",
            "NPC inspect omitted typed sex or source provenance.");
        Assert(document.RootElement.GetProperty("unsupportedFields").GetArrayLength() > 0,
            "NPC inspect silently omitted unsupported fields.");
    }

    private static async Task TestNpcCategoryFilter()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "npc", "list", "--edition", "fallout4",
            "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data",
            "--filter", "unused", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("npcs").GetArrayLength() == 1,
            "NPC unused category filter did not retain its fixture.");
        Assert(document.RootElement.GetProperty("npcs")[0].GetProperty("categories")[0].GetString() == "Unused",
            "NPC category metadata was not deterministic.");
    }

    private static async Task TestNpcCategoryFilterUsage()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "npc", "list", "--edition", "fallout4",
            "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data",
            "--filter", "not-a-category", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError && document.RootElement.GetProperty("code").GetString() == "usage-error",
            "Unknown NPC category did not fail with a usage error.");
    }

    private static async Task TestNpcCategoryIntersection()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-npc-categories");
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Categories.esp");
        File.Copy("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            Path.Combine(root.Value, plugin.Value), true);
        var records = new[]
        {
            new PluginRecordSummary(new FormId(0x801), "NPC_", "UniqueNpc", "Unique", true, false,
                new NpcRecordMetadata(NpcSex.Male, null, null, null, null, null, false, null, false, true, false, false)),
            new PluginRecordSummary(new FormId(0x802), "NPC_", "GenericNpc", "Generic", true, false,
                new NpcRecordMetadata(NpcSex.Female, null, null, null, null, null, true, new FormId(0x900), false, true, false, false)),
            new PluginRecordSummary(new FormId(0x803), "NPC_", "TemplateNpc", "Template", true, false,
                new NpcRecordMetadata(NpcSex.Male, null, null, null, null, null, false, null, true, false, false, false)),
            new PluginRecordSummary(new FormId(0x804), "NPC_", "UnusedNpc", "Unused", true, false,
                new NpcRecordMetadata(NpcSex.Male, null, null, null, null, null, false, null, false, false, false, false))
        };
        try
        {
            var fake = new FakePluginReader(new PluginInspection(GameEdition.Fallout4, plugin, [], records.ToImmutableArray(), []));
            var service = new GameInventoryService(fake, new BethesdaAssetIndexer());
            var selected = await service.ReadAsync(new GameInventoryRequest(GameEdition.Fallout4, root, null, null, false,
                [plugin], ImmutableHashSet.Create(NpcCategory.Unique, NpcCategory.Template), false), CancellationToken.None);
            Assert(selected.Npcs.Select(item => item.EditorId).SequenceEqual(["UniqueNpc", "TemplateNpc"]),
                "Category union did not select the exact expected records.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestNpcChangedFilter()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-npc-overrides");
        Directory.CreateDirectory(root.Value);
        var basePlugin = new PluginName("Base.esp");
        var overridePlugin = new PluginName("Override.esp");
        File.WriteAllText(Path.Combine(root.Value, basePlugin.Value), "fixture");
        File.WriteAllText(Path.Combine(root.Value, overridePlugin.Value), "fixture");
        var formId = new FormId(0x800);
        var baseRecord = new PluginRecordSummary(
            formId, "NPC_", "BaseNpc", "Base", true, false,
            OwnerPlugin: basePlugin);
        var overrideRecord = baseRecord with
        {
            EditorId = "OverrideNpc",
            Name = "Override"
        };
        try
        {
            var service = new GameInventoryService(new FakePluginReader(
                new PluginInspection(GameEdition.Fallout4, basePlugin, [], [baseRecord], []),
                new PluginInspection(GameEdition.Fallout4, overridePlugin, [], [overrideRecord], [])),
                new BethesdaAssetIndexer());
            var inventory = await service.ReadAsync(new GameInventoryRequest(GameEdition.Fallout4, root,
                null, null, false, [basePlugin, overridePlugin], null, true), CancellationToken.None);
            Assert(inventory.Npcs.Length == 1 && inventory.Npcs[0].Plugin == overridePlugin &&
                   inventory.Npcs[0].OwnerPlugin == basePlugin &&
                   inventory.Npcs[0].ChangeState == NpcChangeState.Changed &&
                   inventory.Npcs[0].Provenance?.Kind == NpcProvenanceKind.Override,
                "Changed-only did not select the winning explicit override.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestNpcProtectedRoot()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "npc", "list", "--edition", "fallout4", "--data-root", "F:\\ExampleGame\\Data", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "NPC inventory did not refuse the protected root.");
        output.GetStringBuilder().Clear();
        var assetExit = await runner.RunAsync(CommandLine.Parse([
            "assets", "index", "--edition", "fallout4", "--data-root", "F:\\ExampleGame\\Data", "--json"]), CancellationToken.None);
        using var assetDocument = JsonDocument.Parse(output.ToString());
        Assert(assetExit == CommandExitCode.SecurityRefusal && assetDocument.RootElement.GetProperty("providers").GetArrayLength() == 0,
            "Asset inventory did not fail closed on the protected root.");
    }

    private static async Task TestFormChoiceSearch()
    {
        var root = NewM2LoadOrderRoot("form-choice");
        var basePlugin = new PluginName("Base.esp");
        var overridePlugin = new PluginName("Override.esp");
        File.WriteAllText(Path.Combine(root.Value, basePlugin.Value), "fixture");
        File.WriteAllText(Path.Combine(root.Value, overridePlugin.Value), "fixture");
        var baseRecords = ImmutableArray.Create(
            new PluginRecordSummary(new FormId(0x801), "NPC_", "BaseNpc", "Base", true, false,
                OwnerPlugin: basePlugin),
            new PluginRecordSummary(new FormId(0x802), "RACE", "BaseRace", "Race", false, false,
                OwnerPlugin: basePlugin));
        var overrideRecords = ImmutableArray.Create(
            new PluginRecordSummary(new FormId(0x801), "NPC_", "OverrideNpc", "Override", true, false,
                OwnerPlugin: basePlugin),
            new PluginRecordSummary(new FormId(0x801), "NPC_", "IndependentNpc", "Independent", true, false,
                OwnerPlugin: overridePlugin),
            new PluginRecordSummary(new FormId(0x803), "KYWD", "OtherKeyword", "Keyword", false, false,
                OwnerPlugin: overridePlugin));
        try
        {
            var fake = new FakePluginReader(
                new PluginInspection(GameEdition.Fallout4, basePlugin, [], baseRecords, []),
                new PluginInspection(GameEdition.Fallout4, overridePlugin, [], overrideRecords, []));
            var service = new FormChoiceService(fake);
            var result = await service.SearchAsync(new FormChoiceSearchRequest(GameEdition.Fallout4, root,
                [new RecordSignature("NPC_")], null, null, true, [basePlugin, overridePlugin]), CancellationToken.None);
            Assert(result.AllowNull && result.Candidates.Length == 2,
                "Owner-qualified FormIDs were incorrectly collapsed or signature filtering returned incompatible candidates.");
            var overridden = result.Candidates.Single(item => item.EditorId == "OverrideNpc");
            var independent = result.Candidates.Single(item => item.EditorId == "IndependentNpc");
            Assert(overridden.Provenance.Kind == FormChoiceProvenanceKind.Override &&
                   overridden.Provenance.SourcePlugin == basePlugin &&
                   overridden.Provenance.OverrideChain.SequenceEqual([basePlugin, overridePlugin]) &&
                   independent.Provenance.Kind == FormChoiceProvenanceKind.Base &&
                   independent.Provenance.SourcePlugin == overridePlugin,
                "FormID search did not distinguish an override from an unrelated record with the same local FormID.");
            var duplicate = await service.SearchAsync(new FormChoiceSearchRequest(GameEdition.Fallout4, root,
                [new RecordSignature("NPC_")], null, null, false, [basePlugin, basePlugin]), CancellationToken.None);
            Assert(duplicate.Candidates.IsEmpty && duplicate.Diagnostics.Any(item => item.Code == "form-choice-duplicate-plugin"),
                "Duplicate explicit plugin order was not rejected.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFormChoiceCli()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "forms", "search", "--edition", "skyrimse",
            "--plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp",
            "--type", "NPC_", "--allow-null", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("schemaVersion").GetString() == "1" &&
               document.RootElement.GetProperty("allowNull").GetBoolean() &&
               document.RootElement.GetProperty("candidates")[0].GetProperty("signature").GetString() == "NPC_",
            "FormID search CLI did not emit the versioned typed candidate contract.");
    }

    private static async Task TestFormChoiceUsage()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "forms", "search", "--edition", "fallout4",
            "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data",
            "--type", "BAD", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError && document.RootElement.GetProperty("code").GetString() == "usage-error",
            "Malformed FormID signature did not fail with a usage error.");
    }

    private static async Task TestFormChoiceProtectedRoot()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "forms", "search", "--edition", "fallout4", "--data-root", "F:\\ExampleGame\\Data",
            "--type", "NPC_", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal && document.RootElement.GetProperty("candidates").GetArrayLength() == 0 &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "FormID search did not refuse the protected root.");
    }

    private static async Task TestFormChoiceCancellation()
    {
        var service = new FormChoiceService(new BethesdaPluginReader());
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => service.SearchAsync(new FormChoiceSearchRequest(
            GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            [new RecordSignature("NPC_")], null, null, false, []), source.Token).AsTask());
    }

    private static async Task TestMeshChoiceCli()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "assets", "search", "--edition", "fallout4", "--kind", "mesh",
            "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data",
            "--query", "head.nif", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        var candidate = document.RootElement.GetProperty("candidates")[0];
        var providers = candidate.GetProperty("providers");
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("schemaVersion").GetString() == "1" &&
               candidate.GetProperty("path").GetString() == "meshes/m2-fixture/head.nif" &&
               candidate.GetProperty("providerStatus").GetString() == "resolved" &&
               providers.GetArrayLength() == 2 &&
               providers[0].GetProperty("kind").GetString() == "loose" &&
               providers[1].GetProperty("kind").GetString() == "archive",
            "Mesh choice search did not emit normalized provider evidence.");
    }

    private static async Task TestHeadPartChoice()
    {
        var root = NewM2LoadOrderRoot("headpart-choice");
        var basePlugin = new PluginName("Base.esp");
        var overridePlugin = new PluginName("Override.esp");
        File.WriteAllText(Path.Combine(root.Value, basePlugin.Value), "fixture");
        File.WriteAllText(Path.Combine(root.Value, overridePlugin.Value), "fixture");
        var formId = new FormId(0x901);
        var records = new PluginRecordSummary(formId, "HDPT", "FixtureHead", "Fixture Head", false, false,
            null, new AssetPath("meshes/m2-fixture/head.nif"), OwnerPlugin: basePlugin);
        var fake = new FakePluginReader(
            new PluginInspection(GameEdition.Fallout4, basePlugin, [], [records], []),
            new PluginInspection(GameEdition.Fallout4, overridePlugin, [], [records with { EditorId = "OverrideHead" }], []));
        var indexer = new FakeAssetIndexer(new AssetIndex(GameEdition.Fallout4,
            [new AssetProvider(new AssetPath("meshes/m2-fixture/head.nif"), AssetProviderKind.Loose,
                "fixture", 12, new string('a', 64))], []));
        var result = await new AssetChoiceService(fake, indexer).SearchAsync(new AssetChoiceSearchRequest(
            GameEdition.Fallout4, root, AssetChoiceKind.HeadPart, "override", [basePlugin, overridePlugin]), CancellationToken.None);
        try
        {
            Assert(result.Candidates.Length == 1 && result.Candidates[0].Provider.Status == AssetChoiceProviderStatus.Resolved &&
                   result.Candidates[0].Provenance is { } provenance &&
                   provenance.Kind == FormChoiceProvenanceKind.Override &&
                   provenance.OverrideChain.SequenceEqual([basePlugin, overridePlugin]),
                "Headpart search did not resolve typed provider/provenance evidence.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestHeadPartMissingProvider()
    {
        var root = NewM2LoadOrderRoot("headpart-missing-provider");
        var plugin = new PluginName("MissingProvider.esp");
        File.WriteAllText(Path.Combine(root.Value, plugin.Value), "fixture");
        var fake = new FakePluginReader(new PluginInspection(GameEdition.SkyrimSpecialEdition, plugin, [], [
            new PluginRecordSummary(new FormId(0x902), "HDPT", "MissingHead", "Missing Head", false, false,
                null, new AssetPath("meshes/m2-fixture/missing.nif"))], []));
        try
        {
            var result = await new AssetChoiceService(fake, new FakeAssetIndexer(new AssetIndex(
                GameEdition.SkyrimSpecialEdition, [], []))).SearchAsync(new AssetChoiceSearchRequest(
                GameEdition.SkyrimSpecialEdition, root, AssetChoiceKind.HeadPart, null, [plugin]), CancellationToken.None);
            Assert(result.Candidates.Length == 1 && result.Candidates[0].Provider.Status == AssetChoiceProviderStatus.Missing &&
                   result.Diagnostics.Any(item => item.Code == "asset-choice-provider-missing" && item.Severity == DiagnosticSeverity.Warning),
                "Missing headpart provider was not explicit.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestRaceMenuPaintChoiceCli()
    {
        var source = new SkyrimRaceMenuPaintRegistrationSource(
            new AssetPath("scripts/PaintFixture.pex"),
            SkyrimRaceMenuPaintProviderKind.Bsa,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\PaintFixture.bsa"),
            new Sha256Hash(new string('a', 64)),
            new Sha256Hash(new string('b', 64)),
            1024);
        var candidate = new SkyrimRaceMenuPaintChoiceCandidate(
            SkyrimRaceMenuPaintCategory.Warpaint,
            "$Ash",
            "Ash",
            new AssetPath("actors/paint/ash.dds"),
            new AssetPath("textures/actors/paint/ash.dds"),
            [
                new SkyrimRaceMenuPaintTextureSlot(
                    0,
                    SkyrimRaceMenuPaintSlotKind.Texture,
                    "actors/paint/ash.dds",
                    new AssetPath("actors/paint/ash.dds"))
            ],
            [source]);
        var summary = new SkyrimRaceMenuPaintCatalogSummary(
            2, 1, 1, 0, 1, 1, 0, 1, 1,
            new Sha256Hash(new string('c', 64)));
        var service = new FakeSkyrimRaceMenuPaintChoiceService(
            new SkyrimRaceMenuPaintChoiceResult(
                true, [candidate], summary, []));
        var (runner, output, error) = CreateRunner(
            skyrimRaceMenuPaintChoiceService: service);
        CommandExitCode exit = await runner.RunAsync(CommandLine.Parse([
            "paint", "choices", "--edition", "skyrimse",
            "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\paint-fixture\\Data",
            "--plugins", "Skyrim.esm,RaceMenu.esp",
            "--category", "warpaint", "--search", "Ash", "--json"
        ]), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        JsonElement item = root.GetProperty("candidates")[0];
        Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
               service.Request is
               {
                   Category: SkyrimRaceMenuPaintCategory.Warpaint,
                   Search: "Ash"
               } &&
               service.Request.PluginOrder.Select(plugin => plugin.Value)
                   .SequenceEqual(["Skyrim.esm", "RaceMenu.esp"]) &&
               root.GetProperty("schemaVersion").GetString() == "1" &&
               root.GetProperty("category").GetString() == "warpaint" &&
               !root.GetProperty("runtimeAuthority").GetBoolean() &&
               !root.GetProperty("textureRenderAuthority").GetBoolean() &&
               item.GetProperty("registeredPath").GetString() == "actors/paint/ash.dds" &&
               item.GetProperty("sources")[0].GetProperty("pexSha256").GetString() ==
               new string('b', 64),
            "RaceMenu paint CLI did not preserve the typed request or stable authority JSON.");

        var refusedService = new FakeSkyrimRaceMenuPaintChoiceService(
            new SkyrimRaceMenuPaintChoiceResult(
                false,
                [],
                null,
                [
                    new Diagnostic(
                        "protected-root-refused",
                        DiagnosticSeverity.Error,
                        "The selected root is protected.")
                ]));
        var (refusedRunner, _, _) = CreateRunner(
            skyrimRaceMenuPaintChoiceService: refusedService);
        CommandExitCode refusedExit = await refusedRunner.RunAsync(CommandLine.Parse([
            "paint", "choices", "--edition", "skyrimse",
            "--data-root", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\paint-fixture\\Data",
            "--plugins", "RaceMenu.esp", "--category", "warpaint", "--json"
        ]), CancellationToken.None);
        Assert(refusedExit == CommandExitCode.SecurityRefusal,
            "RaceMenu paint CLI flattened a protected-root refusal into a validation failure.");
    }

    private static async Task TestAssetChoiceUsage()
    {
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "assets", "search", "--edition", "fallout4", "--kind", "bad", "--data-root",
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(error.ToString());
        Assert(exit == CommandExitCode.UsageError && document.RootElement.GetProperty("code").GetString() == "usage-error",
            "Malformed asset choice kind did not fail with a usage error.");
    }

    private static async Task TestAssetChoiceProtectedRoot()
    {
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "assets", "search", "--edition", "fallout4", "--kind", "mesh",
            "--data-root", "F:\\ExampleGame\\Data", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.SecurityRefusal && document.RootElement.GetProperty("candidates").GetArrayLength() == 0 &&
               document.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "protected-root-refused"),
            "Asset choice search did not refuse the protected root.");
    }

    private static async Task TestAssetChoiceCancellation()
    {
        var service = new AssetChoiceService(new BethesdaPluginReader(), new BethesdaAssetIndexer());
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => service.SearchAsync(new AssetChoiceSearchRequest(
            GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            AssetChoiceKind.Mesh, null, []), source.Token).AsTask());
    }

    private static async Task TestAssetChoiceProviderValidation()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data");
        var index = new AssetIndex(GameEdition.Fallout4, [new AssetProvider(
            new AssetPath("meshes/m2-fixture/head.nif"), AssetProviderKind.Loose, "fixture", 1, "bad")], []);
        var result = await new AssetChoiceService(new BethesdaPluginReader(), new FakeAssetIndexer(index)).SearchAsync(
            new AssetChoiceSearchRequest(GameEdition.Fallout4, root, AssetChoiceKind.Mesh, null, []), CancellationToken.None);
        Assert(result.Candidates.IsEmpty && result.Diagnostics.Any(item => item.Code == "asset-choice-provider-invalid" &&
            item.Severity == DiagnosticSeverity.Error), "Malformed provider evidence was accepted.");
    }

    private static async Task TestAssetIndexExportPrecedence()
    {
        var root = NewM2LoadOrderRoot("asset-index-precedence");
        var output = new WorkspacePath(Path.Combine(root.Value, "assets.json"));
        var path = new AssetPath("meshes/m2-fixture/head.nif");
        var fakeIndex = new AssetIndex(GameEdition.Fallout4, [
            new AssetProvider(path, AssetProviderKind.Archive, "ZArchive.ba2", 3, new string('c', 64)),
            new AssetProvider(path, AssetProviderKind.Loose, "loose", 1, new string('a', 64)),
            new AssetProvider(path, AssetProviderKind.Archive, "AArchive.ba2", 2, new string('b', 64))], []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var result = await new AssetIndexExportService(new FakeAssetIndexer(fakeIndex), policy,
                new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(new AssetIndexExportRequest(
                GameEdition.Fallout4, new WorkspacePath("K:\\ExampleWorkspace"), output), CancellationToken.None);
            Assert(result.Written && result.Artifact?.Entries.Length == 1 &&
                   result.Artifact.Entries[0].Winner.Kind == AssetProviderKind.Loose &&
                   result.Artifact.Entries[0].Providers[1].Source == "AArchive.ba2" &&
                   result.Artifact.Entries[0].Providers[2].Source == "ZArchive.ba2",
                "Asset index precedence was not deterministic or loose-first.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestAssetIndexExportCli()
    {
        var root = NewM2LoadOrderRoot("asset-index-cli");
        var outputPath = Path.Combine(root.Value, "assets.json");
        try
        {
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse([
                "assets", "index", "--edition", "fallout4", "--data-root",
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data",
                "--output", outputPath, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && File.Exists(outputPath) &&
                   document.RootElement.GetProperty("schemaVersion").GetString() == "1" &&
                   document.RootElement.GetProperty("written").GetBoolean() &&
                   document.RootElement.GetProperty("entries").GetArrayLength() == 2,
                "Asset index export CLI did not write the typed artifact contract.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestAssetIndexExportProviderValidation()
    {
        var root = NewM2LoadOrderRoot("asset-index-provider-validation");
        var output = new WorkspacePath(Path.Combine(root.Value, "assets.json"));
        var fakeIndex = new AssetIndex(GameEdition.Fallout4, [
            new AssetProvider(new AssetPath("meshes/m2-fixture/head.nif"), AssetProviderKind.Loose, "", -1, "bad")], []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var result = await new AssetIndexExportService(new FakeAssetIndexer(fakeIndex), policy,
                new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(new AssetIndexExportRequest(
                GameEdition.Fallout4, new WorkspacePath("K:\\ExampleWorkspace"), output), CancellationToken.None);
            Assert(!result.Written && result.Diagnostics.Count(item => item.Code == "asset-index-provider-invalid") == 1,
                "Malformed provider evidence was accepted by the asset-index exporter.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestAssetIndexExportExisting()
    {
        var root = NewM2LoadOrderRoot("asset-index-existing");
        var output = new WorkspacePath(Path.Combine(root.Value, "assets.json"));
        File.WriteAllText(output.Value, "existing");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var result = await new AssetIndexExportService(new BethesdaAssetIndexer(), policy,
                new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(new AssetIndexExportRequest(
                GameEdition.SkyrimSpecialEdition, new WorkspacePath("K:\\ExampleWorkspace"), output), CancellationToken.None);
            Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "asset-index-output-exists"),
                "Existing asset index output was overwritten or accepted.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestAssetIndexExportProtectedOutput()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var result = await new AssetIndexExportService(new BethesdaAssetIndexer(), policy,
            new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(new AssetIndexExportRequest(
            GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            new WorkspacePath("F:\\ExampleGame\\Data\\asset-index.json")), CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "protected-root-refused"),
            "Protected asset index output was not refused.");
    }

    private static async Task TestAssetIndexExportCancellation()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => new AssetIndexExportService(new BethesdaAssetIndexer(), policy,
            new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(new AssetIndexExportRequest(
            GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\cancel-assets.json")), source.Token).AsTask());
    }

    private static async Task TestAssetIndexExportAlternateDataStream()
    {
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var result = await new AssetIndexExportService(new BethesdaAssetIndexer(), policy,
            new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(new AssetIndexExportRequest(
            GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\asset-index.json:stream")),
            CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "asset-index-output-ads-refused"),
            "Alternate-data-stream asset-index output was not refused.");
    }

    private static async Task TestMissingPlugin()
    {
        var service = new GameInventoryService(new BethesdaPluginReader(), new BethesdaAssetIndexer());
        var inventory = await service.ReadAsync(new GameInventoryRequest(GameEdition.SkyrimSpecialEdition,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data"),
            null, null, false, ImmutableArray.Create(new PluginName("Missing.esp"))), CancellationToken.None);
        Assert(inventory.Diagnostics.Any(item => item.Code == "plugin-missing"), "Missing plugin was not reported.");
    }

    private static async Task TestMalformedArchive()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-negative");
        Directory.CreateDirectory(root.Value);
        var archive = Path.Combine(root.Value, "malformed.bsa");
        await File.WriteAllTextAsync(archive, "not a BSA archive");
        try
        {
            var index = await new BethesdaAssetIndexer().IndexAsync(new AssetIndexRequest(GameEdition.SkyrimSpecialEdition, root), CancellationToken.None);
            Assert(index.Diagnostics.Any(item => item.Code == "archive-read-failed"), "Malformed archive was not diagnosed.");
        }
        finally
        {
            File.Delete(archive);
        }
    }

    private static async Task TestInventoryCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var service = new GameInventoryService(new BethesdaPluginReader(), new BethesdaAssetIndexer());
        await AssertThrowsAsync<OperationCanceledException>(() => service.ReadAsync(new GameInventoryRequest(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data"),
            null, null, true, ImmutableArray<PluginName>.Empty), source.Token).AsTask());
    }

    private static async Task TestSseMutation()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = NewMutationOutput("M3FixtureSSE.esp");
        try
        {
            var service = CreateMutationService();
            var request = new NpcMutationRequest(GameEdition.SkyrimSpecialEdition, source, output, new FormId(0x800),
                new EditorId("M3FixtureSseEdited"), new NpcName("M3 SSE Edited"), new NpcWeightPatch(60, null, null, null),
                Hash(source), false, null, NpcSex.Female);
            var proposal = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable && proposal.Changes.Length == 4 &&
                   proposal.Changes.Any(change => change.Field == "Sex" && change.After == "female"),
                "SSE mutation proposal was not applicable.");
            var result = await service.ApplyAsync(request, proposal, CancellationToken.None);
            Assert(result.Applied && File.Exists(output.Value), "SSE mutation did not produce a verified output.");
            var verification = await service.VerifyAsync(new PluginVerificationRequest(request.Edition, source, output,
                request.TargetFormId, proposal.Changes, proposal.PreservedFields), CancellationToken.None);
            Assert(verification.IsValid, "SSE independent verification failed.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestFo4Mutation()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = NewMutationOutput("M3FixtureFO4.esp");
        try
        {
            var service = CreateMutationService();
            var request = new NpcMutationRequest(GameEdition.Fallout4, source, output, new FormId(0x800),
                new EditorId("M3FixtureFo4Edited"), new NpcName("M3 FO4 Edited"), new NpcWeightPatch(null, 10, 20, 30),
                Hash(source), false, null, NpcSex.Female);
            var proposal = await service.AnalyzeAsync(request, CancellationToken.None);
            var result = await service.ApplyAsync(request, proposal, CancellationToken.None);
            Assert(result.Applied && proposal.Changes.Any(change => change.Field == "Sex"),
                "FO4 mutation did not produce a verified output.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestExternalArchetypeReference()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = NewMutationOutput("external-archetype.esp");
        var archetype = new NpcArchetypePatch(
            OptionalFormReference.Set(new FormReference(new PluginName("Other.esp"), new FormId(0x801))),
            default, default, default);
        try
        {
            var request = new NpcMutationRequest(GameEdition.Fallout4, source, output, new FormId(0x800),
                null, null, null, Hash(source), true, null, null, archetype);
            var proposal = await CreateMutationService().AnalyzeAsync(request, CancellationToken.None);
            Assert(!proposal.IsApplicable && proposal.Diagnostics.Any(item => item.Code == "reference-plugin-unresolved"),
                "An external archetype reference was not refused before mutation.");
            Assert(!File.Exists(output.Value), "An external archetype refusal created an output.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestMalformedArchetypeReference()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = NewMutationOutput("malformed-archetype.esp");
        var (runner, _, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse([
            "npc", "patch", "--game", "fallout4", "--input-plugin", source.Value,
            "--output", output.Value, "--form-id", "0x00000800", "--race", "not-a-reference", "--json"]),
            CancellationToken.None);
        Assert(exit == CommandExitCode.UsageError && error.ToString().Contains("usage-error", StringComparison.Ordinal),
            "Malformed archetype reference did not return a usage error.");
        Assert(!File.Exists(output.Value), "Malformed archetype reference created an output.");
    }

    private static async Task TestSexMutationCli()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m3-sex-tests");
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = new WorkspacePath(Path.Combine(root.Value, "M3SexCli.esp"));
        try
        {
            var (runner, response, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse([
                "npc", "patch", "--game", "fallout4", "--input-plugin", source.Value,
                "--output", output.Value, "--form-id", "0x00000800", "--sex", "female",
                "--input-sha", Hash(source).Value, "--apply", "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(response.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("applied").GetBoolean() &&
                   document.RootElement.GetProperty("changes").EnumerateArray().Any(change =>
                       change.GetProperty("field").GetString() == "Sex" &&
                       change.GetProperty("after").GetString() == "female"),
                "npc patch CLI did not apply the typed sex mutation.");

            var (verifyRunner, verifyResponse, _) = CreateRunner();
            var verifyExit = await verifyRunner.RunAsync(CommandLine.Parse([
                "plugin", "verify", "--game", "fallout4", "--source-plugin", source.Value,
                "--output-plugin", output.Value, "--form-id", "0x00000800", "--sex", "female", "--json"]),
                CancellationToken.None);
            using var verificationDocument = JsonDocument.Parse(verifyResponse.ToString());
            Assert(verifyExit == CommandExitCode.Success && verificationDocument.RootElement.GetProperty("isValid").GetBoolean(),
                "plugin verify CLI did not validate the typed sex mutation.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestSexMutationNoOp()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = NewMutationOutput("sex-no-op.esp");
        try
        {
            var request = new NpcMutationRequest(GameEdition.SkyrimSpecialEdition, source, output, new FormId(0x800),
                null, null, null, Hash(source), true, null, NpcSex.Male);
            var proposal = await CreateMutationService().AnalyzeAsync(request, CancellationToken.None);
            Assert(!proposal.IsApplicable && proposal.Diagnostics.Any(item => item.Code == "no-changes") &&
                   !File.Exists(output.Value), "Sex no-op was not rejected without writing.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestStaleMutationHash()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = NewMutationOutput("stale.esp");
        try
        {
            var service = CreateMutationService();
            var request = new NpcMutationRequest(GameEdition.SkyrimSpecialEdition, source, output, new FormId(0x800),
                new EditorId("M3Stale"), null, null, new Sha256Hash(new string('0', 64)), true, null);
            var proposal = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(!proposal.IsApplicable && proposal.Diagnostics.Any(item => item.Code == "input-hash-mismatch"), "Stale hash was not refused.");
            Assert(!File.Exists(output.Value), "Stale hash created an output.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestExistingMutationDestination()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = NewMutationOutput("existing.esp");
        await File.WriteAllTextAsync(output.Value, "existing");
        try
        {
            var proposal = await CreateMutationService().AnalyzeAsync(new NpcMutationRequest(GameEdition.SkyrimSpecialEdition,
                source, output, new FormId(0x800), new EditorId("M3Existing"), null, null, Hash(source), true, null), CancellationToken.None);
            Assert(proposal.Diagnostics.Any(item => item.Code == "output-exists"), "Existing destination was not refused.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestUnsafeMutationOutput()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = new WorkspacePath("F:\\ExampleGame\\Data\\M3Unsafe.esp");
        var proposal = await CreateMutationService().AnalyzeAsync(new NpcMutationRequest(GameEdition.SkyrimSpecialEdition,
            source, output, new FormId(0x800), new EditorId("M3Unsafe"), null, null, Hash(source), true, null), CancellationToken.None);
        Assert(!proposal.IsApplicable && proposal.Diagnostics.Any(item => item.Code == "output-root-outside-workspace"), "Protected output was not refused.");
    }

    private static async Task TestMalformedMutationOutput()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = NewMutationOutput("malformed.esp");
        await File.WriteAllTextAsync(output.Value, "not a plugin");
        try
        {
            var result = await CreateMutationService().VerifyAsync(new PluginVerificationRequest(GameEdition.SkyrimSpecialEdition,
                source, output, new FormId(0x800), [new MutationChange("EditorID", null, "M3Malformed")], ["OBND"]), CancellationToken.None);
            Assert(!result.IsValid && result.Diagnostics.Any(item => item.Code == "plugin-parse-failed"), "Malformed output was not rejected.");
        }
        finally { DeleteIfExists(output.Value); }
    }

    private static async Task TestProposalApply()
    {
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = NewMutationOutput("proposal-output.esp");
        var proposalPath = NewMutationOutput("proposal-output.json");
        try
        {
            var service = CreateMutationService();
            var request = new NpcMutationRequest(GameEdition.SkyrimSpecialEdition, source, output, new FormId(0x800),
                new EditorId("M3Proposal"), null, null, Hash(source), false, proposalPath);
            var proposal = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable && File.Exists(proposalPath.Value), "Proposal was not persisted.");
            var result = await service.ApplyAsync(request, proposal, CancellationToken.None);
            Assert(result.Applied && File.Exists(output.Value), "Persisted proposal did not apply.");
        }
        finally { DeleteIfExists(output.Value); DeleteIfExists(proposalPath.Value); }
    }

    private static async Task TestLooksMenuPreset()
    {
        var (runner, output, _) = CreateRunner();
        var fixture = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "looksmenu", "--edition", "fallout4", "--input", fixture, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "LooksMenu fixture inspection failed.");
        Assert(document.RootElement.GetProperty("isValid").GetBoolean(), "LooksMenu fixture was not valid.");
        Assert(document.RootElement.GetProperty("appearance").GetProperty("bodyMorphs").GetProperty("CBBE Breast").GetSingle() == 0.25f, "LooksMenu body morph was not typed.");
        Assert(document.RootElement.GetProperty("diagnostics").GetArrayLength() == 1, "LooksMenu loss diagnostic count changed.");
    }

    private static async Task TestLooksMenuExtendedFields()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-looksmenu-extended-tests");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        try
        {
            var source = new WorkspacePath(Path.Combine(root.Value, "extended.json"));
            await File.WriteAllTextAsync(source.Value, """
                {
                  "Gender": 1,
                  "Weight": [0.2, 0.4, 0.6],
                  "Morphs": {
                    "Presets": {"00ABCDEF": 0.25},
                    "Regions": {"00000004": [1.0, -2.0, 3.0]},
                    "Intensity": 0.75,
                    "NoseWidth": 0.1
                  },
                  "Tints": {
                    "00000002": {"Color": 2, "ColorID": 22, "Percent": 20, "Type": 3},
                    "00000001": {"Color": 1, "ColorID": 11, "Percent": 10, "Type": 1}
                  },
                  "TintOrder": ["00000001", 2]
                }
                """);
            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "looksmenu",
                "--edition", "fallout4", "--input", source.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            var appearance = document.RootElement.GetProperty("appearance");
            var tints = appearance.GetProperty("tints");
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                appearance.GetProperty("chargenFaceMorphs").GetProperty("11259375").GetSingle() == 0.25F &&
                appearance.GetProperty("faceBoneRegions").GetProperty("4")[1].GetSingle() == -2.0F &&
                Math.Abs(appearance.GetProperty("facialMorphIntensity").GetSingle() - 0.75F) < 0.0001F &&
                Math.Abs(appearance.GetProperty("weight").GetProperty("value").GetSingle() - 0.2F) < 0.0001F &&
                tints[0].GetProperty("index").GetInt32() == 1 && tints[0].GetProperty("percent").GetInt32() == 10 &&
                tints[1].GetProperty("index").GetInt32() == 2 && tints[1].GetProperty("type").GetInt32() == 3,
                "LooksMenu Morphs subfields or TintOrder were not typed faithfully.");

            var badHair = new WorkspacePath(Path.Combine(root.Value, "bad-hair.json"));
            await File.WriteAllTextAsync(badHair.Value, "{\"HairColor\":\"\"}");
            var badHairExit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "looksmenu",
                "--edition", "fallout4", "--input", badHair.Value, "--json"]), CancellationToken.None);
            Assert(badHairExit == CommandExitCode.ValidationFailure &&
                output.ToString().Contains("preset-haircolor-invalid", StringComparison.Ordinal),
                "Malformed LooksMenu HairColor was not rejected without throwing.");
            output.GetStringBuilder().Clear();

            var badGender = new WorkspacePath(Path.Combine(root.Value, "bad-gender.json"));
            await File.WriteAllTextAsync(badGender.Value, "{\"Gender\":256}");
            var badGenderExit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "looksmenu",
                "--edition", "fallout4", "--input", badGender.Value, "--json"]), CancellationToken.None);
            Assert(badGenderExit == CommandExitCode.ValidationFailure &&
                output.ToString().Contains("preset-gender-range", StringComparison.Ordinal),
                "Out-of-range LooksMenu Gender was not rejected.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRaceMenuPreset()
    {
        var (runner, output, _) = CreateRunner();
        var fixture = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\sse-racemenu.jslot";
        var exit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "racemenu-jslot", "--edition", "skyrimse", "--input", fixture, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "RaceMenu fixture inspection failed.");
        Assert(document.RootElement.GetProperty("appearance").GetProperty("customMorphs").GetProperty("NoseLength").GetSingle() == 0.2f, "RaceMenu custom morph was not typed.");
        Assert(document.RootElement.GetProperty("appearance").GetProperty("weight").GetProperty("value").GetSingle() == 0.62f, "RaceMenu weight was not typed.");

        var root = NewPresetOutput("racemenu-null-sculpt");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var nullSculpt = new WorkspacePath(Path.Combine(
                root.Value, "ChelStyleNullSculpt.jslot"));
            await File.WriteAllTextAsync(nullSculpt.Value, """
                {
                  "morphs": {
                    "default": {"morphs": [0.1, -0.2]},
                    "sculpt": null
                  },
                  "bodyMorphs": [{"name": "UBE Slider", "keys": [{"key": "NPCManager", "value": 75}]}]
                }
                """);
            var (nullRunner, nullOutput, nullError) = CreateRunner();
            var nullExit = await nullRunner.RunAsync(CommandLine.Parse([
                "preset", "inspect", "--format", "racemenu-jslot",
                "--edition", "skyrimse", "--input", nullSculpt.Value,
                "--json"
            ]), CancellationToken.None);
            using (var nullDocument = JsonDocument.Parse(nullOutput.ToString()))
            {
                JsonElement appearance =
                    nullDocument.RootElement.GetProperty("appearance");
                Assert(nullExit == CommandExitCode.Success &&
                       nullError.ToString().Length == 0 &&
                       nullDocument.RootElement.GetProperty("isValid").GetBoolean() &&
                       !nullOutput.ToString().Contains(
                           "preset-racemenu-sculpt-shape",
                           StringComparison.Ordinal) &&
                       appearance.GetProperty("raceMenu")
                           .GetProperty("sculptParts").GetArrayLength() == 0 &&
                       appearance.GetProperty("bodyMorphs")
                           .GetProperty("UBE Slider").GetSingle() == 75F,
                    "RaceMenu null sculpt was not admitted as a no-sculpt preset.");
            }

            foreach ((string name, string sculptJson) in new[]
                     {
                         ("object", "{}"),
                         ("string", "\"bad\""),
                         ("number", "42")
                     })
            {
                var malformed = new WorkspacePath(Path.Combine(
                    root.Value, $"MalformedSculpt-{name}.jslot"));
                await File.WriteAllTextAsync(malformed.Value,
                    "{\"morphs\":{\"sculpt\":" +
                    sculptJson + "}}");
                var (badRunner, badOutput, _) = CreateRunner();
                var badExit = await badRunner.RunAsync(CommandLine.Parse([
                    "preset", "inspect", "--format", "racemenu-jslot",
                    "--edition", "skyrimse", "--input", malformed.Value,
                    "--json"
                ]), CancellationToken.None);
                Assert(badExit == CommandExitCode.ValidationFailure &&
                       badOutput.ToString().Contains(
                           "preset-racemenu-sculpt-shape",
                           StringComparison.Ordinal),
                    $"Malformed non-null sculpt {name} was not refused.");
            }
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRaceMenuPresetCatalogCli()
    {
        var root = NewPresetOutput("racemenu-catalog-cli");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            const string fixture =
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\sse-racemenu.jslot";
            File.Copy(fixture, Path.Combine(root.Value, "Zulu.jslot"));
            File.Copy(fixture, Path.Combine(root.Value, "Emi Alpha.JSLOT"));
            await File.WriteAllTextAsync(Path.Combine(root.Value, "broken.jslot"),
                "{ not-json");
            await File.WriteAllTextAsync(Path.Combine(root.Value, "ignored.json"),
                await File.ReadAllTextAsync(fixture));

            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse([
                "preset", "catalog", "--directory", root.Value,
                "--filter", "emi", "--json"
            ]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            JsonElement response = document.RootElement;
            JsonElement entries = response.GetProperty("entries");
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   response.GetProperty("accepted").GetBoolean() &&
                   response.GetProperty("filter").GetString() == "emi" &&
                   response.GetProperty("omitted").GetInt32() == 1 &&
                   entries.GetArrayLength() == 1,
                "The RaceMenu catalog CLI did not filter admitted rows or retain malformed omission evidence.");
            JsonElement entry = entries[0];
            Assert(entry.GetProperty("displayName").GetString() == "Emi Alpha" &&
                   entry.GetProperty("sourceSha256").GetString() is { Length: 64 } &&
                   entry.GetProperty("compatibility").GetString() == "unavailable" &&
                   entry.GetProperty("summary").GetProperty("headParts").GetInt32() > 0,
                "The RaceMenu catalog CLI lost the selected preset identity, hash, authority state, or summary.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRaceMenuRealEncodingVariants()
    {
        var root = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-racemenu-real-encoding-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var source = new WorkspacePath(Path.Combine(root.Value, "source.jslot"));
            var exported = new WorkspacePath(Path.Combine(root.Value, "exported.jslot"));
            await File.WriteAllTextAsync(source.Value, """
                {
                  "faceTextures": [{"index": 0, "texture": "Actors/Character/Female/FemaleHead.dds"}],
                  "modNames": ["Skyrim.esm"],
                  "mods": [{"index": 0, "name": "Skyrim.esm"}],
                  "version": {"formatVersion": 3, "runtimeVersion": 17039392, "signature": 1163086675, "skseVersion": 33554736},
                  "overrides": [
                    {"node": "Body [Ovl3]", "values": [
                      {"key": 7, "type": 3, "index": -1, "data": -3094076},
                      {"key": 8, "type": 4, "index": -1, "data": 1},
                      {"key": 9, "type": 2, "index": 0, "data": "Actors/Character/Overlays/Default.dds"}
                    ]},
                    {"node": "Body [Ovl4]", "values": [
                      {"key": 7, "type": 3, "index": -1, "data": 0},
                      {"key": 8, "type": 4, "index": -1, "data": 0},
                      {"key": 9, "type": 2, "index": 0, "data": "\\SL Survival\\spanky\\spank_breasts_light.dds"}
                    ]}
                  ],
                  "transforms": [
                    {"firstPerson": false, "node": "CME Camera1st [Cam1]", "keys": [
                      {"name": "RMX_Head", "values": [{"key": 30, "type": 4, "index": 0, "data": 1.05}]}
                    ]},
                    {"firstPerson": true, "node": "CME Camera1st [Cam1]", "keys": [
                      {"name": "RSMPlugin", "values": [{"key": 30, "type": 4, "index": 0, "data": 1.01}]}
                    ]}
                  ]
                }
                """);

            var (runner, output, error) = CreateRunner();
            var inspectExit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "racemenu-jslot",
                "--edition", "skyrimse", "--input", source.Value, "--json"]), CancellationToken.None);
            using (var inspected = JsonDocument.Parse(output.ToString()))
            {
                var raceMenu = inspected.RootElement.GetProperty("appearance").GetProperty("raceMenu");
                var transforms = raceMenu.GetProperty("nodeTransforms");
                var overlay = raceMenu.GetProperty("bodyOverlays")[0];
                var inactiveLegacyOverlay = raceMenu.GetProperty("bodyOverlays")[1];
                Assert(inspectExit == CommandExitCode.Success && inspected.RootElement.GetProperty("isValid").GetBoolean() &&
                    transforms.GetArrayLength() == 2 &&
                    transforms[0].GetProperty("keySets")[0].GetProperty("name").GetString() == "RMX_Head" &&
                    transforms[1].GetProperty("keySets")[0].GetProperty("name").GetString() == "RSMPlugin" &&
                    transforms[0].GetProperty("firstPerson").GetBoolean() != transforms[1].GetProperty("firstPerson").GetBoolean() &&
                    overlay.GetProperty("alpha").GetSingle() == 1F &&
                    overlay.GetProperty("values")[1].GetProperty("data").GetProperty("kind").GetString() == "signedInteger" &&
                    inactiveLegacyOverlay.GetProperty("alpha").GetSingle() == 0F &&
                    inactiveLegacyOverlay.GetProperty("diffuse").GetString() ==
                    "\\SL Survival\\spanky\\spank_breasts_light.dds" &&
                    raceMenu.GetProperty("faceTextures")[0].GetProperty("index").GetInt32() == 0 &&
                    raceMenu.GetProperty("modNames")[0].GetProperty("value").GetString() == "Skyrim.esm" &&
                    raceMenu.GetProperty("mods")[0].GetProperty("name").GetProperty("value").GetString() == "Skyrim.esm" &&
                    raceMenu.GetProperty("version").GetProperty("formatVersion").GetUInt32() == 3,
                    "A real RaceMenu transform, alpha-zero legacy overlay, dependency, face-texture, or version encoding was rejected or normalized.");
            }

            output.GetStringBuilder().Clear();
            var exportExit = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "racemenu-jslot",
                "--edition", "skyrimse", "--input", source.Value, "--output", exported.Value, "--json"]), CancellationToken.None);
            using var roundTrip = JsonDocument.Parse(await File.ReadAllTextAsync(exported.Value));
            Assert(exportExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                roundTrip.RootElement.GetProperty("transforms")[0].GetProperty("keys")[0].GetProperty("name").GetString() == "RMX_Head" &&
                roundTrip.RootElement.GetProperty("transforms")[1].GetProperty("keys")[0].GetProperty("name").GetString() == "RSMPlugin" &&
                roundTrip.RootElement.GetProperty("overrides")[0].GetProperty("values")[1].GetProperty("data").GetInt32() == 1 &&
                roundTrip.RootElement.GetProperty("overrides")[1].GetProperty("values")[2].GetProperty("data").GetString() ==
                "\\SL Survival\\spanky\\spank_breasts_light.dds" &&
                roundTrip.RootElement.GetProperty("faceTextures")[0].GetProperty("texture").GetString() == "Actors/Character/Female/FemaleHead.dds" &&
                roundTrip.RootElement.GetProperty("modNames")[0].GetString() == "Skyrim.esm" &&
                roundTrip.RootElement.GetProperty("mods")[0].GetProperty("index").GetByte() == 0 &&
                roundTrip.RootElement.GetProperty("version").GetProperty("signature").GetUInt32() == 1163086675,
                "RaceMenu export did not preserve the real named transforms, scalar kind, dependencies, face textures, and version.");

            var activeRooted = new WorkspacePath(Path.Combine(root.Value, "active-rooted.jslot"));
            await File.WriteAllTextAsync(activeRooted.Value, """
                {
                  "overrides": [{"node": "Body [Ovl0]", "values": [
                    {"key": 8, "type": 4, "index": -1, "data": 1},
                    {"key": 9, "type": 2, "index": 0, "data": "\\SL Survival\\active.dds"}
                  ]}]
                }
                """);
            output.GetStringBuilder().Clear();
            var activeRootedExit = await runner.RunAsync(CommandLine.Parse([
                "preset", "inspect", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--input", activeRooted.Value, "--json"
            ]), CancellationToken.None);
            Assert(activeRootedExit == CommandExitCode.ValidationFailure &&
                   output.ToString().Contains("preset-racemenu-overlay-asset-path", StringComparison.Ordinal),
                "An active rooted RaceMenu overlay texture was not rejected.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRaceMenuNestedMetadata()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-racemenu-nested-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var source = new WorkspacePath(Path.Combine(root.Value, "nested.jslot"));
            await File.WriteAllTextAsync(source.Value, """
                {
                  "headParts": [{"formId": 74565, "formIdentifier": "Skyrim.esm|00012345", "type": 1}],
                  "headTexture": "Skyrim.esm|00000020",
                  "actor": {"hairColor": 1122867, "weight": 62},
                  "morphs": {
                    "default": {"morphs": [0.1, -0.2, 0.3], "presets": [1, 4294967295], "future": true},
                    "sculptDivisor": 10000,
                    "sculpt": [{"host": "FemaleHeadCharGen.tri", "vertices": 100, "data": [[3, 100, -200, 300]]}]
                  },
                  "customMorphs": [{"name": "Smile", "value": 0.2}],
                  "tintInfo": [{"color": 4278255360, "index": 4, "texture": "textures\\actors\\character\\tint.dds"}],
                  "bodyMorphs": [{"name": "Breast", "keys": [{"key": "Base", "value": 0.4}, {"key": "Outfit", "value": 0.2}]}],
                  "overrides": [{"node": "Body [Ovl1]", "diffuse": "textures\\actors\\character\\tattoo.dds", "normal": "textures\\actors\\character\\tattoo_n.dds", "tint": [1, 1, 1, 1], "alpha": 0.8}],
                  "transforms": [{"firstPerson": false, "node": "NPC Spine [Spn0]", "keys": [{"name": "RSMTransform", "values": [
                    {"key": 30, "type": 4, "index": 2, "data": 1.02}, {"key": 33, "type": 3, "index": 3, "data": 0}
                  ]}]}],
                  "skinOverrides": [{"firstPerson": false, "slotMask": 32, "values": [
                    {"key": 9, "type": 2, "index": 0, "data": "textures\\actors\\character\\body.dds"},
                    {"key": 8, "type": 4, "index": -1, "data": 0.75}
                  ]}],
                  "EngineExtra": {"keep": true}
                }
                """);
            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--input", source.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            var rootResponse = document.RootElement;
            var appearance = rootResponse.GetProperty("appearance");
            var raceMenu = appearance.GetProperty("raceMenu");
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && rootResponse.GetProperty("isValid").GetBoolean(),
                "Nested RaceMenu metadata inspection failed.");
            Assert(appearance.GetProperty("sliderMorphs").GetArrayLength() == 3 &&
                raceMenu.GetProperty("headTexture").GetString() == "Skyrim.esm|00000020" &&
                raceMenu.GetProperty("faceMorphPresets")[1].GetUInt32() == uint.MaxValue,
                "Nested RaceMenu identity/morph metadata was not typed.");
            Assert(raceMenu.GetProperty("sculptDivisor").GetInt32() == 10000 &&
                Math.Abs(raceMenu.GetProperty("sculptParts")[0].GetProperty("vertices")[0].GetProperty("dx").GetSingle() - 0.01F) < 0.0001F &&
                raceMenu.GetProperty("bodyMorphsKeyed").GetProperty("Breast").GetProperty("Outfit").GetSingle() == 0.2F,
                "RaceMenu sculpt or keyed body morph metadata was not preserved.");
            Assert(raceMenu.GetProperty("bodyOverlays")[0].GetProperty("node").GetString() == "Body [Ovl1]" &&
                raceMenu.GetProperty("nodeTransforms")[0].GetProperty("scale").GetSingle() == 1.02F &&
                raceMenu.GetProperty("skinOverrides")[0].GetProperty("alpha").GetSingle() == 0.75F,
                "RaceMenu overlays, transforms, or skin overrides were not typed.");
            Assert(appearance.GetProperty("unknownFields").EnumerateArray().Any(item => item.GetProperty("path").GetString() == "$.EngineExtra"),
                "Unknown RaceMenu root fields were not reported.");
            Assert(appearance.GetProperty("unknownFields").EnumerateArray().Any(item => item.GetProperty("path").GetString() == "$.morphs.default.future"),
                "Unknown RaceMenu nested fields were not reported.");

            var invalid = new WorkspacePath(Path.Combine(root.Value, "invalid.jslot"));
            await File.WriteAllTextAsync(invalid.Value, "{\"actor\":{\"weight\":101},\"overrides\":[{\"node\":\"Body\",\"diffuse\":\"..\\\\bad.dds\"}]}");
            output.GetStringBuilder().Clear();
            var invalidExit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--input", invalid.Value, "--json"]), CancellationToken.None);
            Assert(invalidExit == CommandExitCode.ValidationFailure && output.ToString().Contains("preset-racemenu-weight-range", StringComparison.Ordinal) &&
                output.ToString().Contains("preset-racemenu-asset-path", StringComparison.Ordinal),
                "Malformed RaceMenu range/path values were not rejected.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRaceMenuCanonicalExport()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-racemenu-save-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var source = new WorkspacePath(Path.Combine(root.Value, "source.jslot"));
            var first = new WorkspacePath(Path.Combine(root.Value, "out", "one.jslot"));
            var second = new WorkspacePath(Path.Combine(root.Value, "out", "two.jslot"));
            Directory.CreateDirectory(Path.GetDirectoryName(first.Value)!);
            await File.WriteAllTextAsync(source.Value, """
                {
                  "actor": {"hairColor": 1122867, "headTexture": "Skyrim.esm|00000020", "weight": 62},
                  "headParts": [{"formId": 74565, "formIdentifier": "Skyrim.esm|00012345", "type": 1}],
                  "morphs": {
                    "default": {"morphs": [0.1, -0.2, 0.3], "presets": [1, 4294967295]},
                    "custom": [{"name": "Smile", "value": 0.2}],
                    "sculptDivisor": 10000,
                    "sculpt": [{"host": "FemaleHeadCharGen.tri", "vertices": 100, "data": [[3, 100, -200, 300]]}]
                  },
                  "tintInfo": [],
                  "bodyMorphs": [{"name": "Breast", "keys": [{"key": "Base", "value": 0.4}, {"key": "Outfit", "value": 0.2}]}],
                  "overrides": [{"node": "Body [Ovl1]", "values": [
                    {"key": 9, "type": 2, "index": 0, "data": "textures\\actors\\character\\tattoo.dds"},
                    {"key": 9, "type": 2, "index": 1, "data": "textures\\actors\\character\\tattoo_n.dds"},
                    {"key": 7, "type": 3, "index": -1, "data": -16711936},
                    {"key": 8, "type": 4, "index": -1, "data": 0.8}
                  ]}],
                  "transforms": [{"firstPerson": false, "node": "NPC Spine [Spn0]", "keys": [{"name": "RSMTransform", "values": [
                    {"key": 30, "type": 4, "index": 2, "data": 1.02}, {"key": 33, "type": 3, "index": 3, "data": 0}
                  ]}]}],
                  "skinOverrides": [{"firstPerson": false, "slotMask": 32, "values": [
                    {"key": 9, "type": 2, "index": 0, "data": "textures\\actors\\character\\body.dds"},
                    {"key": 8, "type": 4, "index": -1, "data": 0.75}
                  ]}]
                }
                """);
            var (runner, output, error) = CreateRunner();
            var firstExit = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--input", source.Value, "--output", first.Value, "--json"]), CancellationToken.None);
            output.GetStringBuilder().Clear();
            var secondExit = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--input", source.Value, "--output", second.Value, "--json"]), CancellationToken.None);
            Assert(firstExit == CommandExitCode.Success && secondExit == CommandExitCode.Success && error.ToString().Length == 0,
                "RaceMenu export did not succeed twice.");
            Assert(Hash(first) == Hash(second), "RaceMenu exports were not deterministic.");
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(first.Value));
            var rootJson = exported.RootElement;
            Assert(rootJson.GetProperty("actor").GetProperty("headTexture").GetString() == "Skyrim.esm|00000020" &&
                !rootJson.TryGetProperty("customMorphs", out _) && !rootJson.TryGetProperty("sliderMorphs", out _) &&
                rootJson.GetProperty("morphs").GetProperty("custom")[0].GetProperty("name").GetString() == "Smile" &&
                rootJson.GetProperty("bodyMorphs")[0].GetProperty("keys").GetArrayLength() == 2,
                "RaceMenu export did not use the pinned nested canonical shape.");
            output.GetStringBuilder().Clear();
            var inspect = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--input", first.Value, "--json"]), CancellationToken.None);
            using var inspected = JsonDocument.Parse(output.ToString());
            Assert(inspect == CommandExitCode.Success && inspected.RootElement.GetProperty("isValid").GetBoolean() &&
                inspected.RootElement.GetProperty("appearance").GetProperty("raceMenu").GetProperty("bodyOverlays")[0].GetProperty("alpha").GetSingle() == 0.8F,
                "Canonical RaceMenu export did not reload through the typed reader.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestPresetExport()
    {
        var (runner, output, _) = CreateRunner();
        var fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json");
        var first = NewPresetOutput("export-one.json"); var second = NewPresetOutput("export-two.json");
        try
        {
            var firstExit = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "looksmenu", "--edition", "fallout4", "--input", fixture.Value, "--output", first.Value, "--json"]), CancellationToken.None);
            var firstResponse = JsonDocument.Parse(output.ToString()); output.GetStringBuilder().Clear();
            var secondExit = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "looksmenu", "--edition", "fallout4", "--input", fixture.Value, "--output", second.Value, "--json"]), CancellationToken.None);
            Assert(firstExit == CommandExitCode.Success && secondExit == CommandExitCode.Success, "Preset export did not succeed twice.");
            Assert(File.Exists(first.Value) && File.Exists(second.Value), "Preset export did not create both outputs.");
            Assert(Hash(first) == Hash(second), "Deterministic preset exports differ.");
            Assert(firstResponse.RootElement.GetProperty("written").GetBoolean(), "Preset export response did not report written.");
        }
        finally { DeleteIfExists(first.Value); DeleteIfExists(second.Value); }
    }

    private static async Task TestLooksMenuCanonicalExport()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-looksmenu-save-tests");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var source = new WorkspacePath(Path.Combine(root.Value, "source.json"));
            var output = new WorkspacePath(Path.Combine(root.Value, "out", "canonical.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output.Value)!);
            await File.WriteAllTextAsync(source.Value, """
                {
                  "BodyMorphs": {"Zeta": 0.2, "Alpha": -0.1},
                  "Gender": 1,
                  "HairColor": "LooksMenu.esp|0000002A",
                  "HeadParts": ["Fallout4.esm|00012345"],
                  "Morphs": {
                    "Values": [0.1, 0.2],
                    "Presets": {"00ABCDEF": 0.25},
                    "Regions": {"00000004": [1.0, -2.0, 3.0]},
                    "Intensity": 0.75
                  },
                  "Overlays": [{"template": "Actors\\\\Character\\\\Overlays\\\\Example.dds", "priority": 3, "tint": [1,0.5,0.25,1], "offsetUV": [0,0], "scaleUV": [1,1]}],
                  "Skin": "LooksMenu.esp|00000030",
                  "Tints": {
                    "00000002": {"Percent": 50, "Type": 2},
                    "00000003": {"Color": 3, "ColorID": 33, "Percent": 20, "Type": 1},
                    "00000001": {"Color": 1, "ColorID": 11, "Percent": 0, "Type": 1}
                  },
                  "TintOrder": ["00000003", "00000002", "00000001"],
                  "Weight": [0.2, 0.5, 0.3],
                  "EngineExtra": {"preserve": "diagnostic-only"}
                }
                """);
            var (runner, outputText, errorText) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "looksmenu", "--edition", "fallout4",
                "--input", source.Value, "--output", output.Value, "--json"]), CancellationToken.None);
            Assert(exit == CommandExitCode.Success && errorText.ToString().Length == 0 && File.Exists(output.Value),
                "Canonical LooksMenu export did not succeed.");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            var rootElement = document.RootElement;
            Assert(rootElement.GetProperty("Gender").GetInt32() == 1 && rootElement.GetProperty("HeadParts").GetArrayLength() == 1 &&
                rootElement.GetProperty("Weight").GetArrayLength() == 3,
                "Canonical LooksMenu export omitted required root fields.");
            var morphs = rootElement.GetProperty("Morphs");
            Assert(morphs.GetProperty("Values").GetArrayLength() == 5 &&
                morphs.GetProperty("Regions").GetProperty("4").GetArrayLength() == 8 &&
                morphs.GetProperty("Presets").GetProperty("ABCDEF").GetSingle() == 0.25F,
                "Canonical LooksMenu export did not pad or normalize typed morph fields.");
            var tints = rootElement.GetProperty("Tints");
            Assert(tints.GetProperty("2").GetProperty("Type").GetInt32() == 2 &&
                !tints.GetProperty("2").TryGetProperty("Color", out _) &&
                tints.GetProperty("3").GetProperty("ColorID").GetInt32() == 33 &&
                !tints.TryGetProperty("1", out _),
                "Canonical LooksMenu export did not filter and type tint layers.");
            var tintOrder = rootElement.GetProperty("TintOrder");
            Assert(tintOrder[0].GetString() == "3" && tintOrder[1].GetString() == "2",
                "Canonical LooksMenu export did not preserve render order independently of dictionary order.");

            outputText.GetStringBuilder().Clear();
            var reload = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "looksmenu", "--edition", "fallout4",
                "--input", output.Value, "--json"]), CancellationToken.None);
            using var reloadDocument = JsonDocument.Parse(outputText.ToString());
            var reloaded = reloadDocument.RootElement.GetProperty("appearance");
            var reloadCodes = string.Join(",", reloadDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                .Select(item => item.GetProperty("code").GetString()));
            Assert(reload == CommandExitCode.Success, $"Canonical LooksMenu reload failed ({reloadCodes}): {outputText}");
            Assert(reloadDocument.RootElement.GetProperty("isValid").GetBoolean(), $"Canonical LooksMenu reload was invalid: {outputText}");
            Assert(reloaded.GetProperty("chargenFaceMorphs").GetProperty("11259375").GetSingle() == 0.25F,
                $"Canonical LooksMenu reload lost Presets: {outputText}");
            Assert(reloaded.GetProperty("faceBoneRegions").GetProperty("4").GetArrayLength() == 8,
                $"Canonical LooksMenu reload lost padded Regions: {outputText}");
            Assert(reloaded.GetProperty("tints").GetArrayLength() == 2,
                $"Canonical LooksMenu reload changed tint count: {outputText}");

            var incomplete = new WorkspacePath(Path.Combine(root.Value, "incomplete.json"));
            var incompleteOutput = new WorkspacePath(Path.Combine(root.Value, "out", "incomplete.json"));
            await File.WriteAllTextAsync(incomplete.Value, "{\"Gender\":1}");
            outputText.GetStringBuilder().Clear();
            var refused = await runner.RunAsync(CommandLine.Parse(["preset", "export", "--format", "looksmenu", "--edition", "fallout4",
                "--input", incomplete.Value, "--output", incompleteOutput.Value, "--json"]), CancellationToken.None);
            Assert(refused == CommandExitCode.ValidationFailure && !File.Exists(incompleteOutput.Value) &&
                outputText.ToString().Contains("preset-export-headparts-required", StringComparison.Ordinal),
                "Incomplete LooksMenu export was not refused with a typed diagnostic.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestPresetDuplicateKeys()
    {
        var path = NewPresetOutput("duplicate.json");
        await File.WriteAllTextAsync(path.Value, "{\"Gender\":1,\"gender\":2}");
        try
        {
            var result = await new PresetService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"))
                .InspectAsync(new PresetParseRequest(PresetFormat.LooksMenu, GameEdition.Fallout4, path), CancellationToken.None);
            Assert(result.Document is null && result.Diagnostics.Any(item => item.Code == "preset-duplicate-key"), "Duplicate preset keys were accepted.");
        }
        finally { DeleteIfExists(path.Value); }
    }

    private static async Task TestPresetOutsideK()
    {
        var result = await new PresetService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"))
            .InspectAsync(new PresetParseRequest(PresetFormat.LooksMenu, GameEdition.Fallout4, new WorkspacePath("F:\\ExampleGame\\Data\\preset.json")), CancellationToken.None);
        Assert(result.Document is null && result.Diagnostics.Any(item => item.Code == "preset-input-outside-lab"), "Outside-K preset input was not refused.");
    }

    private static async Task TestPresetDiff()
    {
        var left = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json");
        var right = NewPresetOutput("diff-right.json");
        var text = await File.ReadAllTextAsync(left.Value); await File.WriteAllTextAsync(right.Value,
            text.Replace("0.25", "0.35", StringComparison.Ordinal).Replace("\"Gender\":1", "\"Gender\":2", StringComparison.Ordinal));
        try
        {
            var result = await new PresetService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"))
                .DiffAsync(new PresetDiffRequest(new PresetParseRequest(PresetFormat.LooksMenu, GameEdition.Fallout4, left), new PresetParseRequest(PresetFormat.LooksMenu, GameEdition.Fallout4, right)), CancellationToken.None);
            Assert(result.Differences.Any(item => item.Path == "bodyMorphs.CBBE Breast"), "Preset diff did not report the changed body morph.");
            Assert(result.Differences.Any(item => item.Path == "gender" && item.Left == "1" && item.Right == "2"), "Preset diff did not report the changed gender.");
        }
        finally { DeleteIfExists(right.Value); }
    }

    private static async Task TestPresetDiffNested()
    {
        var root = NewPresetOutput("diff-nested-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var left = new WorkspacePath(Path.Combine(root.Value, "left.jslot"));
        var right = new WorkspacePath(Path.Combine(root.Value, "right.jslot"));
        var fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\sse-racemenu.jslot");
        var sourceText = await File.ReadAllTextAsync(fixture.Value);
        await File.WriteAllTextAsync(left.Value, sourceText);
        await File.WriteAllTextAsync(right.Value, sourceText
            .Replace("0.4", "0.45", StringComparison.Ordinal)
            .Replace("-0.2", "-0.25", StringComparison.Ordinal)
            .Replace("{\"headParts\"", "{\"futureField\":true,\"headParts\"", StringComparison.Ordinal)
            .Replace("Skyrim.esm|00012345", "unresolved", StringComparison.Ordinal));
        try
        {
            var (runner, output, error) = CreateRunner();
            var args = CommandLine.Parse(["preset", "diff", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--left", left.Value, "--right", right.Value, "--json"]);
            var exit = await runner.RunAsync(args, CancellationToken.None);
            var first = output.ToString();
            using var response = JsonDocument.Parse(first);
            var differences = response.RootElement.GetProperty("differences");
            var diagnostics = response.RootElement.GetProperty("diagnostics");
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0,
                "Nested preset diff should succeed with warning-only loss diagnostics.");
            Assert(differences.EnumerateArray().Any(item => item.GetProperty("path").GetString() == "raceMenu.bodyMorphsKeyed.Breast.NPCManager" &&
                item.GetProperty("left").GetString() == "0.4" && item.GetProperty("right").GetString() == "0.45"),
                "Nested RaceMenu body morph difference was not reported at a stable path.");
            Assert(differences.EnumerateArray().Any(item => item.GetProperty("path").GetString() == "sliderMorphs[1]" &&
                item.GetProperty("left").GetString() == "-0.2" && item.GetProperty("right").GetString() == "-0.25"),
                "Typed slider array differences were not compared.");
            Assert(diagnostics.EnumerateArray().Any(item => item.GetProperty("code").GetString() == "preset-diff-unsupported-field") &&
                diagnostics.EnumerateArray().Any(item => item.GetProperty("code").GetString() == "preset-diff-unresolved-identifier"),
                "Preset diff did not distinguish unsupported and unresolved diagnostics.");

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var secondExit = await runner.RunAsync(args, CancellationToken.None);
            Assert(secondExit == CommandExitCode.Success && output.ToString() == first,
                "Preset diff output was not deterministic across repeated runs.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPresetResolve()
    {
        var (runner, output, _) = CreateRunner();
        var map = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\load-order.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["preset", "resolve", "--identifier", "ExampleHair.esp|01000010", "--load-order", map, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "Portable form identifier did not resolve.");
        Assert(document.RootElement.GetProperty("resolvedFormId").GetString() == "0x02000010", "Load-order mapping produced the wrong FormID.");
    }

    private static async Task TestAppearanceCopy()
    {
        var root = NewPresetOutput("appearance-copy-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "source.json"));
        var target = new WorkspacePath(Path.Combine(root.Value, "target.json"));
        var outputPath = new WorkspacePath(Path.Combine(root.Value, "output.json"));
        var fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json");
        var sourceText = await File.ReadAllTextAsync(fixture.Value);
        await File.WriteAllTextAsync(source.Value, sourceText);
        await File.WriteAllTextAsync(target.Value, sourceText
            .Replace("\"Weight\":[0.2,0.5,0.3]", "\"Weight\":[0.8,0.7,0.6]", StringComparison.Ordinal)
            .Replace("\"Percent\":1", "\"Percent\":99", StringComparison.Ordinal));
        try
        {
            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["appearance", "copy", "--format", "looksmenu", "--edition", "fallout4",
                "--from", source.Value, "--to", target.Value, "--sections", "body-weight,face-tints", "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var response = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && response.RootElement.GetProperty("written").GetBoolean(),
                "Appearance copy did not write a selected-section output.");
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var inspectExit = await runner.RunAsync(CommandLine.Parse(["preset", "inspect", "--format", "looksmenu", "--edition", "fallout4", "--input", outputPath.Value, "--json"]), CancellationToken.None);
            using var inspected = JsonDocument.Parse(output.ToString());
            var appearance = inspected.RootElement.GetProperty("appearance");
            Assert(inspectExit == CommandExitCode.Success && appearance.GetProperty("weight").GetProperty("value").GetSingle() == 0.2F &&
                appearance.GetProperty("tints")[0].GetProperty("percent").GetInt32() == 1 &&
                appearance.GetProperty("bodyMorphs").GetProperty("CBBE Breast").GetSingle() == 0.25F,
                "Appearance copy changed an unselected target section or failed to copy selected source sections.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestAppearanceCopyAll()
    {
        var root = NewPresetOutput("appearance-copy-all-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "source.json"));
        var target = new WorkspacePath(Path.Combine(root.Value, "target.json"));
        var outputPath = new WorkspacePath(Path.Combine(root.Value, "output.json"));
        var fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json");
        var sourceText = await File.ReadAllTextAsync(fixture.Value);
        await File.WriteAllTextAsync(source.Value, sourceText);
        await File.WriteAllTextAsync(target.Value, sourceText
            .Replace("\"Weight\":[0.2,0.5,0.3]", "\"Weight\":[0.8,0.7,0.6]", StringComparison.Ordinal)
            .Replace("\"Percent\":1", "\"Percent\":99", StringComparison.Ordinal));
        try
        {
            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["appearance", "copy", "--format", "looksmenu", "--edition", "fallout4",
                "--from", source.Value, "--to", target.Value, "--sections", "all", "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var response = JsonDocument.Parse(output.ToString());
            var sections = response.RootElement.GetProperty("sections").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && response.RootElement.GetProperty("written").GetBoolean(),
                "Appearance copy with --sections all should write a valid preset output.");
            Assert(!sections.Contains("skin-override", StringComparer.Ordinal) &&
                !sections.Contains("outfit", StringComparer.Ordinal) &&
                !sections.Contains("sculpt", StringComparer.Ordinal) &&
                !sections.Contains("chargen-flag", StringComparer.Ordinal),
                "Appearance copy with --sections all included unsupported or cross-game sections.");

            var skyrimSource = new WorkspacePath(Path.Combine(root.Value, "source.jslot"));
            var skyrimTarget = new WorkspacePath(Path.Combine(root.Value, "target.jslot"));
            var skyrimOutput = new WorkspacePath(Path.Combine(root.Value, "output.jslot"));
            var skyrimFixture = new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\sse-racemenu.jslot");
            string skyrimSourceText = (await File.ReadAllTextAsync(skyrimFixture.Value))
                .Replace("\"actor\":{\"hairColor\":",
                    "\"actor\":{\"headTexture\":\"Skyrim.esm|00000020\",\"hairColor\":",
                    StringComparison.Ordinal)
                .Replace("\"sculpt\":[",
                    "\"morphs\":{\"sculpt\":[{\"host\":\"source-head\",\"vertices\":1,\"data\":[]}]},\"faceTextures\":[{\"index\":4,\"texture\":\"actors/source-tint.dds\"}],\"sculpt\":[",
                    StringComparison.Ordinal);
            string skyrimTargetText = skyrimSourceText
                .Replace("Skyrim.esm|00000020", "Skyrim.esm|00000030",
                    StringComparison.Ordinal)
                .Replace("actors/source-tint.dds", "actors/target-tint.dds",
                    StringComparison.Ordinal)
                .Replace("\"weight\":0.62", "\"weight\":0.31",
                    StringComparison.Ordinal)
                .Replace("\"value\":0.4", "\"value\":0.9",
                    StringComparison.Ordinal);
            await File.WriteAllTextAsync(skyrimSource.Value, skyrimSourceText);
            await File.WriteAllTextAsync(skyrimTarget.Value, skyrimTargetText);
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var skyrimExit = await runner.RunAsync(CommandLine.Parse([
                "appearance", "copy", "--format", "racemenu-jslot",
                "--edition", "skyrimse", "--from", skyrimSource.Value,
                "--to", skyrimTarget.Value, "--sections", "all",
                "--output", skyrimOutput.Value, "--json"
            ]), CancellationToken.None);
            using var skyrimResponse = JsonDocument.Parse(output.ToString());
            string?[] skyrimSections = skyrimResponse.RootElement
                .GetProperty("sections")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            using var skyrimWritten = JsonDocument.Parse(
                await File.ReadAllTextAsync(skyrimOutput.Value));
            JsonElement skyrimRoot = skyrimWritten.RootElement;
            Assert(skyrimExit == CommandExitCode.Success &&
                   error.ToString().Length == 0 &&
                   skyrimSections.Contains("sculpt", StringComparer.Ordinal) &&
                   !skyrimSections.Contains("body-regions", StringComparer.Ordinal) &&
                   !skyrimSections.Contains("face-bone-regions", StringComparer.Ordinal) &&
                   !skyrimSections.Contains("lm-skin-template", StringComparer.Ordinal) &&
                   !skyrimSections.Contains("skin-override", StringComparer.Ordinal) &&
                   !skyrimSections.Contains("outfit", StringComparer.Ordinal) &&
                   !skyrimSections.Contains("chargen-flag", StringComparer.Ordinal),
                "Skyrim appearance copy all did not expose the exact preset-file category set.");
            Assert(skyrimRoot.GetProperty("actor").GetProperty("headTexture").GetString() ==
                       "Skyrim.esm|00000020" &&
                   Math.Abs(skyrimRoot.GetProperty("actor").GetProperty("weight").GetSingle() -
                       0.62F) < 0.0001F &&
                   skyrimRoot.GetProperty("faceTextures")[0]
                       .GetProperty("texture").GetString() == "actors/source-tint.dds" &&
                   Math.Abs(skyrimRoot.GetProperty("bodyMorphs")[0].GetProperty("keys")[0]
                       .GetProperty("value").GetSingle() - 0.4F) < 0.0001F &&
                   skyrimRoot.GetProperty("morphs").GetProperty("sculpt")[0]
                       .GetProperty("host").GetString() == "source-head",
                "Skyrim appearance copy all lost head texture, tint texture, weight, body shape, or sculpt authority.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFo4FaceGenDiagnosis()
    {
        var (runner, output, _) = CreateRunner();
        var manifest = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\fo4-facegen-valid.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "diagnose", "--game", "fallout4", "--manifest", manifest, "--npc", "0x00000800", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success, "FO4 FaceGen diagnosis failed.");
        Assert(document.RootElement.GetProperty("isApplicable").GetBoolean(), "FO4 valid head was not applicable.");
        Assert(document.RootElement.GetProperty("validHeadShapeCount").GetInt32() == 1, "FO4 valid head count changed.");
        output.GetStringBuilder().Clear();
        var aliasExit = await runner.RunAsync(CommandLine.Parse(["facegen", "analyze", "--game", "fallout4", "--manifest", manifest, "--npc", "0x00000800", "--json"]), CancellationToken.None);
        using var aliasDocument = JsonDocument.Parse(output.ToString());
        Assert(aliasExit == CommandExitCode.Success && aliasDocument.RootElement.GetProperty("isApplicable").GetBoolean(),
            "FaceGen analyze alias did not route to the typed diagnosis service.");
    }

    private static async Task TestFaceGeomBuild()
    {
        var root = NewPresetOutput("facegeom-build-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "build-manifest.json"));
        var output = new WorkspacePath(Path.Combine(root.Value, "facegeom-a.json"));
        var repeat = new WorkspacePath(Path.Combine(root.Value, "facegeom-b.json"));
        var fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\fo4-facegen-valid.json");
        var source = (await File.ReadAllTextAsync(fixture.Value)).Trim();
        source = source[..^1] + ",\"headParts\":[{\"editorId\":\"HumanHead\",\"partType\":1,\"meshPath\":\"meshes/actors/character/characterassets/head.nif\"}],\"morphs\":[{\"name\":\"JawShape\",\"value\":0.25}],\"textureRoutes\":[{\"slot\":\"diffuse\",\"path\":\"textures/actors/character/facegen/m5_d.dds\",\"provider\":\"fixture\"}]}";
        await File.WriteAllTextAsync(manifest.Value, source);
        try
        {
            var (runner, response, error) = CreateRunner();
            var args = CommandLine.Parse(["facegen", "build-geom", "--edition", "fallout4", "--manifest", manifest.Value,
                "--npc", "0x00000800", "--output", output.Value, "--json"]);
            var exit = await runner.RunAsync(args, CancellationToken.None);
            using var result = JsonDocument.Parse(response.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && result.RootElement.GetProperty("written").GetBoolean(),
                "FaceGeom build did not write the semantic artifact.");
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            Assert(artifact.RootElement.GetProperty("artifactKind").GetString() == "facegeom-semantic-build" &&
                artifact.RootElement.GetProperty("edition").GetString() == "fallout4" &&
                artifact.RootElement.GetProperty("shapes")[0].GetProperty("included").GetBoolean() &&
                artifact.RootElement.GetProperty("headParts")[0].GetProperty("editorId").GetString() == "HumanHead" &&
                artifact.RootElement.GetProperty("morphs")[0].GetProperty("value").GetSingle() == 0.25F &&
                artifact.RootElement.GetProperty("textureRoutes")[0].GetProperty("slot").GetString() == "diffuse",
                "FaceGeom semantic artifact omitted typed build inputs.");

            response.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var repeatExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-geom", "--edition", "fallout4", "--manifest", manifest.Value,
                "--npc", "0x00000800", "--output", repeat.Value, "--json"]), CancellationToken.None);
            Assert(repeatExit == CommandExitCode.Success &&
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output.Value))) ==
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(repeat.Value))),
                "FaceGeom semantic output was not deterministic.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceTintBuild()
    {
        var root = NewPresetOutput("facetint-build-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "build-manifest.json"));
        var fo4Output = new WorkspacePath(Path.Combine(root.Value, "fo4-tint-a.json"));
        var fo4Repeat = new WorkspacePath(Path.Combine(root.Value, "fo4-tint-b.json"));
        var fo4Dds = new WorkspacePath(Path.Combine(root.Value, "fo4-tint.dds"));
        var bc3Output = new WorkspacePath(Path.Combine(root.Value, "fo4-tint-bc3.json"));
        var bc3Dds = new WorkspacePath(Path.Combine(root.Value, "fo4-tint-bc3.dds"));
        var bc7Output = new WorkspacePath(Path.Combine(root.Value, "fo4-tint-bc7.json"));
        var bc7Dds = new WorkspacePath(Path.Combine(root.Value, "fo4-tint-bc7.dds"));
        var sseOutput = new WorkspacePath(Path.Combine(root.Value, "sse-tint.json"));
        await File.WriteAllTextAsync(manifest.Value, """
            {
              "schemaVersion": "1",
              "npcFormId": "0x00000800",
              "width": 512,
              "height": 512,
              "format": "bgra8",
              "mipCount": 1,
              "alphaMode": "preserve",
              "baseColor": [0.2, 0.4, 0.6, 0.5],
              "layers": [
                {"name":"complexion","source":"textures/complexion.dds","provider":"fixture","blend":"over","opacity":0.5,"color":[1,0,0,1]},
                {"name":"detail","source":"textures/detail.dds","provider":"fixture","blend":"multiply","opacity":0.25,"color":[0.5,1,1,1]}
              ],
              "probes": [{"x":0,"y":0},{"x":511,"y":511}]
            }
            """);
        try
        {
            var (runner, response, error) = CreateRunner();
            var args = CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4", "--manifest", manifest.Value,
                "--npc", "0x00000800", "--output", fo4Output.Value, "--json"]);
            var exit = await runner.RunAsync(args, CancellationToken.None);
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0,
                "FO4 FaceTint build failed.");
            using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(fo4Output.Value));
            Assert(artifact.RootElement.GetProperty("artifactKind").GetString() == "facetint-semantic-build" &&
                artifact.RootElement.GetProperty("edition").GetString() == "fallout4" &&
                artifact.RootElement.GetProperty("width").GetInt32() == 512 &&
                artifact.RootElement.GetProperty("format").GetString() == "bgra8" &&
                artifact.RootElement.GetProperty("orderedLayers").GetArrayLength() == 2 &&
                artifact.RootElement.GetProperty("orderedLayers")[0].GetProperty("name").GetString() == "complexion" &&
                artifact.RootElement.GetProperty("probes")[0].GetProperty("red").GetDouble() > 0.2d,
                "FO4 FaceTint semantic artifact omitted ordered raster contract.");

            response.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var repeatExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4",
                "--manifest", manifest.Value, "--npc", "0x00000800", "--output", fo4Repeat.Value, "--json"]), CancellationToken.None);
            Assert(repeatExit == CommandExitCode.Success &&
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fo4Output.Value))) ==
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fo4Repeat.Value))),
                "FaceTint semantic output was not deterministic.");

            response.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var ddsExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4",
                "--manifest", manifest.Value, "--npc", "0x00000800", "--output", Path.Combine(root.Value, "fo4-tint-dds.json"),
                "--dds-output", fo4Dds.Value, "--json"]), CancellationToken.None);
            var ddsBytes = await File.ReadAllBytesAsync(fo4Dds.Value);
            Assert(ddsExit == CommandExitCode.Success && ddsBytes.Length == 128 + 512 * 512 * 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.AsSpan(0, 4)) == 0x2053_4444 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.AsSpan(12, 4)) == 512 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ddsBytes.AsSpan(16, 4)) == 512,
                "FaceTint optional BGRA8 DDS output was not independently auditable.");

            response.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var bc3Exit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4",
                "--manifest", manifest.Value, "--format", "bc3", "--output", bc3Output.Value, "--json"]), CancellationToken.None);
            using var bc3 = JsonDocument.Parse(await File.ReadAllTextAsync(bc3Output.Value));
            Assert(bc3Exit == CommandExitCode.Success && bc3.RootElement.GetProperty("format").GetString() == "bc3",
                "Typed BC3 format metadata was not preserved.");

            response.GetStringBuilder().Clear();
            var bc3DdsExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4",
                "--manifest", manifest.Value, "--format", "bc3", "--output", Path.Combine(root.Value, "fo4-tint-bc3-dds.json"),
                "--dds-output", bc3Dds.Value, "--json"]), CancellationToken.None);
            var bc3DdsBytes = await File.ReadAllBytesAsync(bc3Dds.Value);
            Assert(bc3DdsExit == CommandExitCode.Success && bc3DdsBytes.Length > 128 + 20 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc3DdsBytes.AsSpan(84, 4)) == 0x3554_5844 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc3DdsBytes.AsSpan(12, 4)) == 512 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc3DdsBytes.AsSpan(16, 4)) == 512,
                "BC3 FaceTint DDS output was not independently auditable.");

            response.GetStringBuilder().Clear();
            var bc7DdsExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4",
                "--manifest", manifest.Value, "--format", "bc7", "--output", bc7Output.Value,
                "--dds-output", bc7Dds.Value, "--json"]), CancellationToken.None);
            var bc7DdsBytes = await File.ReadAllBytesAsync(bc7Dds.Value);
            Assert(bc7DdsExit == CommandExitCode.Success && bc7DdsBytes.Length > 148 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc7DdsBytes.AsSpan(84, 4)) == 0x3031_5844 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc7DdsBytes.AsSpan(128, 4)) == 98 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc7DdsBytes.AsSpan(12, 4)) == 512 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bc7DdsBytes.AsSpan(16, 4)) == 512,
                "BC7 FaceTint DDS output was not independently auditable.");

            var decoder = new TexconvFaceTintTextureDecoder(
                new WorkspacePath("K:\\ExampleWorkspace\\tools\\external\\directxtex-texconv-2026.5.7\\texconv.exe"),
                new WorkspacePath("K:\\ExampleWorkspace"),
                new Sha256Hash("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06"));
            var decoded = await decoder.DecodeAsync(bc3Dds, CancellationToken.None);
            var decodedBytes = decoded.Bytes ?? [];
            Assert(decoded.Decoded && decoded.Width == 512 && decoded.Height == 512 &&
                decodedBytes.Length == 512 * 512 * 4 &&
                decodedBytes.Any(value => value != 0) &&
                string.Equals(decoded.SourceSha256?.Value, Convert.ToHexString(SHA256.HashData(bc3DdsBytes)),
                    StringComparison.OrdinalIgnoreCase),
                $"FaceTint provider DDS decoder did not return a bound BGRA8 raster: decoded={decoded.Decoded}, width={decoded.Width}, height={decoded.Height}, size={decodedBytes.Length}, flags={BinaryPrimitives.ReadUInt32LittleEndian(decodedBytes.AsSpan(80, 4)):X8}, bits={BinaryPrimitives.ReadUInt32LittleEndian(decodedBytes.AsSpan(88, 4))}, rmask={BinaryPrimitives.ReadUInt32LittleEndian(decodedBytes.AsSpan(92, 4)):X8}, source={decoded.SourceSha256?.Value}, expected={Convert.ToHexString(SHA256.HashData(bc3DdsBytes))}, diagnostics={string.Join(";", decoded.Diagnostics.Select(item => item.Code + ":" + item.Message))}.");
            var wrongHashDecoder = new TexconvFaceTintTextureDecoder(
                new WorkspacePath("K:\\ExampleWorkspace\\tools\\external\\directxtex-texconv-2026.5.7\\texconv.exe"),
                new WorkspacePath("K:\\ExampleWorkspace"), new Sha256Hash(new string('0', 64)));
            var wrongHashDecode = await wrongHashDecoder.DecodeAsync(bc3Dds, CancellationToken.None);
            Assert(!wrongHashDecode.Decoded && wrongHashDecode.Diagnostics.Any(item =>
                    item.Code == "facetint-decoder-tool-hash-mismatch"),
                "FaceTint provider decoder accepted a mismatched tool hash.");

            var oversizedSource = new WorkspacePath(Path.Combine(root.Value, "oversized-provider.dds"));
            var oversizedBytes = bc3DdsBytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(oversizedBytes.AsSpan(12, 4), 0xFFFF_FFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(oversizedBytes.AsSpan(16, 4), 0xFFFF_FFFF);
            await File.WriteAllBytesAsync(oversizedSource.Value, oversizedBytes);
            var oversizedDecode = await decoder.DecodeAsync(oversizedSource, CancellationToken.None);
            Assert(!oversizedDecode.Decoded && oversizedDecode.Diagnostics.Any(item =>
                    item.Code == "facetint-decoder-source-dds-invalid"),
                "FaceTint provider decoder did not fail closed on oversized DDS dimensions.");

            var providerRoot = new WorkspacePath(Path.Combine(root.Value, "providers"));
            Directory.CreateDirectory(Path.Combine(providerRoot.Value, "textures"));
            File.Copy(bc3Dds.Value, Path.Combine(providerRoot.Value, "textures", "complexion.dds"));
            File.Copy(bc3Dds.Value, Path.Combine(providerRoot.Value, "textures", "detail.dds"));
            var sampledOutput = new WorkspacePath(Path.Combine(root.Value, "sampled.json"));
            var sampledDds = new WorkspacePath(Path.Combine(root.Value, "sampled.dds"));
            response.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var sampledExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "fallout4",
                "--manifest", manifest.Value, "--provider-root", providerRoot.Value, "--output", sampledOutput.Value,
                "--dds-output", sampledDds.Value, "--json"]), CancellationToken.None);
            using var sampledArtifact = JsonDocument.Parse(await File.ReadAllTextAsync(sampledOutput.Value));
            Assert(sampledExit == CommandExitCode.Success &&
                sampledArtifact.RootElement.GetProperty("rasterSource").GetString() == "provider-sampled" &&
                sampledArtifact.RootElement.GetProperty("providerSources").GetArrayLength() == 2 &&
                string.Equals(sampledArtifact.RootElement.GetProperty("textureDdsSha256").GetString(),
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sampledDds.Value))),
                    StringComparison.OrdinalIgnoreCase),
                "FaceTint provider-root sampling did not emit bound sampled-raster evidence.");

            var noEncoderOutput = new WorkspacePath(Path.Combine(root.Value, "no-encoder.json"));
            var noEncoderDds = new WorkspacePath(Path.Combine(root.Value, "no-encoder.dds"));
            var noEncoderPolicy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
                new WorkspacePath("F:\\ExampleGame"));
            var noEncoder = new FaceTintBuildService(noEncoderPolicy, new WorkspacePath("K:\\ExampleWorkspace"));
            var noEncoderResult = await noEncoder.BuildAsync(new FaceTintBuildRequest(GameEdition.Fallout4, manifest,
                noEncoderOutput, new FormId(0x800), null, FaceTintOutputFormat.Bc3, null,
                FaceTintAlphaMode.Preserve, noEncoderDds), CancellationToken.None);
            Assert(!noEncoderResult.Written && noEncoderResult.Diagnostics.Any(item =>
                    item.Code == "facetint-dds-codec-unavailable") && !File.Exists(noEncoderOutput.Value) &&
                !File.Exists(noEncoderDds.Value), "BC3 output did not fail closed without an admitted codec.");

            var wrongHashOutput = new WorkspacePath(Path.Combine(root.Value, "wrong-hash.json"));
            var wrongHashDds = new WorkspacePath(Path.Combine(root.Value, "wrong-hash.dds"));
            var wrongHashEncoder = new TexconvFaceTintTextureEncoder(
                new WorkspacePath("K:\\ExampleWorkspace\\tools\\external\\directxtex-texconv-2026.5.7\\texconv.exe"),
                new WorkspacePath("K:\\ExampleWorkspace"), new Sha256Hash(new string('0', 64)));
            var wrongHashService = new FaceTintBuildService(noEncoderPolicy, new WorkspacePath("K:\\ExampleWorkspace"),
                wrongHashEncoder);
            var wrongHashResult = await wrongHashService.BuildAsync(new FaceTintBuildRequest(GameEdition.Fallout4, manifest,
                wrongHashOutput, new FormId(0x800), null, FaceTintOutputFormat.Bc3, null,
                FaceTintAlphaMode.Preserve, wrongHashDds), CancellationToken.None);
            Assert(!wrongHashResult.Written && wrongHashResult.Diagnostics.Any(item =>
                    item.Code == "facetint-codec-tool-hash-mismatch") && !File.Exists(wrongHashOutput.Value) &&
                !File.Exists(wrongHashDds.Value), "FaceTint encoder accepted a mismatched tool hash.");

            response.GetStringBuilder().Clear();
            var sseExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-tint", "--edition", "skyrimse",
                "--manifest", manifest.Value, "--output", sseOutput.Value, "--alpha", "opaque", "--json"]), CancellationToken.None);
            using var sse = JsonDocument.Parse(response.ToString());
            Assert(sseExit == CommandExitCode.Success && sse.RootElement.GetProperty("edition").GetString() == "skyrimse" &&
                JsonDocument.Parse(await File.ReadAllTextAsync(sseOutput.Value)).RootElement.GetProperty("alphaMode").GetString() == "opaque",
                "SSE FaceTint semantic artifact did not honor the game and alpha settings.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestSseFaceGenDiagnosis()
    {
        var (runner, output, _) = CreateRunner();
        var manifest = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\sse-facegen-valid.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "diagnose", "--edition", "skyrimse", "--manifest", manifest, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("edition").GetString() == "skyrimse", "SSE FaceGen diagnosis failed.");
    }

    private static async Task TestFaceGenCorrections()
    {
        var root = NewPresetOutput("facegen-correction-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var fo4Manifest = new WorkspacePath(Path.Combine(root.Value, "fo4.json"));
        var fo4Output = new WorkspacePath(Path.Combine(root.Value, "fo4-corrections.json"));
        var sseManifest = new WorkspacePath(Path.Combine(root.Value, "sse.json"));
        var sseOutput = new WorkspacePath(Path.Combine(root.Value, "sse-corrections.json"));
        var fo4Fixture = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\fo4-facegen-valid.json";
        var sseFixture = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\sse-facegen-valid.json";
        var fo4Text = (await File.ReadAllTextAsync(fo4Fixture)).Trim();
        fo4Text = fo4Text[..^1] + ",\"corrections\":[{\"kind\":\"ghoul-head-rear\",\"trigger\":true,\"before\":\"default\",\"after\":\"ghoul-fixed\"},{\"kind\":\"eyebrows-fixed-color\",\"trigger\":false,\"before\":\"palette\",\"after\":\"fixed\"}]}";
        await File.WriteAllTextAsync(fo4Manifest.Value, fo4Text);
        var sseText = (await File.ReadAllTextAsync(sseFixture)).Trim();
        sseText = sseText[..^1] + ",\"corrections\":[{\"kind\":\"sse-neutral-detail\",\"trigger\":true,\"before\":\"detail.dds\",\"after\":\"neutral.dds\"}]}";
        await File.WriteAllTextAsync(sseManifest.Value, sseText);
        try
        {
            var (runner, output, error) = CreateRunner();
            var fo4Exit = await runner.RunAsync(CommandLine.Parse(["facegen", "build", "--edition", "fallout4", "--manifest", fo4Manifest.Value,
                "--npc", "0x00000800", "--corrections", "auto", "--output", fo4Output.Value, "--json"]), CancellationToken.None);
            using var fo4 = JsonDocument.Parse(await File.ReadAllTextAsync(fo4Output.Value));
            Assert(fo4Exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                fo4.RootElement.GetProperty("corrections").GetArrayLength() == 2 &&
                fo4.RootElement.GetProperty("corrections")[0].GetProperty("triggered").GetBoolean() &&
                fo4.RootElement.GetProperty("corrections")[0].GetProperty("decision").GetString() == "applied" &&
                !fo4.RootElement.GetProperty("corrections")[1].GetProperty("triggered").GetBoolean() &&
                fo4.RootElement.GetProperty("corrections")[1].GetProperty("before").GetString() ==
                fo4.RootElement.GetProperty("corrections")[1].GetProperty("after").GetString(),
                "FO4 correction decisions did not preserve trigger semantics.");

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var sseExit = await runner.RunAsync(CommandLine.Parse(["facegen", "build", "--edition", "skyrimse", "--manifest", sseManifest.Value,
                "--corrections", "auto", "--output", sseOutput.Value, "--json"]), CancellationToken.None);
            using var sse = JsonDocument.Parse(await File.ReadAllTextAsync(sseOutput.Value));
            Assert(sseExit == CommandExitCode.Success && sse.RootElement.GetProperty("edition").GetString() == "skyrimse" &&
                sse.RootElement.GetProperty("corrections")[0].GetProperty("decision").GetString() == "applied",
                "SSE correction was not applied.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceGenBatch()
    {
        var root = NewPresetOutput("facegen-batch-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var batch = new WorkspacePath(Path.Combine(root.Value, "batch.json"));
        var output = new WorkspacePath(Path.Combine(root.Value, "batch-output.json"));
        var valid = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\fo4-facegen-valid.json";
        var skipped = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\zero-shapes.json";
        var missing = Path.Combine(root.Value, "missing.json");
        await File.WriteAllTextAsync(batch.Value, $"{{\"schemaVersion\":1,\"edition\":\"fallout4\",\"manifests\":[\"{valid.Replace("\\", "\\\\")}\",\"{skipped.Replace("\\", "\\\\")}\",\"{missing.Replace("\\", "\\\\")}\"]}}");
        try
        {
            var (runner, response, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "bake-all", "--edition", "fallout4",
                "--manifests", batch.Value, "--output", output.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            Assert(exit == CommandExitCode.ValidationFailure && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("attempted").GetInt32() == 3 &&
                document.RootElement.GetProperty("passed").GetInt32() == 1 &&
                document.RootElement.GetProperty("skipped").GetInt32() == 1 &&
                document.RootElement.GetProperty("failed").GetInt32() == 1 &&
                JsonDocument.Parse(response.ToString()).RootElement.GetProperty("hasFailures").GetBoolean(),
                "FaceGen batch did not report passed/skipped/failed entries or failure exit.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceGenPluginTarget()
    {
        var root = NewPresetOutput("facegen-plugin-target-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var targetManifest = new WorkspacePath(Path.Combine(root.Value, "target.json"));
        var output = new WorkspacePath(Path.Combine(root.Value, "target-output.json"));
        var valid = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\fo4-facegen-valid.json";
        var skipped = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\zero-shapes.json";
        var missing = Path.Combine(root.Value, "missing.json");
        var other = Path.Combine(root.Value, "excluded.json");
        var skippedCopy = Path.Combine(root.Value, "zero-shapes-802.json");
        var skippedText = await File.ReadAllTextAsync(skipped);
        await File.WriteAllTextAsync(skippedCopy, skippedText.Replace("0x00000800", "0x00000802", StringComparison.Ordinal));
        await File.WriteAllTextAsync(targetManifest.Value,
            $"{{\"schemaVersion\":1,\"edition\":\"fallout4\",\"targetPlugin\":\"Target.esp\",\"entries\":[" +
            $"{{\"winningPlugin\":\"Target.esp\",\"npcFormId\":\"0x800\",\"manifestPath\":\"{valid.Replace("\\", "\\\\")}\"}}," +
            $"{{\"winningPlugin\":\"Other.esp\",\"npcFormId\":\"0x801\",\"manifestPath\":\"{other.Replace("\\", "\\\\")}\"}}," +
            $"{{\"winningPlugin\":\"Target.esp\",\"npcFormId\":\"0x802\",\"manifestPath\":\"{skippedCopy.Replace("\\", "\\\\")}\"}}," +
            $"{{\"winningPlugin\":\"Target.esp\",\"npcFormId\":\"0x803\",\"manifestPath\":\"{missing.Replace("\\", "\\\\")}\"}}]}}");
        try
        {
            var (runner, response, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "build-plugin", "--edition", "fallout4",
                "--manifest", targetManifest.Value, "--plugin", "Target.esp", "--output", output.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            var entries = document.RootElement.GetProperty("entries");
            Assert(exit == CommandExitCode.ValidationFailure && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("targetPlugin").GetString() == "Target.esp" &&
                document.RootElement.GetProperty("attempted").GetInt32() == 3 &&
                document.RootElement.GetProperty("selected").GetInt32() == 3 &&
                document.RootElement.GetProperty("excluded").GetInt32() == 1 &&
                document.RootElement.GetProperty("passed").GetInt32() == 1 &&
                document.RootElement.GetProperty("skipped").GetInt32() == 1 &&
                document.RootElement.GetProperty("failed").GetInt32() == 1 &&
                entries[0].GetProperty("outputRelativePath").GetString() == "FaceGen/Target/0x00000800.json" &&
                entries[1].GetProperty("outputRelativePath").ValueKind == JsonValueKind.Null &&
                entries[1].GetProperty("disposition").GetString() == "excluded" &&
                JsonDocument.Parse(response.ToString()).RootElement.GetProperty("hasFailures").GetBoolean(),
                "FaceGen plugin-target output leaked excluded entries or hid a selected failure.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewScene()
    {
        var root = NewPresetOutput("preview-scene-root");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var output = new WorkspacePath(Path.Combine(root.Value, "scene-a.json"));
        var repeat = new WorkspacePath(Path.Combine(root.Value, "scene-b.json"));
        var visibleOutput = new WorkspacePath(Path.Combine(root.Value, "scene-visible.json"));
        var aliasOutput = new WorkspacePath(Path.Combine(root.Value, "scene-alias.json"));
        var hash = new string('a', 64);
        await File.WriteAllTextAsync(manifest.Value, $"{{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x800\",\"assets\":[" +
            $"{{\"category\":\"face\",\"path\":\"meshes/face.nif\",\"provider\":\"loose:face.nif\",\"sha256\":\"{hash}\"}}," +
            $"{{\"category\":\"body\",\"path\":\"meshes/body.nif\",\"provider\":\"archive:body.ba2\",\"sha256\":\"{hash}\"}}," +
            $"{{\"category\":\"hair\",\"path\":\"meshes/hair.nif\",\"provider\":\"loose:hair.nif\",\"sha256\":\"{hash}\"}}," +
            $"{{\"category\":\"outfit\",\"path\":\"meshes/outfit.nif\",\"provider\":\"loose:outfit.nif\",\"sha256\":\"{hash}\"}}," +
            $"{{\"category\":\"accessory\",\"path\":\"meshes/amulet.nif\",\"provider\":\"archive:armor.ba2\",\"sha256\":\"{hash}\"}}],\"morphs\":[" +
            "{\"category\":\"bone\",\"name\":\"jaw\",\"value\":0.25},{\"category\":\"sculpt\",\"name\":\"nose\",\"value\":-0.5}]}");
        try
        {
            var (runner, response, error) = CreateRunner();
            var first = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--output", output.Value, "--json"]), CancellationToken.None);
            using var firstResponse = JsonDocument.Parse(response.ToString());
            response.GetStringBuilder().Clear();
            var aliasExit = await runner.RunAsync(CommandLine.Parse(["render", "npc", "--edition", "fallout4",
                "--manifest", manifest.Value, "--output", aliasOutput.Value, "--json"]), CancellationToken.None);
            using var aliasResponse = JsonDocument.Parse(response.ToString());
            response.GetStringBuilder().Clear();
            var second = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--output", repeat.Value, "--json"]), CancellationToken.None);
            var visibleExit = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--visible", "face,hair", "--output", visibleOutput.Value, "--json"]), CancellationToken.None);
            using var firstDocument = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            using var secondDocument = JsonDocument.Parse(await File.ReadAllTextAsync(repeat.Value));
            using var visibleDocument = JsonDocument.Parse(await File.ReadAllTextAsync(visibleOutput.Value));
            Assert(first == CommandExitCode.Success && aliasExit == CommandExitCode.Success && second == CommandExitCode.Success && visibleExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                firstDocument.RootElement.GetProperty("artifactKind").GetString() == "preview-scene-semantic-build" &&
                aliasResponse.RootElement.GetProperty("artifactKind").GetString() == "preview-scene-semantic-build" &&
                firstDocument.RootElement.GetProperty("assetCount").GetInt32() == 5 &&
                firstDocument.RootElement.GetProperty("visibleAssetCount").GetInt32() == 5 &&
                firstDocument.RootElement.GetProperty("appliedMorphCount").GetInt32() == 2 &&
                firstDocument.RootElement.GetProperty("categoryCounts").GetArrayLength() == 5 &&
                firstDocument.RootElement.GetProperty("assets")[0].GetProperty("category").GetString() == "face" &&
                firstDocument.RootElement.GetProperty("sceneSha256").GetString() ==
                secondDocument.RootElement.GetProperty("sceneSha256").GetString() &&
                visibleDocument.RootElement.GetProperty("visibleAssetCount").GetInt32() == 2 &&
                visibleDocument.RootElement.GetProperty("appliedMorphCount").GetInt32() == 2 &&
                visibleDocument.RootElement.GetProperty("assets")[1].GetProperty("visible").GetBoolean() == false &&
                firstResponse.RootElement.GetProperty("written").GetBoolean(),
                "Preview scene evidence did not preserve deterministic ordered providers.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewSceneVariants()
    {
        var root = NewPresetOutput("preview-scene-variants");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var output = new WorkspacePath(Path.Combine(root.Value, "variant.json"));
        var mismatchOutput = new WorkspacePath(Path.Combine(root.Value, "mismatch.json"));
        var hash = new string('b', 64);
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x800\",\"assets\":[" +
            $"{{\"category\":\"face\",\"path\":\"meshes/face.nif\",\"provider\":\"loose:face\",\"sha256\":\"{hash}\"}}," +
            $"{{\"category\":\"outfit\",\"path\":\"meshes/outfit.nif\",\"provider\":\"loose:outfit\",\"sha256\":\"{hash}\"}}," +
            $"{{\"category\":\"accessory\",\"path\":\"meshes/ring.nif\",\"provider\":\"loose:ring\",\"sha256\":\"{hash}\"}}]," +
            "\"morphs\":[{\"category\":\"bone\",\"name\":\"jaw\",\"value\":0.25},{\"category\":\"sculpt\",\"name\":\"nose\",\"value\":-0.5}]," +
            "\"variants\":[{\"id\":\"battle\",\"outfit\":\"M2FixtureFO4.esp|0x801\",\"assets\":[\"meshes/outfit.nif\",\"meshes/ring.nif\"]," +
            "\"morphs\":[{\"category\":\"bone\",\"name\":\"jaw\",\"value\":0.75}]}]}");
        try
        {
            var (runner, response, error) = CreateRunner();
            var selected = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--outfit", "M2FixtureFO4.esp|0x801", "--variant", "battle",
                "--output", output.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output.Value));
            var assets = document.RootElement.GetProperty("assets");
            var morphs = document.RootElement.GetProperty("morphs");
            Assert(selected == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("includedAssetCount").GetInt32() == 2 &&
                document.RootElement.GetProperty("visibleAssetCount").GetInt32() == 2 &&
                document.RootElement.GetProperty("appliedMorphCount").GetInt32() == 1 &&
                document.RootElement.GetProperty("variant").GetProperty("id").GetString() == "battle" &&
                document.RootElement.GetProperty("variant").GetProperty("outfit").GetString() == "M2FixtureFO4.esp|0x00000801" &&
                assets[0].GetProperty("included").GetBoolean() == false && assets[1].GetProperty("included").GetBoolean() &&
                morphs[0].GetProperty("included").GetBoolean() &&
                Math.Abs(morphs[0].GetProperty("value").GetSingle() - 0.75F) < 0.0001F &&
                morphs[1].GetProperty("included").GetBoolean() == false,
                "Preview variant did not bind the outfit or record included/excluded pieces.");

            response.GetStringBuilder().Clear();
            var partial = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--outfit", "M2FixtureFO4.esp|0x801", "--output",
                Path.Combine(root.Value, "partial.json"), "--json"]), CancellationToken.None);
            Assert(partial == CommandExitCode.UsageError && !File.Exists(Path.Combine(root.Value, "partial.json")),
                "Partial preview variant selection was accepted.");

            response.GetStringBuilder().Clear();
            var mismatch = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--outfit", "M2FixtureFO4.esp|0x802", "--variant", "battle",
                "--output", mismatchOutput.Value, "--json"]), CancellationToken.None);
            Assert(mismatch == CommandExitCode.ValidationFailure && !File.Exists(mismatchOutput.Value),
                "Mismatched preview variant outfit was accepted.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewReroll()
    {
        var root = NewPresetOutput("preview-reroll");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var first = new WorkspacePath(Path.Combine(root.Value, "reroll-a.json"));
        var second = new WorkspacePath(Path.Combine(root.Value, "reroll-b.json"));
        var hash = new string('c', 64);
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x900\",\"assets\":[" +
            $"{{\"category\":\"outfit\",\"path\":\"meshes/outfit.nif\",\"provider\":\"fixture\",\"sha256\":\"{hash}\"}}]," +
            "\"variants\":[" +
            "{\"id\":\"a\",\"outfit\":\"M2FixtureFO4.esp|0x801\",\"assets\":[\"meshes/outfit.nif\"]}," +
            "{\"id\":\"b\",\"outfit\":\"M2FixtureFO4.esp|0x802\",\"assets\":[\"meshes/outfit.nif\"]}," +
            "{\"id\":\"c\",\"outfit\":\"M2FixtureFO4.esp|0x803\",\"assets\":[\"meshes/outfit.nif\"]}]}");
        try
        {
            var (runner, output, error) = CreateRunner();
            var firstExit = await runner.RunAsync(CommandLine.Parse(["preview", "reroll", "--edition", "fallout4",
                "--manifest", manifest.Value, "--npc", "0x900", "--seed", "42", "--output", first.Value, "--json"]), CancellationToken.None);
            output.GetStringBuilder().Clear();
            var secondExit = await runner.RunAsync(CommandLine.Parse(["preview", "reroll", "--edition", "fallout4",
                "--manifest", manifest.Value, "--npc", "0x900", "--seed", "42", "--output", second.Value, "--json"]), CancellationToken.None);
            using var firstDocument = JsonDocument.Parse(await File.ReadAllTextAsync(first.Value));
            using var secondDocument = JsonDocument.Parse(await File.ReadAllTextAsync(second.Value));
            Assert(firstExit == CommandExitCode.Success && secondExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                firstDocument.RootElement.GetProperty("artifactKind").GetString() == "preview-reroll-semantic-build" &&
                firstDocument.RootElement.GetProperty("seed").GetInt64() == 42 &&
                firstDocument.RootElement.GetProperty("npcFormId").GetString() == "0x00000900" &&
                firstDocument.RootElement.GetProperty("candidateCount").GetInt32() == 3 &&
                firstDocument.RootElement.GetProperty("eligibleCount").GetInt32() == 3 &&
                firstDocument.RootElement.GetProperty("selectedVariantId").GetString() ==
                    secondDocument.RootElement.GetProperty("selectedVariantId").GetString() &&
                firstDocument.RootElement.GetProperty("selectedIndex").GetInt32() ==
                    secondDocument.RootElement.GetProperty("selectedIndex").GetInt32(),
                "Preview reroll did not produce a seed-stable validated choice.");

            var wrongNpc = await runner.RunAsync(CommandLine.Parse(["preview", "reroll", "--edition", "fallout4",
                "--manifest", manifest.Value, "--npc", "0x901", "--seed", "42", "--output",
                Path.Combine(root.Value, "wrong-npc.json"), "--json"]), CancellationToken.None);
            Assert(wrongNpc == CommandExitCode.ValidationFailure, "Preview reroll accepted an NPC mismatch.");

            var badSeed = await runner.RunAsync(CommandLine.Parse(["preview", "reroll", "--edition", "fallout4",
                "--manifest", manifest.Value, "--npc", "0x900", "--seed", "not-a-seed", "--output",
                Path.Combine(root.Value, "bad-seed.json"), "--json"]), CancellationToken.None);
            Assert(badSeed == CommandExitCode.UsageError, "Preview reroll accepted a malformed seed.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewPresets()
    {
        var root = NewPresetOutput("preview-presets");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var first = new WorkspacePath(Path.Combine(root.Value, "first.json"));
        var second = new WorkspacePath(Path.Combine(root.Value, "second.json"));
        var hash = new string('d', 64);
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x901\",\"assets\":[" +
            $"{{\"category\":\"face\",\"path\":\"meshes/face.nif\",\"provider\":\"fixture\",\"sha256\":\"{hash}\"}}]," +
            "\"cameraPresets\":[{\"id\":\"portrait\",\"version\":3,\"yaw\":15,\"pitch\":-4,\"distance\":5.5,\"fov\":42}]," +
            "\"lightingPresets\":[{\"id\":\"studio\",\"version\":2,\"ambient\":0.35,\"lights\":[" +
            "{\"id\":\"key\",\"azimuth\":-30,\"elevation\":25,\"intensity\":1.2,\"red\":1,\"green\":0.9,\"blue\":0.8}]}]}");
        try
        {
            var (runner, output, error) = CreateRunner();
            var firstExit = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--camera", "portrait", "--lighting", "studio",
                "--output", first.Value, "--json"]), CancellationToken.None);
            output.GetStringBuilder().Clear();
            var secondExit = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--camera", "portrait", "--lighting", "studio",
                "--output", second.Value, "--json"]), CancellationToken.None);
            using var firstDocument = JsonDocument.Parse(await File.ReadAllTextAsync(first.Value));
            using var secondDocument = JsonDocument.Parse(await File.ReadAllTextAsync(second.Value));
            Assert(firstExit == CommandExitCode.Success && secondExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                firstDocument.RootElement.GetProperty("camera").GetProperty("id").GetString() == "portrait" &&
                firstDocument.RootElement.GetProperty("camera").GetProperty("version").GetInt32() == 3 &&
                firstDocument.RootElement.GetProperty("lighting").GetProperty("id").GetString() == "studio" &&
                firstDocument.RootElement.GetProperty("lighting").GetProperty("version").GetInt32() == 2 &&
                firstDocument.RootElement.GetProperty("sceneSha256").GetString() ==
                    secondDocument.RootElement.GetProperty("sceneSha256").GetString(),
                "Preview camera/lighting presets were not recorded deterministically.");

            var unknown = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--camera", "missing", "--output",
                Path.Combine(root.Value, "unknown.json"), "--json"]), CancellationToken.None);
            Assert(unknown == CommandExitCode.ValidationFailure && !File.Exists(Path.Combine(root.Value, "unknown.json")),
                "Unknown preview camera preset was accepted.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewAnimation()
    {
        var root = NewPresetOutput("preview-animation");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var frameOutput = new WorkspacePath(Path.Combine(root.Value, "frame.json"));
        var timeOutput = new WorkspacePath(Path.Combine(root.Value, "time.json"));
        var hash = new string('e', 64);
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x902\",\"assets\":[" +
            $"{{\"category\":\"body\",\"path\":\"meshes/body.nif\",\"provider\":\"fixture\",\"sha256\":\"{hash}\"}}]," +
            "\"animations\":[{\"id\":\"walk\",\"path\":\"animations/walk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":10,\"fps\":30,\"additive\":false}]}");
        try
        {
            var (runner, output, error) = CreateRunner();
            var frameExit = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--animation", "walk", "--frame", "2", "--fps", "24", "--play",
                "--output", frameOutput.Value, "--json"]), CancellationToken.None);
            using var frameDocument = JsonDocument.Parse(await File.ReadAllTextAsync(frameOutput.Value));
            Assert(frameExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                frameDocument.RootElement.GetProperty("animation").GetProperty("id").GetString() == "walk" &&
                frameDocument.RootElement.GetProperty("animation").GetProperty("frame").GetInt32() == 2 &&
                Math.Abs(frameDocument.RootElement.GetProperty("animation").GetProperty("timeSeconds").GetSingle() - (2F / 30F)) < 0.0001F &&
                frameDocument.RootElement.GetProperty("animation").GetProperty("skeleton").GetString() == "meshes/skeleton.nif" &&
                frameDocument.RootElement.GetProperty("animation").GetProperty("playbackRate").GetSingle() == 24 &&
                frameDocument.RootElement.GetProperty("animation").GetProperty("playing").GetBoolean(),
                "Preview animation frame selection did not preserve typed playback metadata.");

            output.GetStringBuilder().Clear();
            var timeExit = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--animation", "walk", "--time", "0.1",
                "--output", timeOutput.Value, "--json"]), CancellationToken.None);
            using var timeDocument = JsonDocument.Parse(await File.ReadAllTextAsync(timeOutput.Value));
            Assert(timeExit == CommandExitCode.Success && timeDocument.RootElement.GetProperty("animation").GetProperty("frame").GetInt32() == 3 &&
                timeDocument.RootElement.GetProperty("animation").GetProperty("playing").GetBoolean() == false,
                "Preview animation time selection did not resolve a deterministic frame.");

            var missingPosition = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--animation", "walk", "--output",
                Path.Combine(root.Value, "missing-position.json"), "--json"]), CancellationToken.None);
            Assert(missingPosition == CommandExitCode.UsageError, "Animation without frame or time was accepted.");

            var outOfRange = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--animation", "walk", "--frame", "10", "--output",
                Path.Combine(root.Value, "out-of-range.json"), "--json"]), CancellationToken.None);
            Assert(outOfRange == CommandExitCode.ValidationFailure && !File.Exists(Path.Combine(root.Value, "out-of-range.json")),
                "Out-of-range animation frame was accepted.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewHairZap()
    {
        var root = NewPresetOutput("preview-hair-zap");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var sseOutput = new WorkspacePath(Path.Combine(root.Value, "sse.json"));
        var disabledOutput = new WorkspacePath(Path.Combine(root.Value, "disabled.json"));
        var fo4Output = new WorkspacePath(Path.Combine(root.Value, "fo4.json"));
        var fo4FaceCullOutput = new WorkspacePath(Path.Combine(root.Value, "fo4-face-cull.json"));
        var fo4Manifest = new WorkspacePath(Path.Combine(root.Value, "fo4-scene.json"));
        var hash = new string('f', 64);
        await File.WriteAllTextAsync(manifest.Value,
            $"{{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"npcFormId\":\"0x902\",\"assets\":[{{\"category\":\"hair\",\"path\":\"meshes/hair.nif\",\"provider\":\"fixture\",\"sha256\":\"{hash}\"}}]}}");
        await File.WriteAllTextAsync(fo4Manifest.Value,
            (await File.ReadAllTextAsync(manifest.Value)).Replace("skyrimse", "fallout4", StringComparison.Ordinal));
        try
        {
            var (runner, output, error) = CreateRunner();
            var sse = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "skyrimse",
                "--manifest", manifest.Value, "--render-headwear", "true", "--hair-slots", "31,41",
                "--output", sseOutput.Value, "--json"]), CancellationToken.None);
            using var sseDocument = JsonDocument.Parse(await File.ReadAllTextAsync(sseOutput.Value));
            var zap = sseDocument.RootElement.GetProperty("hairZap");
            Assert(sse == CommandExitCode.Success && error.ToString().Length == 0 && zap.GetProperty("renderHeadwear").GetBoolean() &&
                zap.GetProperty("topCovered").GetBoolean() && zap.GetProperty("longCovered").GetBoolean() &&
                zap.GetProperty("coveredSlots").GetArrayLength() == 2,
                "Skyrim hair partition coverage was not preserved in the typed preview artifact.");

            output.GetStringBuilder().Clear();
            var disabled = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "skyrimse",
                "--manifest", manifest.Value, "--render-headwear", "false", "--hair-slots", "31",
                "--output", disabledOutput.Value, "--json"]), CancellationToken.None);
            using var disabledDocument = JsonDocument.Parse(await File.ReadAllTextAsync(disabledOutput.Value));
            var disabledZap = disabledDocument.RootElement.GetProperty("hairZap");
            Assert(disabled == CommandExitCode.Success && !disabledZap.GetProperty("topCovered").GetBoolean() &&
                !disabledZap.GetProperty("longCovered").GetBoolean(),
                "Disabling headwear rendering did not clear the hair zap mask.");

            var missingToggle = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "skyrimse",
                "--manifest", manifest.Value, "--hair-slots", "31", "--output",
                Path.Combine(root.Value, "missing-toggle.json"), "--json"]), CancellationToken.None);
            Assert(missingToggle == CommandExitCode.UsageError, "Hair slots without the headwear toggle were accepted.");

            var fo4 = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", fo4Manifest.Value, "--render-headwear", "true", "--hair-slots", "41",
                "--output", fo4Output.Value, "--json"]), CancellationToken.None);
            Assert(fo4 == CommandExitCode.ValidationFailure && !File.Exists(fo4Output.Value) &&
                output.ToString().Contains("preview-hair-zap-slot-invalid", StringComparison.Ordinal),
                "A Skyrim-only hair slot was accepted under the Fallout 4 contract.");

            output.GetStringBuilder().Clear();
            var fo4FaceCull = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", fo4Manifest.Value, "--render-headwear", "true", "--hair-slots", "32",
                "--output", fo4FaceCullOutput.Value, "--json"]), CancellationToken.None);
            using var fo4FaceCullDocument = JsonDocument.Parse(await File.ReadAllTextAsync(fo4FaceCullOutput.Value));
            var faceCullZap = fo4FaceCullDocument.RootElement.GetProperty("hairZap");
            Assert(fo4FaceCull == CommandExitCode.Success && faceCullZap.GetProperty("faceGenHeadCovered").GetBoolean() &&
                !faceCullZap.GetProperty("topCovered").GetBoolean() && !faceCullZap.GetProperty("longCovered").GetBoolean(),
                "FO4 slot 32 did not resolve as the typed FaceGen head cull without zapping hair partitions.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewNifExport()
    {
        var root = NewPresetOutput("preview-nif-export");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "scene-manifest.json"));
        var scene = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        var plan = new WorkspacePath(Path.Combine(root.Value, "npc.nif.plan.json"));
        var hash = new string('f', 64);
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"npcFormId\":\"0x903\",\"assets\":[" +
            $"{{\"category\":\"face\",\"path\":\"meshes/face.nif\",\"provider\":\"fixture\",\"sha256\":\"{hash}\"}}]}}");
        try
        {
            var (runner, output, error) = CreateRunner();
            var sceneExit = await runner.RunAsync(CommandLine.Parse(["preview", "render", "--edition", "fallout4",
                "--manifest", manifest.Value, "--output", scene.Value, "--json"]), CancellationToken.None);
            output.GetStringBuilder().Clear();
            var exportExit = await runner.RunAsync(CommandLine.Parse(["preview", "export-nif", "--edition", "fallout4",
                "--scene", scene.Value, "--output", plan.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(plan.Value));
            Assert(sceneExit == CommandExitCode.Success && exportExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "preview-nif-export-plan" &&
                document.RootElement.GetProperty("targetFormat").GetString() == "nif" &&
                document.RootElement.GetProperty("assets").GetArrayLength() == 1 &&
                document.RootElement.GetProperty("assets")[0].GetProperty("path").GetString() == "meshes/face.nif",
                "Preview NIF export did not write a bounded scene plan.");

            var binaryLike = await runner.RunAsync(CommandLine.Parse(["preview", "export-nif", "--edition", "fallout4",
                "--scene", scene.Value, "--output", Path.Combine(root.Value, "npc.nif"), "--json"]), CancellationToken.None);
            Assert(binaryLike == CommandExitCode.ValidationFailure && !File.Exists(Path.Combine(root.Value, "npc.nif")),
                "Preview NIF export accepted a game-facing binary destination.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestOutfitList()
    {
        var root = NewPresetOutput("outfit-list");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var basePlugin = new PluginName("Base.esp");
        var overridePlugin = new PluginName("Override.esp");
        File.WriteAllBytes(Path.Combine(root.Value, basePlugin.Value), []);
        File.WriteAllBytes(Path.Combine(root.Value, overridePlugin.Value), []);
        var baseRecord = new PluginRecordSummary(new FormId(0x100), "OTFT", "GuardOutfit", "Guard Outfit",
            false, false, null, null, [new FormId(0x200)], OwnerPlugin: basePlugin,
            OutfitItemReferences: [new FormReference(basePlugin, new FormId(0x200))]);
        var qualifiedItems = new[]
        {
            new FormReference(basePlugin, new FormId(0x201)),
            new FormReference(overridePlugin, new FormId(0x202))
        }.ToImmutableArray();
        var overrideRecord = baseRecord with
        {
            Name = "Guard Outfit Override",
            OutfitItems = qualifiedItems.Select(item => item.FormId).ToImmutableArray(),
            OutfitItemReferences = qualifiedItems
        };
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new OutfitChoiceService(new FakePluginReader(
                new PluginInspection(GameEdition.Fallout4, basePlugin, [], [baseRecord], []),
                new PluginInspection(GameEdition.Fallout4, overridePlugin, [], [overrideRecord], [])),
                policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var result = await service.SearchAsync(new OutfitChoiceSearchRequest(GameEdition.Fallout4,
                root, "guard", [basePlugin, overridePlugin]), CancellationToken.None);
            var candidate = result.Candidates.Single();
            Assert(candidate.Name == "Guard Outfit Override" && candidate.Items.SequenceEqual([new FormId(0x201), new FormId(0x202)]) &&
                candidate.ItemReferences.SequenceEqual(qualifiedItems) &&
                candidate.Provenance.Kind == OutfitChoiceProvenanceKind.Override &&
                candidate.Provenance.OverrideChain.SequenceEqual([basePlugin, overridePlugin]),
                "Outfit list did not preserve item links and explicit winning override provenance.");

            var (runner, output, error) = CreateRunner(new FakeOutfitChoiceService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["outfit", "list", "--edition", "fallout4",
                "--data-root", root.Value, "--plugins", "Base.esp,Override.esp", "--search", "guard", "--json"]),
                CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("candidates").GetArrayLength() == 1 &&
                document.RootElement.GetProperty("candidates")[0].GetProperty("items").GetArrayLength() == 2 &&
                document.RootElement.GetProperty("candidates")[0].GetProperty("itemReferences")[1].GetString() ==
                    "Override.esp|0x00000202" &&
                document.RootElement.GetProperty("candidates")[0].GetProperty("provenanceKind").GetString() == "Override",
                "Outfit list CLI did not expose typed candidate metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestOutfitProposal()
    {
        var root = NewPresetOutput("outfit-proposal");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [1, 2, 3]);
        var sourceRecord = new PluginRecordSummary(new FormId(0x801), "OTFT", "SourceOutfit", null,
            false, false, null, null, []);
        var master = new PluginName("Fallout4.esm");
        File.WriteAllBytes(Path.Combine(root.Value, master.Value), []);
        var sourceArmor = new PluginRecordSummary(new FormId(0x900), "ARMO", "SourceArmor", null,
            false, false, OwnerPlugin: plugin);
        var masterArmor = new PluginRecordSummary(new FormId(0x901), "ARMO", "MasterArmor", null,
            false, false, OwnerPlugin: master);
        var invalidItem = new PluginRecordSummary(new FormId(0x902), "NPC_", "NotArmor", null,
            true, false, OwnerPlugin: plugin);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new OutfitProposalService(new FakePluginReader(
                new PluginInspection(GameEdition.Fallout4, plugin, [master],
                    [sourceRecord, sourceArmor, invalidItem], []),
                new PluginInspection(GameEdition.Fallout4, master, [], [masterArmor], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var createPath = new WorkspacePath(Path.Combine(root.Value, "create.outfit-proposal.json"));
            var create = await service.ProposeAsync(new OutfitProposalRequest(GameEdition.Fallout4, source,
                sourceRecord.FormId, OutfitProposalMode.New, new EditorId("NewOutfit"),
                [new FormReference(plugin, new FormId(0x900)), new FormReference(master, new FormId(0x901))], createPath,
                new FormId(0x804)),
                CancellationToken.None);
            Assert(create.Written && create.Artifact?.NoUnrelatedRecords == true &&
                create.Artifact.EditorId == "NewOutfit" && create.Artifact.MasterDependencies.SequenceEqual([master.Value]) &&
                File.Exists(createPath.Value), "Create outfit proposal was not hash-bound and master-aware.");

            var overridePath = new WorkspacePath(Path.Combine(root.Value, "override.outfit-proposal.json"));
            var overrideResult = await service.ProposeAsync(new OutfitProposalRequest(GameEdition.Fallout4, source,
                sourceRecord.FormId, OutfitProposalMode.Override, null,
                [new FormReference(plugin, new FormId(0x900))], overridePath), CancellationToken.None);
            Assert(overrideResult.Written && overrideResult.Artifact?.EditorId == "SourceOutfit",
                "Override outfit proposal did not inherit the source EditorID.");
            var invalidMode = await service.ProposeAsync(new OutfitProposalRequest(GameEdition.Fallout4, source,
                sourceRecord.FormId, (OutfitProposalMode)99, null,
                [new FormReference(plugin, new FormId(0x900))],
                new WorkspacePath(Path.Combine(root.Value, "invalid-mode.outfit-proposal.json"))), CancellationToken.None);
            Assert(!invalidMode.Written && invalidMode.Diagnostics.Any(item => item.Code == "outfit-proposal-mode-invalid"),
                "Invalid outfit proposal mode was accepted.");
            var invalidSignature = await service.ProposeAsync(new OutfitProposalRequest(
                GameEdition.Fallout4, source, sourceRecord.FormId, OutfitProposalMode.Override, null,
                [new FormReference(plugin, invalidItem.FormId)],
                new WorkspacePath(Path.Combine(root.Value, "invalid-signature.outfit-proposal.json"))),
                CancellationToken.None);
            Assert(!invalidSignature.Written && invalidSignature.Diagnostics.Any(item =>
                    item.Code == "outfit-proposal-item-signature-invalid"),
                "Outfit proposal accepted a non-ARMO/LVLI record link.");

            var (runner, output, error) = CreateRunner(null, new FakeOutfitProposalService(create));
            var exit = await runner.RunAsync(CommandLine.Parse(["outfit", "propose", "--edition", "fallout4",
                "--plugin", source.Value, "--source", "0x801", "--mode", "new", "--editor-id", "NewOutfit",
                "--target-form", "0x804",
                "--items", "[\"Source.esp|0x900\"]", "--output", Path.Combine(root.Value, "cli.outfit-proposal.json"), "--json"]),
                CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "outfit-record-proposal" &&
                document.RootElement.GetProperty("noUnrelatedRecords").GetBoolean(),
                "Outfit proposal CLI did not expose the typed plan.");
            output.GetStringBuilder().Clear();
            var aliasExit = await runner.RunAsync(CommandLine.Parse(["outfit", "create", "--edition", "fallout4",
                "--plugin", source.Value, "--source", "0x801", "--mode", "new", "--editor-id", "NewOutfit",
                "--target-form", "0x805", "--items", "[\"Source.esp|0x900\"]",
                "--output", Path.Combine(root.Value, "cli.outfit-create.json"), "--json"]),
                CancellationToken.None);
            using var aliasDocument = JsonDocument.Parse(output.ToString());
            Assert(aliasExit == CommandExitCode.Success &&
                aliasDocument.RootElement.GetProperty("artifactKind").GetString() == "outfit-record-proposal",
                "Outfit create alias did not route to the typed outfit proposal service.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestLeveledListProposal()
    {
        var root = NewPresetOutput("leveled-list-proposal");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [4, 5, 6]);
        var master = new PluginName("Fallout4.esm");
        var record = new PluginRecordSummary(new FormId(0x801), "LVLI", "SourceList", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        LeveledListEntryProposal[] entries = [
            new LeveledListEntryProposal(new FormReference(plugin, new FormId(0x900)), 2, 1, 10),
            new LeveledListEntryProposal(new FormReference(master, new FormId(0x901)), 5, 2, 0),
            new LeveledListEntryProposal(new FormReference(plugin, new FormId(0x900)), 10, 3, 0)
        ];
        try
        {
            var service = new LeveledListProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [master], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var path = new WorkspacePath(Path.Combine(root.Value, "list.leveled-list-proposal.json"));
            var result = await service.ProposeAsync(new LeveledListProposalRequest(GameEdition.Fallout4, source,
                record.FormId, new EditorId("SourceList"), 25, 3, true, false, true, entries.ToImmutableArray(), path), CancellationToken.None);
            Assert(result.Written && result.Artifact?.Entries.Select(item => item.Item).SequenceEqual(entries.Select(item => item.Item.ToString())) == true &&
                result.Artifact.ChanceNone == 25 && result.Artifact.CalculateAllLevels && result.Artifact.UseAll &&
                result.Artifact.MasterDependencies.SequenceEqual([master.Value]),
                "Leveled-list proposal did not preserve typed entries and flags.");

            var (runner, output, error) = CreateRunner(null, null, new FakeLeveledListProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["leveled-list", "propose", "--edition", "fallout4",
                "--plugin", source.Value, "--list", "0x801", "--entries",
                "[{\"item\":\"Source.esp|0x900\",\"level\":2,\"count\":1,\"chanceNone\":10}]",
                "--chance-none", "25", "--calc-all-levels", "--use-all", "--output",
                Path.Combine(root.Value, "cli.leveled-list-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "leveled-list-record-proposal" &&
                document.RootElement.GetProperty("entries").GetArrayLength() == 3,
                "Leveled-list proposal CLI did not expose the typed plan.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestLeveledListResolve()
    {
        var root = NewPresetOutput("leveled-list-resolve");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [7, 8, 9]);
        var record = new PluginRecordSummary(new FormId(0x801), "LVLI", "SourceList", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        LeveledListEntryProposal[] entries = [
            new LeveledListEntryProposal(new FormReference(plugin, new FormId(0x900)), 2, 1, 0),
            new LeveledListEntryProposal(new FormReference(plugin, new FormId(0x901)), 5, 2, 0)
        ];
        try
        {
            var proposalPath = new WorkspacePath(Path.Combine(root.Value, "list.leveled-list-proposal.json"));
            var proposalService = new LeveledListProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var proposal = await proposalService.ProposeAsync(new LeveledListProposalRequest(GameEdition.Fallout4,
                source, record.FormId, new EditorId("SourceList"), 0, 0, false, true, false,
                entries.ToImmutableArray(), proposalPath), CancellationToken.None);
            var resolveService = new LeveledListResolveService(policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var first = await resolveService.ResolveAsync(new LeveledListResolveRequest(GameEdition.Fallout4,
                proposalPath, 42, new WorkspacePath(Path.Combine(root.Value, "first.leveled-list-resolution.json"))), CancellationToken.None);
            var second = await resolveService.ResolveAsync(new LeveledListResolveRequest(GameEdition.Fallout4,
                proposalPath, 42, new WorkspacePath(Path.Combine(root.Value, "second.leveled-list-resolution.json"))), CancellationToken.None);
            Assert(proposal.Written && first.Written && second.Written &&
                first.Artifact?.ResolvedItems.SequenceEqual(second.Artifact!.ResolvedItems) == true &&
                first.Artifact.Selections.Length == 2 && first.Artifact.ResolvedItems.Length == 2,
                "Leveled-list resolve was not seed-stable or CalculateEachInCount-compatible.");

            var (runner, output, error) = CreateRunner(null, null, null, new FakeLeveledListResolveService(first));
            var exit = await runner.RunAsync(CommandLine.Parse(["leveled-list", "resolve", "--edition", "fallout4",
                "--list", proposalPath.Value, "--seed", "42", "--output",
                Path.Combine(root.Value, "cli.leveled-list-resolution.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "leveled-list-resolution" &&
                document.RootElement.GetProperty("resolvedItems").GetArrayLength() == 2,
                "Leveled-list resolve CLI did not expose the deterministic result.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestArmorProposal()
    {
        var root = NewPresetOutput("armor-proposal");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [10, 11, 12]);
        var record = new PluginRecordSummary(new FormId(0x801), "ARMO", "SourceArmor", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new ArmorProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "armor.armor-proposal.json"));
            var result = await service.ProposeAsync(new ArmorProposalRequest(GameEdition.Fallout4, source,
                record.FormId, ArmorProposalMode.New, new EditorId("FixtureArmor"), "Fixture Armor", 0x4000,
                new FormReference(plugin, new FormId(0x900)), "meshes/armor/male.nif", "meshes/armor/female.nif",
                125, 12.5, 80, 35, [new FormReference(plugin, new FormId(0x901))],
                [new ArmorAddonProposal(0, new FormReference(plugin, new FormId(0x902)))], outputPath,
                new FormId(0x802)), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ArtifactKind == "armor-record-proposal" &&
                result.Artifact.ChangedFields.SequenceEqual(["name", "slotMask", "race", "maleWorldModel", "femaleWorldModel", "value", "weight", "health", "armorRating", "keywords", "armorAddons"]) &&
                result.Artifact.Keywords.SequenceEqual(["Source.esp|0x00000901"]) &&
                result.Artifact.ArmorAddons.Single().Addon == "Source.esp|0x00000902",
                "Armor proposal did not preserve supported typed fields.");

            var fakeArmorProposal = new FakeArmorProposalService(result);
            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, fakeArmorProposal);
            var exit = await runner.RunAsync(CommandLine.Parse(["armor", "propose", "--edition", "fallout4", "--plugin", source.Value,
                "--source", "0x801", "--patch", "{\"mode\":\"new\",\"editorId\":\"FixtureArmor\",\"name\":\"Fixture Armor\"}",
                "--output", Path.Combine(root.Value, "cli.armor-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "armor-record-proposal" &&
                document.RootElement.GetProperty("changedFields").GetArrayLength() == 11,
                "Armor proposal CLI did not expose the typed artifact.");

            const string completePatch = "{\"mode\":\"new\",\"editorId\":\"npcm_ARMO_Cli\",\"targetFormId\":\"0x802\",\"name\":\"CLI armor\",\"slotMask\":4,\"race\":\"Source.esp|0x900\",\"value\":4294967295,\"weight\":3.5,\"armorRating\":12.25,\"keywords\":[],\"armorAddons\":[{\"index\":0,\"addon\":\"Source.esp|0x902\"},{\"index\":0,\"addon\":\"Source.esp|0x902\"}],\"description\":\"Complete CLI document\",\"nonPlayable\":true,\"objectBounds\":{\"minimumX\":-1,\"minimumY\":-2,\"minimumZ\":-3,\"maximumX\":4,\"maximumY\":5,\"maximumZ\":6},\"completeDocument\":true}";
            var completeExit = await runner.RunAsync(CommandLine.Parse(["armor", "propose", "--edition", "skyrimse", "--plugin", source.Value,
                "--source", "0x801", "--patch", completePatch,
                "--output", Path.Combine(root.Value, "cli.complete.armor-proposal.json"), "--json"]), CancellationToken.None);
            Assert(completeExit == CommandExitCode.Success &&
                   fakeArmorProposal.LastRequest is { CompleteDocument: true, Value: uint.MaxValue } complete &&
                   complete.ObjectBounds == new ArmorObjectBounds(-1, -2, -3, 4, 5, 6) &&
                   complete.ArmorAddons?.Length == 2,
                "Armor proposal CLI did not parse the complete Skyrim document, UInt32 value, bounds, or repeated ordered ARMA rows.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestArmorDamageResistance()
    {
        var root = NewPresetOutput("armor-damage-resistance");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [13, 14, 15]);
        var record = new PluginRecordSummary(new FormId(0x801), "ARMO", "SourceArmor", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var entries = ImmutableArray.Create(
                new ArmorDamageResistanceEntry(new FormReference(plugin, new FormId(0x900)), 10u),
                new ArmorDamageResistanceEntry(new FormReference(plugin, new FormId(0x901)), 25u));
            var service = new ArmorDamageResistanceService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "armor.armor-damage-resist-proposal.json"));
            var result = await service.ProposeAsync(new ArmorDamageResistanceRequest(GameEdition.Fallout4, source,
                record.FormId, entries, outputPath), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ArtifactKind == "armor-damage-resistance-proposal" &&
                result.Artifact.Entries.Select(item => item.DamageType).SequenceEqual(["Source.esp|0x00000900", "Source.esp|0x00000901"]) &&
                result.Artifact.Entries.Select(item => item.Value).SequenceEqual([10u, 25u]) &&
                result.Artifact.NoUnrelatedRecords && result.Artifact.InputSha256.Length == 64,
                "Armor damage resistance did not preserve ordered typed entries.");

            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null,
                new FakeArmorDamageResistanceService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["armor", "damage-resist", "--edition", "fallout4",
                "--plugin", source.Value, "--source", "0x801", "--damage-resist",
                "[{\"damageType\":\"Source.esp|0x900\",\"value\":10}]", "--output",
                Path.Combine(root.Value, "cli.armor-damage-resist-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "armor-damage-resistance-proposal" &&
                document.RootElement.GetProperty("entries").GetArrayLength() == 2,
                "Armor damage resistance CLI did not expose the typed artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestArmorAddonProposal()
    {
        var root = NewPresetOutput("armor-addon-proposal");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [16, 17, 18]);
        var record = new PluginRecordSummary(new FormId(0x801), "ARMA", "SourceAddon", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var patch = new ArmorAddonProposalPatch(new EditorId("FixtureAddon"), 0x4000,
                new FormReference(plugin, new FormId(0x900)), new FormReference(plugin, new FormId(0x901)), 10, 20, 2, 2, 3,
                1.5, "meshes/armor/male.nif", "meshes/armor/female.nif", "meshes/armor/male_fp.nif", "meshes/armor/female_fp.nif",
                3, 4, 5, 6, new FormReference(plugin, new FormId(0x902)), new FormReference(plugin, new FormId(0x903)),
                new FormReference(plugin, new FormId(0x904)), new FormReference(plugin, new FormId(0x905)), new FormReference(plugin, new FormId(0x906)),
                new FormReference(plugin, new FormId(0x907)), new FormReference(plugin, new FormId(0x908)), new FormReference(plugin, new FormId(0x909)),
                new FormReference(plugin, new FormId(0x90A)), ImmutableArray.Create(new FormReference(plugin, new FormId(0x90B))),
                ImmutableArray.Create(new ArmorAddonSculptProposal(0, "Breast_skin", 1.0, 0.0, -1.0)), true, true, false);
            var service = new ArmorAddonProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "addon.armor-addon-proposal.json"));
            var result = await service.ProposeAsync(new ArmorAddonProposalRequest(GameEdition.Fallout4, source,
                record.FormId, ArmorAddonProposalMode.New, patch, outputPath, new FormId(0x802)), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ArtifactKind == "armor-addon-record-proposal" &&
                result.Artifact.ChangedFields.Contains("maleModel") && result.Artifact.ChangedFields.Contains("sculpt") &&
                result.Artifact.MaleSkinTexture == "Source.esp|0x00000902" && result.Artifact.Sculpt.Single().BoneName == "Breast_skin" &&
                result.Artifact.NoUnrelatedRecords,
                "Armor-addon proposal did not preserve typed model, skin, and sculpt routes.");

            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null, null,
                new FakeArmorAddonProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["armor-addon", "propose", "--edition", "fallout4", "--plugin", source.Value,
                "--source", "0x801", "--patch", "{\"mode\":\"new\",\"editorId\":\"FixtureAddon\",\"maleModel\":\"meshes/armor/male.nif\"}",
                "--output", Path.Combine(root.Value, "cli.armor-addon-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "armor-addon-record-proposal" &&
                document.RootElement.GetProperty("changedFields").GetArrayLength() == result.Artifact!.ChangedFields.Length,
                "Armor-addon CLI did not expose the typed artifact.");

            var completePatch = new ArmorAddonProposalPatch(
                new EditorId("npcm_ARMA_Complete"), 0x20,
                null, null, 1, 2, 1, 0, 3, 100000,
                null, null, null, null,
                null, null, null, null,
                null, null, null, null,
                null, null, null, null,
                null, ImmutableArray<FormReference>.Empty,
                null, null, null, null);
            var skyrimService = new ArmorAddonProposalService(
                new FakePluginReader(new PluginInspection(
                    GameEdition.SkyrimSpecialEdition,
                    plugin, [], [record], [])),
                policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var completeOutput = new WorkspacePath(Path.Combine(
                root.Value, "complete.armor-addon-proposal.json"));
            ArmorAddonProposalResult complete = await skyrimService.ProposeAsync(
                new ArmorAddonProposalRequest(
                    GameEdition.SkyrimSpecialEdition,
                    source,
                    record.FormId,
                    ArmorAddonProposalMode.New,
                    completePatch,
                    completeOutput,
                    new FormId(0x803),
                    CompleteDocument: true,
                    TargetPlugin: new PluginName("Complete.esp")),
                CancellationToken.None);
            Assert(complete.Written && complete.Artifact is
            {
                CompleteDocument: true,
                WeaponAdjust: 100000
            } completeArtifact &&
                completeArtifact.ChangedFields.Contains("race") &&
                completeArtifact.ChangedFields.Contains("maleModel") &&
                completeArtifact.ChangedFields.Contains("maleSkinTexture"),
                "Complete Skyrim ARMA clear semantics or weapon range were lost.");

            ArmorAddonProposalResult outOfRange =
                await skyrimService.ProposeAsync(
                    new ArmorAddonProposalRequest(
                        GameEdition.SkyrimSpecialEdition,
                        source,
                        record.FormId,
                        ArmorAddonProposalMode.New,
                        completePatch with { WeaponAdjust = 100000.01 },
                        new WorkspacePath(Path.Combine(
                            root.Value,
                            "out-of-range.armor-addon-proposal.json")),
                        new FormId(0x804),
                        CompleteDocument: true,
                        TargetPlugin: new PluginName("Complete.esp")),
                    CancellationToken.None);
            Assert(!outOfRange.Written && outOfRange.Diagnostics.Any(item =>
                    item.Code == "armor-addon-weapon-adjust-range"),
                "An out-of-range complete Skyrim weapon adjustment was accepted.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestArmorAddonModels()
    {
        var root = NewPresetOutput("armor-addon-models");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [19, 20, 21]);
        var record = new PluginRecordSummary(new FormId(0x801), "ARMO", "SourceArmor", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var entries = ImmutableArray.Create(
            new ArmorAddonModelEntryProposal(2, new FormReference(plugin, new FormId(0x900))),
            new ArmorAddonModelEntryProposal(7, new FormReference(plugin, new FormId(0x901))));
        try
        {
            var service = new ArmorAddonModelProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "models.armor-addon-model-proposal.json"));
            var result = await service.ProposeAsync(new ArmorAddonModelProposalRequest(GameEdition.Fallout4, source,
                record.FormId, entries, outputPath), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ArtifactKind == "armor-addon-model-entries-proposal" &&
                result.Artifact!.Entries.Length == 2 && result.Artifact.Entries[0].Index == 2 && result.Artifact.Entries[1].Index == 7 &&
                result.Artifact.Entries[0].Addon == "Source.esp|0x00000900" && result.Artifact.Entries[1].Addon == "Source.esp|0x00000901",
                "Armor-addon model entries did not preserve order and indexes.");

            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null, null, null,
                new FakeArmorAddonModelProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["armor-addon", "propose", "--edition", "fallout4", "--plugin", source.Value,
                "--source", "0x801", "--models", "[{\"index\":2,\"addon\":\"Source.esp|0x900\"}]", "--output",
                Path.Combine(root.Value, "cli.armor-addon-model-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "armor-addon-model-entries-proposal" &&
                document.RootElement.GetProperty("entries").GetArrayLength() == 2,
                "Armor-addon model CLI did not expose the typed artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestMaterialSwapProposal()
    {
        var root = NewPresetOutput("material-swap-proposal");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [22, 23, 24]);
        var record = new PluginRecordSummary(new FormId(0x801), "MSWP", "SourceSwap", null, false, false, null, null, []);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var entries = ImmutableArray.Create(
            new MaterialSwapEntryProposal("materials/armor/old.bgsm", "materials/armor/new.bgsm", 0.25, "armor"),
            new MaterialSwapEntryProposal("materials/armor/old2.bgem", string.Empty, null, null));
        try
        {
            var service = new MaterialSwapProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy,
                new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "swap.material-swap-proposal.json"));
            var result = await service.ProposeAsync(new MaterialSwapProposalRequest(GameEdition.Fallout4, source,
                record.FormId, MaterialSwapProposalMode.New, new MaterialSwapProposalPatch(new EditorId("FixtureSwap"), "Armor", entries), outputPath,
                new FormId(0x802)), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ArtifactKind == "material-swap-record-proposal" &&
                result.Artifact.Entries.Length == 2 && result.Artifact.Entries[0].ColorRemapIndex == 0.25 &&
                result.Artifact.Entries[1].ReplacementMaterial.Length == 0,
                "Material-swap proposal did not preserve ordered substitutions and remap values.");

            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null, null, null, null,
                new FakeMaterialSwapProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["material-swap", "propose", "--edition", "fallout4", "--plugin", source.Value,
                "--source", "0x801", "--patch", "{\"mode\":\"new\",\"targetFormId\":\"0x802\",\"editorId\":\"FixtureSwap\",\"entries\":[{\"originalMaterial\":\"materials/armor/old.bgsm\",\"replacementMaterial\":\"materials/armor/new.bgsm\"}]}",
                "--output", Path.Combine(root.Value, "cli.material-swap-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "material-swap-record-proposal" &&
                document.RootElement.GetProperty("entries").GetArrayLength() == 2,
                "Material-swap CLI did not expose the typed artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestObjectTemplateProposal()
    {
        var root = NewPresetOutput("object-template-proposal");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp");
        var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value));
        File.WriteAllBytes(source.Value, [31, 32, 33]);
        var record = new PluginRecordSummary(new FormId(0x801), "ARMO", "SourceArmor", null, false, false, null, null, []);
        var keyword = new FormReference(plugin, new FormId(0x900));
        var include = new FormReference(plugin, new FormId(0x901));
        var combinations = ImmutableArray.Create(new ObjectTemplateCombinationProposal("Default", true, false, null, 1, 20, 5, 2, [keyword],
            [new ObjectTemplateIncludeProposal(include, 3, true, false)]));
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new ObjectTemplateProposalService(new FakePluginReader(new PluginInspection(
                GameEdition.Fallout4, plugin, [], [record], [])), policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "template.object-template-proposal.json"));
            var result = await service.ProposeAsync(new ObjectTemplateProposalRequest(GameEdition.Fallout4, source,
                record.FormId, ObjectTemplateProposalMode.New, new ObjectTemplateProposalPatch(new EditorId("FixtureTemplate"), combinations, new FormId(0x802)), outputPath), CancellationToken.None);
            Assert(result.Written && result.Artifact?.Combinations.Length == 1 && result.Artifact.Combinations[0].Keywords[0] == "Source.esp|0x00000900" &&
                result.Artifact.Combinations[0].Includes[0].Mod == "Source.esp|0x00000901" && result.Artifact.Combinations[0].Includes[0].AttachPointIndex == 3,
                "Object-template proposal did not preserve combination/include order.");

            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null, null, null, null, null,
                new FakeObjectTemplateProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["object-template", "propose", "--edition", "fallout4", "--plugin", source.Value,
                "--source", "0x801", "--combinations", "{\"mode\":\"new\",\"targetFormId\":\"0x802\",\"editorId\":\"FixtureTemplate\",\"items\":[{}]}",
                "--includes", "[]", "--output", Path.Combine(root.Value, "cli.object-template-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("artifactKind").GetString() == "object-template-combinations-proposal" &&
                document.RootElement.GetProperty("combinations").GetArrayLength() == 1,
                "Object-template CLI did not expose the typed artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestObjectTemplateProperties()
    {
        var root = NewPresetOutput("object-template-properties");
        DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var plugin = new PluginName("Source.esp"); var source = new WorkspacePath(Path.Combine(root.Value, plugin.Value)); File.WriteAllBytes(source.Value, [41, 42, 43]);
        var record = new PluginRecordSummary(new FormId(0x801), "ARMO", "SourceArmor", null, false, false, null, null, []);
        var reference = new FormReference(plugin, new FormId(0x902));
        var properties = ImmutableArray.Create(new ObjectTemplatePropertyProposal("FloatType", 1, 7, null, 1.5, null, 0, 2.5, 0.25), new ObjectTemplatePropertyProposal("FormIDInt", 0, 8, null, null, reference, 0, 0, 1));
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new ObjectTemplatePropertyProposalService(new FakePluginReader(new PluginInspection(GameEdition.Fallout4, plugin, [], [record], [])), policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "properties.object-template-properties-proposal.json"));
            var result = await service.ProposeAsync(new ObjectTemplatePropertyProposalRequest(GameEdition.Fallout4, source, record.FormId, properties, outputPath), CancellationToken.None);
            Assert(result.Written && result.Artifact?.Properties.Length == 2 && result.Artifact.Properties[0].Value1Float == 1.5 && result.Artifact.Properties[1].Value1FormId == "Source.esp|0x00000902", "Object-template properties did not preserve typed value routes.");
            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null, null, null, null, null, null, new FakeObjectTemplatePropertyProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["object-template", "propose", "--edition", "fallout4", "--plugin", source.Value, "--source", "0x801", "--properties", "[{\"valueType\":\"FloatType\",\"value1Float\":1.5}]", "--output", Path.Combine(root.Value, "cli.object-template-properties-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString()); Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && document.RootElement.GetProperty("artifactKind").GetString() == "object-template-properties-proposal", "Object-template property CLI did not expose the typed artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestChangeTracking()
    {
        var root = NewPresetOutput("changes-session"); DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var session = new WorkspacePath(Path.Combine(root.Value, "fixture.changes-session.json"));
        await File.WriteAllTextAsync(session.Value, "{\"schemaVersion\":\"1\",\"baseline\":[{\"formId\":\"0x800\",\"signature\":\"NPC_\",\"editorId\":\"One\",\"fields\":{\"weight\":50,\"name\":\"same\"}},{\"formId\":\"0x801\",\"signature\":\"NPC_\",\"fields\":{\"weight\":20}}],\"working\":[{\"formId\":\"0x800\",\"signature\":\"NPC_\",\"editorId\":\"One\",\"fields\":{\"weight\":55,\"name\":\"same\"}},{\"formId\":\"0x801\",\"signature\":\"NPC_\",\"fields\":{\"weight\":20}},{\"formId\":\"0x802\",\"signature\":\"ARMO\",\"fields\":{\"value\":10}}]}");
        try
        {
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
            var service = new ChangeTrackingService(policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var result = await service.ListAsync(new ChangeListRequest(GameEdition.Fallout4, session), CancellationToken.None);
            Assert(result.Succeeded && result.Changes.Length == 2 && result.Changes[0].Fields[0].Name == "weight" && result.Changes[1].Fields[0].Name == "record", "Change tracking did not omit unchanged records or preserve stable ordering.");
            var (runner, cliOutput, error) = CreateRunner(null, null, null, null, null, null, null, null, null, null, null, new FakeChangeTrackingService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["changes", "list", "--edition", "fallout4", "--session", session.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString()); Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && document.RootElement.GetProperty("changes").GetArrayLength() == 2, "Changes CLI did not expose the typed diff.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestChangeAction()
    {
        var root = NewPresetOutput("changes-action"); DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var session = new WorkspacePath(Path.Combine(root.Value, "fixture.changes-session.json"));
        await File.WriteAllTextAsync(session.Value, "{\"baseline\":[{\"formId\":\"0x800\",\"signature\":\"NPC_\",\"editorId\":\"Override\",\"fields\":{\"weight\":50}}],\"working\":[{\"formId\":\"0x800\",\"signature\":\"NPC_\",\"editorId\":\"Override\",\"fields\":{\"weight\":55}},{\"formId\":\"0x801\",\"signature\":\"ARMO\",\"editorId\":\"New\",\"fields\":{\"value\":10}}]}");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new ChangeActionService(policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var resetOutput = new WorkspacePath(Path.Combine(root.Value, "reset.changes-action.json"));
            var reset = await service.UpdateAsync(new ChangeActionRequest(GameEdition.Fallout4, session, new FormId(0x800), "NPC_", ChangeAction.Reset, resetOutput), CancellationToken.None);
            Assert(reset.Written && reset.Artifact?.Outcome == "restore-baseline" && reset.Artifact.BaselineFields["weight"] == "50" && reset.Artifact.WorkingFields["weight"] == "55", "Reset action did not preserve baseline and working snapshots.");
            var deleteOutput = new WorkspacePath(Path.Combine(root.Value, "delete.changes-action.json"));
            var delete = await service.UpdateAsync(new ChangeActionRequest(GameEdition.Fallout4, session, new FormId(0x801), null, ChangeAction.Delete, deleteOutput), CancellationToken.None);
            Assert(delete.Written && delete.Artifact?.Outcome == "remove-new-record" && !delete.Artifact.BaselinePresent && delete.Artifact.WorkingPresent, "Delete action did not mark a new record explicitly.");
            var existing = await service.UpdateAsync(new ChangeActionRequest(GameEdition.Fallout4, session, new FormId(0x800), "NPC_", ChangeAction.Reset, resetOutput), CancellationToken.None);
            Assert(existing.Diagnostics.Any(item => item.Code == "changes-action-output-exists"), "Change-action output overwrite was allowed.");
            var missingBaseline = await service.UpdateAsync(new ChangeActionRequest(GameEdition.Fallout4, session, new FormId(0x801), "ARMO", ChangeAction.Reset, new WorkspacePath(Path.Combine(root.Value, "invalid.changes-action.json"))), CancellationToken.None);
            Assert(!missingBaseline.Written && missingBaseline.Diagnostics.Any(item => item.Code == "changes-action-reset-baseline-missing"), "Reset of a new record was not refused.");
            var (runner, cliOutput, error) = CreateRunner(changeActionService: new FakeChangeActionService(reset));
            var exit = await runner.RunAsync(CommandLine.Parse(["changes", "update", "--edition", "fallout4", "--session", session.Value, "--record", "0x800", "--signature", "NPC_", "--action", "reset", "--output", Path.Combine(root.Value, "cli.changes-action.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && document.RootElement.GetProperty("artifact").GetProperty("outcome").GetString() == "restore-baseline", "Changes update CLI did not expose the typed action artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestRecordProposal()
    {
        var root = NewPresetOutput("record-proposal"); DeleteDirectory(root.Value); Directory.CreateDirectory(root.Value);
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var service = new RecordProposalService(policy, new WorkspacePath("K:\\ExampleWorkspace"));
            var output = new WorkspacePath(Path.Combine(root.Value, "new.record-proposal.json"));
            var result = await service.ProposeAsync(new RecordProposalRequest(GameEdition.Fallout4, new RecordSignature("NPC_"), RecordProposalMode.New,
                new FormId(0x1234), null, new EditorId("npcm_Fixture"), "Fixture NPC", [new PluginName("Fallout4.esm")], output), CancellationToken.None);
            Assert(result.Written && result.Artifact?.Mode == RecordProposalMode.New && result.Artifact.FormId == "0x00001234" &&
                result.Artifact.AllocationStrategy == "explicit-local-form-id" && result.Artifact.MasterDependencies.SequenceEqual(["Fallout4.esm"]),
                "Record proposal did not preserve explicit allocation and masters.");
            var overrideOutput = new WorkspacePath(Path.Combine(root.Value, "override.record-proposal.json"));
            var overrideResult = await service.ProposeAsync(new RecordProposalRequest(GameEdition.SkyrimSpecialEdition, new RecordSignature("NPC_"), RecordProposalMode.Override,
                new FormId(0x2345), new FormId(0x2345), new EditorId("npcm_Override"), null, [], overrideOutput), CancellationToken.None);
            Assert(overrideResult.Written && overrideResult.Artifact?.Mode == RecordProposalMode.Override && overrideResult.Artifact.SourceFormId == "0x00002345",
                "Override proposal did not preserve source identity.");
            var bad = await service.ProposeAsync(new RecordProposalRequest(GameEdition.Fallout4, new RecordSignature("NPC_"), RecordProposalMode.Override,
                new FormId(0x2345), new FormId(0x9999), new EditorId("npcm_Bad"), null, [], new WorkspacePath(Path.Combine(root.Value, "bad.record-proposal.json"))), CancellationToken.None);
            Assert(!bad.Written && bad.Diagnostics.Any(item => item.Code == "record-proposal-override-source"), "Override source mismatch was accepted.");
            var (runner, cliOutput, error) = CreateRunner(recordProposalService: new FakeRecordProposalService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["records", "propose", "--edition", "fallout4", "--type", "NPC_", "--mode", "new", "--form-id", "0x1234", "--editor-id", "npcm_Fixture", "--output", Path.Combine(root.Value, "cli.record-proposal.json"), "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(cliOutput.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 && document.RootElement.GetProperty("artifact").GetProperty("allocationStrategy").GetString() == "explicit-local-form-id", "Records propose CLI did not expose the typed artifact.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestBodySidecarWrite()
    {
        var root = NewMutationOutput("body-sidecar-write-tests");
        Directory.CreateDirectory(root.Value);
        var output = new WorkspacePath(Path.Combine(root.Value, "WriteFixture.bssliders"));
        try
        {
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
            var (runner, response, _) = CreateRunner(bodySidecarWriteService: new BodySidecarWriteService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var exit = await runner.RunAsync(CommandLine.Parse(["body", "sidecar", "write", "--game", "fallout4", "--plugin", "WriteFixture.esp",
                "--npc", "0x000800", "--sliders", "{\"Calf\":0.25,\"Waist\":-0.1}", "--output", output.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(response.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("written").GetBoolean() && File.Exists(output.Value),
                "BodySlide sidecar writer did not emit the typed output.");
            var inspected = await new BodySidecarInspectionService(policy, new WorkspacePath("K:\\ExampleWorkspace"))
                .InspectAsync(new BodySidecarInspectRequest(GameEdition.Fallout4, output), CancellationToken.None);
            Assert(inspected.IsValid && inspected.Document!.Npcs.Single().BodyMorphs["Calf"] == 0.25f, "Written sidecar did not round-trip through the inspector.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestOutfitBinaryWriteCli()
    {
        var root = NewPresetOutput("outfit-binary-write");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "created.esp"));
            var result = new OutfitBinaryWriteResult(true, new WorkspacePath(Path.Combine(root.Value, "proposal.json")),
                outputPath, new FormId(0x804), new Sha256Hash(new string('A', 64)), []);
            var (runner, output, error) = CreateRunner(outfitBinaryWriteService: new FakeOutfitBinaryWriteService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["outfit", "write", "--edition", "fallout4",
                "--proposal", result.Proposal.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("targetFormId").GetString() == "0x00000804" &&
                document.RootElement.GetProperty("outputSha256").GetString() == new string('a', 64),
                "Outfit binary write CLI did not expose typed output metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestLeveledListBinaryWriteCli()
    {
        var root = NewPresetOutput("leveled-list-binary-write");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "created.esp"));
            var result = new LeveledListBinaryWriteResult(true, new WorkspacePath(Path.Combine(root.Value, "proposal.json")),
                outputPath, new FormId(0x801), new Sha256Hash(new string('B', 64)), []);
            var (runner, output, error) = CreateRunner(leveledListBinaryWriteService: new FakeLeveledListBinaryWriteService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["leveled-list", "write", "--edition", "fallout4",
                "--proposal", result.Proposal.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("listFormId").GetString() == "0x00000801" &&
                document.RootElement.GetProperty("outputSha256").GetString() == new string('b', 64),
                "Leveled-list binary write CLI did not expose typed output metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestArmorBinaryWriteCli()
    {
        var root = NewPresetOutput("armor-binary-write");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "created.esp"));
            var result = new ArmorBinaryWriteResult(true, new WorkspacePath(Path.Combine(root.Value, "proposal.json")),
                outputPath, new FormId(0x802), new Sha256Hash(new string('C', 64)), []);
            var (runner, output, error) = CreateRunner(armorBinaryWriteService: new FakeArmorBinaryWriteService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["armor", "write", "--edition", "fallout4",
                "--proposal", result.Proposal.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("targetFormId").GetString() == "0x00000802" &&
                document.RootElement.GetProperty("outputSha256").GetString() == new string('c', 64),
                "Armor binary write CLI did not expose typed output metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestArmorAddonBinaryWriteCli()
    {
        var root = NewPresetOutput("armor-addon-binary-write");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "created.esp"));
            var result = new ArmorAddonBinaryWriteResult(true, new WorkspacePath(Path.Combine(root.Value, "proposal.json")),
                outputPath, new FormId(0x802), new Sha256Hash(new string('D', 64)), []);
            var (runner, output, error) = CreateRunner(armorAddonBinaryWriteService: new FakeArmorAddonBinaryWriteService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["armor-addon", "write", "--edition", "fallout4",
                "--proposal", result.Proposal.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("targetFormId").GetString() == "0x00000802" &&
                document.RootElement.GetProperty("outputSha256").GetString() == new string('d', 64),
                "Armor-addon binary write CLI did not expose typed output metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestMaterialSwapBinaryWriteCli()
    {
        var root = NewPresetOutput("material-swap-binary-write");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "created.esp"));
            var result = new MaterialSwapBinaryWriteResult(true, new WorkspacePath(Path.Combine(root.Value, "proposal.json")),
                outputPath, new FormId(0x802), new Sha256Hash(new string('E', 64)), []);
            var (runner, output, error) = CreateRunner(materialSwapBinaryWriteService: new FakeMaterialSwapBinaryWriteService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["material-swap", "write", "--edition", "fallout4",
                "--proposal", result.Proposal.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("targetFormId").GetString() == "0x00000802" &&
                document.RootElement.GetProperty("outputSha256").GetString() == new string('e', 64),
                "Material-swap binary write CLI did not expose typed output metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestObjectTemplateBinaryWriteCli()
    {
        var root = NewPresetOutput("object-template-binary-write");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var outputPath = new WorkspacePath(Path.Combine(root.Value, "created.esp"));
            var result = new ObjectTemplateBinaryWriteResult(true, new WorkspacePath(Path.Combine(root.Value, "proposal.json")),
                outputPath, new FormId(0x802), new Sha256Hash(new string('F', 64)), []);
            var (runner, output, error) = CreateRunner(objectTemplateBinaryWriteService: new FakeObjectTemplateBinaryWriteService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["object-template", "write", "--edition", "fallout4",
                "--proposal", result.Proposal.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("targetFormId").GetString() == "0x00000802" &&
                document.RootElement.GetProperty("outputSha256").GetString() == new string('f', 64),
                "Object-template binary write CLI did not expose typed output metadata.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPluginWrite()
    {
        var root = NewMutationOutput("plugin-write-tests");
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        var output = new WorkspacePath(Path.Combine(root.Value, "selected.esp"));
        var proposalPath = new WorkspacePath(Path.Combine(root.Value, "selected.npc-proposal.json"));
        var staleOutput = new WorkspacePath(Path.Combine(root.Value, "stale.esp"));
        try
        {
            var proposal = new
            {
                schemaVersion = 1,
                edition = "fallout4",
                inputPlugin = source.Value,
                outputPlugin = output.Value,
                targetFormId = "0x00000800",
                inputSha256 = Hash(source).Value,
                changes = new[] { new { field = "Sex", before = "male", after = "female" } },
                preservedFields = new[] { "ACBS", "DATA" }
            };
            await File.WriteAllTextAsync(proposalPath.Value, JsonSerializer.Serialize(proposal));
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
            var pluginWrite = new PluginWriteService(policy, new WorkspacePath("K:\\ExampleWorkspace"),
                new NpcMutationService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var (runner, response, _) = CreateRunner(pluginWriteService: pluginWrite);
            var exit = await runner.RunAsync(CommandLine.Parse(["plugin", "write", "--game", "fallout4", "--proposal", proposalPath.Value,
                "--output", output.Value, "--no-overwrite", "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(response.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("applied").GetBoolean() && File.Exists(output.Value),
                "plugin write did not apply the selected scalar proposal.");

            var stale = new
            {
                schemaVersion = 1,
                edition = "fallout4",
                inputPlugin = source.Value,
                outputPlugin = staleOutput.Value,
                targetFormId = "0x00000800",
                inputSha256 = new string('0', 64),
                changes = new[] { new { field = "Sex", before = "male", after = "female" } },
                preservedFields = new[] { "ACBS", "DATA" }
            };
            await File.WriteAllTextAsync(proposalPath.Value, JsonSerializer.Serialize(stale));
            var (staleRunner, staleResponse, _) = CreateRunner(pluginWriteService: pluginWrite);
            var staleExit = await staleRunner.RunAsync(CommandLine.Parse(["plugin", "write", "--game", "fallout4", "--proposal", proposalPath.Value,
                "--output", staleOutput.Value, "--no-overwrite", "--json"]), CancellationToken.None);
            using var staleDocument = JsonDocument.Parse(staleResponse.ToString());
            Assert(staleExit == CommandExitCode.ValidationFailure && !staleDocument.RootElement.GetProperty("applied").GetBoolean() && !File.Exists(staleOutput.Value),
                "plugin write accepted a stale input hash.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPluginVerify()
    {
        var root = NewMutationOutput("plugin-verify-tests");
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var output = new WorkspacePath(Path.Combine(root.Value, "verified.esp"));
        var proposal = new WorkspacePath(Path.Combine(root.Value, "verified.npc-proposal.json"));
        try
        {
            var (runner, response, _) = CreateRunner();
            var applyExit = await runner.RunAsync(CommandLine.Parse(["npc", "patch", "--game", "skyrimse", "--input-plugin", source.Value,
                "--output", output.Value, "--form-id", "0x00000800", "--sex", "female", "--expected-sha256", Hash(source).Value,
                "--proposal", proposal.Value, "--apply", "--json"]), CancellationToken.None);
            Assert(applyExit == CommandExitCode.Success && File.Exists(output.Value) && File.Exists(proposal.Value), "fixture proposal was not applied.");
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
            var (verifyRunner, verifyResponse, _) = CreateRunner(pluginVerifyService: new PluginVerifyService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var verifyExit = await verifyRunner.RunAsync(CommandLine.Parse(["plugin", "verify", "--game", "skyrimse", "--before", source.Value,
                "--after", output.Value, "--proposal", proposal.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(verifyResponse.ToString());
            Assert(verifyExit == CommandExitCode.Success && document.RootElement.GetProperty("isValid").GetBoolean(), "plugin verify did not accept the bound output.");

            var packageRoot = Path.Combine(root.Value, "relative-package");
            var packageData = Path.Combine(packageRoot, "Data");
            var packageEvidence = Path.Combine(packageRoot, "evidence");
            Directory.CreateDirectory(packageData);
            Directory.CreateDirectory(packageEvidence);
            var relativeOutput = new WorkspacePath(Path.Combine(packageData, "verified.esp"));
            var relativeProposal = new WorkspacePath(Path.Combine(packageEvidence, "npc-edit-proposal.json"));
            File.Copy(output.Value, relativeOutput.Value);
            var relativeProposalValue = new
            {
                schemaVersion = 1,
                artifactKind = "existing-npc-edit-proposal",
                edition = "skyrimse",
                inputPlugin = source.Value,
                outputPlugin = "Data/verified.esp",
                targetFormId = "0x00000800",
                inputSha256 = Hash(source).Value,
                changes = new[] { new { field = "Sex", before = "male", after = "female" } },
                preservedFields = new[] { "ACBS", "DATA" }
            };
            await File.WriteAllTextAsync(relativeProposal.Value, JsonSerializer.Serialize(relativeProposalValue));
            var (relativeRunner, relativeResponse, _) = CreateRunner(
                pluginVerifyService: new PluginVerifyService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var relativeExit = await relativeRunner.RunAsync(CommandLine.Parse([
                "plugin", "verify", "--game", "skyrimse", "--before", source.Value,
                "--after", relativeOutput.Value, "--proposal", relativeProposal.Value, "--json"
            ]), CancellationToken.None);
            using var relativeDocument = JsonDocument.Parse(relativeResponse.ToString());
            Assert(relativeExit == CommandExitCode.Success &&
                   relativeDocument.RootElement.GetProperty("isValid").GetBoolean(),
                "plugin verify did not accept a safe package-relative existing-NPC output binding.");

            var traversalProposalValue = new
            {
                schemaVersion = 1,
                artifactKind = "existing-npc-edit-proposal",
                edition = "skyrimse",
                inputPlugin = source.Value,
                outputPlugin = "../Data/verified.esp",
                targetFormId = "0x00000800",
                inputSha256 = Hash(source).Value,
                changes = new[] { new { field = "Sex", before = "male", after = "female" } },
                preservedFields = new[] { "ACBS", "DATA" }
            };
            await File.WriteAllTextAsync(relativeProposal.Value, JsonSerializer.Serialize(traversalProposalValue));
            var (traversalRunner, traversalResponse, _) = CreateRunner(
                pluginVerifyService: new PluginVerifyService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var traversalExit = await traversalRunner.RunAsync(CommandLine.Parse([
                "plugin", "verify", "--game", "skyrimse", "--before", source.Value,
                "--after", relativeOutput.Value, "--proposal", relativeProposal.Value, "--json"
            ]), CancellationToken.None);
            using var traversalDocument = JsonDocument.Parse(traversalResponse.ToString());
            Assert(traversalExit == CommandExitCode.ValidationFailure &&
                   !traversalDocument.RootElement.GetProperty("isValid").GetBoolean(),
                "plugin verify accepted traversal in a package-relative existing-NPC output binding.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestExistingNpcGameplayPackage()
    {
        var parent = NewMutationOutput("existing-npc-gameplay-package");
        DeleteDirectory(parent.Value);
        Directory.CreateDirectory(parent.Value);
        var packageRoot = new WorkspacePath(Path.Combine(parent.Value, "package"));
        var source = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\gate3-fixtures\\stats\\Data\\M3StatsSSE.esp");
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var service = new ExistingNpcEditService(
            new NpcOverrideService(policy, new WorkspacePath("K:\\ExampleWorkspace")),
            new PackageVerifyService(new PackageManifestReader(
                policy, new WorkspacePath("K:\\ExampleWorkspace"))),
            policy,
            new WorkspacePath("K:\\ExampleWorkspace"));
        try
        {
            var (runner, output, error) = CreateRunner(existingNpcEditService: service);
            var exit = await runner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", source.Value, "--input-sha256", Hash(source).Value,
                "--npc", "0x00000800", "--output-root", packageRoot.Value,
                "--plugin", "GameplayEdit.esp", "--output-kind", "source-mastered-override", "--level", "18",
                "--magicka-offset", "5", "--stamina-offset", "6", "--health-offset", "7",
                "--calc-min", "4", "--calc-max", "60",
                "--speed-multiplier", "110", "--disposition", "12", "--bleedout", "25",
                "--player-health", "500", "--player-magicka", "450", "--player-stamina", "400",
                "--skill-values", "onehanded=91,archery=92",
                "--skill-offsets", "onehanded=11,archery=12",
                "--far-model-distance", "1234.5", "--geared-weapons", "7",
                "--set-flag", "essential,protected", "--clear-flag", "unique", "--json"
            ]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            var changes = document.RootElement.GetProperty("changes")
                .EnumerateArray()
                .ToDictionary(
                    item => item.GetProperty("field").GetString()!,
                    item => item.GetProperty("after").GetString()!,
                    StringComparer.Ordinal);
            using var verificationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(packageRoot.Value, "evidence", "npc-edit-verification.json")));
            var verification = verificationDocument.RootElement;
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   document.RootElement.GetProperty("completed").GetBoolean() &&
                   changes.Count == 21 && changes["Level"] == "18" &&
                   changes["MagickaOffset"] == "5" && changes["StaminaOffset"] == "6" &&
                   changes["HealthOffset"] == "7" && changes["CalcMinLevel"] == "4" &&
                   changes["CalcMaxLevel"] == "60" && changes["SpeedMultiplier"] == "110" &&
                   changes["DispositionBase"] == "12" && changes["BleedoutOverride"] == "25" &&
                   changes["PlayerHealth"] == "500" && changes["PlayerMagicka"] == "450" &&
                   changes["PlayerStamina"] == "400" && changes["SkillValue:onehanded"] == "91" &&
                   changes["SkillValue:archery"] == "92" && changes["SkillOffset:onehanded"] == "11" &&
                   changes["SkillOffset:archery"] == "12" && changes["FarAwayModelDistance"] == "1234.5" &&
                   changes["GearedUpWeapons"] == "7" && changes["Flag:essential"] == "true" &&
                   changes["Flag:protected"] == "true" && changes["Flag:unique"] == "false" &&
                   File.Exists(Path.Combine(packageRoot.Value, "Data", "GameplayEdit.esp")) &&
                   verification.GetProperty("trueOverride").GetBoolean() &&
                   verification.GetProperty("majorRecordCount").GetInt32() == 1 &&
                   verification.GetProperty("npcRecordCount").GetInt32() == 1 &&
                   verification.GetProperty("sourceOwnedTargetCount").GetInt32() == 1 &&
                   verification.GetProperty("selfOwnedTargetCount").GetInt32() == 0 &&
                   verification.GetProperty("observedMasters").EnumerateArray()
                       .Select(item => item.GetString())
                       .SequenceEqual(["M3StatsSSE.esp"]),
                "Existing-NPC gameplay package did not produce the exact source-mastered override surface.");

            var invalidFarRoot = Path.Combine(parent.Value, "invalid-far");
            var (invalidFarRunner, invalidFarOutput, invalidFarError) =
                CreateRunner(existingNpcEditService: service);
            var invalidFarExit = await invalidFarRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", source.Value, "--input-sha256", Hash(source).Value,
                "--npc", "0x00000800", "--output-root", invalidFarRoot,
                "--plugin", "InvalidFar.esp", "--output-kind", "source-mastered-override", "--far-model-distance", "NaN", "--json"
            ]), CancellationToken.None);
            Assert(invalidFarExit == CommandExitCode.ValidationFailure &&
                   !Directory.Exists(invalidFarRoot) &&
                   invalidFarError.ToString().Length == 0 &&
                   invalidFarOutput.ToString().Contains(
                       "far-away-model-distance-out-of-range", StringComparison.Ordinal),
                "Existing-NPC stats accepted a non-finite DNAM far-away-model distance.");

            var masteredSource = new WorkspacePath(Path.Combine(parent.Value, "MasteredStats.esp"));
            SkyrimTestPluginFactory.CreateSourceWithMaster(source, masteredSource);
            var masteredPackage = new WorkspacePath(Path.Combine(parent.Value, "mastered-package"));
            var (masteredRunner, masteredOutput, masteredError) =
                CreateRunner(existingNpcEditService: service);
            var masteredExit = await masteredRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", masteredSource.Value,
                "--input-sha256", Hash(masteredSource).Value,
                "--npc", "0x00000800", "--output-root", masteredPackage.Value,
                "--plugin", "MasteredEdit.esp", "--output-kind", "source-mastered-override", "--level", "19", "--json"
            ]), CancellationToken.None);
            Assert(masteredExit == CommandExitCode.Success && masteredError.ToString().Length == 0,
                $"Master-chain fixture package was refused: {masteredOutput}{masteredError}");
            using var masteredDocument = JsonDocument.Parse(masteredOutput.ToString());
            using var masteredVerificationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(masteredPackage.Value, "evidence", "npc-edit-verification.json")));
            var masteredVerification = masteredVerificationDocument.RootElement;
            Assert(masteredExit == CommandExitCode.Success && masteredError.ToString().Length == 0 &&
                   masteredDocument.RootElement.GetProperty("completed").GetBoolean() &&
                   masteredVerification.GetProperty("observedMasters").EnumerateArray()
                       .Select(item => item.GetString())
                       .SequenceEqual(["Skyrim.esm", "MasteredStats.esp"]) &&
                   masteredVerification.GetProperty("majorRecordCount").GetInt32() == 1 &&
                   masteredVerification.GetProperty("sourceOwnedTargetCount").GetInt32() == 1 &&
                   masteredVerification.GetProperty("selfOwnedTargetCount").GetInt32() == 0,
                "Existing-NPC override did not preserve the source master chain before adding its source master.");

            var (refusedRunner, _, refusedError) = CreateRunner(existingNpcEditService: service);
            var refused = await refusedRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", source.Value, "--input-sha256", Hash(source).Value,
                "--npc", "0x00000800", "--output-root", Path.Combine(parent.Value, "refused"),
                "--plugin", "Refused.esp", "--output-kind", "source-mastered-override", "--height", "1.1", "--json"
            ]), CancellationToken.None);
            using var refusedDocument = JsonDocument.Parse(refusedError.ToString());
            Assert(refused == CommandExitCode.UsageError &&
                   refusedDocument.RootElement.GetProperty("message").GetString()!.Contains("--height", StringComparison.Ordinal),
                "Existing-NPC CLI silently accepted an appearance-affecting option.");
        }
        finally
        {
            DeleteDirectory(parent.Value);
        }
    }

    private static async Task TestExistingNpcIdentityPackage()
    {
        var parent = NewMutationOutput("existing-npc-identity-package");
        DeleteDirectory(parent.Value);
        Directory.CreateDirectory(parent.Value);
        var template = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\gate3-fixtures\\stats\\Data\\M3StatsSSE.esp");
        var source = new WorkspacePath(Path.Combine(parent.Value, "IdentitySource.esp"));
        var packageRoot = new WorkspacePath(Path.Combine(parent.Value, "package"));
        SkyrimTestPluginFactory.CreateIdentitySource(template, source);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var service = new ExistingNpcEditService(
            new NpcOverrideService(policy, new WorkspacePath("K:\\ExampleWorkspace")),
            new PackageVerifyService(new PackageManifestReader(
                policy, new WorkspacePath("K:\\ExampleWorkspace"))),
            policy,
            new WorkspacePath("K:\\ExampleWorkspace"));
        try
        {
            var sourceHash = Hash(source);
            var inspection = await service.InspectAsync(
                GameEdition.SkyrimSpecialEdition,
                source,
                sourceHash,
                new FormId(0x00000800),
                CancellationToken.None);
            var sourcePlugin = new PluginName("IdentitySource.esp");
            Assert(inspection.Available && inspection.Snapshot is { } snapshot &&
                   snapshot.EditorId == new EditorId("IdentitySourceNpc") &&
                   snapshot.Name == new NpcName("Identity Source NPC") &&
                   snapshot.ShortName == "Source short" &&
                   snapshot.Archetype.Race == new FormReference(sourcePlugin, new FormId(0x900)) &&
                   snapshot.Archetype.Voice == new FormReference(sourcePlugin, new FormId(0x902)) &&
                   snapshot.Archetype.Class == new FormReference(sourcePlugin, new FormId(0x903)) &&
                   snapshot.Archetype.CombatStyle is null,
                "Identity fixture did not reopen its exact full/short names and archetype references.");

            var (runner, output, error) = CreateRunner(existingNpcEditService: service);
            var exit = await runner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", source.Value, "--input-sha256", sourceHash.Value,
                "--npc", "0x00000800", "--output-root", packageRoot.Value,
                "--plugin", "IdentityOverride.esp", "--output-kind", "source-mastered-override",
                "--editor-id", "IdentityEditedNpc",
                "--name", "Identity Edited NPC",
                "--short-name", "Edited short",
                "--race", "IdentitySource.esp|0x00000901",
                "--voice", "none",
                "--class", "IdentitySource.esp|0x00000903",
                "--combat-style", "IdentitySource.esp|0x00000904",
                "--json"
            ]), CancellationToken.None);
            using var result = JsonDocument.Parse(output.ToString());
            var changes = result.RootElement.GetProperty("changes")
                .EnumerateArray()
                .ToDictionary(
                    item => item.GetProperty("field").GetString()!,
                    item => item.GetProperty("after").GetString()!,
                    StringComparer.Ordinal);
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   result.RootElement.GetProperty("completed").GetBoolean() &&
                   changes.Count == 6 &&
                   changes["EditorID"] == "IdentityEditedNpc" &&
                   changes["Name"] == "Identity Edited NPC" &&
                   changes["ShortName"] == "Edited short" &&
                   changes["Race"] == "IdentitySource.esp|0x00000901" &&
                   changes["Voice"] == "none" &&
                   changes["CombatStyle"] == "IdentitySource.esp|0x00000904",
                $"Identity CLI did not retain the exact changed-field surface: {output} {error}");

            using var verificationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(packageRoot.Value, "evidence", "npc-edit-verification.json")));
            var verification = verificationDocument.RootElement;
            Assert(verification.GetProperty("majorRecordCount").GetInt32() == 1 &&
                   verification.GetProperty("sourceOwnedTargetCount").GetInt32() == 1 &&
                   verification.GetProperty("selfOwnedTargetCount").GetInt32() == 0 &&
                   verification.GetProperty("observedChanges").GetArrayLength() == 6 &&
                   verification.GetProperty("observedMasters").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(["IdentitySource.esp"]),
                "Independent identity read-back did not prove the exact source-owned override.");

            var mismatchRoot = Path.Combine(parent.Value, "type-mismatch");
            var (mismatchRunner, mismatchOutput, mismatchError) =
                CreateRunner(existingNpcEditService: service);
            var mismatchExit = await mismatchRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", source.Value, "--input-sha256", sourceHash.Value,
                "--npc", "0x00000800", "--output-root", mismatchRoot,
                "--plugin", "TypeMismatch.esp", "--output-kind", "source-mastered-override",
                "--race", "IdentitySource.esp|0x00000902", "--json"
            ]), CancellationToken.None);
            Assert(mismatchExit == CommandExitCode.ValidationFailure &&
                   mismatchError.ToString().Length == 0 &&
                   !Directory.Exists(mismatchRoot) &&
                   mismatchOutput.ToString().Contains(
                       "archetype-record-type-mismatch", StringComparison.Ordinal),
                "Existing-NPC identity accepted a Voice Type record as a Race.");
        }
        finally
        {
            DeleteDirectory(parent.Value);
        }
    }

    private static async Task TestExistingNpcCollectionPackage()
    {
        var parent = NewMutationOutput("existing-npc-collection-package");
        DeleteDirectory(parent.Value);
        Directory.CreateDirectory(parent.Value);
        var template = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\gate3-fixtures\\stats\\Data\\M3StatsSSE.esp");
        var source = new WorkspacePath(Path.Combine(parent.Value, "M3StatsSSE.esp"));
        var packageRoot = new WorkspacePath(Path.Combine(parent.Value, "package"));
        SkyrimTestPluginFactory.CreateCollectionSource(template, source);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var overrideService = new NpcOverrideService(
            policy, new WorkspacePath("K:\\ExampleWorkspace"));
        var service = new ExistingNpcEditService(
            overrideService,
            new PackageVerifyService(new PackageManifestReader(
                policy, new WorkspacePath("K:\\ExampleWorkspace"))),
            policy,
            new WorkspacePath("K:\\ExampleWorkspace"));

        var skyrim = new PluginName("Skyrim.esm");
        var keyword = new FormReference(skyrim, new FormId(0x00013794));
        var faction = new FormReference(skyrim, new FormId(0x0005A1A4));
        var item = new FormReference(skyrim, new FormId(0x0000000F));
        var outfit = new FormReference(skyrim, new FormId(0x0001697B));
        var perk = new FormReference(skyrim, new FormId(0x00058F6A));
        var effect = new FormReference(skyrim, new FormId(0x00012FCD));
        try
        {
            var sourceHash = Hash(source);
            var inspection = await service.InspectAsync(
                GameEdition.SkyrimSpecialEdition,
                source,
                sourceHash,
                new FormId(0x00000800),
                CancellationToken.None);
            Assert(inspection.Available && inspection.InputSha256 == sourceHash &&
                   inspection.Snapshot is { } baseline &&
                   baseline.Keywords.Keywords.SequenceEqual([keyword]) &&
                   baseline.Factions.Factions.SequenceEqual([new NpcFactionEntry(faction, -1)]) &&
                   baseline.Inventory.Items.SequenceEqual([new NpcInventoryEntry(item, 5)]) &&
                   baseline.Outfits.DefaultOutfit == outfit &&
                   baseline.Outfits.SleepingOutfit is null &&
                   baseline.Perks.Perks.SequenceEqual([new NpcPerkEntry(perk, 1)]) &&
                   baseline.ActorEffects.ActorEffects.SequenceEqual([effect]),
                "Hash-bound existing-NPC inspection did not retain the exact source-mastered collections.");

            var localKeyword = new FormReference(skyrim, new FormId(0x00000901));
            var localFaction = new FormReference(skyrim, new FormId(0x00000902));
            var localItem = new FormReference(skyrim, new FormId(0x00000903));
            var localOutfit = new FormReference(skyrim, new FormId(0x00000904));
            var localPerk = new FormReference(skyrim, new FormId(0x00000905));
            var localEffect = new FormReference(skyrim, new FormId(0x00000906));
            var overridePatch = new NpcOverridePatch(
                null,
                null,
                null,
                new NpcKeywordPatch(
                    new NpcKeywordListPatch([keyword, localKeyword], [], [])),
                new NpcFactionPatch(
                    [new NpcFactionEntry(faction, -2), new NpcFactionEntry(localFaction, 3)],
                    [], [], []),
                new NpcInventoryPatch(
                    [new NpcInventoryEntry(item, -7), new NpcInventoryEntry(localItem, 12)],
                    [], [], []),
                new NpcOutfitPatch(
                    OptionalFormReference.Set(localOutfit), OptionalFormReference.Clear()),
                new NpcPerkPatch(
                    [new NpcPerkEntry(perk, 2), new NpcPerkEntry(localPerk, 3)],
                    [], [], []),
                new NpcActorEffectPatch([effect, localEffect], [], []));
            var request = new ExistingNpcEditRequest(
                GameEdition.SkyrimSpecialEdition,
                source,
                sourceHash,
                new FormId(0x00000800),
                packageRoot,
                new PluginName("CollectionOverride.esp"),
                null,
                null,
                Keywords: overridePatch.Keywords,
                Factions: overridePatch.Factions,
                Inventory: overridePatch.Inventory,
                Outfits: overridePatch.Outfits,
                Perks: overridePatch.Perks,
                ActorEffects: overridePatch.ActorEffects);

            var directRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                source,
                sourceHash,
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(parent.Value, "direct-proposal.json")),
                new WorkspacePath(Path.Combine(parent.Value, "DirectCollectionOverride.esp")),
                overridePatch);
            var directProposal = await overrideService.AnalyzeAsync(
                directRequest, CancellationToken.None);
            Assert(directProposal.IsApplicable && directProposal.ProposalSha256 is not null,
                "Direct collection proposal did not persist its exact typed intent.");
            var drifted = await overrideService.ApplyAsync(
                directRequest with
                {
                    Patch = overridePatch with
                    {
                        ActorEffects = new NpcActorEffectPatch([effect], [], [])
                    }
                },
                directProposal,
                CancellationToken.None);
            Assert(!drifted.Applied && !File.Exists(directRequest.OutputPlugin.Value) &&
                   drifted.Diagnostics.Any(diagnostic =>
                       diagnostic.Code == "npc-override-proposal-request-mismatch"),
                "Proposal binding accepted collection intent that drifted after review.");

            static ImmutableArray<T> Recreate<T>(ImmutableArray<T> values) =>
                ImmutableArray.CreateRange(values.Select(value => value));
            var equivalentPatch = overridePatch with
            {
                Keywords = new NpcKeywordPatch(new NpcKeywordListPatch(
                    Recreate(overridePatch.Keywords!.Keywords.Replace!.Value), [], [])),
                Factions = new NpcFactionPatch(
                    Recreate(overridePatch.Factions!.Replace!.Value), [], [], []),
                Inventory = new NpcInventoryPatch(
                    Recreate(overridePatch.Inventory!.Replace!.Value), [], [], []),
                Perks = new NpcPerkPatch(
                    Recreate(overridePatch.Perks!.Replace!.Value), [], [], []),
                ActorEffects = new NpcActorEffectPatch(
                    Recreate(overridePatch.ActorEffects!.Replace!.Value), [], [])
            };
            var equivalent = await overrideService.ApplyAsync(
                directRequest with { Patch = equivalentPatch },
                directProposal,
                CancellationToken.None);
            Assert(equivalent.Applied && equivalent.Verification?.IsValid == true,
                "Proposal binding rejected structurally identical typed collection intent after reconstruction.");

            var review = await service.ReviewAsync(request, CancellationToken.None);
            Assert(review.Applicable && review.Changes.Select(change => change.Field)
                       .SequenceEqual(["Keywords", "Factions", "Inventory", "DefaultOutfit", "Perks", "ActorEffects"]),
                "Existing-NPC collection review did not expose the exact ordered change surface.");
            var (runner, output, error) = CreateRunner(existingNpcEditService: service);
            var exit = await runner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", source.Value, "--input-sha256", sourceHash.Value,
                "--npc", "0x00000800", "--output-root", packageRoot.Value,
                "--plugin", "CollectionOverride.esp", "--output-kind", "source-mastered-override",
                "--keywords", $"{keyword},{localKeyword}",
                "--factions", $"{faction}=-2,{localFaction}=3",
                "--inventory", $"{item}=-7,{localItem}=12",
                "--default-outfit", localOutfit.ToString(), "--sleep-outfit", "none",
                "--perks", $"{perk}=2,{localPerk}=3",
                "--actor-effects", $"{effect},{localEffect}", "--json"
            ]), CancellationToken.None);
            using var resultDocument = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   resultDocument.RootElement.GetProperty("completed").GetBoolean() &&
                   resultDocument.RootElement.GetProperty("changes").EnumerateArray()
                       .Select(change => change.GetProperty("field").GetString())
                       .SequenceEqual(review.Changes.Select(change => change.Field)) &&
                   File.Exists(Path.Combine(packageRoot.Value, "Data", "CollectionOverride.esp")),
                $"Existing-NPC collection CLI did not retain the reviewed verified override: {output} {error}");

            using var verificationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(packageRoot.Value, "evidence", "npc-edit-verification.json")));
            var verification = verificationDocument.RootElement;
            Assert(verification.GetProperty("majorRecordCount").GetInt32() == 1 &&
                   verification.GetProperty("npcRecordCount").GetInt32() == 1 &&
                   verification.GetProperty("sourceOwnedTargetCount").GetInt32() == 1 &&
                   verification.GetProperty("selfOwnedTargetCount").GetInt32() == 0 &&
                   verification.GetProperty("observedMasters").EnumerateArray()
                       .Select(value => value.GetString())
                       .SequenceEqual(["Skyrim.esm", "M3StatsSSE.esp"]) &&
                   verification.GetProperty("observedChanges").GetArrayLength() == 6,
                "Independent collection read-back did not prove the exact source-owned override surface.");

            var staleInspection = await service.InspectAsync(
                GameEdition.SkyrimSpecialEdition,
                source,
                new Sha256Hash(new string('0', 64)),
                new FormId(0x00000800),
                CancellationToken.None);
            Assert(!staleInspection.Available && staleInspection.Snapshot is null &&
                   staleInspection.Diagnostics.Any(diagnostic =>
                       diagnostic.Code == "npc-override-source-hash-mismatch"),
                "Existing-NPC inspection retained a snapshot after source-hash drift.");
        }
        finally
        {
            DeleteDirectory(parent.Value);
        }
    }

    private static async Task TestPreviewAnimationList()
    {
        var root = NewPresetOutput("preview-animation-list");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "animations.json"));
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"animations\":[" +
            "{\"id\":\"walk\",\"name\":\"Walk Forward\",\"path\":\"animations/MT/Neutral/walk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":10,\"fps\":30,\"roles\":[\"Core\",\"MT\"],\"stateAxes\":\"Forward\"}," +
            "{\"id\":\"attack\",\"name\":\"Power Attack\",\"path\":\"animations/Weapon/attack.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":12,\"fps\":30,\"additive\":true,\"role\":\"Weapon\",\"femaleOnly\":true,\"firstPersonOnly\":true,\"behaviorGraph\":true}," +
            "{\"id\":\"talk\",\"name\":\"Talk Gesture\",\"path\":\"animations/Dialogue/talk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":8,\"fps\":24,\"role\":\"Idle\",\"category\":\"Talk\"}," +
            "{\"id\":\"sneak\",\"name\":\"Sneak\",\"path\":\"animations/MT/Sneak/sneak.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":9,\"fps\":30,\"role\":\"MT\",\"firstPersonOnly\":false}]}");
        try
        {
            var (runner, output, error) = CreateRunner();
            var maleExit = await runner.RunAsync(CommandLine.Parse(["animation", "list", "--edition", "fallout4",
                "--manifest", manifest.Value, "--json"]), CancellationToken.None);
            using var male = JsonDocument.Parse(output.ToString());
            var maleArtifact = male.RootElement.GetProperty("artifact");
            Assert(maleExit == CommandExitCode.Success && error.ToString().Length == 0 &&
                maleArtifact.GetProperty("totalCount").GetInt32() == 4 &&
                maleArtifact.GetProperty("visibleCount").GetInt32() == 3 &&
                maleArtifact.GetProperty("items")[0].GetProperty("id").GetString() == "walk" &&
                maleArtifact.GetProperty("items")[0].GetProperty("folder").GetString() == "MT/Neutral" &&
                maleArtifact.GetProperty("items")[0].GetProperty("stateAxes").GetString() == "Forward" &&
                !maleArtifact.GetProperty("items")[0].GetProperty("requiresFemale").GetBoolean(),
                "Male/default animation discovery did not apply gender and perspective filters.");

            output.GetStringBuilder().Clear();
            var filteredExit = await runner.RunAsync(CommandLine.Parse(["animation", "list", "--game", "fallout4",
                "--manifest", manifest.Value, "--female", "true", "--first-person", "true",
                "--filter", "talk gesture", "--json"]), CancellationToken.None);
            using var filtered = JsonDocument.Parse(output.ToString());
            var filteredArtifact = filtered.RootElement.GetProperty("artifact");
            var filteredItem = filteredArtifact.GetProperty("items")[0];
            Assert(filteredExit == CommandExitCode.Success && filteredArtifact.GetProperty("visibleCount").GetInt32() == 1 &&
                filteredItem.GetProperty("id").GetString() == "talk" &&
                !filteredItem.GetProperty("fromBehaviorGraph").GetBoolean() &&
                filteredItem.GetProperty("category").GetString() == "Talk",
                "Animation category/filter metadata did not preserve the upstream IDLE/dialogue distinction.");

            output.GetStringBuilder().Clear();
            var badFilter = await runner.RunAsync(CommandLine.Parse(["animation", "list", "--edition", "fallout4",
                "--manifest", manifest.Value, "--filter", " bad", "--json"]), CancellationToken.None);
            Assert(badFilter == CommandExitCode.UsageError && output.ToString().Length == 0,
                "Animation list accepted an untrimmed filter.");

            var protectedManifest = await runner.RunAsync(CommandLine.Parse(["animation", "list", "--edition", "fallout4",
                "--manifest", "F:\\ExampleGame\\animation-list.json", "--json"]), CancellationToken.None);
            Assert(protectedManifest == CommandExitCode.SecurityRefusal &&
                output.ToString().Contains("protected", StringComparison.OrdinalIgnoreCase),
                "Animation list did not refuse a protected manifest root.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPreviewAnimationTree()
    {
        var root = NewPresetOutput("preview-animation-tree");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var manifest = new WorkspacePath(Path.Combine(root.Value, "animations.json"));
        await File.WriteAllTextAsync(manifest.Value, "{\"schemaVersion\":1,\"edition\":\"fallout4\",\"animations\":[" +
            "{\"id\":\"walk\",\"name\":\"Walk Forward\",\"path\":\"animations/MT/Neutral/walk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":10,\"fps\":30,\"roles\":[\"Core\",\"MT\"]}," +
            "{\"id\":\"attack\",\"name\":\"Power Attack\",\"path\":\"animations/Weapon/attack.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":12,\"fps\":30,\"additive\":true,\"role\":\"Weapon\",\"femaleOnly\":true}," +
            "{\"id\":\"talk\",\"name\":\"Talk Gesture\",\"path\":\"animations/Dialogue/talk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":8,\"fps\":24,\"role\":\"Idle\",\"category\":\"Talk\"}," +
            "{\"id\":\"sneak\",\"name\":\"Sneak\",\"path\":\"animations/MT/Sneak/sneak.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":9,\"fps\":30,\"role\":\"MT\"}]}");
        try
        {
            var (runner, output, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["animation", "tree", "--edition", "fallout4",
                "--manifest", manifest.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            var artifact = document.RootElement.GetProperty("artifact");
            var groups = artifact.GetProperty("groups");
            var mt = groups.EnumerateArray().First(item => item.GetProperty("kind").GetString() == "role" &&
                item.GetProperty("name").GetString() == "Locomotion (MT)");
            var folderRoot = mt.GetProperty("children").EnumerateArray().First(item =>
                item.GetProperty("name").GetString() == "MT");
            var folder = folderRoot.GetProperty("children").EnumerateArray().First(item =>
                item.GetProperty("name").GetString() == "Neutral");
            var leaf = folder.GetProperty("leaves")[0];
            var gestures = groups.EnumerateArray().First(item => item.GetProperty("kind").GetString() == "gestures");
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                artifact.GetProperty("visibleCount").GetInt32() == 3 &&
                leaf.GetProperty("clip").GetProperty("id").GetString() == "walk" &&
                gestures.GetProperty("children")[0].GetProperty("name").GetString() == "Talk" &&
                gestures.GetProperty("children")[0].GetProperty("leaves")[0].GetProperty("clip").GetProperty("id").GetString() == "talk",
                "Animation tree did not preserve role/folder and IDLE category hierarchy.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPluginSurfaceAudit()
    {
        var fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp");
        var (runner, response, error) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", fixture.Value, "--after", fixture.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(response.ToString());
        Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
               document.RootElement.GetProperty("isValid").GetBoolean() &&
               document.RootElement.GetProperty("beforeRecordCount").GetInt32() ==
               document.RootElement.GetProperty("afterRecordCount").GetInt32() &&
               document.RootElement.GetProperty("changes").GetArrayLength() == 0,
             "plugin audit did not produce a stable no-change surface for the copied fixture.");

        string topologyFixtureRoot = Path.Combine(
            ActorwrightWorkspace.ResolveRoot().Value, "tests", "fixtures", "skyrim-plugin-topology");
        var topologyBroken = new WorkspacePath(Path.Combine(
            topologyFixtureRoot, SyntheticSkyrimPluginTopology.OrphanFileName));
        var topologyRepaired = new WorkspacePath(Path.Combine(
            topologyFixtureRoot, SyntheticSkyrimPluginTopology.RepairedFileName));
        SyntheticTopologyLayout topologyLayout = SyntheticSkyrimPluginTopology.LocateTopology(
            File.ReadAllBytes(topologyRepaired.Value));
        SyntheticTopologyLayout orphanTopologyLayout = SyntheticSkyrimPluginTopology.LocateTopology(
            File.ReadAllBytes(topologyBroken.Value));
        string topologyCellFormId = $"0x{topologyLayout.CellFormId:X8}";
        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var topologyExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", topologyRepaired.Value, "--after", topologyBroken.Value, "--json"]),
            CancellationToken.None);
        using var topologyDocument = JsonDocument.Parse(response.ToString());
        var topologyDiagnostic = topologyDocument.RootElement.GetProperty("diagnostics")
            .EnumerateArray().Single(item => item.GetProperty("code").GetString() == "orphan-cell-children");
        var topologyMessage = topologyDiagnostic.GetProperty("message").GetString() ?? string.Empty;
        Assert(topologyExit == CommandExitCode.ValidationFailure &&
               topologyMessage.Contains("after", StringComparison.Ordinal) &&
               topologyMessage.Contains(topologyBroken.Value, StringComparison.Ordinal) &&
               topologyMessage.Contains($"0x{orphanTopologyLayout.CellChildrenGroupOffset:X8}", StringComparison.Ordinal) &&
               topologyMessage.Contains("type=6", StringComparison.Ordinal) &&
               topologyMessage.Contains(topologyCellFormId, StringComparison.Ordinal) &&
               topologyMessage.Contains($"CELL(type=0) / block(label={topologyLayout.BlockLabel},type=2) / sub-block(label={topologyLayout.SubBlockLabel},type=3) / cell-children(label={topologyCellFormId},type=6)", StringComparison.Ordinal) &&
               topologyMessage.Contains($"expected preceding CELL {topologyCellFormId}", StringComparison.Ordinal) &&
               topologyMessage.Contains("observed preceding sibling <none>", StringComparison.Ordinal) &&
               !topologyMessage.Contains("Unexpected GRUP", StringComparison.Ordinal),
            "plugin audit did not preserve the coded orphan-cell-children context.");

        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var topologyStableExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", topologyRepaired.Value, "--after", topologyRepaired.Value, "--json"]),
            CancellationToken.None);
        using var topologyStableDocument = JsonDocument.Parse(response.ToString());
        Assert(topologyStableExit == CommandExitCode.Success &&
               topologyStableDocument.RootElement.GetProperty("beforeRecordCount").GetInt32() == 3 &&
               topologyStableDocument.RootElement.GetProperty("afterRecordCount").GetInt32() == 3 &&
               topologyStableDocument.RootElement.GetProperty("changes").GetArrayLength() == 0 &&
               topologyStableDocument.RootElement.GetProperty("diagnostics").GetArrayLength() == 0,
            "The repaired synthetic topology fixture did not produce a stable three-record audit.");

        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var directAudit = new PluginSurfaceAuditService(new BethesdaPluginReader(), policy,
            new WorkspacePath("K:\\ExampleWorkspace"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => directAudit.AuditAsync(
            new PluginSurfaceAuditRequest(GameEdition.SkyrimSpecialEdition,
                topologyRepaired, topologyRepaired), cancelled.Token).AsTask());

        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var refused = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", "F:\\ExampleGame\\Data\\M2FixtureSSE.esp", "--after", fixture.Value, "--json"]), CancellationToken.None);
        using var refusedDocument = JsonDocument.Parse(response.ToString());
        Assert(refused == CommandExitCode.SecurityRefusal &&
               refusedDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "before-outside-lab"),
            "plugin audit did not refuse an input outside the K-only lab.");

        var fo4Fixture = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp");
        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var fo4Exit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "fallout4",
            "--before", fo4Fixture.Value, "--after", fo4Fixture.Value, "--json"]), CancellationToken.None);
        using var fo4Document = JsonDocument.Parse(response.ToString());
        Assert(fo4Exit == CommandExitCode.Success && fo4Document.RootElement.GetProperty("edition").GetString() == "fallout4" &&
               fo4Document.RootElement.GetProperty("changes").GetArrayLength() == 0,
            "plugin audit did not accept the copied Fallout 4 fixture.");

        var rawRoot = NewPresetOutput("plugin-surface-raw-digest");
        Directory.CreateDirectory(rawRoot.Value);
        var rawChangedDirectory = Path.Combine(rawRoot.Value, "after");
        Directory.CreateDirectory(rawChangedDirectory);
        var rawChanged = new WorkspacePath(Path.Combine(
            rawChangedDirectory,
            Path.GetFileName(fixture.Value)));
        var rawBytes = await File.ReadAllBytesAsync(fixture.Value);
        var npcGroupOffset = FindAscii(rawBytes, "NPC_");
        var npcOffset = FindAscii(rawBytes, "NPC_", npcGroupOffset + 4);
        Assert(npcOffset >= 0 && npcOffset + 22 < rawBytes.Length,
            "plugin surface fixture did not contain a complete NPC record header.");
        File.Copy(fixture.Value, rawChanged.Value, overwrite: true);
        rawBytes[npcOffset + 20] ^= 0x01;
        await File.WriteAllBytesAsync(rawChanged.Value, rawBytes);
        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var rawExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", fixture.Value, "--after", rawChanged.Value, "--json"]), CancellationToken.None);
        using var rawDocument = JsonDocument.Parse(response.ToString());
        Assert(rawExit == CommandExitCode.Success &&
               rawDocument.RootElement.GetProperty("changedRecordCount").GetInt32() == 1 &&
               rawDocument.RootElement.GetProperty("changes").EnumerateArray()
                   .Any(item => item.GetProperty("changeKind").GetString() == "changed"),
            "plugin audit did not detect a raw record change outside the typed summary fields.");

        var providerRoot = NewM2LoadOrderRoot($"surface-providers-sse-{Environment.ProcessId}");
        var providerBase = new WorkspacePath(Path.Combine(providerRoot.Value, "Base.esp"));
        var providerOverride = new WorkspacePath(Path.Combine(providerRoot.Value, "Override.esp"));
        var providerManifest = new WorkspacePath(Path.Combine(providerRoot.Value, "load-order.json"));
        File.Copy(fixture.Value, providerBase.Value, overwrite: true);
        File.Copy(fixture.Value, providerOverride.Value, overwrite: true);
        await File.WriteAllTextAsync(providerManifest.Value, """
            {
              "schemaVersion": 1,
              "edition": "skyrimse",
              "plugins": [
                { "name": "Base.esp", "order": 0, "enabled": true },
                { "name": "Override.esp", "order": 1, "enabled": true }
              ]
            }
            """);
        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var providerExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", providerBase.Value, "--after", providerOverride.Value,
            "--plugins-root", providerRoot.Value, "--load-order", providerManifest.Value, "--json"]), CancellationToken.None);
        using var providerDocument = JsonDocument.Parse(response.ToString());
        var providerResolutions = providerDocument.RootElement.GetProperty("providerResolutions")
            .EnumerateArray()
            .Where(item => item.GetProperty("formId").GetString() == "0x00000800")
            .ToArray();
        Assert(providerExit == CommandExitCode.Success && providerResolutions.Length == 2 &&
               providerResolutions.All(item =>
                   item.GetProperty("overrideChain").GetArrayLength() == 1),
            "plugin audit collapsed unrelated owner-qualified records that share a local FormID.");

        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var incompleteProviderExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", providerBase.Value, "--after", providerOverride.Value,
            "--plugins-root", providerRoot.Value, "--json"]), CancellationToken.None);
        using var incompleteProviderError = JsonDocument.Parse(error.ToString());
        Assert(incompleteProviderExit == CommandExitCode.UsageError &&
               incompleteProviderError.RootElement.GetProperty("code").GetString() == "usage-error",
            "plugin audit accepted an incomplete provider-resolution option pair.");

        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var outsideProviderExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "skyrimse",
            "--before", providerBase.Value, "--after", providerOverride.Value,
            "--plugins-root", "F:\\ExampleGame\\Data", "--load-order", providerManifest.Value, "--json"]), CancellationToken.None);
        using var outsideProviderDocument = JsonDocument.Parse(response.ToString());
        Assert(outsideProviderExit == CommandExitCode.SecurityRefusal &&
               outsideProviderDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                   .Any(item => item.GetProperty("code").GetString() == "load-order-plugins-outside-lab"),
            "plugin audit did not refuse an explicit provider root outside the K-only lab.");

        var fo4ProviderRoot = NewM2LoadOrderRoot($"surface-providers-fo4-{Environment.ProcessId}");
        var fo4ProviderBase = new WorkspacePath(Path.Combine(fo4ProviderRoot.Value, "Base.esp"));
        var fo4ProviderOverride = new WorkspacePath(Path.Combine(fo4ProviderRoot.Value, "Override.esp"));
        var fo4ProviderManifest = new WorkspacePath(Path.Combine(fo4ProviderRoot.Value, "load-order.json"));
        File.Copy(fo4Fixture.Value, fo4ProviderBase.Value, overwrite: true);
        File.Copy(fo4Fixture.Value, fo4ProviderOverride.Value, overwrite: true);
        await File.WriteAllTextAsync(fo4ProviderManifest.Value, """
            {
              "schemaVersion": 1,
              "edition": "fallout4",
              "plugins": [
                { "name": "Base.esp", "order": 0, "enabled": true },
                { "name": "Override.esp", "order": 1, "enabled": true }
              ]
            }
            """);
        response.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var fo4ProviderExit = await runner.RunAsync(CommandLine.Parse(["plugin", "audit", "--game", "fallout4",
            "--before", fo4ProviderBase.Value, "--after", fo4ProviderOverride.Value,
            "--data-root", fo4ProviderRoot.Value, "--loadorder", fo4ProviderManifest.Value, "--json"]), CancellationToken.None);
        using var fo4ProviderDocument = JsonDocument.Parse(response.ToString());
        var fo4ProviderResolutions = fo4ProviderDocument.RootElement.GetProperty("providerResolutions")
            .EnumerateArray()
            .Where(item => item.GetProperty("formId").GetString() == "0x00000800")
            .ToArray();
        Assert(fo4ProviderExit == CommandExitCode.Success && fo4ProviderResolutions.Length == 2 &&
               fo4ProviderResolutions.All(item =>
                   item.GetProperty("overrideChain").GetArrayLength() == 1),
            "Fallout 4 plugin audit collapsed unrelated owner-qualified records that share a local FormID.");
    }

    private static async Task TestPluginDeploy()
    {
        var root = NewPipelineOutput("plugin-deploy");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var sources = new[]
        {
            ("fallout4", new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp")),
            ("skyrimse", new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp"))
        };
        try
        {
            foreach (var (game, source) in sources)
            {
                var dataRoot = new WorkspacePath(Path.Combine(root.Value, game, "Data"));
                Directory.CreateDirectory(dataRoot.Value);
                var sourceHash = Hash(source);
                var (runner, output, _) = CreateRunner(pluginDeployService:
                    new PluginDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
                var deploy = await runner.RunAsync(CommandLine.Parse(["plugin", "deploy", "--game", game,
                    "--plugin", source.Value, "--data-root", dataRoot.Value,
                    "--expected-sha256", sourceHash.Value, "--json"]), CancellationToken.None);
                using var deployedResponse = JsonDocument.Parse(output.ToString());
                var destination = Path.Combine(dataRoot.Value, Path.GetFileName(source.Value));
                Assert(deploy == CommandExitCode.Success && deployedResponse.RootElement.GetProperty("deployed").GetBoolean() &&
                       File.Exists(destination) && Hash(new WorkspacePath(destination)) == sourceHash,
                    $"{game} plugin deployment did not install the hash-bound plugin.");

                output.GetStringBuilder().Clear();
                var idempotent = await runner.RunAsync(CommandLine.Parse(["plugin", "deploy", "--game", game,
                    "--plugin", source.Value, "--data-root", dataRoot.Value,
                    "--expected-sha256", sourceHash.Value, "--json"]), CancellationToken.None);
                using var idempotentResponse = JsonDocument.Parse(output.ToString());
                Assert(idempotent == CommandExitCode.Success && !idempotentResponse.RootElement.GetProperty("deployed").GetBoolean() &&
                       idempotentResponse.RootElement.GetProperty("alreadyPresent").GetBoolean(),
                    $"{game} identical plugin deployment was not idempotent.");

                await File.WriteAllBytesAsync(destination, [1, 2, 3]);
                output.GetStringBuilder().Clear();
                var conflict = await runner.RunAsync(CommandLine.Parse(["plugin", "deploy", "--game", game,
                    "--plugin", source.Value, "--data-root", dataRoot.Value,
                    "--expected-sha256", sourceHash.Value, "--json"]), CancellationToken.None);
                Assert(conflict == CommandExitCode.ValidationFailure &&
                       output.ToString().Contains("plugin-deploy-destination-conflict", StringComparison.Ordinal) &&
                       (await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 1, 2, 3 }),
                    $"{game} conflicting plugin deployment overwrote the destination.");
            }

            var outsideRoot = new WorkspacePath("F:\\ExampleGame\\Data");
            var (outsideRunner, outsideOutput, _) = CreateRunner(pluginDeployService:
                new PluginDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var sourceOutsideCheck = sources[0].Item2;
            var outsideExit = await outsideRunner.RunAsync(CommandLine.Parse(["plugin", "deploy", "--game", "fallout4",
                "--plugin", sourceOutsideCheck.Value, "--data-root", outsideRoot.Value,
                "--expected-sha256", Hash(sourceOutsideCheck).Value, "--json"]), CancellationToken.None);
            Assert(outsideExit == CommandExitCode.SecurityRefusal &&
                   outsideOutput.ToString().Contains("plugin-deploy-data-outside-lab", StringComparison.Ordinal),
                "Plugin deployment accepted the protected live Data root.");

            var wrongRoot = new WorkspacePath(Path.Combine(root.Value, "wrong-hash"));
            Directory.CreateDirectory(wrongRoot.Value);
            var (wrongRunner, wrongOutput, _) = CreateRunner(pluginDeployService:
                new PluginDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var wrongHashExit = await wrongRunner.RunAsync(CommandLine.Parse(["plugin", "deploy", "--game", "fallout4",
                "--plugin", sources[0].Item2.Value, "--data-root", wrongRoot.Value,
                "--expected-sha256", new string('0', 64), "--json"]), CancellationToken.None);
            Assert(wrongHashExit == CommandExitCode.ValidationFailure &&
                   wrongOutput.ToString().Contains("plugin-deploy-source-hash-mismatch", StringComparison.Ordinal) &&
                   !File.Exists(Path.Combine(wrongRoot.Value, "M2FixtureFO4.esp")),
                "A stale source hash did not fail closed before deployment.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestRuntimeSmokeVerify()
    {
        var root = NewPresetOutput("runtime-smoke-verify");
        Directory.CreateDirectory(root.Value);
        var acceptance = new WorkspacePath(Path.Combine(root.Value, "acceptance.json"));
        var report = new WorkspacePath(Path.Combine(root.Value, "runtime.json"));
        var environment = new WorkspacePath(Path.Combine(root.Value, "environment.json"));
        var hash = new string('a', 64);
        File.WriteAllText(acceptance.Value, JsonSerializer.Serialize(new
        {
            packageArchiveSha256 = hash,
            status = "PASS_WITH_SCOPED_LIMITS",
            runtimeReleaseClaim = false
        }));
        File.WriteAllText(environment.Value, "{\"profile\":\"fixture\"}");
        var environmentHash = Hash(environment).Value;
        var screenshots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["face"] = "face.png",
            ["neck"] = "neck.png",
            ["body"] = "body.png",
            ["hands"] = "hands.png",
            ["eyes"] = "eyes.png",
            ["outfit"] = "outfit.png"
        };
        var validPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        foreach (var name in screenshots.Values) File.WriteAllBytes(Path.Combine(root.Value, name), validPng);
        var runtime = new
        {
            schemaVersion = "1",
            game = "fallout4",
            status = "PASS",
            controlNpcSameFrame = true,
            providerMatchesPackage = true,
            package = new { archiveSha256 = hash },
            target = new
            {
                formId = "0x800",
                plugin = "Fixture.esp",
                faceGeomProvider = "FaceGeom",
                faceTintProvider = "FaceTint",
                bodySkinProvider = "Skin",
                outfitProvider = "Outfit",
                headpartProviders = new[] { "Hair" }
            },
            control = new { formId = "0x801", plugin = "Control.esp" },
            environmentFingerprint = new { path = environment.Value, sha256 = environmentHash },
            screenshots,
            operatorData = new { name = "rational-test", capturedAt = "2026-07-18T00:00:00Z" }
        };
        // The service reads the stable wire property `operator`; keep the test
        // explicit so serializer naming cannot silently alter the contract.
        var json = JsonSerializer.Serialize(runtime).Replace("operatorData", "operator", StringComparison.Ordinal);
        File.WriteAllText(report.Value, json);
        try
        {
            var (runner, response, error) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify", "--game", "fallout4",
                "--runtime-report", report.Value, "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(response.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                   document.RootElement.GetProperty("isValid").GetBoolean() &&
                   document.RootElement.GetProperty("validatedScreenshots").GetArrayLength() == 6,
                "runtime smoke verifier did not accept the complete FO4 evidence contract.");

            File.WriteAllBytes(Path.Combine(root.Value, "hair.png"), validPng);
            var sseReport = new WorkspacePath(Path.Combine(root.Value, "runtime-sse.json"));
            var sseJson = json.Replace("\"game\":\"fallout4\"", "\"game\":\"skyrimse\"", StringComparison.Ordinal)
                .Replace("\"screenshots\":{", "\"screenshots\":{\"hair\":\"hair.png\",", StringComparison.Ordinal);
            File.WriteAllText(sseReport.Value, sseJson);
            response.GetStringBuilder().Clear();
            var allExit = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify-all",
                "--fallout4-report", report.Value, "--skyrimse-report", sseReport.Value,
                "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var allDocument = JsonDocument.Parse(response.ToString());
            Assert(allExit == CommandExitCode.Success &&
                   allDocument.RootElement.GetProperty("isValid").GetBoolean() &&
                   allDocument.RootElement.GetProperty("fallout4").GetProperty("validatedScreenshots").GetArrayLength() == 6 &&
                   allDocument.RootElement.GetProperty("skyrimSe").GetProperty("validatedScreenshots").GetArrayLength() == 7,
                "dual-game runtime smoke verifier did not aggregate both typed evidence reports.");

            response.GetStringBuilder().Clear();
            File.WriteAllText(acceptance.Value, JsonSerializer.Serialize(new
            {
                packageArchiveSha256 = hash,
                status = "FAIL",
                runtimeReleaseClaim = false
            }));
            var failedAcceptance = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify", "--game", "fallout4",
                "--runtime-report", report.Value, "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var failedAcceptanceDocument = JsonDocument.Parse(response.ToString());
            Assert(failedAcceptance == CommandExitCode.ValidationFailure &&
                   failedAcceptanceDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() == "package-acceptance-status-invalid"),
                "runtime smoke verifier accepted a failed package acceptance report.");
            File.WriteAllText(acceptance.Value, JsonSerializer.Serialize(new
            {
                packageArchiveSha256 = hash,
                status = "PASS_WITH_SCOPED_LIMITS",
                runtimeReleaseClaim = false
            }));

            response.GetStringBuilder().Clear();
            var sameIdentityJson = json.Replace(
                "\"control\":{\"formId\":\"0x801\",\"plugin\":\"Control.esp\"}",
                "\"control\":{\"formId\":\"0x800\",\"plugin\":\"Fixture.esp\"}",
                StringComparison.Ordinal);
            File.WriteAllText(report.Value, sameIdentityJson);
            var sameIdentity = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify", "--game", "fallout4",
                "--runtime-report", report.Value, "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var sameIdentityDocument = JsonDocument.Parse(response.ToString());
            Assert(sameIdentity == CommandExitCode.ValidationFailure &&
                   sameIdentityDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() == "runtime-control-target-same"),
                "runtime smoke verifier accepted the target NPC as its own control NPC.");
            File.WriteAllText(report.Value, json);

            response.GetStringBuilder().Clear();
            File.WriteAllBytes(Path.Combine(root.Value, "face.png"), [1, 2, 3]);
            var invalidImage = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify", "--game", "fallout4",
                "--runtime-report", report.Value, "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var invalidImageDocument = JsonDocument.Parse(response.ToString());
            Assert(invalidImage == CommandExitCode.ValidationFailure &&
                   invalidImageDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() == "runtime-screenshot-format-invalid"),
                "runtime smoke verifier accepted a non-image screenshot file.");

            response.GetStringBuilder().Clear();
            var refused = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify", "--game", "fallout4",
                "--runtime-report", "F:\\ExampleGame\\runtime.json", "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var refusedDocument = JsonDocument.Parse(response.ToString());
            Assert(refused == CommandExitCode.SecurityRefusal &&
                   refusedDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() == "runtime-report-outside-lab"),
                "runtime smoke verifier did not refuse a protected-root report.");

            response.GetStringBuilder().Clear();
            File.WriteAllText(report.Value, "[]");
            var malformed = await runner.RunAsync(CommandLine.Parse(["runtime", "smoke", "verify", "--game", "fallout4",
                "--runtime-report", report.Value, "--package-acceptance", acceptance.Value, "--json"]), CancellationToken.None);
            using var malformedDocument = JsonDocument.Parse(response.ToString());
            Assert(malformed == CommandExitCode.ValidationFailure &&
                   malformedDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
                       .Any(item => item.GetProperty("code").GetString() == "runtime-report-root-invalid"),
                "runtime smoke verifier did not fail closed on a non-object report.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceGenZeroShapes()
    {
        var (runner, output, _) = CreateRunner();
        var manifest = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\zero-shapes.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "verify", "--edition", "fallout4", "--manifest", manifest, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.ValidationFailure, "Zero-shape FaceGen verification did not fail.");
        Assert(document.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("code").GetString() == "facegen-zero-shapes"), "Zero-shape diagnostic was missing.");
    }

    private static async Task TestFaceGenPoisonShapes()
    {
        var (runner, output, _) = CreateRunner();
        var manifest = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\poison-shapes.json";
        var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "verify", "--edition", "skyrimse", "--manifest", manifest, "--strict-shapes", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.ValidationFailure, "Poison-shape FaceGen verification did not fail.");
        Assert(document.RootElement.GetProperty("diagnostics").EnumerateArray().Count(item => item.GetProperty("code").GetString() == "facegen-poison-shape") == 2, "Hair/collider poison diagnostics changed.");
    }

    private static Task TestFaceGenProviderPathRules()
    {
        Assert(FaceGenProviderPathRules.ToFaceGenLocalFormId(new FormId(0x01012345)) == 0x012345,
            "Full-plugin FaceGen local FormID rule changed.");
        Assert(FaceGenProviderPathRules.ToFaceGenLocalFormId(new FormId(0xFE032800)) == 0x800,
            "ESL FaceGen local FormID rule did not strip the light slot.");
        return Task.CompletedTask;
    }

    private static Task TestFaceGenProviderContext()
    {
        var context = new FaceGenProviderNpcContext(NpcSex.Female, new FormId(0x801),
            [new FormId(0x811), new FormId(0x812)]);
        Assert(context.Sex == NpcSex.Female && context.RaceFormId == new FormId(0x801) &&
            context.HeadPartFormIds.SequenceEqual([new FormId(0x811), new FormId(0x812)]),
            "FaceGen provider context did not preserve typed winning-NPC identity.");
        return Task.CompletedTask;
    }

    private static async Task TestFaceTintProviderBinding()
    {
        var root = NewPresetOutput("facetint-provider-binding");
        DeleteDirectory(root.Value);
        var dataRoot = new WorkspacePath(Path.Combine(root.Value, "Data"));
        Directory.CreateDirectory(dataRoot.Value);
        var plugin = new WorkspacePath(Path.Combine(dataRoot.Value, "P18Binding.esp"));
        try
        {
            var mod = new Fo4.Fallout4Mod(new ModKey("P18Binding", ModType.Plugin), Fo4.Fallout4Release.Fallout4);
            var npcKey = new FormKey(mod.ModKey, 0x800);
            var raceKey = new FormKey(mod.ModKey, 0x801);
            var colorKey = new FormKey(mod.ModKey, 0x802);
            mod.Colors.Add(new Fo4.ColorRecord(colorKey, Fo4.Fallout4Release.Fallout4)
            {
                EditorID = "P18SkinToneColor",
                Data = new Fo4.ColorData { Color = Color.FromArgb(255, 64, 32, 16) }
            });
            var femaleHead = new Fo4.HeadData
            {
                TintLayers =
                [
                    new Fo4.TintGroup
                    {
                        CategoryIndex = 12,
                        Options =
                        [
                            new Fo4.TintTemplateOption
                            {
                                Index = 42,
                                Slot = Fo4.TintTemplateOption.TintSlot.SkinTone,
                                Default = 0.75F,
                                TemplateColors =
                                [
                                    new Fo4.TintTemplateColor
                                    {
                                        TemplateIndex = 3,
                                        Alpha = 0.8F,
                                        BlendOperation = Fo4.BlendOperation.Multiply,
                                        Color = new FormLink<Fo4.IColorRecordGetter>(colorKey)
                                    }
                                ]
                            }
                        ]
                    }
                ]
            };
            mod.Races.Add(new Fo4.Race(raceKey, Fo4.Fallout4Release.Fallout4)
            {
                EditorID = "P18TintRace",
                NumberOfTintsInList = 1,
                HeadData = new GenderedItem<Fo4.HeadData?>(new Fo4.HeadData(), femaleHead)
            });
            mod.Npcs.Add(new Fo4.Npc(npcKey, Fo4.Fallout4Release.Fallout4)
            {
                EditorID = "P18TintNpc",
                Race = new FormLink<Fo4.IRaceGetter>(raceKey)
            });
            mod.WriteToBinary(new FilePath(plugin.Value));

            var reader = new BethesdaFaceTintProviderBindingReader();
            var result = await reader.ReadAsync(new FaceTintProviderBindingRequest(
                GameEdition.Fallout4, dataRoot, new FormId(0x800), new PluginName("P18Binding.esp"),
                NpcSex.Female, [new PluginName("P18Binding.esp")]), CancellationToken.None);
            var binding = result.Binding;
            var template = binding is null ? null : binding.Groups.Single().Options.Single().TemplateColors.Single();
            Assert(result.Resolved && result.Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error) &&
                   binding is not null && binding.RaceFormId == "0x00000801" && binding.RacePlugin == "P18Binding.esp" &&
                   binding.Sex == NpcSex.Female && binding.Groups.Length == 1 && template is not null &&
                   binding.Groups[0].Options[0].Index == 42 && template.Color.DataKind == FaceTintColorDataKind.Rgb &&
                   template.Color.Red == 64 && template.Color.Green == 32 && template.Color.Blue == 16,
                "FO4 RACE/CLFM provider binding did not preserve the typed copied-plugin contract.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceGenPack()
    {
        var root = NewPresetOutput("facegen-pack-cli");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        try
        {
            var artifact = new FaceGenPackArtifact(
                "1", "facegen-pack", "fallout4", "0x00000800", "Fixture.esp", root.Value,
                [new FaceGenPackFileArtifact(
                    "Data/Meshes/Actors/Character/FaceGenData/FaceGeom/Fixture.esp/00000800.nif",
                    "Data/Meshes/Actors/Character/FaceGenData/FaceGeom/Fixture.esp/00000800.nif",
                    FaceGenPackArchiveRole.Main, 12, new Sha256Hash(new string('A', 64)))], true, false);
            var result = new FaceGenPackResult(true, root,
                artifact, new Sha256Hash(new string('B', 64)), ImmutableArray<Diagnostic>.Empty);
            var (runner, output, error) = CreateRunner(faceGenPackService: new FakeFaceGenPackService(result));
            var exit = await runner.RunAsync(CommandLine.Parse(["facegen", "pack", "--edition", "fallout4",
                "--data-root", Path.Combine(root.Value, "Data"), "--output-root", Path.Combine(root.Value, "package"),
                "--npc", "0x800", "--plugins", "Fixture.esp", "--anchor-plugin", "Fixture.esp", "--json"]),
                CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && error.ToString().Length == 0 &&
                document.RootElement.GetProperty("written").GetBoolean() &&
                document.RootElement.GetProperty("artifact").GetProperty("artifactKind").GetString() == "facegen-pack" &&
                document.RootElement.GetProperty("artifact").GetProperty("files").GetArrayLength() == 1 &&
                document.RootElement.GetProperty("artifact").GetProperty("noWriteToSource").GetBoolean() &&
                !document.RootElement.GetProperty("artifact").GetProperty("runtimeProof").GetBoolean(),
                "FaceGen pack CLI did not expose the typed package boundary.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceGenDeploy()
    {
        var root = NewPipelineOutput("facegen-deploy");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var cases = new[] { ("fallout4", "DeployFO4.esp", "Textures/Actors/Character/FaceCustomization/DeployFO4.esp/00000800_d.dds"),
            ("skyrimse", "DeploySSE.esp", "Textures/Actors/Character/FaceGenData/FaceTint/DeploySSE.esp/00000800.dds") };
        try
        {
            foreach (var (game, pluginName, textureRelative) in cases)
            {
                var gameRoot = new WorkspacePath(Path.Combine(root.Value, game));
                var packageRoot = new WorkspacePath(Path.Combine(gameRoot.Value, "package"));
                var dataRoot = new WorkspacePath(Path.Combine(gameRoot.Value, "Data"));
                Directory.CreateDirectory(Path.Combine(packageRoot.Value, "Data"));
                Directory.CreateDirectory(dataRoot.Value);
                var pluginRelative = $"Data/{pluginName}";
                var geomRelative = $"Data/Meshes/Actors/Character/FaceGenData/FaceGeom/{pluginName}/00000800.nif";
                var packageFiles = new (string Relative, byte[] Bytes, FaceGenPackArchiveRole Role)[]
                {
                    (pluginRelative, [1, 2, 3, 4], FaceGenPackArchiveRole.Main),
                    (geomRelative, [5, 6, 7], FaceGenPackArchiveRole.Main),
                    ($"Data/{textureRelative}", [8, 9], FaceGenPackArchiveRole.Textures)
                };
                var entries = ImmutableArray.CreateBuilder<FaceGenPackFileArtifact>();
                foreach (var item in packageFiles)
                {
                    var path = Path.Combine(packageRoot.Value, item.Relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, item.Bytes);
                    entries.Add(new FaceGenPackFileArtifact(item.Relative, item.Relative, item.Role,
                        item.Bytes.Length, new Sha256Hash(Convert.ToHexString(SHA256.HashData(item.Bytes)))));
                }
                var artifact = new FaceGenPackArtifact("1", "facegen-pack", game, "0x00000800", pluginName,
                    packageRoot.Value, entries.ToImmutable(), true, false);
                var manifest = new WorkspacePath(Path.Combine(packageRoot.Value, "facegen-pack.json"));
                await File.WriteAllTextAsync(manifest.Value, JsonSerializer.Serialize(artifact, jsonOptions));
                var (runner, output, _) = CreateRunner(faceGenDeployService:
                    new FaceGenDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
                var deploy = await runner.RunAsync(CommandLine.Parse(["facegen", "deploy", "--game", game,
                    "--package", manifest.Value, "--data-root", dataRoot.Value, "--json"]), CancellationToken.None);
                using var response = JsonDocument.Parse(output.ToString());
                Assert(deploy == CommandExitCode.Success && response.RootElement.GetProperty("deployed").GetBoolean() &&
                    response.RootElement.GetProperty("files").GetArrayLength() == packageFiles.Length,
                    $"{game} FaceGen deployment did not promote every package file.");
                foreach (var item in packageFiles)
                {
                    var destination = Path.Combine(dataRoot.Value, item.Relative["Data/".Length..].Replace('/', Path.DirectorySeparatorChar));
                    Assert(File.Exists(destination) && (await File.ReadAllBytesAsync(destination)).SequenceEqual(item.Bytes),
                        $"{game} FaceGen destination bytes changed for {item.Relative}.");
                }
                output.GetStringBuilder().Clear();
                var idempotent = await runner.RunAsync(CommandLine.Parse(["facegen", "deploy", "--game", game,
                    "--package", manifest.Value, "--data-root", dataRoot.Value, "--json"]), CancellationToken.None);
                using var idempotentResponse = JsonDocument.Parse(output.ToString());
                Assert(idempotent == CommandExitCode.Success && idempotentResponse.RootElement.GetProperty("alreadyPresent").GetBoolean(),
                    $"{game} identical FaceGen deployment was not idempotent.");
                var conflictPath = Path.Combine(dataRoot.Value, textureRelative.Replace('/', Path.DirectorySeparatorChar));
                await File.WriteAllBytesAsync(conflictPath, [99]);
                output.GetStringBuilder().Clear();
                var conflict = await runner.RunAsync(CommandLine.Parse(["facegen", "deploy", "--game", game,
                    "--package", manifest.Value, "--data-root", dataRoot.Value, "--json"]), CancellationToken.None);
                Assert(conflict == CommandExitCode.ValidationFailure &&
                    output.ToString().Contains("facegen-deploy-destination-conflict", StringComparison.Ordinal) &&
                    (await File.ReadAllBytesAsync(conflictPath)).SequenceEqual(new byte[] { 99 }),
                    $"{game} conflicting FaceGen deployment overwrote a destination.");
            }

            var outsideManifest = new WorkspacePath(Path.Combine(root.Value, "fallout4", "package", "facegen-pack.json"));
            var (outsideRunner, outsideOutput, _) = CreateRunner(faceGenDeployService:
                new FaceGenDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var outside = await outsideRunner.RunAsync(CommandLine.Parse(["facegen", "deploy", "--game", "fallout4",
                "--package", outsideManifest.Value, "--data-root", "F:\\ExampleGame\\Data", "--json"]), CancellationToken.None);
            Assert(outside == CommandExitCode.SecurityRefusal &&
                outsideOutput.ToString().Contains("facegen-deploy-data-outside-lab", StringComparison.Ordinal),
                "FaceGen deployment accepted the protected live Data root.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestPackageCommands()
    {
        var root = NewPipelineOutput($"package-commands-{Environment.ProcessId}");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var source = new WorkspacePath(Path.Combine(root.Value, "source"));
        var outputRoot = new WorkspacePath(Path.Combine(root.Value, "built"));
        var archivePath = new WorkspacePath(Path.Combine(root.Value, "PackageFixture-runtime-test.zip"));
        var archiveRepeatPath = new WorkspacePath(Path.Combine(root.Value, "PackageFixture-runtime-test-repeat.zip"));
        Directory.CreateDirectory(source.Value);
        var dataRoot = Path.Combine(source.Value, "Data");
        Directory.CreateDirectory(dataRoot);
        var plugin = new WorkspacePath(Path.Combine(dataRoot, "PackageFixture.esp"));
        await File.WriteAllBytesAsync(plugin.Value, [1, 2, 3, 4]);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(plugin.Value))).ToLowerInvariant();
        var manifest = new WorkspacePath(Path.Combine(source.Value, "npcmanager-package.json"));
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            edition = "fallout4",
            presetFormat = "looksmenu",
            sourcePreset = "preset.json",
            sourcePresetSha256 = new string('a', 64),
            sourcePlugin = "source.esp",
            sourcePluginSha256 = new string('b', 64),
            outputPlugin = "PackageFixture.esp",
            targetFormId = "0x00000800",
            artifacts = new[] { new { kind = "plugin", relativePath = "Data/PackageFixture.esp", byteLength = 4, sha256 = hash } }
        });
        await File.WriteAllTextAsync(manifest.Value, manifestJson);
        try
        {
            var (runner, output, error) = CreateRunner();
            var inspect = await runner.RunAsync(CommandLine.Parse(["package", "inspect", "--manifest", manifest.Value, "--json"]), CancellationToken.None);
            using var inspectDocument = JsonDocument.Parse(output.ToString());
            Assert(inspect == CommandExitCode.Success && error.ToString().Length == 0 &&
                inspectDocument.RootElement.GetProperty("valid").GetBoolean() &&
                inspectDocument.RootElement.GetProperty("identity").GetProperty("files").GetArrayLength() == 1,
                "Package inspect did not expose the typed manifest identity.");

            output.GetStringBuilder().Clear();
            var verify = await runner.RunAsync(CommandLine.Parse(["package", "verify", "--manifest", manifest.Value, "--json"]), CancellationToken.None);
            using var verifyDocument = JsonDocument.Parse(output.ToString());
            Assert(verify == CommandExitCode.Success && verifyDocument.RootElement.GetProperty("verified").GetBoolean() &&
                verifyDocument.RootElement.GetProperty("artifact").GetProperty("noUndeclaredFiles").GetBoolean() &&
                verifyDocument.RootElement.GetProperty("artifact").GetProperty("files")[0].GetProperty("matches").GetBoolean(),
                "Package verify did not independently confirm the declared artifact.");

            output.GetStringBuilder().Clear();
            var archive = await runner.RunAsync(CommandLine.Parse(["package", "archive",
                "--source-root", source.Value, "--output", archivePath.Value, "--json"]), CancellationToken.None);
            using var archiveDocument = JsonDocument.Parse(output.ToString());
            var archiveArtifact = archiveDocument.RootElement.GetProperty("artifact");
            Assert(archive == CommandExitCode.Success && File.Exists(archivePath.Value) &&
                archiveDocument.RootElement.GetProperty("written").GetBoolean() &&
                archiveArtifact.GetProperty("independentlyReopened").GetBoolean() &&
                archiveArtifact.GetProperty("forwardSlashEntries").GetBoolean() &&
                archiveArtifact.GetProperty("noWrapperDirectory").GetBoolean() &&
                archiveArtifact.GetProperty("noWriteToSource").GetBoolean() &&
                !archiveArtifact.GetProperty("runtimeProof").GetBoolean() &&
                archiveArtifact.GetProperty("entries").GetArrayLength() == 4,
                "Package archive did not expose typed Manager-authored readback evidence.");
            using (var zip = ZipFile.OpenRead(archivePath.Value))
            {
                var names = zip.Entries.Select(entry => entry.FullName)
                    .Order(StringComparer.Ordinal).ToArray();
                Assert(names.SequenceEqual(new[]
                    {
                        "BUILD_INFO.txt",
                        "PackageFixture.esp",
                        "README-NPCMANAGER-RUNTIME-TEST.txt",
                        "RUNTIME-TEST-INSTRUCTIONS.md"
                    }, StringComparer.Ordinal) &&
                    names.All(name => !name.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) &&
                                      !name.Contains('\\')),
                    "Package archive did not use the exact no-wrapper install layout.");
                var pluginEntry = zip.GetEntry("PackageFixture.esp");
                Assert(pluginEntry is not null &&
                    (await ReadZipEntryAsync(pluginEntry)).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                    "Package archive changed the verified plugin bytes.");
                var runtimeEntry = zip.GetEntry("RUNTIME-TEST-INSTRUCTIONS.md");
                var runtimeText = runtimeEntry is null
                    ? string.Empty
                    : Encoding.UTF8.GetString(await ReadZipEntryAsync(runtimeEntry));
                var untouchedCapture = runtimeText.IndexOf(
                    "Capture the untouched actor before running any diagnostic batch",
                    StringComparison.Ordinal);
                var mutatingDiagnostic = runtimeText.IndexOf(
                    "throwaway save only",
                    StringComparison.Ordinal);
                Assert(
                    untouchedCapture >= 0 &&
                    mutatingDiagnostic > untouchedCapture &&
                    runtimeText.Contains(
                        "setnpcweight diagnostic can change neck geometry",
                        StringComparison.Ordinal),
                    "Package archive did not put untouched runtime capture before the mutating weight diagnostic.");
            }

            output.GetStringBuilder().Clear();
            var archiveRepeat = await runner.RunAsync(CommandLine.Parse(["package", "archive",
                "--source-root", source.Value, "--output", archiveRepeatPath.Value, "--json"]), CancellationToken.None);
            var archiveBytes = await File.ReadAllBytesAsync(archivePath.Value);
            var archiveRepeatBytes = await File.ReadAllBytesAsync(archiveRepeatPath.Value);
            Assert(archiveRepeat == CommandExitCode.Success &&
                archiveBytes.SequenceEqual(archiveRepeatBytes),
                "Package archive was not deterministic for the same verified source.");

            var nakedSource = new WorkspacePath(Path.Combine(root.Value, "source-naked"));
            var nakedDataRoot = Path.Combine(nakedSource.Value, "Data");
            var nakedPlugin = Path.Combine(nakedDataRoot, "NakedFixture.esp");
            var nakedProposal = Path.Combine(nakedSource.Value, "evidence", "npc-creation-proposal.json");
            var nakedArchive = Path.Combine(root.Value, "NakedFixture-runtime-test.zip");
            Directory.CreateDirectory(nakedDataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(nakedProposal)!);
            await File.WriteAllBytesAsync(nakedPlugin, [5, 6, 7, 8]);
            await File.WriteAllTextAsync(nakedProposal, JsonSerializer.Serialize(new
            {
                schemaVersion = "3",
                artifactKind = "skyrim-npc-creation-proposal",
                references = new { defaultOutfit = (string?)null }
            }));
            var nakedPluginBytes = await File.ReadAllBytesAsync(nakedPlugin);
            var nakedProposalBytes = await File.ReadAllBytesAsync(nakedProposal);
            var nakedManifest = Path.Combine(nakedSource.Value, "npcmanager-package.json");
            await File.WriteAllTextAsync(nakedManifest, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                edition = "skyrimse",
                presetFormat = "blank-npc-creation-proposal",
                sourcePreset = "evidence/npc-creation-proposal.json",
                sourcePresetSha256 = Convert.ToHexString(SHA256.HashData(nakedProposalBytes)),
                sourcePlugin = "source.esp",
                sourcePluginSha256 = new string('b', 64),
                outputPlugin = "NakedFixture.esp",
                targetFormId = "0x00000800",
                artifacts = new[]
                {
                    new
                    {
                        kind = "plugin",
                        relativePath = "Data/NakedFixture.esp",
                        byteLength = nakedPluginBytes.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(nakedPluginBytes))
                    },
                    new
                    {
                        kind = "npc-creation-proposal",
                        relativePath = "evidence/npc-creation-proposal.json",
                        byteLength = nakedProposalBytes.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(nakedProposalBytes))
                    }
                }
            }));
            output.GetStringBuilder().Clear();
            var nakedArchiveResult = await runner.RunAsync(CommandLine.Parse(["package", "archive",
                "--source-root", nakedSource.Value, "--output", nakedArchive, "--json"]), CancellationToken.None);
            using (var zip = ZipFile.OpenRead(nakedArchive))
            {
                var readmeEntry = zip.GetEntry("README-NPCMANAGER-RUNTIME-TEST.txt");
                var runtimeEntry = zip.GetEntry("RUNTIME-TEST-INSTRUCTIONS.md");
                var readmeText = readmeEntry is null
                    ? string.Empty
                    : Encoding.UTF8.GetString(await ReadZipEntryAsync(readmeEntry));
                var runtimeText = runtimeEntry is null
                    ? string.Empty
                    : Encoding.UTF8.GetString(await ReadZipEntryAsync(runtimeEntry));
                Assert(
                    nakedArchiveResult == CommandExitCode.Success &&
                    readmeText.Contains("intentionally has no default outfit", StringComparison.Ordinal) &&
                    runtimeText.Contains("untouched naked actor", StringComparison.Ordinal) &&
                    runtimeText.Contains(
                        "One clear normal-light screenshot is sufficient to reject a visible neck seam",
                        StringComparison.Ordinal) &&
                    !runtimeText.Contains("outfit in normal and alternate lighting", StringComparison.Ordinal) &&
                    !runtimeText.Contains("After the untouched captures", StringComparison.Ordinal),
                    "Package archive imposed dressed-candidate or optional diagnostic requirements on an intentionally naked NPC.");
            }

            await File.WriteAllTextAsync(nakedProposal, JsonSerializer.Serialize(new
            {
                schemaVersion = "3",
                artifactKind = 17,
                references = new { defaultOutfit = (string?)null }
            }));
            nakedProposalBytes = await File.ReadAllBytesAsync(nakedProposal);
            await File.WriteAllTextAsync(nakedManifest, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                edition = "skyrimse",
                presetFormat = "blank-npc-creation-proposal",
                sourcePreset = "evidence/npc-creation-proposal.json",
                sourcePresetSha256 = Convert.ToHexString(SHA256.HashData(nakedProposalBytes)),
                sourcePlugin = "source.esp",
                sourcePluginSha256 = new string('b', 64),
                outputPlugin = "NakedFixture.esp",
                targetFormId = "0x00000800",
                artifacts = new[]
                {
                    new
                    {
                        kind = "plugin",
                        relativePath = "Data/NakedFixture.esp",
                        byteLength = nakedPluginBytes.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(nakedPluginBytes))
                    },
                    new
                    {
                        kind = "npc-creation-proposal",
                        relativePath = "evidence/npc-creation-proposal.json",
                        byteLength = nakedProposalBytes.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(nakedProposalBytes))
                    }
                }
            }));
            output.GetStringBuilder().Clear();
            var invalidProfileArchive = Path.Combine(root.Value, "NakedFixture-invalid-profile.zip");
            var invalidProfileResult = await runner.RunAsync(CommandLine.Parse(["package", "archive",
                "--source-root", nakedSource.Value, "--output", invalidProfileArchive, "--json"]), CancellationToken.None);
            Assert(
                invalidProfileResult == CommandExitCode.ValidationFailure &&
                !File.Exists(invalidProfileArchive) &&
                output.ToString().Contains("package-archive-proposal-contract", StringComparison.Ordinal),
                "Package archive silently generated generic instructions from an invalid declared NPC creation proposal.");

            output.GetStringBuilder().Clear();
            var archiveOverwrite = await runner.RunAsync(CommandLine.Parse(["package", "archive",
                "--source-root", source.Value, "--output", archivePath.Value, "--json"]), CancellationToken.None);
            Assert(archiveOverwrite == CommandExitCode.ValidationFailure &&
                output.ToString().Contains("package-archive-output-exists", StringComparison.Ordinal),
                "Package archive accepted an existing output.");

            output.GetStringBuilder().Clear();
            var build = await runner.RunAsync(CommandLine.Parse(["package", "build", "--source-root", source.Value,
                "--output-root", outputRoot.Value, "--json"]), CancellationToken.None);
            Assert(build == CommandExitCode.Success && File.Exists(Path.Combine(outputRoot.Value, "npcmanager-package.json")) &&
                File.Exists(Path.Combine(outputRoot.Value, "Data", "PackageFixture.esp")),
                "Package build did not copy the verified package atomically.");

            output.GetStringBuilder().Clear();
            var tampered = Path.Combine(outputRoot.Value, "Data", "PackageFixture.esp");
            await File.WriteAllBytesAsync(tampered, [9, 9, 9, 9]);
            var rejected = await runner.RunAsync(CommandLine.Parse(["package", "verify", "--manifest",
                Path.Combine(outputRoot.Value, "npcmanager-package.json"), "--json"]), CancellationToken.None);
            Assert(rejected == CommandExitCode.ValidationFailure && output.ToString().Contains("package-artifact-hash-mismatch", StringComparison.Ordinal),
                "Package verify accepted a tampered artifact.");

            var unsupportedManifest = manifestJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifest.Value, unsupportedManifest);
            output.GetStringBuilder().Clear();
            var unsupported = await runner.RunAsync(CommandLine.Parse(["package", "inspect", "--manifest",
                manifest.Value, "--json"]), CancellationToken.None);
            Assert(unsupported == CommandExitCode.ValidationFailure &&
                output.ToString().Contains("package-manifest-schema-unsupported", StringComparison.Ordinal),
                "Package inspect accepted an unsupported manifest schema.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceTintProviderBoundBuild()
    {
        var root = NewPresetOutput("facetint-bound-contract");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var dataRoot = new WorkspacePath(Path.Combine(root.Value, "Data"));
        var outputRoot = new WorkspacePath(Path.Combine(root.Value, "Output"));
        Directory.CreateDirectory(dataRoot.Value);
        Directory.CreateDirectory(outputRoot.Value);
        var plugin = new PluginName("BoundFixture.esp");
        var npc = new FormId(0x800);
        var canonical = new AssetPath("Textures/Actors/Character/FaceCustomization/BoundFixture.esp/00000800_d.dds");
        var source = new WorkspacePath(Path.Combine(dataRoot.Value, canonical.Value.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(source.Value)!);
        await File.WriteAllTextAsync(source.Value, "bound-source");
        var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source.Value)));
        var manifest = new WorkspacePath(Path.Combine(root.Value, "manifest.json"));
        await File.WriteAllTextAsync(manifest.Value, $"{{\"schemaVersion\":\"1\",\"npcFormId\":\"{npc}\",\"layers\":[{{\"source\":\"{canonical.Value}\"}}]}}");
        var output = new WorkspacePath(Path.Combine(root.Value, "artifact.json"));
        try
        {
            var provider = new FaceGenProviderArtifact(FaceGenProviderArtifactKind.FaceCustomizationDiffuse,
                canonical, FaceGenProviderRequiredness.Required,
                [new FaceGenProviderEvidence(AssetProviderKind.Loose, "fixture", new FileInfo(source.Value).Length,
                    new Sha256Hash(sourceHash))]);
            var resolution = new FakeFaceGenProviderResolutionService(new FaceGenProviderResolutionArtifact(
                "1", "facegen-provider-resolution", "fallout4", npc.ToString(), "0x00000800", plugin.Value,
                plugin.Value, [plugin], [provider]));
            var builder = new FakeFaceTintBuildService();
            var service = new FaceTintProviderBoundBuildService(resolution, builder,
                new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
                new WorkspacePath("K:\\ExampleWorkspace"));
            var result = await service.BuildAsync(new FaceTintProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], manifest, output, outputRoot), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ProviderPath == canonical && builder.Called,
                "Provider-bound FaceTint service did not bind the canonical provider path.");

            var missingNpcManifest = new WorkspacePath(Path.Combine(root.Value, "missing-npc.json"));
            await File.WriteAllTextAsync(missingNpcManifest.Value,
                $"{{\"schemaVersion\":\"1\",\"layers\":[{{\"source\":\"{canonical.Value}\"}}]}}");
            var missingNpcBuilder = new FakeFaceTintBuildService();
            var missingNpcService = new FaceTintProviderBoundBuildService(resolution, missingNpcBuilder,
                new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
                new WorkspacePath("K:\\ExampleWorkspace"));
            var missingNpc = await missingNpcService.BuildAsync(new FaceTintProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], missingNpcManifest,
                new WorkspacePath(Path.Combine(root.Value, "missing-npc-output.json")), outputRoot),
                CancellationToken.None);
            Assert(!missingNpc.Written && missingNpc.Diagnostics.Any(item =>
                    item.Code == "facetint-bound-manifest-npc-required") && !missingNpcBuilder.Called,
                "Provider-bound FaceTint accepted a manifest without an explicit NPC identity.");

            var mismatchBuilder = new FakeFaceTintBuildService { ReturnWrongBinding = true };
            var mismatchService = new FaceTintProviderBoundBuildService(resolution, mismatchBuilder,
                new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
                new WorkspacePath("K:\\ExampleWorkspace"));
            var mismatch = await mismatchService.BuildAsync(new FaceTintProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], manifest,
                new WorkspacePath(Path.Combine(root.Value, "mismatch.json")), outputRoot), CancellationToken.None);
            Assert(!mismatch.Written && mismatch.Diagnostics.Any(item => item.Code == "facetint-bound-build-binding-mismatch"),
                "FaceTint provider/output evidence mismatch was not rejected.");

            var overlap = await service.BuildAsync(new FaceTintProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], manifest,
                new WorkspacePath(Path.Combine(root.Value, "overlap.json")), dataRoot), CancellationToken.None);
            Assert(!overlap.Written && overlap.Diagnostics.Any(item => item.Code == "facetint-bound-root-overlap") &&
                !builder.OverlapCalled, "Provider/output root overlap was not rejected before the delegated build.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestSkyrimNativeFaceTintCli()
    {
        var hash = new Sha256Hash(new string('A', 64));
        var npc = new FormReference(new PluginName("NativeFixture.esp"), new FormId(0x800));
        var race = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13746));
        var artifact = new SkyrimNativeFaceTintBuildArtifact(
            "1", "skyrim-native-facetint", npc, race, NpcSex.Female,
            512, 512, "DXT5", 10, hash, hash, hash, [], RuntimeAuthority: false);
        var service = new FakeSkyrimNativeFaceTintPipelineService(
            new SkyrimNativeFaceTintPipelineResult(true, artifact, [], [], []));
        var (runner, output, _) = CreateRunner(
            skyrimNativeFaceTintPipelineService: service);

        var dataRoot = NewPresetOutput("native-tint-cli-data");
        var outputPath = NewPresetOutput("native-tint-cli.dds");
        CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
        [
            "facegen", "build-tint-native",
            "--game", "skyrimse",
            "--data-root", dataRoot.Value,
            "--plugins", "Skyrim.esm,NativeFixture.esp",
            "--npc", npc.ToString(),
            "--race", race.ToString(),
            "--sex", "female",
            "--output", outputPath.Value,
            "--json"
        ]), CancellationToken.None);

        Assert(exit == CommandExitCode.Success && service.Request is not null,
            "The native FaceTint CLI did not invoke its typed pipeline service.");
        Assert(service.Request!.Edition == GameEdition.SkyrimSpecialEdition &&
               service.Request.DataRoot == dataRoot &&
               service.Request.PluginOrder.Select(item => item.Value)
                   .SequenceEqual(["Skyrim.esm", "NativeFixture.esp"]) &&
               service.Request.Npc == npc && service.Request.ExpectedRace == race &&
               service.Request.ExpectedSex == NpcSex.Female &&
               service.Request.OutputPath == outputPath,
            "The native FaceTint CLI changed an explicit typed input.");
        using var document = JsonDocument.Parse(output.ToString());
        JsonElement response = document.RootElement;
        Assert(response.GetProperty("written").GetBoolean() &&
               response.GetProperty("output").GetString() == outputPath.Value &&
               !response.GetProperty("artifact").GetProperty("runtimeAuthority").GetBoolean(),
            "The native FaceTint CLI did not preserve written output and non-runtime authority evidence.");
    }

    private static async Task TestNativeFaceGenBatchCli()
    {
        var plugin = new PluginName("NativeFixture.esp");
        var race = new FormReference(
            new PluginName("Skyrim.esm"), new FormId(0x13746));
        var target = new FaceGenBakeTarget(
            new FormId(0x800), plugin, plugin, [plugin], "NPCMNative",
            "Native Fixture", NpcSex.Female, race, [], 50f);
        var dataRoot = NewPresetOutput("native-batch-cli-data");
        var outputRoot = NewPresetOutput("native-batch-cli-output");
        var hash = new Sha256Hash(new string('A', 64));
        string formId = target.FormId.Value.ToString("X8");
        var artifact = new FaceGenNpcBakeArtifact(
            target,
            new WorkspacePath(Path.Combine(outputRoot.Value, "meshes", "actors",
                "character", "FaceGenData", "FaceGeom", plugin.Value,
                formId + ".nif")),
            hash, 128,
            new WorkspacePath(Path.Combine(outputRoot.Value, "textures", "actors",
                "character", "FaceGenData", "FaceTint", plugin.Value,
                formId + ".dds")),
            hash, 256, RuntimeAuthority: false);
        var outcome = new FaceGenNpcBakeResult(
            FaceGenNpcBakeStatus.Baked, target, artifact, []);
        var service = new FakeFaceGenBakeAllService(
            new FaceGenBakeAllResult(FaceGenBakeAllStatus.Succeeded,
                1, 1, 0, 0, [outcome], []));
        var (runner, output, _) = CreateRunner(
            nativeFaceGenBatchService: service);

        CommandExitCode exit = await runner.RunAsync(CommandLine.Parse(
        [
            "facegen", "bake-all-native",
            "--game", "skyrimse",
            "--data-root", dataRoot.Value,
            "--plugins", "Skyrim.esm,NativeFixture.esp",
            "--winning-plugin", plugin.Value,
            "--output-root", outputRoot.Value,
            "--json"
        ]), CancellationToken.None);

        Assert(exit == CommandExitCode.Success && service.Request is not null,
            "The native FaceGen batch CLI did not invoke its typed service.");
        Assert(service.Request!.Edition == GameEdition.SkyrimSpecialEdition &&
               service.Request.DataRoot == dataRoot &&
               service.Request.OutputDataRoot == outputRoot &&
               service.Request.WinningPlugin == plugin &&
               service.Request.PluginOrder.Select(item => item.Value)
                   .SequenceEqual(["Skyrim.esm", "NativeFixture.esp"]),
            "The native FaceGen batch CLI changed an explicit typed input.");
        using var document = JsonDocument.Parse(output.ToString());
        JsonElement response = document.RootElement;
        Assert(response.GetProperty("schemaVersion").GetString() == "1" &&
               response.GetProperty("status").GetString() == "succeeded" &&
               response.GetProperty("baked").GetInt32() == 1 &&
               response.GetProperty("progress").GetArrayLength() == 2 &&
               !response.GetProperty("runtimeAuthority").GetBoolean() &&
               !response.GetProperty("outcomes")[0].GetProperty("artifact")
                   .GetProperty("runtimeAuthority").GetBoolean(),
            "The native FaceGen batch CLI did not preserve ordered progress and static-only artifact authority.");
    }

    private static async Task TestFaceGeomProviderBoundBuild()
    {
        var root = NewPresetOutput("facegeom-bound-contract");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var dataRoot = new WorkspacePath(Path.Combine(root.Value, "Data"));
        var outputRoot = new WorkspacePath(Path.Combine(root.Value, "Output"));
        Directory.CreateDirectory(dataRoot.Value);
        Directory.CreateDirectory(outputRoot.Value);
        var plugin = new PluginName("BoundFixture.esp");
        var npc = new FormId(0x800);
        var canonical = new AssetPath("Meshes/Actors/Character/FaceGenData/FaceGeom/BoundFixture.esp/00000800.nif");
        var source = new WorkspacePath(Path.Combine(dataRoot.Value, canonical.Value.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(source.Value)!);
        await File.WriteAllTextAsync(source.Value, "bound-source");
        var sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source.Value)));
        try
        {
            var provider = new FaceGenProviderArtifact(FaceGenProviderArtifactKind.FaceGeom,
                canonical, FaceGenProviderRequiredness.Required,
                [new FaceGenProviderEvidence(AssetProviderKind.Loose, "fixture", new FileInfo(source.Value).Length,
                    new Sha256Hash(sourceHash))]);
            var resolution = new FakeFaceGenProviderResolutionService(new FaceGenProviderResolutionArtifact(
                "1", "facegen-provider-resolution", "fallout4", npc.ToString(), "0x00000800", plugin.Value,
                plugin.Value, [plugin], [provider]));
            var builder = new FakeFaceGeomBinaryBuildService();
            var service = new FaceGeomProviderBoundBuildService(resolution, builder,
                new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
                new WorkspacePath("K:\\ExampleWorkspace"));
            var result = await service.BuildAsync(new FaceGeomProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], outputRoot,
                [new FaceGeomBinaryMorph("JawOpen", 0.25f)]), CancellationToken.None);
            Assert(result.Written && result.Artifact?.ProviderPath == canonical && builder.Called,
                "Provider-bound FaceGeom service did not bind the canonical provider path.");

            var mismatchBuilder = new FakeFaceGeomBinaryBuildService { ReturnWrongBinding = true };
            var mismatchService = new FaceGeomProviderBoundBuildService(resolution, mismatchBuilder,
                new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
                new WorkspacePath("K:\\ExampleWorkspace"));
            var mismatch = await mismatchService.BuildAsync(new FaceGeomProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], outputRoot,
                [new FaceGeomBinaryMorph("JawOpen", 0.25f)]), CancellationToken.None);
            Assert(!mismatch.Written && mismatch.Diagnostics.Any(item => item.Code == "facegeom-bound-build-binding-mismatch"),
                "FaceGeom provider/output evidence mismatch was not rejected.");

            var overlap = await service.BuildAsync(new FaceGeomProviderBoundBuildRequest(
                GameEdition.Fallout4, dataRoot, npc, [plugin], dataRoot,
                [new FaceGeomBinaryMorph("JawOpen", 0.25f)]), CancellationToken.None);
            Assert(!overlap.Written && overlap.Diagnostics.Any(item => item.Code == "facegeom-bound-root-overlap") &&
                !builder.OverlapCalled, "Provider/output root overlap was not rejected before the delegated build.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestFaceGenDuplicateKeys()
    {
        var path = NewPresetOutput("facegen-duplicate.json");
        await File.WriteAllTextAsync(path.Value, "{\"edition\":\"fallout4\",\"edition\":\"skyrimse\",\"npcFormId\":\"0x800\",\"shapes\":[]}");
        try
        {
            var result = await new FaceGenService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"))
                .DiagnoseAsync(new FaceGenDiagnoseRequest(GameEdition.Fallout4, path, null), CancellationToken.None);
            Assert(result.Diagnostics.Any(item => item.Code == "facegen-manifest-duplicate-key"), "FaceGen duplicate keys were accepted.");
        }
        finally { DeleteIfExists(path.Value); }
    }

    private static async Task TestFaceGenSchemaVersion()
    {
        var path = NewPresetOutput("facegen-schema.json");
        await File.WriteAllTextAsync(path.Value, "{\"schemaVersion\":2,\"edition\":\"fallout4\",\"npcFormId\":\"0x800\",\"shapes\":[]}");
        try
        {
            var result = await new FaceGenService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"))
                .DiagnoseAsync(new FaceGenDiagnoseRequest(GameEdition.Fallout4, path, null), CancellationToken.None);
            Assert(result.Diagnostics.Any(item => item.Code == "facegen-manifest-schema-unsupported"), "Unsupported FaceGen schema was accepted.");
        }
        finally { DeleteIfExists(path.Value); }
    }

    private static async Task TestFaceGenOutsideK()
    {
        var result = await new FaceGenService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace"))
            .DiagnoseAsync(new FaceGenDiagnoseRequest(GameEdition.Fallout4, new WorkspacePath("F:\\ExampleGame\\Data\\facegen.json"), null), CancellationToken.None);
        Assert(result.Diagnostics.Any(item => item.Code == "facegen-manifest-outside-lab"), "Outside-K FaceGen manifest was not refused.");
    }

    private static async Task TestFo4BodyGen()
    {
        var outputRoot = NewBodyGenOutput("fo4");
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["bodygen", "build", "--game", "fallout4", "--plugin", "M2FixtureFO4.esp",
            "--npc", "0x00000800", "--mod-name", "BodyGenFixture.esp", "--morphs",
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json",
            "--output-root", outputRoot.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("written").GetBoolean(), "FO4 BodyGen build failed.");
        var template = Path.Combine(outputRoot.Value, "F4SE", "Plugins", "F4EE", "BodyGen", "BodyGenFixture.esp", "templates.ini");
        var morphs = Path.Combine(outputRoot.Value, "F4SE", "Plugins", "F4EE", "BodyGen", "BodyGenFixture.esp", "morphs.ini");
        Assert(File.Exists(template) && File.Exists(morphs), "FO4 BodyGen files were not written.");
        Assert((await File.ReadAllTextAsync(template)).Contains("CBBE Breast@0.25,CBBE Lower@-0.125", StringComparison.Ordinal), "FO4 template ordering or formatting changed.");
        Assert((await File.ReadAllTextAsync(morphs)).Contains("M2FixtureFO4.esp|00000800=NpcManager_00000800", StringComparison.Ordinal), "FO4 target mapping changed.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestSseBodyGen()
    {
        var outputRoot = NewBodyGenOutput("sse");
        var (runner, output, _) = CreateRunner();
        const string assignments =
            "{\"schemaVersion\":1,\"plugin\":\"M2FixtureSSE.esp\",\"npc\":\"0x00000800\"," +
            "\"editorId\":\"Gate2EmiBodyGen\",\"modName\":\"BodyGenFixture.esp\"," +
            "\"morphs\":[{\"name\":\"XPMSEAARange_dageqp\",\"value\":6}]}";
        var exit = await runner.RunAsync(CommandLine.Parse(["bodygen", "write", "--edition", "skyrimse",
            "--assignments", assignments, "--output", outputRoot.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("files")[0].GetProperty("relativePath").GetString() ==
            "meshes/actors/character/BodyGenData/M2FixtureSSE.esp/templates.ini",
            "SSE BodyGen path lost the output plugin filename or extension.");
        var template = Path.Combine(outputRoot.Value, "meshes", "actors", "character", "BodyGenData",
            "M2FixtureSSE.esp", "templates.ini");
        var morphs = Path.Combine(outputRoot.Value, "meshes", "actors", "character", "BodyGenData",
            "M2FixtureSSE.esp", "morphs.ini");
        Assert((await File.ReadAllTextAsync(template)).Contains(
                "NPCM_Gate2EmiBodyGen=XPMSEAARange_dageqp@6", StringComparison.Ordinal),
            "SSE BodyGen lost the EditorID template identity or clamped a finite keyed sum above one.");
        Assert((await File.ReadAllTextAsync(morphs)).Contains(
                "M2FixtureSSE.esp|000800=NPCM_Gate2EmiBodyGen", StringComparison.Ordinal),
            "SSE BodyGen target mapping is not the six-hex plugin-local FormID bound to the EditorID template.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestBodyGenDuplicateMorphs()
    {
        var input = NewBodyGenInput("duplicate.json", "{\"schemaVersion\":1,\"morphs\":[{\"name\":\"Breasts\",\"value\":0.1},{\"name\":\"breasts\",\"value\":0.2}]}");
        var outputRoot = NewBodyGenOutput("duplicate");
        var service = CreateBodyGenService();
        var result = await service.BuildAsync(new BodyGenBuildRequest(GameEdition.SkyrimSpecialEdition, new PluginName("M2FixtureSSE.esp"),
            new FormId(0x800), "BodyGenFixture.esp", input, outputRoot), CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "bodygen-morph-duplicate"), "Duplicate BodyGen morphs were accepted.");
        DeleteIfExists(input.Value); DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestBodyGenExistingOutput()
    {
        var outputRoot = NewBodyGenOutput("existing");
        var destination = Path.Combine(outputRoot.Value, "F4SE", "Plugins", "F4EE", "BodyGen", "BodyGenFixture.esp", "templates.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "keep");
        var result = await CreateBodyGenService().BuildAsync(new BodyGenBuildRequest(GameEdition.Fallout4, new PluginName("M2FixtureFO4.esp"),
            new FormId(0x800), "BodyGenFixture.esp", new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json"), outputRoot), CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "bodygen-output-exists"), "Existing BodyGen output was overwritten.");
        Assert(await File.ReadAllTextAsync(destination) == "keep", "Existing BodyGen output changed.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestBodyGenOutsideK()
    {
        var result = await CreateBodyGenService().BuildAsync(new BodyGenBuildRequest(GameEdition.Fallout4, new PluginName("M2FixtureFO4.esp"),
            new FormId(0x800), "BodyGenFixture.esp", new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json"),
            new WorkspacePath("F:\\ExampleGame\\Data")), CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "bodygen-output-outside-lab"), "Outside-K BodyGen output was accepted.");
        var (runner, _, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["bodygen", "build", "--game", "fallout4", "--plugin", "M2FixtureFO4.esp",
            "--npc", "0x00000800", "--mod-name", "BodyGenFixture.esp", "--morphs",
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json",
            "--output-root", "F:\\ExampleGame\\Data"]), CancellationToken.None);
        Assert(exit == CommandExitCode.SecurityRefusal, "CLI did not classify outside-K BodyGen output as a security refusal.");
    }

    private static async Task TestBodyGenPluginDelimiter()
    {
        var result = await CreateBodyGenService().BuildAsync(new BodyGenBuildRequest(GameEdition.Fallout4,
            new PluginName("Bad|Name.esp"), new FormId(0x800), "BodyGenFixture.esp",
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json"),
            NewBodyGenOutput("plugin-delimiter")), CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "bodygen-plugin-invalid"), "BodyGen plugin target delimiter was accepted.");
    }

    private static async Task TestBodyGenLoadOrderFormId()
    {
        var result = await CreateBodyGenService().BuildAsync(new BodyGenBuildRequest(GameEdition.Fallout4,
            new PluginName("M2FixtureFO4.esp"), new FormId(0x01000800), "BodyGenFixture.esp",
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json"),
            NewBodyGenOutput("load-order-form")), CancellationToken.None);
        Assert(!result.Written && result.Diagnostics.Any(item => item.Code == "bodygen-formid-load-order-unresolved"), "Unresolved BodyGen load-order byte was accepted.");
    }

    private static async Task TestBodyGenCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => CreateBodyGenService().BuildAsync(new BodyGenBuildRequest(GameEdition.Fallout4,
            new PluginName("M2FixtureFO4.esp"), new FormId(0x800), "BodyGenFixture.esp",
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m5-fixtures\\bodygen-fo4.json"),
            NewBodyGenOutput("cancel")), source.Token).AsTask());
    }

    private static async Task TestBodyGenWrite()
    {
        var root = NewMutationOutput("bodygen-write-tests");
        Directory.CreateDirectory(root.Value);
        var assignments = new WorkspacePath(Path.Combine(root.Value, "assignments.json"));
        await File.WriteAllTextAsync(assignments.Value, "{\"schemaVersion\":1,\"plugin\":\"P11FixtureFO4.esp\",\"npc\":\"0x000800\",\"modName\":\"P11BodyGen\",\"morphs\":[{\"name\":\"Calf\",\"value\":0.25}]}");
        try
        {
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["bodygen", "write", "--game", "fallout4", "--assignments", "@" + assignments.Value,
                "--output", root.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("written").GetBoolean() &&
                   document.RootElement.GetProperty("files").GetArrayLength() == 2,
                "bodygen write did not emit both typed configuration files.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestRuntimeScriptProposal()
    {
        var root = NewPipelineOutput("runtime-script-proposal");
        var appearance = new WorkspacePath(Path.Combine(root.Value, "appearance.json"));
        var outputPath = new WorkspacePath(Path.Combine(root.Value, "fo4.runtime-script-proposal.json"));
        var json = "{\"schemaVersion\":1,\"scriptName\":\"NPCM_Manolov_ApplyFO4\",\"properties\":[" +
            "{\"name\":\"IsFemale\",\"type\":\"BoolValue\",\"value\":true}," +
            "{\"name\":\"SchemaVersion\",\"type\":\"IntValue\",\"value\":7}," +
            "{\"name\":\"OvlTemplate\",\"type\":\"StringArray\",\"value\":[\"tattoo\"]}," +
            "{\"name\":\"OvlPriority\",\"type\":\"IntArray\",\"value\":[1]}," +
            "{\"name\":\"OvlRed\",\"type\":\"FloatArray\",\"value\":[0.1]}," +
            "{\"name\":\"OvlGreen\",\"type\":\"FloatArray\",\"value\":[0.2]}," +
            "{\"name\":\"OvlBlue\",\"type\":\"FloatArray\",\"value\":[0.3]}," +
            "{\"name\":\"OvlAlpha\",\"type\":\"FloatArray\",\"value\":[1]}," +
            "{\"name\":\"OvlOffsetU\",\"type\":\"FloatArray\",\"value\":[0]}," +
            "{\"name\":\"OvlOffsetV\",\"type\":\"FloatArray\",\"value\":[0]}," +
            "{\"name\":\"OvlScaleU\",\"type\":\"FloatArray\",\"value\":[1]}," +
            "{\"name\":\"OvlScaleV\",\"type\":\"FloatArray\",\"value\":[1]}," +
            "{\"name\":\"SkinTemplate\",\"type\":\"StringValue\",\"value\":\"\"}]}";
        await File.WriteAllTextAsync(appearance.Value, json);
        try
        {
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
            var (runner, output, _) = CreateRunner(runtimeScriptProposalService: new RuntimeScriptProposalService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var exit = await runner.RunAsync(CommandLine.Parse(["runtime-script", "propose", "--game", "fallout4", "--npc", "0x000800", "--appearance", "@" + appearance.Value, "--output", outputPath.Value, "--json"]), CancellationToken.None);
            using var document = JsonDocument.Parse(output.ToString());
            Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("written").GetBoolean() &&
                   !document.RootElement.GetProperty("artifact").GetProperty("binaryMutation").GetBoolean() &&
                   document.RootElement.GetProperty("artifact").GetProperty("properties").GetArrayLength() == 13,
                "runtime-script proposal did not preserve the complete typed FO4 property contract.");

            var duplicateAppearance = new WorkspacePath(Path.Combine(root.Value, "duplicate-appearance.json"));
            await File.WriteAllTextAsync(duplicateAppearance.Value, json[..^2] + ",{\"name\":\"IsFemale\",\"type\":\"BoolValue\",\"value\":false}]}");
            var (duplicateRunner, duplicateOutput, _) = CreateRunner(runtimeScriptProposalService: new RuntimeScriptProposalService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var duplicateExit = await duplicateRunner.RunAsync(CommandLine.Parse(["runtime-script", "propose", "--game", "fallout4", "--npc", "0x000800", "--appearance", "@" + duplicateAppearance.Value, "--output", Path.Combine(root.Value, "duplicate.runtime-script-proposal.json"), "--json"]), CancellationToken.None);
            Assert(duplicateExit == CommandExitCode.ValidationFailure && duplicateOutput.ToString().Contains("runtime-script-property-duplicate", StringComparison.Ordinal), "duplicate runtime-script properties were not refused without throwing.");

            var mismatch = NewPipelineOutput("runtime-script-proposal-mismatch");
            var (mismatchRunner, mismatchOutput, _) = CreateRunner(runtimeScriptProposalService: new RuntimeScriptProposalService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var mismatchExit = await mismatchRunner.RunAsync(CommandLine.Parse(["runtime-script", "propose", "--game", "skyrimse", "--npc", "0x000800", "--appearance", "@" + appearance.Value, "--output", Path.Combine(mismatch.Value, "bad.runtime-script-proposal.json"), "--json"]), CancellationToken.None);
            Assert(mismatchExit == CommandExitCode.ValidationFailure && mismatchOutput.ToString().Contains("runtime-script-game-mismatch", StringComparison.Ordinal), "runtime-script game mismatch was not refused.");
            DeleteDirectory(mismatch.Value);
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestRuntimeScriptBuild()
    {
        var root = NewPipelineOutput("runtime-script-build");
        var sourceRoot = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\02-normalized-resources\\upstream-papyrus");
        var sseSourceRoot = new WorkspacePath(Path.Combine(root.Value, "sse-product-source"));
        var sseSourceDirectory = Path.Combine(sseSourceRoot.Value, "src_sse");
        var ssePexDirectory = Path.Combine(sseSourceRoot.Value, "pex_sse");
        Directory.CreateDirectory(sseSourceDirectory);
        Directory.CreateDirectory(ssePexDirectory);
        var productRuntimeRoot =
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\runtime\\skyrimse";
        File.Copy(Path.Combine(productRuntimeRoot, "Source", "Scripts", "NPCM_Manolov_ApplySSE.psc"),
            Path.Combine(sseSourceDirectory, "NPCM_Manolov_ApplySSE.psc"));
        File.Copy(Path.Combine(productRuntimeRoot, "Data", "Scripts", "NPCM_Manolov_ApplySSE.pex"),
            Path.Combine(ssePexDirectory, "NPCM_Manolov_ApplySSE.pex"));
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var (fo4Runner, fo4Output, _) = CreateRunner(runtimeScriptBuildService: new RuntimeScriptBuildService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var fo4Exit = await fo4Runner.RunAsync(CommandLine.Parse(["runtime-script", "build", "--game", "fallout4", "--source-root", sourceRoot.Value, "--output", Path.Combine(root.Value, "fo4.runtime-script-build.json"), "--json"]), CancellationToken.None);
            using var fo4 = JsonDocument.Parse(fo4Output.ToString());
            Assert(fo4Exit == CommandExitCode.Success && fo4.RootElement.GetProperty("artifact").GetProperty("validPex").GetBoolean() &&
                   fo4.RootElement.GetProperty("artifact").GetProperty("properties").GetArrayLength() == 13, "FO4 runtime-script PEX evidence was incomplete.");

            var (sseRunner, sseOutput, _) = CreateRunner(runtimeScriptBuildService: new RuntimeScriptBuildService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var sseExit = await sseRunner.RunAsync(CommandLine.Parse(["runtime-script", "build", "--game", "skyrimse", "--source-root", sseSourceRoot.Value, "--output", Path.Combine(root.Value, "sse.runtime-script-build.json"), "--json"]), CancellationToken.None);
            using var sse = JsonDocument.Parse(sseOutput.ToString());
            Assert(sseExit == CommandExitCode.Success && sse.RootElement.GetProperty("artifact").GetProperty("validPex").GetBoolean() &&
                   sse.RootElement.GetProperty("artifact").GetProperty("properties").GetArrayLength() == 43,
                "SSE runtime-script PEX evidence did not preserve all 43 API properties (36 writable VMAD properties plus 7 read-only constants).");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestRuntimeScriptBuildExecutableIdentityRefusal()
    {
        var (root, sourceRoot) = CreateRuntimeScriptIdentityFixture("executable-drift");
        try
        {
            var toolDirectory = Path.Combine(root.Value, "tools", "external", "caprica-2024-06-14-patched");
            await File.AppendAllBytesAsync(Path.Combine(toolDirectory, "Caprica.exe"), [0xA5]);
            var (result, output) = await RunRuntimeScriptIdentityFixtureAsync(root, sourceRoot, "drift");

            Assert(!result.Written &&
                   result.Diagnostics.Any(item => item.Code == "runtime-script-compiler-identity") &&
                   !File.Exists(output.Value) && !File.Exists(output.Value + ".pex-inspect.json"),
                "Runtime-script build executed or published evidence from a hash-drifted Caprica executable.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRuntimeScriptBuildExactIdentitySuccess()
    {
        var (root, sourceRoot) = CreateRuntimeScriptIdentityFixture("exact");
        try
        {
            var (result, output) = await RunRuntimeScriptIdentityFixtureAsync(root, sourceRoot, "exact");
            Assert(result.Written && result.Artifact is { ValidPex: true } &&
                   result.Artifact.Properties.Length == 43 && File.Exists(output.Value) &&
                   File.Exists(output.Value + ".pex-inspect.json"),
                "Exact revisioned Caprica identity did not retain the runtime-script build behavior.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRuntimeScriptBuildRetainsIdentityThroughExecution()
    {
        var (root, sourceRoot) = CreateRuntimeScriptIdentityFixture("locked");
        try
        {
            var paths = Directory.GetFiles(Path.Combine(root.Value, "tools"), "*", SearchOption.AllDirectories);
            var process = new ReplacementAttemptingRuntimeScriptInspectorProcess(paths);
            var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(@"F:\ExampleGame"));
            var service = new RuntimeScriptBuildService(policy, root, process, TimeSpan.FromMinutes(2));
            var output = new WorkspacePath(Path.Combine(root.Value, "retained.runtime-script-build.json"));
            var result = await service.BuildAsync(
                new RuntimeScriptBuildRequest(GameEdition.SkyrimSpecialEdition, sourceRoot, output),
                CancellationToken.None);
            Assert(result.Written && result.Artifact is { ValidPex: true } &&
                   result.Artifact.Properties.Length == 43,
                "Retaining tool identity prevented authentic Caprica inspection: " +
                string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Message)));
            foreach (var path in paths)
            {
                using var released = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private sealed class ReplacementAttemptingRuntimeScriptInspectorProcess(string[] paths)
        : IRuntimeScriptInspectorProcess
    {
        public async ValueTask<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            AssertReplacementBlocked();
            var exit = await new RuntimeScriptInspectorProcess().RunAsync(startInfo, cancellationToken);
            AssertReplacementBlocked();
            return exit;
        }

        private void AssertReplacementBlocked()
        {
            foreach (var path in paths)
            {
                bool writeBlocked = false;
                try
                {
                    using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException) { writeBlocked = true; }
                Assert(writeBlocked, $"Verified Caprica bytes are writable at process boundary: {path}");

                bool replacementBlocked = false;
                try { File.Move(path, path + ".replaced"); }
                catch (IOException) { replacementBlocked = true; }
                Assert(replacementBlocked, $"Verified Caprica path is replaceable at process boundary: {path}");
            }
        }
    }

    private static async Task TestRuntimeScriptBuildManifestIdentityRefusal()
    {
        var (root, sourceRoot) = CreateRuntimeScriptIdentityFixture("manifest-drift");
        try
        {
            await File.AppendAllTextAsync(Path.Combine(root.Value, "tools", "manifests",
                "caprica-2024-06-14-patched.json"), " ");
            var (result, output) = await RunRuntimeScriptIdentityFixtureAsync(root, sourceRoot, "manifest-drift");
            Assert(!result.Written &&
                   result.Diagnostics.Any(item => item.Code == "runtime-script-compiler-identity") &&
                   !File.Exists(output.Value) && !File.Exists(output.Value + ".pex-inspect.json"),
                "Runtime-script build trusted a drifted caller-writable Caprica manifest.");
        }
        finally
        {
            DeleteDirectory(root.Value);
        }
    }

    private static async Task TestRuntimeScriptBuildDependencyIdentityRefusal()
    {
        var fixtureIndex = 0;
        foreach (var (directory, file) in new[]
                 {
                     (Path.Combine("tools", "manifests"), "caprica-2024-06-14-patched-sha256.csv"),
                     (Path.Combine("tools", "external", "caprica-2024-06-14-patched"),
                         "boost_program_options-vc143-mt-x64-1_83.dll"),
                     (Path.Combine("tools", "external", "caprica-2024-06-14-patched"), "fmt.dll"),
                     (Path.Combine("tools", "external", "caprica-2024-06-14-patched"), "pugixml.dll"),
                     (Path.Combine("tools", "external", "caprica-2024-06-14-patched"), "TESV_Papyrus_Flags.flg")
                 })
        {
            var (root, sourceRoot) = CreateRuntimeScriptIdentityFixture("dep-" + fixtureIndex++);
            try
            {
                await File.AppendAllBytesAsync(Path.Combine(root.Value, directory, file), [0x5A]);
                var (result, output) = await RunRuntimeScriptIdentityFixtureAsync(root, sourceRoot, "dependency-drift");
                Assert(!result.Written &&
                       result.Diagnostics.Any(item => item.Code == "runtime-script-compiler-identity") &&
                       !File.Exists(output.Value) && !File.Exists(output.Value + ".pex-inspect.json"),
                    $"Runtime-script build executed with drifted Caprica dependency '{file}'.");
            }
            finally
            {
                DeleteDirectory(root.Value);
            }
        }
    }

    private static async Task TestRuntimeScriptBuildWrongPathRefusal()
    {
        var (wrongRoot, wrongSource) = CreateRuntimeScriptIdentityFixture("wrong-path");
        try
        {
            var expected = Path.Combine(wrongRoot.Value, "tools", "external", "caprica-2024-06-14-patched");
            Directory.Move(expected, expected + "-alias");
            var (result, output) = await RunRuntimeScriptIdentityFixtureAsync(wrongRoot, wrongSource, "wrong-path");
            Assert(!result.Written &&
                   result.Diagnostics.Any(item => item.Code == "runtime-script-compiler-identity") &&
                   !File.Exists(output.Value + ".pex-inspect.json"),
                "Runtime-script build accepted Caprica from the wrong package-relative location.");
        }
        finally
        {
            DeleteDirectory(wrongRoot.Value);
        }
    }

    private static void TestRuntimeScriptBuildReparseAttributeRefusal()
    {
        Assert(!RuntimeScriptBuildService.IsAdmittedToolAttributes(FileAttributes.ReparsePoint) &&
               !RuntimeScriptBuildService.IsAdmittedToolAttributes(
                   FileAttributes.Archive | FileAttributes.ReparsePoint) &&
               RuntimeScriptBuildService.IsAdmittedToolAttributes(FileAttributes.Archive),
            "Runtime-script tool admission did not refuse reparse file attributes.");
    }

    private static void TestRuntimeScriptBuildAncestorReparseAttributeRefusal()
    {
        Assert(!RuntimeScriptBuildService.IsAdmittedToolPathAttributes(
                   [FileAttributes.Archive, FileAttributes.Directory | FileAttributes.ReparsePoint,
                       FileAttributes.Directory]) &&
               RuntimeScriptBuildService.IsAdmittedToolPathAttributes(
                   [FileAttributes.Archive, FileAttributes.Directory, FileAttributes.Directory]),
            "Runtime-script tool admission did not refuse a reparse-point ancestor.");
    }

    private static async Task TestRuntimeScriptBuildAncestorReparseRefusalWhenSupported()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(),
            "actorwright-runtime-script-reparse-" + Guid.NewGuid().ToString("N"));
        var fixtureRoot = Path.Combine(temporaryRoot, "fixture");
        var targetRoot = Path.Combine(temporaryRoot, "target");
        var link = Path.Combine(fixtureRoot, "tools");
        Directory.CreateDirectory(temporaryRoot);
        var (root, sourceRoot) = CreateRuntimeScriptIdentityFixture("ancestor-reparse", fixtureRoot);
        try
        {
            Directory.Move(link, targetRoot);
            if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                    link,
                    targetRoot,
                    temporaryRoot))
                return;

            Assert(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint),
                "Runtime-script ancestor reparse fixture did not create a reparse point.");
            var (result, output) = await RunRuntimeScriptIdentityFixtureAsync(root, sourceRoot, "ancestor-reparse");
            Assert(!result.Written &&
                   result.Diagnostics.Any(item => item.Code == "runtime-script-compiler-identity") &&
                   !File.Exists(output.Value) && !File.Exists(output.Value + ".pex-inspect.json"),
                "Runtime-script build followed a reparse-point tool ancestor.");
        }
        finally
        {
            DeleteDirectory(temporaryRoot);
        }
    }

    private static async Task TestRuntimeScriptInspectorProcessTreeCancellation()
    {
        var root = NewPipelineOutput("runtime-script-process-tree");
        var lockPath = Path.Combine(root.Value, "child.lock");
        var childReady = Path.Combine(root.Value, "child.ready");
        var parentReady = Path.Combine(root.Value, "parent.ready");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        int? childProcessId = null;
        try
        {
            var childScript =
                $"$stream=[IO.File]::Open('{EscapePowerShellLiteral(lockPath)}',[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                $"[IO.File]::WriteAllText('{EscapePowerShellLiteral(childReady)}','ready');" +
                "Start-Sleep -Seconds 300";
            var childEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
            var parentScript =
                $"$child=Start-Process -FilePath '{EscapePowerShellLiteral(powershell)}' " +
                $"-ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand','{childEncoded}') -PassThru;" +
                $"while(-not [IO.File]::Exists('{EscapePowerShellLiteral(childReady)}')){{Start-Sleep -Milliseconds 10}};" +
                $"[IO.File]::WriteAllText('{EscapePowerShellLiteral(parentReady)}',[string]$child.Id);" +
                "Start-Sleep -Seconds 300";
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(parentScript)));
            using var cancellation = new CancellationTokenSource();
            var pending = new RuntimeScriptInspectorProcess().RunAsync(start, cancellation.Token).AsTask();
            await WaitForFileAsync(parentReady, TimeSpan.FromSeconds(10));
            childProcessId = int.Parse(await File.ReadAllTextAsync(parentReady));
            cancellation.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("Runtime-script process adapter ignored cancellation.");
            }
            catch (OperationCanceledException)
            {
            }

            await WaitForExclusiveOpenAsync(lockPath, TimeSpan.FromSeconds(10));
            Assert(ProcessHasExited(childProcessId.Value),
                "Runtime-script process adapter left its child process running after cancellation.");
        }
        finally
        {
            if (childProcessId is { } processId && !ProcessHasExited(processId))
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            DeleteDirectory(root.Value);
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert(File.Exists(path), $"Timed out waiting for process fixture file '{path}'.");
    }

    private static async Task WaitForExclusiveOpenAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Child process retained its exclusive file lock after cancellation.");
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task TestRuntimeScriptBuildTimeoutAndCancellation()
    {
        var (timeoutRoot, timeoutSource) = CreateRuntimeScriptIdentityFixture("timeout");
        try
        {
            var process = new BlockingRuntimeScriptInspectorProcess();
            var policy = new KOnlyWorkspacePolicy(timeoutRoot, new WorkspacePath(@"F:\ExampleGame"));
            var service = new RuntimeScriptBuildService(policy, timeoutRoot, process, TimeSpan.FromMilliseconds(25));
            var output = new WorkspacePath(Path.Combine(timeoutRoot.Value, "timeout.runtime-script-build.json"));
            var result = await service.BuildAsync(
                new RuntimeScriptBuildRequest(GameEdition.SkyrimSpecialEdition, timeoutSource, output),
                CancellationToken.None);
            Assert(!result.Written &&
                   result.Diagnostics.Any(item => item.Code == "runtime-script-compiler-timeout") &&
                   !File.Exists(output.Value) && !File.Exists(output.Value + ".pex-inspect.json"),
                "Runtime-script build did not terminate and refuse a timed-out Caprica inspection.");
        }
        finally
        {
            DeleteDirectory(timeoutRoot.Value);
        }

        var (cancelRoot, cancelSource) = CreateRuntimeScriptIdentityFixture("cancel");
        try
        {
            var process = new BlockingRuntimeScriptInspectorProcess();
            var policy = new KOnlyWorkspacePolicy(cancelRoot, new WorkspacePath(@"F:\ExampleGame"));
            var service = new RuntimeScriptBuildService(policy, cancelRoot, process, TimeSpan.FromMinutes(2));
            var output = new WorkspacePath(Path.Combine(cancelRoot.Value, "cancel.runtime-script-build.json"));
            using var cancellation = new CancellationTokenSource();
            var pending = service.BuildAsync(
                new RuntimeScriptBuildRequest(GameEdition.SkyrimSpecialEdition, cancelSource, output),
                cancellation.Token).AsTask();
            await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("Runtime-script build ignored caller cancellation.");
            }
            catch (OperationCanceledException)
            {
            }
            Assert(!File.Exists(output.Value) && !File.Exists(output.Value + ".pex-inspect.json"),
                "Cancelled runtime-script inspection published evidence or retained its sidecar.");
        }
        finally
        {
            DeleteDirectory(cancelRoot.Value);
        }
    }

    private sealed class BlockingRuntimeScriptInspectorProcess : IRuntimeScriptInspectorProcess
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static (WorkspacePath Root, WorkspacePath SourceRoot) CreateRuntimeScriptIdentityFixture(
        string name, string? explicitRoot = null)
    {
        var root = explicitRoot is null
            ? NewPipelineOutput("runtime-script-build-identity-" + name)
            : new WorkspacePath(explicitRoot);
        var toolDirectory = Path.Combine(root.Value, "tools", "external", "caprica-2024-06-14-patched");
        var manifestDirectory = Path.Combine(root.Value, "tools", "manifests");
        var sourceRoot = new WorkspacePath(Path.Combine(root.Value, "runtime", "skyrimse"));
        var sourceDirectory = Path.Combine(sourceRoot.Value, "src_sse");
        var pexDirectory = Path.Combine(sourceRoot.Value, "pex_sse");
        Directory.CreateDirectory(toolDirectory);
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(pexDirectory);
        var legitimateRoot = ExampleWorkspaceRoot;
        foreach (var file in new[]
                 {
                     "Caprica.exe", "boost_program_options-vc143-mt-x64-1_83.dll", "fmt.dll",
                     "pugixml.dll", "TESV_Papyrus_Flags.flg"
                 })
        {
            File.Copy(Path.Combine(legitimateRoot, "tools", "external", "caprica-2024-06-14-patched", file),
                Path.Combine(toolDirectory, file));
        }
        foreach (var file in new[]
                 {
                     "caprica-2024-06-14-patched.json", "caprica-2024-06-14-patched-sha256.csv"
                 })
        {
            File.Copy(Path.Combine(legitimateRoot, "tools", "manifests", file), Path.Combine(manifestDirectory, file));
        }
        File.Copy(Path.Combine("runtime", "skyrimse", "Source", "Scripts", "NPCM_Manolov_ApplySSE.psc"),
            Path.Combine(sourceDirectory, "NPCM_Manolov_ApplySSE.psc"));
        File.Copy(Path.Combine("runtime", "skyrimse", "Data", "Scripts", "NPCM_Manolov_ApplySSE.pex"),
            Path.Combine(pexDirectory, "NPCM_Manolov_ApplySSE.pex"));
        return (root, sourceRoot);
    }

    private static async Task<(RuntimeScriptBuildResult Result, WorkspacePath Output)>
        RunRuntimeScriptIdentityFixtureAsync(WorkspacePath root, WorkspacePath sourceRoot, string outputName)
    {
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(@"F:\ExampleGame"));
        var service = new RuntimeScriptBuildService(policy, root);
        var output = new WorkspacePath(Path.Combine(root.Value, outputName + ".runtime-script-build.json"));
        var result = await service.BuildAsync(
            new RuntimeScriptBuildRequest(GameEdition.SkyrimSpecialEdition, sourceRoot, output),
            CancellationToken.None);
        return (result, output);
    }

    private static async Task TestRuntimeScriptBinaryWriteCli()
    {
        var result = new RuntimeScriptBinaryWriteResult(true,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\runtime-script-cli.esp"),
            new FormId(0x800), null, []);
        var (runner, output, _) = CreateRunner(runtimeScriptBinaryWriteService: new FakeRuntimeScriptBinaryWriteService(result));
        var exit = await runner.RunAsync(CommandLine.Parse(["runtime-script", "write", "--game", "fallout4",
            "--source", "K:\\ExampleWorkspace\\source.esp", "--proposal", "K:\\ExampleWorkspace\\proposal.json",
            "--output", result.Output.Value, "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("written").GetBoolean() &&
               document.RootElement.GetProperty("targetFormId").GetProperty("value").GetInt32() == 0x800,
            "runtime-script binary write CLI did not expose the typed writer result.");
    }

    private static async Task TestRuntimeScriptPackage()
    {
        var root = NewPipelineOutput("runtime-script-package");
        var sourceRoot = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\02-normalized-resources\\upstream-papyrus");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        try
        {
            foreach (var (game, script) in new[]
                     { ("fallout4", "NPCM_Manolov_ApplyFO4"), ("skyrimse", "NPCM_Manolov_ApplySSE") })
            {
                var packageRoot = new WorkspacePath(Path.Combine(root.Value, game));
                var (runner, output, _) = CreateRunner(runtimeScriptPackageService:
                    new RuntimeScriptPackageService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
                var exit = await runner.RunAsync(CommandLine.Parse(["runtime-script", "package", "--game", game,
                    "--source-root", sourceRoot.Value, "--output-root", packageRoot.Value, "--json"]), CancellationToken.None);
                using var document = JsonDocument.Parse(output.ToString());
                var artifact = document.RootElement.GetProperty("artifact");
                var installed = Path.Combine(packageRoot.Value, "Data", "Scripts", script + ".pex");
                Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("written").GetBoolean() &&
                       artifact.GetProperty("scriptName").GetString() == script && File.Exists(installed) &&
                       !File.Exists(Path.Combine(packageRoot.Value, "Data", "Scripts", "NiOverride.pex")) &&
                       !File.Exists(Path.Combine(packageRoot.Value, "Data", "Scripts", "BodyGen.pex")),
                    $"{game} runtime-script package did not contain only the game-specific apply PEX.");
                var second = await runner.RunAsync(CommandLine.Parse(["runtime-script", "package", "--game", game,
                    "--source-root", sourceRoot.Value, "--output-root", packageRoot.Value, "--json"]), CancellationToken.None);
                Assert(second == CommandExitCode.ValidationFailure && output.ToString().Contains("runtime-script-package-output-exists", StringComparison.Ordinal),
                    $"{game} runtime-script package overwrote an existing directory.");
            }
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static Task TestPipelineOutputRootIsolation()
    {
        var first = NewPipelineOutput("isolation-proof");
        var marker = Path.Combine(first.Value, "first-invocation.marker");
        File.WriteAllText(marker, "retain");
        var second = NewPipelineOutput("isolation-proof");
        try
        {
            Assert(!string.Equals(first.Value, second.Value, StringComparison.OrdinalIgnoreCase),
                "Repeated test-output requests reused the same directory.");
            Assert(File.Exists(marker),
                "Creating a later test-output root deleted an earlier invocation's evidence.");
        }
        finally
        {
            DeleteDirectory(second.Value);
            DeleteDirectory(first.Value);
        }

        return Task.CompletedTask;
    }

    private static async Task TestRuntimeScriptDeploy()
    {
        var root = NewPipelineOutput("runtime-script-deploy");
        var sourceRoot = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\02-normalized-resources\\upstream-papyrus");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame"));
        var packageService = new RuntimeScriptPackageService(policy, new WorkspacePath("K:\\ExampleWorkspace"));
        Directory.CreateDirectory(Path.Combine(root.Value, "packages"));
        try
        {
            foreach (var (game, script) in new[]
                     { ("fallout4", "NPCM_Manolov_ApplyFO4"), ("skyrimse", "NPCM_Manolov_ApplySSE") })
            {
                var packageRoot = new WorkspacePath(Path.Combine(root.Value, "packages", game));
                var package = await packageService.PackageAsync(new RuntimeScriptPackageRequest(
                    GameEditionExtensions.TryParseWireName(game, out var edition) ? edition : throw new InvalidOperationException(),
                    sourceRoot, packageRoot), CancellationToken.None);
                Assert(package.Written, $"{game} deployment fixture could not create its package.");
                var manifest = new WorkspacePath(Path.Combine(packageRoot.Value, "runtime-script-package.json"));
                var dataRoot = new WorkspacePath(Path.Combine(root.Value, "data", game));
                Directory.CreateDirectory(dataRoot.Value);
                var (runner, output, _) = CreateRunner(runtimeScriptDeployService:
                    new RuntimeScriptDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
                var deploy = await runner.RunAsync(CommandLine.Parse(["runtime-script", "deploy", "--game", game,
                    "--package", manifest.Value, "--data-root", dataRoot.Value, "--json"]), CancellationToken.None);
                using var installedResponse = JsonDocument.Parse(output.ToString());
                var destination = Path.Combine(dataRoot.Value, "Scripts", script + ".pex");
                Assert(deploy == CommandExitCode.Success && installedResponse.RootElement.GetProperty("installed").GetBoolean() &&
                       !installedResponse.RootElement.GetProperty("alreadyPresent").GetBoolean() && File.Exists(destination),
                    $"{game} runtime-script deployment did not install the apply PEX.");
                output.GetStringBuilder().Clear();
                var idempotent = await runner.RunAsync(CommandLine.Parse(["runtime-script", "deploy", "--game", game,
                    "--package", manifest.Value, "--data-root", dataRoot.Value, "--json"]), CancellationToken.None);
                using var idempotentResponse = JsonDocument.Parse(output.ToString());
                Assert(idempotent == CommandExitCode.Success && !idempotentResponse.RootElement.GetProperty("installed").GetBoolean() &&
                       idempotentResponse.RootElement.GetProperty("alreadyPresent").GetBoolean(),
                    $"{game} identical runtime-script deployment was not idempotent.");
                await File.WriteAllBytesAsync(destination, [1, 2, 3]);
                output.GetStringBuilder().Clear();
                var conflict = await runner.RunAsync(CommandLine.Parse(["runtime-script", "deploy", "--game", game,
                    "--package", manifest.Value, "--data-root", dataRoot.Value, "--json"]), CancellationToken.None);
                Assert(conflict == CommandExitCode.ValidationFailure && output.ToString().Contains("runtime-script-deploy-destination-conflict", StringComparison.Ordinal) &&
                       (await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 1, 2, 3 }),
                    $"{game} conflicting runtime-script deployment overwrote the destination.");
            }

            var outsideManifest = new WorkspacePath(Path.Combine(root.Value, "packages", "fallout4", "runtime-script-package.json"));
            var (outsideRunner, outsideOutput, _) = CreateRunner(runtimeScriptDeployService:
                new RuntimeScriptDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var outsideExit = await outsideRunner.RunAsync(CommandLine.Parse(["runtime-script", "deploy", "--game", "fallout4",
                "--package", outsideManifest.Value, "--data-root", "F:\\ExampleGame", "--json"]), CancellationToken.None);
            Assert(outsideExit == CommandExitCode.SecurityRefusal && outsideOutput.ToString().Contains("runtime-script-deploy-data-outside-lab", StringComparison.Ordinal),
                "Runtime-script deployment accepted the protected live root.");

            var malformedRoot = new WorkspacePath(Path.Combine(root.Value, "malformed"));
            Directory.CreateDirectory(Path.Combine(malformedRoot.Value, "Data"));
            var malformedManifest = new WorkspacePath(Path.Combine(malformedRoot.Value, "runtime-script-package.json"));
            await File.WriteAllTextAsync(malformedManifest.Value, "{\"artifactKind\":null,\"installedPath\":null}");
            var malformedData = new WorkspacePath(Path.Combine(root.Value, "malformed-data"));
            Directory.CreateDirectory(malformedData.Value);
            var (malformedRunner, malformedOutput, _) = CreateRunner(runtimeScriptDeployService:
                new RuntimeScriptDeployService(policy, new WorkspacePath("K:\\ExampleWorkspace")));
            var malformedExit = await malformedRunner.RunAsync(CommandLine.Parse(["runtime-script", "deploy", "--game", "fallout4",
                "--package", malformedManifest.Value, "--data-root", malformedData.Value, "--json"]), CancellationToken.None);
            Assert(malformedExit == CommandExitCode.ValidationFailure && malformedOutput.ToString().Contains("runtime-script-deploy-artifact-kind", StringComparison.Ordinal) &&
                   !File.Exists(Path.Combine(malformedData.Value, "Scripts", "NPCM_Manolov_ApplyFO4.pex")),
                "Malformed runtime-script package manifest was not refused without writing.");
        }
        finally { DeleteDirectory(root.Value); }
    }

    private static async Task TestTypedBodyGen()
    {
        var outputRoot = NewPipelineOutput("typed-bodygen");
        var result = await CreateBodyGenService().BuildTypedAsync(new BodyGenTypedBuildRequest(
            GameEdition.Fallout4, new PluginName("M2FixtureFO4.esp"), new FormId(0x800), "TypedBodyGen.esp",
            [new BodyGenMorph("CBBE Waist", -0.1f), new BodyGenMorph("CBBE Breast", 0.25f)], outputRoot), CancellationToken.None);
        Assert(result.Written && result.Files.Length == 2, "Typed BodyGen model did not build two files.");
        var template = Path.Combine(outputRoot.Value, "F4SE", "Plugins", "F4EE", "BodyGen", "TypedBodyGen.esp", "templates.ini");
        Assert((await File.ReadAllTextAsync(template)).Contains("CBBE Breast@0.25,CBBE Waist@-0.1", StringComparison.Ordinal),
            "Typed BodyGen morph ordering was not deterministic.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestFo4Pipeline()
    {
        var outputRoot = NewPipelineOutput("fo4");
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["pipeline", "preset-to-npc", "--format", "looksmenu", "--edition", "fallout4",
            "--preset", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json",
            "--source-plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--plugin", "PipelineFo4.esp", "--npc", "0x00000800", "--mod-name", "PipelineFo4.esp", "--output-root", outputRoot.Value,
            "--editor-id", "PipelineFo4Npc", "--json"]), CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToString());
        Assert(exit == CommandExitCode.Success && document.RootElement.GetProperty("completed").GetBoolean(), "FO4 pipeline did not complete.");
        Assert(document.RootElement.GetProperty("bodyGenFiles").GetArrayLength() == 2, "FO4 pipeline did not report BodyGen artifacts.");
        Assert(File.Exists(Path.Combine(outputRoot.Value, "PipelineFo4.esp")) && File.Exists(Path.Combine(outputRoot.Value, "npcmanager-package.json")),
            "FO4 pipeline package files are incomplete.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestPipelineRuntimeScriptPackage()
    {
        var packageParent = NewPipelineOutput("runtime-script-package-parent");
        var packageRoot = new WorkspacePath(Path.Combine(packageParent.Value, "package"));
        var outputRoot = NewPipelineOutput("runtime-script-package-pipeline");
        var sourceRoot = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\02-normalized-resources\\upstream-papyrus");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        try
        {
            var package = await new RuntimeScriptPackageService(policy, new WorkspacePath("K:\\ExampleWorkspace"))
                .PackageAsync(new RuntimeScriptPackageRequest(GameEdition.Fallout4, sourceRoot, packageRoot),
                    CancellationToken.None);
            Assert(package.Written && package.Artifact is not null, "Runtime-script package fixture was not created.");
            var manifest = new WorkspacePath(Path.Combine(packageRoot.Value, "runtime-script-package.json"));
            var (runner, output, _) = CreateRunner();
            var exit = await runner.RunAsync(CommandLine.Parse(["pipeline", "preset-to-npc", "--format", "looksmenu",
                "--edition", "fallout4", "--preset",
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json",
                "--source-plugin",
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
                "--plugin", "PipelineRuntimeScript.esp", "--npc", "0x00000800", "--mod-name", "PipelineRuntimeScript.esp",
                "--output-root", outputRoot.Value, "--editor-id", "PipelineRuntimeScriptNpc",
                "--runtime-script-package", manifest.Value, "--json"]), CancellationToken.None);
            using var response = JsonDocument.Parse(output.ToString());
            var result = response.RootElement.GetProperty("completed").GetBoolean();
            var installed = Path.Combine(outputRoot.Value, "Data", "Scripts", "NPCM_Manolov_ApplyFO4.pex");
            Assert(exit == CommandExitCode.Success && result && File.Exists(installed),
                "Preset pipeline CLI did not install the bound apply PEX.");
            Assert(response.RootElement.GetProperty("packageArtifacts").EnumerateArray().Any(item =>
                item.GetProperty("kind").GetString() == "runtime-script" &&
                item.GetProperty("relativePath").GetString() == "Data/Scripts/NPCM_Manolov_ApplyFO4.pex"),
                "Preset pipeline manifest did not record the installed apply PEX.");
            var installedBytes = await File.ReadAllBytesAsync(installed).ConfigureAwait(false);
            var packagedBytes = await File.ReadAllBytesAsync(Path.Combine(packageRoot.Value, "Data", "Scripts", "NPCM_Manolov_ApplyFO4.pex"))
                .ConfigureAwait(false);
            Assert(installedBytes.Length > 0 && installedBytes.SequenceEqual(packagedBytes),
                "Preset pipeline installed PEX bytes differ from the bound package.");
        }
        finally
        {
            DeleteDirectory(packageParent.Value);
            DeleteDirectory(outputRoot.Value);
        }
    }

    private static async Task TestLooksMenuBodyRegionPipeline()
    {
        var preset = NewBodyGenInput("looksmenu-mrsv.json",
            "{\"Morphs\":{\"Values\":[0.25,-0.5,0.1,0,1.0]}}");
        var outputRoot = NewPipelineOutput("looksmenu-mrsv");
        var invalidPreset = NewBodyGenInput("looksmenu-mrsv-too-many.json",
            "{\"Morphs\":{\"Values\":[0,0,0,0,0,0]}}");
        var invalidOutputRoot = NewPipelineOutput("looksmenu-mrsv-too-many");
        try
        {
            var invalid = await CreatePipelineService().ExecuteAsync(new PresetToNpcPipelineRequest(
                PresetFormat.LooksMenu, GameEdition.Fallout4, invalidPreset,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp"),
                new PluginName("PipelineMrsvTooMany.esp"), new FormId(0x800), "PipelineMrsvTooMany.esp", invalidOutputRoot,
                null, null), CancellationToken.None);
            Assert(!invalid.Completed && invalid.Diagnostics.Any(item => item.Code == "preset-body-region-count") &&
                !File.Exists(Path.Combine(invalidOutputRoot.Value, "PipelineMrsvTooMany.esp")),
                "LooksMenu over-counted Morphs.Values was not refused before writing.");

            var result = await CreatePipelineService().ExecuteAsync(new PresetToNpcPipelineRequest(
                PresetFormat.LooksMenu, GameEdition.Fallout4, preset,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp"),
                new PluginName("PipelineMrsv.esp"), new FormId(0x800), "PipelineMrsv.esp", outputRoot,
                new EditorId("PipelineMrsvNpc"), null), CancellationToken.None);
            Assert(result.Completed && result.MutationResult?.OutputHash is not null,
                "LooksMenu Morphs.Values did not complete the FO4 pipeline.");

            var verification = BethesdaPluginVerifier.Verify(new PluginVerificationRequest(
                GameEdition.Fallout4,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp"),
                new WorkspacePath(Path.Combine(outputRoot.Value, "PipelineMrsv.esp")), new FormId(0x800),
                [new MutationChange("BodyMorphRegions", null, "patch:head=0.25,upperTorso=-0.5,arms=0.1,lowerTorso=0,legs=1")], ["OBND", "ACBS", "MRSV"]),
                CancellationToken.None);
            Assert(verification.IsValid, "Independent verifier did not confirm pipeline MRSV output.");
        }
        finally
        {
            DeleteIfExists(preset.Value);
            DeleteIfExists(invalidPreset.Value);
            DeleteDirectory(outputRoot.Value);
            DeleteDirectory(invalidOutputRoot.Value);
        }
    }

    private static async Task TestSsePipeline()
    {
        var refusedRoot = NewPipelineOutput("sse-full-preset-refused");
        var boundedRoot = NewPipelineOutput("sse-bounded");
        var boundedPreset = NewBodyGenInput("sse-bounded-pipeline.jslot",
            "{\"actor\":{\"weight\":62},\"bodyMorphs\":[{\"name\":\"Breast\",\"keys\":[{\"key\":\"NPCManager\",\"value\":0.4}]}]}");
        try
        {
            var (refusedRunner, refusedOutput, _) = CreateRunner();
            var refusedExit = await refusedRunner.RunAsync(CommandLine.Parse(["pipeline", "preset-to-npc", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--preset", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\sse-racemenu.jslot",
                "--source-plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp",
                "--plugin", "PipelineSseRefused.esp", "--npc", "0x00000800", "--mod-name", "PipelineSseRefused.esp", "--output-root", refusedRoot.Value,
                "--name", "Pipeline SSE NPC", "--json"]), CancellationToken.None);
            using var refusedDocument = JsonDocument.Parse(refusedOutput.ToString());
            Assert(refusedExit == CommandExitCode.ValidationFailure &&
                   !refusedDocument.RootElement.GetProperty("completed").GetBoolean() &&
                   refusedDocument.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item =>
                       item.GetProperty("code").GetString() == "pipeline-skyrim-headparts-not-applied") &&
                   !File.Exists(Path.Combine(refusedRoot.Value, "PipelineSseRefused.esp")),
                "SSE pipeline reported success while dropping RaceMenu appearance fields.");

            var (boundedRunner, boundedOutput, _) = CreateRunner();
            var boundedExit = await boundedRunner.RunAsync(CommandLine.Parse(["pipeline", "preset-to-npc", "--format", "racemenu-jslot", "--edition", "skyrimse",
                "--preset", boundedPreset.Value,
                "--source-plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp",
                "--plugin", "PipelineSse.esp", "--npc", "0x00000800", "--mod-name", "PipelineSse.esp", "--output-root", boundedRoot.Value,
                "--name", "Pipeline SSE NPC", "--json"]), CancellationToken.None);
            using var boundedDocument = JsonDocument.Parse(boundedOutput.ToString());
            Assert(boundedExit == CommandExitCode.Success && boundedDocument.RootElement.GetProperty("completed").GetBoolean(),
                "SSE bounded pipeline did not complete.");
            Assert(boundedDocument.RootElement.GetProperty("bodyGenFiles").GetArrayLength() == 2,
                "SSE bounded pipeline did not report BodyGen artifacts.");
            Assert(File.Exists(Path.Combine(boundedRoot.Value, "PipelineSse.esp")) &&
                   File.Exists(Path.Combine(boundedRoot.Value, "npcmanager-package.json")),
                "SSE bounded pipeline package files are incomplete.");
        }
        finally
        {
            DeleteIfExists(boundedPreset.Value);
            DeleteDirectory(refusedRoot.Value);
            DeleteDirectory(boundedRoot.Value);
        }
    }

    private static async Task TestPipelineExistingOutput()
    {
        var outputRoot = NewPipelineOutput("existing");
        await File.WriteAllTextAsync(Path.Combine(outputRoot.Value, "PipelineExisting.esp"), "keep");
        var result = await CreatePipelineService().ExecuteAsync(new PresetToNpcPipelineRequest(
            PresetFormat.LooksMenu, GameEdition.Fallout4,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp"),
            new PluginName("PipelineExisting.esp"), new FormId(0x800), "PipelineExisting.esp", outputRoot, null, null), CancellationToken.None);
        Assert(!result.Completed && result.Diagnostics.Any(item => item.Code == "pipeline-output-plugin-exists"), "Existing pipeline output was accepted.");
        Assert(await File.ReadAllTextAsync(Path.Combine(outputRoot.Value, "PipelineExisting.esp")) == "keep", "Existing pipeline output changed.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestPipelineCrossGame()
    {
        var outputRoot = NewPipelineOutput("cross-game");
        var result = await CreatePipelineService().ExecuteAsync(new PresetToNpcPipelineRequest(
            PresetFormat.LooksMenu, GameEdition.SkyrimSpecialEdition,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json"),
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\sse\\Data\\M2FixtureSSE.esp"),
            new PluginName("PipelineCrossGame.esp"), new FormId(0x800), "PipelineCrossGame.esp", outputRoot, null, null), CancellationToken.None);
        Assert(!result.Completed && result.Diagnostics.Any(item => item.Code == "pipeline-format-edition-mismatch"), "Cross-game preset format was accepted.");
        Assert(!File.Exists(Path.Combine(outputRoot.Value, "PipelineCrossGame.esp")), "Cross-game pipeline wrote an output.");
        DeleteDirectory(outputRoot.Value);
    }

    private static async Task TestPipelineRollback()
    {
        var preset = NewBodyGenInput("pipeline-invalid.json", "{\"BodyMorphs\":{\"CBBE Breast\":2.0},\"Weight\":[0.2,0.5,0.3]}");
        var outputRoot = NewPipelineOutput("rollback");
        try
        {
            var result = await CreatePipelineService().ExecuteAsync(new PresetToNpcPipelineRequest(
                PresetFormat.LooksMenu, GameEdition.Fallout4, preset,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp"),
                new PluginName("PipelineRollback.esp"), new FormId(0x800), "PipelineRollback.esp", outputRoot, null, null), CancellationToken.None);
            Assert(!result.Completed && result.Diagnostics.Any(item => item.Code == "bodygen-morph-invalid"), "Invalid BodyGen morph was accepted by the pipeline.");
            Assert(!File.Exists(Path.Combine(outputRoot.Value, "PipelineRollback.esp")) && !File.Exists(Path.Combine(outputRoot.Value, "npcmanager-package.json")),
                "Pipeline rollback left a partial package.");
        }
        finally
        {
            DeleteIfExists(preset.Value);
            DeleteDirectory(outputRoot.Value);
        }
    }

    private static async Task TestPipelineOptionalOutsideK()
    {
        var outputRoot = NewPipelineOutput("optional-outside-k");
        var (runner, output, _) = CreateRunner();
        var exit = await runner.RunAsync(CommandLine.Parse(["pipeline", "preset-to-npc", "--format", "looksmenu",
            "--edition", "fallout4", "--preset", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m4-fixtures\\fo4-looksmenu.json",
            "--source-plugin", "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\fo4\\Data\\M2FixtureFO4.esp",
            "--plugin", "P12Outside.esp", "--npc", "0x00000800", "--mod-name", "P12Outside.esp", "--output-root", outputRoot.Value,
            "--facegeom-manifest", "F:\\ExampleGame\\Data\\facegen.json", "--json"]), CancellationToken.None);
        Assert(exit == CommandExitCode.SecurityRefusal && output.ToString().Contains("pipeline-facegeom-manifest-outside-lab", StringComparison.Ordinal),
            "Pipeline accepted a FaceGeom manifest outside K.");
        Assert(!File.Exists(Path.Combine(outputRoot.Value, "P12Outside.esp")), "Pipeline wrote output after refusing an outside-K optional artifact.");
        DeleteDirectory(outputRoot.Value);
    }

    private static BodyGenService CreateBodyGenService() => new(
        new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
        new WorkspacePath("K:\\ExampleWorkspace"));

    private static PresetToNpcPipeline CreatePipelineService(IRuntimeScriptDeployService? runtimeScriptDeployService = null) => new(
        new PresetService(new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")), new WorkspacePath("K:\\ExampleWorkspace")),
        CreateMutationService(), CreateBodyGenService(),
        new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
        new WorkspacePath("K:\\ExampleWorkspace"), bodySidecarWriteService: new BodySidecarWriteService(
            new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
            new WorkspacePath("K:\\ExampleWorkspace")), runtimeScriptDeployService: runtimeScriptDeployService);

    private static FaceTintBuildService CreateFaceTintService(IWorkspacePolicy policy) => new(
        policy,
        new WorkspacePath("K:\\ExampleWorkspace"),
        new TexconvFaceTintTextureEncoder(
            new WorkspacePath("K:\\ExampleWorkspace\\tools\\external\\directxtex-texconv-2026.5.7\\texconv.exe"),
            new WorkspacePath("K:\\ExampleWorkspace"),
            new Sha256Hash("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06")),
        new TexconvFaceTintTextureDecoder(
            new WorkspacePath("K:\\ExampleWorkspace\\tools\\external\\directxtex-texconv-2026.5.7\\texconv.exe"),
            new WorkspacePath("K:\\ExampleWorkspace"),
            new Sha256Hash("DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06")));

    private static WorkspacePath NewBodyGenInput(string fileName, string content)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-rational-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        File.WriteAllText(path.Value, content);
        return path;
    }

    private static WorkspacePath NewBodyGenOutput(string name)
    {
        var path = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m5-bodygen-{name}");
        DeleteDirectory(path.Value);
        return path;
    }

    private static WorkspacePath NewPipelineOutput(string name)
    {
        var path = new WorkspacePath(
            $"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\" +
            $"m5-pipeline-{name}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path.Value);
        return path;
    }

    private static async Task<byte[]> ReadZipEntryAsync(ZipArchiveEntry entry)
    {
        await using var input = entry.Open();
        await using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static NpcMutationService CreateMutationService() => new(
        new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
        new WorkspacePath("K:\\ExampleWorkspace"));

    private static WorkspacePath NewMutationOutput(string fileName)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m3-rational-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        DeleteIfExists(path.Value);
        return path;
    }

    private static WorkspacePath NewPresetOutput(string fileName)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-rational-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        DeleteIfExists(path.Value);
        return path;
    }

    private static PluginLoadOrderService CreateLoadOrderService() => new(
        new BethesdaPluginReader(),
        new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"), new WorkspacePath("F:\\ExampleGame")),
        new WorkspacePath("K:\\ExampleWorkspace"));

    private static async Task<WorkspacePath> CreateArchiveConsistencyArtifact(GameEdition edition, string game)
    {
        var root = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-archive-consistency-{game}");
        DeleteDirectory(root.Value);
        Directory.CreateDirectory(root.Value);
        var output = new WorkspacePath(Path.Combine(root.Value, "asset-index.json"));
        var dataRoot = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\m2-fixtures\\{game}\\Data");
        var policy = new KOnlyWorkspacePolicy(new WorkspacePath("K:\\ExampleWorkspace"),
            new WorkspacePath("F:\\ExampleGame"));
        var result = await new AssetIndexExportService(new BethesdaAssetIndexer(), policy,
            new WorkspacePath("K:\\ExampleWorkspace")).ExportAsync(
            new AssetIndexExportRequest(edition, dataRoot, output), CancellationToken.None);
        Assert(result.Written, $"Could not create {edition} archive-consistency asset index.");
        return output;
    }

    private static WorkspacePath NewM2LoadOrderRoot(string name)
    {
        var path = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-load-order-{name}");
        DeleteDirectory(path.Value);
        Directory.CreateDirectory(path.Value);
        return path;
    }

    private static WorkspacePath NewM2LoadOrderFile(string fileName, string content)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-load-order-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        File.WriteAllText(path.Value, content);
        return path;
    }

    private static WorkspacePath NewGeneratedScanRoot(string game)
    {
        var path = new WorkspacePath($"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m2-generated-scan-{game}");
        DeleteDirectory(path.Value);
        Directory.CreateDirectory(Path.Combine(path.Value, "Data"));
        return path;
    }

    private static WorkspacePath NewBodySidecarFile(string fileName, string content)
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\m4-sidecar-tests");
        Directory.CreateDirectory(root.Value);
        var path = new WorkspacePath(Path.Combine(root.Value, fileName));
        DeleteIfExists(path.Value);
        File.WriteAllText(path.Value, content);
        return path;
    }

    private static void WriteGeneratedScanFixture(string root, GameEdition edition, string plugin)
    {
        var data = Path.Combine(root, "Data");
        File.WriteAllBytes(Path.Combine(data, plugin), BuildMarkerPlugin());
        var pluginDirectory = plugin;
        var faceGeom = Path.Combine(data, "Meshes", "Actors", "Character", "FaceGenData", "FaceGeom", pluginDirectory);
        Directory.CreateDirectory(faceGeom);
        File.WriteAllBytes(Path.Combine(faceGeom, "00000800.nif"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(faceGeom, "00000800_2.nif"), [4, 5, 6]);

        if (edition == GameEdition.Fallout4)
        {
            var customization = Path.Combine(data, "Textures", "Actors", "Character", "FaceCustomization", pluginDirectory);
            Directory.CreateDirectory(customization);
            File.WriteAllBytes(Path.Combine(customization, "00000800_d.dds"), [7]);
            File.WriteAllBytes(Path.Combine(customization, "00000800_msn.dds"), [8]);
            File.WriteAllBytes(Path.Combine(customization, "00000800_s.dds"), [9]);
            return;
        }

        var tint = Path.Combine(data, "Textures", "Actors", "Character", "FaceGenData", "FaceTint", pluginDirectory);
        Directory.CreateDirectory(tint);
        File.WriteAllBytes(Path.Combine(tint, "00000800.dds"), [7]);
        File.WriteAllBytes(Path.Combine(tint, "facedetailneutral.dds"), [8]);
        var diffuse = Path.Combine(data, "Textures", "Actors", "Character", "FaceGenData", "FaceDiffuse", pluginDirectory);
        Directory.CreateDirectory(diffuse);
        File.WriteAllBytes(Path.Combine(diffuse, "00000800.dds"), [9]);
        var normal = Path.Combine(data, "Textures", "Actors", "Character", "FaceGenData", "FaceNormal", pluginDirectory);
        Directory.CreateDirectory(normal);
        File.WriteAllBytes(Path.Combine(normal, "00000800.dds"), [10]);
    }

    private static byte[] BuildFaceTintPlugin()
    {
        var npcBody = BuildSubrecord("EDID", Encoding.ASCII.GetBytes("P04TintTestNpc\0"))
            .Concat(BuildSubrecord("TETI", [1, 0, 0x30, 0]))
            .Concat(BuildSubrecord("TEND", [50, 1, 2, 3, 0, 255, 255]))
            .Concat(BuildSubrecord("TETI", [2, 0, 0x31, 0]))
            .Concat(BuildSubrecord("TEND", [20]))
            .ToArray();
        var npcHeader = new byte[24]; Encoding.ASCII.GetBytes("NPC_").CopyTo(npcHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(4, 4), (uint)npcBody.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(12, 4), 0x800);
        var tes4Header = new byte[24]; Encoding.ASCII.GetBytes("TES4").CopyTo(tes4Header, 0);
        return tes4Header.Concat(npcHeader).Concat(npcBody).ToArray();
    }

    private static byte[] BuildSseFaceMorphPlugin()
    {
        var nam9 = new byte[76];
        for (var i = 0; i < 18; i++)
            BinaryPrimitives.WriteInt32LittleEndian(nam9.AsSpan(i * 4), BitConverter.SingleToInt32Bits((i - 9) / 10F));
        BinaryPrimitives.WriteInt32LittleEndian(nam9.AsSpan(72), BitConverter.SingleToInt32Bits(float.MaxValue));
        var nama = new byte[16];
        foreach (var (index, value) in new[] { (0, uint.MaxValue), (1, 2U), (2, uint.MaxValue), (3, 4U) })
            BinaryPrimitives.WriteUInt32LittleEndian(nama.AsSpan(index * 4), value);
        var npcBody = BuildSubrecord("EDID", Encoding.ASCII.GetBytes("P04SseMorphTestNpc\0"))
            .Concat(BuildSubrecord("NAM9", nam9))
            .Concat(BuildSubrecord("NAMA", nama))
            .ToArray();
        var npcHeader = new byte[24]; Encoding.ASCII.GetBytes("NPC_").CopyTo(npcHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(4, 4), (uint)npcBody.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(12, 4), 0x800);
        var tes4Header = new byte[24]; Encoding.ASCII.GetBytes("TES4").CopyTo(tes4Header, 0);
        return tes4Header.Concat(npcHeader).Concat(npcBody).ToArray();
    }

    private static byte[] BuildSseFaceMorphPluginWithEightMasters()
    {
        var headerData = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(
            headerData.AsSpan(0, 4),
            BitConverter.SingleToInt32Bits(1.71F));
        BinaryPrimitives.WriteUInt32LittleEndian(headerData.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(headerData.AsSpan(8, 4), 0x801);

        using var tes4Body = new MemoryStream();
        tes4Body.Write(BuildSubrecord("HEDR", headerData));
        string[] masters =
        [
            "Skyrim.esm",
            "Update.esm",
            "Dawnguard.esm",
            "HearthFires.esm",
            "Dragonborn.esm",
            "RaceMenu.esp",
            "High Poly Head.esm",
            "KS Hairdo's.esp"
        ];
        foreach (var master in masters)
        {
            tes4Body.Write(BuildSubrecord("MAST", Encoding.ASCII.GetBytes(master + '\0')));
            tes4Body.Write(BuildSubrecord("DATA", new byte[8]));
        }

        var tes4Header = new byte[24];
        Encoding.ASCII.GetBytes("TES4").CopyTo(tes4Header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(tes4Header.AsSpan(4, 4), checked((uint)tes4Body.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(tes4Header.AsSpan(20, 2), 44);

        var nam9 = new byte[76];
        for (var index = 0; index < 18; index++)
            BinaryPrimitives.WriteInt32LittleEndian(
                nam9.AsSpan(index * 4),
                BitConverter.SingleToInt32Bits((index - 9) / 10F));
        BinaryPrimitives.WriteInt32LittleEndian(
            nam9.AsSpan(72),
            BitConverter.SingleToInt32Bits(42.25F));

        var nama = new byte[16];
        foreach (var (index, value) in new[] { (0, uint.MaxValue), (1, 2U), (2, uint.MaxValue), (3, 4U) })
            BinaryPrimitives.WriteUInt32LittleEndian(nama.AsSpan(index * 4), value);

        var npcBody = BuildSubrecord("EDID", Encoding.ASCII.GetBytes("EightMasterMorphNpc\0"))
            .Concat(BuildSubrecord("NAM9", nam9))
            .Concat(BuildSubrecord("NAMA", nama))
            .ToArray();
        var npcHeader = new byte[24];
        Encoding.ASCII.GetBytes("NPC_").CopyTo(npcHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(4, 4), checked((uint)npcBody.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(12, 4), 0x08000800U);
        BinaryPrimitives.WriteUInt16LittleEndian(npcHeader.AsSpan(20, 2), 44);

        var groupHeader = new byte[24];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(groupHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            groupHeader.AsSpan(4, 4),
            checked((uint)(groupHeader.Length + npcHeader.Length + npcBody.Length)));
        Encoding.ASCII.GetBytes("NPC_").CopyTo(groupHeader, 8);

        return tes4Header
            .Concat(tes4Body.ToArray())
            .Concat(groupHeader)
            .Concat(npcHeader)
            .Concat(npcBody)
            .ToArray();
    }

    internal static byte[] BuildSseFaceTintPlugin()
    {
        var tintOne = BuildSubrecord("TINI", [4, 0])
            .Concat(BuildSubrecord("TINC", [1, 2, 3, 255]))
            .Concat(BuildSubrecord("TINV", BitConverter.GetBytes((uint)10)))
            .Concat(BuildSubrecord("TIAS", BitConverter.GetBytes((short)1)));
        var tintTwo = BuildSubrecord("TINI", [24, 0])
            .Concat(BuildSubrecord("TINC", [9, 8, 7, 64]))
            .Concat(BuildSubrecord("TINV", BitConverter.GetBytes((uint)50)))
            .Concat(BuildSubrecord("TIAS", BitConverter.GetBytes((short)-1)));
        var npcBody = BuildSubrecord("EDID", Encoding.ASCII.GetBytes("P04SseTintTestNpc\0"))
            .Concat(BuildSubrecord("QNAM", [0xAA, 0xBB, 0xCC, 0xDD]))
            .Concat(tintOne)
            .Concat(BuildSubrecord("NAM7", BitConverter.GetBytes(1.0F)))
            .Concat(tintTwo)
            .ToArray();
        var npcHeader = new byte[24]; Encoding.ASCII.GetBytes("NPC_").CopyTo(npcHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(4, 4), (uint)npcBody.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(npcHeader.AsSpan(12, 4), 0x800);
        var tes4Header = new byte[24]; Encoding.ASCII.GetBytes("TES4").CopyTo(tes4Header, 0);
        return tes4Header.Concat(npcHeader).Concat(npcBody).ToArray();
    }

    private static byte[] BuildMarkerPlugin()
    {
        var headerData = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(headerData.AsSpan(0, 4),
            unchecked((uint)BitConverter.SingleToInt32Bits(1f)));
        BinaryPrimitives.WriteUInt32LittleEndian(headerData.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(headerData.AsSpan(8, 4), 0x800);
        var hedr = BuildSubrecord("HEDR", headerData);
        var author = Encoding.ASCII.GetBytes(GeneratedArtifactMarkers.ReferenceAuthor).Concat(new byte[] { 0 }).ToArray();
        var cnam = BuildSubrecord("CNAM", author);
        var body = hedr.Concat(cnam).ToArray();
        var header = new byte[24];
        Encoding.ASCII.GetBytes("TES4").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), 0);
        return header.Concat(body).ToArray();
    }

    private static byte[] BuildSubrecord(string signature, byte[] value)
    {
        var result = new byte[6 + value.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), checked((ushort)value.Length));
        value.CopyTo(result, 6);
        return result;
    }

    private static Sha256Hash Hash(WorkspacePath path)
    {
        using var stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static int FindAscii(byte[] bytes, string value, int startOffset = 0)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(value);
        for (var offset = Math.Max(0, startOffset); offset <= bytes.Length - needle.Length; offset++)
        {
            if (bytes.AsSpan(offset, needle.Length).SequenceEqual(needle)) return offset;
        }
        return -1;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    internal static (CliRunner Runner, StringWriter Output, StringWriter Error) CreateRunner(
        IOutfitChoiceService? outfitChoiceService = null,
        IOutfitProposalService? outfitProposalService = null,
        ILeveledListProposalService? leveledListProposalService = null,
        ILeveledListResolveService? leveledListResolveService = null,
        IArmorProposalService? armorProposalService = null,
        IArmorDamageResistanceService? armorDamageResistanceService = null,
        IArmorAddonProposalService? armorAddonProposalService = null,
        IArmorAddonModelProposalService? armorAddonModelProposalService = null,
        IMaterialSwapProposalService? materialSwapProposalService = null,
        IObjectTemplateProposalService? objectTemplateProposalService = null,
        IObjectTemplatePropertyProposalService? objectTemplatePropertyProposalService = null,
        IChangeTrackingService? changeTrackingService = null,
        IChangeActionService? changeActionService = null,
        IRecordProposalService? recordProposalService = null,
        IPluginWriteService? pluginWriteService = null,
        IPluginVerifyService? pluginVerifyService = null,
        IBodySidecarWriteService? bodySidecarWriteService = null,
        IRuntimeScriptProposalService? runtimeScriptProposalService = null,
        IRuntimeScriptBuildService? runtimeScriptBuildService = null,
        ISchemaExportService? schemaExportService = null,
        IOutfitBinaryWriteService? outfitBinaryWriteService = null,
        ILeveledListBinaryWriteService? leveledListBinaryWriteService = null,
        IArmorBinaryWriteService? armorBinaryWriteService = null,
        IArmorAddonBinaryWriteService? armorAddonBinaryWriteService = null,
        IMaterialSwapBinaryWriteService? materialSwapBinaryWriteService = null,
            IObjectTemplateBinaryWriteService? objectTemplateBinaryWriteService = null,
            IRuntimeScriptBinaryWriteService? runtimeScriptBinaryWriteService = null,
        IRuntimeScriptPackageService? runtimeScriptPackageService = null,
        IRuntimeScriptDeployService? runtimeScriptDeployService = null,
        IFaceGeomBinaryBuildService? faceGeomBinaryBuildService = null,
        IFaceGenPackService? faceGenPackService = null,
        IPackageBuildService? packageBuildService = null,
        IPackageInspectService? packageInspectService = null,
        IPackageVerifyService? packageVerifyService = null,
        IPackageArchiveService? packageArchiveService = null,
        IPluginSurfaceAuditService? pluginSurfaceAuditService = null,
        IRuntimeSmokeVerifyService? runtimeSmokeVerifyService = null,
        IPluginDeployService? pluginDeployService = null,
        IFaceGenDeployService? faceGenDeployService = null,
        IPreviewAnimationListService? previewAnimationListService = null,
        IPreviewAnimationTreeService? previewAnimationTreeService = null,
        IExistingNpcEditService? existingNpcEditService = null,
        ISkyrimNativeFaceTintPipelineService? skyrimNativeFaceTintPipelineService = null,
        IFaceGenBakeAllService? nativeFaceGenBatchService = null,
        ISkyrimRaceMenuPaintChoiceService? skyrimRaceMenuPaintChoiceService = null,
        IReferencePresetAuthoringTransaction? referencePresetAuthoringTransaction = null,
        IReferencePresetSessionService? referencePresetSessionService = null,
        IRaceMenuNpcExecutionRequestFileLoader? referencePresetExecutionRequestFileLoader = null,
        ISkyrimFollowerFinishService? skyrimFollowerFinishService = null,
        ISkyrimFollowerFinishRequestFileLoader? skyrimFollowerFinishRequestFileLoader = null,
        INpcVisualPreviewComposer? npcVisualPreviewComposer = null,
        IFaceGeomHairRegionsPreviewService?
            faceGeomHairRegionsPreviewService = null,
        INpcVisualPreviewVisualValidator?
            faceGeomHairRegionsVisualValidator = null,
        IActorAssemblyPreflightService? actorAssemblyPreflightService = null,
        IDesktopLaunchService? desktopLaunchService = null,
        ISkyrimNpcVoiceService? skyrimNpcVoiceService = null,
        ISkyrimNpcDialogueService? skyrimNpcDialogueService = null,
        WorkspacePath? workspaceRoot = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        WorkspacePath root = workspaceRoot ?? new WorkspacePath(ExampleWorkspaceRoot);
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath("F:\\ExampleGame"));
        var faceGenService = new FaceGenService(policy, root);
        var packageReader = new PackageManifestReader(policy, root);
        var packageVerifier = packageVerifyService ?? new PackageVerifyService(packageReader);
        var animationListService = previewAnimationListService ?? new PreviewAnimationListService(
            policy, root);
        return (new CliRunner(new CliRunnerServices(new WorkspacePreflightService(policy),
            new GameInventoryService(new BethesdaPluginReader(), new BethesdaAssetIndexer(), policy,
                root),
            new FormChoiceService(new BethesdaPluginReader(), policy,
                root),
            new AssetChoiceService(new BethesdaPluginReader(), new BethesdaAssetIndexer(), policy,
                root),
            new GameRootPreflightService(policy),
            new ArchiveConsistencyService(policy, root),
            new GeneratedArtifactCommandHandler(new GeneratedArtifactScanService(policy, new BethesdaPluginReader(),
                root), output, error),
            new BodySidecarCommandHandler(new BodySidecarInspectionService(policy,
                root), output, error),
            new AssetIndexExportService(new BethesdaAssetIndexer(), policy,
                root),
            new NpcMutationService(policy, root),
            new NpcTemplateMaterializationService(policy, root),
            new NpcResetService(policy, root,
                new NpcMutationService(policy, root)),
            new PresetService(policy, root),
            new PresetFormResolver(policy, root),
            faceGenService,
            new BodyGenService(policy, root),
            new PresetToNpcPipeline(
                new PresetService(policy, root),
                new NpcMutationService(policy, root),
                new BodyGenService(policy, root), policy,
                root,
                new FaceGeomBuildService(faceGenService, policy, root),
                CreateFaceTintService(policy),
                new BodySidecarWriteService(policy, root),
                new RuntimeScriptDeployService(policy, root)),
            new PluginLoadOrderService(new BethesdaPluginReader(), policy,
                root),
            root,
            null, new FaceGenOptionsService(policy, root),
            new NpcFaceTintPatchService(policy, root),
            new SkyrimFaceMorphPatchService(policy, root),
            new RaceMenuExtendedMorphPatchService(policy, root),
            new SkyrimFaceTintPatchService(policy, root),
            new RaceMenuSculptPatchService(policy, root),
            new FacePoseResolverService(policy, root),
            new FaceSectionResetService(policy, root),
            new BodySlideResolutionService(new PresetService(policy, root),
                policy, root),
             new SseBodyWeightResolutionService(policy, root),
             new BodyOverlayPatchService(), new SkyrimOverlayFoldService(),
             new SkyrimBodyTransformService(policy, root),
             new BodySectionResetService(policy, root),
             new FaceGeomBuildService(faceGenService, policy, root),
             CreateFaceTintService(policy),
             new FaceGenCorrectionService(faceGenService, policy, root),
             new FaceGenBatchService(faceGenService, policy, root),
             new FaceGenPluginBuildService(faceGenService, policy, root),
             new PreviewSceneService(policy, root),
             new PreviewRerollService(policy, root),
             new PreviewNifExportService(policy, root),
             outfitChoiceService ?? new OutfitChoiceService(new BethesdaPluginReader(), policy,
                 root),
             outfitProposalService ?? new OutfitProposalService(new BethesdaPluginReader(), policy,
                 root),
             leveledListProposalService ?? new LeveledListProposalService(new BethesdaPluginReader(), policy,
                 root),
             leveledListResolveService ?? new LeveledListResolveService(policy,
                 root),
             armorProposalService ?? new ArmorProposalService(new BethesdaPluginReader(), policy,
                 root),
             armorDamageResistanceService ?? new ArmorDamageResistanceService(new BethesdaPluginReader(), policy,
                 root),
             armorAddonProposalService ?? new ArmorAddonProposalService(new BethesdaPluginReader(), policy,
                 root),
             armorAddonModelProposalService ?? new ArmorAddonModelProposalService(new BethesdaPluginReader(), policy,
                 root),
             materialSwapProposalService ?? new MaterialSwapProposalService(new BethesdaPluginReader(), policy,
                 root),
             objectTemplateProposalService ?? new ObjectTemplateProposalService(new BethesdaPluginReader(), policy,
                 root),
             objectTemplatePropertyProposalService ?? new ObjectTemplatePropertyProposalService(new BethesdaPluginReader(), policy,
                 root),
             changeTrackingService ?? new ChangeTrackingService(policy, root),
             changeActionService ?? new ChangeActionService(policy, root),
             recordProposalService ?? new RecordProposalService(policy, root),
             pluginWriteService, pluginVerifyService, bodySidecarWriteService,
             runtimeScriptProposalService, runtimeScriptBuildService,
             schemaExportService ?? new SchemaExportService(policy, root),
             desktopLaunchService ?? new DesktopLaunchService(policy,
                 root),
             outfitBinaryWriteService, leveledListBinaryWriteService, armorBinaryWriteService, armorAddonBinaryWriteService,
             materialSwapBinaryWriteService, objectTemplateBinaryWriteService, runtimeScriptBinaryWriteService,
             runtimeScriptPackageService, runtimeScriptDeployService,
             faceGeomBinaryBuildService: faceGeomBinaryBuildService,
             faceGenPackService: faceGenPackService,
             packageBuildService: packageBuildService ?? new PackageBuildService(packageVerifier, policy,
                 root),
             packageInspectService: packageInspectService ?? new PackageInspectService(packageReader),
             packageVerifyService: packageVerifier,
             packageArchiveService: packageArchiveService ?? new PackageArchiveService(
                 packageVerifier, policy, root),
             pluginSurfaceAuditService: pluginSurfaceAuditService ?? new PluginSurfaceAuditService(
                 new BethesdaPluginReader(), policy, root),
             runtimeSmokeVerifyService: runtimeSmokeVerifyService ?? new RuntimeSmokeVerifyService(
                 policy, root),
             pluginDeployService: pluginDeployService ?? new PluginDeployService(
                 policy, root),
             faceGenDeployService: faceGenDeployService ?? new FaceGenDeployService(
                 policy, root),
             previewAnimationListService: animationListService,
             previewAnimationTreeService: previewAnimationTreeService ?? new PreviewAnimationTreeService(
                 animationListService),
             profileScanService: new ProfileScanService(
                 new PluginLoadOrderService(new BethesdaPluginReader(), policy,
                     root), policy,
                 root),
             recordListingService: new RecordListingService(new BethesdaPluginReader(), policy,
                 root),
             existingNpcEditService: existingNpcEditService,
             skyrimNativeFaceTintPipelineService: skyrimNativeFaceTintPipelineService,
             nativeFaceGenBatchService: nativeFaceGenBatchService,
              reviewedGameIntakeService: new ReviewedGameIntakeService(
                  new GameRootPreflightService(policy),
                  new PluginLoadOrderService(new BethesdaPluginReader(), policy,
                      root),
                  new BodySidecarInspectionService(policy,
                      root),
                  new GeneratedArtifactScanService(policy, new BethesdaPluginReader(),
                      root),
                  new BethesdaAssetIndexer()),
              raceMenuPresetCatalogService: new RaceMenuPresetCatalogService(
                  new PresetService(policy, root),
                  policy, root),
              skyrimRaceMenuPaintChoiceService: skyrimRaceMenuPaintChoiceService,
              bodySlideSliderPresetInspectionService:
                  new BodySlideSliderPresetInspectionService(
                      policy,
                      root),
              referencePresetAuthoringTransaction:
                  referencePresetAuthoringTransaction,
              referencePresetSessionService:
                  referencePresetSessionService,
              referencePresetExecutionRequestFileLoader:
                  referencePresetExecutionRequestFileLoader,
              skyrimFollowerFinishService:
                  skyrimFollowerFinishService,
              skyrimFollowerFinishRequestFileLoader:
                  skyrimFollowerFinishRequestFileLoader,
              npcVisualPreviewFactory:
                  npcVisualPreviewComposer is null
                      ? null
                      : new FixedPreviewFactory<
                          INpcVisualPreviewComposer>(
                              npcVisualPreviewComposer),
              faceGeomHairRegionsPreviewFactory:
                  faceGeomHairRegionsPreviewService is null
                      ? null
                      : new FixedPreviewFactory<
                          FaceGeomHairRegionsPreviewServices>(
                              new FaceGeomHairRegionsPreviewServices(
                                  faceGeomHairRegionsPreviewService,
                                  faceGeomHairRegionsVisualValidator ??
                                  new UnconfiguredNpcVisualPreviewVisualValidator())),
              actorAssemblyPreflightService:
                  actorAssemblyPreflightService,
              skyrimNpcVoiceService: skyrimNpcVoiceService,
              skyrimNpcDialogueService: skyrimNpcDialogueService), output, error), output, error);
    }

    internal static (
        CliRunner Runner,
        StringWriter Output,
        StringWriter Error)
        CreateRunnerForReferencePresetTests(
            IReferencePresetAuthoringTransaction transaction,
            IReferencePresetSessionService sessions,
            IRaceMenuNpcExecutionRequestFileLoader requestLoader) =>
        CreateRunner(
            referencePresetAuthoringTransaction:
                transaction,
            referencePresetSessionService: sessions,
            referencePresetExecutionRequestFileLoader:
                requestLoader);

    internal static (
        CliRunner Runner,
        StringWriter Output,
        StringWriter Error)
        CreateRunnerForFollowerFinishTests(
            ISkyrimFollowerFinishService? service,
            ISkyrimFollowerFinishRequestFileLoader? loader) =>
        CreateRunner(
            skyrimFollowerFinishService: service,
            skyrimFollowerFinishRequestFileLoader: loader);

    internal static (
        CliRunner Runner,
        StringWriter Output,
        StringWriter Error)
        CreateRunnerForNpcVisualPreviewTests(
            INpcVisualPreviewComposer? composer) =>
        CreateRunner(npcVisualPreviewComposer: composer);

    internal static (
        CliRunner Runner,
        StringWriter Output,
        StringWriter Error)
        CreateRunnerForFaceGeomHairRegionsTests(
            IFaceGeomHairRegionsPreviewService? previewService,
            INpcVisualPreviewVisualValidator?
                visualValidator = null) =>
        CreateRunner(
            faceGeomHairRegionsPreviewService: previewService,
            faceGeomHairRegionsVisualValidator:
                visualValidator,
            workspaceRoot: ActorwrightWorkspace.ResolveRoot());

    private sealed class FakePluginReader(params PluginInspection[] inspections) : IPluginReader
    {
        private readonly ImmutableDictionary<string, PluginInspection> _inspections = inspections
            .ToImmutableDictionary(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase);

        public ValueTask<PluginInspection> ReadAsync(PluginReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(request.PluginPath.Value);
            if (_inspections.TryGetValue(name, out var inspection)) return ValueTask.FromResult(inspection);
            throw new InvalidDataException($"No fake inspection registered for '{name}'.");
        }
    }

    private sealed class FakeDesktopLaunchService : IDesktopLaunchService
    {
        public DesktopLaunchRequest? LastRequest { get; private set; }
        public int LaunchCount { get; private set; }

        public DesktopLaunchResult Launch(DesktopLaunchRequest request)
        {
            LastRequest = request;
            LaunchCount++;
            return new DesktopLaunchResult(true, request.Executable,
                ImmutableArray<Diagnostic>.Empty);
        }
    }

    private sealed class FailOnEvaluationWorkspacePolicy : IWorkspacePolicy
    {
        public ImmutableArray<Diagnostic> Evaluate(
            WorkspacePath workspaceRoot, WorkspacePath outputRoot) =>
            throw new InvalidOperationException(
                "Workflow binding validation must precede policy evaluation.");

        public ImmutableArray<Diagnostic> EvaluateReadRoot(
            WorkspacePath workspaceRoot, WorkspacePath readRoot) =>
            throw new InvalidOperationException(
                "Workflow binding validation must precede policy evaluation.");
    }

    private sealed class FixedPreviewFactory<T>(T service) :
        IPreviewServiceFactory<T>
    {
        public ValueTask<PreviewServiceLease<T>> CreateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new PreviewServiceLease<T>(service));
        }
    }

    private sealed class FakeAssetIndexer(AssetIndex index) : IAssetIndexer
    {
        public ValueTask<AssetIndex> IndexAsync(AssetIndexRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(index with { Edition = request.Edition });
        }
    }

    private sealed class FakeFaceGenProviderResolutionService(FaceGenProviderResolutionArtifact artifact) :
        IFaceGenProviderResolutionService
    {
        public ValueTask<FaceGenProviderResolutionResult> ResolveAsync(
            FaceGenProviderResolutionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new FaceGenProviderResolutionResult(true, artifact,
                ImmutableArray<Diagnostic>.Empty));
        }
    }

    private sealed class FakeSkyrimNativeFaceTintPipelineService(
        SkyrimNativeFaceTintPipelineResult result) : ISkyrimNativeFaceTintPipelineService
    {
        public SkyrimNativeFaceTintPipelineRequest? Request { get; private set; }

        public ValueTask<SkyrimNativeFaceTintPipelineResult> BuildAsync(
            SkyrimNativeFaceTintPipelineRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeFaceGenBakeAllService(
        FaceGenBakeAllResult result) : IFaceGenBakeAllService
    {
        public FaceGenBakeAllRequest? Request { get; private set; }

        public ValueTask<FaceGenBakeAllResult> RunAsync(
            FaceGenBakeAllRequest request,
            IProgress<FaceGenBakeAllProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            progress?.Report(new FaceGenBakeAllProgress(
                1, FaceGenBakeAllProgressPhase.Discovering,
                0, 0, null, "Discovering."));
            progress?.Report(new FaceGenBakeAllProgress(
                2, FaceGenBakeAllProgressPhase.Completed,
                result.Outcomes.Length, result.Discovered, null, "Completed."));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeSkyrimRaceMenuPaintChoiceService(
        SkyrimRaceMenuPaintChoiceResult result) : ISkyrimRaceMenuPaintChoiceService
    {
        public SkyrimRaceMenuPaintChoiceRequest? Request { get; private set; }

        public ValueTask<SkyrimRaceMenuPaintChoiceResult> SearchAsync(
            SkyrimRaceMenuPaintChoiceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeFaceGenPackService(FaceGenPackResult result) : IFaceGenPackService
    {
        public ValueTask<FaceGenPackResult> PackAsync(FaceGenPackRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result with { OutputRoot = request.OutputRoot });
        }
    }

    private static Task TestRuntimeScriptVmadInspect()
    {
        var artifact = new RuntimeScriptVmadInspectArtifact(
            "1", "runtime-script-vmad-inspection", "fallout4", "Fixture.esp", new string('A', 64),
            "0x00000800", "NPCM_Manolov_ApplyFO4", ["IsFemale", "SchemaVersion"], 2, true, false);
        Assert(artifact.PropertyCount == artifact.PropertyNames.Length && artifact.NoWrite && !artifact.RuntimeProof &&
            artifact.ScriptName == "NPCM_Manolov_ApplyFO4",
            "VMAD inspection artifact did not preserve its read-only boundary.");
        return Task.CompletedTask;
    }

    private sealed class FakeFaceGeomBinaryBuildService : IFaceGeomBinaryBuildService
    {
        public bool Called { get; private set; }
        public bool OverlapCalled { get; private set; }
        public bool ReturnWrongBinding { get; set; }

        public ValueTask<FaceGeomBinaryBuildResult> BuildAsync(FaceGeomBinaryBuildRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Called) OverlapCalled = true;
            Called = true;
            var hash = new Sha256Hash(new string('B', 64));
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(request.SourceNif.Value)));
            var artifact = new FaceGeomBinaryBuildArtifact("1", "facegeom-binary-sandbox-build",
                request.Edition.ToWireName(), request.OutputPath.Value, hash.Value, 128, 1,
                ReturnWrongBinding ? new string('C', 64) : sourceHash,
                [new FaceGeomBinarySource("source.tri", new string('D', 64))],
                request.Morphs, new string('E', 64), new string('F', 64), "fixture", false);
            return ValueTask.FromResult(new FaceGeomBinaryBuildResult(true, artifact, hash,
                ImmutableArray<Diagnostic>.Empty));
        }
    }

    private sealed class FakeFaceTintBuildService : IFaceTintBuildService
    {
        public bool Called { get; private set; }
        public bool OverlapCalled { get; private set; }
        public bool ReturnWrongBinding { get; set; }

        public ValueTask<FaceTintBuildResult> BuildAsync(FaceTintBuildRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ProviderRoot is null || request.TextureOutputPath is null)
                OverlapCalled = true;
            Called = true;
            var hash = new Sha256Hash(new string('A', 64));
            var providerPath = request.Edition == GameEdition.Fallout4
                ? "Textures/Actors/Character/FaceCustomization/BoundFixture.esp/00000800_d.dds"
                : "Textures/Actors/Character/FaceGenData/FaceTint/BoundFixture.esp/00000800.dds";
            var providerFile = Path.Combine(request.ProviderRoot!.Value.Value,
                providerPath.Replace('/', Path.DirectorySeparatorChar));
            var providerHash = ReturnWrongBinding
                ? new string('Z', 64)
                : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(providerFile)));
            var artifact = new FaceTintBuildArtifact("1", "facetint-semantic-build",
                request.Edition.ToWireName(), request.NpcFormId?.ToString() ?? "0x00000800", "manifest",
                512, 512, "bgra8", 1, "preserve", "bgra8", [0, 0, 0, 1], [], [], hash.Value,
                request.TextureOutputPath?.Value, hash.Value, "provider-sampled",
                [new FaceTintProviderBinding(providerPath, providerHash, 512, 512)]);
            return ValueTask.FromResult(new FaceTintBuildResult(true, artifact, hash,
                ImmutableArray<Diagnostic>.Empty, hash));
        }
    }

    private sealed class FakeOutfitChoiceService(OutfitChoiceSearchResult result) : IOutfitChoiceService
    {
        public ValueTask<OutfitChoiceSearchResult> SearchAsync(OutfitChoiceSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result with { Edition = request.Edition });
        }
    }

    private sealed class FakeOutfitProposalService(OutfitProposalResult result) : IOutfitProposalService
    {
        public ValueTask<OutfitProposalResult> ProposeAsync(OutfitProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeOutfitBinaryWriteService(OutfitBinaryWriteResult result) : IOutfitBinaryWriteService
    {
        public ValueTask<OutfitBinaryWriteResult> WriteAsync(OutfitBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeLeveledListBinaryWriteService(LeveledListBinaryWriteResult result) : ILeveledListBinaryWriteService
    {
        public ValueTask<LeveledListBinaryWriteResult> WriteAsync(LeveledListBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorBinaryWriteService(ArmorBinaryWriteResult result) : IArmorBinaryWriteService
    {
        public ValueTask<ArmorBinaryWriteResult> WriteAsync(ArmorBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorAddonBinaryWriteService(ArmorAddonBinaryWriteResult result) : IArmorAddonBinaryWriteService
    {
        public ValueTask<ArmorAddonBinaryWriteResult> WriteAsync(ArmorAddonBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeMaterialSwapBinaryWriteService(MaterialSwapBinaryWriteResult result) : IMaterialSwapBinaryWriteService
    {
        public ValueTask<MaterialSwapBinaryWriteResult> WriteAsync(MaterialSwapBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeLeveledListProposalService(LeveledListProposalResult result) : ILeveledListProposalService
    {
        public ValueTask<LeveledListProposalResult> ProposeAsync(LeveledListProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeLeveledListResolveService(LeveledListResolveResult result) : ILeveledListResolveService
    {
        public ValueTask<LeveledListResolveResult> ResolveAsync(LeveledListResolveRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorProposalService(ArmorProposalResult result) : IArmorProposalService
    {
        public ArmorProposalRequest? LastRequest { get; private set; }

        public ValueTask<ArmorProposalResult> ProposeAsync(ArmorProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorDamageResistanceService(ArmorDamageResistanceResult result) : IArmorDamageResistanceService
    {
        public ValueTask<ArmorDamageResistanceResult> ProposeAsync(ArmorDamageResistanceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorAddonProposalService(ArmorAddonProposalResult result) : IArmorAddonProposalService
    {
        public ValueTask<ArmorAddonProposalResult> ProposeAsync(ArmorAddonProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorAddonModelProposalService(ArmorAddonModelProposalResult result) : IArmorAddonModelProposalService
    {
        public ValueTask<ArmorAddonModelProposalResult> ProposeAsync(ArmorAddonModelProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeMaterialSwapProposalService(MaterialSwapProposalResult result) : IMaterialSwapProposalService
    {
        public ValueTask<MaterialSwapProposalResult> ProposeAsync(MaterialSwapProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeObjectTemplateBinaryWriteService(ObjectTemplateBinaryWriteResult result) : IObjectTemplateBinaryWriteService
    {
        public ValueTask<ObjectTemplateBinaryWriteResult> WriteAsync(ObjectTemplateBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeRuntimeScriptBinaryWriteService(RuntimeScriptBinaryWriteResult result) : IRuntimeScriptBinaryWriteService
    {
        public ValueTask<RuntimeScriptBinaryWriteResult> WriteAsync(RuntimeScriptBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeObjectTemplateProposalService(ObjectTemplateProposalResult result) : IObjectTemplateProposalService
    {
        public ValueTask<ObjectTemplateProposalResult> ProposeAsync(ObjectTemplateProposalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeObjectTemplatePropertyProposalService(ObjectTemplatePropertyProposalResult result) : IObjectTemplatePropertyProposalService
    {
        public ValueTask<ObjectTemplatePropertyProposalResult> ProposeAsync(ObjectTemplatePropertyProposalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeChangeTrackingService(ChangeListResult result) : IChangeTrackingService
    {
        public ValueTask<ChangeListResult> ListAsync(ChangeListRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeChangeActionService(ChangeActionResult result) : IChangeActionService
    {
        public ValueTask<ChangeActionResult> UpdateAsync(ChangeActionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeRecordProposalService(RecordProposalResult result) : IRecordProposalService
    {
        public ValueTask<RecordProposalResult> ProposeAsync(RecordProposalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
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
