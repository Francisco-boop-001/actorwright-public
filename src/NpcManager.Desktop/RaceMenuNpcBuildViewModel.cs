using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

/// <summary>
/// Desktop adapter over the same immutable request loader and execution service
/// used by the CLI. It changes only the in-memory output and NPC identity.
/// </summary>
public sealed class RaceMenuNpcBuildViewModel :
    INotifyPropertyChanged,
    IDisposable,
    ISkyrimMainWorkspaceArtifactOwner
{
    private const string EmptyResult = "\u2014";
    private static readonly TimeSpan MinimumCancellationAcknowledgement =
        TimeSpan.FromMilliseconds(750);
    private readonly IRaceMenuNpcExecutionRequestFileLoader requestLoader;
    private readonly IRaceMenuNpcBuildService buildService;
    private readonly IRaceMenuPresetCatalogService presetCatalogService;
    private readonly IRaceMenuPresetPreviewService? presetPreviewService;
    private readonly IRaceMenuPresetSelectionTransactionService? presetSelectionTransactionService;
    private readonly IRaceMenuJslotNpcBuildService? jslotNpcBuildService;
    private readonly INpcBuildPreflightService? preflightService;
    private readonly INpcVisualPreviewComposer? npcVisualPreviewComposer;
    private readonly IDisposable presetCatalogLifetime;
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath outputParent;
    private readonly string targetCardEyebrow = "3  APPEARANCE";
    private readonly AsyncCommand browseRequestCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand reviewPreflightCommand;
    private readonly AsyncCommand runCommand;
    private readonly AsyncCommand cancelCommand;
    private readonly DelegateCommand browseOutputCommand;
    private readonly DelegateCommand newOutputCommand;
    private RaceMenuNpcExecutionRequest? loadedRequest;
    private ReviewedGameIntake? reviewedIntake;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? cancellationAcknowledgementSource;
    private Task cancellationAcknowledgement = Task.CompletedTask;
    private readonly SkyrimMainWorkspaceArtifactEmitter artifactEmitter = new();
    private RaceMenuExistingNpcTarget? workspaceExistingNpcTarget;
    private ReferencePresetNpcDesktopHandoff? referencePresetHandoff;
    private NpcBuildPreflightDocument? reviewedPreflightDocument;
    private NpcBuildPreflightReviewAuthority? reviewedPreflightAuthority;
    private WorkspacePath? reviewedCompanionRoot;

    public RaceMenuNpcBuildViewModel(
        IRaceMenuNpcExecutionRequestFileLoader requestLoader,
        IRaceMenuNpcBuildService buildService,
        IRaceMenuPresetCatalogService presetCatalogService,
        IDisposable presetCatalogLifetime,
        WorkspacePath labRoot,
        WorkspacePath initialRequestFile,
        string initialRequestSha256,
        WorkspacePath outputParent,
        IRaceMenuPresetPreviewService? presetPreviewService = null,
        IRaceMenuPresetSelectionTransactionService? presetSelectionTransactionService = null,
        IRaceMenuJslotNpcBuildService? jslotNpcBuildService = null,
        INpcBuildPreflightService? preflightService = null,
        INpcVisualPreviewComposer? npcVisualPreviewComposer = null)
    {
        this.requestLoader = requestLoader;
        this.buildService = buildService;
        this.presetCatalogService = presetCatalogService;
        this.presetPreviewService = presetPreviewService;
        this.presetSelectionTransactionService = presetSelectionTransactionService;
        this.jslotNpcBuildService = jslotNpcBuildService;
        this.preflightService = preflightService;
        this.npcVisualPreviewComposer = npcVisualPreviewComposer;
        this.presetCatalogLifetime = presetCatalogLifetime;
        this.labRoot = labRoot;
        this.outputParent = outputParent;
        requestPath = initialRequestFile.Value;
        requestSha256 = initialRequestSha256;
        browseRequestCommand = new AsyncCommand(BrowseRequestAsync, () => !IsBusy);
        reviewCommand = new AsyncCommand(ReviewAsync,
            () => !IsBusy && !string.IsNullOrWhiteSpace(RequestPath) &&
                  !string.IsNullOrWhiteSpace(RequestSha256));
        reviewPreflightCommand = new AsyncCommand(ReviewPreflightAsync,
            () => CanReviewPreflight);
        runCommand = new AsyncCommand(RunAsync, () => !IsBusy && IsReady);
        cancelCommand = new AsyncCommand(RequestCancellationAsync,
            () => IsBusy && !IsCancelling);
        browseOutputCommand = new DelegateCommand(_ => BrowseOutput(), _ => !IsBusy);
        newOutputCommand = new DelegateCommand(_ => OutputRoot = CreateFreshOutputRoot().Value,
            _ => !IsBusy && HasReviewedRequest);
        ResetResults();
    }

    public ObservableCollection<string> Diagnostics { get; } = [];
    public bool IsExistingNpcTarget =>
        loadedRequest?.Build.ExistingNpcTarget is not null;
    public bool IsNewNpcTarget => !IsExistingNpcTarget;
    public bool CanEditIdentity => IsNewNpcTarget && IsNotBusy;
    public bool CanChoosePreset => HasReviewedRequest && reviewedIntake is not null && IsNotBusy;
    public string WorkspaceAutomationName => IsExistingNpcTarget
        ? "Preset to existing NPC workspace"
        : "Preset to new NPC workspace";
    public string HeroEyebrow => IsExistingNpcTarget
        ? "RACEMENU PRESET  /  EXISTING NPC"
        : "RACEMENU PRESET  /  NEW NPC";
    public string HeroTitle => IsExistingNpcTarget
        ? "Bake a prepared preset into an existing NPC override"
        : "Turn a prepared preset into a verified package";
    public string HeroDescription => IsExistingNpcTarget
        ? "Review the exact preset, CharGen face, tint, and source-owned NPC first. The app then creates a fresh override plugin, FaceGen, BodyGen, runtime PEX, and package through the same service used by the CLI."
        : "Review the exact preset, CharGen face, and tint first. The app then creates a fresh NPC plugin, FaceGen, BodyGen, runtime PEX, and package through the same service used by the CLI.";
    public string TargetCardEyebrow => targetCardEyebrow;
    public string TargetCardTitle =>
        IsExistingNpcTarget ? "Retained identity and fresh override" : "Identity and fresh output";
    public string IdentityHelpText => IsExistingNpcTarget
        ? "Display name and Editor ID belong to the source NPC and cannot be changed by this appearance-only override."
        : "Choose the in-game display name and stable Editor ID for the new NPC.";
    public string PluginLabel => IsExistingNpcTarget
        ? "Override plugin filename"
        : "Plugin filename";
    public string BuildActionContent => PreviewReady
        ? "Build, verify & preview"
        : "Build & verify";
    public string BuildActionName => PreviewReady
        ? "Build verify and preview NPC package"
        : "Build and verify NPC package";
    public string BuildActionHelpText => PreviewReady
        ? "Builds the reviewed package, verifies it, then renders the separate six-view off-engine preview."
        : "Builds and verifies the reviewed package; preview prerequisites are unavailable.";
    public ICommand BrowseRequestCommand => browseRequestCommand;
    public ICommand ReviewCommand => reviewCommand;
    public ICommand ReviewPreflightCommand => reviewPreflightCommand;
    public ICommand RunCommand => runCommand;
    public ICommand CancelCommand => cancelCommand;
    public ICommand BrowseOutputCommand => browseOutputCommand;
    public ICommand NewOutputCommand => newOutputCommand;

    public event EventHandler<SkyrimMainWorkspaceArtifactHandoff>?
        ArtifactCommitted
    {
        add => artifactEmitter.ArtifactCommitted += value;
        remove => artifactEmitter.ArtifactCommitted -= value;
    }

    public void BindWorkspaceIdentity(
        SkyrimMainWorkspaceIdentity identity) =>
        artifactEmitter.Bind(identity);
    public string CancelActionContent => IsCancelling ? "Cancelling..." : "_Cancel";
    public string CancelActionName => IsCancelling
        ? "Cancelling preset NPC build"
        : "Cancel preset NPC build";

    private string bodySlideChoice = "BodySlide sliders: apply";
    public string BodySlideChoice
    {
        get => bodySlideChoice;
        private set => Set(ref bodySlideChoice, value);
    }

    private string requestPath;
    public string RequestPath
    {
        get => requestPath;
        set
        {
            if (!Set(ref requestPath, value)) return;
            InvalidateReview("Request path changed. Review the prepared request again.");
        }
    }

    private string requestSha256;
    public string RequestSha256
    {
        get => requestSha256;
        set
        {
            if (!Set(ref requestSha256, value)) return;
            InvalidateReview("Request hash changed. Review the prepared request again.");
        }
    }

    private bool hasReviewedRequest;
    public bool HasReviewedRequest
    {
        get => hasReviewedRequest;
        private set
        {
            if (!Set(ref hasReviewedRequest, value)) return;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(CanChoosePreset));
            RaiseCommands();
        }
    }

    private bool hasReviewedPreflight;
    public bool HasReviewedPreflight
    {
        get => hasReviewedPreflight;
        private set
        {
            if (!Set(ref hasReviewedPreflight, value)) return;
            OnPropertyChanged(nameof(PreviewReady));
            OnPropertyChanged(nameof(BuildActionContent));
            OnPropertyChanged(nameof(BuildActionName));
            OnPropertyChanged(nameof(BuildActionHelpText));
            RefreshReadiness();
        }
    }

    public bool PreviewReady => HasReviewedPreflight &&
        reviewedPreflightDocument?.Value.PreviewReady == true &&
        reviewedIntake is not null &&
        npcVisualPreviewComposer is not null;

    private string preflightSummary =
        "Review the production gates before build.";
    public string PreflightSummary
    {
        get => preflightSummary;
        private set => Set(ref preflightSummary, value);
    }

    private string preflightPath = EmptyResult;
    public string PreflightPath
    {
        get => preflightPath;
        private set => Set(ref preflightPath, value);
    }

    private string preflightSha256 = EmptyResult;
    public string PreflightSha256
    {
        get => preflightSha256;
        private set => Set(ref preflightSha256, value);
    }

    private string presetPath = EmptyResult;
    public string PresetPath { get => presetPath; private set => Set(ref presetPath, value); }

    private string presetHash = EmptyResult;
    public string PresetHash { get => presetHash; private set => Set(ref presetHash, value); }

    private string charGenPath = EmptyResult;
    public string CharGenPath { get => charGenPath; private set => Set(ref charGenPath, value); }

    private string charGenHash = EmptyResult;
    public string CharGenHash { get => charGenHash; private set => Set(ref charGenHash, value); }

    private string faceTintPath = EmptyResult;
    public string FaceTintPath { get => faceTintPath; private set => Set(ref faceTintPath, value); }

    private string faceTintHash = EmptyResult;
    public string FaceTintHash { get => faceTintHash; private set => Set(ref faceTintHash, value); }

    private string sourceSummary = "Choose and review a prepared request.";
    public string SourceSummary { get => sourceSummary; private set => Set(ref sourceSummary, value); }

    private string displayName = string.Empty;
    public string DisplayName { get => displayName; set => SetOverride(ref displayName, value); }

    private string editorId = string.Empty;
    public string EditorId { get => editorId; set => SetOverride(ref editorId, value); }

    private string pluginName = string.Empty;
    public string PluginName { get => pluginName; set => SetOverride(ref pluginName, value); }

    private string outputRoot = string.Empty;
    public string OutputRoot { get => outputRoot; set => SetOverride(ref outputRoot, value); }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanEditIdentity));
            OnPropertyChanged(nameof(CanChoosePreset));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(CanRun));
            RaiseCommands();
        }
    }
    public bool IsNotBusy => !IsBusy;

    private bool isCancelling;
    public bool IsCancelling
    {
        get => isCancelling;
        private set
        {
            if (!Set(ref isCancelling, value)) return;
            OnPropertyChanged(nameof(CancelActionContent));
            OnPropertyChanged(nameof(CancelActionName));
            cancelCommand.RaiseCanExecuteChanged();
        }
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private string status = "Ready to review a prepared RaceMenu request.";
    public string Status { get => status; private set => Set(ref status, value); }

    private string verdict = "Not built";
    public string Verdict { get => verdict; private set => Set(ref verdict, value); }

    private string resultNpc = EmptyResult;
    public string ResultNpc { get => resultNpc; private set => Set(ref resultNpc, value); }

    private string resultPlugin = EmptyResult;
    public string ResultPlugin { get => resultPlugin; private set => Set(ref resultPlugin, value); }

    private string resultFaceGeom = EmptyResult;
    public string ResultFaceGeom { get => resultFaceGeom; private set => Set(ref resultFaceGeom, value); }

    private string resultFaceTint = EmptyResult;
    public string ResultFaceTint { get => resultFaceTint; private set => Set(ref resultFaceTint, value); }

    private string resultBodyGen = EmptyResult;
    public string ResultBodyGen { get => resultBodyGen; private set => Set(ref resultBodyGen, value); }

    private string resultPex = EmptyResult;
    public string ResultPex { get => resultPex; private set => Set(ref resultPex, value); }

    private string resultPackage = EmptyResult;
    public string ResultPackage { get => resultPackage; private set => Set(ref resultPackage, value); }

    private string resultPreview = EmptyResult;
    public string ResultPreview { get => resultPreview; private set => Set(ref resultPreview, value); }

    public bool CanReviewPreflight => !IsBusy &&
        preflightService is not null &&
        TryBuildExecutionRequest(out _, out _);
    public bool IsReady => HasReviewedPreflight &&
        TryBuildExecutionRequest(out _, out _);
    public bool CanRun => !IsBusy && IsReady;

    public string ReadinessText
    {
        get
        {
            if (!TryBuildExecutionRequest(out _, out string reason))
                return reason;
            return HasReviewedPreflight
                ? PreviewReady
                    ? "Ready to build, verify, and render six off-engine views."
                    : "Ready to build and verify. Preview prerequisites are unavailable."
                : "Review the production preflight before build.";
        }
    }

    public async Task ReviewAsync()
    {
        if (IsBusy) return;
        Diagnostics.Clear();
        ClearReviewedRequestState();
        ClearCancellationAcknowledgement();
        IsCancelling = false;
        IsBusy = true;
        Status = "Hash-binding and reviewing the prepared request...";
        var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            var file = new WorkspacePath(RequestPath.Trim());
            var expected = new Sha256Hash(RequestSha256.Trim());
            var result = await requestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(file, expected), source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Loaded || result.Request is null)
            {
                Verdict = result.Status == RaceMenuNpcExecutionRequestFileLoadStatus.SecurityRefused
                    ? "Request blocked"
                    : "Request invalid";
                Status = "The request was not loaded. Review the diagnostics.";
                return;
            }

            loadedRequest = ApplyWorkspaceTarget(result.Request);
            LoadReview(loadedRequest);
            HasReviewedRequest = true;
            Verdict = "Request reviewed";
            Status = IsExistingNpcTarget
                ? "Prepared request verified. The source NPC identity is retained; review the override plugin and fresh output."
                : "Prepared request verified. Review the new NPC identity and fresh output.";
        }
        catch (OperationCanceledException)
        {
            await AwaitCancellationAcknowledgementAsync(source);
            Status = "Request review cancelled.";
            Verdict = "Cancelled";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or InvalidDataException)
        {
            Diagnostics.Add($"request-review: {exception.Message}");
            Status = "The request was not loaded. Review the diagnostics.";
            Verdict = "Request invalid";
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            ClearCancellationAcknowledgement(source);
            source.Dispose();
            IsBusy = false;
            IsCancelling = false;
            RefreshReadiness();
        }
    }

    public async Task ReviewPreflightAsync()
    {
        if (!CanReviewPreflight || preflightService is null) return;
        Diagnostics.Clear();
        ClearPreflightState();
        if (!TryBuildExecutionRequest(out RaceMenuNpcExecutionRequest? current,
                out string reason) || current is null)
        {
            Diagnostics.Add($"Error: npc-build-preflight-input: {reason}");
            Status = "Correct the appearance and output fields before preflight.";
            Verdict = "Preflight input required";
            return;
        }

        IsBusy = true;
        Status = "Running the read-only production build preflight...";
        Verdict = "Preflight in progress";
        var source = new CancellationTokenSource();
        cancellation = source;
        try
        {
            WorkspacePath? companion = referencePresetHandoff is null
                ? null
                : FreshChild(outputParent, "gui-reference-companion");
            WorkspacePath output = FreshPreflightPath();
            NpcBuildPreflightRequest request = BuildPreflightRequest(
                current, companion, output);
            NpcBuildPreflightResult result =
                await preflightService.CreateAsync(
                    request, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Created || !result.ReadyForBuild ||
                result.Document?.Path is null)
            {
                Status = "Preflight refused the build. Review the failed gates.";
                Verdict = "Preflight refused";
                PreflightSummary = SummarizePreflight(result.Document?.Value);
                return;
            }

            reviewedPreflightDocument = result.Document;
            reviewedPreflightAuthority =
                new NpcBuildPreflightReviewAuthority(
                    result.Document.Path.Value,
                    result.Document.Sha256);
            reviewedCompanionRoot = companion;
            PreflightPath = result.Document.Path.Value.Value;
            PreflightSha256 = result.Document.Sha256.Value;
            PreflightSummary = SummarizePreflight(result.Document.Value);
            HasReviewedPreflight = true;
            Verdict = "Preflight reviewed";
            Status = PreviewReady
                ? "Required gates passed. The six-view preview prerequisites are also ready."
                : "Required gates passed. Build is ready; preview remains separately unavailable.";
        }
        catch (OperationCanceledException)
        {
            await AwaitCancellationAcknowledgementAsync(source);
            Status = "Preflight cancelled without creating build output.";
            Verdict = "Preflight cancelled";
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            InvalidDataException or NotSupportedException)
        {
            Diagnostics.Add($"npc-build-preflight: {exception.Message}");
            Status = "Preflight failed without creating build output.";
            Verdict = "Preflight failed";
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            source.Dispose();
            IsBusy = false;
            RefreshReadiness();
        }
    }

    internal void PresentStartupError(string message)
    {
        if (IsBusy) return;
        ClearReviewedRequestState();
        Diagnostics.Clear();
        Diagnostics.Add($"startup: {message}");
        Verdict = "Startup request invalid";
        Status = "The explicit startup request was not loaded. Correct the arguments or choose a request manually.";
    }

    internal void ApplyReviewedIntake(ReviewedGameIntake intake)
    {
        reviewedIntake = intake;
        OnPropertyChanged(nameof(CanChoosePreset));
        OnPropertyChanged(nameof(PreviewReady));
        OnPropertyChanged(nameof(BuildActionContent));
        OnPropertyChanged(nameof(BuildActionName));
        OnPropertyChanged(nameof(BuildActionHelpText));
        RefreshReadiness();
    }

    internal void ClearReviewedIntake()
    {
        reviewedIntake = null;
        referencePresetHandoff = null;
        ClearPreflightState();
        OnPropertyChanged(nameof(CanChoosePreset));
    }

    internal FormReference? TryResolveTargetRace(ReviewedGameIntake intake)
    {
        ArgumentNullException.ThrowIfNull(intake);
        if (reviewedIntake is null ||
            reviewedIntake.IntakeFingerprint != intake.IntakeFingerprint)
            return null;
        return loadedRequest?.Build.References.Race;
    }

    internal bool TryAcceptReferencePresetHandoff(
        ReferencePresetNpcDesktopHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        Diagnostics.Clear();
        if (IsBusy ||
            loadedRequest is null ||
            !HasReviewedRequest ||
            jslotNpcBuildService is null ||
            loadedRequest.Build.ExistingNpcTarget is not null)
        {
            Diagnostics.Add(
                "Error: reference-preset-handoff-state: Review one compatible new-NPC request before continuing from Create preset.");
            return false;
        }
        VerifiedReferencePreset preset = handoff.Preset;
        if (preset.Authority !=
                ReferencePresetAuthorityKind.VerifiedPreset ||
            preset.Race != loadedRequest.Build.References.Race ||
            preset.Sex != loadedRequest.Build.Traits.Sex ||
            !preset.PresetPath.IsUnder(labRoot) ||
            !File.Exists(preset.PresetPath.Value) ||
            HashFileExact(preset.PresetPath) !=
                preset.PresetSha256)
        {
            Diagnostics.Add(
                "Error: reference-preset-handoff-authority: The verified JSlot changed or does not match the reviewed NPC race and sex.");
            return false;
        }
        PluginName[] pluginOrder =
            handoff.ReviewedIntake.Plugins
                .Where(item =>
                    item.Enabled &&
                    item.Exists &&
                    item.ReadSucceeded &&
                    item.SourceHash is not null)
                .OrderBy(item => item.Order)
                .Select(item => item.Plugin)
                .ToArray();
        if (pluginOrder.Length == 0 ||
            !handoff.ReviewedIntake.DataRoot.IsUnder(labRoot) ||
            !Directory.Exists(
                handoff.ReviewedIntake.DataRoot.Value))
        {
            Diagnostics.Add(
                "Error: reference-preset-handoff-intake: The reviewed Data root or plugin order is no longer usable.");
            return false;
        }
        if (reviewedIntake is not null &&
            reviewedIntake.IntakeFingerprint !=
                handoff.ReviewedIntake.IntakeFingerprint)
        {
            Diagnostics.Add(
                "Error: reference-preset-handoff-intake-drift: The open NPC task belongs to a different reviewed workspace.");
            return false;
        }

        reviewedIntake = handoff.ReviewedIntake;
        loadedRequest = loadedRequest with
        {
            Build = loadedRequest.Build with
            {
                Stats = loadedRequest.Build.Stats with
                {
                    Weight = preset.Weight
                }
            }
        };
        referencePresetHandoff = handoff;
        ClearPreflightState();
        PresetPath = preset.PresetPath.Value;
        PresetHash = preset.PresetSha256.Value;
        CharGenPath = "Generated from this JSlot by NPC Manager";
        CharGenHash = EmptyResult;
        FaceTintPath = "Generated from this JSlot by NPC Manager";
        FaceTintHash = EmptyResult;
        SourceSummary =
            $"Verified reference preset / {preset.Race} / {preset.Sex} / weight {preset.Weight}";
        OutputRoot = CreateFreshOutputRoot().Value;
        ResetResults();
        Verdict = "Reference preset handed off";
        Status =
            "Exact verified JSlot retained. Review the NPC identity and fresh output, then create the package through the corrected Manager-only JSlot route.";
        OnPropertyChanged(nameof(CanChoosePreset));
        RefreshReadiness();
        return true;
    }

    internal void ApplyWorkspaceExistingNpcTarget(
        WorkspacePath sourcePlugin,
        Sha256Hash sourceSha256,
        FormId targetFormId)
    {
        workspaceExistingNpcTarget = new RaceMenuExistingNpcTarget(
            sourcePlugin,
            sourceSha256,
            targetFormId);
        if (loadedRequest is not null && HasReviewedRequest)
        {
            loadedRequest = ApplyWorkspaceTarget(loadedRequest);
            ClearPreflightState();
            LoadReview(loadedRequest);
            ResetResults();
            Verdict = "Workspace target bound";
            Status =
                "The reviewed preset request now targets the exact hash-bound source NPC.";
            RefreshReadiness();
        }
        else
        {
            Status =
                "Exact workbench NPC target retained. Review one preset request to bind it before build.";
        }
    }

    private RaceMenuNpcExecutionRequest ApplyWorkspaceTarget(
        RaceMenuNpcExecutionRequest request) =>
        workspaceExistingNpcTarget is null
            ? request
            : request with
            {
                Build = request.Build with
                {
                    ExistingNpcTarget = workspaceExistingNpcTarget
                }
            };

    internal RaceMenuPresetLoaderViewModel? CreatePresetLoader()
    {
        if (!CanChoosePreset || loadedRequest is null || reviewedIntake is null)
            return null;
        RaceMenuPresetTargetBuildResult target = RaceMenuPresetTargetFactory.Create(
            reviewedIntake,
            loadedRequest.Build.References.Race,
            loadedRequest.Build.Traits.Sex);
        if (!target.Accepted || target.Target is null)
        {
            Diagnostics.Clear();
            foreach (Diagnostic diagnostic in target.Diagnostics)
                Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
            Status = "The reviewed workspace cannot authorize a race-compatible preset catalog.";
            return null;
        }

        string? directory = Path.GetDirectoryName(
            loadedRequest.Build.PresetBundle.PresetPath.Value);
        if (string.IsNullOrWhiteSpace(directory))
        {
            Diagnostics.Clear();
            Diagnostics.Add("Error: preset-directory-missing: The reviewed preset has no parent folder.");
            return null;
        }
        return new RaceMenuPresetLoaderViewModel(
            presetCatalogService,
            new WorkspacePath(directory),
            target.Target,
            presetPreviewService,
            new RaceMenuPresetSelection(
                loadedRequest.Build.PresetBundle.PresetPath,
                loadedRequest.Build.PresetBundle.ExpectedPresetSha256,
                loadedRequest.ApplyBodySlide));
    }

    internal async Task ApplyPresetSelectionAsync(RaceMenuPresetSelection selection)
    {
        if (loadedRequest is null || !HasReviewedRequest) return;
        RaceMenuNpcPresetBundle bundle = loadedRequest.Build.PresetBundle;
        if (string.Equals(bundle.PresetPath.Value, selection.SourcePath.Value,
                StringComparison.OrdinalIgnoreCase) &&
            bundle.ExpectedPresetSha256 == selection.SourceSha256)
        {
            loadedRequest = loadedRequest with
            {
                ApplyBodySlide = selection.ApplyBodySlide
            };
            ClearPreflightState();
            BodySlideChoice = selection.ApplyBodySlide
                ? "BodySlide sliders: apply"
                : "BodySlide sliders: omit by operator choice";
            Status = "Exact prepared preset selection committed to the reviewed build transaction.";
            Verdict = "Preset selection reviewed";
            ResetResults();
            RefreshReadiness();
            return;
        }

        Diagnostics.Clear();
        if (presetSelectionTransactionService is null ||
            selection.Document is null || selection.Companion is null ||
            selection.Target is null)
        {
            Diagnostics.Add(
                "Error: preset-selection-authority-unavailable: The selected row has no complete typed document, CharGen companion, target, or transaction service.");
            Status = "The selected preset was not committed; the current request is unchanged.";
            Verdict = "Selection authority unavailable";
            return;
        }
        if (!TryBuildExecutionRequest(out RaceMenuNpcExecutionRequest? current,
                out string reason) || current is null)
        {
            Diagnostics.Add($"Error: preset-selection-current-request: {reason}");
            Status = "Correct the current request before changing its preset.";
            Verdict = "Current request not ready";
            return;
        }

        IsBusy = true;
        Status = "Generating and reopening the selected preset authority bundle...";
        Verdict = "Preset selection in progress";
        var source = new CancellationTokenSource();
        try
        {
            RaceMenuPresetSelectionTransactionResult result =
                await presetSelectionTransactionService.RebindAsync(
                    new RaceMenuPresetSelectionTransactionRequest(
                        current,
                        selection.Document,
                        selection.Companion,
                        selection.Target,
                        selection.ApplyBodySlide,
                        outputParent),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Committed || result.CandidateRequest is null)
            {
                Status = "The selected preset was refused; the current request and preview state are unchanged.";
                Verdict = "Preset selection refused";
                return;
            }

            loadedRequest = result.CandidateRequest;
            ClearPreflightState();
            LoadReview(result.CandidateRequest);
            HasReviewedRequest = true;
            BodySlideChoice = selection.ApplyBodySlide
                ? "BodySlide sliders: apply"
                : "BodySlide sliders: omit by operator choice";
            Status = "Selected preset committed after full authority readback. Review the unchanged NPC identity and fresh output.";
            Verdict = "Preset selection reviewed";
            ResetResults();
        }
        catch (OperationCanceledException)
        {
            Status = "Preset selection cancelled; the current request is unchanged.";
            Verdict = "Preset selection cancelled";
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            Diagnostics.Add($"preset-selection-transaction: {exception.Message}");
            Status = "Preset selection failed; the current request is unchanged.";
            Verdict = "Preset selection failed";
        }
        finally
        {
            source.Dispose();
            IsBusy = false;
            RefreshReadiness();
        }
    }

    internal void PresentProgressDialogError(Exception exception)
    {
        Diagnostics.Add($"progress-dialog: {exception.GetType().Name}: {exception.Message}");
        PresentFailure("Build failed", "No verified package was retained.");
    }

    public async Task RunAsync()
    {
        if (IsBusy) return;
        Diagnostics.Clear();
        if (!TryBuildExecutionRequest(out var request, out var reason) || request is null)
        {
            Diagnostics.Add($"input: {reason}");
            Verdict = "Input required";
            Status = IsExistingNpcTarget
                ? "Review the override plugin and fresh output path."
                : "Review the identity, plugin, and fresh output path.";
            return;
        }
        if (preflightService is null || reviewedPreflightAuthority is null)
        {
            Diagnostics.Add(
                "input: Review the production preflight before build.");
            Verdict = "Preflight required";
            Status = "Build did not start because no reviewed preflight is bound.";
            return;
        }

        ResetResults();
        ClearCancellationAcknowledgement();
        IsCancelling = false;
        Verdict = "Building";
        Status = IsExistingNpcTarget
            ? "Starting the preset-to-existing-NPC pipeline..."
            : "Starting the preset-to-new-NPC pipeline...";
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        using Activity activity = new Activity(
                "Actorwright.Desktop.RaceMenuNpcBuild")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        try
        {
            var progress = new Progress<BlankNpcBuildProgress>(item =>
            {
                ProgressPercent = item.Percent;
                Status = item.Message;
            });
            RaceMenuNpcExecutionResult result;
            if (referencePresetHandoff is { } referenceHandoff)
            {
                if (jslotNpcBuildService is null ||
                    !File.Exists(
                        referenceHandoff.Preset.PresetPath.Value) ||
                    HashFileExact(
                        referenceHandoff.Preset.PresetPath) !=
                        referenceHandoff.Preset.PresetSha256)
                {
                    Diagnostics.Add(
                        "Error: reference-preset-handoff-stale: The handed-off JSlot no longer matches its verified hash.");
                    RecordFailureEvidence(
                        activity,
                        ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                        ["reference-preset-handoff-stale"]);
                    PresentFailure(
                        "Build refused",
                        "No verified package was retained.");
                    return;
                }
                ImmutableArray<PluginName> pluginOrder =
                    referenceHandoff.ReviewedIntake.Plugins
                        .Where(item =>
                            item.Enabled &&
                            item.Exists &&
                            item.ReadSucceeded &&
                            item.SourceHash is not null)
                        .OrderBy(item => item.Order)
                        .Select(item => item.Plugin)
                        .ToImmutableArray();
                if (reviewedCompanionRoot is not { } companionRoot)
                {
                    Diagnostics.Add(
                        "Error: npc-build-preflight-companion: The reviewed transaction staging root is absent.");
                    RecordFailureEvidence(
                        activity,
                        ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                        ["npc-build-preflight-companion"]);
                    PresentFailure(
                        "Build refused",
                        "No verified package was retained.");
                    return;
                }
                RaceMenuJslotNpcBuildResult jslotResult =
                    await jslotNpcBuildService.ExecuteAsync(
                        new RaceMenuJslotNpcBuildRequest(
                            request,
                            referenceHandoff.Preset.PresetPath,
                            referenceHandoff.Preset.PresetSha256,
                            referenceHandoff.ReviewedIntake.DataRoot,
                            pluginOrder,
                            companionRoot)
                        {
                            SourceRequest = new WorkspacePath(
                                RequestPath.Trim()),
                            SourceRequestSha256 = new Sha256Hash(
                                RequestSha256.Trim()),
                            ReviewedPreflight = reviewedPreflightAuthority
                        },
                        progress,
                        source.Token);
                AddDiagnostics(jslotResult.Diagnostics);
                if (!jslotResult.Completed ||
                    jslotResult.Execution is null)
                {
                    RecordFailureEvidence(
                        activity,
                        ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                        jslotResult.Diagnostics.Select(item => item.Code));
                    PresentFailure(
                        "Build refused",
                        "No verified package was retained.");
                    return;
                }
                result = jslotResult.Execution;
            }
            else
            {
                NpcBuildPreflightResult currentPreflight =
                    await preflightService.VerifyReviewedAsync(
                        BuildPreflightRequest(
                            request, null, null),
                        reviewedPreflightAuthority,
                        source.Token);
                AddDiagnostics(currentPreflight.Diagnostics);
                if (!currentPreflight.Created ||
                    !currentPreflight.ReadyForBuild)
                {
                    RecordFailureEvidence(
                        activity,
                        ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                        currentPreflight.Diagnostics.Select(item => item.Code));
                    PresentFailure(
                        "Build refused",
                        "The reviewed preflight became stale; no build output was created.");
                    return;
                }
                result = await buildService.ExecuteAsync(
                    request,
                    progress,
                    source.Token);
            }
            source.Token.ThrowIfCancellationRequested();
            AddDiagnostics(result.Diagnostics);
            var artifact = result.Build?.Artifact;
            var existingArtifact = result.ExistingNpcBuild?.Artifact;
            if (!result.Completed || artifact is null && existingArtifact is null)
            {
                RecordFailureEvidence(
                    activity,
                    ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                    result.Diagnostics.Select(item => item.Code));
                PresentFailure("Build refused", "No verified package was retained.");
                return;
            }

            Verdict = artifact?.Verdict ?? existingArtifact!.Verdict;
            ResultNpc = artifact is not null
                ? $"{artifact.AllocatedFormId}  {request.Build.Identity.Name.Value}"
                : $"{existingArtifact!.SourceOwnerPlugin.Value}|{existingArtifact.SourceOwnerFormId}";
            ResultPlugin = artifact is not null
                ? WithHash(artifact.Plugin, artifact.PluginSha256)
                : WithHash(existingArtifact!.Plugin, existingArtifact.PluginSha256);
            ResultFaceGeom = artifact is not null
                ? WithHash(artifact.FaceGeom, artifact.FaceGeomSha256)
                : WithHash(existingArtifact!.FaceGeom, existingArtifact.FaceGeomSha256);
            ResultFaceTint = artifact is not null
                ? WithHash(artifact.FaceTint, artifact.FaceTintSha256)
                : WithHash(existingArtifact!.FaceTint, existingArtifact.FaceTintSha256);
            ResultPackage = artifact is not null
                ? WithHash(artifact.Manifest, artifact.ManifestSha256)
                : WithHash(existingArtifact!.Manifest, existingArtifact.ManifestSha256);
            artifactEmitter.Commit(
                this,
                "preset-to-npc-package",
                artifact?.Manifest ?? existingArtifact!.Manifest,
                artifact?.ManifestSha256 ??
                    existingArtifact!.ManifestSha256,
                artifact?.AllocatedFormId ??
                    existingArtifact!.SourceOwnerFormId);
            ResultBodyGen = result.BodyGen is { Files.Length: > 0 } bodyGen
                ? string.Join(Environment.NewLine, bodyGen.Files.Select(item =>
                    WithHash(item.AbsolutePath, item.Sha256)))
                : "No BodyGen files were emitted.";
            var artifactRoot = artifact?.OutputRoot ?? existingArtifact!.OutputRoot;
            var pexPath = new WorkspacePath(Path.Combine(artifactRoot.Value,
                "Data", "Scripts", "NPCM_Manolov_ApplySSE.pex"));
            ResultPex = File.Exists(pexPath.Value)
                ? WithHash(pexPath, await HashFileAsync(pexPath, source.Token))
                : pexPath.Value + "  [not found by desktop readback]";
            if (!File.Exists(pexPath.Value))
                Diagnostics.Add("Warning: runtime PEX was not found at the verified package route.");
            ProgressPercent = 100;
            if (PreviewReady)
            {
                await RenderVerifiedPreviewAsync(
                    request,
                    artifact,
                    existingArtifact,
                    activity,
                    source.Token);
            }
            else
            {
                Status = "Static package created and independently verified. In-game proof is still required.";
            }
        }
        catch (OperationCanceledException)
        {
            await AwaitCancellationAcknowledgementAsync(source);
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Cancelled,
                []);
            PresentFailure("Cancelled", "Creation cancelled. No verified package was retained.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Exception,
                []);
            Diagnostics.Add($"build-io: {exception.Message}");
            PresentFailure("Build failed", "No verified package was retained.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Exception,
                []);
            Diagnostics.Add($"build-unexpected: {exception.GetType().Name}: {exception.Message}");
            PresentFailure("Build failed", "No verified package was retained.");
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) cancellation = null;
            ClearCancellationAcknowledgement(source);
            source.Dispose();
            IsBusy = false;
            IsCancelling = false;
            RefreshReadiness();
        }
    }

    private async Task RenderVerifiedPreviewAsync(
        RaceMenuNpcExecutionRequest request,
        BlankNpcBuildArtifact? artifact,
        ExistingNpcAppearanceBuildArtifact? existingArtifact,
        Activity activity,
        CancellationToken cancellationToken)
    {
        if (npcVisualPreviewComposer is null || reviewedIntake is null)
        {
            Diagnostics.Add(
                "Warning: npc-build-preview-unavailable: The verified package is retained, but the desktop preview service or reviewed intake is unavailable.");
            Status =
                "Static package verified. Preview was unavailable separately; in-game proof is still required.";
            return;
        }
        try
        {
            SkyrimMainWorkspaceIdentity identity = artifact is not null
                ? new SkyrimMainWorkspaceIdentity(
                    request.Build.OutputPlugin,
                    request.Build.OutputPlugin,
                    artifact.AllocatedFormId,
                    "NPC_")
                : new SkyrimMainWorkspaceIdentity(
                    existingArtifact!.SourceOwnerPlugin,
                    request.Build.OutputPlugin,
                    existingArtifact.SourceOwnerFormId,
                    "NPC_");
            WorkspacePath manifest = artifact?.Manifest ??
                existingArtifact!.Manifest;
            Sha256Hash manifestHash = artifact?.ManifestSha256 ??
                existingArtifact!.ManifestSha256;
            var previewRequest = new NpcVisualPreviewComposeRequest(
                reviewedIntake,
                identity,
                new NpcVisualPreviewPackageOverlay(
                    manifest,
                    manifestHash),
                new NpcVisualPreviewOptions(),
                new WorkspacePath(
                    request.Build.OutputRoot.Value + "-preview"));
            NpcVisualPreviewComposeResult preview =
                await npcVisualPreviewComposer.ComposeAsync(
                    previewRequest,
                    cancellationToken);
            AddDiagnostics(preview.Diagnostics);
            if (!preview.Composed || preview.Bundle is null)
            {
                RecordFailureEvidence(
                    activity,
                    ActorwrightObservabilityEventSource.DesktopFailureKindId.HandledResult,
                    preview.Diagnostics.Select(item => item.Code),
                    ActorwrightObservabilityEventSource.DesktopFailureOperationId.BlenderNpcVisualPreview);
                Diagnostics.Add(
                    "Warning: npc-build-preview-refused: The verified package remains valid; only the separate off-engine preview was refused.");
                Status =
                    "Static package verified. The separate preview was refused; in-game proof is still required.";
                return;
            }
            ResultPreview = WithHash(
                preview.Bundle.ContactSheetPath,
                preview.Bundle.ContactSheetSha256);
            Status =
                "Static package and six-view off-engine preview verified. Skyrim runtime remains authoritative.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            RecordFailureEvidence(
                activity,
                ActorwrightObservabilityEventSource.DesktopFailureKindId.Exception,
                [],
                ActorwrightObservabilityEventSource.DesktopFailureOperationId.BlenderNpcVisualPreview);
            Diagnostics.Add(
                $"Warning: npc-build-preview-failed: {exception.GetType().Name}: {exception.Message}");
            Status =
                "Static package verified. The separate preview failed; in-game proof is still required.";
        }
    }

    private void RecordFailureEvidence(
        Activity activity,
        ActorwrightObservabilityEventSource.DesktopFailureKindId failureKind,
        IEnumerable<string> diagnosticCodes,
        ActorwrightObservabilityEventSource.DesktopFailureOperationId operation =
            ActorwrightObservabilityEventSource.DesktopFailureOperationId.RaceMenuNpcBuild)
    {
        DesktopEvidenceStoreWriteResult? write =
            DesktopFailureEvidenceListener.RecordCurrentOperationFailure(
                activity,
                operation,
                failureKind,
                diagnosticCodes);
        if (write is { Written: false })
            Diagnostics.Add(
                "Desktop failure evidence could not be safely saved.");
    }

    private async Task RequestCancellationAsync()
    {
        var source = cancellation;
        if (!IsBusy || source is null || IsCancelling) return;

        IsCancelling = true;
        Status = "Cancelling. Waiting for the active stage to stop safely...";
        BeginCancellationAcknowledgement(source);

        // The upstream dialog forces an immediate repaint before signalling its
        // cooperative cancellation flag. Yield below WPF's render priority so
        // the disabled Cancelling state is visible even when the pipeline exits
        // at its next cancellation checkpoint.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
        else
            await Task.Yield();

        if (ReferenceEquals(cancellation, source) && IsBusy && IsCancelling)
            source.Cancel();
    }

    private void BeginCancellationAcknowledgement(CancellationTokenSource source)
    {
        cancellationAcknowledgementSource = source;
        cancellationAcknowledgement = Task.Delay(MinimumCancellationAcknowledgement);
    }

    private async Task AwaitCancellationAcknowledgementAsync(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellationAcknowledgementSource, source))
            await cancellationAcknowledgement;
    }

    private void ClearCancellationAcknowledgement(CancellationTokenSource? source = null)
    {
        if (source is not null && !ReferenceEquals(cancellationAcknowledgementSource, source))
            return;

        cancellationAcknowledgementSource = null;
        cancellationAcknowledgement = Task.CompletedTask;
    }

    private async Task BrowseRequestAsync()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Prepared NPC request (*.json)|*.json|All files (*.*)|*.*",
            Title = "Choose a prepared preset-to-NPC request"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var path = new WorkspacePath(dialog.FileName);
            if (!path.IsUnder(labRoot))
                throw new UnauthorizedAccessException(
                    "Choose a prepared request copied under the configured Actorwright workspace.");
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length is <= 0 or > 1 * 1024 * 1024)
                throw new InvalidDataException(
                    "Prepared requests must contain 1 byte to 1 MiB.");
            RequestPath = path.Value;
            RequestSha256 = (await HashFileAsync(path, CancellationToken.None)).Value;
            await ReviewAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or InvalidDataException)
        {
            Diagnostics.Clear();
            Diagnostics.Add($"request-selection: {exception.Message}");
            Status = "The selected request was not accepted.";
        }
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = IsExistingNpcTarget
                ? "Choose a K-local parent for the existing NPC override"
                : "Choose a K-local parent for the new NPC package"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var parent = new WorkspacePath(dialog.FolderName);
            if (!parent.IsUnder(labRoot) || parent == labRoot)
                throw new UnauthorizedAccessException(
                    "Output must remain below the configured Actorwright workspace.");
            OutputRoot = FreshChild(parent).Value;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException)
        {
            Diagnostics.Clear();
            Diagnostics.Add($"output-selection: {exception.Message}");
        }
    }

    private void LoadReview(RaceMenuNpcExecutionRequest request)
    {
        var build = request.Build;
        PresetPath = build.PresetBundle.PresetPath.Value;
        PresetHash = build.PresetBundle.ExpectedPresetSha256.Value;
        CharGenPath = build.PresetBundle.CharGenFaceGeom.Value;
        CharGenHash = build.PresetBundle.ExpectedCharGenFaceGeomSha256.Value;
        FaceTintPath = build.PresetBundle.CharGenFaceTint.Value;
        FaceTintHash = build.PresetBundle.ExpectedCharGenFaceTintSha256.Value;
        SourceSummary = build.ExistingNpcTarget is { } target
            ? $"Existing {Path.GetFileName(target.SourcePlugin.Value)}|{target.TargetFormId} / {build.Traits.Sex} / identity retained"
            : $"New {build.Traits.Role} / {build.Traits.Sex} / bundle {Path.GetFileName(build.PresetBundle.ManifestPath.Value)}";
        BodySlideChoice = request.ApplyBodySlide
            ? "BodySlide sliders: apply"
            : "BodySlide sliders: omit by operator choice";
        DisplayName = build.Identity.Name.Value;
        EditorId = build.Identity.EditorId.Value;
        PluginName = build.OutputPlugin.Value;
        OutputRoot = CreateFreshOutputRoot().Value;
        NotifyTargetMode();
        ResetResults();
    }

    private bool TryBuildExecutionRequest(
        out RaceMenuNpcExecutionRequest? request,
        out string reason)
    {
        request = null;
        if (loadedRequest is null || !HasReviewedRequest)
        {
            reason = "Review a hash-bound prepared request first.";
            return false;
        }
        try
        {
            var output = new WorkspacePath(OutputRoot.Trim());
            if (!output.IsUnder(labRoot) || output == labRoot)
                throw new ArgumentException("Fresh output must remain below the K-only workspace root.");
            if (Directory.Exists(output.Value) || File.Exists(output.Value))
                throw new ArgumentException("Choose a fresh output folder; this path already exists.");
            var plugin = new PluginName(PluginName.Trim());
            if (!plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The preset pipeline currently writes an ordinary .esp plugin.");
            var identity = IsExistingNpcTarget
                ? loadedRequest.Build.Identity
                : new NpcCreationIdentity(
                    new EditorId(EditorId.Trim()), new NpcName(DisplayName.Trim()));
            request = loadedRequest with
            {
                Build = loadedRequest.Build with
                {
                    OutputRoot = output,
                    OutputPlugin = plugin,
                    Identity = identity
                }
            };
            reason = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private NpcBuildPreflightRequest BuildPreflightRequest(
        RaceMenuNpcExecutionRequest current,
        WorkspacePath? companionRoot,
        WorkspacePath? output)
    {
        var source = new WorkspacePath(RequestPath.Trim());
        var sourceHash = new Sha256Hash(RequestSha256.Trim());
        if (referencePresetHandoff is { } handoff)
        {
            ImmutableArray<PluginName> plugins = handoff.ReviewedIntake.Plugins
                .Where(item => item.Enabled && item.Exists &&
                    item.ReadSucceeded && item.SourceHash is not null)
                .OrderBy(item => item.Order)
                .Select(item => item.Plugin)
                .ToImmutableArray();
            return new NpcBuildPreflightRequest(
                current,
                source,
                sourceHash,
                handoff.Preset.PresetPath,
                handoff.Preset.PresetSha256,
                handoff.ReviewedIntake.DataRoot,
                plugins,
                companionRoot,
                output);
        }
        return new NpcBuildPreflightRequest(
            current,
            source,
            sourceHash,
            current.Build.PresetBundle.PresetPath,
            current.Build.PresetBundle.ExpectedPresetSha256,
            null,
            [],
            null,
            output);
    }

    private WorkspacePath FreshPreflightPath()
    {
        string stamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(
            outputParent.Value,
            $"npc-build-preflight-{stamp}-{Guid.NewGuid():N}.json"));
    }

    private static string SummarizePreflight(
        NpcBuildPreflightArtifact? artifact)
    {
        if (artifact is null) return "No review artifact was produced.";
        int requiredPassed = artifact.RequiredGates.Count(item => item.Passed);
        int previewPassed = artifact.OptionalPreview.Count(item => item.Passed);
        return $"Required {requiredPassed}/{artifact.RequiredGates.Length}; " +
               $"preview {previewPassed}/{artifact.OptionalPreview.Length}; " +
               $"{artifact.HeadParts.Length} head parts; " +
               $"{artifact.FinalDependencyClosure.Length} final dependencies.";
    }

    private WorkspacePath CreateFreshOutputRoot() => FreshChild(outputParent);

    private static WorkspacePath FreshChild(
        WorkspacePath parent,
        string prefix = "gui-preset-npc")
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(parent.Value,
            $"{prefix}-{stamp}-{Guid.NewGuid():N}"[..Math.Min(
                47,
                prefix.Length + 1 + stamp.Length + 1 + 32)]));
    }

    private void SetOverride(ref string field, string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName)) return;
        ClearPreflightState();
        ResetResults();
        if (HasReviewedRequest && !IsBusy)
            Status = IsExistingNpcTarget
                ? "Output changed. Build again using a fresh path."
                : "Identity or output changed. Build again using a fresh path.";
        RefreshReadiness();
    }

    private void InvalidateReview(string message)
    {
        ClearReviewedRequestState();
        Verdict = "Not reviewed";
        Status = message;
        RaiseCommands();
    }

    private void ClearReviewedRequestState()
    {
        ClearPreflightState();
        loadedRequest = null;
        referencePresetHandoff = null;
        NotifyTargetMode();
        HasReviewedRequest = false;
        PresetPath = PresetHash = CharGenPath = CharGenHash =
            FaceTintPath = FaceTintHash = EmptyResult;
        SourceSummary = "Choose and review a prepared request.";
        BodySlideChoice = "BodySlide sliders: apply";
        Set(ref displayName, string.Empty, nameof(DisplayName));
        Set(ref editorId, string.Empty, nameof(EditorId));
        Set(ref pluginName, string.Empty, nameof(PluginName));
        Set(ref outputRoot, string.Empty, nameof(OutputRoot));
        ResetResults();
    }

    private void ResetResults()
    {
        ProgressPercent = 0;
        ResultNpc = ResultPlugin = ResultFaceGeom = ResultFaceTint =
            ResultBodyGen = ResultPex = ResultPackage = ResultPreview =
                EmptyResult;
        if (Verdict is not ("Building" or "Request reviewed")) Verdict = "Not built";
    }

    private void ClearPreflightState()
    {
        reviewedPreflightDocument = null;
        reviewedPreflightAuthority = null;
        reviewedCompanionRoot = null;
        PreflightPath = EmptyResult;
        PreflightSha256 = EmptyResult;
        PreflightSummary = "Review the production gates before build.";
        HasReviewedPreflight = false;
    }

    private static Sha256Hash HashFileExact(
        WorkspacePath path)
    {
        using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.SequentialScan);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private void NotifyTargetMode()
    {
        OnPropertyChanged(nameof(IsExistingNpcTarget));
        OnPropertyChanged(nameof(IsNewNpcTarget));
        OnPropertyChanged(nameof(CanEditIdentity));
        OnPropertyChanged(nameof(WorkspaceAutomationName));
        OnPropertyChanged(nameof(HeroEyebrow));
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroDescription));
        OnPropertyChanged(nameof(TargetCardEyebrow));
        OnPropertyChanged(nameof(TargetCardTitle));
        OnPropertyChanged(nameof(IdentityHelpText));
        OnPropertyChanged(nameof(PluginLabel));
        OnPropertyChanged(nameof(BuildActionContent));
        OnPropertyChanged(nameof(BuildActionName));
        OnPropertyChanged(nameof(BuildActionHelpText));
        OnPropertyChanged(nameof(PreviewReady));
    }

    private void PresentFailure(string failureVerdict, string absentStatus)
    {
        var outputExists = !string.IsNullOrWhiteSpace(OutputRoot) &&
                           (Directory.Exists(OutputRoot) || File.Exists(OutputRoot));
        var rollbackIncomplete = Diagnostics.Any(item =>
            item.Contains("rollback-incomplete", StringComparison.Ordinal) ||
            item.Contains("output-create-unclaimed", StringComparison.Ordinal));
        Verdict = outputExists && rollbackIncomplete
            ? "Partial output - do not install"
            : failureVerdict;
        Status = outputExists && rollbackIncomplete
            ? $"An unverified partial output remains at '{OutputRoot}'."
            : absentStatus;
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
    }

    private static string WithHash(WorkspacePath path, Sha256Hash hash) =>
        $"{path.Value}{Environment.NewLine}SHA-256 {hash.Value}";

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private void RefreshReadiness()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(CanReviewPreflight));
        OnPropertyChanged(nameof(BuildActionContent));
        OnPropertyChanged(nameof(BuildActionName));
        OnPropertyChanged(nameof(BuildActionHelpText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        browseRequestCommand?.RaiseCanExecuteChanged();
        reviewCommand?.RaiseCanExecuteChanged();
        reviewPreflightCommand?.RaiseCanExecuteChanged();
        runCommand?.RaiseCanExecuteChanged();
        cancelCommand?.RaiseCanExecuteChanged();
        browseOutputCommand?.RaiseCanExecuteChanged();
        newOutputCommand?.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value,
        [CallerMemberName] string? propertyName = null)
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
        if (source is not null)
        {
            source.Cancel();
            source.Dispose();
        }
        presetCatalogLifetime.Dispose();
    }

    private sealed class DelegateCommand(
        Action<object?> execute,
        Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
