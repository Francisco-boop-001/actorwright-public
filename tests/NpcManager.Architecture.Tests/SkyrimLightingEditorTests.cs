using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimLightingEditor()
    {
        TestSkyrimLightingRules();
        await TestSkyrimLightingSettingsService();
    }

    private static void TestSkyrimLightingRules()
    {
        PreviewLightingPreset initial = SkyrimLightingRules.DefaultPreset;
        Assert(initial.Id == "default" && initial.Version == 1 &&
               initial.AmbientIntensity == 0.2f && initial.Lights.Length == 1 &&
               SkyrimLightingRules.Validate(initial).Length == 0,
            "The documented replacement light-rig default is not complete and valid.");

        PreviewLightSource fill = new(
            "fill", 40, 5, 0.5f, 0.4f, 0.5f, 1f);
        SkyrimLightingEditResult added = SkyrimLightingRules.AddLight(initial, fill);
        Assert(added.Accepted && added.Preset is { Lights.Length: 2 } &&
               added.Preset.Lights[1] == fill,
            "A valid light was not appended with exact typed values.");

        SkyrimLightingEditResult moved = SkyrimLightingRules.MoveLight(
            added.Preset!, 1, -1);
        Assert(moved.Accepted && moved.Preset!.Lights.SequenceEqual(
                   ImmutableArray.Create(fill, initial.Lights[0])),
            "Light reordering did not preserve exact ordered identities.");

        PreviewLightSource changed = fill with
        {
            AzimuthDegrees = -120,
            ElevationDegrees = 60,
            Intensity = 7.5f,
            Red = 0.1f,
            Green = 0.2f,
            Blue = 0.3f
        };
        SkyrimLightingEditResult replaced = SkyrimLightingRules.ReplaceLight(
            moved.Preset!, 0, changed);
        Assert(replaced.Accepted && replaced.Preset!.Lights[0] == changed,
            "A complete valid light edit lost a bounded value.");

        SkyrimLightingEditResult removed = SkyrimLightingRules.RemoveLight(
            replaced.Preset!, 0);
        Assert(removed.Accepted && removed.Preset!.Lights.SequenceEqual(initial.Lights),
            "Removing a light did not retain the unaffected ordered light.");
        Assert(!SkyrimLightingRules.RemoveLight(initial, 0).Accepted,
            "The editor allowed an empty light rig.");
        Assert(!SkyrimLightingRules.AddLight(initial, initial.Lights[0]).Accepted,
            "The editor allowed a duplicate case-insensitive light ID.");

        PreviewLightingPreset invalid = initial with
        {
            AmbientIntensity = float.NaN,
            Lights = [initial.Lights[0] with { Intensity = float.PositiveInfinity }]
        };
        ImmutableArray<Diagnostic> diagnostics = SkyrimLightingRules.Validate(invalid);
        Assert(diagnostics.Any(item => item.Code == "lighting-ambient-invalid") &&
               diagnostics.Any(item => item.Code == "lighting-intensity-invalid"),
            "Non-finite lighting values did not fail closed with typed diagnostics.");
    }

    private static async Task TestSkyrimLightingSettingsService()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string parent = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-026-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string settingsPath = Path.Combine(parent, "preview-lighting.json");
        try
        {
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var service = new SkyrimLightingSettingsService(
                policy, labRoot, new WorkspacePath(settingsPath));

            SkyrimLightingSettingsLoadResult missing = await service.LoadAsync(
                CancellationToken.None);
            Assert(!missing.LoadedFromDisk &&
                   missing.Preset == SkyrimLightingRules.DefaultPreset &&
                   !missing.Diagnostics.Any(item =>
                       item.Severity == DiagnosticSeverity.Error),
                "Absent settings did not produce the documented in-memory default.");

            PreviewLightingPreset accepted = SkyrimLightingRules.DefaultPreset with
            {
                Id = "portrait",
                Version = 4,
                AmbientIntensity = 0.45f,
                Lights =
                [
                    new PreviewLightSource(
                        "key", -20, 35, 1.5f, 1, 0.8f, 0.6f),
                    new PreviewLightSource(
                        "rim", 155, 15, 0.8f, 0.3f, 0.5f, 1)
                ]
            };
            SkyrimLightingSettingsSaveResult saved = await service.SaveAsync(
                accepted, CancellationToken.None);
            Assert(saved.Saved && saved.Preset is not null &&
                   SkyrimLightingRules.AreEquivalent(saved.Preset, accepted) &&
                   File.Exists(settingsPath) && saved.Sha256 is not null,
                "A valid K-local light rig was not atomically persisted.");

            SkyrimLightingSettingsLoadResult reopened = await service.LoadAsync(
                CancellationToken.None);
            Assert(reopened.LoadedFromDisk &&
                   SkyrimLightingRules.AreEquivalent(reopened.Preset, accepted) &&
                   reopened.Sha256 == saved.Sha256,
                "Persisted lighting settings did not reopen with exact fields and hash.");

            const string malformed = "{\"schemaVersion\":1,\"preset\":false}";
            await File.WriteAllTextAsync(settingsPath, malformed);
            SkyrimLightingSettingsLoadResult refused = await service.LoadAsync(
                CancellationToken.None);
            Assert(!refused.LoadedFromDisk &&
                   refused.Preset == SkyrimLightingRules.DefaultPreset &&
                   refused.Diagnostics.Any(item =>
                       item.Severity == DiagnosticSeverity.Error) &&
                   await File.ReadAllTextAsync(settingsPath) == malformed,
                "Malformed settings were trusted, hidden, or rewritten during fail-closed load.");
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }
}
