using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimSavePackageViewModelTest()
    {
        RunSkyrimSavePackageViewModelTestAsync().GetAwaiter().GetResult();

        string xaml = File.ReadAllText(Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src",
            "NpcManager.Desktop",
            "SkyrimSavePackagePanel.xaml"));
        foreach (string required in new[]
                 {
                     "SourceRootTextBox",
                     "OutputRootTextBox",
                     "_Choose source",
                     "Choose destination _parent",
                     "_Review package",
                     "_Apply verified choices",
                     "_Cancel operation",
                     "Update existing",
                     "All changed",
                     "ESM/ESL",
                     "LVLN",
                     "Runtime authority remains false",
                     "automation:AutomationProperties.Name"
                 })
            Assert(xaml.Contains(required, StringComparison.Ordinal),
                $"The save-package production panel lacks '{required}'.");
        Console.WriteLine(
            "PASS Skyrim save-package task reviews, invalidates, promotes, cancels, and exposes explicit parity boundaries.");
    }

    private static async Task RunSkyrimSavePackageViewModelTestAsync()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        WorkspacePath source = new(Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "gate23-desktop-source"));
        WorkspacePath output = new(Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "gate23-desktop-output"));
        var service = new CompletingSkyrimSavePackageService();
        using var viewModel = new SkyrimSavePackageViewModel(
            service,
            labRoot,
            source,
            output);
        await viewModel.ReviewAsync();
        Assert(viewModel.IsReviewed &&
               viewModel.OutputPlugin == "Gate23Desktop.esp" &&
               viewModel.TargetFormId == "0x00000800" &&
               viewModel.NpcRecordCount == 1 &&
               viewModel.HasFaceGeom &&
               viewModel.HasFaceTint &&
               viewModel.HasBodyGen &&
               viewModel.HasApplyScript &&
               viewModel.ScopeText == "Selected only (1 NPC in source)" &&
               viewModel.TargetText == "Fresh package",
            "The desktop review lost exact verified package facts.");

        viewModel.SelectedScope = SkyrimSaveScope.AllChanged;
        viewModel.SelectedTargetMode =
            SkyrimSaveTargetMode.UpdateExisting;
        viewModel.SelectedEncodingMode =
            SkyrimSaveEncodingMode.Utf8;
        viewModel.SelectedArchiveMode =
            SkyrimSaveArchiveMode.Bsa;
        viewModel.SelectedLeveledListMode =
            SkyrimSaveLeveledListMode.New;
        viewModel.LeveledListEditorId = "Gate23DesktopActors";
        viewModel.NoDuplicateLeveledEntries = true;
        Assert(!viewModel.IsReviewed,
            "Changing a save choice did not invalidate the bound proposal.");
        await viewModel.ReviewAsync();
        Assert(viewModel.IsReviewed &&
               viewModel.ScopeText == "All changed (1 NPC)" &&
               viewModel.TargetText.StartsWith(
                   "Fresh-derived update",
                   StringComparison.Ordinal),
            "The desktop review did not bind the typed production choices.");

        viewModel.OutputRoot = output.Value + "-changed";
        Assert(!viewModel.IsReviewed && viewModel.ReviewedManifestSha256 == "—",
            "Changing a bound path did not invalidate the package review.");
        await viewModel.ReviewAsync();
        await viewModel.PromoteAsync();
        Assert(viewModel.HasCompleted &&
               viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
               viewModel.ProgressPercent == 100 &&
               viewModel.ResultRoot == output.Value + "-changed" &&
               viewModel.ResultManifestSha256 == new string('a', 64),
            "The desktop did not retain the verified promotion result.");

        var blocking = new CompletingSkyrimSavePackageService
        {
            BlockExecutionUntilCancelled = true
        };
        using var cancelled = new SkyrimSavePackageViewModel(
            blocking,
            labRoot,
            source,
            new WorkspacePath(output.Value + "-cancelled"));
        await cancelled.ReviewAsync();
        Task promotion = cancelled.PromoteAsync();
        Assert(cancelled.IsBusy && cancelled.CancelCommand.CanExecute(null),
            "The active save-package task did not expose one cancellation action.");
        cancelled.CancelCommand.Execute(null);
        await promotion;
        Assert(!cancelled.HasCompleted && cancelled.Verdict == "Cancelled" &&
               cancelled.ProgressPercent == 0,
            "Cancellation retained a successful package result.");
    }

    private sealed class CompletingSkyrimSavePackageService :
        ISkyrimSavePackageService
    {
        private static readonly Sha256Hash Hash = new(new string('a', 64));

        public bool BlockExecutionUntilCancelled { get; init; }

        public ValueTask<SkyrimSavePackageReviewResult> ReviewAsync(
            SkyrimSavePackageReviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkyrimSavePackageSnapshot snapshot = Snapshot();
            SkyrimSavePackageOptions options =
                SkyrimSavePackageRules.CreateOptions(
                    snapshot,
                    request.Choices);
            var artifact = new SkyrimSavePackageReviewArtifact(
                "1",
                "skyrim-save-package-review",
                request.SourceRoot,
                request.OutputRoot,
                new WorkspacePath(Path.Combine(
                    request.SourceRoot.Value,
                    "npcmanager-package.json")),
                Hash,
                snapshot,
                options,
                true,
                false);
            return ValueTask.FromResult(new SkyrimSavePackageReviewResult(
                true,
                artifact,
                null,
                []));
        }

        public async ValueTask<SkyrimSavePackageExecutionResult> ExecuteAsync(
            SkyrimSavePackageExecutionRequest request,
            IProgress<SkyrimSavePackageProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new SkyrimSavePackageProgress(
                SkyrimSavePackageStage.TransformAssets,
                50,
                "Producing fixture package."));
            if (BlockExecutionUntilCancelled)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var comparison = new SkyrimSavePackageFileComparison(
                new AssetPath("Gate23Desktop.esp"),
                100,
                100,
                Hash,
                Hash,
                true);
            var artifact = new SkyrimSavePackageExecutionArtifact(
                "1",
                "skyrim-save-package-promotion",
                request.Review.SourceRoot,
                request.Review.OutputRoot,
                Hash,
                Hash,
                request.Review.Snapshot.OutputPlugin,
                request.Review.Snapshot.TargetFormId,
                [comparison],
                true,
                false);
            return new SkyrimSavePackageExecutionResult(
                true,
                artifact,
                null,
                []);
        }

        private static SkyrimSavePackageSnapshot Snapshot() =>
            new(
                new PluginName("Gate23Desktop.esp"),
                new FormId(0x800),
                1,
                false,
                false,
                true,
                true,
                false,
                true,
                true,
                false,
                2,
                0,
                [
                    new PackageManifestFile(
                        "plugin",
                        new AssetPath("Gate23Desktop.esp"),
                        100,
                        Hash)
                ]);
    }
}
