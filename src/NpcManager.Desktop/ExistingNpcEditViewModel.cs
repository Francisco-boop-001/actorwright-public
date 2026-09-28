using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Input;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class ExistingNpcEditViewModel :
    INotifyPropertyChanged,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyResult = "—";

    private readonly IExistingNpcEditService service;
    private readonly IFormChoiceService formChoices;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand browseInputCommand;
    private readonly AsyncCommand loadNpcCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();

    public ExistingNpcEditViewModel(
        IExistingNpcEditService service,
        IFormChoiceService formChoices,
        WorkspacePath labRoot,
        ExistingNpcEditRequest initialRequest)
    {
        this.service = service;
        this.formChoices = formChoices;
        this.labRoot = labRoot;
        Identity.Changed += IdentityChanged;
        Collections.Changed += CollectionsChanged;
        Stats.Changed += StatsChanged;
        Load(initialRequest);
        browseInputCommand = new AsyncCommand(BrowseInputAsync, () => !IsBusy);
        loadNpcCommand = new AsyncCommand(LoadNpcAsync, CanLoadNpc);
        reviewCommand = new AsyncCommand(ReviewAsync, () => !IsBusy);
        executeCommand = new AsyncCommand(ExecuteAsync,
            () => !IsBusy && IsReviewed && !HasCompleted);
        cancelCommand = new DelegateCommand(_ => cancellation?.Cancel(), _ => IsBusy);
        BrowseOutputParentCommand = new DelegateCommand(BrowseOutputParent, _ => !IsBusy);
        NewOutputCommand = new DelegateCommand(_ => OutputRoot = CreateFreshOutputRoot().Value,
            _ => !IsBusy);
    }

    public ObservableCollection<ExistingNpcEditChangeViewModel> Changes { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public ExistingNpcCollectionState Collections { get; } = new();
    public ExistingNpcStatsState Stats { get; } = new();
    public ExistingNpcIdentityState Identity { get; } = new();
    public ICommand BrowseInputCommand => browseInputCommand;
    public ICommand LoadNpcCommand => loadNpcCommand;
    public ICommand BrowseOutputParentCommand { get; }
    public ICommand NewOutputCommand { get; }
    public ICommand ReviewCommand => reviewCommand;
    public ICommand ExecuteCommand => executeCommand;
    public ICommand CancelCommand => cancelCommand;

    public event EventHandler<SkyrimMainWorkspaceArtifactHandoff>?
        ArtifactCommitted
    {
        add => artifactEmitter.ArtifactCommitted += value;
        remove => artifactEmitter.ArtifactCommitted -= value;
    }

    public void BindWorkspaceIdentity(
        SkyrimMainWorkspaceIdentity identity) =>
        artifactEmitter.Bind(identity);

    private string inputPlugin = string.Empty;
    public string InputPlugin { get => inputPlugin; set => SetSourceInput(ref inputPlugin, value); }

    private string inputSha256 = string.Empty;
    public string InputSha256 { get => inputSha256; set => SetSourceInput(ref inputSha256, value); }

    private string npcFormId = string.Empty;
    public string NpcFormId { get => npcFormId; set => SetSourceInput(ref npcFormId, value); }

    private string outputRoot = string.Empty;
    public string OutputRoot { get => outputRoot; set => SetInput(ref outputRoot, value); }

    private string pluginName = string.Empty;
    public string PluginName { get => pluginName; set => SetInput(ref pluginName, value); }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanEditCollections));
            OnPropertyChanged(nameof(CanEditStats));
            OnPropertyChanged(nameof(CanEditIdentity));
            RaiseCommandState();
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool CanEditCollections => Collections.IsLoaded && !IsBusy;
    public bool CanEditStats => Stats.IsLoaded && !IsBusy;
    public bool CanEditIdentity => Identity.IsLoaded && !IsBusy;
    public string CollectionSummary => Collections.Summary;
    public string StatsSummary => Stats.Summary;
    public string IdentitySummary => Identity.Summary;

    private bool isReviewed;
    public bool IsReviewed
    {
        get => isReviewed;
        private set
        {
            if (!Set(ref isReviewed, value)) return;
            executeCommand?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ReadinessText));
        }
    }

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set
        {
            if (!Set(ref hasCompleted, value)) return;
            executeCommand?.RaiseCanExecuteChanged();
        }
    }

    private int progressPercent;
    public int ProgressPercent { get => progressPercent; private set => Set(ref progressPercent, value); }

    private string status = "Choose a copied plugin and review one bounded NPC edit.";
    public string Status { get => status; private set => Set(ref status, value); }

    private string verdict = "Not reviewed";
    public string Verdict { get => verdict; private set => Set(ref verdict, value); }

    private string resultPlugin = EmptyResult;
    public string ResultPlugin { get => resultPlugin; private set => Set(ref resultPlugin, value); }

    private string resultPluginSha256 = EmptyResult;
    public string ResultPluginSha256 { get => resultPluginSha256; private set => Set(ref resultPluginSha256, value); }

    private string resultManifest = EmptyResult;
    public string ResultManifest { get => resultManifest; private set => Set(ref resultManifest, value); }

    public string ReadinessText
    {
        get
        {
            if (!File.Exists(InputPlugin)) return "Choose an existing copied Skyrim plugin.";
            if (!FormId.TryParse(NpcFormId, out _)) return "Enter a hexadecimal NPC FormID.";
            if (!HasRequestedChange())
                return "Enter at least one identity or supported gameplay change.";
            if (Directory.Exists(OutputRoot) || File.Exists(OutputRoot))
                return "Choose a fresh output folder; this path already exists.";
            return IsReviewed
                ? "Review is current. The verified package can be created."
                : "Inputs are ready. Review the exact before/after changes first.";
        }
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        Changes.Clear();
        ResetResults();
        ExistingNpcEditRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (ArgumentException exception)
        {
            PresentInputError(exception.Message);
            return;
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = "Reviewing";
        Status = "Reading the selected NPC and calculating the bounded change surface.";
        try
        {
            var review = await service.ReviewAsync(request, source.Token);
            AddDiagnostics(review.Diagnostics);
            foreach (var change in review.Changes)
                Changes.Add(ExistingNpcEditChangeViewModel.From(change));
            if (!review.Applicable)
            {
                Verdict = "Review refused";
                Status = "Nothing will be written. Correct the inputs and review again.";
                return;
            }

            IsReviewed = true;
            Verdict = "Ready to create";
            Status = $"{Changes.Count} exact bounded change(s) reviewed; appearance data remains untouched.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Review cancelled. Nothing was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "Nothing was written. Review the diagnostic.";
        }
        finally
        {
            Release(source);
        }
    }

    private async Task ExecuteAsync()
    {
        Diagnostics.Clear();
        ExistingNpcEditRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (ArgumentException exception)
        {
            PresentInputError(exception.Message);
            return;
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = "Creating";
        ProgressPercent = 0;
        try
        {
            var progress = new Progress<ExistingNpcEditProgress>(item =>
            {
                ProgressPercent = item.Percent;
                Status = item.Message;
            });
            var result = await service.ExecuteAsync(request, progress, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Completed ||
                result.OutputPluginPath is null ||
                result.OutputPluginSha256 is not { } outputHash ||
                result.ManifestPath is null)
            {
                PresentFailure("Edit refused", "No package was retained. Review the diagnostics.");
                return;
            }

            HasCompleted = true;
            Verdict = result.Verdict;
            ResultPlugin = result.OutputPluginPath.Value.Value;
            ResultPluginSha256 = result.OutputPluginSha256?.Value ?? EmptyResult;
            ResultManifest = result.ManifestPath.Value.Value;
            artifactEmitter.Commit(
                this,
                "npc-plugin",
                result.OutputPluginPath.Value,
                outputHash,
                request.TargetFormId);
            ProgressPercent = 100;
            Status = "Existing NPC override package created, reopened, and independently verified.";
        }
        catch (OperationCanceledException)
        {
            PresentFailure("Cancelled", "Edit cancelled. No partial package was retained.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            PresentFailure("Edit failed", "No package was retained. Review the diagnostics.");
        }
        finally
        {
            Release(source);
            OnPropertyChanged(nameof(ReadinessText));
        }
    }

    private ExistingNpcEditRequest BuildRequest()
    {
        if (!FormId.TryParse(NpcFormId, out var formId))
            throw new ArgumentException("NPC FormID must be hexadecimal, for example 0x00000800.");
        var identity = Identity.BuildPatches();
        var stats = Stats.BuildPatch();
        var collections = Collections.BuildPatches();
        return new ExistingNpcEditRequest(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(InputPlugin),
            new Sha256Hash(InputSha256),
            formId,
            new WorkspacePath(OutputRoot),
            new PluginName(PluginName),
            identity.EditorId,
            null,
            stats,
            collections.Keywords,
            collections.Factions,
            collections.Inventory,
            collections.Outfits,
            collections.Perks,
            collections.ActorEffects,
            identity.Names,
            identity.Archetype);
    }

    private bool HasRequestedChange() =>
        Identity.HasChanges ||
        Stats.HasChanges ||
        Collections.HasChanges;

    private bool CanLoadNpc() =>
        !IsBusy && File.Exists(InputPlugin) &&
        FormId.TryParse(NpcFormId, out _) &&
        TrySha256(InputSha256, out _);

    public async Task LoadNpcAsync()
    {
        Diagnostics.Clear();
        Identity.Clear();
        Collections.Clear();
        Stats.Clear();
        if (!FormId.TryParse(NpcFormId, out var formId) ||
            !TrySha256(InputSha256, out var expectedHash))
        {
            PresentInputError("Choose a copied plugin with a valid SHA-256 and NPC FormID first.");
            return;
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = "Loading NPC";
        Status = "Reopening the hash-bound source NPC and its editable lists.";
        try
        {
            var inspection = await service.InspectAsync(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(InputPlugin),
                expectedHash,
                formId,
                source.Token);
            AddDiagnostics(inspection.Diagnostics);
            if (!inspection.Available || inspection.Snapshot is null)
            {
                Verdict = "NPC unavailable";
                Status = "Nothing was changed. Correct the source selection and load it again.";
                return;
            }

            Identity.Load(inspection.Snapshot);
            Collections.Load(inspection.Snapshot);
            Stats.Load(inspection.Snapshot);
            Verdict = "NPC loaded";
            Status = "Exact source identity, archetype, statistics, and lists loaded for bounded editing.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "NPC loading cancelled. No editor state was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Load failed";
            Status = "No editor state was retained. Review the diagnostic.";
        }
        finally
        {
            Release(source);
        }
    }

    private static bool TrySha256(string text, out Sha256Hash value)
    {
        try
        {
            value = new Sha256Hash(text);
            return true;
        }
        catch (ArgumentException)
        {
            value = default;
            return false;
        }
    }

    private async Task BrowseInputAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Skyrim plugins (*.esp;*.esm;*.esl)|*.esp;*.esm;*.esl",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var selected = new WorkspacePath(dialog.FileName);
            if (!selected.IsUnder(labRoot))
                throw new ArgumentException("Choose a copied plugin under the K-only workspace.");
            InputPlugin = selected.Value;
            InputSha256 = (await HashFileAsync(selected.Value, CancellationToken.None)).Value;
            Status = "Plugin selected and SHA-256 bound. Review the target NPC.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Clear();
            Diagnostics.Add(exception.Message);
            Verdict = "Selection refused";
        }
    }

    private void BrowseOutputParent(object? parameter)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        OutputRoot = FreshChild(dialog.FolderName).Value;
    }

    private WorkspacePath CreateFreshOutputRoot()
    {
        var currentParent = Directory.GetParent(OutputRoot)?.FullName;
        if (currentParent is null || !Directory.Exists(currentParent))
        {
            currentParent = Path.Combine(labRoot.Value, ".actorwright", "work",
                "existing-npc-stats-gui-product");
            Directory.CreateDirectory(currentParent);
        }
        return FreshChild(currentParent);
    }

    private static WorkspacePath FreshChild(string parent)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(parent, "gui-run-" + stamp));
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return new Sha256Hash(Convert.ToHexString(bytes));
    }

    private void Load(ExistingNpcEditRequest request)
    {
        inputPlugin = request.InputPlugin.Value;
        inputSha256 = request.ExpectedInputSha256.Value;
        npcFormId = request.TargetFormId.ToString();
        Identity.InitializePatch(
            request.EditorId, request.Name, request.Names, request.Archetype);
        Stats.InitializePatch(request.Stats);
        outputRoot = request.OutputRoot.Value;
        pluginName = request.OutputPlugin.Value;
    }

    private void InvalidateReview()
    {
        IsReviewed = false;
        Changes.Clear();
        ResetResults();
        Verdict = "Not reviewed";
        Status = "Inputs changed. Review the exact before/after changes again.";
    }

    private void ResetResults()
    {
        HasCompleted = false;
        ProgressPercent = 0;
        ResultPlugin = ResultPluginSha256 = ResultManifest = EmptyResult;
    }

    private void PresentInputError(string message)
    {
        Diagnostics.Clear();
        Diagnostics.Add(message);
        IsReviewed = false;
        Verdict = "Input required";
        Status = "Correct the inputs and review again.";
    }

    private void PresentFailure(string ordinaryVerdict, string absentStatus)
    {
        var outputExists = Directory.Exists(OutputRoot) || File.Exists(OutputRoot);
        var collision = Diagnostics.Any(item => item.Contains("existing-npc-edit-output-exists",
            StringComparison.Ordinal));
        if (outputExists && collision)
        {
            Verdict = ordinaryVerdict;
            Status = $"The existing output path '{OutputRoot}' was refused and left untouched.";
            return;
        }
        if (outputExists)
        {
            Verdict = "Partial output — do not install";
            Status = $"Unexpected output remains at '{OutputRoot}'. Do not install it; review the diagnostics.";
            return;
        }
        Verdict = ordinaryVerdict;
        Status = absentStatus;
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> items)
    {
        foreach (var item in items.Where(item => item.Severity != DiagnosticSeverity.Info))
            Diagnostics.Add($"{item.Code}: {item.Message}");
    }

    private bool SetInput<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(ReadinessText));
        InvalidateReview();
        return true;
    }

    private bool SetSourceInput<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetInput(ref field, value, propertyName)) return false;
        Identity.Clear(discardInitialPatch: true);
        Collections.Clear();
        Stats.Clear(discardInitialPatch: true);
        OnPropertyChanged(nameof(CanEditCollections));
        OnPropertyChanged(nameof(CollectionSummary));
        OnPropertyChanged(nameof(CanEditStats));
        OnPropertyChanged(nameof(StatsSummary));
        OnPropertyChanged(nameof(CanEditIdentity));
        OnPropertyChanged(nameof(IdentitySummary));
        loadNpcCommand?.RaiseCanExecuteChanged();
        return true;
    }

    private void CollectionsChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CanEditCollections));
        OnPropertyChanged(nameof(CollectionSummary));
        OnPropertyChanged(nameof(ReadinessText));
        InvalidateReview();
    }

    private void StatsChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CanEditStats));
        OnPropertyChanged(nameof(StatsSummary));
        OnPropertyChanged(nameof(ReadinessText));
        InvalidateReview();
    }

    private void IdentityChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CanEditIdentity));
        OnPropertyChanged(nameof(IdentitySummary));
        OnPropertyChanged(nameof(ReadinessText));
        InvalidateReview();
    }

    public async Task<ExistingNpcIdentityEditorViewModel?> CreateIdentityEditorAsync()
    {
        if (!CanEditIdentity) return null;
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Status = "Loading typed Race, Voice Type, Class, and Combat Style choices.";
        try
        {
            var editor = await Identity.CreateEditorAsync(
                formChoices, new WorkspacePath(InputPlugin), source.Token);
            Status = "Typed identity choices loaded from the copied source closure.";
            return editor;
        }
        catch (OperationCanceledException)
        {
            Status = "Identity choice loading cancelled. No editor state was changed.";
            return null;
        }
        finally
        {
            Release(source);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RaiseCommandState()
    {
        browseInputCommand?.RaiseCanExecuteChanged();
        loadNpcCommand?.RaiseCanExecuteChanged();
        reviewCommand?.RaiseCanExecuteChanged();
        executeCommand?.RaiseCanExecuteChanged();
        cancelCommand?.RaiseCanExecuteChanged();
    }

    private void Release(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellation, source)) cancellation = null;
        source.Dispose();
        IsBusy = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        Collections.Changed -= CollectionsChanged;
        Stats.Changed -= StatsChanged;
        Identity.Changed -= IdentityChanged;
        var source = cancellation;
        cancellation = null;
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    private sealed class DelegateCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record ExistingNpcEditChangeViewModel(string Field, string Before, string After)
{
    internal static ExistingNpcEditChangeViewModel From(MutationChange change) =>
        new(change.Field, change.Before ?? "—", change.After ?? "—");
}
