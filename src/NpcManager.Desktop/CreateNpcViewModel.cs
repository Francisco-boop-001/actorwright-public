using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

public sealed class CreateNpcViewModel : INotifyPropertyChanged, IDisposable
{
    private const string EmptyResult = "—";

    private readonly IBlankNpcBuildService service;
    private readonly BlankNpcProviderService? providerSelector;
    private readonly BlankNpcBuildRequest initialRequest;
    private readonly AsyncCommand createCommand;
    private readonly AsyncCommand selectProviderCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private BlankNpcProviderBindingRequest? selectedProviderRequest;
    private bool isSelectingProvider;
    private string providerSelectionStatus =
        "No appearance provider is selected. Install a legally obtained provider bundle inside the admitted workspace, then choose its provider-manifest.json.";

    public CreateNpcViewModel(
        IBlankNpcBuildService service,
        BlankNpcBuildRequest initialRequest,
        BlankNpcProviderService? providerSelector = null)
    {
        this.service = service;
        this.providerSelector = providerSelector;
        this.initialRequest = initialRequest;
        RoleOptions =
        [
            new("Static validation", NpcCreationRole.StaticValidation)
        ];
        SexOptions = [new("Female provider", NpcSex.Female)];
        Load(initialRequest);
        createCommand = new AsyncCommand(CreateAsync,
            () => !IsBusy && !HasCompleted && selectedProviderRequest is not null);
        selectProviderCommand = new AsyncCommand(SelectProviderManifestAsync,
            () => !IsBusy && !isSelectingProvider);
        cancelCommand = new DelegateCommand(_ => cancellation?.Cancel(), _ => IsBusy);
        BrowseFolderCommand = new DelegateCommand(BrowseFolder);
    }

    public IReadOnlyList<Choice<NpcCreationRole>> RoleOptions { get; }
    public IReadOnlyList<Choice<NpcSex>> SexOptions { get; }
    public ObservableCollection<string> Diagnostics { get; } = [];
    public ICommand CreateCommand => createCommand;
    public ICommand SelectProviderCommand => selectProviderCommand;
    public ICommand CancelCommand => cancelCommand;
    public ICommand BrowseFolderCommand { get; }

    private string displayName = string.Empty;
    public string DisplayName { get => displayName; set => SetInput(ref displayName, value); }

    private string editorId = string.Empty;
    public string EditorId { get => editorId; set => SetInput(ref editorId, value); }

    private string pluginName = string.Empty;
    public string PluginName { get => pluginName; set => SetInput(ref pluginName, value); }

    private string outputRoot = string.Empty;
    public string OutputRoot { get => outputRoot; set => SetInput(ref outputRoot, value); }

    private NpcCreationRole selectedRole;
    public NpcCreationRole SelectedRole { get => selectedRole; set => SetInput(ref selectedRole, value); }

    private NpcSex selectedSex;
    public NpcSex SelectedSex
    {
        get => selectedSex;
        set
        {
            if (!SetInput(ref selectedSex, value)) return;
            selectedProviderRequest = null;
            providerSelectionStatus =
                "Provider selection cleared because the requested sex changed. Choose a matching provider manifest.";
            OnPropertyChanged(nameof(ProviderSelectionStatus));
            createCommand?.RaiseCanExecuteChanged();
        }
    }

    private string providerManifest = string.Empty;
    public string ProviderManifest { get => providerManifest; private set => SetInput(ref providerManifest, value); }

    public string ProviderSelectionStatus => providerSelectionStatus;

    private string templatePlugin = string.Empty;
    public string TemplatePlugin { get => templatePlugin; set => SetInput(ref templatePlugin, value); }

    private string templateSha256 = string.Empty;
    public string TemplateSha256 { get => templateSha256; set => SetInput(ref templateSha256, value); }

    private string templateNpc = string.Empty;
    public string TemplateNpc { get => templateNpc; set => SetInput(ref templateNpc, value); }

    private string faceGeomCarrier = string.Empty;
    public string FaceGeomCarrier { get => faceGeomCarrier; set => SetInput(ref faceGeomCarrier, value); }

    private string faceGeomSha256 = string.Empty;
    public string FaceGeomSha256 { get => faceGeomSha256; set => SetInput(ref faceGeomSha256, value); }

    private string faceTintManifest = string.Empty;
    public string FaceTintManifest { get => faceTintManifest; set => SetInput(ref faceTintManifest, value); }

    private string providerRoot = string.Empty;
    public string ProviderRoot { get => providerRoot; set => SetInput(ref providerRoot, value); }

    private string dependencyManifest = string.Empty;
    public string DependencyManifest { get => dependencyManifest; set => SetInput(ref dependencyManifest, value); }

