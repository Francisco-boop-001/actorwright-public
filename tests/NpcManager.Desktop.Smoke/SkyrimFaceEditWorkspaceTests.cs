using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimFaceEditWorkspaceViewModelTest() =>
        RunSkyrimFaceEditWorkspaceViewModelTestAsync().GetAwaiter().GetResult();

    private static async Task RunSkyrimFaceEditWorkspaceViewModelTestAsync()
    {
        var plugin = new PluginName("FaceEditFixtureSSE.esp");
        var sourcePath = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test\\Data\\FaceEditFixtureSSE.esp");
        var sourceHash = new Sha256Hash(new string('A', 64));
        var race = new FormReference(plugin, new FormId(0x801));
        var hairColor = new FormReference(plugin, new FormId(0x802));
        var headTexture = new FormReference(plugin, new FormId(0x804));
        var face = new NpcHeadPartSelection(
            new FormReference(plugin, new FormId(0x811)),
            NpcHeadPartType.Face);
        var hair = new NpcHeadPartSelection(
            new FormReference(plugin, new FormId(0x813)),
            NpcHeadPartType.Hair);
        var morphs = new SkyrimFaceMorphPatch(
            Enumerable.Repeat(0F, SkyrimFaceEditorDocumentRules.NativeSliderCount)
                .ToImmutableArray(),
            0.125F,
            Enumerable.Repeat(SkyrimFaceEditorDocumentRules.NamaUnset,
                    SkyrimFaceEditorDocumentRules.NativeFamilyCount)
                .ToImmutableArray());
        var tint = new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 75, 2);
        var document = new SkyrimFaceEditorDocument(
            new SkyrimFaceEditorParts(
                [face, hair],
                OptionalFormReference.Set(hairColor),
                headTexture,
                false),
            morphs,
            [],
            [new SkyrimFaceEditorTintLayer(tint, true, null, null)],
            [],
            []);
        var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
            [new ExternalSkyrimNpcHeadPart(face), new ExternalSkyrimNpcHeadPart(hair)],
            new ExternalSkyrimNpcHairColor(hairColor),
            new ExternalSkyrimNpcFaceTextureSet(headTexture),
            55F,
            morphs,
            new SkyrimFaceTintPatch([tint]),
            new SkyrimQnamRgb(32F / 255F, 96F / 255F, 160F / 255F));
        var paint = new SkyrimRaceMenuPaintChoiceCandidate(
            SkyrimRaceMenuPaintCategory.Warpaint,
            "TestWarpaint",
            "Test warpaint",
            new AssetPath("actors/character/overlays/test-warpaint.dds"),
            new AssetPath("textures/actors/character/overlays/test-warpaint.dds"),
            [new SkyrimRaceMenuPaintTextureSlot(
                0,
                SkyrimRaceMenuPaintSlotKind.Texture,
                "actors/character/overlays/test-warpaint.dds",
                new AssetPath("textures/actors/character/overlays/test-warpaint.dds"))],
            [new SkyrimRaceMenuPaintRegistrationSource(
                new AssetPath("scripts/test-paint.pex"),
                SkyrimRaceMenuPaintProviderKind.Bsa,
                new WorkspacePath(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test\\Data\\RaceMenu.bsa"),
                new Sha256Hash(new string('1', 64)),
                new Sha256Hash(new string('2', 64)),
                128)]);
        var paints = Enum.GetValues<SkyrimRaceMenuPaintCategory>()
            .ToImmutableDictionary(
                category => category,
                category => new SkyrimRaceMenuPaintChoiceResult(
                    true,
                    category == SkyrimRaceMenuPaintCategory.Warpaint ? [paint] : [],
                    null,
                    []));
        var loadedState = new SkyrimFaceEditLoadedState(
            sourcePath,
            sourceHash,
            new FormId(0x800),
            new EditorId("FaceEditFixtureNpcSSE"),
            race,
            "FaceEditFixtureRaceSSE",
            NpcSex.Female,
            document,
            new SkyrimFaceEditWriterBaseline(document, appearance, null),
            new SkyrimFaceEditCatalogs(
                new FormChoiceSearchResult(
                    GameEdition.SkyrimSpecialEdition,
                    true,
                    [],
                    []),
                ImmutableDictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult>.Empty,
                paints,
                null,
                []));
        var loadService = new CompletingFaceEditLoadService(
            new SkyrimFaceEditLoadResult(true, loadedState, []));
        var writeService = new CompletingAppearanceOverrideService();
        using var viewModel = new SkyrimFaceEditWorkspaceViewModel(
            loadService,
            writeService,
            null,
            new WorkspacePath("K:\\ExampleWorkspace"));
        viewModel.ApplyReviewedIntake(CreateFaceEditIntake(plugin, sourcePath, sourceHash));
        viewModel.SourcePlugin = plugin.Value;
        viewModel.NpcFormId = "0x00000800";
        viewModel.ProposalPath =
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test\\proposal\\face-edit.json";
        viewModel.OutputPlugin =
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test\\output\\FaceEditFixtureSSE-Edited.esp";

        await viewModel.LoadAsync();
        Assert(viewModel.IsLoaded && !viewModel.HasStagedChange &&
               SkyrimFaceEditorDocumentRules.Equivalent(
                   viewModel.StagedDocument!,
                   document),
            "Face-edit workspace load staged an implicit mutation.");

        SkyrimFaceEditorViewModel cancelled = viewModel.CreateEditor();
        cancelled.NativeMorphs.Sliders[0].Value = 0.25F;
        cancelled.Cancel();
        Assert(!viewModel.ApplyEditor(cancelled) &&
               !viewModel.HasStagedChange &&
               SkyrimFaceEditorDocumentRules.Equivalent(
                   viewModel.StagedDocument!,
                   document),
            "Whole-editor Cancel changed the parent transaction.");

        SkyrimFaceEditorViewModel editor = viewModel.CreateEditor();
        SkyrimRaceMenuPaintPickerViewModel selectedPaint =
            editor.CreateTintMaskPicker(1);
        selectedPaint.SelectedRow = selectedPaint.VisibleRows.Single(item => !item.IsClear);
        Assert(selectedPaint.TryAccept() &&
               editor.ApplyTintMaskPicker(1, selectedPaint) &&
               editor.CurrentDocument.Tints[0].MaskOverride == paint.RegisteredPath,
            "The production tint caller did not retain an exact paint selection.");
        SkyrimRaceMenuPaintPickerViewModel clearedPaint =
            editor.CreateTintMaskPicker(1);
        clearedPaint.SelectedRow = clearedPaint.VisibleRows.Single(item => item.IsClear);
        Assert(clearedPaint.TryAccept() &&
               editor.ApplyTintMaskPicker(1, clearedPaint) &&
               editor.CurrentDocument.Tints[0].MaskOverride is null,
            "The production tint caller did not distinguish explicit clear.");
        SkyrimRaceMenuPaintPickerViewModel cancelledPaint =
            editor.CreateTintMaskPicker(1);
        cancelledPaint.SelectedRow = cancelledPaint.VisibleRows.Single(item => !item.IsClear);
        cancelledPaint.Cancel();
        Assert(!editor.ApplyTintMaskPicker(1, cancelledPaint) &&
               editor.CurrentDocument.Tints[0].MaskOverride is null,
            "Cancelled paint selection changed the face document.");

        editor.NativeMorphs.Sliders[0].Value = 0.25F;
        Assert(editor.TryAccept() && viewModel.ApplyEditor(editor) &&
               viewModel.HasStagedChange &&
               viewModel.StagedDocument!.NativeMorphs.Nam9Sliders[0] == 0.25F &&
               viewModel.StagedDocument.Tints[0].MaskOverride is null,
            "An accepted native face edit was not staged without the reset paint mask.");

        await viewModel.ReviewAsync();
        Assert(viewModel.IsReviewed && writeService.AnalyzeCalls == 1 &&
               writeService.ApplyCalls == 0 && writeService.VerifyCalls == 0,
            "Face-edit review wrote early or did not retain exactly one proposal.");
        await viewModel.ExecuteAsync();
        Assert(viewModel.HasCompleted && writeService.ApplyCalls == 1 &&
               writeService.VerifyCalls == 1 &&
               viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
               viewModel.OutputSha256 == new Sha256Hash(new string('E', 64)).Value,
            "Face-edit execution did not retain one independently verified output.");

        string panelXaml = File.ReadAllText(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\SkyrimFaceEditPanel.xaml");
        Assert(panelXaml.Contains(
                   "AutomationProperties.Name=\"Load complete face document\"",
                   StringComparison.Ordinal) &&
               panelXaml.Contains(
                   "AutomationProperties.Name=\"Open six-section face editor\"",
                   StringComparison.Ordinal) &&
               panelXaml.Contains(
                   "AutomationProperties.Name=\"Review face proposal\"",
                   StringComparison.Ordinal) &&
               panelXaml.Contains(
                   "AutomationProperties.Name=\"Write and verify fresh face plugin\"",
                   StringComparison.Ordinal) &&
               panelXaml.Contains(
                   "Runtime, FaceGen, texture-render, and visual authority remain false",
                   StringComparison.Ordinal) &&
               panelXaml.Contains(
                   "TargetType=\"{x:Type GridViewColumnHeader}\"",
                   StringComparison.Ordinal) &&
               panelXaml.Contains(
                   "<Setter Property=\"Background\" Value=\"#13252C\" />",
                   StringComparison.Ordinal),
            "The face-edit production panel does not expose the required accessible actions, false-authority warning, and readable native headers.");
        Assert(!panelXaml.Contains('\u00C2', StringComparison.Ordinal) &&
               !panelXaml.Contains('\u00C3', StringComparison.Ordinal) &&
               !panelXaml.Contains('\u00E2', StringComparison.Ordinal),
            "The face-edit production panel contains mojibake characters.");
        string appXaml = File.ReadAllText(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\App.xaml");
        Assert(appXaml.Contains(
                   "<SolidColorBrush x:Key=\"WarningBrush\" Color=\"#F6C86D\" />",
                   StringComparison.Ordinal),
            "The shared warning brush is missing its high-contrast amber definition.");

        Console.WriteLine(
            "PASS production face-edit workspace loads neutrally, distinguishes paint outcomes, rolls back Cancel, reviews, writes, and verifies one native morph.");
    }

    private static ReviewedGameIntake CreateFaceEditIntake(
        PluginName plugin,
        WorkspacePath sourcePath,
        Sha256Hash sourceHash) => new(
        GameEdition.SkyrimSpecialEdition,
        new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test"),
        new WorkspacePath(Path.GetDirectoryName(sourcePath.Value)!),
        new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test\\loadorder.txt"),
        new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\face-edit-desktop-test\\future-output"),
        new Sha256Hash(new string('B', 64)),
        [new PluginClosureReviewEntry(
            plugin, 0, true, true, true, false, true, sourcePath, sourceHash, [])],
        [], [], [], 0,
        new Sha256Hash(new string('C', 64)),
        new Sha256Hash(new string('D', 64)),
        false);

    private sealed class CompletingFaceEditLoadService(
        SkyrimFaceEditLoadResult result) : ISkyrimFaceEditLoadService
    {
        public ValueTask<SkyrimFaceEditLoadResult> LoadAsync(
            SkyrimFaceEditLoadRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class CompletingAppearanceOverrideService :
        INpcAppearanceOverrideService
    {
        public int AnalyzeCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }

        public ValueTask<NpcAppearanceOverrideProposal> AnalyzeAsync(
            NpcAppearanceOverrideRequest request,
            CancellationToken cancellationToken)
        {
            AnalyzeCalls++;
            return ValueTask.FromResult(Proposal(request));
        }

        public ValueTask<NpcAppearanceOverrideResult> ApplyAsync(
            NpcAppearanceOverrideRequest request,
            NpcAppearanceOverrideProposal proposal,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            return ValueTask.FromResult(new NpcAppearanceOverrideResult(
                true,
                proposal,
                Verification(request),
                []));
        }

        public ValueTask<NpcAppearanceOverrideVerificationResult> VerifyAsync(
            NpcAppearanceOverrideRequest request,
            NpcAppearanceOverrideProposal proposal,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;
            return ValueTask.FromResult(Verification(request));
        }

        private static NpcAppearanceOverrideProposal Proposal(
            NpcAppearanceOverrideRequest request) => new(
            1,
            request.Edition,
            request.SourcePlugin,
            request.ExpectedSourceSha256,
            new PluginName(Path.GetFileName(request.SourcePlugin.Value)),
            request.TargetFormId,
            new EditorId("FaceEditFixtureNpcSSE"),
            request.ProposalPath,
            new Sha256Hash(new string('F', 64)),
            request.OutputPlugin,
            new PluginName(Path.GetFileName(request.OutputPlugin.Value)),
            request.Race,
            request.Sex,
            request.Appearance,
            request.RuntimeAppearance,
            [],
            [new RecordSignature("NPC_")],
            ["NAM9"],
            []);

        private static NpcAppearanceOverrideVerificationResult Verification(
            NpcAppearanceOverrideRequest request) => new(
            true,
            request.OutputPlugin,
            new Sha256Hash(new string('E', 64)),
            [],
            [new RecordSignature("NPC_")],
            1,
            0,
            true,
            true,
            true,
            true,
            []);
    }
}
