using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Modal animation-picker state. The shared service owns manifest parsing,
/// hashing, filtering, and taxonomy; this adapter owns only presentation and
/// the pending-versus-committed selection boundary.
/// </summary>
public sealed class AnimationPickerViewModel : INotifyPropertyChanged
{
    private readonly IPreviewAnimationPickerService? service;
    private PreviewAnimationPickerSession? session;
    private PreviewAnimationSelection? committedSelection;
    private string manifestPath = string.Empty;
    private GameEdition selectedEdition = GameEdition.SkyrimSpecialEdition;
    private string filter = string.Empty;
    private bool isFemale;
    private bool filterByGender = true;
    private bool showFirstPerson;
    private bool isBusy;
    private string status = "Choose a K-local animation catalog.";
    private string manifestHash = "Not loaded";
    private AnimationTreeItemViewModel? selectedItem;

    public AnimationPickerViewModel(IPreviewAnimationPickerService? service)
    {
        this.service = service;
        EditionOptions =
        [
            new GameEditionOption(GameEdition.Fallout4, "Fallout 4"),
            new GameEditionOption(
                GameEdition.SkyrimSpecialEdition,
                "Skyrim Special Edition")
        ];
        Groups = [];
        if (service is null)
            Status = "Animation selection is unavailable in this shell configuration.";
    }

    public ObservableCollection<GameEditionOption> EditionOptions { get; }

    public ObservableCollection<AnimationTreeItemViewModel> Groups { get; }

    public string ManifestPath
    {
        get => manifestPath;
        set
        {
            if (!Set(ref manifestPath, value)) return;
            ResetAuthority();
            OnPropertyChanged(nameof(CanOpenPicker));
        }
    }

    public GameEdition SelectedEdition
    {
        get => selectedEdition;
        set
        {
            if (!Set(ref selectedEdition, value)) return;
            ResetAuthority();
            OnPropertyChanged(nameof(TargetSummary));
        }
    }

    /// <summary>The caller's NPC sex, not a UI filter toggle.</summary>
    public bool IsFemale
    {
        get => isFemale;
        set
        {
            if (!Set(ref isFemale, value)) return;
            ResetAuthority();
            OnPropertyChanged(nameof(TargetSummary));
        }
    }

    public bool FilterByGender
    {
        get => filterByGender;
        set
        {
            if (!Set(ref filterByGender, value)) return;
            RefreshProjection();
        }
    }

    public bool ShowFirstPerson
    {
        get => showFirstPerson;
        set
        {
            if (!Set(ref showFirstPerson, value)) return;
            RefreshProjection();
        }
    }

    public string Filter
    {
        get => filter;
        set
        {
            if (!Set(ref filter, value)) return;
            RefreshProjection();
        }
    }

    public AnimationTreeItemViewModel? SelectedItem
    {
        get => selectedItem;
        private set
        {
            if (!Set(ref selectedItem, value)) return;
            OnPropertyChanged(nameof(SelectedClip));
            OnPropertyChanged(nameof(CanAccept));
            OnPropertyChanged(nameof(SelectionTitle));
            OnPropertyChanged(nameof(SelectionPath));
            OnPropertyChanged(nameof(SelectionMetadata));
            OnPropertyChanged(nameof(SelectionAuthority));
        }
    }

    public PreviewAnimationListItem? SelectedClip => SelectedItem?.Clip;

    public bool CanAccept => !IsBusy && SelectedClip is not null && session is not null;

    public bool CanOpenPicker => service is not null &&
                                 !string.IsNullOrWhiteSpace(ManifestPath);

    public bool HasNoVisibleClips => session is not null && Groups.Count == 0;

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

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public string ManifestHash
    {
        get => manifestHash;
        private set => Set(ref manifestHash, value);
    }

    public string TargetSummary =>
        $"{SelectedEdition.ToWireName()} · {(IsFemale ? "female" : "male")} NPC";

    public string SelectionTitle => SelectedClip?.ClipName ??
        "Select an animation leaf";

    public string SelectionPath => SelectedClip?.Path ??
        "Folders and categories cannot be accepted.";

    public string SelectionMetadata => SelectedClip is not { } clip
        ? "Choose a leaf to inspect its exact playback metadata."
        : $"{clip.FrameCount} frames · {clip.FramesPerSecond:0.###} fps · " +
          $"{(clip.Additive ? "additive overlay" : "full pose")} · " +
          $"{(clip.RequiresFemale ? "female only" : "gender neutral")}";