    private string race = string.Empty;
    public string Race { get => race; set => SetInput(ref race, value); }

    private string voice = string.Empty;
    public string Voice { get => voice; set => SetInput(ref voice, value); }

    private string actorClass = string.Empty;
    public string ActorClass { get => actorClass; set => SetInput(ref actorClass, value); }

    private string combatStyle = string.Empty;
    public string CombatStyle { get => combatStyle; set => SetInput(ref combatStyle, value); }

    private string defaultOutfit = string.Empty;
    public string DefaultOutfit { get => defaultOutfit; set => SetInput(ref defaultOutfit, value); }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            createCommand?.RaiseCanExecuteChanged();
            cancelCommand?.RaiseCanExecuteChanged();
        }
    }
    public bool IsNotBusy => !IsBusy;

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set
        {
            if (!Set(ref hasCompleted, value)) return;
            createCommand?.RaiseCanExecuteChanged();
        }
    }

    private int progressPercent;
    public int ProgressPercent { get => progressPercent; private set => Set(ref progressPercent, value); }

    private string status = "Ready to build a new Skyrim NPC.";
    public string Status { get => status; private set => Set(ref status, value); }

    private string verdict = "Not built";
    public string Verdict { get => verdict; private set => Set(ref verdict, value); }

    private string resultPlugin = EmptyResult;
    public string ResultPlugin { get => resultPlugin; private set => Set(ref resultPlugin, value); }

    private string resultFaceGeom = EmptyResult;
    public string ResultFaceGeom { get => resultFaceGeom; private set => Set(ref resultFaceGeom, value); }

    private string resultFaceTint = EmptyResult;
    public string ResultFaceTint { get => resultFaceTint; private set => Set(ref resultFaceTint, value); }

    private string resultManifest = EmptyResult;
    public string ResultManifest { get => resultManifest; private set => Set(ref resultManifest, value); }

    public string ReadinessText
    {
        get
        {
            if (selectedProviderRequest is null)
                return providerSelectionStatus;
            var requiredFiles = new[]
            {
                selectedProviderRequest.ManifestPath.Value,
                selectedProviderRequest.TemplatePlugin.Value,
                selectedProviderRequest.FaceGeomCarrier.Value,
                selectedProviderRequest.FaceTintManifest.Value,
                selectedProviderRequest.DependencyManifest.Value
            };
            if (requiredFiles.Any(path => !File.Exists(path)) ||
                !Directory.Exists(selectedProviderRequest.FaceTintProviderRoot.Value))
                return "An admitted provider file changed or disappeared. Choose the provider manifest again.";
            if (Directory.Exists(OutputRoot) || File.Exists(OutputRoot))
                return "Choose a fresh output folder; this path already exists.";
            return "Provider admission passed. Build rechecks every boundary and hash before writing.";
        }
    }

    private async Task CreateAsync()
    {
        Diagnostics.Clear();
        HasCompleted = false;
        Verdict = "Building";
        ResultPlugin = ResultFaceGeom = ResultFaceTint = ResultManifest = EmptyResult;
        ProgressPercent = 0;
        BlankNpcBuildRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (ArgumentException exception)
        {
            Diagnostics.Add(exception.Message);
            Status = "Review the inputs and try again.";
            Verdict = "Input required";
            return;
        }

        using Activity activity = new Activity("Actorwright.Desktop.BlankNpcBuild")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        var cancellationSource = new CancellationTokenSource();
        cancellation = cancellationSource;
        IsBusy = true;
        try
        {
            var progress = new Progress<BlankNpcBuildProgress>(item =>
            {
                ProgressPercent = item.Percent;
                Status = item.Message;
            });
            var result = await service.ExecuteAsync(request, progress, cancellationSource.Token);
            foreach (var diagnostic in result.Diagnostics.Where(item => item.Severity != DiagnosticSeverity.Info))
                Diagnostics.Add($"{diagnostic.Code}: {diagnostic.Message}");
            if (!result.Completed || result.Artifact is null)
            {
                RecordFailureEvidence(
                    activity,
                    ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                    result.Diagnostics.Select(item => item.Code));
                PresentFailure("Build refused", "No package was retained. Review the diagnostics.");
                return;
            }

            HasCompleted = true;
            Verdict = result.Artifact.Verdict;
            ResultPlugin = result.Artifact.Plugin.Value;
            ResultFaceGeom = result.Artifact.FaceGeom.Value;
            ResultFaceTint = result.Artifact.FaceTint.Value;
            ResultManifest = result.Artifact.Manifest.Value;
            Status = "New NPC package created and independently verified.";
        }
        catch (OperationCanceledException)
        {
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Cancelled,
                []);
            PresentFailure("Cancelled", "Creation cancelled. No partial package was retained.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Exception,
                []);
            Diagnostics.Add(exception.Message);
            PresentFailure("Build failed", "No package was retained. Review the diagnostics.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Exception,
                []);
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            PresentFailure("Build failed", "No package was retained. Review the diagnostics.");
        }
        finally
        {
            if (ReferenceEquals(cancellation, cancellationSource)) cancellation = null;
            cancellationSource.Dispose();
            IsBusy = false;
            OnPropertyChanged(nameof(ReadinessText));
        }
    }

    private BlankNpcBuildRequest BuildRequest()
    {
        if (selectedProviderRequest is null)
            throw new ArgumentException(providerSelectionStatus);
        if (!FormId.TryParse(TemplateNpc, out var templateNpc))
            throw new ArgumentException("Template NPC must be a hexadecimal FormID.");
        if (!FormReference.TryParse(Race, out var raceReference) ||
            !FormReference.TryParse(Voice, out var voiceReference) ||
            !FormReference.TryParse(ActorClass, out var classReference) ||
            !FormReference.TryParse(CombatStyle, out var combatReference))
            throw new ArgumentException(
                "Race, voice, class, and combat style must use Plugin|0xFormID.");
        FormReference? outfitReference = null;
        if (!string.IsNullOrWhiteSpace(DefaultOutfit))
        {
            if (!FormReference.TryParse(
                    DefaultOutfit, out var parsedOutfit))
                throw new ArgumentException(
                    "Default outfit must be blank or use Plugin|0xFormID.");
            outfitReference = parsedOutfit;
        }
        return initialRequest with
        {
            ProviderManifest = selectedProviderRequest.ManifestPath,
            ExpectedProviderManifestSha256 = selectedProviderRequest.ExpectedManifestSha256,
            TemplatePlugin = selectedProviderRequest.TemplatePlugin,
            ExpectedTemplatePluginSha256 = selectedProviderRequest.ExpectedTemplatePluginSha256,
            TemplateNpcFormId = templateNpc,
            FaceGeomCarrier = selectedProviderRequest.FaceGeomCarrier,
            ExpectedFaceGeomCarrierSha256 = selectedProviderRequest.ExpectedFaceGeomCarrierSha256,
            FaceTintManifest = selectedProviderRequest.FaceTintManifest,
            FaceTintProviderRoot = selectedProviderRequest.FaceTintProviderRoot,
            DependencyManifest = selectedProviderRequest.DependencyManifest,
            OutputRoot = new WorkspacePath(OutputRoot),
            OutputPlugin = new PluginName(PluginName),
            Identity = new NpcCreationIdentity(new EditorId(EditorId), new NpcName(DisplayName)),
            Traits = initialRequest.Traits with { Sex = SelectedSex, Role = SelectedRole },
            References = new SkyrimNpcCreationReferences(raceReference, voiceReference,
                classReference, combatReference, outfitReference)
        };
    }

    private void BrowseFolder(object? parameter)
    {
        var target = parameter?.ToString();
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        if (target == "output")
            OutputRoot = Path.Combine(dialog.FolderName,
                "NpcManagerBuild-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    private async Task SelectProviderManifestAsync()
    {
        if (providerSelector is null)
        {
            providerSelectionStatus = "Provider selection is unavailable in this composition.";
            OnPropertyChanged(nameof(ProviderSelectionStatus));
            OnPropertyChanged(nameof(ReadinessText));
            return;
        }

        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Provider manifest (provider-manifest.json)|provider-manifest.json|JSON files (*.json)|*.json",
            Title = "Choose an installed appearance provider manifest"
        };
        string? initialDirectory = Path.GetDirectoryName(initialRequest.ProviderManifest.Value);
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        if (dialog.ShowDialog() != true) return;

        await SelectProviderManifestAsync(new WorkspacePath(dialog.FileName));
    }

    internal async Task SelectProviderManifestAsync(WorkspacePath manifestPath)
    {
        if (providerSelector is null)
        {
            providerSelectionStatus = "Provider selection is unavailable in this composition.";
            OnPropertyChanged(nameof(ProviderSelectionStatus));
            OnPropertyChanged(nameof(ReadinessText));
            return;
        }

        selectedProviderRequest = null;
        ProviderManifest = manifestPath.Value;
        Diagnostics.Clear();
        isSelectingProvider = true;
        selectProviderCommand.RaiseCanExecuteChanged();
        createCommand.RaiseCanExecuteChanged();
        providerSelectionStatus = "Checking the selected manifest and every referenced provider file…";
        OnPropertyChanged(nameof(ProviderSelectionStatus));
        OnPropertyChanged(nameof(ReadinessText));
        try
        {
            var request = await providerSelector.SelectWorkspaceManifestAsync(
                manifestPath, initialRequest.Edition,
                SelectedSex, CancellationToken.None);
            selectedProviderRequest = request;
            ProviderManifest = request.ManifestPath.Value;
            TemplatePlugin = request.TemplatePlugin.Value;
            TemplateSha256 = request.ExpectedTemplatePluginSha256.Value;
            TemplateNpc = request.TemplateNpcFormId.ToString();
            FaceGeomCarrier = request.FaceGeomCarrier.Value;
            FaceGeomSha256 = request.ExpectedFaceGeomCarrierSha256.Value;
            FaceTintManifest = request.FaceTintManifest.Value;
            ProviderRoot = request.FaceTintProviderRoot.Value;
            DependencyManifest = request.DependencyManifest.Value;
            providerSelectionStatus =
                "Provider manifest and referenced files passed admission. They will be checked again at build time.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or InvalidDataException or JsonException)
        {
            Diagnostics.Add($"provider-manifest-refused: {exception.Message}");
            providerSelectionStatus =
                "The selected provider manifest is missing, malformed, outside the admitted workspace, or failed file/hash admission. Review diagnostics and choose a valid provider.";
        }
        finally
        {
            isSelectingProvider = false;
            selectProviderCommand.RaiseCanExecuteChanged();
            createCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ProviderSelectionStatus));
            OnPropertyChanged(nameof(ReadinessText));
        }
    }

    private void Load(BlankNpcBuildRequest request)
    {
        displayName = request.Identity.Name.Value;
        editorId = request.Identity.EditorId.Value;
        pluginName = request.OutputPlugin.Value;
        outputRoot = request.OutputRoot.Value;
        selectedRole = request.Traits.Role;
        selectedSex = request.Traits.Sex;
        providerManifest = string.Empty;
        templatePlugin = string.Empty;
        templateSha256 = string.Empty;
        templateNpc = string.Empty;
        faceGeomCarrier = string.Empty;
        faceGeomSha256 = string.Empty;
        faceTintManifest = string.Empty;
        providerRoot = string.Empty;
        dependencyManifest = string.Empty;
        race = request.References.Race.ToString();
        voice = request.References.Voice.ToString();
        actorClass = request.References.Class.ToString();
        combatStyle = request.References.CombatStyle.ToString();
        defaultOutfit = request.References.DefaultOutfit?.ToString() ??
            string.Empty;
    }

    private bool SetInput<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        var completedResultWasVisible = HasCompleted;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(ReadinessText));
        if (completedResultWasVisible) ResetCompletedResult();
        return true;
    }

    private void ResetCompletedResult()
    {
        HasCompleted = false;
        ProgressPercent = 0;
        Verdict = "Not built";
        ResultPlugin = ResultFaceGeom = ResultFaceTint = ResultManifest = EmptyResult;
        Status = "Inputs changed. Build again to verify the new request.";
        Diagnostics.Clear();
    }

    private void RecordFailureEvidence(
        Activity activity,
        ActorwrightObservabilityEventSource.DesktopFailureKindId failureKind,
        IEnumerable<string> diagnosticCodes)
    {
        DesktopEvidenceStoreWriteResult? write =
            DesktopFailureEvidenceListener.RecordCurrentOperationFailure(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureOperationId.BlankNpcBuild,
                failureKind,
                diagnosticCodes);
        if (write is { Written: false })
            Diagnostics.Add(
                "Desktop failure evidence could not be safely saved.");
    }

    private void PresentFailure(string ordinaryVerdict, string absentStatus)
    {
        var rollbackIncomplete = Diagnostics.Any(item =>
            item.Contains("blank-npc-rollback-incomplete", StringComparison.Ordinal) ||
            item.Contains("blank-npc-output-create-unclaimed", StringComparison.Ordinal));
        var outputExists = Directory.Exists(OutputRoot) || File.Exists(OutputRoot);
        var untouchedCollision = !rollbackIncomplete && Diagnostics.Any(item =>
            item.Contains("blank-npc-output-exists", StringComparison.Ordinal) ||
            item.Contains("blank-npc-output-raced", StringComparison.Ordinal));
        if (outputExists && untouchedCollision)
        {
            Verdict = ordinaryVerdict;
            Status = $"The existing output path '{OutputRoot}' was refused and left untouched.";
            return;
        }
        if (rollbackIncomplete || outputExists)
        {
            Verdict = "Partial output — do not install";
            Status = $"A partial or unclaimed output remains at '{OutputRoot}'. Do not install it; review the diagnostics.";
            return;
        }
        Verdict = ordinaryVerdict;
        Status = absentStatus;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        var source = cancellation;
        cancellation = null;
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    public sealed record Choice<T>(string Label, T Value);

    private sealed class DelegateCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
