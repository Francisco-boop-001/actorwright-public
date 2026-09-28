using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimLightingEditorViewModelTest()
    {
        RunSkyrimLightingEditorViewModelTestAsync().GetAwaiter().GetResult();

        string xaml = File.ReadAllText(Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation",
            "src",
            "NpcManager.Desktop",
            "SkyrimLightingEditorWindow.xaml"));
        foreach (string required in new[]
                 {
                     "Preview lighting",
                     "Off-engine schematic",
                     "_Add light",
                     "_Remove",
                     "Move _up",
                     "Move _down",
                     "_Reset rig",
                     "_Apply",
                     "_Cancel",
                     "Ambient intensity",
                     "Azimuth",
                     "Elevation",
                     "Runtime authority remains false",
                     "automation:AutomationProperties.Name"
                 })
        {
            Assert(xaml.Contains(required, StringComparison.Ordinal),
                $"The lighting editor window lacks '{required}'.");
        }

        Console.WriteLine(
            "PASS Skyrim lighting editor isolates working state, refreshes previews, validates, persists, and cancels exactly.");
    }

    private static async Task RunSkyrimLightingEditorViewModelTestAsync()
    {
        PreviewLightingPreset initial = SkyrimLightingRules.DefaultPreset;
        var service = new RecordingLightingSettingsService();
        using var viewModel = new SkyrimLightingEditorViewModel(initial, service);
        int previewEvents = 0;
        viewModel.PreviewChanged += (_, _) => previewEvents++;

        int openingRevision = viewModel.PreviewRevision;
        viewModel.AmbientText = "0.65";
        Assert(viewModel.CanApply && viewModel.PreviewRevision == openingRevision + 1 &&
               viewModel.PreviewPreset.AmbientIntensity == 0.65f && previewEvents == 1,
            "A valid ambient edit did not synchronously refresh the typed schematic state.");

        viewModel.AmbientText = "NaN";
        Assert(!viewModel.CanApply &&
               viewModel.PreviewPreset.AmbientIntensity == 0.65f &&
               viewModel.ValidationMessage.Contains("finite", StringComparison.OrdinalIgnoreCase),
            "Non-finite text escaped validation or replaced the last valid preview.");
        viewModel.AmbientText = "0.5";

        Assert(viewModel.TryAddLight() && viewModel.Lights.Count == 2 &&
               viewModel.SelectedLight == viewModel.Lights[1],
            "Add light did not create and select one unique working row.");
        viewModel.SelectedLight!.Id = "rim";
        viewModel.SelectedLight.AzimuthText = "150";
        viewModel.SelectedLight.ElevationText = "20";
        viewModel.SelectedLight.IntensityText = "0.75";
        viewModel.SelectedLight.RedText = "0.2";
        viewModel.SelectedLight.GreenText = "0.4";
        viewModel.SelectedLight.BlueText = "1";
        Assert(viewModel.CanApply &&
               viewModel.PreviewPreset.Lights[1] == new PreviewLightSource(
                   "rim", 150, 20, 0.75f, 0.2f, 0.4f, 1),
            "A complete light row did not reach the typed preview without value loss.");

        Assert(viewModel.TryMoveSelectedLight(-1) &&
               viewModel.PreviewPreset.Lights[0].Id == "rim" &&
               viewModel.SelectedLight == viewModel.Lights[0],
            "Move up did not preserve the selected light and exact order.");
        Assert(viewModel.TryRemoveSelectedLight() && viewModel.Lights.Count == 1 &&
               !viewModel.TryRemoveSelectedLight(),
            "Remove did not retain the required last light boundary.");

        viewModel.Reset();
        Assert(SkyrimLightingRules.AreEquivalent(
                   viewModel.PreviewPreset,
                   SkyrimLightingRules.DefaultPreset) &&
               viewModel.CanApply,
            "Reset did not restore the documented complete default.");

        bool applied = await viewModel.ApplyAsync();
        Assert(applied && viewModel.AcceptedPreset is not null &&
               service.SaveRequests.Count == 1 &&
               SkyrimLightingRules.AreEquivalent(
                   service.SaveRequests[0], viewModel.AcceptedPreset),
            "Apply did not persist exactly one complete accepted preset.");

        using var cancelled = new SkyrimLightingEditorViewModel(initial, service);
        cancelled.AmbientText = "1.25";
        cancelled.Cancel();
        Assert(cancelled.AcceptedPreset is null &&
               SkyrimLightingRules.AreEquivalent(initial, SkyrimLightingRules.DefaultPreset) &&
               service.SaveRequests.Count == 1,
            "Cancel leaked the working copy or wrote settings.");

        var refusingService = new RecordingLightingSettingsService
        {
            RefuseSave = true
        };
        using var refused = new SkyrimLightingEditorViewModel(initial, refusingService);
        refused.AmbientText = "0.7";
        Assert(!await refused.ApplyAsync() && refused.AcceptedPreset is null &&
               refused.ValidationMessage.Contains("refused", StringComparison.OrdinalIgnoreCase),
            "A failed settings write escaped as an accepted modal result.");
    }

    private sealed class RecordingLightingSettingsService :
        ISkyrimLightingSettingsService
    {
        public List<PreviewLightingPreset> SaveRequests { get; } = [];
        public bool RefuseSave { get; init; }

        public ValueTask<SkyrimLightingSettingsLoadResult> LoadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimLightingSettingsLoadResult(
                false,
                SkyrimLightingRules.DefaultPreset,
                null,
                []));

        public ValueTask<SkyrimLightingSettingsSaveResult> SaveAsync(
            PreviewLightingPreset preset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveRequests.Add(preset);
            return ValueTask.FromResult(RefuseSave
                ? new SkyrimLightingSettingsSaveResult(
                    false,
                    null,
                    null,
                    [new Diagnostic(
                        "lighting-settings-refused",
                        DiagnosticSeverity.Error,
                        "Fixture settings write refused.")])
                : new SkyrimLightingSettingsSaveResult(
                    true,
                    preset,
                    new Sha256Hash(new string('b', 64)),
                    ImmutableArray<Diagnostic>.Empty));
        }
    }
}