    public string SelectionAuthority => SelectedClip is not { } clip
        ? "Static catalog metadata only; runtime authority is false."
        : $"Skeleton: {clip.Skeleton} · " +
          $"{(clip.FromBehaviorGraph ? "behavior-graph route" : "search-path-only route")}. " +
          "Static metadata only; runtime authority is false.";

    public string CommittedSelectionSummary => committedSelection is null
        ? "No animation selected."
        : $"{committedSelection.Clip.ClipName} · {committedSelection.Clip.Path}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (service is null) return;
        if (string.IsNullOrWhiteSpace(ManifestPath))
        {
            Status = "Enter a K-local animation catalog before opening the picker.";
            return;
        }

        IsBusy = true;
        Groups.Clear();
        SelectedItem = null;
        session = null;
        ManifestHash = "Loading";
        Status = "Reading and hashing the animation catalog once...";
        try
        {
            PreviewAnimationPickerOpenResult opened = await service.OpenAsync(
                new PreviewAnimationPickerOpenRequest(
                    SelectedEdition,
                    new WorkspacePath(ManifestPath),
                    IsFemale,
                    committedSelection?.Clip.Path ?? string.Empty),
                cancellationToken);
            if (!opened.Succeeded || opened.Session is null)
            {
                Status = DiagnosticText(opened.Diagnostics,
                    "The animation catalog was refused.");
                ManifestHash = "Refused";
                return;
            }

            session = opened.Session;
            ManifestHash = session.ManifestSha256.Value;
            RefreshProjection();
        }
        catch (OperationCanceledException)
        {
            Status = "Animation selection cancelled; the prior selection is unchanged.";
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            Status = exception.Message;
            ManifestHash = "Refused";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Select(AnimationTreeItemViewModel? item) =>
        SelectedItem = item?.IsLeaf == true ? item : null;

    public PreviewAnimationSelection? AcceptSelection() =>
        session is not null && SelectedClip is { } clip
            ? new PreviewAnimationSelection(clip, session.ManifestSha256)
            : null;

    public void CommitSelection(PreviewAnimationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (session is null ||
            selection.ManifestSha256 != session.ManifestSha256 ||
            !session.Items.Any(item => item == selection.Clip))
        {
            throw new InvalidDataException(
                "The selected animation does not belong to the current hash-bound catalog.");
        }
        committedSelection = selection;
        OnPropertyChanged(nameof(CommittedSelectionSummary));
    }

    public void CancelPendingSelection()
    {
        if (session is null) return;
        RefreshProjection();
        Status = "Selection cancelled; the prior animation is unchanged.";
    }

    private void RefreshProjection()
    {
        if (service is null || session is null) return;
        PreviewAnimationPickerProjection projection = service.Filter(
            session,
            new PreviewAnimationPickerFilter(
                Filter,
                FilterByGender,
                ShowFirstPerson));
        Groups.Clear();
        foreach (PreviewAnimationTreeBranch group in projection.Groups)
        {
            Groups.Add(AnimationTreeItemViewModel.FromBranch(
                group, expandAll: !string.IsNullOrWhiteSpace(Filter)));
        }

        SelectedItem = null;
        string restorePath = committedSelection?.Clip.Path ??
                             session.CurrentAnimationPath;
        if (!string.IsNullOrWhiteSpace(restorePath))
        {
            foreach (AnimationTreeItemViewModel group in Groups)
            {
                if (!group.TrySelectPath(restorePath,
                        out AnimationTreeItemViewModel? restored)) continue;
                SelectedItem = restored;
                break;
            }
        }

        Status = projection.VisibleCount == 0
            ? $"No animations match these filters · 0 of {projection.TotalCount}"
            : $"{projection.VisibleCount} of {projection.TotalCount} animations · cached in memory";
        OnPropertyChanged(nameof(HasNoVisibleClips));
    }

    private void ResetAuthority()
    {
        session = null;
        committedSelection = null;
        Groups.Clear();
        SelectedItem = null;
        ManifestHash = "Not loaded";
        Status = "Catalog settings changed. Open the picker to review them.";
        OnPropertyChanged(nameof(HasNoVisibleClips));
        OnPropertyChanged(nameof(CommittedSelectionSummary));
    }

    private static string DiagnosticText(
        IEnumerable<Diagnostic> diagnostics,
        string fallback)
    {
        string text = string.Join(" ", diagnostics
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .Select(item => item.Message));
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private bool Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed record GameEditionOption(GameEdition Value, string Name);
}
