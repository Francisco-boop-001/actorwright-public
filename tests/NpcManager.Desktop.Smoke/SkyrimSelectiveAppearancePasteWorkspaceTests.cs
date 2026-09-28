using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimSelectiveAppearancePasteWorkspaceViewModelTest() =>
        RunSkyrimSelectiveAppearancePasteWorkspaceViewModelTestAsync()
            .GetAwaiter().GetResult();

    private static async Task RunSkyrimSelectiveAppearancePasteWorkspaceViewModelTestAsync()
    {
        var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
        string fixtureRoot = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "tests", "fixtures",
            "skyrim-production");
        string presetRoot = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "tools", "fixtures",
            "sky-gui-014");
        var plugin = new PluginName("SelectivePasteFixtureSSE.esp");
        var pluginPath = new WorkspacePath(Path.Combine(fixtureRoot, plugin.Value));
        var sourcePreset = new WorkspacePath(Path.Combine(presetRoot, "source.jslot"));
        var targetPreset = new WorkspacePath(Path.Combine(presetRoot, "target.jslot"));
        Sha256Hash pluginHash = HashSelectivePasteFile(pluginPath.Value);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(fixtureRoot),
            new WorkspacePath(Path.Combine(fixtureRoot, "load-order.json")),
            new WorkspacePath(Path.Combine(fixtureRoot, "future-output")),
            new Sha256Hash(new string('1', 64)),
            [
                new PluginClosureReviewEntry(
                    plugin, 0, true, true, true, false, true,
                    pluginPath, pluginHash, [])
            ],
            [], [], [], 0,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath("F:\\ExampleGame"));
        var authorityLoader = new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var faceLoader = new SkyrimFaceEditLoadService(
            new SkyrimHeadPartEditLoadService(
                new BethesdaSkyrimHeadPartChoiceService(authorityLoader)),
            new FormChoiceService(new BethesdaPluginReader(), policy, labRoot),
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimFaceEditSourceReader());
        var presets = new PresetService(policy, labRoot);
        var loader = new SkyrimSelectiveAppearancePasteLoadService(
            faceLoader,
            presets,
            new BethesdaSkyrimSelectiveAppearanceNpcStateReader());
        var transaction = new SkyrimSelectiveAppearancePasteTransactionService(
            new NpcAppearanceOverrideService(policy, labRoot),
            presets,
            presets,
            policy,
            labRoot);
        string root = Path.Combine(
            labRoot.Value,
            "projects", "NpcManagerReimplementation", "03-builds", "work",
            $"selective-paste-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var viewModel = new SkyrimSelectiveAppearancePasteWorkspaceViewModel(
                loader,
                transaction,
                labRoot);
            viewModel.ApplyReviewedIntake(intake);
            viewModel.SourcePlugin = plugin.Value;
            viewModel.SourceNpcFormId = "0x00000900";
            viewModel.SourcePreset = sourcePreset.Value;
            viewModel.TargetPlugin = plugin.Value;
            viewModel.TargetNpcFormId = "0x00000800";
            viewModel.TargetPreset = targetPreset.Value;
            viewModel.TransactionProposalPath = Path.Combine(root, "transaction.proposal.json");
            viewModel.PluginProposalPath = Path.Combine(root, "plugin.proposal.json");
            viewModel.OutputPlugin = Path.Combine(root, "SelectivePasteDesktop.esp");
            viewModel.OutputPreset = Path.Combine(root, "SelectivePasteDesktop.jslot");

            await viewModel.LoadAsync();
            Assert(viewModel.IsLoaded && !viewModel.HasSelection &&
                   viewModel.Context.Contains("SelectivePasteSourceNpcSSE", StringComparison.Ordinal) &&
                   viewModel.Context.Contains("SelectivePasteTargetNpcSSE", StringComparison.Ordinal),
                "The production selective-paste parent did not load both exact endpoints: " +
                $"loaded={viewModel.IsLoaded} verdict={viewModel.Verdict} " +
                $"status={viewModel.Status} context={viewModel.Context} diagnostics=" +
                string.Join(" | ", viewModel.Diagnostics));

            SkyrimSelectiveAppearancePasteViewModel none = viewModel.CreateSelector();
            none.DeselectAll();
            Assert(none.TryAccept() && viewModel.ApplySelector(none) &&
                   !viewModel.HasSelection && !viewModel.ReviewCommand.CanExecute(null) &&
                   !File.Exists(viewModel.OutputPlugin) &&
                   !File.Exists(viewModel.OutputPreset),
                "Select None did not remain a clean production no-op.");

            SkyrimSelectiveAppearancePasteViewModel partial = viewModel.CreateSelector();
            foreach (SkyrimSelectiveAppearancePasteOptionViewModel option in partial.Options)
            {
                if (option.Category is
                    SkyrimAppearancePasteCategory.RaceMenuPaintsAndSkin or
                    SkyrimAppearancePasteCategory.HairColor or
                    SkyrimAppearancePasteCategory.FaceTints or
                    SkyrimAppearancePasteCategory.FaceMorphs)
                {
                    option.IsSelected = false;
                }
            }
            Assert(partial.TryAccept() && viewModel.ApplySelector(partial) &&
                   viewModel.HasSelection && partial.AcceptedSelection is
                   { Categories.Length: 6 },
                "The accepted partial selection did not reach the production parent.");

            await viewModel.ReviewAsync();
            Assert(viewModel.IsReviewed &&
                   File.Exists(viewModel.TransactionProposalPath) &&
                   File.Exists(viewModel.PluginProposalPath) &&
                   !File.Exists(viewModel.OutputPlugin) &&
                   !File.Exists(viewModel.OutputPreset),
                "Production review wrote early or omitted a persisted proposal: " +
                string.Join(" | ", viewModel.Diagnostics));

            await viewModel.ExecuteAsync();
            Assert(viewModel.HasCompleted &&
                   viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED" &&
                   File.Exists(viewModel.OutputPlugin) &&
                   File.Exists(viewModel.OutputPreset) &&
                   viewModel.OutputPresetSha256 ==
                       HashSelectivePasteFile(viewModel.OutputPreset).Value,
                "The production desktop parent did not write and explicitly verify both carriers: " +
                string.Join(" | ", viewModel.Diagnostics));

            string panel = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "SkyrimSelectiveAppearancePastePanel.xaml"));
            string mainWindow = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects", "NpcManagerReimplementation", "src",
                "NpcManager.Desktop", "MainWindow.xaml"));
            Assert(panel.Contains(
                       "AutomationProperties.Name=\"Load selective appearance source and target\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Open selective appearance category chooser\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Review selective appearance proposals\"",
                       StringComparison.Ordinal) &&
                   panel.Contains(
                       "AutomationProperties.Name=\"Write and verify selective appearance outputs\"",
                       StringComparison.Ordinal) &&
                   panel.Contains("runtime, or visual proof", StringComparison.Ordinal) &&
                   mainWindow.Contains("Header=\"Paste appearance\"", StringComparison.Ordinal),
                "The production panel or shell tab is missing accessible actions or authority warnings.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine(
            "PASS production selective-paste workspace proves no-op, proposal-only review, atomic write, and dual readback.");
    }

    private static Sha256Hash HashSelectivePasteFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }
}
