using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record RaceMenuPresetSelection(
    WorkspacePath SourcePath,
    Sha256Hash SourceSha256,
    bool ApplyBodySlide,
    PresetDocument? Document = null,
    RaceMenuPresetCompanionExport? Companion = null,
    RaceMenuPresetTarget? Target = null);

public sealed class RaceMenuPresetLoaderViewModel(
    IRaceMenuPresetCatalogService catalogService,
    WorkspacePath presetDirectory,
    RaceMenuPresetTarget? target,
    IRaceMenuPresetPreviewService? previewService = null,
    RaceMenuPresetSelection? preparedSelection = null) : INotifyPropertyChanged, IDisposable
{
    private readonly List<RaceMenuPresetCatalogEntryViewModel> allEntries = [];
    private CancellationTokenSource? previewCancellation;
    private Task previewTask = Task.CompletedTask;
    private int previewVersion;
    private bool disposed;
    private RaceMenuPresetCompanionExport? selectedCompanion;

    public ObservableCollection<RaceMenuPresetCatalogEntryViewModel> VisibleEntries { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public string PresetDirectory { get; } = presetDirectory.Value;
    public string TargetSummary { get; } = target is null
        ? "No reviewed race authority — compatibility filter unavailable"
        : $"{target.Race} · {target.Sex} · {target.PluginOrder.Length} copied plugins";

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (!Set(ref filterText, value)) return;
            RefreshVisibleEntries();
        }
    }

    private bool compatibleOnly;
    public bool CompatibleOnly
    {
        get => compatibleOnly;
        set
        {
            if (!CompatibilityFilterAvailable && value) return;
            if (!Set(ref compatibleOnly, value)) return;
            RefreshVisibleEntries();
        }
    }

    private bool compatibilityFilterAvailable;
    public bool CompatibilityFilterAvailable
    {
        get => compatibilityFilterAvailable;
        private set
        {
            if (!Set(ref compatibilityFilterAvailable, value)) return;
            OnPropertyChanged(nameof(CompatibilityHelp));
        }
    }

    public string CompatibilityHelp => CompatibilityFilterAvailable
        ? "Uses the exact reviewed RACE, HDPT, FLST, plugin order, and SHA-256 authorities."
        : "Disabled until every admitted preset has complete copied-provider race authority.";

    private RaceMenuPresetCatalogEntryViewModel? selectedEntry;
    public RaceMenuPresetCatalogEntryViewModel? SelectedEntry
    {
        get => selectedEntry;
        set
        {
            if (!Set(ref selectedEntry, value)) return;
            OnPropertyChanged(nameof(CanAccept));
            OnPropertyChanged(nameof(DetailsPromptVisible));
            OnPropertyChanged(nameof(SelectionHelp));
            SchedulePreview();
        }
    }

    private bool applyBodySlide = preparedSelection?.ApplyBodySlide ?? true;
    public bool ApplyBodySlide
    {
        get => applyBodySlide;
        set
        {
            if (!Set(ref applyBodySlide, value)) return;
            OnPropertyChanged(nameof(PreviewScope));
            SchedulePreview();
        }
    }

    private bool isPreviewBusy;
    public bool IsPreviewBusy
    {
        get => isPreviewBusy;
        private set
        {
            if (!Set(ref isPreviewBusy, value)) return;
            OnPropertyChanged(nameof(CanAccept));
            OnPropertyChanged(nameof(SelectionHelp));
        }
    }

    private string? previewImagePath;
    public string? PreviewImagePath
    {
        get => previewImagePath;
        private set
        {
            if (!Set(ref previewImagePath, value)) return;
            OnPropertyChanged(nameof(HasPreviewImage));
        }
    }

    public bool HasPreviewImage => !string.IsNullOrWhiteSpace(PreviewImagePath);

    private string previewStatus =
        "Select a preset to render its same-stem CharGen head export.";
    public string PreviewStatus
    {
        get => previewStatus;
        private set => Set(ref previewStatus, value);
    }

    private string previewAuthority =
        "Off-engine staging preview only — Skyrim runtime authority is false.";
    public string PreviewAuthority
    {
        get => previewAuthority;
        private set => Set(ref previewAuthority, value);
    }

    public string PreviewScope => ApplyBodySlide
        ? "CharGen head preview · BodySlide will apply in the accepted transaction"
        : "CharGen head preview · BodySlide will be omitted by operator choice";

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanAccept));
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool CanAccept => !IsBusy && !IsPreviewBusy &&
                             SelectedEntry is
                             {
                                 Compatibility: RaceMenuPresetCompatibilityKind.Compatible
                             } entry &&
                             selectedCompanion is { } companion &&
                             companion.Preset == entry.SourcePath &&
                             companion.PresetSha256 == entry.SourceSha256 &&
                             target is not null;
    public bool DetailsPromptVisible => SelectedEntry is null;
    public string SelectionHelp => SelectedEntry is null
        ? "Select a preset to inspect its exact copied source."
        : SelectedEntry.Compatibility != RaceMenuPresetCompatibilityKind.Compatible
            ? "This preset is not compatible with the reviewed race and head-part authority."
            : IsPreviewBusy
                ? "Rendering and hash-binding the same-stem CharGen export..."
                : selectedCompanion is null
                    ? "A valid same-stem CharGen NIF/DDS preview is required before this preset can be used."
                    : IsPreparedSelection(SelectedEntry)
                        ? "This preset already matches the reviewed bundle and can be committed immediately."
                        : "Use preset will generate and reopen a new authority bundle without changing the current request on failure.";

    private string status = "Loading copied RaceMenu presets...";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool HasNoVisibleEntries => !IsBusy && VisibleEntries.Count == 0;
    public string EmptyState => allEntries.Count == 0
        ? "No usable RaceMenu presets were found in this folder."
        : "No presets match the current filter.";
    public string VisibleCount => $"{VisibleEntries.Count} shown · {allEntries.Count} admitted";

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Scanning and hash-binding the flat .jslot catalog...";
        allEntries.Clear();
        VisibleEntries.Clear();
        Diagnostics.Clear();
        SelectedEntry = null;
        try
        {
            RaceMenuPresetCatalogResult result = await catalogService.LoadAsync(
                new RaceMenuPresetCatalogRequest(presetDirectory, target),
                cancellationToken);
            allEntries.AddRange(result.Entries.Select(item =>
                new RaceMenuPresetCatalogEntryViewModel(item)));
            foreach (Diagnostic diagnostic in result.Diagnostics)
                Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
            CompatibilityFilterAvailable = target is not null && allEntries.Count > 0 &&
                                           allEntries.All(item =>
                                               item.Compatibility != RaceMenuPresetCompatibilityKind.Unavailable);
            Status = result.Accepted
                ? $"Catalog ready. {allEntries.Count} preset(s) admitted."
                : "The preset folder was refused. Review the diagnostics.";
            RefreshVisibleEntries();
        }
        catch (OperationCanceledException)
        {
            Status = "Preset loading cancelled.";
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException)
        {
            Diagnostics.Add($"Error: preset-catalog-ui: {exception.Message}");
            Status = "The preset folder could not be loaded.";
        }
        finally
        {
            IsBusy = false;
            NotifyCollectionState();
        }
    }

    public RaceMenuPresetSelection? AcceptSelection() => !CanAccept || SelectedEntry is null
        ? null
        : new RaceMenuPresetSelection(
            SelectedEntry.SourcePath,
            SelectedEntry.SourceSha256,
            ApplyBodySlide,
            SelectedEntry.Document,
            selectedCompanion,
            target);

    private bool IsPreparedSelection(RaceMenuPresetCatalogEntryViewModel entry) =>
        preparedSelection is null ||
        (string.Equals(entry.SourcePath.Value, preparedSelection.SourcePath.Value,
             StringComparison.OrdinalIgnoreCase) &&
         entry.SourceSha256 == preparedSelection.SourceSha256);

    internal Task WaitForPreviewAsync() => previewTask;

    private void SchedulePreview()
    {
        int version = Interlocked.Increment(ref previewVersion);
        if (previewCancellation is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }
        previewCancellation = null;
        selectedCompanion = null;
        OnPropertyChanged(nameof(CanAccept));
        OnPropertyChanged(nameof(SelectionHelp));
        PreviewImagePath = null;
        PreviewAuthority =
            "Off-engine staging preview only — Skyrim runtime authority is false.";

        if (SelectedEntry is null)
        {
            IsPreviewBusy = false;
            PreviewStatus = "Select a preset to render its same-stem CharGen head export.";
            previewTask = Task.CompletedTask;
            return;
        }
        if (previewService is null)
        {
            IsPreviewBusy = false;
            PreviewStatus = "Off-engine CharGen preview is unavailable in this composition.";
            previewTask = Task.CompletedTask;
            return;
        }

        var source = new CancellationTokenSource();
        previewCancellation = source;
        IsPreviewBusy = true;
        PreviewStatus = "Rendering the hash-bound same-stem CharGen export...";
        RaceMenuPresetCatalogEntryViewModel entry = SelectedEntry;
        previewTask = RenderPreviewAsync(entry, version, source.Token);
    }

    private async Task RenderPreviewAsync(
        RaceMenuPresetCatalogEntryViewModel entry,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            RaceMenuPresetPreviewResult result = await previewService!.RenderAsync(
                new RaceMenuPresetPreviewRequest(
                    entry.SourcePath, entry.SourceSha256), cancellationToken);
            if (version != previewVersion || cancellationToken.IsCancellationRequested ||
                SelectedEntry != entry)
                return;

            if (!result.Rendered || result.Image is null || result.Companion is null)
            {
                Diagnostic? error = result.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error);
                PreviewStatus = error is null
                    ? "The selected preset has no renderable same-stem CharGen export."
                    : $"Preview unavailable: {error.Code}: {error.Message}";
                return;
            }

            PreviewImagePath = result.Image.Path;
            selectedCompanion = result.Companion;
            OnPropertyChanged(nameof(CanAccept));
            OnPropertyChanged(nameof(SelectionHelp));
            PreviewStatus =
                $"Rendered {result.Image.Width}×{result.Image.Height} from " +
                $"{Path.GetFileName(result.Companion.FaceGeom.Value)}.";
            PreviewAuthority =
                $"NIF {ShortHash(result.Companion.FaceGeomSha256)} · " +
                $"DDS {ShortHash(result.Companion.FaceTintSha256)} · runtime authority false";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer selection or dialog close owns cancellation.
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           InvalidDataException or
                                           InvalidOperationException or
                                           NotSupportedException or
                                           TimeoutException or
                                           Win32Exception or
                                           System.Security.Cryptography.CryptographicException)
        {
            if (version == previewVersion)
                PreviewStatus = $"Preview unavailable: {exception.Message}";
        }
        finally
        {
            if (version == previewVersion) IsPreviewBusy = false;
        }
    }

    private static string ShortHash(Sha256Hash hash) =>
        hash.Value.Length <= 12 ? hash.Value : hash.Value[..12] + "…";

    private void RefreshVisibleEntries()
    {
        string filter = FilterText.Trim();
        RaceMenuPresetCatalogEntryViewModel? previous = SelectedEntry;
        VisibleEntries.Clear();
        foreach (RaceMenuPresetCatalogEntryViewModel entry in allEntries)
        {
            if (filter.Length > 0 && !entry.DisplayName.Contains(
                    filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (CompatibleOnly &&
                entry.Compatibility != RaceMenuPresetCompatibilityKind.Compatible) continue;
            VisibleEntries.Add(entry);
        }
        SelectedEntry = previous is not null && VisibleEntries.Contains(previous)
            ? previous
            : null;
        NotifyCollectionState();
    }

    private void NotifyCollectionState()
    {
        OnPropertyChanged(nameof(HasNoVisibleEntries));
        OnPropertyChanged(nameof(EmptyState));
        OnPropertyChanged(nameof(VisibleCount));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (disposed) return;
        Interlocked.Increment(ref previewVersion);
        if (previewCancellation is { } source)
        {
            source.Cancel();
            source.Dispose();
            previewCancellation = null;
        }
        disposed = true;
        GC.SuppressFinalize(this);
    }
}

public sealed class RaceMenuPresetCatalogEntryViewModel
{
    internal RaceMenuPresetCatalogEntryViewModel(RaceMenuPresetCatalogEntry entry)
    {
        SourcePath = entry.SourcePath;
        SourceSha256 = entry.SourceSha256;
        Document = entry.Document;
        DisplayName = entry.DisplayName;
        Compatibility = entry.Compatibility;
        CompatibilityLabel = entry.Compatibility switch
        {
            RaceMenuPresetCompatibilityKind.Compatible => "Compatible",
            RaceMenuPresetCompatibilityKind.Incompatible => "Race mismatch",
            _ => "Authority unavailable"
        };
        RaceMenuPresetSummary summary = entry.Summary;
        SummaryLine = $"{summary.HeadParts} head parts · {summary.Tints} tints · " +
                      $"{summary.SliderMorphs + summary.CustomMorphs} face morphs";
        SourcePathText = entry.SourcePath.Value;
        SourceSha256Text = entry.SourceSha256.Value;
        WeightText = summary.Weight?.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture) ?? "—";
        HeadAndFaceText = $"{summary.HeadParts} head parts · {summary.Tints} tints · " +
                          $"{summary.FaceMorphPresets} presets · {summary.SliderMorphs} sliders · " +
                          $"{summary.CustomMorphs} custom · {summary.SculptParts} sculpt";
        BodyText = $"Weight {WeightText} · {summary.BodyMorphs} BodySlide · " +
                   $"{summary.BodyOverlays} overlays · {summary.NodeTransforms} transforms · " +
                   $"{summary.SkinOverrides} skin overrides";
        DiagnosticText = entry.Diagnostics.Length == 0
            ? "No entry diagnostics."
            : string.Join(Environment.NewLine, entry.Diagnostics.Select(item =>
                $"{item.Severity}: {item.Code}: {item.Message}"));
    }

    public string DisplayName { get; }
    public string SummaryLine { get; }
    public WorkspacePath SourcePath { get; }
    public Sha256Hash SourceSha256 { get; }
    public PresetDocument Document { get; }
    public string SourcePathText { get; }
    public string SourceSha256Text { get; }
    public RaceMenuPresetCompatibilityKind Compatibility { get; }
    public string CompatibilityLabel { get; }
    public string WeightText { get; }
    public string HeadAndFaceText { get; }
    public string BodyText { get; }
    public string DiagnosticText { get; }
}
