using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimLightingPreviewChangedEventArgs(
    PreviewLightingPreset preset,
    int revision) : EventArgs
{
    public PreviewLightingPreset Preset { get; } = preset;
    public int Revision { get; } = revision;
}

public sealed class SkyrimLightingEditorViewModel : NotifyViewModel, IDisposable
{
    private readonly ISkyrimLightingSettingsService settingsService;
    private CancellationTokenSource? saveCancellation;
    private bool suppressEdits;
    private bool isValid;
    private bool disposed;
    private string presetId = string.Empty;
    private string versionText = string.Empty;
    private string ambientText = string.Empty;
    private SkyrimLightingLightRowViewModel? selectedLight;
    private PreviewLightingPreset previewPreset = SkyrimLightingRules.DefaultPreset;
    private int previewRevision;
    private string validationMessage = string.Empty;
    private string statusMessage = "Edit any valid value to refresh the schematic immediately.";
    private bool isBusy;

    public SkyrimLightingEditorViewModel(
        PreviewLightingPreset initialPreset,
        ISkyrimLightingSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(initialPreset);
        this.settingsService = settingsService ??
            throw new ArgumentNullException(nameof(settingsService));
        if (SkyrimLightingRules.Validate(initialPreset).Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            throw new ArgumentException(
                "The opening preview-lighting preset must be complete and valid.",
                nameof(initialPreset));
        }
        LoadPreset(initialPreset, "Loaded the accepted preview-lighting rig.");
    }

    public ObservableCollection<SkyrimLightingLightRowViewModel> Lights { get; } = [];

    public string PresetId
    {
        get => presetId;
        set
        {
            if (!Set(ref presetId, value)) return;
            RebuildPreview();
        }
    }

    public string VersionText
    {
        get => versionText;
        set
        {
            if (!Set(ref versionText, value)) return;
            RebuildPreview();
        }
    }

    public string AmbientText
    {
        get => ambientText;
        set
        {
            if (!Set(ref ambientText, value)) return;
            RebuildPreview();
        }
    }

    public SkyrimLightingLightRowViewModel? SelectedLight
    {
        get => selectedLight;
        set
        {
            if (!Set(ref selectedLight, value)) return;
            RaiseSelectionState();
        }
    }

    public PreviewLightingPreset PreviewPreset
    {
        get => previewPreset;
        private set
        {
            previewPreset = value;
            Raise(nameof(PreviewPreset));
            Raise(nameof(PreviewSummary));
        }
    }

    public int PreviewRevision
    {
        get => previewRevision;
        private set => Set(ref previewRevision, value);
    }

    public string PreviewSummary =>
        $"{PreviewPreset.Lights.Length} light(s) · ambient " +
        PreviewPreset.AmbientIntensity.ToString("0.###", CultureInfo.InvariantCulture) +
        $" · revision {PreviewRevision}";

    public string PreviewAuthority { get; } =
        "Off-engine state schematic only. Runtime authority remains false.";

    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => Set(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            Raise(nameof(IsNotBusy));
            Raise(nameof(CanApply));
            RaiseSelectionState();
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool CanApply => !IsBusy && isValid;
    public bool CanRemove => IsNotBusy && Lights.Count > 1 && SelectedLight is not null;
    public bool CanMoveUp => IsNotBusy && SelectedLight is not null &&
                             Lights.IndexOf(SelectedLight) > 0;
    public bool CanMoveDown => IsNotBusy && SelectedLight is not null &&
                               Lights.IndexOf(SelectedLight) is var index &&
                               index >= 0 && index < Lights.Count - 1;
    public PreviewLightingPreset? AcceptedPreset { get; private set; }
    public Sha256Hash? SavedSha256 { get; private set; }

    public event EventHandler<SkyrimLightingPreviewChangedEventArgs>? PreviewChanged;

    public bool TryAddLight()
    {
        if (IsBusy || Lights.Count >= SkyrimLightingRules.MaximumLights) return false;
        int suffix = 2;
        string id;
        do id = "light" + suffix++.ToString(CultureInfo.InvariantCulture);
        while (Lights.Any(item => string.Equals(
            item.Id, id, StringComparison.OrdinalIgnoreCase)));
        var row = new SkyrimLightingLightRowViewModel(
            new PreviewLightSource(id, 35, 10, 0.5f, 1, 1, 1));
        Attach(row);
        Lights.Add(row);
        SelectedLight = row;
        RebuildPreview();
        return isValid;
    }

    public bool TryRemoveSelectedLight()
    {
        if (!CanRemove || SelectedLight is null) return false;
        int index = Lights.IndexOf(SelectedLight);
        Detach(SelectedLight);
        Lights.RemoveAt(index);
        SelectedLight = Lights[Math.Min(index, Lights.Count - 1)];
        RebuildPreview();
        return isValid;
    }

    public bool TryMoveSelectedLight(int offset)
    {
        if (IsBusy || SelectedLight is null) return false;
        int index = Lights.IndexOf(SelectedLight);
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= Lights.Count) return false;
        SkyrimLightingLightRowViewModel selected = SelectedLight;
        Lights.Move(index, destination);
        SelectedLight = selected;
        RebuildPreview();
        return isValid;
    }

