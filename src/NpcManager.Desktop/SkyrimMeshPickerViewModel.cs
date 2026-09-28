using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public enum SkyrimMeshTargetField
{
    ArmorAddonMaleThirdPerson,
    ArmorAddonFemaleThirdPerson,
    ArmorAddonMaleFirstPerson,
    ArmorAddonFemaleFirstPerson,
    ArmorMaleWorld,
    ArmorFemaleWorld
}

public delegate SkyrimMeshPickerSelection SkyrimMeshPathPicker(
    SkyrimMeshTargetField field,
    string? currentPath);

public delegate SkyrimMeshPickerSelection SkyrimMeshPickerModal(
    SkyrimMeshTargetField field,
    SkyrimMeshPickerCatalogResult catalog);

internal static class SkyrimMeshPickerSelectionBoundary
{
    internal static bool TryGetRelativePath(
        SkyrimMeshPickerSelection selection,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (!selection.Accepted ||
            selection.RelativePath is not { } returnedPath ||
            selection.Candidate is not { } candidate)
            return false;
        SkyrimMeshPickerSelection rebound =
            SkyrimMeshPickerRules.Use(candidate);
        if (!rebound.Accepted || rebound.RelativePath is not { } boundPath ||
            !string.Equals(returnedPath.Value, boundPath.Value,
                StringComparison.Ordinal) ||
            !string.Equals(candidate.IndexedPath.Value,
                "meshes/" + boundPath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            boundPath.Value.StartsWith("meshes/",
                StringComparison.OrdinalIgnoreCase) ||
            !boundPath.Value.EndsWith(".nif",
                StringComparison.OrdinalIgnoreCase) ||
            candidate.Providers.IsDefaultOrEmpty ||
            !candidate.Providers.Contains(candidate.SelectedProvider))
            return false;
        // AssetPath uses forward slashes for provider matching, while Bethesda
        // model subrecords conventionally store Meshes-relative paths with
        // backslashes. Keep those concerns separate at the editor boundary.
        relativePath = boundPath.Value.Replace('/', '\\');
        return true;
    }
}

public sealed class SkyrimMeshPickerViewModel : NotifyViewModel, IDisposable
{
    private readonly string title = "Select Skyrim mesh (.nif)";
    private readonly string previewAuthority =
        "K-local off-engine NIF geometry evidence only | textures, skinning, live provider precedence, and Skyrim runtime authority are not proven";
    private readonly SkyrimMeshPickerCatalogResult catalog;
    private readonly SkyrimMeshPickerRowViewModel[] catalogRows;
    private readonly ISkyrimMeshPreviewService? previewService;
    private CancellationTokenSource? previewCancellation;
    private Task previewTask = Task.CompletedTask;
    private int previewVersion;
    private bool disposed;

    public SkyrimMeshPickerViewModel(
        SkyrimMeshPickerCatalogResult catalog,
        ISkyrimMeshPreviewService? previewService = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.Accepted || catalog.Candidates.IsDefault ||
            catalog.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException(
                "Only an accepted, error-free Skyrim mesh catalog can open the picker.",
                nameof(catalog));
        this.catalog = catalog;
        this.previewService = previewService;
        catalogRows = catalog.Candidates
            .Select(SkyrimMeshPickerRowViewModel.From)
            .ToArray();
        RefreshVisibleRows();
        if (catalog.InitialCandidate is { } initial)
            SelectedRow = VisibleRows.FirstOrDefault(item =>
                item.Candidate == initial);
    }

