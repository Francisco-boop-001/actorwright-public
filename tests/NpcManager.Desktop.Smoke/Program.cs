using System.IO;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;
using NpcManager.Rendering;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private const string PreflightActionName = "Review copied Skyrim workspace";
    private const string Selector_TestAsyncCommand = "--test-async-command";
    private const string Selector_TestOnloadImages = "--test-onload-images";
    private const string Selector_TestBoundedProgress = "--test-bounded-progress";
    private const string Selector_TestStartupFailureBoundaries = "--test-startup-failure-boundaries";
    private const string Selector_TestMainWorkspaceResilience = "--test-main-workspace-resilience";
    private const string Selector_TestSkyrimNpcVoicePanel = "--test-skyrim-npc-voice-panel";
    private const string Selector_TestHairRegionProductionTransaction = "--test-hair-region-production-transaction";
    private const string Selector_TestNpcFinishWizard = "--test-npc-finish-wizard";
    private const string Selector_TestBinaryConsumerStartupComposition = "--test-binary-consumer-startup-composition";
    private const string Selector_TestNpcPreviewProfileAuthorityComposition = "--test-npc-preview-profile-authority-composition";
    private const string Selector_TestNpcBuildPreflight = "--test-npc-build-preflight";
    private const string Selector_TestHairRegionWizardWindow = "--test-hair-region-wizard-window";
    private const string Selector_SkyGui002Prepare = "--sky-gui-002-prepare";
    private const string Selector_SkyGui002PackagedAcceptance = "--sky-gui-002-packaged-acceptance";
    private const string Selector_SkyGui023PackagedAcceptance = "--sky-gui-023-packaged-acceptance";
    private const string Selector_ViewmodelOnly = "--viewmodel-only";

    private static SkyrimSelectiveAppearancePasteViewModel? selectivePasteRenderFixture;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (StandaloneSelectorInventory.TryList(
                    args,
                    typeof(Program),
                    Preview254ExternalSmpDesktopTestRegistry.Selectors))
                return 0;
            if (args is [Selector_TestAsyncCommand])
            {
                AsyncCommandTests.Run();
                Console.WriteLine("PASS shared desktop asynchronous command contract");
                return 0;
            }
            if (args is [Selector_TestOnloadImages])
            {
                OnLoadImageSourceTests.Run();
                Console.WriteLine("PASS eager desktop image loading and same-path refresh");
                return 0;
            }
            if (args is [Selector_TestBoundedProgress])
            {
                BoundedProgressTests.Run();
                RunNativeFaceGenBatchViewModelTest();
                Console.WriteLine("PASS bounded asynchronous desktop progress");
                return 0;
            }
            if (args is [Selector_TestStartupFailureBoundaries])
            {
                DesktopStartupFailureBoundaryTests.Run();
                Console.WriteLine(
                    "PASS bounded desktop startup failure boundaries");
                return 0;
            }
            if (args is [Selector_TestMainWorkspaceResilience])
            {
                RunSkyrimMainWorkspaceResilienceTest();
                DesktopCrashReporterTests.VerifyOperationFailureReport();
                Console.WriteLine(
                    "PASS main workspace failure capture and stale commands");
                return 0;
            }
            if (args is [Selector_TestSkyrimNpcVoicePanel])
            {
                RunSkyrimNpcVoiceDesktopTest();
                Console.WriteLine(
                    "PASS Skyrim NPC voice WAV drop panel");
                return 0;
            }
            if (args is
                [
                    Selector_TestHairRegionProductionTransaction
                ])
            {
                TestFaceGeomHairRegionsWizardProductionTransaction()
                    .GetAwaiter()
                    .GetResult();
                Console.WriteLine(
                    "PASS FaceGeom hair-region desktop production transaction");
                return 0;
            }
            if (args is [Selector_TestNpcFinishWizard])
            {
                SkyrimNpcFinishWizardTests.Run();
                Console.WriteLine("PASS Skyrim NPC Finish Core wizard");
                return 0;
            }
            if (args is [Selector_TestBinaryConsumerStartupComposition])
            {
                RunBinaryConsumerStartupCompositionTest();
                Console.WriteLine("PASS binary-consumer desktop startup composition");
                return 0;
            }
            if (args is [Selector_TestNpcPreviewProfileAuthorityComposition])
            {
                RunNpcPreviewProfileAuthorityCompositionTest();
                Console.WriteLine(
                    "PASS NPC preview profile authority composition");
                return 0;
            }
            if (args is [Selector_TestNpcBuildPreflight])
            {
                RunNpcBuildPreflightViewModelTest();
                Console.WriteLine("PASS NPC build preflight desktop flow");
                return 0;
            }
            if (args is
                [
                    Selector_TestHairRegionWizardWindow
                ])
            {
                TestFaceGeomHairRegionsWizardWindow();
                Console.WriteLine(
                    "PASS rendered FaceGeom hair-region wizard window");
                return 0;
            }
            if (args is
                [
                    Selector_SkyGui002Prepare,
                    var preparedIntakeRoot,
                    var preparedRealAssetRoot
                ])
                return PrepareSkyrimMainWorkspacePackagedAcceptance(
                    preparedIntakeRoot,
                    preparedRealAssetRoot);
            if (args is
                [
                    Selector_SkyGui002PackagedAcceptance,
                    var mainWorkspaceExecutable,
                    var intakeRoot,
                    var previewManifest,
                    var assetRoot,
                    var mainWorkspaceOutputRoot,
                    var mainWorkspaceScreenshotRoot,
                    var mainWorkspaceReportPath,
                    var mainWorkspaceRawReportPath,
                    var mainWorkspaceNegativeReportPath,
                    var mainWorkspaceVerifierPath
                ])
                return RunSkyrimMainWorkspacePackagedAcceptance(
                    mainWorkspaceExecutable,
                    intakeRoot,
                    previewManifest,
                    assetRoot,
                    mainWorkspaceOutputRoot,
                    mainWorkspaceScreenshotRoot,
                    mainWorkspaceReportPath,
                    mainWorkspaceRawReportPath,
                    mainWorkspaceNegativeReportPath,
                    mainWorkspaceVerifierPath);
            if (args is
                [
                    Selector_SkyGui023PackagedAcceptance,
                    var executable,
                    var sourceRoot,
                    var cancelledOutput,
                    var outputRoot,
                    var screenshotRoot,
                    var reportPath,
                    var rawReportPath,
                    var negativeReportPath,
                    var verifierPath
                ])
                return RunSkyrimSavePackagePackagedAcceptance(
                    executable,
                    sourceRoot,
                    cancelledOutput,
                    outputRoot,
                    screenshotRoot,
                    reportPath,
                    rawReportPath,
                    negativeReportPath,
                    verifierPath);
            if (args is ["--test-desktop-external-smp-composition"])
                return Preview254ExternalSmpDesktopTestRegistry.TryRun(args) ?? 1;
            int? externalFinishWizard =
                Preview254ExternalSmpDesktopTestRegistry.TryRun(args);
            if (externalFinishWizard is { } externalFinishWizardExitCode)
                return externalFinishWizardExitCode;
            RunSchema5DefaultCompositionTest();
            RunDesktopStartupOptionsTest();
            RunPresetCompletionViewModelTest();
            RunNpcBuildPreflightViewModelTest();
            RunPresetLoaderViewModelTest();
            RunAnimationPickerViewModelTest();
            RunTypedFormIdPickerViewModelTest();
            RunSkyrimHeadPartPickerViewModelTest();
            RunSkyrimHeadPartEditViewModelTest();
            RunSkyrimHeadPartPreviewServiceTest();
            RunSkyrimMeshPickerViewModelTest();
            RunSkyrimMeshPickerWindowContractTest();
            RunSkyrimRaceMenuPaintPickerViewModelTest();
            RunSkyrimFaceEditorViewModelTest();
            RunSkyrimFaceEditWorkspaceViewModelTest();
            RunSkyrimBodyEditorViewModelTest();
            RunSkyrimBodyEditWorkspaceViewModelTest();
            RunSkyrimSelectiveAppearancePasteViewModelTest();
            RunSkyrimSelectiveAppearancePasteWorkspaceViewModelTest();
            RunSkyrimOutfitEditorViewModelTest();
            RunSkyrimOutfitProductionWorkspaceViewModelTest();
            RunSkyrimLeveledListProductionWorkspaceViewModelTest();
            RunSkyrimLeveledListEditorViewModelTest();
            RunSkyrimLeveledEntryEditorViewModelTest();
            RunSkyrimArmorEditorViewModelTest();
            RunSkyrimArmorProductionWorkspaceViewModelTest();
            RunSkyrimArmorAddonReferenceEditorViewModelTest();
            RunSkyrimArmorAddonEditorViewModelTest();
            RunSkyrimArmorAddonEditorWindowContractTest();
            RunSkyrimCharGenOptionsEditorTest();
            RunSkyrimCharGenOptionsProductionWorkspaceTest();
            RunSkyrimMainWorkspaceViewModelTest();
            RunSkyrimMainWorkspaceProductionPreviewTest();
            RunSkyrimMainWorkspaceExternalPackagePreviewTest();
            RunSkyrimSavePackageViewModelTest();
            RunSkyrimLightingEditorViewModelTest();
            RunExistingNpcIdentityEditorTest();
            RunExistingNpcCollectionEditorTest();
            RunExistingNpcStatsEditorTest();
            RunExistingNpcViewModelTest();
            RunCancellationViewModelTest();
            RunNativeFaceGenBatchViewModelTest();
            RunReviewedWorkspaceViewModelTest();
            OnLoadImageSourceTests.Run();
            ReferencePresetAuthoringViewModelTests.Run();
            DesktopCrashReporterTests.Run();
            StartupInitializationCloseGateTests.Run();
            FaceGeomHairRegionsWizardTests.Run();
            SkyrimNpcFinishWizardTests.Run();
            if (args.Contains(Selector_ViewmodelOnly, StringComparer.Ordinal))
            {
                Console.WriteLine("RESULT PASS 52/52");
                return 0;
            }
            RunBlankNpcDefaultCompositionTest();
            RunFirstRenderSmoke();
            Console.WriteLine("RESULT PASS 55/55");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL desktop first-render smoke: {exception}");
            return 1;
        }
    }

    private static void RunBinaryConsumerStartupCompositionTest()
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string root = Path.Combine(
            repositoryRoot,
            "artifacts",
            "test-work",
            $"actorwright-desktop-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var labRoot = new WorkspacePath(root);
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));

            ExistingNpcEditDesktopContext context =
                ExistingNpcEditDesktopComposition.Create(policy, labRoot);

            Assert(!File.Exists(context.InitialRequest.InputPlugin.Value),
                "Binary-consumer startup unexpectedly depended on a packaged development fixture.");
            Assert(context.InitialRequest.InputPlugin.IsUnder(labRoot),
                "Binary-consumer default input escaped the admitted workspace root.");
            Assert(context.InitialRequest.ExpectedInputSha256.Value == new string('0', 64),
                "Binary-consumer missing-input placeholder was not explicitly unbound.");

            using ReferencePresetAuthoringViewModel referencePreset =
                ReferencePresetDesktopComposition.Create(
                    policy,
                    labRoot,
                    null!,
                    _ => false);
            Assert(referencePreset is not null,
                "Binary-consumer startup did not construct the reference-preset panel in typed-unavailable mode.");

            using SkyrimMainWorkspaceViewModel mainWorkspace =
                SkyrimMainWorkspaceDesktopComposition.Create(
                    policy,
                    labRoot);
            Assert(mainWorkspace is not null,
                "Binary-consumer startup did not construct the main workspace with optional visual services unavailable.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void RunNpcPreviewProfileAuthorityCompositionTest()
    {
        ApplicationResourcePath expectedPath = new(Path.Combine(
            Path.GetTempPath(),
            "controlled-npc-preview-profile-manifest.json"));
        Sha256Hash expectedHash = new(new string('A', 64));
        PreviewDependencyPreflightResult dependencies = new(
            [
                new PreviewDependencyAuthority(
                    "Blender profile",
                    @"K:\controlled-profile",
                    19,
                    expectedHash,
                    ApplicationOwned: false)
            ],
            []);

        (ApplicationResourcePath actualPath, Sha256Hash actualHash) =
            SkyrimMainWorkspaceDesktopComposition
                .BindNpcVisualPreviewRendererAuthority(
                    expectedPath,
                    dependencies);

        Assert(actualPath == expectedPath && actualHash == expectedHash,
            "Desktop NPC preview composition did not thread the exact admitted profile-manifest path/hash into renderer construction.");
    }

    private static void RunSkyrimOutfitEditorViewModelTest()
    {
        PluginName skyrim = new("Skyrim.esm");
        PluginName sourcePlugin = new("Source.esp");
        FormReference armorA = new(sourcePlugin, new FormId(0x100));
        FormReference armorB = new(sourcePlugin, new FormId(0x101));
        FormReference leveled = new(sourcePlugin, new FormId(0x200));
        var first = new SkyrimOutfitEditorItem(
            armorA, SkyrimOutfitEditorItemKind.Armor, "First armor", 0x04);
        var later = new SkyrimOutfitEditorItem(
            armorB, SkyrimOutfitEditorItemKind.Armor, "Later armor", 0x04);
        var list = new SkyrimOutfitEditorItem(
            leveled, SkyrimOutfitEditorItemKind.LeveledList,
            "Leveled accessory", 0, null, []);
        var existing = new OutfitChoiceCandidate(
            sourcePlugin,
            new FormId(0x900),
            "ExistingOutfit",
            "Existing outfit",
            [armorA.FormId, leveled.FormId],
            false,
            new OutfitChoiceProvenance(
                OutfitChoiceProvenanceKind.Base, sourcePlugin, [sourcePlugin]),
            [armorA, leveled]);

        WorkspacePath dataRoot = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures");
        WorkspacePath template = new(Path.Combine(dataRoot.Value, sourcePlugin.Value));
        WorkspacePath output = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\desktop.outfit-proposal.json");
        SkyrimOutfitEditorItem? childBasis = null;
        var viewModel = new SkyrimOutfitEditorViewModel(
            [existing],
            [first, later, list],
            dataRoot,
            template,
            new FormId(0x900),
            new FormId(0x901),
            output,
            (reference, seed) => reference == leveled
                ? [new SkyrimOutfitPreviewArmor(
                    new FormReference(skyrim, new FormId(seed == 77 ? 0x301U : 0x300U)),
                    0x08)]
                : throw new InvalidOperationException("Only LVLI candidates can be resolved."),
            (kind, current) =>
            {
                childBasis = current;
                return current?.DisplayName == "Child armor"
                    ? current with { DisplayName = current.DisplayName + " edited" }
                    : kind == SkyrimOutfitEditorItemKind.Armor
                    ? new SkyrimOutfitEditorItem(
                        new FormReference(sourcePlugin, new FormId(0x102)),
                        kind, "Child armor", 0x10)
                    : new SkyrimOutfitEditorItem(
                        new FormReference(sourcePlugin, new FormId(0x201)),
                        kind, "Child leveled list", 0, null, []);
            });

        Assert(viewModel.BrowseRows.Count == 3 &&
               viewModel.SelectedBrowseRow?.Kind == SkyrimOutfitChoiceKind.RecordDefault &&
               viewModel.TryAccept() &&
               viewModel.AcceptedResult?.Choice?.Kind == SkyrimOutfitChoiceKind.RecordDefault,
            "Outfit browser did not expose or accept record default, none, and existing choices.");
        viewModel.Cancel();
        Assert(!viewModel.IsAccepted && viewModel.AcceptedResult is null,
            "Outfit browser Cancel retained an accepted choice.");

        Assert(viewModel.TryBeginNew() && viewModel.IsAuthoring && !viewModel.IsOverride,
            "New outfit authoring did not start.");
        foreach (FormReference reference in new[] { armorA, armorB, leveled })
        {
            viewModel.SelectedAvailableItem = viewModel.AvailableItems.Single(
                item => item.Item.Reference == reference);
            Assert(viewModel.TryAddSelectedItem(),
                $"Could not add {reference} to the desktop outfit transaction.");
        }
        Assert(viewModel.DraftItems.Count == 3 &&
               viewModel.DraftItems[0].PreviewStatus.Contains("hidden", StringComparison.OrdinalIgnoreCase) &&
               viewModel.DraftItems[1].IsPreviewWinner,
            "Desktop outfit preview did not expose later-wins slot conflicts while retaining all rows.");

        viewModel.SelectedDraftItem = viewModel.DraftItems.Single(item =>
            item.Item.Reference == leveled);
        viewModel.RerollSeed = "77";
        Assert(viewModel.TryRerollSelected() &&
               viewModel.SelectedDraftItem?.Item.RealizationSeed == 77 &&
               viewModel.PreviewSummary.Contains("0x00000301", StringComparison.Ordinal),
            "Desktop LVLI reroll did not use and expose the deterministic seed-bound realization.");

        viewModel.SelectedDraftItem = viewModel.DraftItems.Single(item =>
            item.Item.Reference == armorB);
        Assert(viewModel.TryMoveSelectedItem(-1) &&
               viewModel.DraftItems.Select(item => item.Item.Reference)
                   .SequenceEqual([armorB, armorA, leveled]),
            "Desktop outfit item move did not update authored equip order.");
        viewModel.Reset();
        Assert(viewModel.DraftItems.Count == 0,
            "Desktop Reset did not restore the exact empty opening draft.");

        viewModel.SelectedAvailableItem = viewModel.AvailableItems.Single(item =>
            item.Item.Reference == armorA);
        Assert(viewModel.TryCreateChildItem(SkyrimOutfitEditorItemKind.Armor) &&
               childBasis == first &&
               viewModel.DraftItems.Single().Item.DisplayName == "Child armor" &&
               viewModel.TryEditSelectedChildItem() &&
               viewModel.DraftItems.Single().Item.DisplayName == "Child armor edited",
            "Typed ARMO child create/edit did not remain inside the outer transaction.");
        viewModel.Reset();

        viewModel.SelectedAvailableItem = viewModel.AvailableItems.Single(item =>
            item.Item.Reference == armorA);
        Assert(viewModel.TryAddSelectedItem() && viewModel.TryAccept() &&
               viewModel.AcceptedResult?.Proposal is { } proposal &&
               proposal.Items.SequenceEqual([armorA]),
            "Desktop Save did not return the typed ordered proposal.");
        viewModel.Cancel();
        Assert(viewModel.AcceptedResult is null && viewModel.DraftItems.Count == 0,
            "Desktop Cancel did not discard the whole authoring transaction.");

        viewModel.SelectedBrowseRow = viewModel.BrowseRows.Single(row =>
            row.Candidate == existing);
        Assert(viewModel.TryBeginOverride() && viewModel.IsOverride &&
               viewModel.DraftItems.Select(item => item.Item.Reference)
                   .SequenceEqual([armorA, leveled]),
            "Desktop override authoring lost qualified existing item order.");
        Console.WriteLine(
            "PASS Skyrim outfit editor view model covers browse, author, order, reroll, reset, Save, and exact Cancel.");
    }

    private static void RunSkyrimLeveledListEditorViewModelTest()
    {
        var childEditor = new SkyrimLeveledListEditorViewModel(
            [new EditorId("npcm_LVLI_Existing")]);
        Assert(!childEditor.CanAccept && !childEditor.TryAccept(),
            "The empty LVLI child editor accepted without a name.");
        childEditor.NameSuffix = "Accessories";
        childEditor.ChanceNone = 20;
        childEditor.MaxCount = 0;
        childEditor.CalculateAllLevels = true;
        childEditor.CalculateEachInCount = true;
        Assert(childEditor.CanAccept && childEditor.PackedFlags == "LVLF 0x03" &&
               childEditor.EditorIdPreview == "npcm_LVLI_Accessories" &&
               childEditor.TryAccept() && childEditor.AcceptedDocument is not null,
            "The LVLI child editor did not expose or accept its complete immutable header.");
        SkyrimLeveledListEditorDocument document = childEditor.AcceptedDocument ??
            throw new InvalidOperationException("The accepted LVLI document is missing.");

        PluginName provider = new("NpcManager.esp");
        SkyrimLeveledListOutfitItemResult child =
            SkyrimLeveledListEditorRules.AttachToOutfit(
                document, new FormReference(provider, new FormId(0x902)));
        Assert(child.Accepted && child.Item is not null,
            "The accepted LVLI header could not become a typed outfit child item.");

        WorkspacePath dataRoot = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures");
        var outfit = new SkyrimOutfitEditorViewModel(
            [],
            [],
            dataRoot,
            new WorkspacePath(Path.Combine(dataRoot.Value, provider.Value)),
            new FormId(0x900),
            new FormId(0x901),
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\lvli-child.outfit-proposal.json"),
            (_, _) => [],
            (kind, _) => kind == SkyrimOutfitEditorItemKind.LeveledList
                ? child.Item
                : null);
        Assert(outfit.TryBeginNew() &&
               outfit.TryCreateChildItem(SkyrimOutfitEditorItemKind.LeveledList) &&
               outfit.DraftItems.Single().Item.AuthoredLeveledList == document,
            "The Gate 015 child boundary did not retain the Gate 016 LVLI document inside the outer transaction.");
        Assert(outfit.TryAccept() &&
               outfit.AcceptedResult?.AuthoredLeveledLists.SequenceEqual([document]) == true,
            "The accepted outfit result lost the Gate 016 LVLI header proposal input.");
        outfit.Cancel();
        Assert(outfit.DraftItems.Count == 0 && outfit.AcceptedResult is null,
            "Outer outfit Cancel leaked the Gate 016 LVLI child document.");

        childEditor.Cancel();
        Assert(!childEditor.IsAccepted && childEditor.AcceptedDocument is null,
            "Child Cancel retained an accepted LVLI document.");
        Console.WriteLine(
            "PASS Skyrim leveled-list editor view model validates, accepts, nests, and rolls back transactionally.");
    }

    private static void RunSkyrimArmorEditorViewModelTest()
    {
        PluginName source = new("Source.esp");
        FormReference race = new(source, new FormId(0x900));
        FormReference addon = new(source, new FormId(0x901));
        FormReference keyword = new(source, new FormId(0x902));
        var opening = new SkyrimArmorEditorDocument(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorEditorIntent.BlankNew,
            ArmorProposalMode.New,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures\\Source.esp"),
            new FormId(0xA00),
            new EditorId("npcm_ARMO_Travel"),
            new FormId(0xB00),
            "Travel armor",
            race,
            null,
            false,
            string.Empty,
            25,
            4.5,
            15,
            0x04,
            null,
            null,
            null,
            null,
            new ArmorObjectBounds(-1, -2, -3, 4, 5, 6),
            null,
            null,
            null,
            [addon],
            [keyword]);
        var viewModel = new SkyrimArmorEditorViewModel(
            opening,
            [new EditorId("npcm_ARMO_Existing")],
            intent => intent == SkyrimArmorEditorIntent.BlankNew
                ? opening
                : null,
            document => document.ArmorAddons
                .Distinct()
                .Select(reference => new SkyrimArmorAddonSlotEvidence(reference, 0x40))
                .ToImmutableArray(),
            (_, _, _, _) => new SkyrimArmorPreviewResult(
                false, "Preview renderer is not connected in this foundation."),
            _ => new SkyrimArmorDeletionResult(
                false, "Dependency evidence is required before delete or revert."),
            (field, _) => field == SkyrimMeshTargetField.ArmorMaleWorld
                ? SkyrimMeshPickerRules.Use(new SkyrimMeshPickerCandidate(
                    new AssetPath("Meshes/armor/travel/world_m.nif"),
                    new AssetPath("armor/travel/world_m.nif"),
                    new AssetChoiceProviderEvidence(
                        AssetProviderKind.Loose,
                        "loose",
                        100,
                        new Sha256Hash(new string('a', 64))),
                    [new AssetChoiceProviderEvidence(
                        AssetProviderKind.Loose,
                        "loose",
                        100,
                        new Sha256Hash(new string('a', 64)))]))
                : SkyrimMeshPickerRules.Cancel());

        Assert(viewModel.CanSave &&
               viewModel.Identity.IntentLabel.Contains("blank", StringComparison.OrdinalIgnoreCase) &&
               viewModel.Collections.ArmorAddons.Single() == addon.ToString(),
            "Armor editor sections did not load the complete opening document.");
        Assert(viewModel.TryPickWorldModel(
                   SkyrimMeshTargetField.ArmorMaleWorld) &&
               viewModel.References.MaleWorldModel ==
                   "armor\\travel\\world_m.nif" &&
               !viewModel.TryPickWorldModel(
                   SkyrimMeshTargetField.ArmorFemaleWorld) &&
               viewModel.References.FemaleWorldModel.Length == 0 &&
               !viewModel.TryPickWorldModel(
                   SkyrimMeshTargetField.ArmorAddonMaleThirdPerson),
            "Armor world-model picking did not accept, cancel, and reject fields transactionally.");
        viewModel.Core.WeightText = "not-a-number";
        Assert(!viewModel.CanSave && !viewModel.TrySave(),
            "Invalid armor numeric text did not fail closed.");
        viewModel.Core.WeightText = "4.5";
        viewModel.Collections.CandidateReference = addon.ToString();
        Assert(viewModel.TryAddArmorAddon() &&
               viewModel.Collections.ArmorAddons.Count == 2,
            "The desktop Armor editor rejected a legal repeated ordered ARMA row.");
        Assert(viewModel.TryRecalculateSlots() &&
               viewModel.Core.SlotMaskText == "0x00000040",
            "The desktop Armor editor did not use complete typed slot evidence.");
        viewModel.Collections.CandidateReference = keyword.ToString();
        Assert(!viewModel.TryAddKeyword(),
            "The desktop Armor editor accepted a duplicate keyword.");
        Assert(!viewModel.TryPreview() &&
               viewModel.StatusMessage.Contains("not connected", StringComparison.OrdinalIgnoreCase),
            "A failed Armor preview was swallowed or reported as rendered.");
        Assert(!viewModel.TryDeleteOrRevert() &&
               viewModel.StatusMessage.Contains("dependency", StringComparison.OrdinalIgnoreCase),
            "Armor delete/revert ignored its dependency-evidence boundary.");
        Assert(!viewModel.TrySwitchIntent(SkyrimArmorEditorIntent.OverrideExisting) &&
               viewModel.Identity.Intent == SkyrimArmorEditorIntent.BlankNew,
            "A cancelled Armor intent switch did not preserve the current transaction.");
        Assert(viewModel.TrySave() && viewModel.AcceptedDocument is { } accepted &&
               accepted.ArmorAddons.Length == 2 && accepted.SlotMask == 0x40,
            "Desktop Armor Save did not return the exact immutable document.");
        viewModel.Cancel();
        Assert(!viewModel.IsAccepted && viewModel.AcceptedDocument is null &&
               viewModel.Collections.ArmorAddons.Count == 1 &&
               viewModel.Core.SlotMaskText == "0x00000004",
            "Desktop Armor Cancel did not restore the exact opening transaction and remove acceptance.");
        var addonRow = new SkyrimArmorAddonReferenceRow(
            addon,
            "Reviewed addon",
            new SkyrimArmorAddonRaceEvidence(true, race, race, []));
        Assert(viewModel.TryApplyArmorAddonReferenceRow(0, addonRow) &&
               viewModel.Collections.ArmorAddons.Single() == addon.ToString(),
            "The desktop Armor boundary did not consume a valid Gate 019 row.");
        string armorXaml = File.ReadAllText(Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src", "NpcManager.Desktop", "SkyrimArmorEditorWindow.xaml"));
        Assert(armorXaml.Contains("PickMaleWorldModel_OnClick", StringComparison.Ordinal) &&
               armorXaml.Contains("PickFemaleWorldModel_OnClick", StringComparison.Ordinal) &&
               armorXaml.Contains("Browse male world model", StringComparison.Ordinal) &&
               armorXaml.Contains("Browse female world model", StringComparison.Ordinal) &&
               armorXaml.Contains("Add armor addon reference", StringComparison.Ordinal) &&
               armorXaml.Contains("Replace selected armor addon reference", StringComparison.Ordinal) &&
               armorXaml.Contains("Remove selected armor addon reference", StringComparison.Ordinal) &&
               armorXaml.Contains("Move selected armor addon up", StringComparison.Ordinal) &&
               armorXaml.Contains("Move selected armor addon down", StringComparison.Ordinal) &&
               armorXaml.Contains("Add armor keyword reference", StringComparison.Ordinal) &&
               armorXaml.Contains("Remove selected armor keyword reference", StringComparison.Ordinal),
            "The Armor modal does not expose its typed mesh and collection automation entry points.");
        viewModel.Cancel();
        Console.WriteLine(
            "PASS Skyrim Armor editor view model validates sections, ordered references, typed preview failure, Save, and exact Cancel.");
    }

    private static void RunSkyrimArmorAddonReferenceEditorViewModelTest()
    {
        PluginName source = new("Source.esp");
        FormReference owningRace = new(source, new FormId(0x100));
        FormReference otherRace = new(source, new FormId(0x101));
        FormReference primaryReference = new(source, new FormId(0x200));
        FormReference editedReference = new(source, new FormId(0x201));
        SkyrimArmorAddonRaceEvidence primaryEvidence =
            new(true, owningRace, owningRace, []);
        var compatible = new SkyrimArmorAddonReferenceCandidate(
            primaryReference, new RecordSignature("ARMA"), "Primary addon",
            false, false, primaryEvidence);
        var stale = compatible with
        {
            Reference = new FormReference(source, new FormId(0x202)),
            DisplayName = "Stale addon",
            IsStale = true
        };
        var deleted = compatible with
        {
            Reference = new FormReference(source, new FormId(0x203)),
            DisplayName = "Deleted addon",
            IsDeleted = true
        };
        var incompatible = compatible with
        {
            Reference = new FormReference(source, new FormId(0x204)),
            DisplayName = "Wrong race addon",
            Compatibility = new SkyrimArmorAddonRaceEvidence(
                true, owningRace, otherRace, [])
        };
        var editor = new SkyrimArmorAddonReferenceEditorViewModel(
            owningRace,
            null,
            [compatible, stale, deleted, incompatible],
            _ => new SkyrimArmorAddonDeepEditResult(false, null));

        Assert(editor.Candidates.Count == 1 &&
               editor.Candidates.Single().AutomationName.Contains(
                   primaryReference.ToString(), StringComparison.Ordinal) &&
               editor.CatalogSummary.Contains("1 deleted", StringComparison.Ordinal) &&
               editor.CatalogSummary.Contains("1 stale", StringComparison.Ordinal) &&
               editor.CatalogSummary.Contains("1 incompatible", StringComparison.Ordinal),
            "The ARMA desktop catalog did not expose its fail-closed filtering evidence.");
        editor.SearchText = "not present";
        Assert(editor.Candidates.Count == 0 &&
               editor.EmptyMessage.Contains("No reviewed", StringComparison.Ordinal),
            "The ARMA desktop search did not expose an explicit empty state.");
        editor.SearchText = string.Empty;
        editor.SelectedCandidate = editor.Candidates.Single();
        Assert(editor.TryChoose() && editor.CanUse &&
               editor.CurrentReference == primaryReference.ToString() &&
               !editor.IsAccepted,
            "Choose did not update only the local compatible ARMA working row.");
        Assert(!editor.TryDeepEdit() && !editor.IsAccepted && editor.CanUse &&
               editor.StatusMessage.Contains("unchanged", StringComparison.OrdinalIgnoreCase),
            "A child Cancel did not preserve the current ARMA selection locally.");
        Assert(editor.TryUse() && editor.IsAccepted && !editor.WasAutoAccepted &&
               editor.AcceptedRow?.Reference == primaryReference,
            "Use ARMA did not return one exact fresh row.");
        editor.Cancel();
        Assert(!editor.IsAccepted && editor.AcceptedRow is null && !editor.CanUse,
            "ARMA reference Cancel retained an accepted row or local selection.");

        var opening = new SkyrimArmorAddonReferenceRow(
            primaryReference, "Primary addon", primaryEvidence);
        var edited = new SkyrimArmorAddonReferenceRow(
            editedReference,
            "Edited addon",
            new SkyrimArmorAddonRaceEvidence(
                true, owningRace, otherRace, [owningRace]));
        var deepEditor = new SkyrimArmorAddonReferenceEditorViewModel(
            owningRace,
            opening,
            [compatible],
            _ => new SkyrimArmorAddonDeepEditResult(true, edited));
        Assert(deepEditor.TryDeepEdit() && deepEditor.IsAccepted &&
               deepEditor.WasAutoAccepted &&
               deepEditor.AcceptedRow?.Reference == editedReference,
            "A valid committed child edit did not auto-accept the returned ARMA row.");
        deepEditor.Cancel();
        Assert(!deepEditor.IsAccepted && deepEditor.AcceptedRow is null &&
               deepEditor.CurrentReference == primaryReference.ToString(),
            "Cancel after a deep edit did not restore the exact opening row.");
        string referenceXaml = File.ReadAllText(Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src", "NpcManager.Desktop",
            "SkyrimArmorAddonReferenceEditorWindow.xaml"));
        Assert(referenceXaml.Contains(
                "automation:AutomationProperties.Name=\"{Binding CatalogSummary}\"",
                StringComparison.Ordinal) &&
               referenceXaml.Contains(
                   "Property=\"automation:AutomationProperties.Name\"",
                   StringComparison.Ordinal) &&
               referenceXaml.Contains(
                   "Value=\"{Binding AutomationName}\"",
                   StringComparison.Ordinal),
            "The live ARMA exclusion summary or selectable candidate identity is hidden from accessibility clients.");
        Console.WriteLine(
            "PASS Skyrim Armor-addon reference editor filters candidates, preserves Cancel, and auto-accepts only valid deep edits.");
    }

    private static void RunSkyrimArmorAddonEditorViewModelTest()
    {
        PluginName source = new("Source.esp");
        FormReference owningRace = new(source, new FormId(0x900));
        FormReference additionalRace = new(source, new FormId(0x901));
        FormReference secondAdditionalRace = new(source, new FormId(0x90B));
        var opening = new SkyrimArmorAddonEditorDocument(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorAddonEditorIntent.NewFromTemplate,
            ArmorAddonProposalMode.New,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures\\Source.esp"),
            new FormId(0xA00),
            new EditorId("npcm_ARMA_Travel"),
            new FormId(0xB00),
            new PluginName("NewAddon.esp"),
            true,
            "armor\\travel_m.nif",
            "armor\\travel_f.nif",
            "armor\\travel_1st_m.nif",
            "armor\\travel_1st_f.nif",
            0x14,
            owningRace,
            [additionalRace],
            new FormReference(source, new FormId(0x902)),
            new FormReference(source, new FormId(0x903)),
            new FormReference(source, new FormId(0x904)),
            new FormReference(source, new FormId(0x905)),
            new FormReference(source, new FormId(0x906)),
            new FormReference(source, new FormId(0x907)),
            4,
            5,
            true,
            false,
            7,
            123.25);
        WorkspacePath output = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\desktop-gate020.armor-addon-proposal.json");
        var provenance = new FormChoiceProvenance(
            FormChoiceProvenanceKind.Base,
            source,
            [source]);
        var referenceCatalog = new FormChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition,
            true,
            [
                new FormChoiceCandidate(
                    source, owningRace.FormId, new RecordSignature("RACE"),
                    "SourceRace", null, false, provenance),
                new FormChoiceCandidate(
                    source, secondAdditionalRace.FormId,
                    new RecordSignature("RACE"), "SecondRace", null, false,
                    provenance),
                new FormChoiceCandidate(
                    source, new FormId(0x907), new RecordSignature("ARTO"),
                    "SourceArt", null, false, provenance)
            ],
            []);
        var editor = new SkyrimArmorAddonEditorViewModel(
            opening,
            owningRace,
            [new EditorId("npcm_ARMA_Existing")],
            output,
            intent => intent == SkyrimArmorAddonEditorIntent.NewFromTemplate
                ? opening
                : null,
            request => new SkyrimArmorAddonPreviewResult(
                false,
                $"Preview {request.Scope} is not connected in this foundation."),
            _ => new SkyrimArmorAddonDeletionResult(
                false,
                "Dependency evidence is required before delete or revert."),
            (field, _) => field == SkyrimArmorAddonReferenceField.ArtObject
                ? new SkyrimArmorAddonReferencePickResult(true, null)
                : new SkyrimArmorAddonReferencePickResult(false, null),
            meshPathPicker: (field, _) => field switch
            {
                SkyrimMeshTargetField.ArmorAddonMaleThirdPerson =>
                    SkyrimMeshPickerRules.Use(new SkyrimMeshPickerCandidate(
                        new AssetPath("meshes/armor/travel/picked_m.nif"),
                        new AssetPath("armor/travel/picked_m.nif"),
                        new AssetChoiceProviderEvidence(
                            AssetProviderKind.Loose,
                            "loose",
                            100,
                            new Sha256Hash(new string('b', 64))),
                        [new AssetChoiceProviderEvidence(
                            AssetProviderKind.Loose,
                            "loose",
                            100,
                            new Sha256Hash(new string('b', 64)))])),
                SkyrimMeshTargetField.ArmorAddonMaleFirstPerson =>
                    new SkyrimMeshPickerSelection(
                        true,
                        new AssetPath("armor/travel/forged.nif"),
                        new SkyrimMeshPickerCandidate(
                            new AssetPath("meshes/armor/travel/other.nif"),
                            new AssetPath("armor/travel/other.nif"),
                            new AssetChoiceProviderEvidence(
                                AssetProviderKind.Loose,
                                "loose",
                                100,
                                new Sha256Hash(new string('c', 64))),
                            [new AssetChoiceProviderEvidence(
                                AssetProviderKind.Loose,
                                "loose",
                                100,
                                new Sha256Hash(new string('c', 64)))])),
                _ => SkyrimMeshPickerRules.Cancel()
            },
            referenceCatalog: referenceCatalog);

        Assert(editor.CanSave &&
               editor.Identity.Intent ==
                   SkyrimArmorAddonEditorIntent.NewFromTemplate &&
               editor.Models.MaleModel == opening.MaleModel &&
               editor.Slots.SelectedMask == opening.SlotMask &&
               editor.RaceAndSkin.AdditionalRaces.Single() ==
                   additionalRace.ToString() &&
               editor.Data.WeaponAdjustText == "123.25",
            "Armor-addon editor sections did not load the complete opening document.");
        Assert(editor.TryPickModel(
                   SkyrimMeshTargetField.ArmorAddonMaleThirdPerson) &&
               editor.Models.MaleModel == "armor\\travel\\picked_m.nif" &&
               !editor.TryPickModel(
                   SkyrimMeshTargetField.ArmorAddonFemaleThirdPerson) &&
               editor.Models.FemaleModel == opening.FemaleModel &&
               !editor.TryPickModel(
                   SkyrimMeshTargetField.ArmorAddonMaleFirstPerson) &&
               editor.Models.MaleFirstPersonModel ==
                   opening.MaleFirstPersonModel &&
               editor.StatusMessage.Contains(
                   "refused", StringComparison.OrdinalIgnoreCase) &&
               !editor.TryPickModel(
                   SkyrimMeshTargetField.ArmorMaleWorld),
            "Armor-addon mesh picking did not accept, cancel, reject forged output, and reject foreign fields transactionally.");
        Assert(editor.TryPickReference(
                   SkyrimArmorAddonReferenceField.ArtObject) &&
               editor.RaceAndSkin.ArtObject.Length == 0,
            "The typed Armor-addon picker boundary did not preserve an explicit clear.");
        editor.RaceAndSkin.ArtObject = opening.ArtObject?.ToString() ??
            string.Empty;
        TypedFormIdPickerViewModel artPicker = editor.CreateReferencePicker(
            SkyrimArmorAddonReferenceField.ArtObject);
        Assert(artPicker.VisibleRows.Any(item => item.IsNull) &&
               artPicker.AllowedSignatures == "ARTO",
            "The Armor-addon art picker did not expose a typed optional NULL choice.");
        artPicker.SelectedRow = artPicker.VisibleRows.Single(item => item.IsNull);
        Assert(artPicker.TryAccept() &&
               editor.ApplyReferencePicker(
                   SkyrimArmorAddonReferenceField.ArtObject, artPicker) &&
               editor.RaceAndSkin.ArtObject.Length == 0,
            "The production typed picker did not apply an explicit optional clear.");
        TypedFormIdPickerViewModel racePicker = editor.CreateReferencePicker(
            SkyrimArmorAddonReferenceField.PrimaryRace);
        Assert(racePicker.AllowedSignatures == "RACE" &&
               racePicker.VisibleRows.All(item => !item.IsNull),
            "The required primary-race picker exposed an invalid NULL choice.");
        editor.RaceAndSkin.ArtObject = opening.ArtObject?.ToString() ??
            string.Empty;
        editor.Data.WeaponAdjustText = "not-a-number";
        Assert(!editor.CanSave && !editor.TrySave(),
            "Invalid Armor-addon numeric text did not fail closed.");
        editor.Data.WeaponAdjustText = "123.25";
        editor.RaceAndSkin.CandidateAdditionalRace =
            additionalRace.ToString();
        Assert(!editor.TryAddAdditionalRace(),
            "The desktop Armor-addon editor accepted a duplicate additional race.");
        editor.RaceAndSkin.CandidateAdditionalRace =
            secondAdditionalRace.ToString();
        Assert(editor.TryAddAdditionalRace(),
            "The desktop Armor-addon editor refused a distinct qualified additional race.");
        editor.RaceAndSkin.SelectedAdditionalRaceIndex = 1;
        Assert(editor.TryMoveAdditionalRace(-1) &&
               editor.RaceAndSkin.AdditionalRaces.SequenceEqual(
                   [secondAdditionalRace.ToString(), additionalRace.ToString()]) &&
               !editor.TryMoveAdditionalRace(-1),
            "The ordered additional-race editor did not move exactly once or fail closed at its boundary.");
        Assert(editor.TryMoveAdditionalRace(1) &&
               editor.RaceAndSkin.SelectedAdditionalRaceIndex == 1 &&
               editor.TryRemoveAdditionalRace() &&
               editor.RaceAndSkin.AdditionalRaces.SequenceEqual(
                   [additionalRace.ToString()]),
            "The ordered additional-race editor did not preserve selection or remove the intended row.");
        editor.Preview.Scope = SkyrimArmorAddonPreviewScope.FullArmor;
        editor.Preview.IncludeBody = true;
        Assert(!editor.TryPreview() &&
               editor.StatusMessage.Contains(
                   "not connected", StringComparison.OrdinalIgnoreCase),
            "A failed Armor-addon preview was swallowed or reported as rendered.");
        Assert(!editor.TryDeleteOrRevert() &&
               editor.StatusMessage.Contains(
                   "dependency", StringComparison.OrdinalIgnoreCase),
            "Armor-addon delete/revert ignored its dependency-evidence boundary.");
        Assert(!editor.TrySwitchIntent(
                    SkyrimArmorAddonEditorIntent.OverrideExisting) &&
               editor.Identity.Intent ==
                   SkyrimArmorAddonEditorIntent.NewFromTemplate,
            "A cancelled Armor-addon intent switch changed the transaction.");
        Assert(editor.TrySave(),
            "Desktop Armor-addon Save was refused: " +
            editor.ValidationMessage);
        SkyrimArmorAddonEditorDocument accepted =
            editor.AcceptedDocument ?? throw new InvalidOperationException(
                "Desktop Armor-addon Save returned no document.");
        Assert(accepted.Intent == opening.Intent &&
               accepted.Mode == opening.Mode &&
               accepted.SourcePlugin == opening.SourcePlugin &&
               accepted.SourceFormId == opening.SourceFormId &&
               accepted.EditorId == opening.EditorId &&
               accepted.TargetFormId == opening.TargetFormId &&
               accepted.TargetPlugin == opening.TargetPlugin &&
               accepted.SeedFromSource == opening.SeedFromSource &&
               accepted.MaleModel == "armor\\travel\\picked_m.nif" &&
               accepted.FemaleModel == opening.FemaleModel &&
               accepted.MaleFirstPersonModel ==
                   opening.MaleFirstPersonModel &&
               accepted.FemaleFirstPersonModel ==
                   opening.FemaleFirstPersonModel &&
               accepted.SlotMask == opening.SlotMask &&
               accepted.Race == opening.Race &&
               accepted.AdditionalRaces.SequenceEqual(
                   opening.AdditionalRaces) &&
               accepted.MaleSkinTexture == opening.MaleSkinTexture &&
               accepted.FemaleSkinTexture == opening.FemaleSkinTexture &&
               accepted.MaleSkinTextureSwapList ==
                   opening.MaleSkinTextureSwapList &&
               accepted.FemaleSkinTextureSwapList ==
                   opening.FemaleSkinTextureSwapList &&
               accepted.FootstepSet == opening.FootstepSet &&
               accepted.ArtObject == opening.ArtObject &&
               accepted.MalePriority == opening.MalePriority &&
               accepted.FemalePriority == opening.FemalePriority &&
               accepted.MaleWeightSliderEnabled ==
                   opening.MaleWeightSliderEnabled &&
               accepted.FemaleWeightSliderEnabled ==
                   opening.FemaleWeightSliderEnabled &&
               accepted.DetectionSound == opening.DetectionSound &&
               accepted.WeaponAdjust == opening.WeaponAdjust,
            "Desktop Armor-addon Save changed a complete document value.");
        Assert(editor.AcceptedRow is { } acceptedRow &&
               acceptedRow.Reference == new FormReference(
                   new PluginName("NewAddon.esp"), new FormId(0xB00)) &&
               acceptedRow.AuthoredProposal is { CompleteDocument: true },
            "Desktop Armor-addon Save did not return the exact Gate 019 row.");
        editor.Models.MaleModel = "armor\\changed.nif";
        editor.Cancel();
        Assert(!editor.IsAccepted &&
               editor.AcceptedDocument is null &&
               editor.AcceptedRow is null &&
               editor.Models.MaleModel == opening.MaleModel &&
               editor.RaceAndSkin.AdditionalRaces.Single() ==
                   additionalRace.ToString(),
            "Desktop Armor-addon Cancel did not restore the exact opening transaction.");
        Console.WriteLine(
            "PASS Skyrim Armor-addon editor view model preserves sections, typed failures, Gate 019 Save, and exact Cancel.");
    }

    private static void RunSkyrimArmorAddonEditorWindowContractTest()
    {
        Type windowType = typeof(SkyrimArmorAddonEditorWindow);
        Assert(typeof(Window).IsAssignableFrom(windowType) &&
               windowType.GetConstructor(
               [
                   typeof(SkyrimArmorAddonEditorViewModel)
               ]) is not null,
            "The Armor-addon editor is not an owner-modal WPF window with a typed view-model boundary.");
        string xamlPath = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src", "NpcManager.Desktop",
            "SkyrimArmorAddonEditorWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);
        foreach (string required in new[]
                 {
                     "Intent and source status",
                     "Identity",
                     "Models",
                     "PickMaleThirdPersonModel_OnClick",
                     "PickFemaleThirdPersonModel_OnClick",
                     "PickMaleFirstPersonModel_OnClick",
                     "PickFemaleFirstPersonModel_OnClick",
                     "Browse male third-person model",
                     "Browse female third-person model",
                     "Browse male first-person model",
                     "Browse female first-person model",
                     "Slots",
                     "Race and skin",
                     "MoveRaceUp_OnClick",
                     "MoveRaceDown_OnClick",
                     "AutomationProperties.Name=\"Primary Armor-addon race\"",
                     "AutomationProperties.Name=\"Ordered additional Armor-addon races\"",
                     "AutomationProperties.Name=\"Male Armor-addon priority\"",
                     "AutomationProperties.Name=\"Armor-addon detection sound\"",
                     "AutomationProperties.Name=\"Armor-addon weapon adjustment\"",
                     "Data",
                     "Preview controls",
                     "Validation and authority",
                     "Delete / _revert",
                     "_Save",
                     "_Cancel",
                     "IsDefault=\"True\"",
                     "IsCancel=\"True\"",
                     "ShowInTaskbar=\"False\"",
                     "WindowStartupLocation=\"CenterOwner\""
                 })
            Assert(xaml.Contains(required, StringComparison.Ordinal),
                $"The Armor-addon modal is missing required surface token '{required}'.");
        Console.WriteLine(
            "PASS Skyrim Armor-addon modal exposes every supported section, action, keyboard boundary, and authority notice.");
    }

    private static void RunSkyrimCharGenOptionsEditorTest()
    {
        CharGenOptions defaults = CharGenOptionsDefaults.For(
            GameEdition.SkyrimSpecialEdition);
        CharGenOptions opening = defaults with
        {
            Convention = defaults.Convention with
            {
                NormalSpecular = defaults.Convention.NormalSpecular with
                {
                    WorkingSpace = FaceTintWorkingSpace.G24
                },
                Swap = defaults.Convention.Swap with
                {
                    Framework = FaceTintFramework.ModSrc
                },
                SeedMode = FaceTintSeedMode.BaseTexture,
                SeedConstant = [0.2, 0.4, 0.6]
            }
        };
        var editor = new SkyrimCharGenOptionsEditorViewModel(opening);
        Assert(editor.CanSave &&
               editor.Texture.DiffuseResolution ==
                   opening.DiffuseResolution &&
               editor.Sort.TintRules.Count ==
                   opening.TintSort.TintRules.Length,
            "The Skyrim CharGen desktop sections did not load the complete opening document.");

        editor.Texture.PerLayerResolution = false;
        editor.Texture.DiffuseResolution =
            FaceGenChannelResolution.R2048;
        editor.Texture.DiffuseCompression =
            FaceGenDiffuseCompression.Bc7;
        Assert(editor.Texture.NormalResolution ==
                   FaceGenChannelResolution.R2048 &&
               editor.Texture.NormalCompression ==
                   FaceGenNormalSpecularCompression.Uncompressed,
            "The desktop uniform mode did not expose the derived Skyrim Normal state.");
        editor.Sort.TintCandidateKey = FaceTintSseSortKey.Coverage;
        editor.Sort.TintCandidateDescending = true;
        Assert(editor.TryAddSortRule(SkyrimCharGenSortList.Tint) &&
               editor.Sort.TintRules.Count == 2 &&
               !editor.TryAddSortRule(SkyrimCharGenSortList.Tint),
            "The desktop tint-order editor did not append once and refuse a duplicate key.");
        editor.Sort.SelectedTintIndex = 1;
        Assert(editor.TryMoveSortRule(SkyrimCharGenSortList.Tint, -1) &&
               editor.Sort.TintRules[0].Key ==
                   (int)FaceTintSseSortKey.Coverage,
            "The desktop tint-order editor did not move the selected rule.");
        Assert(editor.TrySave() && editor.AcceptedOptions is { } accepted &&
               accepted.Convention.NormalSpecular ==
                   opening.Convention.NormalSpecular &&
               accepted.Convention.Swap == opening.Convention.Swap &&
               accepted.Convention.SeedMode == opening.Convention.SeedMode &&
               accepted.Convention.SeedConstant.SequenceEqual(
                   opening.Convention.SeedConstant),
            "Saving visible Skyrim options lost hidden complete convention values.");

        editor.Texture.GenerateTga = true;
        editor.Texture.BakeSseRaceMenuOverlays = false;
        editor.Convention.SeedDiffuseG22 =
            !editor.Convention.SeedDiffuseG22;
        int tintCount = editor.Sort.TintRules.Count;
        editor.ResetTexture();
        Assert(!editor.Texture.GenerateTga &&
               !editor.Texture.BakeSseRaceMenuOverlays &&
               editor.Sort.TintRules.Count == tintCount &&
               editor.Convention.SeedDiffuseG22 !=
                   opening.Convention.SeedDiffuseG22,
            "The desktop texture reset leaked into fixes, convention, or sort sections.");
        editor.ResetConvention();
        Assert(editor.Convention.SeedDiffuseG22 ==
                   defaults.Convention.SeedDiffuseG22 &&
               editor.Sort.TintRules.Count == tintCount,
            "The desktop convention reset leaked into the ordered lists.");
        editor.ResetSort();
        Assert(editor.Sort.TintRules.Count ==
                   defaults.TintSort.TintRules.Length,
            "The desktop sort reset did not load the exact Skyrim defaults.");

        editor.Cancel();
        Assert(!editor.IsAccepted && editor.AcceptedOptions is null &&
               editor.Texture.DiffuseResolution == opening.DiffuseResolution &&
               editor.Texture.BakeSseRaceMenuOverlays ==
                   opening.BakeSseRaceMenuOverlays &&
               editor.Convention.SeedDiffuseG22 ==
                   opening.Convention.SeedDiffuseG22 &&
               editor.Sort.TintRules.Count ==
                   opening.TintSort.TintRules.Length,
            "Skyrim CharGen Cancel did not restore the exact opening transaction.");

        Type windowType = typeof(SkyrimCharGenOptionsEditorWindow);
        Assert(typeof(Window).IsAssignableFrom(windowType) &&
               windowType.GetConstructor(
               [
                   typeof(SkyrimCharGenOptionsEditorViewModel)
               ]) is not null,
            "The Skyrim CharGen options editor is not an owner-modal typed WPF window.");
        string xaml = File.ReadAllText(Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src", "NpcManager.Desktop",
            "SkyrimCharGenOptionsEditorWindow.xaml"));
        foreach (string required in new[]
                 {
                     "Texture output",
                     "FaceTint conventions",
                     "Tint _order",
                     "Authority",
                     "ResetTexture_OnClick",
                     "ResetConvention_OnClick",
                     "ResetSort_OnClick",
                     "AddTintRule_OnClick",
                     "AddOverlayRule_OnClick",
                     "_Save options",
                     "_Cancel",
                     "IsDefault=\"True\"",
                     "IsCancel=\"True\"",
                     "ShowInTaskbar=\"False\"",
                     "WindowStartupLocation=\"CenterOwner\""
                 })
            Assert(xaml.Contains(required, StringComparison.Ordinal),
                $"The Skyrim CharGen modal is missing required surface token '{required}'.");
        Assert(!xaml.Contains("Ghoul", StringComparison.OrdinalIgnoreCase) &&
               !xaml.Contains("Mouth vanilla", StringComparison.OrdinalIgnoreCase),
            "The Skyrim CharGen modal exposes Fallout-only fixes.");
        Console.WriteLine(
            "PASS Skyrim CharGen options desktop preserves hidden values, derivation, resets, ordered rules, Save, Cancel, and modal contract.");
    }

    private static void RunSkyrimLeveledEntryEditorViewModelTest()
    {
        var reference = new FormReference(
            new PluginName("NpcManager.esp"), new FormId(0x800));
        var candidate = new SkyrimLeveledEntryCandidate(
            reference, SkyrimOutfitEditorItemKind.Armor, "Travel armor");
        var add = new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Add, candidate);
        Assert(add.CanAccept && add.LevelText == "1" && add.CountText == "1",
            "New Skyrim LVLO defaults did not begin at Level 1 and Count 1.");
        add.LevelText = "32768";
        Assert(!add.CanAccept && !add.TryAccept() &&
               add.ValidationMessage.Contains("32767", StringComparison.Ordinal),
            "The desktop LVLO editor accepted an unrepresentable signed value.");
        add.LevelText = "10";
        add.CountText = "2";
        Assert(add.CanAccept && add.TryAccept() &&
               add.AcceptedEntry is { Level: 10, Count: 2, ChanceNone: 0 } &&
               add.AcceptedEntry.Item == reference,
            "The desktop LVLO editor lost accepted add values or immutable reference identity.");
        LeveledListEntryProposal accepted = add.AcceptedEntry ??
            throw new InvalidOperationException("The accepted LVLO row is missing.");

        var edit = new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Edit, candidate, accepted);
        Assert(edit.LevelText == "10" && edit.CountText == "2" && edit.CanAccept,
            "Edit mode did not retain the exact selected LVLO row.");
        edit.CountText = "3";
        Assert(edit.TryAccept() && edit.AcceptedEntry is { Count: 3 } replacement &&
               replacement.Item == reference,
            "Edit mode did not return one exact replacement LVLO row.");
        edit.Cancel();
        Assert(!edit.IsAccepted && edit.AcceptedEntry is null,
            "Leveled-entry Cancel retained an accepted replacement.");
        var unsupported = new SkyrimLeveledEntryEditorViewModel(
            SkyrimLeveledEntryEditorMode.Edit, candidate,
            accepted with { ChanceNone = 1 });
        Assert(!unsupported.CanAccept &&
               unsupported.ValidationMessage.Contains(
                   "unsupported", StringComparison.OrdinalIgnoreCase),
            "Edit mode silently normalized unsupported Skyrim per-entry chance data.");
        Console.WriteLine(
            "PASS Skyrim leveled-entry editor view model preserves add/edit values and exact Cancel.");
    }

    private static void RunSkyrimSelectiveAppearancePasteViewModelTest()
    {
        SkyrimSelectiveAppearancePasteDocument source = Document("source", 70F, true, 0x700);
        SkyrimSelectiveAppearancePasteDocument target = Document("target", 30F, false, 0x800);
        var viewModel = new SkyrimSelectiveAppearancePasteViewModel(
            source, target, "Copied source", "Target NPC");

        Assert(viewModel.SelectedCount == 10 && viewModel.HasSelection &&
               viewModel.BodyOptions.Length == 3 &&
               viewModel.FaceOptions.Length == 5 &&
               viewModel.RecordOptions.Length == 2,
            "Selective-paste defaults or Skyrim group membership drifted.");

        viewModel.DeselectAll();
        Assert(viewModel.SelectedCount == 0 && !viewModel.HasSelection &&
               viewModel.TryAccept() && !viewModel.HasAcceptedChanges &&
               viewModel.AcceptedSelection is { Categories.IsDefaultOrEmpty: true } &&
               viewModel.AcceptedDocument is not null &&
               SkyrimSelectiveAppearancePasteRules.Equivalent(
                   target, viewModel.AcceptedDocument),
            "Deselect All did not produce an explicit exact non-dirty no-op.");

        SkyrimSelectiveAppearancePasteOptionViewModel tints =
            viewModel.Options.Single(item =>
                item.Category == SkyrimAppearancePasteCategory.FaceTints);
        tints.IsSelected = true;
        Assert(viewModel.AcceptedDocument is null && !viewModel.IsAccepted &&
               viewModel.TryAccept() && viewModel.HasAcceptedChanges &&
               viewModel.AcceptedDocument is { } tintOnly &&
               tintOnly.Face.Tints.SequenceEqual(source.Face.Tints) &&
               tintOnly.Body.Weight == target.Body.Weight &&
               tintOnly.Face.Parts == target.Face.Parts,
            "A category toggle retained stale acceptance or coupled tint paste to another carrier.");

        viewModel.SelectAll();
        Assert(viewModel.TryAccept() && viewModel.AcceptedDocument is { } all &&
               SkyrimSelectiveAppearancePasteRules.Equivalent(source, all),
            "Select All did not prepare the complete source document.");
        viewModel.Cancel();
        Assert(!viewModel.IsAccepted && viewModel.AcceptedDocument is null &&
               viewModel.AcceptedSelection is null &&
               !viewModel.HasAcceptedChanges &&
               viewModel.AuthorityNotice.Contains("runtime", StringComparison.OrdinalIgnoreCase),
            "Cancel retained a selective-paste commit or false authority state.");

        selectivePasteRenderFixture = new SkyrimSelectiveAppearancePasteViewModel(
            source, target, "Copied source", "Target NPC");
        Console.WriteLine(
            "PASS Skyrim selective-paste view model covers defaults, all, none, independence, and exact Cancel.");

        static SkyrimSelectiveAppearancePasteDocument Document(
            string prefix,
            float weight,
            bool charGen,
            uint formBase)
        {
            PluginName plugin = new($"{prefix}.esp");
            FormReference Reference(uint offset) =>
                new(plugin, new FormId(formBase + offset));
            ImmutableArray<RaceMenuBodyOverlay> overlays =
            [
                new RaceMenuBodyOverlay(
                    "Body [Ovl0]", $"{prefix}-body.dds", null, [], 1F, []),
                new RaceMenuBodyOverlay(
                    "Face [Ovl0]", $"{prefix}-face.dds", null, [], 1F, [])
            ];
            var body = new SkyrimBodyEditorDocument(
                weight,
                [new BodySlideSliderValue("Waist", weight / 100F)],
                [],
                [],
                overlays);
            var face = new SkyrimFaceEditorDocument(
                new SkyrimFaceEditorParts(
                [
                    new NpcHeadPartSelection(Reference(1), NpcHeadPartType.Face),
                    new NpcHeadPartSelection(Reference(2), NpcHeadPartType.Hair)
                ],
                OptionalFormReference.Set(Reference(3)),
                Reference(4),
                charGen),
                new SkyrimFaceMorphPatch(
                    Enumerable.Range(0, 18)
                        .Select(index => (weight + index) / 100F)
                        .ToImmutableArray(),
                    0F,
                    [0, 1, 2, 3]),
                [new SkyrimRaceMenuCustomMorphValue($"{prefix}-morph", 0.25F)],
                [new SkyrimFaceEditorTintLayer(
                    new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 50, 0),
                    true,
                    new AssetPath($"actors/{prefix}/tint.dds"),
                    null)],
                [new RaceMenuSculptPart($"{prefix}-head", 12, [], true, true)],
                overlays);
            return new SkyrimSelectiveAppearancePasteDocument(
                face,
                body,
                new NpcOutfitSnapshot(Reference(5), Reference(6)));
        }
    }

    private static void RunSkyrimBodyEditorViewModelTest()
    {
        SkyrimRaceMenuPaintChoiceCandidate bodyPaint = BodyPaint(
            SkyrimRaceMenuPaintCategory.Body, "Body paint", "actors/paint/body-new.dds");
        SkyrimRaceMenuPaintChoiceCandidate handsPaint = BodyPaint(
            SkyrimRaceMenuPaintCategory.Hands, "Hands paint", "actors/paint/hands.dds");
        SkyrimRaceMenuPaintChoiceCandidate feetPaint = BodyPaint(
            SkyrimRaceMenuPaintCategory.Feet, "Feet paint", "actors/paint/feet.dds");
        var paints = new Dictionary<SkyrimRaceMenuPaintCategory, SkyrimRaceMenuPaintChoiceResult>
        {
            [SkyrimRaceMenuPaintCategory.Body] = new(true, [bodyPaint], null, []),
            [SkyrimRaceMenuPaintCategory.Hands] = new(true, [handsPaint], null, []),
            [SkyrimRaceMenuPaintCategory.Feet] = new(true, [feetPaint], null, [])
        }.ToImmutableDictionary();
        var limits = new Dictionary<BodyOverlayTarget, int>
        {
            [BodyOverlayTarget.Body] = 4,
            [BodyOverlayTarget.Hands] = 4,
            [BodyOverlayTarget.Feet] = 4
        }.ToImmutableDictionary();
        var catalogs = new SkyrimBodyEditorCatalogs(
            ["Waist", "Catalog only"],
            ["NPC Spine [Spn0]", "NPC Pelvis [Pelv]"],
            paints,
            limits);

        RaceMenuTransformKeySet primary = new("RSMTransform",
        [
            new RaceMenuValue(30, 4, 2, RaceMenuScalar.FromNumber(1.05F))
        ]);
        RaceMenuTransformKeySet plugin = new("PluginAux",
        [
            new RaceMenuValue(30, 4, 9, RaceMenuScalar.FromNumber(1.1F))
        ]);
        var third = new SkyrimNodeTransform(
            "NPC Spine [Spn0]", false, [primary, plugin], 1.05F, null, [], []);
        var first = new SkyrimNodeTransform(
            "NPC Spine [Spn0]", true,
            [new RaceMenuTransformKeySet("RSMTransform",
            [
                new RaceMenuValue(30, 4, 2, RaceMenuScalar.FromNumber(0.9F))
            ])], 0.9F, null, [], []);
        var textures = ImmutableDictionary<int, string>.Empty
            .Add(0, "actors/skin/base.dds")
            .Add(5, "actors/skin/preserved.dds");
        var skin = new SkyrimSkinOverride(0x20, false,
        [
            new RaceMenuValue(9, 2, 0, RaceMenuScalar.FromText(textures[0])),
            new RaceMenuValue(9, 2, 5, RaceMenuScalar.FromText(textures[5]))
        ], textures, [], null);
        var baseline = new SkyrimBodyEditorDocument(
            50F,
            [new BodySlideSliderValue("Waist", 0.25F)],
            [third, first],
            [skin],
            [
                new RaceMenuBodyOverlay("Body [Ovl0]", "actors/paint/body.dds", null, [], 1F, []),
                new RaceMenuBodyOverlay("Face [Ovl0]", "actors/paint/face.dds", null, [], 1F, [])
            ]);
        var editor = new SkyrimBodyEditorViewModel(baseline, "Emi", catalogs);

        editor.Weight.Value = 65F;
        SkyrimBodySlideRowViewModel waist = editor.BodySlide.Rows.Single(item => item.Name == "Waist");
        waist.Percent = 40F;
        Assert(editor.CurrentDocument.Weight == 65F &&
               editor.CurrentDocument.BodySlide.Single(item => item.Name == "Waist").Value == 0.4F,
            "Weight or BodySlide presentation state did not update the immutable body document.");

        editor.Transforms.SelectedRow = editor.Transforms.Rows.Single(item =>
            item.Node == "NPC Spine [Spn0]" && !item.FirstPerson);
        editor.Transforms.HasScale = true;
        editor.Transforms.Scale = 1.2F;
        Assert(editor.Transforms.Apply() &&
               editor.CurrentDocument.NodeTransforms.Single(item => !item.FirstPerson).KeySets.Any(item =>
                   item.Name == "PluginAux" && item.Values.SequenceEqual(plugin.Values)) &&
               editor.CurrentDocument.NodeTransforms.Single(item => item.FirstPerson).Scale == 0.9F,
            "Transform presentation lost preserved key sets or collapsed first-person identity.");

        editor.SkinOverrides.SelectedRow = editor.SkinOverrides.Rows.Single();
        editor.SkinOverrides.Diffuse = "actors/skin/new.dds";
        editor.SkinOverrides.TintEnabled = true;
        editor.SkinOverrides.Red = 0.1F;
        editor.SkinOverrides.Green = 0.2F;
        editor.SkinOverrides.Blue = 0.3F;
        Assert(editor.SkinOverrides.Apply() &&
               editor.CurrentDocument.SkinOverrides.Single().Textures[5] == "actors/skin/preserved.dds",
            "Skin presentation discarded a non-editable preset texture slot.");

        ApplyPaint(BodyOverlayTarget.Body, bodyPaint);
        ApplyPaint(BodyOverlayTarget.Hands, handsPaint);
        ApplyPaint(BodyOverlayTarget.Feet, feetPaint);
        Assert(editor.CurrentDocument.BodyOverlays.Any(item => item.Node == "Face [Ovl0]") &&
               editor.CurrentDocument.BodyOverlays.Any(item => item.Node == "Body [Ovl1]") &&
               editor.CurrentDocument.BodyOverlays.Any(item => item.Node == "Hands [Ovl0]") &&
               editor.CurrentDocument.BodyOverlays.Any(item => item.Node == "Feet [Ovl0]"),
            "Real Body/Hands/Feet picker callers lost hidden Face rows or slot identity.");

        editor.SelectedSection = SkyrimBodyEditorSection.BodyOverlays;
        editor.ResetSelectedSection();
        Assert(editor.CurrentDocument.BodyOverlays == baseline.BodyOverlays &&
               editor.CurrentDocument.Weight == 65F,
            "Body-overlay section reset changed another section or lost the exact opening collection.");
        editor.Cancel();
        Assert(editor.CurrentDocument == baseline && !editor.IsDirty && editor.AcceptedDocument is null,
            "Body Cancel did not restore the exact immutable baseline.");
        editor.Weight.Value = 55F;
        Assert(editor.TryAccept() && editor.AcceptedDocument?.Weight == 55F &&
               editor.AuthorityNotice.Contains("runtime", StringComparison.OrdinalIgnoreCase),
            "Body OK did not return the complete current document or preserve false authority.");
        Console.WriteLine(
            "PASS Skyrim body editor coordinates lossless sections and Body/Hands/Feet picker callers.");

        void ApplyPaint(BodyOverlayTarget target, SkyrimRaceMenuPaintChoiceCandidate expected)
        {
            SkyrimRaceMenuPaintPickerViewModel picker = editor.CreateBodyPaintPicker(target);
            picker.SelectedRow = picker.VisibleRows.Single(item => item.Candidate == expected);
            Assert(picker.TryAccept() && editor.ApplyBodyPaintPicker(target, picker),
                $"The {target} paint picker was not consumed.");
        }

        static SkyrimRaceMenuPaintChoiceCandidate BodyPaint(
            SkyrimRaceMenuPaintCategory category,
            string name,
            string path) =>
            new(category, name, name, new AssetPath(path), new AssetPath("textures/" + path),
            [
                new SkyrimRaceMenuPaintTextureSlot(0, SkyrimRaceMenuPaintSlotKind.Texture,
                    path, new AssetPath(path)),
                new SkyrimRaceMenuPaintTextureSlot(1, SkyrimRaceMenuPaintSlotKind.Texture,
                    path.Replace(".dds", "_n.dds", StringComparison.Ordinal),
                    new AssetPath(path.Replace(".dds", "_n.dds", StringComparison.Ordinal)))
            ],
            [new SkyrimRaceMenuPaintRegistrationSource(
                new AssetPath("scripts/body-paint.pex"),
                SkyrimRaceMenuPaintProviderKind.Bsa,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\paint.bsa"),
                new Sha256Hash(new string('d', 64)),
                new Sha256Hash(new string('e', 64)),
                128)]);
    }

    private static void RunSkyrimFaceEditorViewModelTest()
    {
        var dataRoot = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies");
        var skyrim = new PluginName("Skyrim.esm");
        var hairPlugin = new PluginName("Hair.esp");
        var race = Ref(skyrim, 0x13746);
        var provider = new SkyrimFaceRecordProvider(
            hairPlugin,
            new WorkspacePath(Path.Combine(dataRoot.Value, hairPlugin.Value)),
            new Sha256Hash(new string('a', 64)));
        SkyrimHeadPartChoiceCandidate misc = HeadPart(0x202, "Hairline", NpcHeadPartType.Misc);
        SkyrimHeadPartChoiceCandidate hairA = HeadPart(0x200, "HairA", NpcHeadPartType.Hair);
        SkyrimHeadPartChoiceCandidate hairB = HeadPart(0x201, "HairB", NpcHeadPartType.Hair) with
        {
            ExtraParts = [misc.Reference]
        };
        var raceDefaultEyes = new NpcHeadPartSelection(
            Ref(skyrim, 0x500), NpcHeadPartType.Eyes);
        var headPartCatalogs = new Dictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult>
        {
            [NpcHeadPartType.Hair] = new(true, [hairA, hairB], []),
            [NpcHeadPartType.Misc] = new(true, [misc], [])
        }.ToImmutableDictionary();

        FormChoiceCandidate hairColor = Typed(0x300, "CLFM", "HairBlack");
        FormChoiceCandidate headTexture = Typed(0x400, "TXST", "FemaleHeadTexture");
        var typedForms = new FormChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition,
            true,
            [hairColor, headTexture],
            []);
        SkyrimRaceMenuPaintChoiceCandidate warpaint = Paint(
            SkyrimRaceMenuPaintCategory.Warpaint,
            "Warpaint custom",
            "actors/paint/warpaint.dds");
        SkyrimRaceMenuPaintChoiceCandidate facePaint = Paint(
            SkyrimRaceMenuPaintCategory.Face,
            "Face paint",
            "actors/paint/face.dds");
        var paints = new Dictionary<SkyrimRaceMenuPaintCategory, SkyrimRaceMenuPaintChoiceResult>
        {
            [SkyrimRaceMenuPaintCategory.Warpaint] = new(true, [warpaint], null, []),
            [SkyrimRaceMenuPaintCategory.Face] = new(true, [facePaint], null, [])
        }.ToImmutableDictionary();
        var sliders = new SkyrimRaceMenuSliderCatalog(
            [
                new SkyrimRaceMenuSliderDefinition(
                    "NordRace",
                    SkyrimRaceMenuSliderGender.Female,
                    "NoseLength",
                    SkyrimRaceMenuSliderCategory.Face,
                    SkyrimRaceMenuSliderType.Slider,
                    "NoseShort",
                    "NoseLong",
                    0,
                    hairPlugin,
                    new AssetPath("meshes/actors/character/FaceGenMorphs/Hair.esp/races.ini"),
                    1),
                new SkyrimRaceMenuSliderDefinition(
                    "NordRace",
                    SkyrimRaceMenuSliderGender.Female,
                    "BrowPreset",
                    SkyrimRaceMenuSliderCategory.Face,
                    SkyrimRaceMenuSliderType.Preset,
                    string.Empty,
                    string.Empty,
                    2,
                    hairPlugin,
                    new AssetPath("meshes/actors/character/FaceGenMorphs/Hair.esp/races.ini"),
                    2)
            ],
            []);
        var catalogs = new SkyrimFaceEditorCatalogs(
            typedForms, headPartCatalogs, paints, sliders, [raceDefaultEyes]);
        var baseline = new SkyrimFaceEditorDocument(
            new SkyrimFaceEditorParts(
                [new NpcHeadPartSelection(hairA.Reference, NpcHeadPartType.Hair)],
                default,
                null,
                false),
            new SkyrimFaceMorphPatch(
                Enumerable.Repeat(0F, 18).ToImmutableArray(),
                0.375F,
                [uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue]),
            [new SkyrimRaceMenuCustomMorphValue("NoInstalledDefinition", 0.2F)],
            [new SkyrimFaceEditorTintLayer(
                new SkyrimFaceTintLayer(1, 100, 110, 120, 255, 40, 0),
                true,
                new AssetPath("actors/paint/default.dds"),
                null)],
            [new RaceMenuSculptPart("Head", 10, [], true, true)],
            [new RaceMenuBodyOverlay("Body [Ovl0]", "body.dds", null, [], 1F, [])]);
        var editor = new SkyrimFaceEditorViewModel(
            baseline,
            dataRoot,
            race,
            "NordRace",
            NpcSex.Female,
            catalogs,
            2);
        Assert(editor.FaceOverlays.HasNoRows &&
               !string.IsNullOrWhiteSpace(editor.FaceOverlays.EmptyState),
            "An empty face-overlay section had no explicit user-facing state.");

        SkyrimCustomMorphRowViewModel presetRow = editor.CustomMorphs.Rows.Single(item =>
            item.Name == "BrowPreset");
        Assert(presetRow.IsPreset &&
               presetRow.PresetOptions.Select(item => item.Value).SequenceEqual([0, 1, 2]),
            "A RaceMenu preset slider did not expose Default plus exact discrete choices.");
        presetRow.SelectedPreset = presetRow.PresetOptions[2];
        Assert(editor.CurrentDocument.CustomMorphs.Single(item =>
                   item.Name == "BrowPreset").Value == 2F,
            "A discrete RaceMenu preset choice did not update the typed face document.");
        presetRow.SelectedPreset = presetRow.PresetOptions[0];
        Assert(editor.CurrentDocument.CustomMorphs.All(item =>
                item.Name != "BrowPreset"),
            "Selecting the Default RaceMenu preset did not remove the redundant custom morph.");

        using (SkyrimHeadPartPickerViewModel picker =
               editor.CreateHeadPartPicker(NpcHeadPartType.Hair))
        {
            picker.SelectedRow = picker.VisibleRows.Single(item =>
                item.Reference == hairB.Reference);
            Assert(picker.TryAccept() && editor.ApplyHeadPartPicker(picker) &&
                   editor.CurrentDocument.Parts.OrderedHeadParts.Single().Reference ==
                   hairB.Reference && editor.Parts.HeadParts.Any(item =>
                       item.Kind == SkyrimFaceHeadPartRowKind.HnamExtra &&
                       item.Reference == misc.Reference) &&
                   editor.Parts.HeadParts.Any(item =>
                       item.Kind == SkyrimFaceHeadPartRowKind.RaceDefault &&
                       item.Reference == raceDefaultEyes.Reference),
                "The face editor did not consume the picker or display derived HNAM/RACE rows.");
        }
        editor.Parts.SelectedHeadPart = editor.Parts.HeadParts.Single(item =>
            item.Kind == SkyrimFaceHeadPartRowKind.RaceDefault);
        Assert(!editor.Parts.CanRemove,
            "A read-only race-default head-part row became removable.");

        TypedFormIdPickerViewModel colorPicker = editor.CreateHairColorPicker();
        colorPicker.SelectedRow = colorPicker.VisibleRows.Single(item =>
            item.Reference == new FormReference(skyrim, hairColor.FormId));
        Assert(colorPicker.TryAccept() && editor.ApplyHairColorPicker(colorPicker) &&
               editor.CurrentDocument.Parts.HairColor.Value ==
               new FormReference(skyrim, hairColor.FormId),
            "The shared typed FormID picker did not update HCLF.");

        SkyrimRaceMenuPaintPickerViewModel maskPicker = editor.CreateTintMaskPicker(1);
        maskPicker.SelectedRow = maskPicker.VisibleRows.Single(item => !item.IsClear);
        Assert(maskPicker.TryAccept() && editor.ApplyTintMaskPicker(1, maskPicker) &&
               editor.CurrentDocument.Tints[0].MaskOverride?.Value ==
               warpaint.RegisteredPath.Value,
            "The face editor did not consume the real warpaint picker result.");

        SkyrimRaceMenuPaintPickerViewModel facePicker = editor.CreateFacePaintPicker();
        facePicker.SelectedRow = facePicker.VisibleRows.Single();
        Assert(facePicker.TryAccept() && editor.ApplyFacePaintPicker(facePicker) &&
               editor.FaceOverlays.Rows.Single().Slot == 0 &&
               editor.CurrentDocument.BodyOverlays.Any(item => item.Node == "Body [Ovl0]"),
            "Face-paint picker application lost slot identity or a non-face overlay.");

        editor.SelectedSection = SkyrimFaceEditorSection.Tints;
        editor.ResetSelectedSection();
        Assert(editor.CurrentDocument.Tints == baseline.Tints &&
               editor.CurrentDocument.Parts != baseline.Parts,
            "Section reset changed another face section or failed to restore the baseline tint.");
        editor.Cancel();
        Assert(editor.CurrentDocument == baseline && !editor.IsDirty &&
               editor.AcceptedDocument is null,
            "Cancel did not restore the exact immutable baseline and clear commit state.");

        editor.NativeMorphs.Sliders[0].Value = 0.5F;
        editor.NativeMorphs.Sliders[0].Value = 0F;
        Assert(!editor.IsDirty,
            "Structurally equal face content remained dirty because immutable-array identity leaked.");
        editor.NativeMorphs.Sliders[0].Value = 0.5F;
        Assert(editor.TryAccept() && editor.AcceptedDocument is { } accepted &&
               accepted.NativeMorphs.Nam9Sliders[0] == 0.5F &&
               accepted.NativeMorphs.Nam9Trailing == 0.375F &&
               editor.AuthorityNotice.Contains("runtime", StringComparison.OrdinalIgnoreCase),
            "OK did not return the complete current document or preserve false authority.");
        Console.WriteLine(
            "PASS Skyrim face editor coordinates six immutable sections and real typed picker callers.");

        FormReference Ref(PluginName plugin, uint id) => new(plugin, new FormId(id));

        SkyrimHeadPartChoiceCandidate HeadPart(
            uint id,
            string editorId,
            NpcHeadPartType type) => new(
            Ref(hairPlugin, id),
            editorId,
            editorId,
            type,
            false,
            true,
            true,
            null,
            [],
            [],
            provider,
            SkyrimHeadPartRaceMatchKind.RaceDefault);

        FormChoiceCandidate Typed(uint id, string signature, string editorId) => new(
            skyrim,
            new FormId(id),
            new RecordSignature(signature),
            editorId,
            editorId,
            false,
            new FormChoiceProvenance(
                FormChoiceProvenanceKind.Base,
                skyrim,
                [skyrim]));

        SkyrimRaceMenuPaintChoiceCandidate Paint(
            SkyrimRaceMenuPaintCategory category,
            string name,
            string path)
        {
            var source = new SkyrimRaceMenuPaintRegistrationSource(
                new AssetPath("scripts/paint.pex"),
                SkyrimRaceMenuPaintProviderKind.Bsa,
                new WorkspacePath(Path.Combine(dataRoot.Value, "RaceMenu.bsa")),
                new Sha256Hash(new string('b', 64)),
                new Sha256Hash(new string('c', 64)),
                100);
            return new SkyrimRaceMenuPaintChoiceCandidate(
                category,
                name,
                name,
                new AssetPath(path),
                new AssetPath("textures/" + path),
                [new SkyrimRaceMenuPaintTextureSlot(
                    0,
                    SkyrimRaceMenuPaintSlotKind.Texture,
                    path,
                    new AssetPath("textures/" + path))],
                [source]);
        }
    }

    private static void RunTypedFormIdPickerViewModelTest()
    {
        var source = new PluginName("Source.esm");
        var winner = new PluginName("Winning.esp");
        var provenance = new FormChoiceProvenance(
            FormChoiceProvenanceKind.Override, source, [source, winner]);
        var nord = Candidate(0x900, "RaceNord", "Nord", false);
        var breton = Candidate(0x901, "RaceBreton", "Breton", false);
        var deleted = Candidate(0x902, "RaceDeleted", "Deleted race", true);
        var search = new FormChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition,
            true,
            [nord, breton, deleted],
            []);
        var deletedCallbacks = 0;
        var picker = new TypedFormIdPickerViewModel(
            search,
            new TypedFormIdPickerOptions(
                "Choose race",
                "Pick one reviewed race.",
                [new RecordSignature("RACE")],
                new FormReference(source, nord.FormId),
                true,
                candidate => candidate.FormId == nord.FormId,
                _ =>
                {
                    deletedCallbacks++;
                    return true;
                }));

        Assert(picker.VisibleRows.Count == 2 && picker.VisibleRows[0].IsNull &&
               picker.SelectedRow?.Reference == new FormReference(source, nord.FormId),
            "Typed picker did not pin NULL, apply compatibility, or preselect current.");
        picker.ShowAll = true;
        Assert(picker.VisibleRows.Count == 3 &&
               picker.VisibleRows.All(row => row.Candidate?.IsDeleted != true),
            "Show all did not reveal only the non-deleted typed catalog rows.");
        picker.FilterText = "RaceBreton";
        Assert(picker.SelectedRow is null && picker.VisibleRows.Count == 2 &&
               picker.VisibleRows[0].IsNull &&
               picker.VisibleRows[1].Reference == new FormReference(source, breton.FormId),
            "Filtering did not clear stale selection while retaining the pinned NULL row.");
        Assert(!picker.TryAccept() &&
               picker.ValidationMessage.Contains("Select", StringComparison.Ordinal),
            "Typed picker accepted without a deliberate selected row.");
        picker.SelectedRow = picker.VisibleRows[1];
        Assert(picker.TryAccept() && picker.IsAccepted &&
               picker.AcceptedReference == new FormReference(source, breton.FormId) &&
               picker.AcceptedFormId == breton.FormId,
            "Typed picker did not return the exact plugin-qualified FormID.");
        picker.Cancel();
        Assert(!picker.IsAccepted && picker.AcceptedReference is null &&
               picker.AcceptedFormId is null,
            "Cancel retained an accepted typed picker result.");
        Assert(picker.TryDeleteOrRevertSelected() && deletedCallbacks == 1 &&
               picker.VisibleRows.All(row => row.Reference !=
                   new FormReference(source, breton.FormId)),
            "Caller-confirmed Delete or Revert did not remove only the per-open row.");

        var nullPicker = new TypedFormIdPickerViewModel(
            search,
            new TypedFormIdPickerOptions(
                "Choose voice",
                "Pick a voice or NULL.",
                [new RecordSignature("VTYP")],
                null,
                true));
        Assert(nullPicker.SelectedRow?.IsNull == true && nullPicker.TryAccept() &&
               nullPicker.AcceptedReference is null &&
               nullPicker.AcceptedFormId == new FormId(0),
            "Explicit NULL did not round-trip as zero FormID.");

        var empty = new TypedFormIdPickerViewModel(
            search,
            new TypedFormIdPickerOptions(
                "Choose class",
                "Pick a class.",
                [new RecordSignature("CLAS")],
                null,
                false));
        Assert(empty.HasNoVisibleRows && empty.SelectedRow is null && !empty.CanAccept,
            "Unavailable signature did not produce a non-accepting empty state.");
        var pickerXaml = File.ReadAllText(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\TypedFormIdPickerWindow.xaml");
        Assert(pickerXaml.Contains(
                   "TargetType=\"{x:Type GridViewColumnHeader}\"", StringComparison.Ordinal) &&
               pickerXaml.Contains(
                   "<Setter Property=\"Background\" Value=\"#13252C\" />", StringComparison.Ordinal),
            "The typed picker does not pin a readable dark GridView header palette.");
        Console.WriteLine(
            "PASS typed FormID picker is cached, typed, filter-safe, and cancel-safe.");

        FormChoiceCandidate Candidate(
            uint formId,
            string editorId,
            string name,
            bool isDeleted) => new(
                winner,
                new FormId(formId),
                new RecordSignature("RACE"),
                editorId,
                name,
                isDeleted,
                provenance);
    }

    private static void RunSkyrimHeadPartPickerViewModelTest()
    {
        var plugin = new PluginName("HeadParts.esp");
        var provider = new SkyrimFaceRecordProvider(
            plugin,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\HeadParts.esp"),
            new Sha256Hash(new string('a', 64)));
        var race = new FormReference(plugin, new FormId(0x900));
        var lassi = Candidate(0xA9555, "0Lassi", "Lassi", "KS Hairdo's/Lassi.nif", true);
        var missing = Candidate(0xA9556, "NoModel", "No model", null, false);
        var catalog = new SkyrimHeadPartChoiceResult(true, [lassi, missing], []);
        var preview = new ControlledHeadPartPreviewService();
        using var picker = new SkyrimHeadPartPickerViewModel(
            catalog,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies"),
            race,
            NpcSex.Female,
            NpcHeadPartType.Hair,
            preview);

        Assert(picker.VisibleRows.Count == 2 && picker.SelectedRow is null &&
               !picker.CanAccept && !picker.TryAccept(),
            "Head-part picker selected or accepted an arbitrary first row.");
        picker.SelectedRow = picker.VisibleRows[0];
        Assert(picker.IsPreviewBusy && !picker.CanAccept && preview.Requests.Count == 1,
            "Head-part selection did not start one bounded preview request.");
        picker.SelectedRow = picker.VisibleRows[1];
        Assert(!picker.IsPreviewBusy && picker.PreviewImagePath is null &&
               preview.Tokens[0].IsCancellationRequested &&
               picker.PreviewStatus.Contains("no readable model", StringComparison.Ordinal),
            "A model-less selection retained stale preview state or failed to cancel its predecessor.");

        picker.SelectedRow = picker.VisibleRows[0];
        Assert(preview.Requests.Count == 2 && picker.IsPreviewBusy,
            "Reselecting the renderable HDPT did not start a fresh preview.");
        preview.Complete(
            1,
            new SkyrimHeadPartPreviewResult(
                true,
                new PreviewRenderedImage(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preview-current.png",
                    new string('b', 64),
                    512,
                    512,
                    3),
                []));
        picker.WaitForPreviewAsync().GetAwaiter().GetResult();
        Assert(!picker.IsPreviewBusy && picker.HasPreviewImage &&
               picker.PreviewImagePath!.EndsWith("preview-current.png", StringComparison.Ordinal) &&
               picker.TryAccept() && picker.AcceptedReference == lassi.Reference &&
               picker.AcceptedFormId == lassi.Reference.FormId,
            "Settled preview did not preserve the exact accepted HDPT reference.");

        picker.FilterText = "NoModel";
        Assert(picker.SelectedRow is null && picker.PreviewImagePath is null &&
               picker.VisibleRows.Count == 1,
            "Filtering retained a stale selection or stale preview image.");
        picker.Cancel();
        Assert(!picker.IsAccepted && picker.AcceptedReference is null &&
               picker.AcceptedFormId is null && picker.PreviewImagePath is null,
            "Cancel retained a head-part selection or preview artifact.");
        Console.WriteLine(
            "PASS Skyrim head-part picker is typed, filter-safe, preview-stale-safe, and cancel-safe.");

        SkyrimHeadPartChoiceCandidate Candidate(
            uint formId,
            string editorId,
            string name,
            string? model,
            bool hasPreview)
        {
            var reference = new FormReference(plugin, new FormId(formId));
            AssetPath? asset = model is null ? null : new AssetPath(model);
            ImmutableArray<SkyrimHeadPartPreviewModel> models = hasPreview && asset is { } previewAsset
                ? [new SkyrimHeadPartPreviewModel(reference, previewAsset, provider, true)]
                : [];
            return new SkyrimHeadPartChoiceCandidate(
                reference,
                editorId,
                name,
                NpcHeadPartType.Hair,
                false,
                false,
                true,
                asset,
                [],
                models,
                provider,
                SkyrimHeadPartRaceMatchKind.ValidRaceList);
        }
    }

    private static void RunSkyrimMeshPickerViewModelTest()
    {
        WorkspacePath dataRoot = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\mesh-picker\\Data");
        var provider = new AssetChoiceProviderEvidence(
            AssetProviderKind.Loose,
            "loose",
            100,
            new Sha256Hash(new string('a', 64)));
        var body = new SkyrimMeshPickerCandidate(
            new AssetPath("meshes/armor/travel/body.nif"),
            new AssetPath("armor/travel/body.nif"),
            provider,
            [provider]);
        var helmet = new SkyrimMeshPickerCandidate(
            new AssetPath("meshes/armor/travel/helmet.nif"),
            new AssetPath("armor/travel/helmet.nif"),
            provider,
            [provider]);
        var catalog = new SkyrimMeshPickerCatalogResult(
            true,
            dataRoot,
            [body, helmet],
            body,
            []);
        var preview = new ControlledMeshPreviewService();
        using var picker = new SkyrimMeshPickerViewModel(catalog, preview);

        Assert(picker.VisibleRows.Count == 2 &&
               picker.SelectedRow?.Candidate == body &&
               picker.IsPreviewBusy && preview.Requests.Count == 1,
            "The mesh picker did not preselect and preview the exact opening path.");
        picker.SelectedRow = picker.VisibleRows[1];
        Assert(preview.Tokens[0].IsCancellationRequested &&
               preview.Requests.Count == 2 && picker.IsPreviewBusy,
            "Changing mesh selection did not cancel the stale preview.");
        preview.Complete(1, new SkyrimMeshPreviewResult(
            false,
            null,
            [new Diagnostic(
                "mesh-preview-fixture-failure",
                DiagnosticSeverity.Error,
                "Unreadable fixture.")]));
        picker.WaitForPreviewAsync().GetAwaiter().GetResult();
        Assert(!picker.IsPreviewBusy && picker.CanAccept &&
               picker.PreviewImagePath is null &&
               picker.PreviewStatus.Contains(
                   "mesh-preview-fixture-failure",
                   StringComparison.Ordinal),
            "A typed mesh preview failure was hidden or incorrectly blocked selection.");

        picker.FilterText = "body";
        Assert(picker.VisibleRows.Count == 1 && picker.SelectedRow is null &&
               !picker.CanAccept && picker.PreviewImagePath is null,
            "Filtering retained a hidden mesh selection or preview.");
        picker.SelectedRow = picker.VisibleRows[0];
        preview.Complete(2, new SkyrimMeshPreviewResult(
            true,
            new PreviewRenderedImage(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\preview-mesh-current.png",
                new string('b', 64),
                512,
                512,
                1),
            []));
        picker.WaitForPreviewAsync().GetAwaiter().GetResult();
        SkyrimMeshPickerRowViewModel retainedRow = picker.SelectedRow ??
            throw new InvalidOperationException("The rendered mesh row disappeared.");
        int settledRequestCount = preview.Requests.Count;
        picker.FilterText = "travel";
        Assert(ReferenceEquals(picker.SelectedRow, retainedRow) &&
               preview.Requests.Count == settledRequestCount &&
               !picker.IsPreviewBusy,
            "A filter that retained the selected mesh restarted its settled preview.");
        Assert(picker.HasPreviewImage && picker.TryAccept() &&
               picker.AcceptedSelection is
               {
                   Accepted: true,
                   RelativePath.Value: "armor/travel/body.nif"
               },
            "The settled mesh selection did not return the exact prefix-free path.");
        picker.Cancel();
        Assert(!picker.IsAccepted && picker.AcceptedSelection is null &&
               picker.PreviewImagePath is null,
            "Mesh-picker Cancel retained an accepted result or preview image.");
        Console.WriteLine(
            "PASS Skyrim mesh picker is provider-aware, preview-stale-safe, prefix-free, and cancel-safe.");
    }

    private static void RunSkyrimMeshPickerWindowContractTest()
    {
        Type windowType = typeof(SkyrimMeshPickerWindow);
        Assert(typeof(Window).IsAssignableFrom(windowType) &&
               windowType.GetConstructor(
               [
                   typeof(SkyrimMeshPickerViewModel)
               ]) is not null,
            "The mesh picker is not an owner-modal WPF window with a typed view-model boundary.");
        string xamlPath = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src",
            "NpcManager.Desktop",
            "SkyrimMeshPickerWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);
        foreach (string required in new[]
                 {
                     "WindowStartupLocation=\"CenterOwner\"",
                     "ShowInTaskbar=\"False\"",
                     "MinWidth=\"900\"",
                     "FocusManager.FocusedElement",
                     "FilterBox",
                     "VisibleRows",
                     "SelectedRow",
                     "MeshList_OnMouseDoubleClick",
                     "MeshList_OnPreviewKeyDown",
                     "PreviewImagePath",
                     "PreviewStatus",
                     "PreviewAuthority",
                     "IsDefault=\"True\"",
                     "IsCancel=\"True\"",
                     "automation:AutomationProperties.Name"
                 })
            Assert(xaml.Contains(required, StringComparison.Ordinal),
                $"The mesh-picker modal is missing required surface token '{required}'.");
        Console.WriteLine(
            "PASS Skyrim mesh-picker modal exposes filter, provider, preview, keyboard, and exact transaction boundaries.");
    }

    private static void RunSkyrimRaceMenuPaintPickerViewModelTest()
    {
        var source = new SkyrimRaceMenuPaintRegistrationSource(
            new AssetPath("scripts/PaintFixture.pex"),
            SkyrimRaceMenuPaintProviderKind.Bsa,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\PaintFixture.bsa"),
            new Sha256Hash(new string('a', 64)),
            new Sha256Hash(new string('b', 64)),
            512);
        SkyrimRaceMenuPaintChoiceCandidate ash = Candidate(
            "$Ash", "Ash", "actors/paint/ash.dds");
        SkyrimRaceMenuPaintChoiceCandidate rune = Candidate(
            "$Rune", "Rune", "actors/paint/rune.dds");
        var summary = new SkyrimRaceMenuPaintCatalogSummary(
            2, 1, 2, 0, 2, 1, 0, 2, 2,
            new Sha256Hash(new string('c', 64)));
        var result = new SkyrimRaceMenuPaintChoiceResult(
            true, [ash, rune], summary, []);
        var picker = new SkyrimRaceMenuPaintPickerViewModel(
            result,
            SkyrimRaceMenuPaintCategory.Warpaint,
            "textures\\actors\\paint\\rune.dds",
            allowNone: true);

        Assert(picker.VisibleRows.Count == 3 && picker.VisibleRows[0].IsClear &&
               picker.SelectedRow?.Candidate == rune && picker.CanAccept,
            "Paint picker did not pin clear or normalize current-path selection.");
        picker.FilterText = "no-match";
        Assert(picker.VisibleRows.Count == 1 && picker.VisibleRows[0].IsClear &&
               picker.SelectedRow is null && !picker.TryAccept(),
            "Paint filtering retained a hidden selection or accepted no row.");
        picker.SelectedRow = picker.VisibleRows[0];
        Assert(picker.TryAccept() && picker.IsAccepted && picker.AcceptedPath == string.Empty &&
               picker.AcceptedEntry is null,
            "Explicit clear was not distinguishable from Cancel.");
        picker.Cancel();
        Assert(!picker.IsAccepted && picker.AcceptedPath is null && picker.AcceptedEntry is null,
            "Paint picker Cancel retained an accepted result.");

        picker.FilterText = "Rune";
        picker.SelectedRow = picker.VisibleRows.Single(item => !item.IsClear);
        Assert(picker.TryAccept() && picker.AcceptedPath == "actors/paint/rune.dds" &&
               picker.AcceptedEntry == rune,
            "Paint picker did not return the exact typed registered entry.");
        var noClear = new SkyrimRaceMenuPaintPickerViewModel(
            result,
            SkyrimRaceMenuPaintCategory.Warpaint,
            "actors/paint/missing.dds",
            allowNone: false);
        Assert(noClear.SelectedRow is null && !noClear.CanAccept,
            "Paint picker silently selected a replacement for an unmatched current path.");
        Console.WriteLine(
            "PASS RaceMenu paint picker is normalized, explicit-clear-safe, typed, filter-safe, and cancel-safe.");

        SkyrimRaceMenuPaintChoiceCandidate Candidate(
            string name,
            string display,
            string path)
        {
            var registered = new AssetPath(path);
            return new SkyrimRaceMenuPaintChoiceCandidate(
                SkyrimRaceMenuPaintCategory.Warpaint,
                name,
                display,
                registered,
                new AssetPath("textures/" + registered.Value),
                [
                    new SkyrimRaceMenuPaintTextureSlot(
                        0,
                        SkyrimRaceMenuPaintSlotKind.Texture,
                        registered.Value,
                        registered)
                ],
                [source]);
        }
    }

    private static void RunSkyrimHeadPartPreviewServiceTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var testRoot = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "tests",
            $"headpart-preview-{Guid.NewGuid():N}"));
        var dataRoot = new WorkspacePath(Path.Combine(testRoot.Value, "Data"));
        var cacheRoot = new WorkspacePath(Path.Combine(testRoot.Value, "cache"));
        string modelsRoot = Path.Combine(dataRoot.Value, "meshes", "actors", "headparts");
        Directory.CreateDirectory(modelsRoot);
        string rootModel = Path.Combine(modelsRoot, "root.nif");
        string extraModel = Path.Combine(modelsRoot, "extra.nif");
        File.WriteAllBytes(rootModel, [1, 2, 3, 4]);
        File.WriteAllBytes(extraModel, [5, 6, 7, 8]);
        string providerPath = Path.Combine(dataRoot.Value, "HeadParts.esp");
        File.WriteAllBytes(providerPath, [9, 10, 11, 12]);

        try
        {
            var plugin = new PluginName("HeadParts.esp");
            var reference = new FormReference(plugin, new FormId(0x800));
            var extraReference = new FormReference(plugin, new FormId(0x801));
            var provider = new SkyrimFaceRecordProvider(
                plugin,
                new WorkspacePath(providerPath),
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(providerPath)))));
            var candidate = new SkyrimHeadPartChoiceCandidate(
                reference,
                "TestHair",
                "Test hair",
                NpcHeadPartType.Hair,
                false,
                false,
                true,
                new AssetPath("actors/headparts/root.nif"),
                [extraReference],
                [
                    new SkyrimHeadPartPreviewModel(
                        reference,
                        new AssetPath("actors/headparts/root.nif"),
                        provider,
                        true),
                    new SkyrimHeadPartPreviewModel(
                        extraReference,
                        new AssetPath("actors/headparts/extra.nif"),
                        provider,
                        false)
                ],
                provider,
                SkyrimHeadPartRaceMatchKind.ValidRaceList);
            var renderer = new RecordingPreviewImageRenderer();
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var service = new SkyrimHeadPartPreviewService(
                renderer,
                policy,
                labRoot,
                cacheRoot);
            var request = new SkyrimHeadPartPreviewRequest(
                dataRoot,
                candidate,
                512,
                512);

            SkyrimHeadPartPreviewResult first = service.RenderAsync(
                    request,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            PreviewRenderedImage firstImage = first.Image ??
                throw new InvalidOperationException("The initial preview returned no image.");
            Assert(first.Rendered && renderer.CallCount == 1 &&
                   File.Exists(firstImage.Path) &&
                   !firstImage.Path.Contains(".tmp", StringComparison.OrdinalIgnoreCase),
                "The head-part preview was not rendered into one retained cache entry.");

            SkyrimHeadPartPreviewResult cached = service.RenderAsync(
                    request,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(cached.Rendered && cached.Image?.Path == firstImage.Path &&
                   renderer.CallCount == 1 && cached.Diagnostics.Any(item =>
                       item.Code == "headpart-preview-cache-hit"),
                "The exact hash-bound head-part preview was not reused.");

            File.WriteAllBytes(rootModel, [1, 2, 3, 4, 5]);
            SkyrimHeadPartPreviewResult changed = service.RenderAsync(
                    request,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            PreviewRenderedImage changedImage = changed.Image ??
                throw new InvalidOperationException("The changed preview returned no image.");
            Assert(changed.Rendered && changedImage.Path != firstImage.Path &&
                   renderer.CallCount == 2,
                "A changed head-part model reused stale preview pixels.");

            File.WriteAllBytes(changedImage.Path, [0, 1, 2, 3]);
            SkyrimHeadPartPreviewResult corrupt = service.RenderAsync(
                    request,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(!corrupt.Rendered && corrupt.Image is null &&
                   renderer.CallCount == 2 && corrupt.Diagnostics.Any(item =>
                       item.Code == "headpart-preview-cache-invalid"),
                "A corrupt deterministic preview cache entry was accepted or silently replaced.");

            File.Delete(extraModel);
            SkyrimHeadPartPreviewResult missing = service.RenderAsync(
                    request,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(!missing.Rendered && missing.Image is null &&
                   renderer.CallCount == 2 && missing.Diagnostics.Any(item =>
                       item.Code == "headpart-preview-model-missing"),
                "A missing HNAM model closure returned stale or partial preview pixels.");

            var rootCacheService = new SkyrimHeadPartPreviewService(
                renderer,
                policy,
                labRoot,
                labRoot);
            SkyrimHeadPartPreviewResult rootCache = rootCacheService.RenderAsync(
                    request,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(!rootCache.Rendered && rootCache.Diagnostics.Any(item =>
                    item.Code == "headpart-preview-cache-root"),
                "The preview service accepted the lab root itself as a cache directory.");

            var rootDataService = new SkyrimHeadPartPreviewService(
                renderer,
                policy,
                labRoot,
                new WorkspacePath(Path.Combine(testRoot.Value, "alternate-cache")));
            SkyrimHeadPartPreviewResult rootData = rootDataService.RenderAsync(
                    request with { DataRoot = labRoot },
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(!rootData.Rendered && rootData.Diagnostics.Any(item =>
                    item.Code == "headpart-preview-data-root"),
                "The preview service accepted the lab root itself as copied Data.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool cancelled = false;
            try
            {
                service.RenderAsync(request, cancellation.Token)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Assert(cancelled, "A cancelled head-part preview request continued.");
            Console.WriteLine(
                "PASS Skyrim head-part preview is hash-bound, cached, transactional, and fail-closed.");
        }
        finally
        {
            if (Directory.Exists(testRoot.Value))
                Directory.Delete(testRoot.Value, recursive: true);
        }
    }

    private static void RunExistingNpcIdentityEditorTest()
    {
        var source = new PluginName("Source.esp");
        var race = new FormReference(source, new FormId(0x900));
        var alternateRace = new FormReference(source, new FormId(0x901));
        var voice = new FormReference(source, new FormId(0x902));
        var npcClass = new FormReference(source, new FormId(0x903));
        var combatStyle = new FormReference(source, new FormId(0x904));
        var snapshot = new NpcOverrideSourceSnapshot(
            new EditorId("SourceNpc"),
            new NpcName("Source NPC"),
            NpcSex.Female,
            50,
            new NpcArchetypeReferences(race, voice, npcClass, null),
            new NpcStatsSnapshot(
                new NpcLevelValue(NpcLevelMode.Fixed, 10),
                0, 0, 0, 0, 0, 0, 100, 0, 0, 1, null, []),
            new NpcKeywordSnapshot([], []),
            new NpcFactionSnapshot([]),
            new NpcInventorySnapshot([]),
            new NpcOutfitSnapshot(null, null),
            new NpcPerkSnapshot([]),
            new NpcActorEffectSnapshot([]),
            "Source short");
        var provenance = new FormChoiceProvenance(
            FormChoiceProvenanceKind.Base, source, [source]);
        var candidates = ImmutableArray.Create(
            Candidate(race, "RACE", "SourceRace", provenance),
            Candidate(alternateRace, "RACE", "AlternateRace", provenance),
            Candidate(voice, "VTYP", "SourceVoice", provenance),
            Candidate(npcClass, "CLAS", "SourceClass", provenance),
            Candidate(combatStyle, "CSTY", "SourceCombat", provenance));
        var search = new FormChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition, true, candidates, []);
        var state = new ExistingNpcIdentityState();
        state.Load(snapshot);
        Assert(state.IsLoaded && !state.HasChanges && state.BuildPatches().IsEmpty,
            "Identity state did not retain the exact source values.");

        var cancelled = new ExistingNpcIdentityEditorViewModel(
            new ExistingNpcIdentityValues(
                snapshot.EditorId, snapshot.Name?.Value, snapshot.ShortName, snapshot.Archetype),
            search)
        {
            FullName = "Uncommitted name"
        };
        Assert(cancelled.FullName == "Uncommitted name" && !state.HasChanges,
            "An uncommitted identity editor session mutated caller state.");

        var invalid = new ExistingNpcIdentityEditorViewModel(
            new ExistingNpcIdentityValues(
                snapshot.EditorId, snapshot.Name?.Value, snapshot.ShortName, snapshot.Archetype),
            search)
        {
            Race = new ExistingNpcArchetypeChoice(
                new FormReference(source, new FormId(0x999)),
                "Untrusted race",
                false,
                false)
        };
        Assert(!invalid.TryAccept() && invalid.AcceptedValues is null &&
               invalid.ValidationMessage.Contains("typed record", StringComparison.Ordinal),
            "Identity editor accepted an archetype reference outside the typed catalog.");

        var accepted = new ExistingNpcIdentityEditorViewModel(
            new ExistingNpcIdentityValues(
                snapshot.EditorId, snapshot.Name?.Value, snapshot.ShortName, snapshot.Archetype),
            search)
        {
            EditorId = "EditedNpc",
            FullName = "Edited NPC",
            ShortName = string.Empty
        };
        var racePicker = accepted.CreatePicker(ExistingNpcArchetypeField.Race);
        racePicker.FilterText = "AlternateRace";
        racePicker.SelectedRow = racePicker.VisibleRows.Single();
        Assert(racePicker.TryAccept(), "Typed race picker refused its exact catalog row.");
        accepted.ApplyPicker(ExistingNpcArchetypeField.Race, racePicker);
        var voicePicker = accepted.CreatePicker(ExistingNpcArchetypeField.Voice);
        voicePicker.SelectedRow = voicePicker.VisibleRows.Single(item => item.IsNull);
        Assert(voicePicker.TryAccept(), "Typed voice picker refused the explicit NULL row.");
        accepted.ApplyPicker(ExistingNpcArchetypeField.Voice, voicePicker);
        accepted.CombatStyle = accepted.CombatStyles.Single(
            item => item.Reference == combatStyle);
        Assert(accepted.TryAccept(),
            $"Valid typed identity edits were refused: {accepted.ValidationMessage}");
        state.Commit(accepted);
        var patches = state.BuildPatches();
        Assert(state.HasChanges && patches.EditorId == new EditorId("EditedNpc") &&
               patches.Names?.FullName.IsSpecified == true &&
               patches.Names.FullName.Value == "Edited NPC" &&
               patches.Names.ShortName.IsSpecified && patches.Names.ShortName.Value is null &&
               patches.Archetype?.Race.Value == alternateRace &&
               patches.Archetype.Voice.IsSpecified && patches.Archetype.Voice.Value is null &&
               !patches.Archetype.Class.IsSpecified &&
               patches.Archetype.CombatStyle.Value == combatStyle,
            "Committed identity state did not emit exact changed-field-only patches.");
        Console.WriteLine(
            "PASS NPC identity/archetype editor is typed, source-backed, and cancel-safe.");

        static FormChoiceCandidate Candidate(
            FormReference reference,
            string signature,
            string editorId,
            FormChoiceProvenance provenance) => new(
                reference.Plugin,
                reference.FormId,
                new RecordSignature(signature),
                editorId,
                null,
                false,
                provenance);
    }

    private static void RunExistingNpcCollectionEditorTest()
    {
        var source = new PluginName("Source.esp");
        var keyword = new FormReference(source, new FormId(0x901));
        var faction = new FormReference(source, new FormId(0x902));
        var item = new FormReference(source, new FormId(0x903));
        var outfit = new FormReference(source, new FormId(0x904));
        var perk = new FormReference(source, new FormId(0x905));
        var effect = new FormReference(source, new FormId(0x906));
        var snapshot = new NpcOverrideSourceSnapshot(
            new EditorId("SourceNpc"),
            new NpcName("Source NPC"),
            NpcSex.Female,
            50,
            new NpcArchetypeReferences(null, null, null, null),
            new NpcStatsSnapshot(
                new NpcLevelValue(NpcLevelMode.Fixed, 10),
                0, 0, 0, 0, 0, 0, 100, 0, 0, 1, null, []),
            new NpcKeywordSnapshot([keyword], []),
            new NpcFactionSnapshot([new NpcFactionEntry(faction, -1)]),
            new NpcInventorySnapshot([new NpcInventoryEntry(item, 3)]),
            new NpcOutfitSnapshot(outfit, null),
            new NpcPerkSnapshot([new NpcPerkEntry(perk, 1)]),
            new NpcActorEffectSnapshot([effect]));
        var state = new ExistingNpcCollectionState();
        state.Load(snapshot);
        Assert(state.IsLoaded && !state.HasChanges &&
               state.Summary.Contains("1 factions", StringComparison.Ordinal),
            "Collection state did not retain the exact loaded source snapshot.");

        var cancelled = state.CreateEditor();
        cancelled.Keywords.Clear();
        cancelled.DefaultOutfitMode = ExistingNpcOutfitEditMode.Clear;
        Assert(!state.HasChanges && state.BuildPatches().IsEmpty,
            "An uncommitted collection editor session mutated caller state.");

        var invalid = state.CreateEditor();
        invalid.Factions[0].Value = "128";
        Assert(!invalid.TryAccept() && invalid.AcceptedValues is null &&
               invalid.ValidationMessage.Contains("-128", StringComparison.Ordinal),
            "Collection editor accepted a faction rank outside the signed-byte range.");
        invalid.Factions[0].Value = "0";
        invalid.Keywords.Add(new NpcReferenceRowViewModel(keyword.ToString()));
        Assert(!invalid.TryAccept() &&
               invalid.ValidationMessage.Contains("duplicate", StringComparison.OrdinalIgnoreCase),
            "Collection editor accepted a duplicate typed reference.");

        var accepted = state.CreateEditor();
        var replacementKeyword = new FormReference(source, new FormId(0x911));
        var replacementEffect = new FormReference(source, new FormId(0x916));
        accepted.Keywords[0].Reference = replacementKeyword.ToString();
        accepted.Factions[0].Value = "-2";
        accepted.Inventory[0].Value = "-7";
        accepted.DefaultOutfitMode = ExistingNpcOutfitEditMode.Clear;
        accepted.SleepingOutfitMode = ExistingNpcOutfitEditMode.Set;
        accepted.SleepingOutfitReference = outfit.ToString();
        accepted.Perks[0].Value = "2";
        accepted.ActorEffects[0].Reference = replacementEffect.ToString();
        Assert(accepted.TryAccept() && accepted.AcceptedValues is not null,
            $"Valid typed collection edits were refused: {accepted.ValidationMessage}");
        state.Commit(accepted);
        var patches = state.BuildPatches();
        Assert(state.HasChanges && patches.Keywords?.Keywords.Replace is { } keywords &&
               keywords.SequenceEqual([replacementKeyword]) &&
               patches.Factions?.Replace is { } factions && factions[0].Rank == -2 &&
               patches.Inventory?.Replace is { } inventory && inventory[0].Count == -7 &&
               patches.Outfits?.DefaultOutfit.IsSpecified == true &&
               patches.Outfits.DefaultOutfit.Value is null &&
               patches.Outfits.SleepingOutfit.Value == outfit &&
               patches.Perks?.Replace is { } perks && perks[0].Rank == 2 &&
               patches.ActorEffects?.Replace is { } effects &&
               effects.SequenceEqual([replacementEffect]),
            "Committed collection state did not produce the exact typed replacement patches.");

        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        var context = ExistingNpcEditDesktopComposition.Create(policy, labRoot);
        using var viewModel = new ExistingNpcEditViewModel(
            context.Service, context.FormChoices, labRoot, context.InitialRequest);
        var identityNotifications = new HashSet<string>(StringComparer.Ordinal);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null) identityNotifications.Add(args.PropertyName);
        };
        viewModel.LoadNpcAsync().GetAwaiter().GetResult();
        Assert(viewModel.CanEditCollections && viewModel.Collections.IsLoaded,
            "Desktop existing-NPC editor did not load the hash-bound source snapshot.");
        Assert(!viewModel.Identity.HasChanges && !viewModel.Stats.HasChanges &&
               !viewModel.Collections.HasChanges,
            "The production existing-NPC task loaded implicit demo edits before the user changed a field.");
        Assert(viewModel.CanEditIdentity &&
               identityNotifications.Contains(nameof(viewModel.CanEditIdentity)) &&
               identityNotifications.Contains(nameof(viewModel.IdentitySummary)),
            "Loading a new source NPC did not refresh the visible identity card and editor action.");
        var panelXaml = File.ReadAllText(Path.Combine(
            labRoot.Value, "projects", "NpcManagerReimplementation", "src",
            "NpcManager.Desktop", "ExistingNpcEditPanel.xaml"));
        Assert(panelXaml.Contains("Background=\"#0E1924\"", StringComparison.Ordinal) &&
               panelXaml.Contains("TargetType=\"{x:Type GridViewColumnHeader}\"", StringComparison.Ordinal),
            "The existing-NPC before/after review does not pin readable dark-theme row and header contrast.");
        var integratedEditor = viewModel.Collections.CreateEditor();
        integratedEditor.Keywords.Add(new NpcReferenceRowViewModel(
            "M3ArchetypeSSE.esp|0x00000920"));
        Assert(integratedEditor.TryAccept(),
            $"Desktop collection handoff rejected a typed source reference: {integratedEditor.ValidationMessage}");
        viewModel.Collections.Commit(integratedEditor);
        viewModel.ReviewAsync().GetAwaiter().GetResult();
        Assert(viewModel.IsReviewed && viewModel.Changes.Any(change =>
                   change.Field == "Keywords" &&
                   change.After.Contains("0x00000920", StringComparison.OrdinalIgnoreCase)),
            "Desktop collection commit did not reach the shared exact-change review transaction.");

        string refusalRoot = Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "03-builds", "tests",
            $"desktop-collection-refusal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(refusalRoot);
        string refusalSource = Path.Combine(
            refusalRoot, Path.GetFileName(context.InitialRequest.InputPlugin.Value));
        File.Copy(context.InitialRequest.InputPlugin.Value, refusalSource);
        try
        {
            var refusalRequest = context.InitialRequest with
            {
                InputPlugin = new WorkspacePath(refusalSource),
                ExpectedInputSha256 = new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(refusalSource)))),
                OutputRoot = new WorkspacePath(Path.Combine(refusalRoot, "future-output"))
            };
            using var refusalViewModel = new ExistingNpcEditViewModel(
                context.Service, context.FormChoices, labRoot, refusalRequest);
            refusalViewModel.LoadNpcAsync().GetAwaiter().GetResult();
            Assert(refusalViewModel.Collections.IsLoaded,
                "Desktop refusal fixture did not first load its complete source snapshot.");
            File.Delete(refusalSource);
            refusalViewModel.LoadNpcAsync().GetAwaiter().GetResult();
            Assert(!refusalViewModel.Collections.IsLoaded &&
                   refusalViewModel.Verdict == "NPC unavailable",
                "A refused reload retained the previously loaded collection snapshot.");
        }
        finally
        {
            if (Directory.Exists(refusalRoot))
                Directory.Delete(refusalRoot, recursive: true);
        }
        Console.WriteLine("PASS existing-NPC collection editor is typed, lossless, and cancel-safe.");
    }

    private static void RunExistingNpcStatsEditorTest()
    {
        var source = new PluginName("Source.esp");
        var skillValues = Enum.GetValues<NpcSkill>()
            .ToImmutableDictionary(skill => skill, _ => (byte)10);
        var skillOffsets = Enum.GetValues<NpcSkill>()
            .ToImmutableDictionary(skill => skill, _ => (byte)1);
        var snapshot = new NpcOverrideSourceSnapshot(
            new EditorId("SourceNpc"),
            new NpcName("Source NPC"),
            NpcSex.Female,
            50,
            new NpcArchetypeReferences(null, null, null, null),
            new NpcStatsSnapshot(
                new NpcLevelValue(NpcLevelMode.Fixed, 12),
                7, 1, 2, 3, 5, 50, 100, 10, 0, 1,
                new NpcPlayerSkillsSnapshot(
                    100, 100, 100, skillValues, skillOffsets, 512, 2),
                [NpcFlag.Female, NpcFlag.Essential, NpcFlag.Unique]),
            new NpcKeywordSnapshot([], []),
            new NpcFactionSnapshot([]),
            new NpcInventorySnapshot([]),
            new NpcOutfitSnapshot(null, null),
            new NpcPerkSnapshot([]),
            new NpcActorEffectSnapshot([]));
        var state = new ExistingNpcStatsState();
        state.Load(snapshot);
        Assert(state.IsLoaded && !state.HasChanges && state.BuildPatch() is null &&
               state.Summary.Contains("18 skills", StringComparison.Ordinal),
            "Statistics state did not retain the complete loaded Skyrim snapshot.");

        var cancelled = state.CreateEditor();
        cancelled.PlayerHealth = "500";
        cancelled.Flags.First(row => row.Flag == NpcFlag.Essential).IsEnabled = false;
        Assert(!state.HasChanges && state.BuildPatch() is null,
            "An uncommitted statistics editor session mutated caller state.");

        var invalid = state.CreateEditor();
        invalid.LevelMode = NpcLevelMode.Multiplier;
        invalid.Level = "1.2345";
        Assert(!invalid.TryAccept() && invalid.AcceptedValues is null,
            "Statistics editor accepted a multiplier with excessive precision.");
        invalid.Level = "1.5";
        invalid.CalcMinLevel = "51";
        Assert(!invalid.TryAccept() &&
               invalid.ValidationMessage.Contains("cannot exceed", StringComparison.Ordinal),
            "Statistics editor accepted an inverted calculated-level range.");
        invalid.CalcMinLevel = "5";
        invalid.Skills.First(row => row.Skill == NpcSkill.Speech).Value = "256";
        Assert(!invalid.TryAccept() &&
               invalid.ValidationMessage.Contains("0 and 255", StringComparison.Ordinal),
            "Statistics editor accepted an out-of-range skill value.");
        invalid.Skills.First(row => row.Skill == NpcSkill.Speech).Value = "10";
        invalid.FarAwayModelDistance = "NaN";
        Assert(!invalid.TryAccept() &&
               invalid.ValidationMessage.Contains("finite", StringComparison.Ordinal),
            "Statistics editor accepted a non-finite far-model distance.");

        var accepted = state.CreateEditor();
        accepted.LevelMode = NpcLevelMode.Multiplier;
        accepted.Level = "1.5";
        accepted.CalcMinLevel = "10";
        accepted.CalcMaxLevel = "40";
        accepted.PlayerHealth = "500";
        accepted.Skills.First(row => row.Skill == NpcSkill.Speech).Value = "42";
        accepted.FarAwayModelDistance = "1234.5";
        accepted.GearedUpWeapons = "7";
        accepted.Flags.First(row => row.Flag == NpcFlag.Essential).IsEnabled = false;
        accepted.Flags.First(row => row.Flag == NpcFlag.Protected).IsEnabled = true;
        Assert(accepted.TryAccept(),
            $"Valid complete Skyrim statistics were refused: {accepted.ValidationMessage}");
        state.Commit(accepted);

        var patch = state.BuildPatch();
        Assert(state.HasChanges && patch is not null &&
               patch.Level == new NpcLevelValue(NpcLevelMode.Multiplier, 1.5m) &&
               patch.XpValueOffset is null && patch.MagickaOffset is null &&
               patch.StaminaOffset is null && patch.HealthOffset is null &&
               patch.CalcMinLevel == 10 && patch.CalcMaxLevel == 40 &&
               patch.SpeedMultiplier is null && patch.DispositionBase is null &&
               patch.BleedoutOverride is null && patch.Height is null &&
               patch.PlayerSkills?.Health == 500 &&
               patch.PlayerSkills.Magicka is null && patch.PlayerSkills.Stamina is null &&
               patch.PlayerSkills.Values is { Count: 1 } values &&
               values[NpcSkill.Speech] == 42 && patch.PlayerSkills.Offsets is null &&
               patch.PlayerSkills.FarAwayModelDistance == 1234.5f &&
               patch.PlayerSkills.GearedUpWeapons == 7 &&
               patch.Flags?.Set.SequenceEqual([NpcFlag.Protected]) == true &&
               patch.Flags.Clear.SequenceEqual([NpcFlag.Essential]),
            "Committed statistics did not produce the exact changed-field-only patch.");
        Console.WriteLine(
            "PASS complete Skyrim statistics preserve Cancel and emit exact typed changes.");
    }

    private static void RunAnimationPickerViewModelTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        string root = Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "03-builds", "tests",
            $"desktop-animation-picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string manifest = Path.Combine(root, "animations.json");
        File.WriteAllText(manifest,
            "{\"schemaVersion\":1,\"edition\":\"skyrimse\",\"animations\":[" +
            "{\"id\":\"walk\",\"name\":\"Walk Forward\",\"path\":\"animations/MT/Neutral/walk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":10,\"fps\":30,\"roles\":[\"Core\",\"MT\"],\"stateAxes\":\"Forward\"}," +
            "{\"id\":\"attack\",\"name\":\"Power Attack\",\"path\":\"animations/Weapon/attack.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":12,\"fps\":30,\"additive\":true,\"role\":\"Weapon\",\"femaleOnly\":true,\"firstPersonOnly\":true,\"behaviorGraph\":true}," +
            "{\"id\":\"talk\",\"name\":\"Talk Gesture\",\"path\":\"animations/Dialogue/talk.hkx\",\"skeleton\":\"meshes/skeleton.nif\",\"frames\":8,\"fps\":24,\"role\":\"Idle\",\"category\":\"Talk\"}]}");
        try
        {
            var viewModel = new AnimationPickerViewModel(
                PreviewAnimationComposition.CreatePickerService(policy, labRoot))
            {
                ManifestPath = manifest,
                SelectedEdition = GameEdition.SkyrimSpecialEdition,
                IsFemale = false
            };
            viewModel.LoadAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            AnimationTreeItemViewModel[] defaultItems =
                FlattenAnimationTree(viewModel.Groups).ToArray();
            Assert(defaultItems.Where(item => item.Clip is not null)
                       .Select(item => item.Clip!.Id)
                       .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
                   defaultItems.Any(item => item.Clip?.Id == "walk") &&
                   defaultItems.Any(item => item.Clip?.Id == "talk") &&
                   !defaultItems.Any(item => item.Clip?.Id == "attack") &&
                   viewModel.Status.StartsWith("2 of 3", StringComparison.Ordinal) &&
                   viewModel.ManifestHash.Length == 64,
                "Animation picker did not apply the default male and perspective filters to its hash-bound catalog.");

            var femaleViewModel = new AnimationPickerViewModel(
                PreviewAnimationComposition.CreatePickerService(policy, labRoot))
            {
                ManifestPath = manifest,
                SelectedEdition = GameEdition.SkyrimSpecialEdition,
                IsFemale = true
            };
            femaleViewModel.LoadAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(!FlattenAnimationTree(femaleViewModel.Groups)
                       .Any(item => item.Clip?.Id == "attack"),
                "First-person-only animation was visible before the explicit toggle.");
            femaleViewModel.ShowFirstPerson = true;
            Assert(FlattenAnimationTree(femaleViewModel.Groups)
                       .Any(item => item.Clip?.Id == "attack"),
                "Female target did not retain its female-only animation when perspective filtering was broadened.");

            File.Delete(manifest);
            viewModel.ShowFirstPerson = true;
            viewModel.FilterByGender = false;
            viewModel.Filter = "power weapon";
            AnimationTreeItemViewModel[] filtered =
                FlattenAnimationTree(viewModel.Groups).ToArray();
            AnimationTreeItemViewModel attack =
                filtered.Single(item => item.Clip?.Id == "attack");
            viewModel.Select(viewModel.Groups[0]);
            Assert(!viewModel.CanAccept,
                "Animation picker accepted a role or folder branch.");
            viewModel.Select(attack);
            PreviewAnimationSelection selection = viewModel.AcceptSelection() ??
                throw new InvalidOperationException(
                    "Animation picker did not return its selected leaf.");
            Assert(selection.Clip.Id == "attack" &&
                   selection.ManifestSha256.Value == viewModel.ManifestHash,
                "Animation picker did not return the exact leaf and manifest hash.");
            viewModel.CommitSelection(selection);

            viewModel.Filter = "walk";
            Assert(!viewModel.CanAccept && viewModel.SelectedClip is null &&
                   viewModel.CommittedSelectionSummary.Contains(
                       "Power Attack", StringComparison.Ordinal),
                "A hidden leaf remained acceptable or changed the committed selection.");
            viewModel.Filter = string.Empty;
            Assert(viewModel.SelectedClip?.Id == "attack",
                "The committed current clip was not restored after broadening filters.");
            viewModel.CancelPendingSelection();
            Assert(viewModel.CommittedSelectionSummary.Contains(
                       "Power Attack", StringComparison.Ordinal) &&
                   !File.Exists(manifest),
                "Cancel changed the caller selection or filtering reread the deleted manifest.");

            string malformed = Path.Combine(root, "malformed.json");
            File.WriteAllText(malformed, "{not-json}");
            var refused = new AnimationPickerViewModel(
                PreviewAnimationComposition.CreatePickerService(policy, labRoot))
            {
                ManifestPath = malformed,
                SelectedEdition = GameEdition.SkyrimSpecialEdition
            };
            refused.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(refused.Groups.Count == 0 &&
                   refused.ManifestHash == "Refused" &&
                   !refused.CanAccept,
                "Malformed animation input retained a partial tree or selection.");
            Console.WriteLine(
                "PASS animation picker caches one hash-bound catalog and returns only typed leaves.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<AnimationTreeItemViewModel>
        FlattenAnimationTree(
            IEnumerable<AnimationTreeItemViewModel> roots)
    {
        foreach (AnimationTreeItemViewModel item in roots)
        {
            yield return item;
            foreach (AnimationTreeItemViewModel child in
                     FlattenAnimationTree(item.Children))
            {
                yield return child;
            }
        }
    }

    private static void RunDesktopStartupOptionsTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var requestFile = new WorkspacePath(Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "01-source-copies", "gate2-emi2",
            "execution-request-v5.json"));
        var requestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(requestFile.Value)));
        var selected = DesktopStartupRequestOptions.Resolve([
            "--race-menu-request", requestFile.Value,
            "--race-menu-request-sha256", requestHash
        ], labRoot);
        Assert(selected.HasExplicitRequest && selected.Error is null &&
               selected.RequestFile == requestFile &&
               string.Equals(selected.RequestSha256, requestHash,
                   StringComparison.OrdinalIgnoreCase),
            "The desktop startup contract did not retain the explicit hash-bound request.");

        var missingHash = DesktopStartupRequestOptions.Resolve([
            "--race-menu-request", requestFile.Value
        ], labRoot);
        var outsideWorkspace = DesktopStartupRequestOptions.Resolve([
            "--race-menu-request", @"F:\ExampleGame\request.json",
            "--race-menu-request-sha256", requestHash
        ], labRoot);
        var unknown = DesktopStartupRequestOptions.Resolve(["--build-now", "true"], labRoot);
        var defaultSelection = DesktopStartupRequestOptions.Resolve([], labRoot);
        Assert(!defaultSelection.HasExplicitRequest && defaultSelection.Error is null &&
               !missingHash.HasExplicitRequest && missingHash.Error?.Contains(
                   "supplied together", StringComparison.Ordinal) == true &&
               !outsideWorkspace.HasExplicitRequest && outsideWorkspace.Error?.Contains(
                   "below K:\\ExampleWorkspace", StringComparison.Ordinal) == true &&
               !unknown.HasExplicitRequest && unknown.Error?.Contains(
                   "Unknown desktop startup option", StringComparison.Ordinal) == true,
            "The desktop startup contract did not fail closed for partial, protected-root, or unknown arguments.");
        Console.WriteLine("PASS desktop startup accepts only one explicit K-local hash-bound request.");
    }

    private static void RunBlankNpcDefaultCompositionTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        BlankNpcDesktopContext context = BlankNpcDesktopComposition.Create(policy, labRoot);
        string outputRoot = context.InitialRequest.OutputRoot.Value;
        Assert(!Directory.Exists(outputRoot) && !File.Exists(outputRoot),
            "The default desktop blank-NPC regression output was not fresh.");

        try
        {
            BlankNpcBuildResult result = context.Service.ExecuteAsync(
                    context.InitialRequest, null, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            string diagnostics = string.Join(" | ", result.Diagnostics.Select(
                item => $"{item.Code}: {item.Message}"));
            Assert(result.Completed && result.Artifact is not null &&
                   File.Exists(result.Artifact.Plugin.Value) &&
                   File.Exists(result.Artifact.FaceGeom.Value) &&
                   File.Exists(result.Artifact.FaceTint.Value) &&
                   File.Exists(result.Artifact.Manifest.Value),
                "The shipped desktop blank-NPC composition refused its qualified DXT5 provider or failed to retain a verified package. " +
                diagnostics);
        }
        finally
        {
            string ownedParent = Path.Combine(labRoot.Value, "projects",
                "NpcManagerReimplementation", "03-builds", "work",
                "gate1-blank-npc-walking-product");
            if (Directory.Exists(outputRoot))
            {
                Assert(new WorkspacePath(outputRoot).IsUnder(
                           new WorkspacePath(ownedParent)) &&
                       !File.GetAttributes(outputRoot).HasFlag(
                           FileAttributes.ReparsePoint),
                    "The desktop blank-NPC regression refused to clean an unowned output.");
                Directory.Delete(outputRoot, recursive: true);
            }
        }

        Console.WriteLine(
            "PASS shipped desktop blank-NPC composition decodes its qualified DXT5 provider and retains a verified package.");
    }

    private static void RunSchema5DefaultCompositionTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        var context = RaceMenuNpcDesktopComposition.Create(
            policy,
            labRoot,
            new QualifiedFaceGeomCarrierService(policy, labRoot));

        Assert(context.FaceTintBuildGuard is ExactOnlyFaceTintBuildService &&
               context.InitialRequestFile.Value.EndsWith(
                   "execution-request-v5.json", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(context.InitialRequestSha256),
            "The desktop RaceMenu composition did not bind its schema-5 request and exact-only FaceTint guard.");

        RaceMenuNpcExecutionRequestFileLoadResult loaded = context.RequestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    context.InitialRequestFile,
                    new Sha256Hash(context.InitialRequestSha256)),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert(loaded.Loaded && loaded.Request is not null &&
               loaded.Request.AssetAuthority.ManifestPath.Value.EndsWith(
                   "standalone-assets-v5.json", StringComparison.OrdinalIgnoreCase),
            "The desktop schema-5 default request did not load its exact standalone authority.");

        using (JsonDocument manifest = JsonDocument.Parse(
                   File.ReadAllBytes(loaded.Request!.AssetAuthority.ManifestPath.Value)))
        {
            Assert(manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 5,
                "The desktop default authority is not schema version 5.");
        }

        using var viewModel = new RaceMenuNpcBuildViewModel(
            context.RequestLoader,
            context.BuildService,
            context.PresetCatalogService,
            context.PresetCatalogLifetime,
            labRoot,
            context.InitialRequestFile,
            context.InitialRequestSha256,
            context.OutputParent,
            preflightService: new DesktopPreflightService());
        viewModel.ReviewAsync().GetAwaiter().GetResult();
        viewModel.ReviewPreflightAsync().GetAwaiter().GetResult();
        Assert(viewModel.HasReviewedRequest && viewModel.IsReady &&
               viewModel.Verdict == "Request reviewed",
            "The real desktop schema-5 default did not reach a reviewed, ready state.");
        viewModel.PresentStartupError("synthetic invalid startup request");
        Assert(!viewModel.HasReviewedRequest && !viewModel.IsReady &&
               viewModel.Verdict == "Startup request invalid" &&
               viewModel.Diagnostics.Single().Contains(
                   "synthetic invalid startup request", StringComparison.Ordinal),
            "An invalid desktop startup request did not fail closed in the visible state model.");
        Console.WriteLine(
            "PASS desktop default reviews schema 5 and invalid startup state fails closed.");
    }

    private static void RunPresetCompletionViewModelTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var requestFile = new WorkspacePath(Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "01-source-copies", "gate2-emi2",
            "execution-request.json"));
        var requestHash = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(requestFile.Value))));
        var service = new CompletingRaceMenuNpcBuildService();
        using var viewModel = new RaceMenuNpcBuildViewModel(
            new RaceMenuNpcExecutionRequestFileLoader(labRoot),
            service,
            EmptyPresetCatalogService.Instance,
            NoopDisposable.Instance,
            labRoot,
            requestFile,
            requestHash.Value,
            new WorkspacePath(Path.Combine(labRoot.Value, "projects",
                "NpcManagerReimplementation", "03-builds", "work")),
            preflightService: new DesktopPreflightService());

        viewModel.ReviewAsync().GetAwaiter().GetResult();
        viewModel.ReviewPreflightAsync().GetAwaiter().GetResult();
        Assert(viewModel.HasReviewedRequest && viewModel.IsReady &&
               viewModel.PresetPath.EndsWith("emi2-neutral.jslot",
                   StringComparison.OrdinalIgnoreCase) &&
               viewModel.CharGenPath.EndsWith("emi2-neutral.nif",
                   StringComparison.OrdinalIgnoreCase) &&
               viewModel.FaceTintPath.EndsWith("emi2-neutral.dds",
                   StringComparison.OrdinalIgnoreCase),
            "The desktop did not review the real prepared preset, CharGen, and tint inputs.");

        viewModel.DisplayName = "Emi Desktop Proof";
        viewModel.EditorId = "EmiDesktopProof";
        viewModel.PluginName = "EmiDesktopProof.esp";
        viewModel.RunAsync().GetAwaiter().GetResult();

        var executed = service.LastRequest ?? throw new InvalidOperationException(
            "The desktop did not call IRaceMenuNpcBuildService.");
        Assert(executed.Build.Identity.Name.Value == "Emi Desktop Proof" &&
               executed.Build.Identity.EditorId.Value == "EmiDesktopProof" &&
               executed.Build.OutputPlugin.Value == "EmiDesktopProof.esp" &&
               executed.Build.OutputRoot.IsUnder(labRoot) &&
               !Directory.Exists(executed.Build.OutputRoot.Value),
            "Desktop in-memory identity/output overrides were not passed to the shared service.");
        Assert(viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
               viewModel.ResultPlugin.Contains("EmiDesktopProof.esp", StringComparison.Ordinal) &&
               viewModel.ResultFaceGeom.Contains(".nif", StringComparison.OrdinalIgnoreCase) &&
               viewModel.ResultFaceTint.Contains(".dds", StringComparison.OrdinalIgnoreCase) &&
               viewModel.ResultBodyGen.Contains("morphs.ini", StringComparison.OrdinalIgnoreCase) &&
               viewModel.ResultPex.Contains("NPCM_Manolov_ApplySSE.pex", StringComparison.Ordinal) &&
               viewModel.ResultPackage.Contains("npcmanager-package.json", StringComparison.Ordinal),
            "Desktop completion did not present plugin/NIF/DDS/BodyGen/PEX/package results.");
        Console.WriteLine("PASS preset desktop completion uses the shared request and build services.");
    }

    private static void RunNpcBuildPreflightViewModelTest()
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "NpcManager.Desktop",
            "RaceMenuNpcBuildPanel.xaml"));
        string fixtureRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "test-work",
            $"desktop-npc-build-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            RunNpcBuildPreflightViewModelTestCore(
                xaml,
                new WorkspacePath(fixtureRoot));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static void RunNpcBuildPreflightViewModelTestCore(
        string xaml,
        WorkspacePath root)
    {
        foreach (string step in new[]
                 {
                     "1  PRESET",
                     "2  RECORDS AND ASSETS",
                     "4  PREFLIGHT",
                     "5  BUILD AND PREVIEW"
                 })
            Assert(xaml.Contains(step, StringComparison.Ordinal),
                $"The existing NPC creator is missing approved step '{step}'.");
        Assert(xaml.Contains("TargetCardEyebrow", StringComparison.Ordinal) &&
               xaml.Contains("Review _preflight", StringComparison.Ordinal) &&
               !xaml.Contains("1  PREPARED REQUEST", StringComparison.Ordinal) &&
               !xaml.Contains("2  SOURCE REVIEW", StringComparison.Ordinal),
            "The desktop retained an obsolete creator step or omitted appearance/preflight review.");

        RaceMenuNpcExecutionRequest request = BuildPreflightDesktopRequest(root);
        var source = new WorkspacePath(Path.Combine(root.Value,
            "desktop-preflight-request.json"));
        var sourceHash = new Sha256Hash(new string('1', 64));
        var build = new CompletingRaceMenuNpcBuildService();
        var preflight = new DesktopPreflightService();
        var preview = new RefusingDesktopNpcPreviewComposer();
        using var viewModel = new RaceMenuNpcBuildViewModel(
            new FixedRequestLoader(request),
            build,
            EmptyPresetCatalogService.Instance,
            NoopDisposable.Instance,
            root,
            source,
            sourceHash.Value,
            root,
            preflightService: preflight,
            npcVisualPreviewComposer: preview);
        viewModel.ApplyReviewedIntake(new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            root,
            new WorkspacePath(Path.Combine(root.Value, "Data")),
            new WorkspacePath(Path.Combine(root.Value, "loadorder.txt")),
            root,
            new Sha256Hash(new string('2', 64)),
            [], [], [], [], 0,
            new Sha256Hash(new string('3', 64)),
            new Sha256Hash(new string('4', 64)),
            RuntimeAuthority: false));

        viewModel.ReviewAsync().GetAwaiter().GetResult();
        Assert(viewModel.HasReviewedRequest &&
               !viewModel.HasReviewedPreflight &&
               !viewModel.CanRun &&
               viewModel.CanReviewPreflight &&
               viewModel.TargetCardEyebrow == "3  APPEARANCE" &&
               viewModel.BuildActionContent == "Build & verify",
            "The desktop allowed build before the explicit production preflight.");

        viewModel.ReviewPreflightAsync().GetAwaiter().GetResult();
        Assert(viewModel.HasReviewedPreflight &&
               viewModel.PreviewReady &&
               viewModel.CanRun &&
               viewModel.BuildActionContent == "Build, verify & preview" &&
               viewModel.PreflightSummary.Contains("Required 1/1",
                   StringComparison.Ordinal),
            "A ready reviewed preflight did not enable the preview-aware primary action.");

        viewModel.RunAsync().GetAwaiter().GetResult();
        Assert(preflight.VerifyCalls == 1 &&
               preview.Calls == 1 &&
               viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
               viewModel.ResultPackage.Contains(
                   "npcmanager-package.json", StringComparison.Ordinal) &&
               viewModel.ResultPreview == "\u2014" &&
               viewModel.Status.Contains("preview was refused",
                   StringComparison.OrdinalIgnoreCase) &&
               viewModel.Diagnostics.Any(item => item.Contains(
                   "npc-build-preview-refused", StringComparison.Ordinal)),
            "Preview refusal incorrectly invalidated or hid the verified game artifacts.");

        viewModel.DisplayName = "Changed after review";
        Assert(!viewModel.HasReviewedPreflight && !viewModel.CanRun,
            "An appearance change did not invalidate the reviewed preflight.");
    }

    private static RaceMenuNpcExecutionRequest BuildPreflightDesktopRequest(
        WorkspacePath root)
    {
        WorkspacePath PathOf(string name) => new(Path.Combine(root.Value, name));
        Sha256Hash hash = new(new string('a', 64));
        var skyrim = new PluginName("Skyrim.esm");
        var reference = new FormReference(skyrim, new FormId(0x13746));
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            new RaceMenuNpcPresetBundle(
                PathOf("bundle.json"), hash,
                PathOf("selected.jslot"), hash,
                PathOf("char-gen.nif"), hash,
                PathOf("char-gen.dds"), hash,
                new RaceMenuNpcRecordAuthority(
                    PathOf("record.json"), hash)),
            new BlankNpcProviderBindingRequest(
                PathOf("provider.json"), hash,
                GameEdition.SkyrimSpecialEdition,
                NpcSex.Female,
                PathOf("template.esp"), hash,
                new FormId(0x800),
                PathOf("carrier.nif"), hash,
                PathOf("tint.json"),
                PathOf("provider-data"),
                PathOf("dependencies.json")),
            PathOf("desktop-preflight-output"),
            new PluginName("DesktopPreflight.esp"),
            new NpcCreationIdentity(
                new EditorId("DesktopPreflight"),
                new NpcName("Desktop Preflight")),
            new SkyrimNpcCreationTraits(
                NpcSex.Female, NpcCreationRole.Follower,
                true, false, true, false, true),
            new SkyrimNpcCreationReferences(
                reference, reference, reference, reference, reference),
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 0, 0,
                50, 50, 50, 1f, 0f, 255));
        return new RaceMenuNpcExecutionRequest(
            build,
            new RaceMenuNpcStandaloneAssetAuthority(
                PathOf("standalone.json"), hash));
    }

    private static void RunExistingNpcViewModelTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var context = RaceMenuNpcDesktopComposition.Create(
            policy,
            labRoot,
            new QualifiedFaceGeomCarrierService(policy, labRoot));
        var loaded = context.RequestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    context.InitialRequestFile,
                    new Sha256Hash(context.InitialRequestSha256)),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        var request = loaded.Request ?? throw new InvalidDataException(
            "The desktop fixture request did not load.");
        var sourcePlugin = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "01-source-copies",
            "m2-fixtures",
            "sse",
            "Data",
            "M2FixtureSSE.esp"));
        var existing = request with
        {
            Build = request.Build with
            {
                ExistingNpcTarget = new RaceMenuExistingNpcTarget(
                    sourcePlugin,
                    new Sha256Hash(Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(sourcePlugin.Value)))),
                    new FormId(0x800))
            }
        };
        var service = new CompletingRaceMenuNpcBuildService();
        using var viewModel = new RaceMenuNpcBuildViewModel(
            new FixedRequestLoader(existing),
            service,
            context.PresetCatalogService,
            context.PresetCatalogLifetime,
            labRoot,
            context.InitialRequestFile,
            context.InitialRequestSha256,
            context.OutputParent,
            preflightService: new DesktopPreflightService());

        viewModel.ReviewAsync().GetAwaiter().GetResult();
        viewModel.ReviewPreflightAsync().GetAwaiter().GetResult();
        var originalName = viewModel.DisplayName;
        var originalEditorId = viewModel.EditorId;
        Assert(viewModel.HasReviewedRequest && viewModel.IsReady &&
               viewModel.IsExistingNpcTarget && !viewModel.IsNewNpcTarget &&
               !viewModel.CanEditIdentity &&
               viewModel.TargetCardEyebrow == "3  APPEARANCE" &&
               viewModel.HeroEyebrow.Contains("EXISTING NPC", StringComparison.Ordinal) &&
               viewModel.HeroTitle.Contains("existing NPC override", StringComparison.Ordinal) &&
               viewModel.IdentityHelpText.Contains("cannot be changed", StringComparison.Ordinal) &&
               viewModel.PluginLabel == "Override plugin filename" &&
               viewModel.BuildActionName == "Build and verify NPC package" &&
               viewModel.WorkspaceAutomationName == "Preset to existing NPC workspace",
            "The desktop did not enter its retained-identity existing-NPC state.");

        viewModel.PluginName = "ExistingDesktopProof.esp";
        viewModel.RunAsync().GetAwaiter().GetResult();
        var executed = service.LastRequest ?? throw new InvalidOperationException(
            "The existing-NPC desktop did not call the shared build service.");
        Assert(executed.Build.Identity.Name.Value == originalName &&
               executed.Build.Identity.EditorId.Value == originalEditorId &&
               executed.Build.ExistingNpcTarget == existing.Build.ExistingNpcTarget &&
               viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
               viewModel.ResultNpc.Contains("M2FixtureSSE.esp|0x00000800", StringComparison.Ordinal) &&
               viewModel.ResultPlugin.Contains("ExistingDesktopProof.esp", StringComparison.Ordinal),
            "The desktop changed retained identity or failed to present its existing-NPC package result.");

        viewModel.RequestSha256 = new string('0', 64);
        Assert(!viewModel.HasReviewedRequest && viewModel.IsNewNpcTarget &&
               viewModel.DisplayName.Length == 0 && viewModel.EditorId.Length == 0 &&
               viewModel.PluginName.Length == 0 && viewModel.OutputRoot.Length == 0 &&
               viewModel.PresetPath == "\u2014" && viewModel.ResultPlugin == "\u2014" &&
               viewModel.SourceSummary == "Choose and review a prepared request." &&
               viewModel.Verdict == "Not reviewed",
            "Changing a reviewed request did not clear the stale trusted preview and output state.");
        Console.WriteLine("PASS existing-NPC desktop state retains identity and presents the shared result.");
    }

    private static void RunCancellationViewModelTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var requestFile = new WorkspacePath(Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "01-source-copies", "gate2-emi2",
            "execution-request.json"));
        var requestHash = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(requestFile.Value))));
        var service = new BlockingCancellationBuildService();
        using var viewModel = new RaceMenuNpcBuildViewModel(
            new RaceMenuNpcExecutionRequestFileLoader(labRoot),
            service,
            EmptyPresetCatalogService.Instance,
            NoopDisposable.Instance,
            labRoot,
            requestFile,
            requestHash.Value,
            new WorkspacePath(Path.Combine(labRoot.Value, "projects",
                "NpcManagerReimplementation", "03-builds", "work")),
            preflightService: new DesktopPreflightService());

        viewModel.ReviewAsync().GetAwaiter().GetResult();
        viewModel.ReviewPreflightAsync().GetAwaiter().GetResult();
        var outputRoot = viewModel.OutputRoot;
        var run = viewModel.RunAsync();
        service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert(viewModel.IsBusy && !viewModel.IsCancelling &&
               viewModel.CancelCommand.CanExecute(null),
            "The desktop build did not expose an enabled cancellation action while active.");

        viewModel.CancelCommand.Execute(null);
        service.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter().GetResult();
        Assert(viewModel.IsBusy && viewModel.IsCancelling &&
               !viewModel.CancelCommand.CanExecute(null) &&
               viewModel.CancelActionContent == "Cancelling..." &&
               viewModel.CancelActionName == "Cancelling preset NPC build" &&
               viewModel.Status.Contains("Cancelling", StringComparison.Ordinal),
            "The desktop did not expose a one-shot visible cancellation state.");

        service.ReleaseCancellation();
        Assert(!run.Wait(TimeSpan.FromMilliseconds(200)) &&
               viewModel.IsBusy && viewModel.IsCancelling &&
               viewModel.Status.Contains("Cancelling", StringComparison.Ordinal),
            "The desktop collapsed the visible cancellation acknowledgement into an imperceptible flicker.");
        run.GetAwaiter().GetResult();
        Assert(!viewModel.IsBusy && !viewModel.IsCancelling &&
               !viewModel.CancelCommand.CanExecute(null) &&
               viewModel.Verdict == "Cancelled" &&
               viewModel.Status == "Creation cancelled. No verified package was retained." &&
               viewModel.ResultPlugin == "\u2014" && viewModel.ResultPackage == "\u2014" &&
               !Directory.Exists(outputRoot) && !File.Exists(outputRoot),
            "The desktop did not return to a clean cancelled state without retained output.");
        Console.WriteLine(
            "PASS desktop cancellation is one-shot, visible, cooperative, and leaves no result.");
    }

    private static void RunReviewedWorkspaceViewModelTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        using var viewModel = new ReviewedGameIntakeViewModel(
            ReviewedGameIntakeDesktopComposition.Create(policy, labRoot));

        viewModel.ReviewAsync().GetAwaiter().GetResult();
        Assert(viewModel.HasAcceptedIntake && viewModel.AcceptedIntake is not null &&
               viewModel.AcceptedIntake.Plugins.Length == 1 &&
               viewModel.ManifestHash.Length == 64 &&
               viewModel.IntakeFingerprint.Length == 64 &&
               viewModel.ProgressPercent == 100 &&
               !viewModel.IsProgressIndeterminate &&
               !viewModel.HasNoVisiblePlugins &&
               !viewModel.AcceptedIntake.RuntimeAuthority,
            "The desktop preflight did not retain the real hash-bound copied-Data intake and final progress state: " +
            $"accepted={viewModel.HasAcceptedIntake} plugins={viewModel.AcceptedIntake?.Plugins.Length} " +
            $"manifest={viewModel.ManifestHash.Length} fingerprint={viewModel.IntakeFingerprint.Length} " +
            $"progress={viewModel.ProgressPercent} indeterminate={viewModel.IsProgressIndeterminate} " +
            $"empty={viewModel.HasNoVisiblePlugins} status={viewModel.Status} verdict={viewModel.Verdict} " +
            $"diagnostics={string.Join(" | ", viewModel.Diagnostics)}");

        viewModel.FilterText = "no-such-plugin";
        Assert(viewModel.HasNoVisiblePlugins &&
               viewModel.PluginListState == "No plugins match the current filter.",
            "The reviewed plugin filter did not expose its explicit no-match state.");
        viewModel.FilterText = string.Empty;

        var accepted = viewModel.AcceptedIntake ?? throw new InvalidOperationException(
            "The reviewed intake disappeared after the acceptance assertion.");
        var bakeService = new RefusingUnexpectedFaceGenBatchService();
        using (var batch = new NativeFaceGenBatchViewModel(
                   bakeService, new StaleReviewedIntakeService(accepted)))
        {
            batch.ApplyReviewedIntake(accepted);
            batch.RunAsync().GetAwaiter().GetResult();
            Assert(bakeService.CallCount == 0 && batch.Verdict == "Workspace changed" &&
                   batch.Diagnostics.Any(item => item.Contains(
                       "gui-facegen-intake-stale", StringComparison.Ordinal)),
                "A stale reviewed intake reached the native FaceGen batch service.");
        }

        var handoffParent = Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "03-builds", "tests",
            $"desktop-reviewed-facegen-{Guid.NewGuid():N}");
        var futureOutput = new WorkspacePath(Path.Combine(handoffParent, "Data"));
        Directory.CreateDirectory(handoffParent);
        try
        {
            var handoffIntake = accepted with { OutputRoot = futureOutput };
            var stableIntake = new StableReviewedIntakeService(handoffIntake);
            var existingOutputRequired = new ExistingOutputRequiredFaceGenBatchService();
            using var batch = new NativeFaceGenBatchViewModel(
                existingOutputRequired, stableIntake);
            var consumedCount = 0;
            batch.ReviewedOutputConsumed += () => consumedCount++;
            batch.ApplyReviewedIntake(handoffIntake);
            batch.RunAsync().GetAwaiter().GetResult();
            Assert(stableIntake.SawAbsentOutput &&
                   existingOutputRequired.SawExistingEmptyOutput &&
                   existingOutputRequired.CallCount == 1 &&
                   consumedCount == 1 && !batch.CanStart &&
                   batch.Verdict == "Static pass",
                "The reviewed future output was not safely materialized and consumed between intake revalidation and the batch service.");
        }
        finally
        {
            if (Directory.Exists(handoffParent))
                Directory.Delete(handoffParent, recursive: true);
        }

        viewModel.OutputRoot += "-changed";
        Assert(!viewModel.HasAcceptedIntake && viewModel.AcceptedIntake is null &&
               viewModel.Verdict == "Review required" &&
               viewModel.Status.Contains("input changed", StringComparison.OrdinalIgnoreCase),
            "Changing a reviewed input did not immediately invalidate the desktop intake.");

        Console.WriteLine("PASS copied-workspace desktop reviews real inputs and invalidates stale state.");
    }

    private static void RunNativeFaceGenBatchViewModelTest()
    {
        var plugin = new PluginName("DesktopBatch.esp");
        var race = new FormReference(
            new PluginName("Skyrim.esm"), new FormId(0x13746));
        var target = new FaceGenBakeTarget(
            new FormId(0x800), plugin, plugin, [plugin], "DesktopBatchNpc",
            "Desktop Batch NPC", NpcSex.Female, race, [], 50F);
        var hash = new Sha256Hash(new string('A', 64));
        var dataRoot = new WorkspacePath(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\tests\desktop-native-input\Data");
        var outputRoot = new WorkspacePath(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\tests\desktop-native-output\Data");
        var artifact = new FaceGenNpcBakeArtifact(
            target,
            new WorkspacePath(Path.Combine(outputRoot.Value, "meshes", "actors",
                "character", "FaceGenData", "FaceGeom", plugin.Value,
                "00000800.nif")),
            hash, 128,
            new WorkspacePath(Path.Combine(outputRoot.Value, "textures", "actors",
                "character", "FaceGenData", "FaceTint", plugin.Value,
                "00000800.dds")),
            hash, 256, RuntimeAuthority: false);
        var outcome = new FaceGenNpcBakeResult(
            FaceGenNpcBakeStatus.Baked, target, artifact, []);
        var service = new CompletingFaceGenBatchService(
            new FaceGenBakeAllResult(FaceGenBakeAllStatus.Succeeded,
                1, 1, 0, 0, [outcome], []));
        using var viewModel = new NativeFaceGenBatchViewModel(service)
        {
            DataRoot = dataRoot.Value,
            PluginOrderText = "Skyrim.esm\r\nDesktopBatch.esp",
            WinningPlugin = plugin.Value,
            OutputRoot = outputRoot.Value
        };
        Assert(viewModel.CanStart && viewModel.CloseActionContent == "_Close",
            "The native FaceGen desktop did not become ready from explicit inputs.");
        viewModel.RunAsync().GetAwaiter().GetResult();
        Assert(service.Request is not null &&
               service.Request.DataRoot == dataRoot &&
               service.Request.OutputDataRoot == outputRoot &&
               service.Request.WinningPlugin == plugin &&
               service.Request.PluginOrder.Select(item => item.Value)
                   .SequenceEqual(["Skyrim.esm", "DesktopBatch.esp"]),
            "The native FaceGen desktop changed its typed batch request.");
        Assert(viewModel.Verdict == "Static pass" &&
               viewModel.Discovered == 1 && viewModel.Baked == 1 &&
               viewModel.ProgressPercent == 100 &&
               viewModel.ProgressEvents.Count == 2 &&
               viewModel.Outcomes.Count == 1 &&
               viewModel.CloseActionContent == "_Close" &&
               viewModel.RuntimeAuthority.Contains("false", StringComparison.Ordinal),
            "The native FaceGen desktop did not retain ordered static-only completion evidence.");

        var blocking = new BlockingFaceGenBatchService();
        using var cancelling = new NativeFaceGenBatchViewModel(blocking)
        {
            DataRoot = dataRoot.Value,
            PluginOrderText = plugin.Value,
            OutputRoot = outputRoot.Value
        };
        var closeActionStates = new List<bool>();
        cancelling.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NativeFaceGenBatchViewModel.CanUseCloseAction))
                closeActionStates.Add(cancelling.CanUseCloseAction);
        };
        Task run = cancelling.RunAsync();
        blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter().GetResult();
        cancelling.RequestCancellation();
        blocking.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter().GetResult();
        Assert(cancelling.IsBusy && cancelling.IsCancelling &&
               cancelling.IsProgressIndeterminate &&
               cancelling.CloseActionContent == "Cancelling..." &&
               !cancelling.CanUseCloseAction,
            "The native FaceGen dialog did not change Cancel into a one-shot cancelling state.");
        blocking.Release.SetResult();
        run.GetAwaiter().GetResult();
        Assert(!cancelling.IsBusy && !cancelling.IsCancelling &&
               !cancelling.IsProgressIndeterminate &&
               cancelling.Verdict == "Cancelled" &&
               cancelling.CloseActionContent == "_Close" &&
               cancelling.CanUseCloseAction &&
               closeActionStates.Count > 0 && closeActionStates[^1],
            "The native FaceGen dialog did not change its action to Close after cancellation completed.");
        Console.WriteLine(
            "PASS native FaceGen desktop maps the shared batch and retains Cancel-to-Close results.");
    }

    private static void RunFirstRenderSmoke()
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "The desktop smoke must execute on an STA thread.");

        App? app = null;
        MainWindow? window = null;
        try
        {
            WorkspacePath labRoot = ActorwrightWorkspace.ResolveRoot();
            app = new App(() => labRoot)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.InitializeComponent();
            int startupExitCode = app.RunDesktopStartup(
                _ => { },
                message => throw new InvalidOperationException(message));
            Assert(startupExitCode == 0,
                "The desktop startup boundary refused the render fixture.");
            DesktopCrashReporterTests.VerifyDispatcherWiring(app, labRoot);
            RunSkyrimMainWorkspaceVirtualizationTest();

            var leveledHeaderViewModel = new SkyrimLeveledListEditorViewModel([])
            {
                NameSuffix = "Rendered",
                ChanceNone = 25,
                MaxCount = 0,
                CalculateAllLevels = true,
                UseAll = true
            };
            var leveledHeaderWindow = new SkyrimLeveledListEditorWindow(
                leveledHeaderViewModel);
            try
            {
                leveledHeaderWindow.Show();
                leveledHeaderWindow.Dispatcher.Invoke(
                    () => { }, DispatcherPriority.ApplicationIdle);
                var editorIdPreview = FindVisualDescendant<TextBox>(
                    leveledHeaderWindow,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Complete leveled-list EditorID",
                        StringComparison.Ordinal));
                var chanceSlider = FindVisualDescendant<Slider>(
                    leveledHeaderWindow,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Chance None percent from zero through one hundred",
                        StringComparison.Ordinal));
                var maxSlider = FindVisualDescendant<Slider>(
                    leveledHeaderWindow,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Maximum count from zero through two hundred fifty five",
                        StringComparison.Ordinal));
                var nameLabel = FindVisualDescendant<Label>(
                    leveledHeaderWindow,
                    item => item.Content is string content &&
                            content.Contains("Name suffix", StringComparison.Ordinal));
                var expectedLabelColor = Color.FromRgb(0xF4, 0xF7, 0xFA);
                Assert(leveledHeaderWindow.IsVisible &&
                       leveledHeaderWindow.ActualWidth >= 520 &&
                       leveledHeaderWindow.ActualHeight >= 520 &&
                       editorIdPreview is { IsReadOnly: true, Text: "npcm_LVLI_Rendered" } &&
                       !Validation.GetHasError(editorIdPreview) &&
                       editorIdPreview.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Mode ==
                           BindingMode.OneWay &&
                       chanceSlider is { Minimum: 0, Maximum: 100, Value: 25 } &&
                       maxSlider is { Minimum: 0, Maximum: 255, Value: 0 } &&
                       nameLabel?.Foreground is SolidColorBrush labelForeground &&
                       labelForeground.Color == expectedLabelColor,
                    "The Gate 016 modal did not first-render its live identity or exact bounded controls safely.");
            }
            finally
            {
                leveledHeaderWindow.Close();
            }

            var leveledEntryViewModel = new SkyrimLeveledEntryEditorViewModel(
                SkyrimLeveledEntryEditorMode.Add,
                new SkyrimLeveledEntryCandidate(
                    new FormReference(new PluginName("OutfitBase.esm"), new FormId(0x800)),
                    SkyrimOutfitEditorItemKind.Armor,
                    "Rendered armor"));
            var leveledEntryWindow = new SkyrimLeveledEntryEditorWindow(
                leveledEntryViewModel);
            try
            {
                leveledEntryWindow.Show();
                leveledEntryWindow.Dispatcher.Invoke(
                    () => { }, DispatcherPriority.ApplicationIdle);
                var levelBox = FindVisualDescendant<TextBox>(
                    leveledEntryWindow,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Entry level from one through 32767",
                        StringComparison.Ordinal));
                var countBox = FindVisualDescendant<TextBox>(
                    leveledEntryWindow,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Entry count from one through 32767",
                        StringComparison.Ordinal));
                Assert(leveledEntryWindow.IsVisible &&
                       leveledEntryWindow.ActualWidth >= 500 &&
                       leveledEntryWindow.ActualHeight >= 440 &&
                       levelBox is { Text: "1" } && countBox is { Text: "1" } &&
                       leveledEntryViewModel.CanAccept,
                    "The Gate 017 modal did not first-render its immutable item and editable LVLO values.");
            }
            finally
            {
                leveledEntryWindow.Close();
            }

            Assert(selectivePasteRenderFixture is not null,
                "The selective-paste render fixture was not prepared.");
            var selectivePasteWindow = new SkyrimSelectiveAppearancePasteWindow(
                selectivePasteRenderFixture!);
            try
            {
                selectivePasteWindow.Show();
                selectivePasteWindow.Dispatcher.Invoke(
                    () => { }, DispatcherPriority.ApplicationIdle);
                Assert(selectivePasteWindow.IsVisible &&
                       selectivePasteWindow.ActualWidth >= 760 &&
                       selectivePasteWindow.ActualHeight >= 560 &&
                       FindVisualDescendant<CheckBox>(
                           selectivePasteWindow,
                           item => string.Equals(
                               AutomationProperties.GetName(item),
                               "Body weight",
                               StringComparison.Ordinal)) is not null,
                    "The selective-paste modal did not render its real category controls.");
            }
            finally
            {
                selectivePasteWindow.Close();
            }

            var outfitPlugin = new PluginName("OutfitFixture.esp");
            var outfitArmor = new SkyrimOutfitEditorItem(
                new FormReference(outfitPlugin, new FormId(0x800)),
                SkyrimOutfitEditorItemKind.Armor,
                "Rendered armor",
                0x04);
            var outfitViewModel = new SkyrimOutfitEditorViewModel(
                [],
                [outfitArmor],
                new WorkspacePath(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures"),
                new WorkspacePath(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\fixtures\\OutfitFixture.esp"),
                new FormId(0x900),
                new FormId(0xA00),
                new WorkspacePath(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\render.outfit-proposal.json"),
                (_, _) => [],
                (_, _) => null);
            var outfitWindow = new SkyrimOutfitEditorWindow(outfitViewModel);
            try
            {
                outfitWindow.Show();
                outfitWindow.Dispatcher.Invoke(
                    () => { }, DispatcherPriority.ApplicationIdle);
                Assert(outfitWindow.IsVisible &&
                       outfitWindow.ActualWidth >= 980 &&
                       outfitWindow.ActualHeight >= 680 &&
                       FindVisualDescendant<Button>(
                           outfitWindow,
                           item => string.Equals(
                               AutomationProperties.GetName(item),
                               "Begin a new outfit",
                               StringComparison.Ordinal)) is not null,
                    "The outfit workbench did not render its real authoring controls.");
                var wholeOutfitRadio = FindVisualDescendant<RadioButton>(
                    outfitWindow,
                    item => item.Content is string content &&
                            content.Contains("outfit", StringComparison.OrdinalIgnoreCase));
                var previewPlan = FindVisualDescendant<TextBox>(
                    outfitWindow,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Qualified preview item plan",
                        StringComparison.Ordinal));
                var expectedTextColor = Color.FromRgb(0xF4, 0xF7, 0xFA);
                Assert(wholeOutfitRadio?.Foreground is SolidColorBrush radioForeground &&
                       radioForeground.Color == expectedTextColor &&
                       previewPlan?.Foreground is SolidColorBrush previewForeground &&
                       previewForeground.Color == expectedTextColor,
                    "The outfit preview controls rendered dark-on-dark instead of using the primary text color.");
            }
            finally
            {
                outfitWindow.Close();
            }

            var dialogWorkCalls = 0;
            var progressDialog = new RaceMenuNpcBuildProgressWindow(
                new ProgressDialogFixture(
                    "Verifying modal progress fixture.",
                    42,
                    "_Cancel",
                    "Cancel preset NPC build"),
                async () =>
                {
                    dialogWorkCalls++;
                    await Task.Delay(50);
                });
            progressDialog.ShowDialog();
            Assert(dialogWorkCalls == 1 && progressDialog.WorkStarted &&
                   progressDialog.WorkCompleted && !progressDialog.IsVisible,
                "The modal build-progress dialog did not run once and close itself.");
            Assert(progressDialog.ResizeMode == ResizeMode.NoResize &&
                   progressDialog.WindowStyle == WindowStyle.None &&
                   !progressDialog.ShowInTaskbar,
                "The build-progress dialog is not a fixed caller-blocking surface.");

            Exception? dispatcherFailure = null;
            var frame = new DispatcherFrame();
            app.DispatcherUnhandledException += (_, args) =>
            {
                dispatcherFailure = args.Exception;
                args.Handled = true;
                frame.Continue = false;
            };

            window = new MainWindow();
            var contentRendered = false;
            var applicationIdle = false;
            var timedOut = false;
            window.ContentRendered += (_, _) =>
            {
                if (contentRendered)
                {
                    return;
                }

                contentRendered = true;
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    applicationIdle = true;
                    frame.Continue = false;
                });
            };

            var timeout = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                timedOut = true;
                frame.Continue = false;
            };

            window.Show();
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();

            if (dispatcherFailure is not null)
            {
                throw new InvalidOperationException(
                    "The real desktop raised an unhandled exception before first-render idle.", dispatcherFailure);
            }

            Assert(!timedOut, "The real desktop did not reach first-render application idle within 15 seconds.");
            Assert(contentRendered, "The real MainWindow never raised ContentRendered.");
            Assert(applicationIdle, "The dispatcher did not reach ApplicationIdle after first render.");

            Assert(window.DataContext is WorkspaceShellViewModel
            {
                CreateNpc:
                {
                    ResultPlugin: "—",
                    ResultFaceGeom: "—",
                    ResultFaceTint: "—",
                    ResultManifest: "—"
                }
            },
                "The rendered workspace result placeholders are not the intended em dash; mojibake has regressed.");
            var shell = (WorkspaceShellViewModel)window.DataContext;
            Assert(shell.Message == shell.Preflight.Status,
                "The first-render shell footer does not describe the selected Open-workspace task.");

            var preflightAction = FindVisualDescendant<Button>(window,
                button => string.Equals(AutomationProperties.GetName(button), PreflightActionName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No rendered Button exposes automation name '{PreflightActionName}'.");
            Assert(preflightAction.IsLoaded && preflightAction.IsVisible,
                "The preflight action exists in markup but is not loaded and visible in the rendered window.");
            Assert(preflightAction.ActualWidth > 0 && preflightAction.ActualHeight > 0,
                "The rendered preflight action has no arranged size.");
            Assert(PresentationSource.FromVisual(preflightAction) is not null,
                "The preflight action is not connected to the rendered presentation source.");

            var automationPeer = FrameworkElementAutomationPeer.CreatePeerForElement(preflightAction)
                ?? new ButtonAutomationPeer(preflightAction);
            Assert(string.Equals(automationPeer.GetName(), PreflightActionName, StringComparison.Ordinal),
                "The rendered preflight action is not discoverable through its WPF automation peer.");

            var progress = FindVisualDescendant<ProgressBar>(window,
                control => string.Equals(AutomationProperties.GetName(control),
                    "Copied workspace review progress", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The rendered preflight ProgressBar is missing.");
            Assert(progress.IsLoaded && progress.IsVisible,
                "The copied-workspace progress indicator is not visible on the first task.");

            var pluginList = FindVisualDescendant<ListBox>(window,
                control => string.Equals(AutomationProperties.GetName(control),
                    "Reviewed plugin selection", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The rendered reviewed-plugin list is missing.");
            Assert(pluginList.Focus(), "The rendered reviewed-plugin list could not receive keyboard focus.");
            var enterInsidePluginList = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(pluginList)!,
                Environment.TickCount,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            pluginList.RaiseEvent(enterInsidePluginList);
            Assert(enterInsidePluginList.Handled &&
                   window.DataContext is WorkspaceShellViewModel { Preflight.IsBusy: false },
                "Enter inside the plugin list escaped to the default Review action.");

            var emptyPluginState = FindVisualDescendant<TextBlock>(window,
                control => string.Equals(AutomationProperties.GetName(control),
                    "Plugin list state", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The rendered plugin-list empty state is missing.");
            Assert(emptyPluginState.IsVisible &&
                   emptyPluginState.Text == "Review a copied workspace to see its plugins.",
                "The first-render plugin list does not explain its empty state.");

            var faceGenTab = FindVisualDescendant<TabItem>(window,
                item => string.Equals(AutomationProperties.GetName(item),
                    "Bake native FaceGen batch task", StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "The rendered native FaceGen task tab is missing.");
            var taskTabs = FindVisualDescendant<TabControl>(window,
                control => string.Equals(AutomationProperties.GetName(control),
                    "NPC Studio tasks", StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "The rendered NPC Studio task tabs are missing.");
            Assert(!faceGenTab.IsEnabled && taskTabs.SelectedIndex == 0,
                "Downstream NPC tasks were enabled before the copied workspace was reviewed.");

            var workspaceTab = window.FindName("NpcWorkspaceTab") as TabItem
                ?? throw new InvalidOperationException(
                    "The rendered integrated NPC workspace tab is missing.");
            workspaceTab.IsEnabled = true;
            taskTabs.SelectedItem = workspaceTab;
            window.UpdateLayout();
            window.Dispatcher.Invoke(
                () => { },
                DispatcherPriority.ApplicationIdle);
            string[] workbenchAutomationNames =
            [
                "NPC and leveled NPC record browser",
                "Main workspace search",
                "Main workspace actual preview",
                "Main workspace selected details",
                "Mark selected changed",
                "Reset selected drafts",
                "Mark selected for deletion",
                "Restore selected deletion",
                "Open Manager package for high fidelity preview",
                "Render selected NPC preview",
                "Edit selected NPC face",
                "Save selected package",
                "Dual-tone Hair / Accent Regions",
                "Export accepted preview scene NIF"
            ];
            foreach (string name in workbenchAutomationNames)
            {
                FrameworkElement? control =
                    FindVisualDescendant<FrameworkElement>(
                        window,
                        item => string.Equals(
                            AutomationProperties.GetName(item),
                            name,
                            StringComparison.Ordinal));
                Assert(
                    control is { IsLoaded: true, IsVisible: true },
                    $"The rendered workbench control '{name}' is missing or not visible.");
            }
            var workbenchPanel =
                FindVisualDescendant<SkyrimMainWorkspacePanel>(
                    window,
                    _ => true) ??
                throw new InvalidOperationException(
                    "The rendered integrated NPC workspace panel is missing.");
            FrameworkElement selectedDetails =
                FindVisualDescendant<FrameworkElement>(
                    workbenchPanel,
                    item => string.Equals(
                        AutomationProperties.GetName(item),
                        "Main workspace selected details",
                        StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
                    "The rendered exact-selection pane is missing.");
            Point detailsRightEdge = selectedDetails
                .TransformToAncestor(workbenchPanel)
                .Transform(
                    new Point(
                        selectedDetails.ActualWidth,
                        0));
            Assert(
                selectedDetails.ActualWidth > 0 &&
                detailsRightEdge.X <= workbenchPanel.ActualWidth + 1,
                "The rendered exact-selection pane extends beyond the visible " +
                "main-workspace viewport.");

            Button hairRegionsAction =
                FindVisualDescendant<Button>(
                    workbenchPanel,
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "Dual-tone Hair / Accent Regions",
                        StringComparison.Ordinal)) ??
                throw new InvalidOperationException(
                    "The rendered production workbench has no Dual-tone Hair action.");
            FaceGeomHairRegionsWizardWindow?
                productionHairRegionsWindow = null;
            var hairRegionsOpenFrame =
                new DispatcherFrame();
            _ = window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                () =>
                {
                    productionHairRegionsWindow =
                        app.Windows
                            .OfType<
                                FaceGeomHairRegionsWizardWindow>()
                            .SingleOrDefault();
                    productionHairRegionsWindow?.Close();
                    hairRegionsOpenFrame.Continue =
                        false;
                });
            hairRegionsAction.RaiseEvent(
                new RoutedEventArgs(
                    Button.ClickEvent));
            if (hairRegionsOpenFrame.Continue)
            {
                Dispatcher.PushFrame(
                    hairRegionsOpenFrame);
            }
            Assert(
                productionHairRegionsWindow is
                {
                    ResizeMode:
                        ResizeMode.CanResize
                } &&
                productionHairRegionsWindow.Owner ==
                    window &&
                productionHairRegionsWindow
                    .ViewModel.IsStandaloneSource &&
                string.IsNullOrWhiteSpace(
                    productionHairRegionsWindow
                        .ViewModel.SourcePath),
                "The real workbench action did not open the owner-modal standalone HairTint wizard through the production desktop composition.");

            taskTabs.SelectedIndex = 0;
            workspaceTab.IsEnabled = false;
            window.UpdateLayout();

            var lightingAction = FindVisualDescendant<Button>(window,
                button => string.Equals(
                    AutomationProperties.GetName(button),
                    "Open preview lighting editor",
                    StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "The rendered production shell has no preview-lighting action.");
            Assert(lightingAction.IsLoaded && lightingAction.IsVisible &&
                   lightingAction.ActualWidth > 0 && lightingAction.ActualHeight > 0,
                "The production preview-lighting action is not visibly arranged.");

            SkyrimLightingEditorWindow? productionLightingWindow = null;
            var productionOpenFrame = new DispatcherFrame();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                productionLightingWindow = app.Windows
                    .OfType<SkyrimLightingEditorWindow>()
                    .SingleOrDefault();
                productionLightingWindow?.Close();
                productionOpenFrame.Continue = false;
            });
            lightingAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (productionOpenFrame.Continue) Dispatcher.PushFrame(productionOpenFrame);
            Assert(productionLightingWindow is not null,
                "The rendered production lighting action did not open its owner-modal window.");

            using (var lightingViewModel = new SkyrimLightingEditorViewModel(
                       SkyrimLightingRules.DefaultPreset,
                       new RecordingLightingSettingsService()))
            {
                var lightingWindow = new SkyrimLightingEditorWindow(lightingViewModel)
                {
                    Owner = window
                };
                lightingWindow.Show();
                lightingWindow.UpdateLayout();
                var schematic = FindVisualDescendant<SkyrimLightingPreviewControl>(
                    lightingWindow,
                    _ => true)
                    ?? throw new InvalidOperationException(
                        "The rendered preview-lighting modal has no schematic control.");
                Assert(schematic.IsLoaded && schematic.IsVisible &&
                       schematic.ActualWidth > 0 && schematic.ActualHeight > 0 &&
                       PresentationSource.FromVisual(schematic) is not null,
                    "The preview-lighting schematic is not connected to the rendered presentation source.");
                lightingViewModel.AmbientText = "0.8";
                schematic.UpdateLayout();
                Assert(schematic.Preset.AmbientIntensity == 0.8f &&
                       lightingViewModel.PreviewRevision > 1,
                    "A real rendered modal edit did not reach the schematic dependency property.");
                lightingWindow.Close();
                Assert(!lightingWindow.IsVisible,
                    "The preview-lighting title close did not close the modal surface.");
            }

            window.Close();
            Assert(!window.IsVisible, "MainWindow remained visible after Close().");
            app.Shutdown();

            Console.WriteLine(
                $"PASS desktop modal progress auto-close + first-render smoke: ContentRendered + ApplicationIdle; automation='{automationPeer.GetName()}'.");
            window = null;
            app = null;
        }
        finally
        {
            if (window is not null)
            {
                window.Close();
            }

            if (app is not null && !app.Dispatcher.HasShutdownStarted)
            {
                app.Shutdown();
            }
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record ProgressDialogFixture(
        string Status,
        int ProgressPercent,
        string CancelActionContent,
        string CancelActionName);

    private sealed class CompletingFaceGenBatchService(
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

    private sealed class RefusingUnexpectedFaceGenBatchService : IFaceGenBakeAllService
    {
        public int CallCount { get; private set; }

        public ValueTask<FaceGenBakeAllResult> RunAsync(
            FaceGenBakeAllRequest request,
            IProgress<FaceGenBakeAllProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("A stale intake must not reach the batch service.");
        }
    }

    private sealed class StaleReviewedIntakeService(ReviewedGameIntake original) : IReviewedGameIntakeService
    {
        public ValueTask<ReviewedGameIntakeResult> ReviewAsync(
            ReviewedGameIntakeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stale = original with
            {
                IntakeFingerprint = new Sha256Hash(new string('0', 64))
            };
            return ValueTask.FromResult(new ReviewedGameIntakeResult(
                original.Edition, original.Plugins, stale, []));
        }
    }

    private sealed class StableReviewedIntakeService(ReviewedGameIntake original) : IReviewedGameIntakeService
    {
        public bool SawAbsentOutput { get; private set; }

        public ValueTask<ReviewedGameIntakeResult> ReviewAsync(
            ReviewedGameIntakeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SawAbsentOutput = request.OutputRoot == original.OutputRoot &&
                              !Directory.Exists(request.OutputRoot.Value) &&
                              !File.Exists(request.OutputRoot.Value);
            return ValueTask.FromResult(new ReviewedGameIntakeResult(
                original.Edition, original.Plugins, original, []));
        }
    }

    private sealed class ExistingOutputRequiredFaceGenBatchService : IFaceGenBakeAllService
    {
        public int CallCount { get; private set; }
        public bool SawExistingEmptyOutput { get; private set; }

        public ValueTask<FaceGenBakeAllResult> RunAsync(
            FaceGenBakeAllRequest request,
            IProgress<FaceGenBakeAllProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            SawExistingEmptyOutput = Directory.Exists(request.OutputDataRoot.Value) &&
                                     !Directory.EnumerateFileSystemEntries(
                                         request.OutputDataRoot.Value).Any();
            return ValueTask.FromResult(new FaceGenBakeAllResult(
                FaceGenBakeAllStatus.Succeeded, 0, 0, 0, 0, [], []));
        }
    }

    private sealed class BlockingFaceGenBatchService : IFaceGenBakeAllService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<FaceGenBakeAllResult> RunAsync(
            FaceGenBakeAllRequest request,
            IProgress<FaceGenBakeAllProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new FaceGenBakeAllProgress(
                1, FaceGenBakeAllProgressPhase.Discovering,
                0, 0, null, "Discovering."));
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.SetResult();
            }
            await Release.Task;
            progress?.Report(new FaceGenBakeAllProgress(
                2, FaceGenBakeAllProgressPhase.Cancelled,
                0, 0, null, "Cancelled."));
            return new FaceGenBakeAllResult(
                FaceGenBakeAllStatus.Cancelled, 0, 0, 0, 0, [], []);
        }
    }

    private sealed class DesktopPreflightService : INpcBuildPreflightService
    {
        public int VerifyCalls { get; private set; }

        public ValueTask<NpcBuildPreflightResult> CreateAsync(
            NpcBuildPreflightRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result(request, request.Output));

        public ValueTask<NpcBuildPreflightResult> VerifyReviewedAsync(
            NpcBuildPreflightRequest request,
            NpcBuildPreflightReviewAuthority reviewed,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;
            return ValueTask.FromResult(Result(request, reviewed.Path));
        }

        private static NpcBuildPreflightResult Result(
            NpcBuildPreflightRequest request,
            WorkspacePath? path)
        {
            var hash = new Sha256Hash(new string('b', 64));
            var artifact = new NpcBuildPreflightArtifact(
                NpcBuildPreflightSchemas.Artifact,
                "Actorwright", "test", "test", null,
                NpcBuildPreflightSchemas.DerivationVersion,
                request.SourceRequest,
                request.SourceRequestSha256,
                request.ExpectedPresetSha256,
                "Skyrim.esm|0x00013746",
                "female",
                [], [], [], [], [],
                [new NpcBuildPreflightGate(
                    "production", true, true, "accepted")],
                [new NpcBuildPreflightGate(
                    "preview", false, true, "accepted")],
                [], true, true, false);
            return new NpcBuildPreflightResult(
                true,
                true,
                new NpcBuildPreflightDocument(
                    artifact, [1], hash, path),
                []);
        }
    }

    private sealed class RefusingDesktopNpcPreviewComposer :
        INpcVisualPreviewComposer
    {
        public int Calls { get; private set; }

        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new NpcVisualPreviewComposeResult(
                false,
                null,
                [new Diagnostic(
                    "desktop-preview-fixture-refusal",
                    DiagnosticSeverity.Error,
                    "Synthetic preview refusal after verified build.")]));
        }
    }

    private sealed class CompletingRaceMenuNpcBuildService : IRaceMenuNpcBuildService
    {
        private static readonly Sha256Hash ArtifactHash = new(new string('a', 64));

        public RaceMenuNpcExecutionRequest? LastRequest { get; private set; }

        public ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync(
            RaceMenuNpcExecutionRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            progress?.Report(new BlankNpcBuildProgress(
                BlankNpcBuildStage.Verification, 92, "Verifying desktop completion fixture."));
            var root = request.Build.OutputRoot.Value;
            var data = Path.Combine(root, "Data");
            var formId = new FormId(0x00000800);
            if (request.Build.ExistingNpcTarget is { } existingTarget)
            {
                var sourceOwner = new PluginName(Path.GetFileName(
                    existingTarget.SourcePlugin.Value));
                var existingArtifact = new ExistingNpcAppearanceBuildArtifact(
                    "1",
                    "existing-npc-appearance-walking-product",
                    "STATIC_PASS_RUNTIME_REQUIRED",
                    request.Build.OutputRoot,
                    new WorkspacePath(Path.Combine(data, request.Build.OutputPlugin.Value)),
                    ArtifactHash,
                    sourceOwner,
                    existingTarget.TargetFormId,
                    new WorkspacePath(Path.Combine(data, "meshes", "actors", "character",
                        "FaceGenData", "FaceGeom", sourceOwner.Value, "00000800.nif")),
                    ArtifactHash,
                    new WorkspacePath(Path.Combine(data, "textures", "actors", "character",
                        "FaceGenData", "FaceTint", sourceOwner.Value, "00000800.dds")),
                    ArtifactHash,
                    new WorkspacePath(Path.Combine(root, "npcmanager-package.json")),
                    ArtifactHash,
                    RuntimeAuthority: false);
                var existingBodyFile = new BodyGenFileArtifact(
                    new AssetPath($"meshes/actors/character/BodyGenData/{sourceOwner.Value}/morphs.ini"),
                    new WorkspacePath(Path.Combine(data, "meshes", "actors", "character",
                        "BodyGenData", sourceOwner.Value, "morphs.ini")),
                    64,
                    ArtifactHash);
                var existingBodyGen = new BodyGenBuildResult(
                    true,
                    GameEdition.SkyrimSpecialEdition,
                    sourceOwner,
                    existingTarget.TargetFormId,
                    "NPCM_ExistingDesktopProof",
                    null,
                    [existingBodyFile],
                    []);
                return ValueTask.FromResult(new RaceMenuNpcExecutionResult(
                    true, null, null, null, null, existingBodyGen, null, [])
                {
                    ExistingNpcBuild = new ExistingNpcAppearanceBuildResult(
                        true, existingArtifact, null, existingBodyGen, null, null, null, [])
                });
            }
            var artifact = new BlankNpcBuildArtifact(
                "1", "walking-product", "STATIC_PASS_RUNTIME_REQUIRED",
                request.Build.OutputRoot,
                new WorkspacePath(Path.Combine(data, request.Build.OutputPlugin.Value)),
                ArtifactHash,
                formId,
                new WorkspacePath(Path.Combine(data, "meshes", "actors", "character",
                    "FaceGenData", "FaceGeom", request.Build.OutputPlugin.Value,
                    "00000800.nif")),
                ArtifactHash,
                new WorkspacePath(Path.Combine(data, "textures", "actors", "character",
                    "FaceGenData", "FaceTint", request.Build.OutputPlugin.Value,
                    "00000800.dds")),
                ArtifactHash,
                new WorkspacePath(Path.Combine(root, "npcmanager-package.json")),
                ArtifactHash,
                RuntimeAuthority: false);
            var bodyFile = new BodyGenFileArtifact(
                new AssetPath("meshes/actors/character/BodyGenData/morphs.ini"),
                new WorkspacePath(Path.Combine(data, "meshes", "actors", "character",
                    "BodyGenData", "morphs.ini")),
                64,
                ArtifactHash);
            var bodyGen = new BodyGenBuildResult(
                true, GameEdition.SkyrimSpecialEdition, request.Build.OutputPlugin,
                formId, "NPCM_EmiDesktopProof", null, [bodyFile], []);
            var build = new BlankNpcBuildResult(
                true, artifact, null, null, null, null, null, []);
            return ValueTask.FromResult(new RaceMenuNpcExecutionResult(
                true, null, null, null, null, bodyGen, build, []));
        }
    }

    private sealed class BlockingCancellationBuildService : IRaceMenuNpcBuildService
    {
        private readonly TaskCompletionSource<bool> releaseCancellation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync(
            RaceMenuNpcExecutionRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                await releaseCancellation.Task;
                throw;
            }

            throw new InvalidOperationException("The cancellation fixture completed unexpectedly.");
        }

        public void ReleaseCancellation() => releaseCancellation.TrySetResult(true);
    }

    private sealed class FixedRequestLoader(RaceMenuNpcExecutionRequest loadedRequest)
        : IRaceMenuNpcExecutionRequestFileLoader
    {
        public ValueTask<RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
            RaceMenuNpcExecutionRequestFileLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RaceMenuNpcExecutionRequestFileLoadResult(
                RaceMenuNpcExecutionRequestFileLoadStatus.Loaded,
                request.RequestFile,
                request.ExpectedSha256,
                request.ExpectedSha256,
                1,
                loadedRequest,
                []));
        }
    }

    private static void RunPresetLoaderViewModelTest()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var directory = new WorkspacePath(Path.Combine(labRoot.Value, "projects",
            "NpcManagerReimplementation", "01-source-copies", "m4-fixtures"));
        var target = new RaceMenuPresetTarget(
            "desktop-preset-loader-test",
            new FormReference(new PluginName("Skyrim.esm"), new FormId(0x0001_3746)),
            NpcSex.Female,
            directory,
            [new SkyrimFaceRecordPluginAuthority(
                new PluginName("Skyrim.esm"),
                new WorkspacePath(Path.Combine(directory.Value, "Skyrim.esm")),
                new Sha256Hash(new string('0', 64))) ]);
        var service = new FixedPresetCatalogService(directory);
        var previewService = new FixedPresetPreviewService(directory);
        var preparedSelection = new RaceMenuPresetSelection(
            new WorkspacePath(Path.Combine(directory.Value, "alpha.jslot")),
            new Sha256Hash(new string('a', 64)),
            true);
        using var viewModel = new RaceMenuPresetLoaderViewModel(
            service, directory, target, previewService, preparedSelection);

        viewModel.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(service.LoadCount == 1 && viewModel.VisibleEntries.Count == 3 &&
               viewModel.CompatibilityFilterAvailable && !viewModel.CanAccept,
            "The loader did not cache and expose the admitted catalog exactly once.");

        viewModel.SelectedEntry = viewModel.VisibleEntries[0];
        viewModel.WaitForPreviewAsync().GetAwaiter().GetResult();
        Assert(previewService.Requests is [{ } firstRequest] &&
               firstRequest.Preset == viewModel.VisibleEntries[0].SourcePath &&
               firstRequest.ExpectedPresetSha256 == viewModel.VisibleEntries[0].SourceSha256 &&
               viewModel.HasPreviewImage && !viewModel.IsPreviewBusy &&
               viewModel.PreviewStatus.Contains("Rendered 640", StringComparison.Ordinal) &&
               viewModel.PreviewAuthority.Contains("runtime authority false",
                   StringComparison.Ordinal),
            "The loader did not render and expose the exact hash-bound preview selection.");
        viewModel.ApplyBodySlide = false;
        viewModel.WaitForPreviewAsync().GetAwaiter().GetResult();
        RaceMenuPresetSelection selection = viewModel.AcceptSelection() ??
            throw new InvalidOperationException("The selected preset was not accepted.");
        Assert(!selection.ApplyBodySlide &&
               selection.SourcePath == viewModel.VisibleEntries[0].SourcePath &&
               selection.SourceSha256 == viewModel.VisibleEntries[0].SourceSha256 &&
               selection.Document is not null && selection.Companion is not null &&
               selection.Target == target &&
               previewService.Requests.Count == 2 &&
               viewModel.PreviewScope.Contains("omitted", StringComparison.Ordinal),
            "The loader did not retain the exact source path/hash, preview, and BodySlide choice.");

        viewModel.FilterText = "beta";
        Assert(viewModel.VisibleEntries is [{ DisplayName: "beta" }] &&
               viewModel.SelectedEntry is null && !viewModel.CanAccept &&
               !viewModel.HasPreviewImage && service.LoadCount == 1,
            "Filename filtering reparsed the catalog or retained a stale hidden selection.");
        viewModel.SelectedEntry = viewModel.VisibleEntries[0];
        viewModel.WaitForPreviewAsync().GetAwaiter().GetResult();
        Assert(viewModel.HasPreviewImage && !viewModel.CanAccept &&
               viewModel.SelectionHelp.Contains("not compatible", StringComparison.Ordinal),
            "An incompatible preset was not previewable or was incorrectly enabled for building.");
        viewModel.FilterText = "gamma";
        viewModel.SelectedEntry = viewModel.VisibleEntries[0];
        viewModel.WaitForPreviewAsync().GetAwaiter().GetResult();
        RaceMenuPresetSelection generatedSelection = viewModel.AcceptSelection() ??
            throw new InvalidOperationException(
                "A compatible unprepared preset was not admitted for an authority transaction.");
        Assert(viewModel.CanAccept && generatedSelection.Document is not null &&
               generatedSelection.Companion is not null &&
               generatedSelection.Target == target &&
               viewModel.SelectionHelp.Contains("generate and reopen",
                   StringComparison.Ordinal),
            "A compatible unprepared preset did not retain the typed transaction inputs.");
        viewModel.FilterText = string.Empty;
        viewModel.CompatibleOnly = true;
        Assert(viewModel.VisibleEntries.Select(item => item.DisplayName)
                   .SequenceEqual(["alpha", "gamma"]) &&
               service.LoadCount == 1,
            "The provider-backed compatible-only filter did not use the cached verdicts.");
        Console.WriteLine("PASS RaceMenu preset loader filters cached authority, previews, and retains exact selection.");
    }

    private sealed class EmptyPresetCatalogService : IRaceMenuPresetCatalogService
    {
        public static EmptyPresetCatalogService Instance { get; } = new();

        public ValueTask<RaceMenuPresetCatalogResult> LoadAsync(
            RaceMenuPresetCatalogRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RaceMenuPresetCatalogResult([], []));
        }
    }

    private sealed class FixedPresetCatalogService(WorkspacePath directory)
        : IRaceMenuPresetCatalogService
    {
        public int LoadCount { get; private set; }

        public ValueTask<RaceMenuPresetCatalogResult> LoadAsync(
            RaceMenuPresetCatalogRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            Assert(request.PresetDirectory == directory && request.Target is not null,
                "The loader changed the typed directory or target authority.");
            return ValueTask.FromResult(new RaceMenuPresetCatalogResult(
                [
                     Entry("alpha", 'a', RaceMenuPresetCompatibilityKind.Compatible),
                     Entry("beta", 'b', RaceMenuPresetCompatibilityKind.Incompatible),
                     Entry("gamma", 'f', RaceMenuPresetCompatibilityKind.Compatible)
                ],
                []));
        }

        private RaceMenuPresetCatalogEntry Entry(
            string name,
            char hashCharacter,
            RaceMenuPresetCompatibilityKind compatibility)
        {
            var hash = new Sha256Hash(new string(hashCharacter, 64));
            var appearance = new PresetAppearance(
                null, [], null, null,
                ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty,
                ImmutableDictionary<string, float>.Empty,
                [], [], [], null,
                new PresetFieldPresence(false, false, false, false, false,
                    false, false, false, false),
                []);
            var document = new PresetDocument(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                appearance,
                hash,
                []);
            return new RaceMenuPresetCatalogEntry(
                name,
                new WorkspacePath(Path.Combine(directory.Value, $"{name}.jslot")),
                hash,
                document,
                new RaceMenuPresetSummary(4, 2, 1, 3, 2, 1, 42, 2, 1, 1, 0),
                compatibility,
                []);
        }
    }

    private sealed class FixedPresetPreviewService(WorkspacePath directory)
        : IRaceMenuPresetPreviewService
    {
        public List<RaceMenuPresetPreviewRequest> Requests { get; } = [];

        public ValueTask<RaceMenuPresetPreviewResult> RenderAsync(
            RaceMenuPresetPreviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var faceGeom = new WorkspacePath(Path.ChangeExtension(request.Preset.Value, ".nif"));
            var faceTint = new WorkspacePath(Path.ChangeExtension(request.Preset.Value, ".dds"));
            var companion = new RaceMenuPresetCompanionExport(
                request.Preset,
                request.ExpectedPresetSha256,
                faceGeom,
                new Sha256Hash(new string('c', 64)),
                faceTint,
                new Sha256Hash(new string('d', 64)));
            var image = new PreviewRenderedImage(
                Path.Combine(directory.Value, "preview.png"),
                new string('e', 64),
                request.Width,
                request.Height,
                1,
                Edition: "skyrimse",
                DeformationMode: "desktop-preview-fixture");
            return ValueTask.FromResult(new RaceMenuPresetPreviewResult(
                true,
                companion,
                image,
                []));
        }
    }

    private sealed class ControlledHeadPartPreviewService : ISkyrimHeadPartPreviewService
    {
        private readonly List<TaskCompletionSource<SkyrimHeadPartPreviewResult>> completions = [];

        public List<SkyrimHeadPartPreviewRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<SkyrimHeadPartPreviewResult> RenderAsync(
            SkyrimHeadPartPreviewRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            var completion = new TaskCompletionSource<SkyrimHeadPartPreviewResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completions.Add(completion);
            return new ValueTask<SkyrimHeadPartPreviewResult>(
                completion.Task.WaitAsync(cancellationToken));
        }

        public void Complete(int index, SkyrimHeadPartPreviewResult result) =>
            completions[index].TrySetResult(result);
    }

    private sealed class ControlledMeshPreviewService : ISkyrimMeshPreviewService
    {
        private readonly List<TaskCompletionSource<SkyrimMeshPreviewResult>> completions = [];

        public List<SkyrimMeshPreviewRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<SkyrimMeshPreviewResult> RenderAsync(
            SkyrimMeshPreviewRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            var completion = new TaskCompletionSource<SkyrimMeshPreviewResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completions.Add(completion);
            return new ValueTask<SkyrimMeshPreviewResult>(
                completion.Task.WaitAsync(cancellationToken));
        }

        public void Complete(int index, SkyrimMeshPreviewResult result) =>
            completions[index].TrySetResult(result);
    }

    private sealed class RecordingPreviewImageRenderer : IPreviewImageRenderer
    {
        public int CallCount { get; private set; }

        public ValueTask<PreviewImageRenderResult> RenderAsync(
            PreviewImageRenderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var png = new byte[24];
            byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
            signature.CopyTo(png, 0);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(8, 4), 13);
            "IHDR"u8.CopyTo(png.AsSpan(12, 4));
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), request.Width);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), request.Height);
            File.WriteAllBytes(request.OutputPath.Value, png);
            string hash = Convert.ToHexString(SHA256.HashData(png));
            return ValueTask.FromResult(new PreviewImageRenderResult(
                true,
                new PreviewRenderedImage(
                    request.OutputPath.Value,
                    hash,
                    request.Width,
                    request.Height,
                    request.Assets.Length,
                    Edition: request.Edition.ToWireName(),
                    DeformationMode: "test-headpart-preview"),
                []));
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