    public void Reset() =>
        LoadPreset(
            SkyrimLightingRules.DefaultPreset,
            "The complete preview-lighting rig was reset to the documented product default.");

    public async Task<bool> ApplyAsync()
    {
        if (!CanApply) return false;
        AcceptedPreset = null;
        SavedSha256 = null;
        var source = new CancellationTokenSource();
        saveCancellation = source;
        IsBusy = true;
        StatusMessage = "Persisting and reopening the complete K-local light rig...";
        try
        {
            SkyrimLightingSettingsSaveResult result = await settingsService.SaveAsync(
                PreviewPreset, source.Token);
            if (!result.Saved || result.Preset is null || result.Sha256 is null ||
                !SkyrimLightingRules.AreEquivalent(result.Preset, PreviewPreset))
            {
                Diagnostic? error = result.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error);
                ValidationMessage = error is null
                    ? "Save refused: exact lighting-settings readback was unavailable."
                    : $"Save refused: {error.Code}: {error.Message}";
                StatusMessage = "The accepted shell lighting was not changed.";
                return false;
            }
            AcceptedPreset = result.Preset;
            SavedSha256 = result.Sha256;
            ValidationMessage = string.Empty;
            StatusMessage =
                $"Saved and reopened {result.Preset.Lights.Length} light(s).";
            return true;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            ValidationMessage = "Save cancelled; the accepted shell lighting was not changed.";
            StatusMessage = "Lighting-settings save cancelled.";
            return false;
        }
        finally
        {
            if (ReferenceEquals(saveCancellation, source)) saveCancellation = null;
            source.Dispose();
            IsBusy = false;
        }
    }

    public void Cancel()
    {
        if (saveCancellation is { } source)
        {
            source.Cancel();
            return;
        }
        AcceptedPreset = null;
        SavedSha256 = null;
        StatusMessage = "Lighting edit cancelled; the accepted shell rig is unchanged.";
    }

    private void LoadPreset(PreviewLightingPreset preset, string status)
    {
        suppressEdits = true;
        try
        {
            foreach (SkyrimLightingLightRowViewModel row in Lights) Detach(row);
            Lights.Clear();
            presetId = preset.Id;
            versionText = preset.Version.ToString(CultureInfo.InvariantCulture);
            ambientText = preset.AmbientIntensity.ToString("R", CultureInfo.InvariantCulture);
            foreach (PreviewLightSource light in preset.Lights)
            {
                var row = new SkyrimLightingLightRowViewModel(light);
                Attach(row);
                Lights.Add(row);
            }
            selectedLight = Lights[0];
            Raise(nameof(PresetId));
            Raise(nameof(VersionText));
            Raise(nameof(AmbientText));
            Raise(nameof(SelectedLight));
        }
        finally
        {
            suppressEdits = false;
        }
        RebuildPreview();
        StatusMessage = status;
        RaiseSelectionState();
    }

    private void RebuildPreview()
    {
        if (suppressEdits || disposed) return;
        if (!int.TryParse(
                VersionText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int version))
        {
            RefuseWorkingCopy("Preset version must be a whole number from 1 to 1000.");
            return;
        }
        if (!TryParseFloat(AmbientText, out float ambient))
        {
            RefuseWorkingCopy("Ambient intensity must be a finite number from 0 to 4.");
            return;
        }

        var lights = new List<PreviewLightSource>(Lights.Count);
        foreach (SkyrimLightingLightRowViewModel row in Lights)
        {
            if (!row.TryCreate(out PreviewLightSource? light, out string? error))
            {
                RefuseWorkingCopy(error ?? "A light contains invalid numeric text.");
                return;
            }
            lights.Add(light!);
        }
        var candidate = new PreviewLightingPreset(
            PresetId,
            version,
            ambient,
            [.. lights]);
        Diagnostic? validation = SkyrimLightingRules.Validate(candidate)
            .FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (validation is not null)
        {
            RefuseWorkingCopy(validation.Message);
            return;
        }

        isValid = true;
        ValidationMessage = string.Empty;
        PreviewPreset = candidate;
        PreviewRevision++;
        StatusMessage = "Preview refreshed from the complete valid working light rig.";
        Raise(nameof(CanApply));
        RaiseSelectionState();
        PreviewChanged?.Invoke(
            this,
            new SkyrimLightingPreviewChangedEventArgs(candidate, PreviewRevision));
    }

    private void RefuseWorkingCopy(string message)
    {
        isValid = false;
        ValidationMessage = message;
        StatusMessage = "Fix the invalid field; the schematic retains the last valid rig.";
        Raise(nameof(CanApply));
    }

    private void OnRowEdited(object? sender, EventArgs eventArgs) => RebuildPreview();

    private void Attach(SkyrimLightingLightRowViewModel row) =>
        row.Edited += OnRowEdited;

    private void Detach(SkyrimLightingLightRowViewModel row) =>
        row.Edited -= OnRowEdited;

    private void RaiseSelectionState()
    {
        Raise(nameof(CanRemove));
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
    }

    internal static bool TryParseFloat(string text, out float value) =>
        float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && float.IsFinite(value);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (saveCancellation is { } source)
        {
            source.Cancel();
            source.Dispose();
            saveCancellation = null;
        }
        foreach (SkyrimLightingLightRowViewModel row in Lights) Detach(row);
    }
}

