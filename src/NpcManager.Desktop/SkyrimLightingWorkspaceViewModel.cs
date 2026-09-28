using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimLightingWorkspaceViewModel(
    ISkyrimLightingSettingsService settingsService) : NotifyViewModel
{
    private PreviewLightingPreset currentPreset = SkyrimLightingRules.DefaultPreset;
    private Sha256Hash? settingsSha256;
    private string status = "Preview lighting has not been loaded yet.";
    private bool isBusy;

    public PreviewLightingPreset CurrentPreset
    {
        get => currentPreset;
        private set
        {
            currentPreset = value;
            Raise(nameof(CurrentPreset));
            Raise(nameof(Summary));
        }
    }

    public Sha256Hash? SettingsSha256
    {
        get => settingsSha256;
        private set
        {
            settingsSha256 = value;
            Raise(nameof(SettingsSha256));
            Raise(nameof(SettingsAuthority));
        }
    }

    public string Summary =>
        $"{CurrentPreset.Lights.Length} light(s) · ambient " +
        CurrentPreset.AmbientIntensity.ToString("0.###", CultureInfo.InvariantCulture);

    public string SettingsAuthority => SettingsSha256 is null
        ? "Documented in-memory default"
        : "K-local settings " + SettingsSha256.Value.Value[..12] + "…";

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => Set(ref isBusy, value);
    }

    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            SkyrimLightingSettingsLoadResult result = await settingsService.LoadAsync(
                CancellationToken.None);
            CurrentPreset = result.Preset;
            SettingsSha256 = result.Sha256;
            Diagnostic? error = result.Diagnostics.FirstOrDefault(item =>
                item.Severity == DiagnosticSeverity.Error);
            Status = error is null
                ? result.LoadedFromDisk
                    ? "Saved preview lighting loaded and validated."
                    : "Using the documented preview-lighting default."
                : $"Saved lighting refused: {error.Code}: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public SkyrimLightingEditorViewModel CreateEditor() =>
        new(CurrentPreset, settingsService);

    public void Commit(SkyrimLightingEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.AcceptedPreset is null || editor.SavedSha256 is null)
            throw new InvalidOperationException(
                "Only an independently saved and reopened light rig can become shell state.");
        CurrentPreset = editor.AcceptedPreset;
        SettingsSha256 = editor.SavedSha256;
        Status = "Preview lighting applied from exact persisted settings.";
    }
}
