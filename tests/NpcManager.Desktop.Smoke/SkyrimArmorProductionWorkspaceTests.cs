using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimArmorProductionWorkspaceViewModelTest() =>
        RunSkyrimArmorProductionWorkspaceViewModelTestAsync()
            .GetAwaiter().GetResult();

    private static async Task RunSkyrimArmorProductionWorkspaceViewModelTestAsync()
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work",
            $"armor-production-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PluginName sourcePlugin = new("Source.esp");
            WorkspacePath sourcePath = new(Path.Combine(root, sourcePlugin.Value));
            await File.WriteAllTextAsync(sourcePath.Value, "reviewed-source");
            Sha256Hash sourceHash = HashArmorDesktopFile(sourcePath.Value);
            ReviewedGameIntake intake = CreateArmorDesktopIntake(
                labRoot,
                root,
                sourcePlugin,
                sourcePath,
                sourceHash);
            SkyrimArmorProductionSource source = CreateArmorDesktopSource(
                intake,
                sourcePlugin,
                sourcePath,
                sourceHash);
            var reader = new FakeArmorProductionReader(source);
            var transaction = new FakeArmorProductionTransactionService();
            var formChoices = new FakeArmorFormChoiceService(
                CreateArmorReferenceCatalog(sourcePlugin));
            var meshChoices = new FakeArmorMeshChoiceService(
                CreateArmorMeshCatalog(root));
            var pickedMeshFields = new List<SkyrimMeshTargetField>();
            var meshInitialPaths = new List<string?>();
            bool cancelNextMesh = false;
            SkyrimMeshPickerSelection OpenMeshPicker(
                SkyrimMeshTargetField field,
                SkyrimMeshPickerCatalogResult catalog)
            {
                pickedMeshFields.Add(field);
                meshInitialPaths.Add(catalog.InitialCandidate?.RelativePath.Value);
                if (cancelNextMesh)
                {
                    cancelNextMesh = false;
                    return SkyrimMeshPickerRules.Cancel();
                }
                string selected = field is SkyrimMeshTargetField.ArmorMaleWorld or
                    SkyrimMeshTargetField.ArmorFemaleWorld
                    ? "armor/fixture/ground.nif"
                    : "armor/fixture/body.nif";
                return SkyrimMeshPickerRules.Use(catalog.Candidates.Single(item =>
                    item.RelativePath.Value == selected));
            }
            using var viewModel = new SkyrimArmorProductionWorkspaceViewModel(
                reader,
                transaction,
                labRoot,
                formChoices,
                meshChoices,
                OpenMeshPicker);
            viewModel.ApplyReviewedIntake(intake);
            viewModel.SourcePlugin = sourcePlugin.Value;
            viewModel.SourceFormId = "0x00000A00";
            viewModel.NewTargetFormId = "0x00000B00";
            viewModel.ProposalPath = Path.Combine(
                root,
                "GeneratedArmor.armor-proposal.json");
            viewModel.OutputPlugin = Path.Combine(root, "GeneratedArmor.esp");
            viewModel.NewArmorAddonTargetFormId = "0x00000C00";
            viewModel.ArmorAddonOutputPlugin = Path.Combine(
                root,
                "GeneratedAddonOverride.esp");

            await viewModel.LoadAsync();
            Assert(viewModel.IsLoaded && viewModel.CanOpenEditor &&
                   reader.ReadCalls == 1 &&
                   formChoices.SearchCalls == 1 &&
                   meshChoices.SearchCalls == 1 &&
                   meshChoices.LastRequest is
                   {
                       Edition: GameEdition.SkyrimSpecialEdition,
                       Kind: AssetChoiceKind.Mesh,
                       Search: null
                   } &&
                   meshChoices.LastRequest.DataRoot == intake.DataRoot &&
                   formChoices.LastRequest?.AllowedSignatures.Select(item =>
                       item.Value).SequenceEqual(
                       ["RACE", "TXST", "FLST", "FSTS", "ARTO"]) == true &&
                   viewModel.SourceSummary.Contains("Source.esp|0x00000A00",
                       StringComparison.Ordinal),
                "The Armor production owner did not load its explicit reviewed source and closed typed reference catalog.");

            SkyrimArmorAddonProductionCatalogEntry editableCatalogEntry =
                source.ArmorAddonCatalog.Single(item =>
                    item.EditableDocument is not null);
            SkyrimArmorAddonReferenceRow editableRow =
                SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    source.NewFromTemplate.Race,
                    editableCatalogEntry.Candidate).Row ??
                throw new InvalidOperationException(
                    "The fake production catalog lost its editable ARMA row.");
            SkyrimArmorAddonEditorViewModel deepEditor =
                viewModel.CreateArmorAddonEditor(
                    source.NewFromTemplate.Race,
                    editableRow);
            Assert(deepEditor.HasTypedReferenceCatalog &&
                   deepEditor.CreateReferencePicker(
                       SkyrimArmorAddonReferenceField.PrimaryRace)
                       .AllowedSignatures == "RACE" &&
                   deepEditor.TrySwitchIntent(
                       SkyrimArmorAddonEditorIntent.BlankNew) &&
                   deepEditor.Identity.Mode == ArmorAddonProposalMode.New &&
                   deepEditor.Identity.TargetPlugin ==
                       new PluginName("GeneratedAddonOverride.esp") &&
                   deepEditor.Identity.TargetFormId == new FormId(0xC00) &&
                   deepEditor.TrySwitchIntent(
                       SkyrimArmorAddonEditorIntent.NewFromTemplate) &&
                   deepEditor.Identity.SeedFromSource &&
                   deepEditor.TrySwitchIntent(
                       SkyrimArmorAddonEditorIntent.OverrideExisting) &&
                   deepEditor.Identity.Mode == ArmorAddonProposalMode.Override,
                "The production Gate 020 boundary did not expose exact blank, template, and override intent identities.");
            Assert(deepEditor.TryPickModel(
                       SkyrimMeshTargetField.ArmorAddonMaleThirdPerson) &&
                   deepEditor.Models.MaleModel == "armor\\fixture\\body.nif" &&
                   meshInitialPaths[^1] is null &&
                   deepEditor.TryPickModel(
                       SkyrimMeshTargetField.ArmorAddonMaleThirdPerson) &&
                   meshInitialPaths[^1] == "armor/fixture/body.nif",
                "The production Gate 021 modal did not store a Skyrim-style prefix-free ARMA mesh or exactly preselect it on reopen.");
            deepEditor.Slots.Load(0x40);
            Assert(deepEditor.CanSave && deepEditor.TrySave() &&
                   deepEditor.AcceptedRow?.AuthoredProposal is { } nestedProposal &&
                   nestedProposal.OutputProposal.IsUnder(labRoot) &&
                   !File.Exists(nestedProposal.OutputProposal.Value),
                "The production Gate 020 child did not stay I/O-free or return one K-local nested proposal.");
            SkyrimArmorEditorViewModel parentWithAuthoredAddon =
                viewModel.CreateEditor((_, _) => deepEditor.AcceptedRow);
            parentWithAuthoredAddon.Collections.SelectedArmorAddonIndex = 0;
            Assert(parentWithAuthoredAddon.TryReplaceArmorAddon() &&
                   parentWithAuthoredAddon.TryRecalculateSlots() &&
                   parentWithAuthoredAddon.Core.SlotMaskText == "0x00000040",
                "Parent ARMO slot recalculation ignored the accepted Gate 020 complete child document.");
            SkyrimArmorAddonReferenceEditorViewModel referenceEditor =
                viewModel.CreateArmorAddonReferenceEditor(
                    source.NewFromTemplate,
                    0,
                    _ => new SkyrimArmorAddonDeepEditResult(
                        true,
                        deepEditor.AcceptedRow));
            Assert(referenceEditor.CurrentReference ==
                       source.NewFromTemplate.ArmorAddons.Single().ToString() &&
                   referenceEditor.Candidates.Count == 2 &&
                   referenceEditor.TryDeepEdit() &&
                   referenceEditor.WasAutoAccepted &&
                   referenceEditor.AcceptedRow?.AuthoredProposal ==
                       deepEditor.AcceptedRow?.AuthoredProposal,
                "The production Gate 019 modal did not open the exact row and auto-accept its committed Gate 020 child.");
            SkyrimArmorAddonProductionCatalogEntry selectionOnlyEntry =
                source.ArmorAddonCatalog.Single(item =>
                    item.EditableDocument is null);
            SkyrimArmorAddonReferenceRow selectionOnlyRow =
                SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    source.NewFromTemplate.Race,
                    selectionOnlyEntry.Candidate).Row ??
                throw new InvalidOperationException(
                    "The fake catalog lost its selection-only ARMA row.");
            bool unsafeDeepEditRefused = false;
            try
            {
                _ = viewModel.CreateArmorAddonEditor(
                    source.NewFromTemplate.Race,
                    selectionOnlyRow);
            }
            catch (InvalidOperationException exception)
            {
                unsafeDeepEditRefused = exception.Message.Contains(
                    "cannot be deep edited safely",
                    StringComparison.Ordinal);
            }
            Assert(unsafeDeepEditRefused,
                "The production Gate 020 boundary deep-edited a selection-only winning-provider ARMA.");

            SkyrimArmorEditorViewModel first = viewModel.CreateEditor();
            Assert(first.Identity.Intent == SkyrimArmorEditorIntent.NewFromTemplate &&
                   first.TrySwitchIntent(SkyrimArmorEditorIntent.BlankNew) &&
                   first.Identity.Intent == SkyrimArmorEditorIntent.BlankNew &&
                   first.TrySwitchIntent(SkyrimArmorEditorIntent.OverrideExisting) &&
                   first.Identity.Intent == SkyrimArmorEditorIntent.OverrideExisting &&
                   !first.TrySwitchIntent(SkyrimArmorEditorIntent.EditAuthored) &&
                   first.Identity.Intent == SkyrimArmorEditorIntent.OverrideExisting,
                "The production owner did not expose exact blank/template/override and unavailable edit-authored intent semantics.");
            Assert(first.TryPickWorldModel(
                       SkyrimMeshTargetField.ArmorMaleWorld) &&
                   first.References.MaleWorldModel ==
                       "armor\\fixture\\ground.nif",
                "The production Gate 021 modal did not store a Skyrim-style prefix-free ARMO world mesh.");
            cancelNextMesh = true;
            string femaleBeforeCancel = first.References.FemaleWorldModel;
            Assert(!first.TryPickWorldModel(
                       SkyrimMeshTargetField.ArmorFemaleWorld) &&
                   first.References.FemaleWorldModel == femaleBeforeCancel &&
                   pickedMeshFields.Contains(
                       SkyrimMeshTargetField.ArmorAddonMaleThirdPerson) &&
                   pickedMeshFields.Contains(
                       SkyrimMeshTargetField.ArmorMaleWorld) &&
                   pickedMeshFields.Contains(
                       SkyrimMeshTargetField.ArmorFemaleWorld),
                "Cancelling the production Gate 021 modal mutated the ARMO field or lost caller identity.");
            first.Cancel();
            viewModel.ReportEditorCancelled();
            Assert(!viewModel.HasAcceptedDocument,
                "A cancelled Armor modal leaked a production document.");

            int armorAddonPickerCalls = 0;
            var pickedAddon = new SkyrimArmorAddonReferenceRow(
                new FormReference(sourcePlugin, new FormId(0x908)),
                "Reviewed second addon",
                new SkyrimArmorAddonRaceEvidence(
                    true,
                    source.NewFromTemplate.Race,
                    source.NewFromTemplate.Race,
                    []));
            SkyrimArmorEditorViewModel modalPickerEditor =
                viewModel.CreateEditor((document, index) =>
                {
                    armorAddonPickerCalls++;
                    Assert(index is null &&
                           document.Race == source.NewFromTemplate.Race,
                        "The Add ARMA picker did not receive the current complete Armor document.");
                    return pickedAddon;
                });
            Assert(modalPickerEditor.TryAddArmorAddon() &&
                   armorAddonPickerCalls == 1 &&
                   modalPickerEditor.Collections.ArmorAddons
                       .SequenceEqual([
                           source.NewFromTemplate.ArmorAddons.Single().ToString(),
                           pickedAddon.Reference.ToString()
                       ]),
                "The production Armor editor did not consume one accepted modal ARMA row.");
            modalPickerEditor.Cancel();

            SkyrimArmorEditorViewModel acceptedEditor = viewModel.CreateEditor();
            acceptedEditor.Core.Name = "Generated travel armor";
            Assert(!acceptedEditor.TryPreview() &&
                   acceptedEditor.StatusMessage.Contains("unavailable",
                       StringComparison.OrdinalIgnoreCase) &&
                   acceptedEditor.TrySave() &&
                   viewModel.ApplyEditor(acceptedEditor) &&
                   viewModel.HasAcceptedDocument &&
                   viewModel.DocumentSummary.Contains("npcm_ARMO_SourceArmor",
                       StringComparison.Ordinal),
                "The production owner swallowed preview failure or lost the accepted immutable document.");

            SkyrimArmorEditorViewModel editAuthored = viewModel.CreateEditor();
            Assert(editAuthored.Identity.Intent == SkyrimArmorEditorIntent.EditAuthored &&
                   editAuthored.TrySwitchIntent(SkyrimArmorEditorIntent.NewFromTemplate) &&
                   editAuthored.TrySwitchIntent(SkyrimArmorEditorIntent.EditAuthored) &&
                   editAuthored.Identity.Intent == SkyrimArmorEditorIntent.EditAuthored,
                "The accepted ARMO could not reopen through Edit authored intent.");
            editAuthored.Cancel();
            viewModel.ReportEditorCancelled();
            Assert(viewModel.HasAcceptedDocument,
                "Cancelling a later Armor edit erased the previously accepted document.");

            SkyrimArmorAddonEditorViewModel reopenedAddon =
                viewModel.CreateArmorAddonEditor(
                    source.NewFromTemplate.Race,
                    deepEditor.AcceptedRow!);
            Assert(reopenedAddon.Identity.Intent ==
                       SkyrimArmorAddonEditorIntent.EditAuthored &&
                   reopenedAddon.Models.MaleModel ==
                        "armor\\fixture\\body.nif" &&
                   reopenedAddon.TrySwitchIntent(
                       SkyrimArmorAddonEditorIntent.EditAuthored) &&
                   reopenedAddon.TrySave() &&
                   reopenedAddon.AcceptedRow?.AuthoredProposal is not null,
                "The production Gate 020 authored override did not reopen and save with stable identity.");
            SkyrimArmorEditorViewModel nestedParent = viewModel.CreateEditor(
                (_, index) => index == 0
                    ? reopenedAddon.AcceptedRow
                    : null);
            nestedParent.Collections.SelectedArmorAddonIndex = 0;
            Assert(nestedParent.TryReplaceArmorAddon() &&
                   nestedParent.TrySave() &&
                   nestedParent.AcceptedDocument?.AuthoredArmorAddons.Length == 1 &&
                   viewModel.ApplyEditor(nestedParent),
                "The accepted Gate 020 result did not remain inside one complete parent ARMO document.");

            var sourceItem = new SkyrimOutfitEditorItem(
                source.SourceReference,
                SkyrimOutfitEditorItemKind.Armor,
                "Source armor",
                0x04);
            SkyrimOutfitEditorItem? createdChild = null;
            var outfit = new SkyrimOutfitEditorViewModel(
                [],
                [sourceItem],
                new WorkspacePath(root),
                sourcePath,
                new FormId(0xA00),
                new FormId(0xC00),
                new WorkspacePath(Path.Combine(
                    root,
                    "CancelledOutfit.outfit-proposal.json")),
                (_, _) => [],
                (kind, basis) =>
                {
                    if (kind != SkyrimOutfitEditorItemKind.Armor || basis is null)
                        return null;
                    SkyrimArmorEditorViewModel childEditor =
                        viewModel.CreateOutfitChildEditor(basis);
                    if (!childEditor.TrySave()) return null;
                    createdChild = viewModel.CreateOutfitChildItem(
                        childEditor,
                        basis);
                    return createdChild;
                });
            Assert(outfit.TryBeginNew(),
                "Could not begin the real outer outfit transaction.");
            outfit.SelectedAvailableItem = outfit.AvailableItems.Single();
            Assert(outfit.TryCreateChildItem(SkyrimOutfitEditorItemKind.Armor) &&
                   createdChild?.AuthoredArmor is not null &&
                   outfit.DraftItems.Single().Item == createdChild,
                "The real outfit child boundary did not retain the explicitly selected authored ARMO.");
            outfit.Cancel();
            Assert(outfit.DraftItems.Count == 0 && outfit.AcceptedResult is null,
                "Outer outfit Cancel leaked the authored ARMO child document.");

            await viewModel.ReviewAsync();
            Assert(viewModel.IsReviewed && transaction.AnalyzeCalls == 1 &&
                   transaction.LastAnalyzeRequest?.ArmorAddonOutputs.Length == 1 &&
                   File.Exists(viewModel.ProposalPath) &&
                   File.Exists(reopenedAddon.AcceptedRow!.AuthoredProposal!
                       .OutputProposal.Value) &&
                   !File.Exists(viewModel.OutputPlugin),
                "Combined ARMA/ARMO Review did not remain proposal-only.");
            await viewModel.ExecuteAsync();
            Assert(viewModel.HasCompleted && transaction.ApplyCalls == 1 &&
                   transaction.VerifyCalls == 1 &&
                   viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
                   File.Exists(viewModel.OutputPlugin) &&
                   File.Exists(viewModel.ArmorAddonOutputPlugin) &&
                   viewModel.ArmorAddonResultPlugin ==
                       viewModel.ArmorAddonOutputPlugin &&
                   viewModel.ArmorAddonResultSha256 ==
                       HashArmorDesktopFile(
                           viewModel.ArmorAddonOutputPlugin).Value &&
                   viewModel.ResultSha256 ==
                       HashArmorDesktopFile(viewModel.OutputPlugin).Value,
                "Combined Apply did not write and explicitly verify child ARMA plus parent ARMO.");

            string panel = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "SkyrimArmorProductionPanel.xaml"));
            string shell = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "MainWindow.xaml"));
            string editor = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "SkyrimArmorEditorWindow.xaml"));
            string coordinator = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop",
                "SkyrimArmorProductionDialogCoordinator.cs"));
            string composition = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop",
                "SkyrimArmorProductionDesktopComposition.cs"));
            Assert(panel.Contains(
                       "AutomationProperties.Name=\"Load reviewed armor source\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Open Skyrim armor editor\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Review armor proposal\"",
                       StringComparison.Ordinal) &&
                    panel.Contains(
                        "AutomationProperties.Name=\"Write and verify armor output\"",
                        StringComparison.Ordinal) &&
                    panel.Contains(
                        "AutomationProperties.Name=\"New armor-addon local FormID\"",
                        StringComparison.Ordinal) &&
                    panel.Contains(
                        "AutomationProperties.Name=\"Armor-addon output plugin\"",
                        StringComparison.Ordinal) &&
                   panel.Contains("runtime, or visual proof",
                       StringComparison.Ordinal) &&
                   shell.Contains("Header=\"Armor\"", StringComparison.Ordinal) &&
                   editor.Contains("x:Name=\"CollectionsScrollViewer\"",
                       StringComparison.Ordinal) &&
                   editor.Split("MinHeight=\"96\"",
                       StringSplitOptions.None).Length >= 3 &&
                   coordinator.Contains(
                       "new SkyrimArmorAddonReferenceEditorWindow",
                       StringComparison.Ordinal) &&
                   coordinator.Contains(
                       "new SkyrimArmorAddonEditorWindow",
                       StringComparison.Ordinal) &&
                   coordinator.Contains(
                       "CreateArmorAddonReferenceEditor",
                       StringComparison.Ordinal) &&
                   composition.Contains(
                       "new FormChoiceService",
                       StringComparison.Ordinal) &&
                   composition.Contains(
                       "new AssetChoiceService",
                       StringComparison.Ordinal) &&
                   composition.Contains(
                       "new SkyrimMeshPreviewService",
                       StringComparison.Ordinal) &&
                   composition.Contains(
                       "new BlenderPreviewImageRenderer",
                       StringComparison.Ordinal),
                "The Armor production panel, shell, modal chain, or authority marker is missing.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine(
            "PASS production Armor workspace proves reviewed source, four intents, modal rollback, proposal-only review, write, and second readback.");
    }

    private static ReviewedGameIntake CreateArmorDesktopIntake(
        WorkspacePath labRoot,
        string root,
        PluginName plugin,
        WorkspacePath path,
        Sha256Hash hash) => new(
        GameEdition.SkyrimSpecialEdition,
        labRoot,
        new WorkspacePath(root),
        new WorkspacePath(Path.Combine(root, "load-order.json")),
        new WorkspacePath(Path.Combine(root, "future-output")),
        new Sha256Hash(new string('7', 64)),
        [new PluginClosureReviewEntry(
            plugin, 0, true, true, true, true, true, path, hash, [])],
        [], [], [], 0,
        new Sha256Hash(new string('8', 64)),
        new Sha256Hash(new string('9', 64)),
        false);

    private static SkyrimArmorProductionSource CreateArmorDesktopSource(
        ReviewedGameIntake intake,
        PluginName plugin,
        WorkspacePath path,
        Sha256Hash hash)
    {
        FormReference race = new(plugin, new FormId(0x900));
        FormReference addon = new(plugin, new FormId(0x907));
        FormReference keyword = new(plugin, new FormId(0x906));
        var template = new SkyrimArmorEditorDocument(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorEditorIntent.NewFromTemplate,
            ArmorProposalMode.New,
            path,
            new FormId(0xA00),
            new EditorId("npcm_ARMO_SourceArmor"),
            new FormId(0xB00),
            "Source armor",
            race,
            null,
            false,
            "Source description",
            10,
            2,
            5,
            0x04,
            null,
            null,
            null,
            null,
            new ArmorObjectBounds(-1, -1, -1, 1, 1, 1),
            null,
            null,
            null,
            [addon],
            [keyword]);
        var addonDocument = new SkyrimArmorAddonEditorDocument(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorAddonEditorIntent.OverrideExisting,
            ArmorAddonProposalMode.Override,
            path,
            addon.FormId,
            new EditorId("SourceAddon"),
            null,
            null,
            false,
            "armor\\source_addon_m.nif",
            null,
            null,
            null,
            0x04,
            race,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            false,
            false,
            0,
            0D);
        SkyrimArmorAddonRaceEvidence compatibility =
            new(true, race, race, []);
        ImmutableArray<SkyrimArmorAddonProductionCatalogEntry> catalog =
        [
            new SkyrimArmorAddonProductionCatalogEntry(
                new SkyrimArmorAddonReferenceCandidate(
                    addon,
                    new RecordSignature("ARMA"),
                    "SourceAddon",
                    false,
                    false,
                    compatibility),
                plugin,
                addonDocument),
            new SkyrimArmorAddonProductionCatalogEntry(
                new SkyrimArmorAddonReferenceCandidate(
                    new FormReference(plugin, new FormId(0x908)),
                    new RecordSignature("ARMA"),
                    "SecondAddon",
                    false,
                    false,
                    compatibility),
                plugin,
                null)
        ];
        return new SkyrimArmorProductionSource(
            intake,
            [plugin],
            path,
            hash,
            new FormReference(plugin, new FormId(0xA00)),
            template with
            {
                Intent = SkyrimArmorEditorIntent.BlankNew,
                Name = string.Empty,
                Description = string.Empty,
                Value = 0,
                Weight = 0,
                ArmorRating = 0,
                SlotMask = 0,
                ArmorAddons = [],
                Keywords = []
            },
            template,
            template with
            {
                Intent = SkyrimArmorEditorIntent.OverrideExisting,
                Mode = ArmorProposalMode.Override,
                EditorId = new EditorId("SourceArmor"),
                TargetFormId = null
            },
            [new EditorId("SourceArmor")],
            [new SkyrimArmorAddonSlotEvidence(addon, 0x04)],
            catalog,
            [new EditorId("SecondAddon"), new EditorId("SourceAddon")]);
    }

    private static Sha256Hash HashArmorDesktopFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static FormChoiceSearchResult CreateArmorReferenceCatalog(
        PluginName plugin)
    {
        var provenance = new FormChoiceProvenance(
            FormChoiceProvenanceKind.Base,
            plugin,
            [plugin]);
        return new FormChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition,
            true,
            [
                new FormChoiceCandidate(
                    plugin, new FormId(0x900), new RecordSignature("RACE"),
                    "SourceRace", null, false, provenance),
                new FormChoiceCandidate(
                    plugin, new FormId(0x910), new RecordSignature("TXST"),
                    "SourceTexture", null, false, provenance),
                new FormChoiceCandidate(
                    plugin, new FormId(0x911), new RecordSignature("FLST"),
                    "SourceSwaps", null, false, provenance),
                new FormChoiceCandidate(
                    plugin, new FormId(0x912), new RecordSignature("FSTS"),
                    "SourceFootsteps", null, false, provenance),
                new FormChoiceCandidate(
                    plugin, new FormId(0x913), new RecordSignature("ARTO"),
                    "SourceArt", null, false, provenance)
            ],
            []);
    }

    private static AssetChoiceSearchResult CreateArmorMeshCatalog(string root)
    {
        AssetChoiceCandidate Candidate(string relative, char hashCharacter) =>
            new(
                AssetChoiceKind.Mesh,
                new AssetPath("meshes/" + relative),
                new AssetChoiceProvider(
                    AssetChoiceProviderStatus.Resolved,
                    [new AssetChoiceProviderEvidence(
                        AssetProviderKind.Loose,
                        Path.Combine(root, "meshes", relative.Replace(
                            '/', Path.DirectorySeparatorChar)),
                        4,
                        new Sha256Hash(new string(hashCharacter, 64)))]),
                null,
                null,
                null,
                null,
                null,
                null);
        return new AssetChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition,
            AssetChoiceKind.Mesh,
            [
                Candidate("armor/fixture/body.nif", 'A'),
                Candidate("armor/fixture/ground.nif", 'B')
            ],
            []);
    }

    private sealed class FakeArmorProductionReader(
        SkyrimArmorProductionSource source) : ISkyrimArmorProductionReader
    {
        public int ReadCalls { get; private set; }

        public SkyrimArmorProductionReadResult Read(
            SkyrimArmorProductionReadRequest request)
        {
            ReadCalls++;
            return new SkyrimArmorProductionReadResult(true, source, []);
        }
    }

    private sealed class FakeArmorFormChoiceService(
        FormChoiceSearchResult result) : IFormChoiceService
    {
        public int SearchCalls { get; private set; }
        public FormChoiceSearchRequest? LastRequest { get; private set; }

        public ValueTask<FormChoiceSearchResult> SearchAsync(
            FormChoiceSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls++;
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorMeshChoiceService(
        AssetChoiceSearchResult result) : IAssetChoiceService
    {
        public int SearchCalls { get; private set; }
        public AssetChoiceSearchRequest? LastRequest { get; private set; }

        public ValueTask<AssetChoiceSearchResult> SearchAsync(
            AssetChoiceSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls++;
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeArmorProductionTransactionService :
        ISkyrimArmorProductionTransactionService
    {
        public int AnalyzeCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public SkyrimArmorProductionRequest? LastAnalyzeRequest { get; private set; }

        public async ValueTask<SkyrimArmorProductionProposal> AnalyzeAsync(
            SkyrimArmorProductionRequest request,
            CancellationToken cancellationToken)
        {
            AnalyzeCalls++;
            LastAnalyzeRequest = request;
            byte[] bytes = "{}"u8.ToArray();
            await File.WriteAllBytesAsync(
                request.OutputProposal.Value,
                bytes,
                cancellationToken);
            foreach (SkyrimArmorAddonProductionRequest addon in
                     request.ArmorAddonOutputs.IsDefault
                         ? []
                         : request.ArmorAddonOutputs)
                await File.WriteAllBytesAsync(
                    addon.ProposalRequest.OutputProposal.Value,
                    bytes,
                    cancellationToken);
            return new SkyrimArmorProductionProposal(
                request,
                Artifact(request),
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))),
                []);
        }

        public async ValueTask<SkyrimArmorProductionResult> ApplyAsync(
            SkyrimArmorProductionRequest request,
            SkyrimArmorProductionProposal proposal,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            byte[] bytes = "verified-armor"u8.ToArray();
            await File.WriteAllBytesAsync(
                request.OutputPlugin.Value,
                bytes,
                cancellationToken);
            foreach (SkyrimArmorAddonProductionRequest addon in
                     request.ArmorAddonOutputs.IsDefault
                         ? []
                         : request.ArmorAddonOutputs)
                await File.WriteAllBytesAsync(
                    addon.OutputPlugin.Value,
                    "verified-addon"u8.ToArray(),
                    cancellationToken);
            Sha256Hash hash = new(Convert.ToHexString(SHA256.HashData(bytes)));
            var write = new ArmorBinaryWriteResult(
                true,
                request.OutputProposal,
                request.OutputPlugin,
                request.Document.TargetFormId,
                hash,
                []);
            return new SkyrimArmorProductionResult(
                true,
                proposal,
                write,
                Verification(request, hash),
                []);
        }

        public ValueTask<SkyrimArmorProductionVerification> VerifyAsync(
            SkyrimArmorProductionRequest request,
            SkyrimArmorProductionProposal proposal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return ValueTask.FromResult(Verification(
                request,
                HashArmorDesktopFile(request.OutputPlugin.Value)));
        }

        private static ArmorProposalArtifact Artifact(
            SkyrimArmorProductionRequest request) => new(
            "1",
            "armor-record-proposal",
            "skyrimse",
            request.Document.Mode,
            request.Document.SourcePlugin.Value,
            request.Document.SourceFormId.ToString(),
            request.Document.EditorId.Value,
            request.Source.SourcePluginSha256.Value,
            new string('A', 64),
            request.Document.Name,
            request.Document.SlotMask,
            request.Document.Race.ToString(),
            request.Document.MaleWorldModel,
            request.Document.FemaleWorldModel,
            request.Document.Value,
            request.Document.Weight,
            null,
            request.Document.ArmorRating,
            request.Document.Keywords.Select(item => item.ToString()).ToImmutableArray(),
            request.Document.ArmorAddons.Select(item =>
                new ArmorAddonArtifact(0, item.ToString())).ToImmutableArray(),
            ["name"],
            [],
            true,
            request.Document.TargetFormId?.ToString(),
            request.Document.Description,
            request.Document.NonPlayable,
            CompleteDocument: true);

        private static SkyrimArmorProductionVerification Verification(
            SkyrimArmorProductionRequest request,
            Sha256Hash hash)
        {
            ImmutableArray<SkyrimArmorAddonProductionRequest> addons =
                request.ArmorAddonOutputs.IsDefault
                    ? []
                    : request.ArmorAddonOutputs;
            ImmutableArray<SkyrimArmorAddonProductionVerification> verifiedAddons =
                addons.Select(item =>
                {
                    Sha256Hash addonHash = HashArmorDesktopFile(
                        item.OutputPlugin.Value);
                    return new SkyrimArmorAddonProductionVerification(
                        true,
                        item.OutputPlugin,
                        addonHash,
                        1,
                        0,
                        new PluginName(Path.GetFileName(item.OutputPlugin.Value)),
                        new PluginName(Path.GetFileName(
                            item.ProposalRequest.SourcePlugin.Value)),
                        item.ProposalRequest.SourceFormId,
                        true,
                        true,
                        true,
                        true,
                        []);
                }).ToImmutableArray();
            return new SkyrimArmorProductionVerification(
                true,
                request.OutputPlugin,
                hash,
                1,
                0,
                new PluginName(Path.GetFileName(request.OutputPlugin.Value)),
                request.Document.TargetFormId,
                true,
                true,
                true,
                true,
                [],
                verifiedAddons);
        }
    }
}
