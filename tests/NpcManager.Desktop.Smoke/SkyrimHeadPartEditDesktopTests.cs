using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimHeadPartEditViewModelTest() =>
        RunSkyrimHeadPartEditViewModelTestAsync().GetAwaiter().GetResult();

    private static async Task RunSkyrimHeadPartEditViewModelTestAsync()
    {
        PluginName plugin = new("P04SSE.esp");
        FormReference race = new(plugin, new FormId(0x810));
        ImmutableArray<NpcHeadPartSelection> baseline = Enum
            .GetValues<NpcHeadPartType>()
            .Where(type => type != NpcHeadPartType.Misc)
            .Select(type => new NpcHeadPartSelection(
                new FormReference(plugin, new FormId((uint)(0x810 + (int)type))), type))
            .ToImmutableArray();
        var replacement = new SkyrimHeadPartChoiceCandidate(
            new FormReference(plugin, new FormId(0x823)),
            "ReplacementHair",
            "Replacement hair",
            NpcHeadPartType.Hair,
            false,
            false,
            true,
            null,
            [],
            [],
            new SkyrimFaceRecordProvider(
                plugin,
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\Data\\P04SSE.esp"),
                new Sha256Hash(new string('A', 64))),
            SkyrimHeadPartRaceMatchKind.RaceDefault);
        var catalogs = Enum.GetValues<NpcHeadPartType>()
            .ToImmutableDictionary(
                type => type,
                type => new SkyrimHeadPartChoiceResult(
                    true,
                    type == NpcHeadPartType.Hair ? [replacement] : [],
                    []));
        var load = new SkyrimHeadPartEditLoadResult(
            true,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\Data\\P04SSE.esp"),
            new Sha256Hash(new string('A', 64)),
            new NpcFaceSnapshot(NpcSex.Female, race, baseline, null),
            catalogs,
            []);
        var facePatch = new CompletingFacePatchService();
        using var viewModel = new SkyrimHeadPartEditViewModel(
            new CompletingHeadPartEditLoadService(load),
            facePatch,
            null,
            new WorkspacePath("K:\\ExampleWorkspace"));
        viewModel.ApplyReviewedIntake(CreateHeadPartIntake(plugin));
        viewModel.SourcePlugin = plugin.Value;
        viewModel.NpcFormId = "0x00000800";
        viewModel.OutputPlugin = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\output\\P04SSE-HeadPart.esp";

        await viewModel.LoadAsync();
        Assert(viewModel.IsLoaded && !viewModel.HasStagedChange &&
               viewModel.StagedHeadParts.SequenceEqual(baseline),
            "Head-part edit load staged an implicit mutation.");

        viewModel.SelectedType = viewModel.TypeOptions.Single(item =>
            item.Value == NpcHeadPartType.Hair);
        using (SkyrimHeadPartPickerViewModel cancelled = viewModel.CreatePicker())
        {
            cancelled.SelectedRow = cancelled.VisibleRows.Single();
            cancelled.Cancel();
            Assert(!viewModel.ApplyPicker(cancelled) &&
                   viewModel.StagedHeadParts.SequenceEqual(baseline),
                "Cancelled head-part picker mutated its parent transaction.");
        }

        using (SkyrimHeadPartPickerViewModel accepted = viewModel.CreatePicker())
        {
            accepted.SelectedRow = accepted.VisibleRows.Single();
            Assert(accepted.TryAccept() && viewModel.ApplyPicker(accepted),
                "Accepted head-part picker did not commit its typed result.");
        }
        Assert(viewModel.HasStagedChange &&
               viewModel.StagedHeadParts.Single(item =>
                   item.Type == NpcHeadPartType.Hair).Reference == replacement.Reference,
            "The parent did not retain the accepted typed hair selection.");

        await viewModel.ReviewAsync();
        Assert(viewModel.IsReviewed && facePatch.AnalyzeCalls == 1 &&
               facePatch.ApplyCalls == 0,
            "Head-part review wrote early or did not retain one proposal.");
        await viewModel.ExecuteAsync();
        Assert(viewModel.HasCompleted && facePatch.ApplyCalls == 1 &&
               viewModel.Verdict == "STATIC_PASS_RUNTIME_REQUIRED",
            "Head-part execution did not retain the independently verified result.");

        var untypedLoad = load with
        {
            Snapshot = load.Snapshot! with
            {
                HeadParts = baseline.Select(item => item with
                {
                    Type = NpcHeadPartType.Misc
                }).ToImmutableArray()
            }
        };
        using (var untypedParent = new SkyrimHeadPartEditViewModel(
                   new CompletingHeadPartEditLoadService(untypedLoad),
                   new CompletingFacePatchService(),
                   null,
                   new WorkspacePath("K:\\ExampleWorkspace")))
        {
            untypedParent.ApplyReviewedIntake(CreateHeadPartIntake(plugin));
            untypedParent.SourcePlugin = plugin.Value;
            untypedParent.NpcFormId = "0x00000800";
            await untypedParent.LoadAsync();
            using SkyrimHeadPartPickerViewModel untypedPicker = untypedParent.CreatePicker();
            untypedPicker.SelectedRow = untypedPicker.VisibleRows.Single();
            Assert(untypedPicker.TryAccept() && !untypedParent.ApplyPicker(untypedPicker) &&
                   !untypedParent.HasStagedChange,
                "A typed selection against an unclassified PNAM baseline did not fail closed.");
        }

        string pickerXaml = File.ReadAllText(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\SkyrimHeadPartPickerWindow.xaml");
        Assert(pickerXaml.Contains(
                   "TargetType=\"{x:Type GridViewColumnHeader}\"", StringComparison.Ordinal) &&
               pickerXaml.Contains(
                   "<Setter Property=\"Background\" Value=\"#13252C\" />", StringComparison.Ordinal) &&
               pickerXaml.Contains(
                   "Foreground=\"#E5B96A\"", StringComparison.Ordinal),
            "The head-part picker does not pin readable native-header and authority palettes.");
        string pickerViewModel = File.ReadAllText(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\SkyrimHeadPartPickerViewModel.cs");
        Assert(!pickerViewModel.Contains('\u00C2', StringComparison.Ordinal) &&
               !pickerViewModel.Contains('\u00C3', StringComparison.Ordinal),
            "The head-part picker exposes mojibake in packaged user-facing text.");
        string parentViewModel = File.ReadAllText(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop\SkyrimHeadPartEditViewModel.cs");
        Assert(!parentViewModel.Contains('\u00C2', StringComparison.Ordinal) &&
               !parentViewModel.Contains('\u00C3', StringComparison.Ordinal) &&
               !parentViewModel.Contains('\u00E2', StringComparison.Ordinal),
            "The head-part parent exposes mojibake in packaged user-facing text.");

        Console.WriteLine(
            "PASS production head-part edit parent loads neutrally, rolls back Cancel, reviews, and writes one typed result.");
    }

    private static ReviewedGameIntake CreateHeadPartIntake(PluginName plugin) => new(
        GameEdition.SkyrimSpecialEdition,
        new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test"),
        new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\Data"),
        new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\loadorder.txt"),
        new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\future-output"),
        new Sha256Hash(new string('B', 64)),
        [new PluginClosureReviewEntry(plugin, 0, true, true, true, false, true,
            new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\headpart-test\\Data\\P04SSE.esp"),
            new Sha256Hash(new string('A', 64)), [])],
        [], [], [], 0, new Sha256Hash(new string('C', 64)),
        new Sha256Hash(new string('D', 64)), false);

    private sealed class CompletingHeadPartEditLoadService(
        SkyrimHeadPartEditLoadResult result) : ISkyrimHeadPartEditLoadService
    {
        public ValueTask<SkyrimHeadPartEditLoadResult> LoadAsync(
            SkyrimHeadPartEditLoadRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class CompletingFacePatchService : INpcFacePatchService
    {
        public int AnalyzeCalls { get; private set; }
        public int ApplyCalls { get; private set; }

        public ValueTask<NpcFacePatchProposal> AnalyzeAsync(
            NpcFacePatchRequest request,
            CancellationToken cancellationToken)
        {
            AnalyzeCalls++;
            return ValueTask.FromResult(new NpcFacePatchProposal(
                request.Edition,
                request.InputPlugin,
                request.OutputPlugin,
                request.TargetFormId,
                request.ExpectedInputHash ?? new Sha256Hash(new string('A', 64)),
                [new MutationChange("PNAM", "baseline", "replacement")],
                ["EDID"],
                [],
                []));
        }

        public ValueTask<NpcFacePatchResult> ApplyAsync(
            NpcFacePatchRequest request,
            NpcFacePatchProposal proposal,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            return ValueTask.FromResult(new NpcFacePatchResult(
                true,
                proposal,
                new Sha256Hash(new string('E', 64)),
                []));
        }
    }
}