    public string Title => title;
    public ObservableCollection<SkyrimMeshPickerRowViewModel> VisibleRows { get; } = [];
    public bool HasNoVisibleRows => VisibleRows.Count == 0;
    public bool CanAccept => SelectedRow is not null && !IsPreviewBusy;
    public string VisibleCount =>
        $"{VisibleRows.Count} of {catalog.Candidates.Length} mesh(es) visible";
    public string EmptyState => catalog.Candidates.IsDefaultOrEmpty
        ? "No safe .nif assets exist in the reviewed mesh catalog."
        : "No mesh paths match the current filter.";
    public string SelectionPath => SelectedRow?.RelativePath ??
        "No Meshes-relative path selected.";
    public string SelectionProvider => SelectedRow?.ProviderEvidence ??
        "No static provider evidence selected.";
    public string PreviewAuthority => previewAuthority;
    public bool IsAccepted { get; private set; }
    public SkyrimMeshPickerSelection? AcceptedSelection { get; private set; }

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (Set(ref filterText, value ?? string.Empty))
                RefreshVisibleRows();
        }
    }

    private SkyrimMeshPickerRowViewModel? selectedRow;
    public SkyrimMeshPickerRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            ValidationMessage = string.Empty;
            Raise(nameof(CanAccept));
            Raise(nameof(SelectionPath));
            Raise(nameof(SelectionProvider));
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
            Raise(nameof(CanAccept));
        }
    }

    private string? previewImagePath;
    public string? PreviewImagePath
    {
        get => previewImagePath;
        private set
        {
            if (!Set(ref previewImagePath, value)) return;
            Raise(nameof(HasPreviewImage));
        }
    }

    public bool HasPreviewImage => !string.IsNullOrWhiteSpace(PreviewImagePath);

    private string previewStatus =
        "Select a mesh to render its hash-bound off-engine geometry preview.";
    public string PreviewStatus
    {
        get => previewStatus;
        private set => Set(ref previewStatus, value);
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public bool TryAccept()
    {
        ClearAccepted();
        if (SelectedRow is null)
        {
            ValidationMessage = "Select one .nif mesh first.";
            return false;
        }
        if (IsPreviewBusy)
        {
            ValidationMessage =
                "Wait for the selected mesh preview state to settle.";
            return false;
        }
        SkyrimMeshPickerSelection selection =
            SkyrimMeshPickerRules.Use(SelectedRow.Candidate);
        if (!selection.Accepted)
        {
            ValidationMessage = "The selected mesh could not be accepted.";
            return false;
        }
        IsAccepted = true;
        AcceptedSelection = selection;
        ValidationMessage = string.Empty;
        return true;
    }

    public void Cancel()
    {
        Interlocked.Increment(ref previewVersion);
        CancelPreview();
        PreviewImagePath = null;
        IsPreviewBusy = false;
        PreviewStatus = "Mesh selection cancelled.";
        ClearAccepted();
        ValidationMessage = string.Empty;
    }

    internal Task WaitForPreviewAsync() => previewTask;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CancelPreview();
    }

    private void RefreshVisibleRows()
    {
        SkyrimMeshPickerRowViewModel? retained = SelectedRow;
        var candidates = SkyrimMeshPickerRules.Filter(catalog, FilterText);
        var rows = catalogRows
            .Where(row => candidates.Contains(row.Candidate))
            .ToArray();
        VisibleRows.Clear();
        foreach (SkyrimMeshPickerRowViewModel row in rows)
            VisibleRows.Add(row);
        if (retained is not null)
        {
            if (!VisibleRows.Contains(retained)) SelectedRow = null;
        }
        Raise(nameof(HasNoVisibleRows));
        Raise(nameof(VisibleCount));
        Raise(nameof(EmptyState));
    }

    private void SchedulePreview()
    {
        int version = Interlocked.Increment(ref previewVersion);
        CancelPreview();
        PreviewImagePath = null;
        if (SelectedRow is null)
        {
            IsPreviewBusy = false;
            PreviewStatus =
                "Select a mesh to render its hash-bound off-engine geometry preview.";
            previewTask = Task.CompletedTask;
            return;
        }
        if (previewService is null)
        {
            IsPreviewBusy = false;
            PreviewStatus =
                "Preview is unavailable in this composition; the provider-bound path remains selectable.";
            previewTask = Task.CompletedTask;
            return;
        }
        var source = new CancellationTokenSource();
        previewCancellation = source;
        IsPreviewBusy = true;
        PreviewStatus =
            $"Resolving and rendering {SelectedRow.RelativePath}...";
        SkyrimMeshPickerRowViewModel row = SelectedRow;
        previewTask = RenderPreviewAsync(row, version, source.Token);
    }

    private async Task RenderPreviewAsync(
        SkyrimMeshPickerRowViewModel row,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            SkyrimMeshPreviewResult result = await previewService!.RenderAsync(
                new SkyrimMeshPreviewRequest(
                    catalog.DataRoot,
                    row.Candidate),
                cancellationToken);
            if (version != previewVersion ||
                cancellationToken.IsCancellationRequested ||
                SelectedRow?.Candidate != row.Candidate)
                return;
            if (!result.Rendered || result.Image is null)
            {
                Diagnostic? error = result.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error);
                PreviewStatus = error is null
                    ? "Preview unavailable: the renderer produced no image."
                    : $"Preview unavailable: {error.Code}: {error.Message}";
                return;
            }
            PreviewImagePath = result.Image.Path;
            PreviewStatus =
                $"Rendered {result.Image.Width}x{result.Image.Height} from " +
                $"{result.Image.MeshCount} mesh shape(s).";
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

    private void CancelPreview()
    {
        if (previewCancellation is not { } source) return;
        source.Cancel();
        source.Dispose();
        previewCancellation = null;
    }

    private void ClearAccepted()
    {
        IsAccepted = false;
        AcceptedSelection = null;
    }
}

public sealed class SkyrimMeshPickerRowViewModel
{
    private SkyrimMeshPickerRowViewModel(
        SkyrimMeshPickerCandidate candidate,
        string folder,
        string providerEvidence)
    {
        Candidate = candidate;
        Folder = folder;
        ProviderEvidence = providerEvidence;
    }

    public SkyrimMeshPickerCandidate Candidate { get; }
    public string RelativePath => Candidate.RelativePath.Value;
    public string Folder { get; }
    public string Provider =>
        $"{Candidate.SelectedProvider.Kind.ToString().ToLowerInvariant()} | " +
        Candidate.SelectedProvider.Source;
    public string Sha256Short =>
        Candidate.SelectedProvider.Sha256.Value[..12] + "...";
    public string ProviderEvidence { get; }

    internal static SkyrimMeshPickerRowViewModel From(
        SkyrimMeshPickerCandidate candidate)
    {
        int separator = candidate.RelativePath.Value.LastIndexOf('/');
        string folder = separator < 0
            ? "(Meshes root)"
            : candidate.RelativePath.Value[..separator];
        string chain = string.Join(" -> ", candidate.Providers.Select(item =>
            $"{item.Kind.ToString().ToLowerInvariant()}:{item.Source}:{item.Sha256.Value[..12]}..."));
        return new SkyrimMeshPickerRowViewModel(
            candidate,
            folder,
            $"Static selected provider {candidate.SelectedProvider.Kind.ToString().ToLowerInvariant()}:{candidate.SelectedProvider.Source} | indexed chain {chain}");
    }
}
