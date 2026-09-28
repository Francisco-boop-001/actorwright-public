using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimOutfitProductionWorkspaceViewModelTest() =>
        RunSkyrimOutfitProductionWorkspaceViewModelTestAsync()
            .GetAwaiter().GetResult();

    private static async Task RunSkyrimOutfitProductionWorkspaceViewModelTestAsync()
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work",
            $"outfit-production-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var plugin = new PluginName("OutfitProvider.esp");
            var owner = new PluginName("OutfitBase.esm");
            var armor = new FormReference(owner, new FormId(0x800));
            var leveled = new FormReference(owner, new FormId(0x810));
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "load-order.json")),
                new WorkspacePath(Path.Combine(root, "future-output")),
                new Sha256Hash(new string('1', 64)),
                [
                    new PluginClosureReviewEntry(
                        plugin, 0, true, true, true, false, true,
                        new WorkspacePath(Path.Combine(root, plugin.Value)),
                        new Sha256Hash(new string('2', 64)), [])
                ],
                [], [], [], 0,
                new Sha256Hash(new string('3', 64)),
                new Sha256Hash(new string('4', 64)),
                false);
            var existing = new OutfitChoiceCandidate(
                plugin,
                new FormId(0x900),
                "ProductionBaseOutfit",
                "Production outfit",
                [armor.FormId, leveled.FormId],
                false,
                new OutfitChoiceProvenance(
                    OutfitChoiceProvenanceKind.Override,
                    owner,
                    [owner, plugin]),
                [armor, leveled]);
            var armorItem = new SkyrimOutfitEditorItem(
                armor,
                SkyrimOutfitEditorItemKind.Armor,
                "Production armor",
                0x04);
            var leveledItem = new SkyrimOutfitEditorItem(
                leveled,
                SkyrimOutfitEditorItemKind.LeveledList,
                "Production leveled armor",
                0x08,
                11,
                [new SkyrimOutfitPreviewArmor(
                    new FormReference(owner, new FormId(0x801)),
                    0x08)]);
            var state = new SkyrimOutfitProductionState(
                intake,
                [plugin],
                new WorkspacePath(Path.Combine(root, plugin.Value)),
                new Sha256Hash(new string('2', 64)),
                new FormId(0x900),
                new FormId(0xA00),
                11,
                [existing],
                [armorItem, leveledItem]);
            var loader = new FakeOutfitProductionLoadService(state);
            var transaction = new FakeOutfitProductionTransactionService();
            var catalog = new FakeOutfitItemCatalogReader(leveledItem.PreviewRealization);
            using var viewModel = new SkyrimOutfitProductionWorkspaceViewModel(
                loader,
                transaction,
                catalog,
                labRoot);
            viewModel.ApplyReviewedIntake(intake);
            viewModel.TemplatePlugin = plugin.Value;
            viewModel.TemplateFormId = "0x00000900";
            viewModel.NewTargetFormId = "0x00000A00";
            viewModel.PreviewSeed = "11";
            viewModel.ProposalPath = Path.Combine(root, "new.outfit-proposal.json");
            viewModel.OutputPlugin = Path.Combine(root, "NewOutfit.esp");

            await viewModel.LoadAsync();
            Assert(viewModel.IsLoaded && viewModel.CanOpenEditor &&
                   viewModel.CatalogSummary.Contains("1 ARMO / 1 LVLI", StringComparison.Ordinal),
                "The production outfit parent did not expose its reviewed catalog.");

            SkyrimOutfitEditorViewModel cancelled = viewModel.CreateEditor((_, _) => null);
            cancelled.TryBeginNew();
            cancelled.Cancel();
            viewModel.ReportEditorCancelled();
            Assert(!viewModel.HasAcceptedSelection,
                "A cancelled outfit modal leaked an outer accepted transaction.");

            SkyrimOutfitEditorViewModel editor = viewModel.CreateEditor((_, _) => null);
            Assert(editor.TryBeginNew(), "The production parent could not begin a new outfit.");
            editor.SelectedAvailableItem = editor.AvailableItems.Single(item =>
                item.Item.Reference == armor);
            Assert(editor.TryAddSelectedItem(), "The production parent could not add ARMO.");
            editor.SelectedAvailableItem = editor.AvailableItems.Single(item =>
                item.Item.Reference == leveled);
            Assert(editor.TryAddSelectedItem() && editor.TryAccept() &&
                   viewModel.ApplyEditor(editor) && viewModel.HasAuthoredProposal,
                "The authored OTFT did not return from the owner-modal boundary.");

            await viewModel.ReviewAsync();
            Assert(viewModel.IsReviewed && File.Exists(viewModel.ProposalPath) &&
                   !File.Exists(viewModel.OutputPlugin),
                "Production outfit Review did not remain proposal-only.");
            await viewModel.ExecuteAsync();
            Assert(viewModel.HasCompleted &&
                   viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
                   File.Exists(viewModel.OutputPlugin) &&
                   viewModel.ResultSha256 == HashOutfitDesktopFile(viewModel.OutputPlugin).Value,
                "Production outfit Apply did not write and explicitly verify its output.");

            viewModel.OutputPlugin = Path.Combine(root, "ChangedAfterReview.esp");
            Assert(!viewModel.IsReviewed && !viewModel.HasCompleted,
                "Changing the binary destination did not invalidate reviewed authority.");

            using var overrideWorkspace = new SkyrimOutfitProductionWorkspaceViewModel(
                loader,
                new FakeOutfitProductionTransactionService(),
                catalog,
                labRoot);
            overrideWorkspace.ApplyReviewedIntake(intake);
            overrideWorkspace.TemplatePlugin = plugin.Value;
            overrideWorkspace.TemplateFormId = "0x00000900";
            overrideWorkspace.NewTargetFormId = "0x00000A01";
            overrideWorkspace.PreviewSeed = "11";
            overrideWorkspace.ProposalPath = Path.Combine(root, "override.outfit-proposal.json");
            overrideWorkspace.OutputPlugin = Path.Combine(root, "OverrideOutfit.esp");
            await overrideWorkspace.LoadAsync();
            SkyrimOutfitEditorViewModel overrideEditor =
                overrideWorkspace.CreateEditor((_, _) => null);
            overrideEditor.SelectedBrowseRow = overrideEditor.BrowseRows.Single(row =>
                row.Candidate == existing);
            Assert(overrideEditor.TryBeginOverride() &&
                   overrideEditor.DraftItems.Select(item => item.Item.Reference)
                       .SequenceEqual([armor, leveled]),
                "Override setup did not preserve its reviewed qualified item order.");
            overrideEditor.SelectedDraftItem = overrideEditor.DraftItems[0];
            Assert(overrideEditor.TryMoveSelectedItem(1) &&
                   overrideEditor.TryAccept() &&
                   overrideWorkspace.ApplyEditor(overrideEditor) &&
                   overrideWorkspace.SelectionSummary.Contains("Override", StringComparison.Ordinal),
                "The source-owned override did not return through the production parent.");

            string panel = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "SkyrimOutfitProductionPanel.xaml"));
            string shell = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "MainWindow.xaml"));
            Assert(panel.Contains(
                       "AutomationProperties.Name=\"Load reviewed outfit catalog\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Open Skyrim outfit workbench\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Review outfit proposal\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Write and verify outfit output\"",
                       StringComparison.Ordinal) &&
                   panel.Contains("runtime, or visual proof", StringComparison.Ordinal) &&
                   shell.Contains("Header=\"Outfits\"", StringComparison.Ordinal),
                "The production outfit panel or shell tab lacks accessible actions or authority warnings.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine(
            "PASS production outfit workspace proves modal rollback, new/override staging, proposal-only review, write, and second readback.");
    }

    private static Sha256Hash HashOutfitDesktopFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class FakeOutfitProductionLoadService(
        SkyrimOutfitProductionState state) : ISkyrimOutfitProductionLoadService
    {
        public ValueTask<SkyrimOutfitProductionLoadResult> LoadAsync(
            SkyrimOutfitProductionLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimOutfitProductionLoadResult(
                true, state, []));
        }
    }

    private sealed class FakeOutfitItemCatalogReader(
        ImmutableArray<SkyrimOutfitPreviewArmor> realization) :
        ISkyrimOutfitItemCatalogReader
    {
        public SkyrimOutfitItemCatalogResult Read(
            SkyrimOutfitItemCatalogRequest request) => new(true, [], []);

        public ImmutableArray<SkyrimOutfitPreviewArmor> Resolve(
            SkyrimOutfitPreviewResolveRequest request) => realization;
    }

    private sealed class FakeOutfitProductionTransactionService :
        ISkyrimOutfitProductionTransactionService
    {
        public async ValueTask<SkyrimOutfitProductionProposal> AnalyzeAsync(
            SkyrimOutfitProductionTransactionRequest request,
            CancellationToken cancellationToken)
        {
            byte[] bytes = "{}"u8.ToArray();
            await File.WriteAllBytesAsync(
                request.OutfitProposal.OutputProposal.Value,
                bytes,
                cancellationToken);
            var artifact = Artifact(request);
            return new SkyrimOutfitProductionProposal(
                request,
                artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))),
                []);
        }

        public async ValueTask<SkyrimOutfitProductionResult> ApplyAsync(
            SkyrimOutfitProductionTransactionRequest request,
            SkyrimOutfitProductionProposal proposal,
            CancellationToken cancellationToken)
        {
            byte[] bytes = "verified-outfit"u8.ToArray();
            await File.WriteAllBytesAsync(request.OutputPlugin.Value, bytes,
                cancellationToken);
            Sha256Hash hash = new(Convert.ToHexString(SHA256.HashData(bytes)));
            var write = new OutfitBinaryWriteResult(
                true,
                request.OutfitProposal.OutputProposal,
                request.OutputPlugin,
                request.OutfitProposal.TargetFormId ?? request.OutfitProposal.SourceFormId,
                hash,
                []);
            SkyrimOutfitProductionVerification verification = Verification(
                request, hash);
            return new SkyrimOutfitProductionResult(
                true, proposal, write, verification, []);
        }

        public ValueTask<SkyrimOutfitProductionVerification> VerifyAsync(
            SkyrimOutfitProductionTransactionRequest request,
            SkyrimOutfitProductionProposal proposal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Verification(
                request,
                HashOutfitDesktopFile(request.OutputPlugin.Value)));
        }

        private static OutfitProposalArtifact Artifact(
            SkyrimOutfitProductionTransactionRequest request) => new(
            "1",
            "outfit-record-proposal",
            "skyrimse",
            request.OutfitProposal.Mode,
            request.OutfitProposal.SourcePlugin.Value,
            request.OutfitProposal.SourceFormId.ToString(),
            request.OutfitProposal.EditorId?.Value ?? "ProductionBaseOutfit",
            new string('a', 64),
            request.OutfitProposal.Items.Select(item => item.ToString()).ToImmutableArray(),
            [],
            true,
            (request.OutfitProposal.TargetFormId ??
             request.OutfitProposal.SourceFormId).ToString(),
            request.OutfitProposal.Mode == OutfitProposalMode.Override
                ? "OutfitBase.esm"
                : "OutfitProvider.esp");

        private static SkyrimOutfitProductionVerification Verification(
            SkyrimOutfitProductionTransactionRequest request,
            Sha256Hash hash) => new(
            true,
            request.OutputPlugin,
            hash,
            1,
            request.OutfitProposal.Mode == OutfitProposalMode.New
                ? new PluginName(Path.GetFileName(request.OutputPlugin.Value))
                : new PluginName("OutfitBase.esm"),
            request.OutfitProposal.TargetFormId ?? request.OutfitProposal.SourceFormId,
            true,
            true,
            true,
            true,
            []);
    }
}
