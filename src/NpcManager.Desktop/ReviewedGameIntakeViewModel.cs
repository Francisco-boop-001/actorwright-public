using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class ReviewedGameIntakeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IReviewedGameIntakeService service;
    private readonly WorkspacePath workspaceRoot;
    private readonly ObservableCollection<PluginRowViewModel> allPlugins = [];
    private readonly DelegateCommand browseDataCommand;
    private readonly DelegateCommand browseLoadOrderCommand;
    private readonly DelegateCommand browseOutputCommand;
    private readonly DelegateCommand cancelCommand;
    private readonly DelegateCommand selectActiveCommand;
    private readonly DelegateCommand selectShownCommand;
    private readonly DelegateCommand clearShownCommand;
    private readonly DelegateCommand addMastersCommand;
    private CancellationTokenSource? cancellation;
    private bool replacingRows;

    internal ReviewedGameIntakeViewModel(ReviewedGameIntakeDesktopContext context)
    {
        service = context.Service;
        workspaceRoot = context.WorkspaceRoot;
        dataRoot = context.DataRoot.Value;
        loadOrderPath = context.LoadOrderPath.Value;
        outputRoot = context.OutputRoot.Value;
        RuntimeAuthority = "Static intake only — not runtime authority.";
        browseDataCommand = new DelegateCommand(_ => BrowseData(), _ => !IsBusy);
        browseLoadOrderCommand = new DelegateCommand(_ => BrowseLoadOrder(), _ => !IsBusy);
        browseOutputCommand = new DelegateCommand(_ => BrowseOutput(), _ => !IsBusy);
        cancelCommand = new DelegateCommand(_ => Cancel(), _ => IsBusy && !IsCancelling);
        selectActiveCommand = new DelegateCommand(_ => SetSelection(
            allPlugins.Where(row => row.Enabled && row.Exists), true), _ => !IsBusy && allPlugins.Count > 0);
        selectShownCommand = new DelegateCommand(_ => SetSelection(VisiblePlugins, true),
            _ => !IsBusy && VisiblePlugins.Count > 0);
        clearShownCommand = new DelegateCommand(_ => SetSelection(VisiblePlugins, false),
            _ => !IsBusy && VisiblePlugins.Count > 0);
        addMastersCommand = new DelegateCommand(_ => AddRequiredMasters(),
            _ => !IsBusy && allPlugins.Count > 0);
    }

    public ObservableCollection<PluginRowViewModel> VisiblePlugins { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];

    public ICommand BrowseDataCommand => browseDataCommand;
    public ICommand BrowseLoadOrderCommand => browseLoadOrderCommand;
    public ICommand BrowseOutputCommand => browseOutputCommand;
    public ICommand CancelCommand => cancelCommand;
    public ICommand SelectActiveCommand => selectActiveCommand;
    public ICommand SelectShownCommand => selectShownCommand;
    public ICommand ClearShownCommand => clearShownCommand;
    public ICommand AddMastersCommand => addMastersCommand;

    public event Action<ReviewedGameIntake?>? AcceptedIntakeChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private string dataRoot;
    public string DataRoot
    {
        get => dataRoot;
        set => SetInput(ref dataRoot, value);
    }

    private string loadOrderPath;
    public string LoadOrderPath
    {
        get => loadOrderPath;
        set => SetInput(ref loadOrderPath, value);
    }

    private string outputRoot;
    public string OutputRoot
    {
        get => outputRoot;
        set => SetInput(ref outputRoot, value);
    }

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (!Set(ref filterText, value)) return;
            RefreshVisiblePlugins();
        }
    }

    private bool activeOnly;
    public bool ActiveOnly
    {
        get => activeOnly;
        set
        {
            if (!Set(ref activeOnly, value)) return;
            RefreshVisiblePlugins();
        }
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanReview));
            RaiseCommands();
        }
    }

    public bool IsNotBusy => !IsBusy;

    private bool isProgressIndeterminate;
    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        private set => Set(ref isProgressIndeterminate, value);
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private bool isCancelling;
    public bool IsCancelling
    {
        get => isCancelling;
        private set
        {
            if (!Set(ref isCancelling, value)) return;
            RaiseCommands();
        }
    }

    public bool CanReview => !IsBusy && IsAbsolute(DataRoot) &&
                             IsAbsolute(LoadOrderPath) && IsAbsolute(OutputRoot);

    private string status = "Choose copied Skyrim inputs, then review the workspace.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string verdict = "Review required";
    public string Verdict
    {
        get => verdict;
        private set => Set(ref verdict, value);
    }

    private string manifestHash = "—";
    public string ManifestHash
    {
        get => manifestHash;
        private set => Set(ref manifestHash, value);
    }

    private string intakeFingerprint = "—";
    public string IntakeFingerprint
    {
        get => intakeFingerprint;
        private set => Set(ref intakeFingerprint, value);
    }

    private int generatedPluginCount;
    public int GeneratedPluginCount
    {
        get => generatedPluginCount;
        private set => Set(ref generatedPluginCount, value);
    }

    private int sidecarCount;
    public int SidecarCount
    {
        get => sidecarCount;
        private set => Set(ref sidecarCount, value);
    }

    private int assetProviderCount;
    public int AssetProviderCount
    {
        get => assetProviderCount;
        private set => Set(ref assetProviderCount, value);
    }

    public int PluginCount => allPlugins.Count;
    public int SelectedCount => allPlugins.Count(row => row.IsSelected);
    public bool HasNoVisiblePlugins => VisiblePlugins.Count == 0;
    public string PluginListState => allPlugins.Count == 0
        ? "Review a copied workspace to see its plugins."
        : "No plugins match the current filter.";
    public bool HasAcceptedIntake => AcceptedIntake is not null;
    public string RuntimeAuthority { get; }
    public ReviewedGameIntake? AcceptedIntake { get; private set; }

    internal async Task ReviewAsync()
    {
        if (!CanReview) return;
        if (!TryBuildRequest(out var request, out var message))
        {
            Verdict = "Workspace needs attention";
            Status = message;
            Diagnostics.Clear();
            Diagnostics.Add($"Error: reviewed-intake-input: {message}");
            return;
        }

        ClearAcceptedIntake();
        ReplaceRows([]);
        Diagnostics.Clear();
        IsBusy = true;
        IsCancelling = false;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        Verdict = "Reviewing copied workspace";
        Status = "Reading the manifest, plugin closure, sidecars, generated files, and assets...";
        var source = new CancellationTokenSource();
        var progressGate = new object();
        var acceptsProgress = true;
        cancellation = source;
        try
        {
            var progress = new Progress<ReviewedGameIntakeProgress>(item =>
            {
                lock (progressGate)
                {
                    if (!acceptsProgress)
                        return;
                    ProgressPercent = item.Percent;
                    IsProgressIndeterminate = item.IsIndeterminate;
                    Status = item.Message;
                }
            });
            var result = await Task.Run(async () =>
                await service.ReviewAsync(request, progress, source.Token).ConfigureAwait(false)).ConfigureAwait(true);
            lock (progressGate)
                acceptsProgress = false;
            Present(result);
        }
        catch (OperationCanceledException)
        {
            Verdict = "Review cancelled";
            Status = "No workspace intake was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Verdict = "Workspace needs attention";
            Status = "The copied workspace could not be reviewed safely.";
            Diagnostics.Add($"Error: reviewed-intake-exception: {exception.Message}");
        }
        finally
        {
            lock (progressGate)
                acceptsProgress = false;
            IsProgressIndeterminate = false;
            IsBusy = false;
            IsCancelling = false;
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            source.Dispose();
        }
    }

    private void Present(ReviewedGameIntakeResult result)
    {
        ReplaceRows(result.AvailablePlugins);
        foreach (var diagnostic in result.Diagnostics)
            Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
        if (!result.IsAccepted || result.Intake is null)
        {
            Verdict = "Workspace needs attention";
            Status = "Resolve the listed findings, then review again. No intake was retained.";
            return;
        }

        AcceptedIntake = result.Intake;
        ProgressPercent = 100;
        IsProgressIndeterminate = false;
        ManifestHash = result.Intake.LoadOrderHash.Value;
        IntakeFingerprint = result.Intake.IntakeFingerprint.Value;
        GeneratedPluginCount = result.Intake.GeneratedPlugins.Length;
        SidecarCount = result.Intake.BodySidecars.Length;
        AssetProviderCount = result.Intake.AssetProviderCount;
        Verdict = "Workspace reviewed";
        Status = $"{result.Intake.Plugins.Length} plugins in the trusted closure. Downstream tasks are ready.";
        OnPropertyChanged(nameof(HasAcceptedIntake));
        AcceptedIntakeChanged?.Invoke(AcceptedIntake);
    }

    private bool TryBuildRequest(out ReviewedGameIntakeRequest request, out string message)
    {
        request = default!;
        try
        {
            ImmutableArray<PluginName> selected = allPlugins.Count == 0
                ? default
                : allPlugins.Where(row => row.IsSelected).Select(row => row.Plugin).ToImmutableArray();
            request = new ReviewedGameIntakeRequest(
                GameEdition.SkyrimSpecialEdition,
                workspaceRoot,
                new WorkspacePath(DataRoot),
                new WorkspacePath(LoadOrderPath),
                new WorkspacePath(OutputRoot),
                selected);
            message = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }
    }

    private void ReplaceRows(ImmutableArray<PluginClosureReviewEntry> entries)
    {
        replacingRows = true;
        try
        {
            foreach (var row in allPlugins) row.PropertyChanged -= PluginRowChanged;
            allPlugins.Clear();
            foreach (var entry in entries.OrderBy(item => item.Order))
            {
                var row = new PluginRowViewModel(entry);
                row.PropertyChanged += PluginRowChanged;
                allPlugins.Add(row);
            }
        }
        finally
        {
            replacingRows = false;
        }
        RefreshVisiblePlugins();
        OnPropertyChanged(nameof(PluginCount));
        OnPropertyChanged(nameof(SelectedCount));
        RaiseCommands();
    }

    private void PluginRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (replacingRows || e.PropertyName != nameof(PluginRowViewModel.IsSelected)) return;
        ClearAcceptedIntake();
        Status = "Review required — plugin selection changed.";
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void RefreshVisiblePlugins()
    {
        VisiblePlugins.Clear();
        foreach (var row in allPlugins.Where(IsVisible)) VisiblePlugins.Add(row);
        OnPropertyChanged(nameof(HasNoVisiblePlugins));
        OnPropertyChanged(nameof(PluginListState));
        RaiseCommands();
    }

    private bool IsVisible(PluginRowViewModel row) =>
        (!ActiveOnly || row.Enabled) &&
        (string.IsNullOrWhiteSpace(FilterText) ||
         row.Name.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void SetSelection(IEnumerable<PluginRowViewModel> rows, bool selected)
    {
        foreach (var row in rows.ToArray()) row.IsSelected = selected;
    }

    private void AddRequiredMasters()
    {
        var byName = allPlugins.ToDictionary(row => row.Plugin.Value, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<PluginRowViewModel>(allPlugins.Where(row => row.IsSelected));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var row = pending.Dequeue();
            if (!seen.Add(row.Plugin.Value)) continue;
            foreach (var master in row.Masters)
            {
                if (!byName.TryGetValue(master.Value, out var masterRow)) continue;
                masterRow.IsSelected = true;
                pending.Enqueue(masterRow);
            }
        }
    }

    private void SetInput(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CanReview));
        ClearAcceptedIntake();
        ReplaceRows([]);
        Status = "Review required — input changed.";
    }

    private void ClearAcceptedIntake()
    {
        if (AcceptedIntake is null) return;
        AcceptedIntake = null;
        ManifestHash = "—";
        IntakeFingerprint = "—";
        GeneratedPluginCount = 0;
        SidecarCount = 0;
        AssetProviderCount = 0;
        Verdict = "Review required";
        OnPropertyChanged(nameof(HasAcceptedIntake));
        AcceptedIntakeChanged?.Invoke(null);
    }

    internal void InvalidateConsumedOutput()
    {
        ClearAcceptedIntake();
        Status = "Review required — the previous output was consumed. Choose a new output folder, then review again.";
    }

    private void Cancel()
    {
        if (!IsBusy || IsCancelling || cancellation is null) return;
        IsCancelling = true;
        Status = "Cancelling at the next safe review boundary...";
        cancellation.Cancel();
    }

    private void BrowseData()
    {
        var dialog = new OpenFolderDialog { Multiselect = false, Title = "Choose copied Skyrim Data folder" };
        if (dialog.ShowDialog() == true) DataRoot = dialog.FolderName;
    }

    private void BrowseLoadOrder()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Load-order JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Choose explicit load-order manifest"
        };
        if (dialog.ShowDialog() == true) LoadOrderPath = dialog.FileName;
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Multiselect = false, Title = "Choose output parent folder" };
        if (dialog.ShowDialog() == true) OutputRoot = Path.Combine(dialog.FolderName, "NpcStudioOutput");
    }

    private static bool IsAbsolute(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && Path.IsPathFullyQualified(value);

    private void RaiseCommands()
    {
        browseDataCommand.RaiseCanExecuteChanged();
        browseLoadOrderCommand.RaiseCanExecuteChanged();
        browseOutputCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
        selectActiveCommand.RaiseCanExecuteChanged();
        selectShownCommand.RaiseCanExecuteChanged();
        clearShownCommand.RaiseCanExecuteChanged();
        addMastersCommand.RaiseCanExecuteChanged();
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
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        foreach (var row in allPlugins) row.PropertyChanged -= PluginRowChanged;
    }

    private sealed class DelegateCommand(Action<object?> execute, Predicate<object?> canExecute) : ICommand
    {
        public bool CanExecute(object? parameter) => canExecute(parameter);
        public void Execute(object? parameter) => execute(parameter);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
