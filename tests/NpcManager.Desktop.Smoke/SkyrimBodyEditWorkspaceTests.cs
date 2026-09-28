using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimBodyEditWorkspaceViewModelTest() =>
        RunSkyrimBodyEditWorkspaceViewModelTestAsync().GetAwaiter().GetResult();

    private static async Task RunSkyrimBodyEditWorkspaceViewModelTestAsync()
    {
        var plugin = new PluginName("FaceEditFixtureSSE.esp");
        var sourcePath = new WorkspacePath(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\body-edit-desktop-test\\Data\\FaceEditFixtureSSE.esp");
        var sourceHash = new Sha256Hash(new string('A', 64));
        var document = new SkyrimBodyEditorDocument(55F, [], [], [], []);
        var paints = new[]
        {
            SkyrimRaceMenuPaintCategory.Body,
            SkyrimRaceMenuPaintCategory.Hands,
            SkyrimRaceMenuPaintCategory.Feet
        }.ToImmutableDictionary(
            category => category,
            category => new SkyrimRaceMenuPaintChoiceResult(
                true,
                [],
                null,
                []));
        var limits = Enum.GetValues<BodyOverlayTarget>()
            .ToImmutableDictionary(target => target, _ => 16);
        var loadedState = new SkyrimBodyEditLoadedState(
            sourcePath,
            sourceHash,
            new FormId(0x800),
            new EditorId("FaceEditFixtureNpcSSE"),
            document,
            new SkyrimBodyEditWriterBaseline(document),
            new SkyrimBodyEditCatalogs(["Waist"], [], paints, limits));
        var loadService = new CompletingBodyEditLoadService(
            new SkyrimBodyEditLoadResult(true, loadedState, []));
        var overrideService = new CompletingBodyOverrideService();
        string transactionRoot = Path.Combine(
            "K:\\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"body-edit-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionRoot);
        try
        {
            string proposalPath = Path.Combine(transactionRoot, "body-edit-proposal.json");
            string outputPath = Path.Combine(
                transactionRoot,
                "FaceEditFixtureSSE-BodyEdited.esp");
            using var viewModel = new SkyrimBodyEditWorkspaceViewModel(
                loadService,
                overrideService,
                new WorkspacePath("K:\\ExampleWorkspace"));
            viewModel.ApplyReviewedIntake(CreateFaceEditIntake(
                plugin,
                sourcePath,
                sourceHash));
            viewModel.SourcePlugin = plugin.Value;
            viewModel.NpcFormId = "0x00000800";
            viewModel.ProposalPath = proposalPath;
            viewModel.OutputPlugin = outputPath;

            await viewModel.LoadAsync();
            Assert(viewModel.IsLoaded && !viewModel.HasStagedChange &&
                   SkyrimBodyEditorDocumentRules.Equivalent(
                       viewModel.StagedDocument!,
                       document),
                "Body-edit workspace load staged an implicit mutation.");

            SkyrimBodyEditorViewModel cancelled = viewModel.CreateEditor();
            cancelled.Weight.Value = 60F;
            cancelled.Cancel();
            Assert(!viewModel.ApplyEditor(cancelled) &&
                   !viewModel.HasStagedChange &&
                   viewModel.StagedDocument!.Weight == 55F,
                "Whole-editor body Cancel changed the parent transaction.");

            SkyrimBodyEditorViewModel unsupported = viewModel.CreateEditor();
            unsupported.BodySlide.Rows[0].Percent = 50F;
            Assert(unsupported.TryAccept() &&
                   !viewModel.ApplyEditor(unsupported) &&
                   !viewModel.HasStagedChange &&
                   viewModel.Diagnostics.Any(item => item.Contains(
                       "body-edit-bodyslide-sidecar-only",
                       StringComparison.Ordinal)),
                "The body parent silently staged an unsupported BodySlide change.");

            SkyrimBodyEditorViewModel editor = viewModel.CreateEditor();
            editor.Weight.Value = 60F;
            Assert(editor.TryAccept() && viewModel.ApplyEditor(editor) &&
                   viewModel.HasStagedChange &&
                   viewModel.StagedDocument!.Weight == 60F,
                "An accepted NAM7 change was not staged.");

            await viewModel.ReviewAsync();
            Assert(viewModel.IsReviewed &&
                   overrideService.AnalyzeCalls == 1 &&
                   overrideService.ApplyCalls == 0 &&
                   overrideService.VerifyCalls == 0 &&
                   File.Exists(proposalPath) &&
                   !File.Exists(outputPath),
                "Body review wrote the plugin early or did not retain one proposal.");

            await viewModel.ExecuteAsync();
            Assert(viewModel.HasCompleted &&
                   overrideService.ApplyCalls == 1 &&
                   overrideService.VerifyCalls == 1 &&
                   File.Exists(outputPath) &&
                   viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
                   viewModel.OutputSha256 == new Sha256Hash(Convert.ToHexString(
                       SHA256.HashData(File.ReadAllBytes(outputPath)))).Value,
                "Body execution did not retain one explicitly verified output: " +
                $"completed={viewModel.HasCompleted} apply={overrideService.ApplyCalls} " +
                $"verify={overrideService.VerifyCalls} exists={File.Exists(outputPath)} " +
                $"verdict={viewModel.Verdict} hash={viewModel.OutputSha256} " +
                $"diagnostics={string.Join(" | ", viewModel.Diagnostics)}");

            string panelXaml = File.ReadAllText(
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\SkyrimBodyEditPanel.xaml");
            Assert(panelXaml.Contains(
                       "AutomationProperties.Name=\"Load complete body document\"",
                       StringComparison.Ordinal) &&
                   panelXaml.Contains(
                       "AutomationProperties.Name=\"Open five-section body editor\"",
                       StringComparison.Ordinal) &&
                   panelXaml.Contains(
                       "AutomationProperties.Name=\"Review body proposal\"",
                       StringComparison.Ordinal) &&
                   panelXaml.Contains(
                       "AutomationProperties.Name=\"Write and verify fresh body plugin\"",
                       StringComparison.Ordinal) &&
                   panelXaml.Contains(
                       "BodySlide-build, mesh, texture-render, runtime, and visual authority remain false",
                       StringComparison.Ordinal),
                "The body production panel is missing accessible actions or false-authority warnings.");
            Assert(!panelXaml.Contains('\u00C2', StringComparison.Ordinal) &&
                   !panelXaml.Contains('\u00C3', StringComparison.Ordinal) &&
                   !panelXaml.Contains('\u00E2', StringComparison.Ordinal),
                "The body production panel contains mojibake characters.");
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
        }

        Console.WriteLine(
            "PASS production body-edit workspace rolls back Cancel, refuses sidecar loss, reviews, writes, and verifies NAM7.");
    }

    private sealed class CompletingBodyEditLoadService(
        SkyrimBodyEditLoadResult result) : ISkyrimBodyEditLoadService
    {
        public ValueTask<SkyrimBodyEditLoadResult> LoadAsync(
            SkyrimBodyEditLoadRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class CompletingBodyOverrideService : INpcOverrideService
    {
        public int AnalyzeCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }

        public ValueTask<NpcOverrideSourceInspection> InspectSourceAsync(
            GameEdition edition,
            WorkspacePath sourcePlugin,
            Sha256Hash expectedSourceSha256,
            FormId targetFormId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<NpcOverrideProposal> AnalyzeAsync(
            NpcOverrideRequest request,
            CancellationToken cancellationToken)
        {
            AnalyzeCalls++;
            File.WriteAllText(request.ProposalPath!.Value.Value, "{\"accepted\":true}");
            return ValueTask.FromResult(Proposal(
                request,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(request.ProposalPath.Value.Value))))));
        }

        public ValueTask<NpcOverrideResult> ApplyAsync(
            NpcOverrideRequest request,
            NpcOverrideProposal proposal,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            File.WriteAllText(request.OutputPlugin.Value, "verified body output");
            Sha256Hash hash = new(Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(request.OutputPlugin.Value))));
            return ValueTask.FromResult(new NpcOverrideResult(
                true,
                proposal,
                hash,
                Verification(request, proposal, hash),
                []));
        }

        public ValueTask<NpcOverrideVerificationResult> VerifyAsync(
            NpcOverrideRequest request,
            NpcOverrideProposal proposal,
            CancellationToken cancellationToken)
        {
            VerifyCalls++;
            Sha256Hash hash = new(Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(request.OutputPlugin.Value))));
            return ValueTask.FromResult(Verification(request, proposal, hash));
        }

        private static NpcOverrideProposal Proposal(
            NpcOverrideRequest request,
            Sha256Hash proposalHash) => new(
            1,
            request.Edition,
            request.SourcePlugin,
            request.ExpectedSourceSha256,
            new PluginName(Path.GetFileName(request.SourcePlugin.Value)),
            request.TargetFormId,
            request.ProposalPath,
            proposalHash,
            request.OutputPlugin,
            new PluginName(Path.GetFileName(request.OutputPlugin.Value)),
            request.Patch,
            [new PluginName(Path.GetFileName(request.SourcePlugin.Value))],
            [new MutationChange("SkyrimWeight", "55", "60")],
            ["EDID", "NAM7"],
            []);

        private static NpcOverrideVerificationResult Verification(
            NpcOverrideRequest request,
            NpcOverrideProposal proposal,
            Sha256Hash outputHash) => new(
            true,
            request.SourcePlugin,
            request.ExpectedSourceSha256,
            request.OutputPlugin,
            outputHash,
            proposal.SourcePluginName,
            request.TargetFormId,
            proposal.RequiredMasters,
            proposal.RequiredMasters,
            1,
            1,
            1,
            0,
            proposal.Changes,
            []);
    }
}
