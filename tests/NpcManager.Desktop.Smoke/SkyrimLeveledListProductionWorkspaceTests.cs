using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimLeveledListProductionWorkspaceViewModelTest() =>
        RunSkyrimLeveledListProductionWorkspaceViewModelTestAsync()
            .GetAwaiter().GetResult();

    private static async Task RunSkyrimLeveledListProductionWorkspaceViewModelTestAsync()
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work",
            $"leveled-list-production-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var provider = new PluginName("OutfitBase.esm");
            var armor = new SkyrimOutfitEditorItem(
                new FormReference(provider, new FormId(0x800)),
                SkyrimOutfitEditorItemKind.Armor,
                "Reviewed armor",
                0x04);
            var existingList = new SkyrimOutfitEditorItem(
                new FormReference(provider, new FormId(0x810)),
                SkyrimOutfitEditorItemKind.LeveledList,
                "npcm_LVLI_Existing",
                0,
                17,
                []);
            var intake = new ReviewedGameIntake(
                GameEdition.SkyrimSpecialEdition,
                labRoot,
                new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "load-order.json")),
                new WorkspacePath(Path.Combine(root, "future-output")),
                new Sha256Hash(new string('1', 64)),
                [
                    new PluginClosureReviewEntry(
                        provider, 0, true, true, true, false, true,
                        new WorkspacePath(Path.Combine(root, provider.Value)),
                        new Sha256Hash(new string('2', 64)), [])
                ],
                [], [], [], 0,
                new Sha256Hash(new string('3', 64)),
                new Sha256Hash(new string('4', 64)),
                false);
            var state = new SkyrimLeveledListProductionState(
                intake,
                [provider],
                [armor, existingList],
                [new EditorId("npcm_LVLI_Existing")]);
            var transaction = new FakeLeveledListProductionTransactionService();
            using var viewModel = new SkyrimLeveledListProductionWorkspaceViewModel(
                new FakeLeveledListProductionLoadService(state),
                transaction,
                labRoot);
            viewModel.ApplyReviewedIntake(intake);
            viewModel.PreviewSeed = "17";
            viewModel.TargetFormId = "0x00000A10";
            viewModel.OutputPlugin = Path.Combine(root, "CreatedList.esp");
            viewModel.ProposalPath = Path.Combine(
                root, "CreatedList.leveled-list-production-proposal.json");

            await viewModel.LoadAsync();
            Assert(viewModel.IsLoaded && viewModel.Candidates.Count == 2 &&
                   viewModel.SelectedCandidate?.Item == armor && viewModel.CanOpenEditor,
                "The production LVLI parent did not expose its reviewed typed catalog.");

            SkyrimLeveledListEditorViewModel cancelledHeader = viewModel.CreateHeaderEditor();
            cancelledHeader.NameSuffix = "Cancelled";
            cancelledHeader.TryAccept();
            cancelledHeader.Cancel();
            viewModel.ReportEditorCancelled();
            Assert(!viewModel.HasAuthoredDocument,
                "A cancelled LVLI modal leaked an accepted production document.");

            SkyrimLeveledListEditorViewModel header = viewModel.CreateHeaderEditor();
            header.NameSuffix = "TravelGear";
            header.ChanceNone = 25;
            header.MaxCount = 0;
            header.CalculateAllLevels = true;
            header.UseAll = true;
            Assert(header.TryAccept(), "The production LVLI header could not be accepted.");
            SkyrimLeveledEntryEditorViewModel entry = viewModel.CreateEntryEditor();
            entry.LevelText = "7";
            entry.CountText = "2";
            Assert(entry.TryAccept() && viewModel.ApplyEditors(header, entry) &&
                   viewModel.DocumentSummary.Contains("LVLF 0x05", StringComparison.Ordinal) &&
                   viewModel.DocumentSummary.Contains("25%", StringComparison.Ordinal),
                "The Gate 016 header and Gate 017 row did not stage one complete LVLI.");

            viewModel.SelectedCandidate = viewModel.Candidates.Single(item =>
                item.Item == existingList);
            Assert(viewModel.HasAuthoredDocument &&
                   viewModel.DocumentSummary.Contains("1 row", StringComparison.Ordinal),
                "Choosing the next typed row candidate erased the accepted LVLI document.");
            viewModel.SelectedCandidate = viewModel.Candidates.Single(item =>
                item.Item == armor);

            Type productionOwner = typeof(SkyrimLeveledListProductionWorkspaceViewModel);
            string[] requiredRowMethods =
            [
                "CreateAdditionalEntryEditor",
                "CreateSelectedEntryEditor",
                "ApplyAddedEntry",
                "ApplyEditedEntry",
                "RemoveSelectedEntry"
            ];
            Assert(requiredRowMethods.All(name =>
                       productionOwner.GetMethod(name) is not null) &&
                   productionOwner.GetProperty("AuthoredEntries") is not null &&
                   productionOwner.GetProperty("SelectedAuthoredEntry") is not null &&
                   productionOwner.GetProperty("CanAddEntry") is not null &&
                   productionOwner.GetProperty("CanEditEntry") is not null &&
                   productionOwner.GetProperty("CanRemoveEntry") is not null,
                "The LVLI production owner does not expose bounded Add/Edit/Remove row operations.");
            Assert(viewModel.AuthoredEntries.Count == 1 &&
                   viewModel.SelectedAuthoredEntry?.Entry == entry.AcceptedEntry &&
                   viewModel.CanAddEntry && viewModel.CanEditEntry &&
                   viewModel.CanRemoveEntry,
                "The accepted first LVLO row was not projected as one selectable production row.");

            SkyrimLeveledEntryEditorViewModel? additional = null;
            try { additional = viewModel.CreateAdditionalEntryEditor(); }
            catch (NotSupportedException) { }
            Assert(additional is not null && additional.LevelText == "1" &&
                   additional.CountText == "1",
                "The production owner did not open a defaulted Add row editor.");
            additional!.LevelText = "32767";
            additional.CountText = "32767";
            Assert(additional.TryAccept() && viewModel.ApplyAddedEntry(additional) &&
                   viewModel.AuthoredEntries.Count == 2 &&
                   viewModel.AuthoredEntries[0].Entry.Item ==
                       viewModel.AuthoredEntries[1].Entry.Item &&
                   viewModel.AuthoredEntries[1].Entry.Level == 32767 &&
                   viewModel.AuthoredEntries[1].Entry.Count == 32767 &&
                   viewModel.SelectedAuthoredEntry?.Index == 1,
                "Add did not retain one ordered repeated-reference boundary row.");

            SkyrimLeveledEntryEditorViewModel cancelled =
                viewModel.CreateAdditionalEntryEditor();
            cancelled.LevelText = "19";
            cancelled.CountText = "3";
            cancelled.TryAccept();
            cancelled.Cancel();
            viewModel.ReportEditorCancelled();
            Assert(viewModel.AuthoredEntries.Count == 2 &&
                   viewModel.AuthoredEntries[1].Entry.Level == 32767,
                "Cancelling an additional row changed the accepted document.");

            viewModel.SelectedAuthoredEntry = viewModel.AuthoredEntries[0];
            SkyrimLeveledEntryEditorViewModel? edit = null;
            try { edit = viewModel.CreateSelectedEntryEditor(); }
            catch (NotSupportedException) { }
            Assert(edit is not null && edit.LevelText == "7" &&
                   edit.CountText == "2",
                "Edit did not seed the exact selected repeated-reference row.");
            edit!.LevelText = "1";
            edit.CountText = "1";
            Assert(edit.TryAccept() && viewModel.ApplyEditedEntry(edit) &&
                   viewModel.AuthoredEntries.Count == 2 &&
                   viewModel.AuthoredEntries[0].Entry.Level == 1 &&
                   viewModel.AuthoredEntries[0].Entry.Count == 1 &&
                   viewModel.AuthoredEntries[1].Entry.Level == 32767 &&
                   viewModel.SelectedAuthoredEntry?.Index == 0,
                "Edit did not replace only the selected repeated-reference row.");

            viewModel.SelectedAuthoredEntry = viewModel.AuthoredEntries[1];
            Assert(viewModel.RemoveSelectedEntry() &&
                   viewModel.AuthoredEntries.Count == 1 &&
                   viewModel.AuthoredEntries[0].Entry.Level == 1,
                "Remove did not delete exactly one selected repeated-reference row.");
            SkyrimLeveledEntryEditorViewModel restored =
                viewModel.CreateAdditionalEntryEditor();
            restored.LevelText = "32767";
            restored.CountText = "32767";
            Assert(restored.TryAccept() && viewModel.ApplyAddedEntry(restored) &&
                   viewModel.AuthoredEntries.Count == 2,
                "The removed boundary row could not be deliberately re-added.");

            await viewModel.ReviewAsync();
            Assert(viewModel.IsReviewed && transaction.AnalyzeCalls == 1 &&
                   File.Exists(viewModel.ProposalPath) && !File.Exists(viewModel.OutputPlugin),
                "LVLI Review did not remain proposal-only.");
            await viewModel.ExecuteAsync();
            Assert(viewModel.HasCompleted && transaction.ApplyCalls == 1 &&
                   transaction.VerifyCalls == 1 &&
                   viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
                   File.Exists(viewModel.OutputPlugin) &&
                   viewModel.ResultSha256 == HashLeveledListDesktopFile(viewModel.OutputPlugin).Value,
                "LVLI Apply did not write and perform its explicit second verification.");

            viewModel.OutputPlugin = Path.Combine(root, "ChangedAfterReview.esp");
            Assert(!viewModel.IsReviewed && !viewModel.HasCompleted &&
                   !viewModel.HasAuthoredDocument,
                "Changing the LVLI identity destination did not invalidate authored and reviewed authority.");

            string panel = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "SkyrimLeveledListProductionPanel.xaml"));
            string shell = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "MainWindow.xaml"));
            string headerXaml = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "SkyrimLeveledListEditorWindow.xaml"));
            Assert(panel.Contains(
                       "AutomationProperties.Name=\"Load reviewed leveled-list entry catalog\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Open leveled-list header and entry editors\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Review leveled-list proposal\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Write and verify leveled-list output\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Ordered authored leveled-list rows\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Add leveled-list row\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Edit selected leveled-list row\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Remove selected leveled-list row\"",
                       StringComparison.Ordinal) &&
                   panel.Contains("ItemsSource=\"{Binding AuthoredEntries}\"",
                       StringComparison.Ordinal) &&
                   panel.Contains("runtime, or visual proof", StringComparison.Ordinal) &&
                   shell.Contains("Header=\"Leveled lists\"", StringComparison.Ordinal) &&
                   headerXaml.Contains(
                       "Text=\"{Binding EditorIdPreview, Mode=OneWay}\"",
                       StringComparison.Ordinal),
                "The LVLI production panel, shell, or getter-only binding safety marker is missing.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine(
            "PASS production leveled-list workspace proves modal rollback, staged Gate 016/017 document, proposal-only review, write, and second readback.");
    }

    private static Sha256Hash HashLeveledListDesktopFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class FakeLeveledListProductionLoadService(
        SkyrimLeveledListProductionState state) :
        ISkyrimLeveledListProductionLoadService
    {
        public ValueTask<SkyrimLeveledListProductionLoadResult> LoadAsync(
            SkyrimLeveledListProductionLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimLeveledListProductionLoadResult(
                true, state, []));
        }
    }

    private sealed class FakeLeveledListProductionTransactionService :
        ISkyrimLeveledListProductionTransactionService
    {
        public int AnalyzeCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }

        public async ValueTask<SkyrimLeveledListProductionProposal> AnalyzeAsync(
            SkyrimLeveledListProductionRequest request,
            CancellationToken cancellationToken)
        {
            AnalyzeCalls++;
            byte[] bytes = "{}"u8.ToArray();
            await File.WriteAllBytesAsync(request.OutputProposal.Value, bytes,
                cancellationToken);
            SkyrimLeveledListProductionArtifact artifact = Artifact(request);
            return new SkyrimLeveledListProductionProposal(
                request,
                artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))),
                []);
        }

        public async ValueTask<SkyrimLeveledListProductionResult> ApplyAsync(
            SkyrimLeveledListProductionRequest request,
            SkyrimLeveledListProductionProposal proposal,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            byte[] bytes = "verified-leveled-list"u8.ToArray();
            await File.WriteAllBytesAsync(request.OutputPlugin.Value, bytes,
                cancellationToken);
            Sha256Hash hash = new(Convert.ToHexString(SHA256.HashData(bytes)));
            var write = new SkyrimLeveledListProductionWriteResult(
                true, request.OutputPlugin, hash, []);
            return new SkyrimLeveledListProductionResult(
                true, proposal, write, Verification(request, hash), []);
        }

        public ValueTask<SkyrimLeveledListProductionVerification> VerifyAsync(
            SkyrimLeveledListProductionRequest request,
            SkyrimLeveledListProductionProposal proposal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return ValueTask.FromResult(Verification(
                request,
                HashLeveledListDesktopFile(request.OutputPlugin.Value)));
        }

        private static SkyrimLeveledListProductionArtifact Artifact(
            SkyrimLeveledListProductionRequest request) => new(
            "1",
            "skyrim-new-leveled-list-production-proposal",
            "skyrimse",
            Path.GetFileName(request.OutputPlugin.Value),
            request.TargetFormId.ToString(),
            request.Document.EditorId.Value,
            request.Document.ChanceNone,
            request.Document.MaxCount,
            request.Document.CalculateAllLevels,
            request.Document.CalculateEachInCount,
            request.Document.UseAll,
            request.Document.Entries.Select(item =>
                    new SkyrimLeveledListProductionEntryArtifact(
                        item.Item.ToString(), item.Level, item.Count, item.ChanceNone))
                .ToImmutableArray(),
            ["OutfitBase.esm"],
            [],
            true,
            true);

        private static SkyrimLeveledListProductionVerification Verification(
            SkyrimLeveledListProductionRequest request,
            Sha256Hash hash) => new(
            true,
            request.OutputPlugin,
            hash,
            1,
            0,
            new PluginName(Path.GetFileName(request.OutputPlugin.Value)),
            request.TargetFormId,
            true,
            true,
            true,
            true,
            []);
    }
}