public sealed class SkyrimLightingLightRowViewModel : NotifyViewModel
{
    private string id;
    private string azimuthText;
    private string elevationText;
    private string intensityText;
    private string redText;
    private string greenText;
    private string blueText;

    public SkyrimLightingLightRowViewModel(PreviewLightSource light)
    {
        id = light.Id;
        azimuthText = Format(light.AzimuthDegrees);
        elevationText = Format(light.ElevationDegrees);
        intensityText = Format(light.Intensity);
        redText = Format(light.Red);
        greenText = Format(light.Green);
        blueText = Format(light.Blue);
    }

    public string Id { get => id; set => SetAndEdit(ref id, value); }
    public string AzimuthText { get => azimuthText; set => SetAndEdit(ref azimuthText, value); }
    public string ElevationText { get => elevationText; set => SetAndEdit(ref elevationText, value); }
    public string IntensityText { get => intensityText; set => SetAndEdit(ref intensityText, value); }
    public string RedText { get => redText; set => SetAndEdit(ref redText, value); }
    public string GreenText { get => greenText; set => SetAndEdit(ref greenText, value); }
    public string BlueText { get => blueText; set => SetAndEdit(ref blueText, value); }

    public event EventHandler? Edited;

    public bool TryCreate(out PreviewLightSource? light, out string? error)
    {
        light = null;
        error = null;
        float azimuth;
        float elevation = 0;
        float intensity = 0;
        float red = 0;
        float green = 0;
        float blue = 0;
        if (!SkyrimLightingEditorViewModel.TryParseFloat(AzimuthText, out azimuth))
            error = $"Light '{Id}' azimuth must be a finite number from -180 to 180.";
        else if (!SkyrimLightingEditorViewModel.TryParseFloat(ElevationText, out elevation))
            error = $"Light '{Id}' elevation must be a finite number from -90 to 90.";
        else if (!SkyrimLightingEditorViewModel.TryParseFloat(IntensityText, out intensity))
            error = $"Light '{Id}' intensity must be a finite number from 0 to 8.";
        else if (!SkyrimLightingEditorViewModel.TryParseFloat(RedText, out red))
            error = $"Light '{Id}' red channel must be a finite number from 0 to 1.";
        else if (!SkyrimLightingEditorViewModel.TryParseFloat(GreenText, out green))
            error = $"Light '{Id}' green channel must be a finite number from 0 to 1.";
        else if (!SkyrimLightingEditorViewModel.TryParseFloat(BlueText, out blue))
            error = $"Light '{Id}' blue channel must be a finite number from 0 to 1.";
        if (error is not null) return false;
        light = new PreviewLightSource(
            Id, azimuth, elevation, intensity, red, green, blue);
        return true;
    }

    private void SetAndEdit(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
