using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimHeadPartPickerViewModel : NotifyViewModel, IDisposable
{
    private readonly ImmutableArray<SkyrimHeadPartChoiceRowViewModel> catalog;
    private readonly WorkspacePath dataRoot;
    private readonly ISkyrimHeadPartPreviewService? previewService;
    private CancellationTokenSource? previewCancellation;
    private Task previewTask = Task.CompletedTask;
    private int previewVersion;
    private bool disposed;

    public SkyrimHeadPartPickerViewModel(
        SkyrimHeadPartChoiceResult result,
        WorkspacePath dataRoot,
        FormReference race,
        NpcSex sex,
        NpcHeadPartType type,
        ISkyrimHeadPartPreviewService? previewService = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted || result.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            throw new ArgumentException(
                "Only an accepted, error-free head-part catalog can open the picker.",
                nameof(result));
        }
        this.dataRoot = dataRoot;
        this.previewService = previewService;
        Race = race;
        Sex = sex;
        Type = type;
        Diagnostics = result.Diagnostics;
        catalog = result.Candidates
            .Select(SkyrimHeadPartChoiceRowViewModel.From)
            .ToImmutableArray();
        RefreshVisibleRows();
    }

    public string Title => $"Choose {Type.ToWireName()}";
    public string Context =>
        $"{Type.ToWireName()} for {Race} / {Sex.ToString().ToLowerInvariant()}";
    public FormReference Race { get; }
    public NpcSex Sex { get; }
    public NpcHeadPartType Type { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public ObservableCollection<SkyrimHeadPartChoiceRowViewModel> VisibleRows { get; } = [];
    public bool HasNoVisibleRows => VisibleRows.Count == 0;
    public bool CanAccept => SelectedRow is not null && !IsPreviewBusy;
    public string VisibleCount => $"{VisibleRows.Count} of {catalog.Length} compatible head part(s) visible";
    public string EmptyState => catalog.IsDefaultOrEmpty
        ? $"No compatible {Type.ToWireName()} records exist for this race and sex."
        : "No compatible head parts match the current filter.";
    public string SelectionTitle => SelectedRow?.DisplayName ?? "No head part selected";
    public string SelectionReference => SelectedRow?.Reference.ToString() ??
        "Select one row deliberately.";
    public string SelectionEvidence => SelectedRow?.Evidence ??
        "No HDPT provider or race-compatibility evidence is selected.";
    public bool IsAccepted { get; private set; }
    public FormReference? AcceptedReference { get; private set; }
    public FormId? AcceptedFormId { get; private set; }

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (Set(ref filterText, value ?? string.Empty)) RefreshVisibleRows();
        }
    }

    private SkyrimHeadPartChoiceRowViewModel? selectedRow;
    public SkyrimHeadPartChoiceRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            ValidationMessage = string.Empty;
            Raise(nameof(CanAccept));
            Raise(nameof(SelectionTitle));
            Raise(nameof(SelectionReference));
            Raise(nameof(SelectionEvidence));
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

    private string previewStatus = "Select a compatible head part to preview its model chain.";
    public string PreviewStatus
    {
        get => previewStatus;
        private set => Set(ref previewStatus, value);
    }

    public string PreviewAuthority =>
        $"Off-engine {Type.ToWireName()} geometry evidence only / Skyrim runtime authority false";

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
            ValidationMessage = "Select one compatible head part first.";
            return false;
        }
        if (IsPreviewBusy)
        {
            ValidationMessage = "Wait for the selected preview state to settle.";
            return false;
        }
        IsAccepted = true;
        AcceptedReference = SelectedRow.Reference;
        AcceptedFormId = SelectedRow.Reference.FormId;
        ValidationMessage = string.Empty;
        return true;
    }

    public void Cancel()
    {
        CancelPreview();
        PreviewImagePath = null;
        IsPreviewBusy = false;
        PreviewStatus = "Head-part selection cancelled.";
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
        SkyrimHeadPartChoiceRowViewModel? retained = SelectedRow;
        string query = FilterText.Trim();
        VisibleRows.Clear();
        foreach (SkyrimHeadPartChoiceRowViewModel row in catalog.Where(item =>
                     query.Length == 0 ||
                     item.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.Reference.FormId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleRows.Add(row);
        }
        if (retained is not null && !VisibleRows.Contains(retained))
            SelectedRow = null;
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
            PreviewStatus = "Select a compatible head part to preview its model chain.";
            previewTask = Task.CompletedTask;
            return;
        }
        if (SelectedRow.Candidate.PreviewModels.IsDefaultOrEmpty)
        {
            IsPreviewBusy = false;
            PreviewStatus = "Preview unavailable: the selected HDPT chain has no readable model route.";
            previewTask = Task.CompletedTask;
            return;
        }
        if (previewService is null)
        {
            IsPreviewBusy = false;
            PreviewStatus =
                "Preview unavailable in this composition; the typed model chain remains selectable.";
            previewTask = Task.CompletedTask;
            return;
        }

        var source = new CancellationTokenSource();
        previewCancellation = source;
        IsPreviewBusy = true;
        PreviewStatus =
            $"Rendering {SelectedRow.Candidate.PreviewModels.Length} hash-bound model route(s)...";
        SkyrimHeadPartChoiceRowViewModel row = SelectedRow;
        previewTask = RenderPreviewAsync(row, version, source.Token);
    }

    private async Task RenderPreviewAsync(
        SkyrimHeadPartChoiceRowViewModel row,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            SkyrimHeadPartPreviewResult result = await previewService!.RenderAsync(
                new SkyrimHeadPartPreviewRequest(dataRoot, row.Candidate),
                cancellationToken);
            if (version != previewVersion || cancellationToken.IsCancellationRequested ||
                SelectedRow != row)
            {
                return;
            }
            if (!result.Rendered || result.Image is null)
            {
                Diagnostic? error = result.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error);
                PreviewStatus = error is null
                    ? "Preview unavailable: no image was produced for the selected model chain."
                    : $"Preview unavailable: {error.Code}: {error.Message}";
                return;
            }
            PreviewImagePath = result.Image.Path;
            PreviewStatus =
                $"Rendered {result.Image.Width}×{result.Image.Height} from " +
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
        AcceptedReference = null;
        AcceptedFormId = null;
    }
}

public sealed class SkyrimHeadPartChoiceRowViewModel
{
    private SkyrimHeadPartChoiceRowViewModel(
        SkyrimHeadPartChoiceCandidate candidate,
        string displayName,
        string evidence)
    {
        Candidate = candidate;
        DisplayName = displayName;
        Evidence = evidence;
    }

    public SkyrimHeadPartChoiceCandidate Candidate { get; }
    public FormReference Reference => Candidate.Reference;
    public string EditorId => Candidate.EditorId;
    public string DisplayName { get; }
    public string FormIdText => Reference.FormId.ToString();
    public string PluginText => Candidate.Provider.Plugin.Value;
    public string ModelText => Candidate.ModelNif?.Value ?? "No model";
    public string Evidence { get; }

    internal static SkyrimHeadPartChoiceRowViewModel From(
        SkyrimHeadPartChoiceCandidate candidate)
    {
        string name = candidate.Name ?? candidate.EditorId;
        string evidence =
            $"{candidate.RaceMatch} · {candidate.Provider.Plugin.Value} · " +
            $"{candidate.Provider.Sha256.Value[..12]}… · " +
            $"{candidate.PreviewModels.Length} preview model route(s)";
        return new SkyrimHeadPartChoiceRowViewModel(candidate, name, evidence);
    }
}
