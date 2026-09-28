using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record SkyrimSaveChoiceItem<T>(T Value, string Label);

public sealed class SkyrimSavePackageViewModel :
    INotifyPropertyChanged,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyValue = "—";
    private readonly ISkyrimSavePackageService service;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand promoteCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();
    private SkyrimSavePackageReviewArtifact? review;
    private bool choicesSpecified;

    public SkyrimSavePackageViewModel(
        ISkyrimSavePackageService service,
        WorkspacePath labRoot,
        WorkspacePath sourceRoot,
        WorkspacePath outputRoot)
    {
        this.service = service;
        this.labRoot = labRoot;
        this.sourceRoot = sourceRoot.Value;
        this.outputRoot = outputRoot.Value;
        reviewCommand = new AsyncCommand(ReviewAsync, () => !IsBusy);
        promoteCommand = new AsyncCommand(
            PromoteAsync,
            () => !IsBusy && IsReviewed && !HasCompleted);
        cancelCommand = new DelegateCommand(
            _ => cancellation?.Cancel(),
            _ => IsBusy && cancellation is not null);
        BrowseSourceCommand = new DelegateCommand(
            BrowseSource,
            _ => !IsBusy);
        BrowseOutputParentCommand = new DelegateCommand(
            BrowseOutputParent,
            _ => !IsBusy);
    }

    public ObservableCollection<string> Diagnostics { get; } = [];

    public IReadOnlyList<SkyrimSaveChoiceItem<SkyrimSaveScope>> ScopeChoices { get; } =
    [
        new(SkyrimSaveScope.SelectedOnly, "Selected NPC only"),
        new(SkyrimSaveScope.AllChanged, "All changed NPCs")
    ];

    public IReadOnlyList<SkyrimSaveChoiceItem<SkyrimSaveTargetMode>> TargetModeChoices { get; } =
    [
        new(SkyrimSaveTargetMode.FreshPackage, "Fresh package"),
        new(SkyrimSaveTargetMode.UpdateExisting, "Fresh-derived update")
    ];

    public IReadOnlyList<SkyrimSaveChoiceItem<SkyrimSaveEncodingMode>> EncodingChoices { get; } =
    [
        new(SkyrimSaveEncodingMode.PreservePackageBytes, "Preserve source encoding"),
        new(SkyrimSaveEncodingMode.Utf8, "UTF-8"),
        new(SkyrimSaveEncodingMode.Windows1252, "Windows-1252")
    ];

    public IReadOnlyList<SkyrimSaveChoiceItem<SkyrimSaveArchiveMode>> ArchiveChoices { get; } =
    [
        new(SkyrimSaveArchiveMode.PreservePackageLayout, "Preserve package layout"),
        new(SkyrimSaveArchiveMode.Loose, "Loose assets"),
        new(SkyrimSaveArchiveMode.Bsa, "Skyrim BSA v105")
    ];

    public IReadOnlyList<SkyrimSaveChoiceItem<SkyrimSaveLeveledListMode>> LeveledListChoices { get; } =
    [
        new(SkyrimSaveLeveledListMode.PreservePackageRecords, "Preserve LVLN records"),
        new(SkyrimSaveLeveledListMode.New, "Create new LVLN"),
        new(SkyrimSaveLeveledListMode.Existing, "Append existing LVLN")
    ];

    public ICommand BrowseSourceCommand { get; }

    public ICommand BrowseOutputParentCommand { get; }

    public ICommand ReviewCommand => reviewCommand;

    public ICommand PromoteCommand => promoteCommand;

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

    private SkyrimSaveScope selectedScope = SkyrimSaveScope.SelectedOnly;
    public SkyrimSaveScope SelectedScope
    {
        get => selectedScope;
        set => SetChoice(ref selectedScope, value, nameof(SelectedScope));
    }

    private SkyrimSaveTargetMode selectedTargetMode =
        SkyrimSaveTargetMode.FreshPackage;
    public SkyrimSaveTargetMode SelectedTargetMode
    {
        get => selectedTargetMode;
        set => SetChoice(
            ref selectedTargetMode,
            value,
            nameof(SelectedTargetMode));
    }

    private bool markAsMaster;
    public bool MarkAsMaster
    {
        get => markAsMaster;
        set => SetChoice(ref markAsMaster, value, nameof(MarkAsMaster));
    }

    private bool lightMaster;
    public bool LightMaster
    {
        get => lightMaster;
        set => SetChoice(ref lightMaster, value, nameof(LightMaster));
    }

    private SkyrimSaveEncodingMode selectedEncodingMode =
        SkyrimSaveEncodingMode.PreservePackageBytes;
    public SkyrimSaveEncodingMode SelectedEncodingMode
    {
        get => selectedEncodingMode;
        set => SetChoice(
            ref selectedEncodingMode,
            value,
            nameof(SelectedEncodingMode));
    }

    private SkyrimSaveArchiveMode selectedArchiveMode =
        SkyrimSaveArchiveMode.PreservePackageLayout;
    public SkyrimSaveArchiveMode SelectedArchiveMode
    {
        get => selectedArchiveMode;
        set => SetChoice(
            ref selectedArchiveMode,
            value,
            nameof(SelectedArchiveMode));
    }

    private SkyrimSaveLeveledListMode selectedLeveledListMode =
        SkyrimSaveLeveledListMode.PreservePackageRecords;
    public SkyrimSaveLeveledListMode SelectedLeveledListMode
    {
        get => selectedLeveledListMode;
        set
        {
            if (!SetChoice(
                    ref selectedLeveledListMode,
                    value,
                    nameof(SelectedLeveledListMode)))
                return;
            OnPropertyChanged(nameof(UsesLeveledList));
        }
    }

    private string leveledListEditorId = string.Empty;
    public string LeveledListEditorId
    {
        get => leveledListEditorId;
        set => SetChoice(
            ref leveledListEditorId,
            value,
            nameof(LeveledListEditorId));
    }

    private bool noDuplicateLeveledEntries;
    public bool NoDuplicateLeveledEntries
    {
        get => noDuplicateLeveledEntries;
        set => SetChoice(
            ref noDuplicateLeveledEntries,
            value,
            nameof(NoDuplicateLeveledEntries));
    }

    public bool UsesLeveledList =>
        SelectedLeveledListMode !=
        SkyrimSaveLeveledListMode.PreservePackageRecords;

    private string sourceRoot;
    public string SourceRoot
    {
        get => sourceRoot;
        set => SetPath(
            ref sourceRoot,
            value,
            nameof(SourceRoot),
            resetChoices: true);
    }

    private string outputRoot;
    public string OutputRoot
    {
        get => outputRoot;
        set => SetPath(
            ref outputRoot,
            value,
            nameof(OutputRoot),
            resetChoices: false);
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            RaiseCommandState();
        }
    }

    public bool IsNotBusy => !IsBusy;

    private bool isReviewed;
    public bool IsReviewed
    {
        get => isReviewed;
        private set
        {
            if (!Set(ref isReviewed, value)) return;
            promoteCommand.RaiseCanExecuteChanged();
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
            promoteCommand.RaiseCanExecuteChanged();
        }
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private string status =
        "Choose a verified Skyrim package and a fresh K-local destination.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string verdict = "Not reviewed";
    public string Verdict
    {
        get => verdict;
        private set => Set(ref verdict, value);
    }

    private string outputPlugin = EmptyValue;
    public string OutputPlugin
    {
        get => outputPlugin;
        private set => Set(ref outputPlugin, value);
    }

    private string targetFormId = EmptyValue;
    public string TargetFormId
    {
        get => targetFormId;
        private set => Set(ref targetFormId, value);
    }

    private int npcRecordCount;
    public int NpcRecordCount
    {
        get => npcRecordCount;
        private set => Set(ref npcRecordCount, value);
    }

    private string scopeText = "Selected only (pending review)";
    public string ScopeText
    {
        get => scopeText;
        private set => Set(ref scopeText, value);
    }

    private string targetText = "Fresh package only";
    public string TargetText
    {
        get => targetText;
        private set => Set(ref targetText, value);
    }

    private string pluginFlagsText = EmptyValue;
    public string PluginFlagsText
    {
        get => pluginFlagsText;
        private set => Set(ref pluginFlagsText, value);
    }

    private string reviewedManifestSha256 = EmptyValue;
    public string ReviewedManifestSha256
    {
        get => reviewedManifestSha256;
        private set => Set(ref reviewedManifestSha256, value);
    }

    private int fileCount;
    public int FileCount
    {
        get => fileCount;
        private set => Set(ref fileCount, value);
    }

    private int authoredRecordCount;
    public int AuthoredRecordCount
    {
        get => authoredRecordCount;
        private set => Set(ref authoredRecordCount, value);
    }

    private int leveledRecordCount;
    public int LeveledRecordCount
    {
        get => leveledRecordCount;
        private set => Set(ref leveledRecordCount, value);
    }

    private bool hasFaceGeom;
    public bool HasFaceGeom
    {
        get => hasFaceGeom;
        private set => Set(ref hasFaceGeom, value);
    }

    private bool hasFaceTint;
    public bool HasFaceTint
    {
        get => hasFaceTint;
        private set => Set(ref hasFaceTint, value);
    }

    private bool hasBodySlideSidecar;
    public bool HasBodySlideSidecar
    {
        get => hasBodySlideSidecar;
        private set => Set(ref hasBodySlideSidecar, value);
    }

    private bool hasBodyGen;
    public bool HasBodyGen
    {
        get => hasBodyGen;
        private set => Set(ref hasBodyGen, value);
    }

    private bool hasApplyScript;
    public bool HasApplyScript
    {
        get => hasApplyScript;
        private set => Set(ref hasApplyScript, value);
    }

    private bool hasArchive;
    public bool HasArchive
    {
        get => hasArchive;
        private set => Set(ref hasArchive, value);
    }

    private string resultRoot = EmptyValue;
    public string ResultRoot
    {
        get => resultRoot;
        private set => Set(ref resultRoot, value);
    }

    private string resultManifestSha256 = EmptyValue;
    public string ResultManifestSha256
    {
        get => resultManifestSha256;
        private set => Set(ref resultManifestSha256, value);
    }

    private string proposalSha256 = EmptyValue;
    public string ProposalSha256
    {
        get => proposalSha256;
        private set => Set(ref proposalSha256, value);
    }

    private string resultPluginSha256 = EmptyValue;
    public string ResultPluginSha256
    {
        get => resultPluginSha256;
        private set => Set(ref resultPluginSha256, value);
    }

    private string resultArchiveText = "No produced archive";
    public string ResultArchiveText
    {
        get => resultArchiveText;
        private set => Set(ref resultArchiveText, value);
    }

    public string ReadinessText
    {
        get
        {
            if (IsBusy) return "A package transaction is active.";
            if (!IsReviewed)
                return "Review re-hashes the complete source and checks the fresh destination.";
            if (HasCompleted)
                return "The destination was reopened and matches every declared source artifact.";
            return "Review is current. Promotion will revalidate before writing.";
        }
    }

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        InvalidateReview(clearStatus: false);
        SkyrimSavePackageReviewRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (ArgumentException exception)
        {
            PresentInputFailure(exception.Message);
            return;
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = "Reviewing";
        Status = "Inspecting and independently hashing the complete source package.";
        try
        {
            SkyrimSavePackageReviewResult result = await service.ReviewAsync(
                request,
                source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.Artifact is null)
            {
                Verdict = "Review refused";
                Status = "Nothing was written. Correct the package or destination and review again.";
                return;
            }

            review = result.Artifact;
            ApplySnapshot(result.Artifact);
            IsReviewed = true;
            Verdict = "Ready to promote";
            Status = "The complete package inventory and exact supported options are hash-bound.";
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

    public async Task PromoteAsync()
    {
        if (review is null || !IsReviewed)
        {
            PresentInputFailure("Review the package before promotion.");
            return;
        }

        Diagnostics.Clear();
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = "Promoting";
        ProgressPercent = 0;
        Status = "Revalidating the reviewed package before any write.";
        try
        {
            var progress = new Progress<SkyrimSavePackageProgress>(item =>
            {
                ProgressPercent = item.Percent;
                Status = item.Message;
            });
            SkyrimSavePackageExecutionResult result = await service.ExecuteAsync(
                new SkyrimSavePackageExecutionRequest(review),
                progress,
                source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Completed || result.Artifact is null)
            {
                Verdict = "Promotion refused";
                Status = "No destination package was retained. Review the diagnostics.";
                return;
            }

            HasCompleted = true;
            ProgressPercent = 100;
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            ResultRoot = result.Artifact.OutputRoot.Value;
            ResultManifestSha256 = result.Artifact.OutputManifestSha256.Value;
            ProposalSha256 =
                result.Artifact.ProposalSha256?.Value ?? EmptyValue;
            ResultPluginSha256 =
                result.Artifact.PluginTransform?.OutputSha256.Value ??
                EmptyValue;
            ResultArchiveText = result.Artifact.ArchiveBuild is null
                ? "Loose/preserved layout; no new BSA"
                : $"{result.Artifact.ArchiveBuild.Version} / " +
                  $"{result.Artifact.ArchiveBuild.Members.Length} verified members / " +
                  result.Artifact.ArchiveBuild.ArchiveSha256.Value;
            artifactEmitter.Commit(
                this,
                "saved-package",
                new WorkspacePath(Path.Combine(
                    result.Artifact.OutputRoot.Value,
                    "npcmanager-package.json")),
                result.Artifact.OutputManifestSha256,
                result.Artifact.TargetFormId);
            Status = "Destination reopened; proposal, plugin, layout, manifest, and every output hash verified.";
        }
        catch (OperationCanceledException)
        {
            ProgressPercent = 0;
            Verdict = "Cancelled";
            Status = "Promotion cancelled. No partial destination was retained.";
            ClearResults();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            ProgressPercent = 0;
            Verdict = "Promotion failed";
            Status = "No destination package was retained. Review the diagnostic.";
            ClearResults();
        }
        finally
        {
            Release(source);
        }
    }

    private SkyrimSavePackageReviewRequest BuildRequest()
    {
        var source = new WorkspacePath(SourceRoot);
        var output = new WorkspacePath(OutputRoot);
        if (!source.IsUnder(labRoot) || !output.IsUnder(labRoot))
            throw new ArgumentException(
                "Source and destination must remain under the K-only workspace.");
        SkyrimSavePackageChoiceRequest? choices = choicesSpecified
            ? new SkyrimSavePackageChoiceRequest(
                SelectedScope,
                SelectedTargetMode,
                MarkAsMaster,
                LightMaster,
                SelectedEncodingMode,
                SelectedArchiveMode,
                SelectedLeveledListMode,
                UsesLeveledList
                    ? LeveledListEditorId
                    : null,
                UsesLeveledList && NoDuplicateLeveledEntries)
            : null;
        return new SkyrimSavePackageReviewRequest(
            source,
            output,
            choices);
    }

    private void ApplySnapshot(SkyrimSavePackageReviewArtifact artifact)
    {
        SkyrimSavePackageSnapshot snapshot = artifact.Snapshot;
        OutputPlugin = snapshot.OutputPlugin.Value;
        TargetFormId = snapshot.TargetFormId.ToString();
        NpcRecordCount = snapshot.NpcRecordCount;
        ScopeText = artifact.Options.Scope == SkyrimSaveScope.SelectedOnly
            ? $"Selected only ({snapshot.NpcRecordCount} NPC in source)"
            : $"All changed ({snapshot.NpcRecordCount} " +
              (snapshot.NpcRecordCount == 1 ? "NPC)" : "NPCs)");
        TargetText = artifact.Options.TargetMode ==
                     SkyrimSaveTargetMode.FreshPackage
            ? "Fresh package"
            : "Fresh-derived update (source is not overwritten)";
        PluginFlagsText =
            $"ESM={artifact.Options.MarkAsMaster}; ESL={artifact.Options.LightMaster}";
        ReviewedManifestSha256 = artifact.ManifestSha256.Value;
        FileCount = snapshot.Files.Length;
        AuthoredRecordCount = snapshot.AuthoredRecordCount;
        LeveledRecordCount = snapshot.LeveledRecordCount;
        HasFaceGeom = snapshot.HasFaceGeom;
        HasFaceTint = snapshot.HasFaceTint;
        HasBodySlideSidecar = snapshot.HasBodySlideSidecar;
        HasBodyGen = snapshot.HasBodyGen;
        HasApplyScript = snapshot.HasApplyScript;
        HasArchive = snapshot.HasArchive;
        ApplyChoiceDefaults(artifact.Options);
    }

    private void ApplyChoiceDefaults(
        SkyrimSavePackageOptions options)
    {
        selectedScope = options.Scope;
        selectedTargetMode = options.TargetMode;
        markAsMaster = options.MarkAsMaster;
        lightMaster = options.LightMaster;
        selectedEncodingMode = options.EncodingMode;
        selectedArchiveMode = options.ArchiveMode;
        selectedLeveledListMode = options.LeveledListMode;
        leveledListEditorId =
            options.LeveledListEditorId ?? string.Empty;
        noDuplicateLeveledEntries =
            options.NoDuplicateLeveledEntries;
        choicesSpecified = true;
        OnPropertyChanged(nameof(SelectedScope));
        OnPropertyChanged(nameof(SelectedTargetMode));
        OnPropertyChanged(nameof(MarkAsMaster));
        OnPropertyChanged(nameof(LightMaster));
        OnPropertyChanged(nameof(SelectedEncodingMode));
        OnPropertyChanged(nameof(SelectedArchiveMode));
        OnPropertyChanged(nameof(SelectedLeveledListMode));
        OnPropertyChanged(nameof(LeveledListEditorId));
        OnPropertyChanged(nameof(NoDuplicateLeveledEntries));
        OnPropertyChanged(nameof(UsesLeveledList));
    }

    private bool SetChoice<T>(
        ref T field,
        T value,
        string propertyName)
    {
        if (IsBusy ||
            EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        choicesSpecified = true;
        OnPropertyChanged(propertyName);
        InvalidateReview(clearStatus: true);
        return true;
    }

    private void SetPath(
        ref string field,
        string value,
        string propertyName,
        bool resetChoices)
    {
        if (IsBusy) return;
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        if (resetChoices) choicesSpecified = false;
        OnPropertyChanged(propertyName);
        InvalidateReview(clearStatus: true);
    }

    private void InvalidateReview(bool clearStatus)
    {
        review = null;
        IsReviewed = false;
        HasCompleted = false;
        ClearSnapshot();
        ClearResults();
        if (clearStatus)
        {
            Verdict = "Not reviewed";
            Status = "A bound path changed. Review the package again.";
        }
        OnPropertyChanged(nameof(ReadinessText));
    }

    private void ClearSnapshot()
    {
        OutputPlugin = EmptyValue;
        TargetFormId = EmptyValue;
        NpcRecordCount = 0;
        ScopeText = "Selected only (pending review)";
        TargetText = "Fresh package only";
        PluginFlagsText = EmptyValue;
        ReviewedManifestSha256 = EmptyValue;
        FileCount = 0;
        AuthoredRecordCount = 0;
        LeveledRecordCount = 0;
        HasFaceGeom = false;
        HasFaceTint = false;
        HasBodySlideSidecar = false;
        HasBodyGen = false;
        HasApplyScript = false;
        HasArchive = false;
    }

    private void ClearResults()
    {
        HasCompleted = false;
        ResultRoot = EmptyValue;
        ResultManifestSha256 = EmptyValue;
        ProposalSha256 = EmptyValue;
        ResultPluginSha256 = EmptyValue;
        ResultArchiveText = "No produced archive";
    }

    private void BrowseSource(object? parameter)
    {
        _ = parameter;
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        SourceRoot = dialog.FolderName;
    }

    private void BrowseOutputParent(object? parameter)
    {
        _ = parameter;
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        OutputRoot = FreshChild(dialog.FolderName).Value;
    }

    private static WorkspacePath FreshChild(string parent)
    {
        string stamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(
            parent,
            "npc-package-promotion-" + stamp));
    }

    private void PresentInputFailure(string message)
    {
        Diagnostics.Clear();
        Diagnostics.Add(message);
        Verdict = "Input refused";
        Status = "Nothing was written. Correct the explicit path and review again.";
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> items)
    {
        foreach (Diagnostic item in items)
            Diagnostics.Add($"{item.Code}: {item.Message}");
    }

    private void Release(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellation, source)) cancellation = null;
        source.Dispose();
        IsBusy = false;
        OnPropertyChanged(nameof(ReadinessText));
    }

    private void RaiseCommandState()
    {
        reviewCommand.RaiseCanExecuteChanged();
        promoteCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
        (BrowseSourceCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (BrowseOutputParentCommand as DelegateCommand)?.RaiseCanExecuteChanged();
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

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    private sealed class DelegateCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null) : ICommand
    {
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => execute(parameter);

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
