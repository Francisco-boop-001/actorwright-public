using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Identity- and hash-bound coordinator for the integrated Skyrim NPC
/// workbench. Production child transactions remain the only game-facing
/// writers.
/// </summary>
public sealed class SkyrimMainWorkspaceViewModel :
    NotifyViewModel,
    IDisposable
{
    private const string NoRaceMenuCarrier =
        "No complete RaceMenu carrier is bound to this actor. NPC_ bytes " +
        "cannot recover lost sculpt, overlay, or custom-morph authority.";
    private readonly ISkyrimMainWorkspaceCatalogService catalogService;
    private readonly ISkyrimMainWorkspaceSettingsService settingsService;
    private readonly ISkyrimMainWorkspaceSessionService sessionService;
    private readonly ISkyrimMainWorkspacePreviewService previewService;
    private readonly INpcVisualPreviewComposer? npcVisualPreviewComposer;
    private readonly INpcVisualComparisonService?
        npcVisualComparisonService;
    private readonly IPackageVerifyService? packageVerifyService;
    private readonly Func<Exception, WorkspacePath?>? operationFailureSink;
    private readonly IPresetService presetService;
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath? npcVisualOutputParent;
    private readonly WorkspacePath?
        npcVisualComparisonOutputParent;
    private readonly IDisposable? npcVisualLifetime;
    private FaceGeomHairRegionsWizardDesktopComposition?
        hairRegionsDesktopComposition;
    private Func<FaceGeomHairRegionsWizardDesktopComposition?>?
        hairRegionsDesktopCompositionFactory;
    private readonly Dictionary<string, NpcVisualPreviewBundle>
        npcVisualCache = new(StringComparer.Ordinal);
    private readonly Dictionary<
        SkyrimMainWorkspaceIdentity,
        SkyrimMainWorkspaceRowViewModel> rowsByIdentity = [];
    private readonly Dictionary<
        SkyrimMainWorkspaceIdentity,
        SkyrimMainWorkspaceArtifactHandoff> artifactsByIdentity = [];
    private readonly MainWorkspaceCommand loadCommand;
    private readonly MainWorkspaceCommand cancelCommand;
    private readonly MainWorkspaceCommand markChangedCommand;
    private readonly MainWorkspaceCommand resetCommand;
    private readonly MainWorkspaceCommand markDeleteCommand;
    private readonly MainWorkspaceCommand restoreCommand;
    private readonly MainWorkspaceCommand randomNpcCommand;
    private readonly MainWorkspaceCommand rerollCommand;
    private readonly MainWorkspaceCommand renderPreviewCommand;
    private readonly MainWorkspaceCommand refreshPreviewCommand;
    private readonly MainWorkspaceCommand comparePreviewCommand;
    private readonly MainWorkspaceCommand copyPlaceAtMeCommand;
    private readonly MainWorkspaceCommand editNpcCommand;
    private readonly MainWorkspaceCommand editHeadPartsCommand;
    private readonly MainWorkspaceCommand editFaceCommand;
    private readonly MainWorkspaceCommand editBodyCommand;
    private readonly MainWorkspaceCommand editOutfitCommand;
    private readonly MainWorkspaceCommand loadRaceMenuPresetCommand;
    private readonly MainWorkspaceCommand saveRaceMenuPresetCommand;
    private readonly MainWorkspaceCommand copyAppearanceCommand;
    private readonly MainWorkspaceCommand pasteAppearanceCommand;
    private readonly MainWorkspaceCommand charGenOptionsCommand;
    private readonly MainWorkspaceCommand buildCharGenCommand;
    private readonly MainWorkspaceCommand savePackageCommand;
    private readonly MainWorkspaceCommand exportSceneNifCommand;
    private readonly MainWorkspaceCommand lightingCommand;
    private readonly MainWorkspaceCommand animationCommand;
    private readonly MainWorkspaceCommand saveSessionCommand;
    private CancellationTokenSource? cancellation;
    private ReviewedGameIntake? acceptedIntake;
    private SkyrimMainWorkspaceSnapshot? snapshot;
    private SkyrimMainWorkspaceRowViewModel? selectedRow;
    private SkyrimMainWorkspaceIdentity? copiedAppearance;
    private SkyrimMainWorkspaceSettings settings = new(
        "1",
        SkyrimMainWorkspaceFilter.Default,
        SkyrimMainWorkspacePreviewOptions.Default);
    private SkyrimMainWorkspacePreviewConfiguration? previewConfiguration;
    private WorkspacePath? sessionDestination;
    private WorkspacePath? nifDestination;
    private WorkspacePath? presetDestination;
    private NpcVisualPackagePreviewTarget?
        npcVisualPackagePreviewTarget;

    public SkyrimMainWorkspaceViewModel(
        ISkyrimMainWorkspaceCatalogService catalogService,
        ISkyrimMainWorkspaceSettingsService settingsService,
        ISkyrimMainWorkspaceSessionService sessionService,
        ISkyrimMainWorkspacePreviewService previewService,
        IPresetService presetService,
        WorkspacePath labRoot,
        INpcVisualPreviewComposer? npcVisualPreviewComposer = null,
        WorkspacePath? npcVisualOutputParent = null,
        IDisposable? npcVisualLifetime = null,
        INpcVisualComparisonService?
            npcVisualComparisonService = null,
        WorkspacePath? npcVisualComparisonOutputParent = null,
        IPackageVerifyService? packageVerifyService = null,
        Func<Exception, WorkspacePath?>? operationFailureSink = null)
    {
        this.catalogService = catalogService;
        this.settingsService = settingsService;
        this.sessionService = sessionService;
        this.previewService = previewService;
        this.presetService = presetService;
        this.labRoot = labRoot;
        this.npcVisualPreviewComposer = npcVisualPreviewComposer;
        this.npcVisualOutputParent = npcVisualOutputParent;
        this.npcVisualLifetime = npcVisualLifetime;
        this.npcVisualComparisonService =
            npcVisualComparisonService;
        this.npcVisualComparisonOutputParent =
            npcVisualComparisonOutputParent;
        this.packageVerifyService = packageVerifyService;
        this.operationFailureSink = operationFailureSink;

        loadCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(() => LoadAsync(acceptedIntake)),
            _ => !HasActiveOperation && acceptedIntake is not null);
        cancelCommand = new MainWorkspaceCommand(
            _ => cancellation?.Cancel(),
            _ => HasActiveOperation && cancellation is not null);
        markChangedCommand = DraftCommand(
            SkyrimMainWorkspaceRules.MarkChanged);
        resetCommand = DraftCommand(SkyrimMainWorkspaceRules.Reset);
        markDeleteCommand = DraftCommand(
            SkyrimMainWorkspaceRules.MarkDelete);
        restoreCommand = DraftCommand(
            SkyrimMainWorkspaceRules.Restore);
        randomNpcCommand = new MainWorkspaceCommand(
            _ => SelectRandomNpc(),
            _ => CanSelectRandomNpc);
        rerollCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(RerollAsync),
            _ => CanReroll);
        renderPreviewCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(RenderPreviewAsync),
            _ => CanRenderPreview);
        refreshPreviewCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(
                () => RenderPreviewAsync(forceRefresh: true)),
            _ => CanRenderPreview);
        comparePreviewCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(ComparePreviewAsync),
            _ => CanComparePreview);
        copyPlaceAtMeCommand = new MainWorkspaceCommand(
            _ => CopyPlaceAtMe(),
            _ => SelectedPreviewRecord is not null &&
                 !HasActiveOperation);
        editNpcCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.EditNpc);
        editHeadPartsCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.EditHeadParts);
        editFaceCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.EditFace);
        editBodyCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.EditBody);
        editOutfitCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.EditOutfit);
        loadRaceMenuPresetCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.LoadRaceMenuPreset);
        saveRaceMenuPresetCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(SaveRaceMenuPresetAsync),
            _ => CanSaveRaceMenuPreset);
        copyAppearanceCommand = new MainWorkspaceCommand(
            _ => CopyAppearance(),
            _ => CanRoute(SkyrimMainWorkspaceRoute.CopyAppearance));
        pasteAppearanceCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.PasteAppearance,
            () => copiedAppearance is not null);
        charGenOptionsCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.CharGenOptions);
        buildCharGenCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.BuildCharGen);
        savePackageCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.SavePackage,
            CanSaveSelected);
        exportSceneNifCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(ExportSceneNifAsync),
            _ => CanExportSceneNif);
        lightingCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.Lighting);
        animationCommand = RouteCommand(
            SkyrimMainWorkspaceRoute.Animation);
        saveSessionCommand = new MainWorkspaceCommand(
            _ => RunCommandAsync(SaveSessionAsync),
            _ => !HasActiveOperation &&
                 snapshot is not null &&
                 sessionDestination is not null);
    }

    public ObservableCollection<SkyrimMainWorkspaceRowViewModel> Rows
    {
        get;
    } = [];

    public ObservableCollection<SkyrimMainWorkspaceRowViewModel> VisibleRows
    {
        get;
    } = [];

    public ObservableCollection<SkyrimMainWorkspaceRowViewModel> SelectedRows
    {
        get;
    } = [];

    public ObservableCollection<SkyrimMainWorkspaceArtifactHandoff> Artifacts
    {
        get;
    } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ObservableCollection<NpcVisualPreviewViewItem>
        PreviewViews { get; } = [];

    public ObservableCollection<string> ResolvedPreviewAssets { get; } = [];

    public ObservableCollection<string> PreviewWarnings { get; } = [];

    public ObservableCollection<NpcVisualComparisonViewItem>
        PreviewComparisons { get; } = [];

    public IReadOnlyList<SkyrimMainWorkspaceGender> GenderChoices { get; } =
        Enum.GetValues<SkyrimMainWorkspaceGender>();

    public IReadOnlyList<SkyrimMainWorkspacePreviewMode> PreviewModeChoices
    {
        get;
    } = Enum.GetValues<SkyrimMainWorkspacePreviewMode>();

    public event EventHandler<SkyrimWorkspaceNavigationRequest>?
        NavigationRequested;

    public ICommand LoadCommand => loadCommand;
    public ICommand CancelCommand => cancelCommand;
    public ICommand MarkChangedCommand => markChangedCommand;
    public ICommand ResetCommand => resetCommand;
    public ICommand MarkDeleteCommand => markDeleteCommand;
    public ICommand RestoreCommand => restoreCommand;
    public ICommand RandomNpcCommand => randomNpcCommand;
    public ICommand RerollCommand => rerollCommand;
    public ICommand RenderPreviewCommand => renderPreviewCommand;
    public ICommand RefreshPreviewCommand => refreshPreviewCommand;
    public ICommand ComparePreviewCommand => comparePreviewCommand;
    public ICommand CopyPlaceAtMeCommand => copyPlaceAtMeCommand;
    public ICommand EditNpcCommand => editNpcCommand;
    public ICommand EditHeadPartsCommand => editHeadPartsCommand;
    public ICommand EditFaceCommand => editFaceCommand;
    public ICommand EditBodyCommand => editBodyCommand;
    public ICommand EditOutfitCommand => editOutfitCommand;
    public ICommand LoadRaceMenuPresetCommand =>
        loadRaceMenuPresetCommand;
    public ICommand SaveRaceMenuPresetCommand =>
        saveRaceMenuPresetCommand;
    public ICommand CopyAppearanceCommand => copyAppearanceCommand;
    public ICommand PasteAppearanceCommand => pasteAppearanceCommand;
    public ICommand CharGenOptionsCommand => charGenOptionsCommand;
    public ICommand BuildCharGenCommand => buildCharGenCommand;
    public ICommand SavePackageCommand => savePackageCommand;
    public ICommand ExportSceneNifCommand => exportSceneNifCommand;
    public ICommand LightingCommand => lightingCommand;
    public ICommand AnimationCommand => animationCommand;
    public ICommand SaveSessionCommand => saveSessionCommand;

    public SkyrimMainWorkspaceSnapshot? Snapshot => snapshot;

    public SkyrimMainWorkspaceSettings CurrentSettings => settings;

    public SkyrimMainWorkspaceIdentity? CopiedAppearanceIdentity =>
        copiedAppearance;

    public bool TryGetRecord(
        SkyrimMainWorkspaceIdentity identity,
        out SkyrimMainWorkspaceRecord? record)
    {
        if (rowsByIdentity.TryGetValue(identity, out var row))
        {
            record = row.Source;
            return true;
        }
        record = null;
        return false;
    }

    public bool TryGetArtifact(
        SkyrimMainWorkspaceIdentity identity,
        out SkyrimMainWorkspaceArtifactHandoff? artifact) =>
        artifactsByIdentity.TryGetValue(identity, out artifact);

    internal void AttachHairRegionsDesktopComposition(
        FaceGeomHairRegionsWizardDesktopComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (hairRegionsDesktopComposition is not null)
        {
            throw new InvalidOperationException(
                "The main workspace already owns a HairTint wizard composition.");
        }

        hairRegionsDesktopComposition =
            composition;
    }

    internal void AttachHairRegionsDesktopCompositionFactory(
        Func<FaceGeomHairRegionsWizardDesktopComposition?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (hairRegionsDesktopComposition is not null ||
            hairRegionsDesktopCompositionFactory is not null)
            throw new InvalidOperationException(
                "The main workspace already owns a HairTint wizard composition or factory.");
        hairRegionsDesktopCompositionFactory = factory;
    }
    internal bool TryGetHairRegionsWizardContext(
        out FaceGeomHairRegionsWizardProductionContext? context)
    {
        if (hairRegionsDesktopComposition is null &&
            hairRegionsDesktopCompositionFactory is not null)
        {
            hairRegionsDesktopComposition =
                hairRegionsDesktopCompositionFactory();
            if (hairRegionsDesktopComposition is not null)
                hairRegionsDesktopCompositionFactory = null;
        }
        if (hairRegionsDesktopComposition is null)
        {
            context = null;
            return false;
        }

        SkyrimMainWorkspaceRecord? selected =
            SelectedPreviewRecord;
        context =
            new FaceGeomHairRegionsWizardProductionContext(
                hairRegionsDesktopComposition,
                labRoot,
                acceptedIntake,
                selected?.Identity,
                selected is null
                    ? FaceGeomHairRegionsWizardLaunchContext
                        .Standalone.DisplayName
                    : selected.Name ??
                      selected.EditorId ??
                      $"{selected.Identity.OwnerPlugin.Value} " +
                      selected.Identity.FormId);
        return true;
    }

    public SkyrimMainWorkspaceRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (value is not null &&
                !rowsByIdentity.TryGetValue(
                    value.Identity,
                    out SkyrimMainWorkspaceRowViewModel? canonical))
                throw new ArgumentException(
                    "The selected row is not part of this snapshot.",
                    nameof(value));
            if (ReferenceEquals(selectedRow, value))
                return;
            selectedRow = value;
            if (value is not null &&
                !SelectedRows.Contains(value))
                ReplaceSelection([value.Identity]);
            Raise(nameof(SelectedRow));
            RaiseSelectedDetails();
            PresentSelectionDiagnostic(value);
            RaiseCommandState();
        }
    }

    public string Search
    {
        get => settings.Filter.Search;
        set => UpdateFilter(
            settings.Filter with { Search = value ?? string.Empty });
    }

    public bool ShowNpcs
    {
        get => settings.Filter.ShowNpcs;
        set => UpdateFilter(
            settings.Filter with { ShowNpcs = value });
    }

    public bool ShowLeveledNpcs
    {
        get => settings.Filter.ShowLeveledNpcs;
        set => UpdateFilter(
            settings.Filter with { ShowLeveledNpcs = value });
    }

    public SkyrimMainWorkspaceGender Gender
    {
        get => settings.Filter.Gender;
        set => UpdateFilter(settings.Filter with { Gender = value });
    }

    public bool ChangedOnly
    {
        get => settings.Filter.ChangedOnly;
        set => UpdateFilter(
            settings.Filter with { ChangedOnly = value });
    }

    public bool IncludeDeleted
    {
        get => settings.Filter.IncludeDeleted;
        set => UpdateFilter(
            settings.Filter with { IncludeDeleted = value });
    }

    public bool ShowUnique
    {
        get => settings.Filter.Categories.Contains(NpcCategory.Unique);
        set => UpdateCategory(NpcCategory.Unique, value);
    }

    public bool ShowGeneric
    {
        get => settings.Filter.Categories.Contains(NpcCategory.Generic);
        set => UpdateCategory(NpcCategory.Generic, value);
    }

    public bool ShowTemplate
    {
        get => settings.Filter.Categories.Contains(NpcCategory.Template);
        set => UpdateCategory(NpcCategory.Template, value);
    }

    public bool ShowUnused
    {
        get => settings.Filter.Categories.Contains(NpcCategory.Unused);
        set => UpdateCategory(NpcCategory.Unused, value);
    }

    public SkyrimMainWorkspacePreviewOptions PreviewOptions
    {
        get => settings.Preview;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (settings.Preview == value)
                return;
            settings = settings with { Preview = value };
            PreviewIsStale = PreviewImageSha256 is not null;
            RaisePreviewProperties();
            RaiseCommandState();
        }
    }

    public SkyrimMainWorkspacePreviewMode PreviewMode
    {
        get => PreviewOptions.Mode;
        set => PreviewOptions = PreviewOptions with { Mode = value };
    }

    public SkyrimMainWorkspaceGender PreviewGender
    {
        get => PreviewOptions.Gender;
        set => PreviewOptions = PreviewOptions with { Gender = value };
    }

    public bool RenderBody
    {
        get => PreviewOptions.RenderBody;
        set => PreviewOptions = PreviewOptions with { RenderBody = value };
    }

    public bool RenderUnderarmor
    {
        get => PreviewOptions.RenderUnderarmor;
        set => PreviewOptions = PreviewOptions with
        {
            RenderUnderarmor = value
        };
    }

    public bool RenderArmor
    {
        get => PreviewOptions.RenderArmor;
        set => PreviewOptions = PreviewOptions with { RenderArmor = value };
    }

    public bool RenderHeadwear
    {
        get => PreviewOptions.RenderHeadwear;
        set => PreviewOptions = PreviewOptions with
        {
            RenderHeadwear = value
        };
    }

    public bool RenderGore
    {
        get => PreviewOptions.RenderGore;
        set => PreviewOptions = PreviewOptions with { RenderGore = value };
    }

    public bool ApplyBoneMorphs
    {
        get => PreviewOptions.ApplyBoneMorphs;
        set => PreviewOptions = PreviewOptions with
        {
            ApplyBoneMorphs = value
        };
    }

    public bool ApplyVertexMorphs
    {
        get => PreviewOptions.ApplyVertexMorphs;
        set => PreviewOptions = PreviewOptions with
        {
            ApplyVertexMorphs = value
        };
    }

    public bool ApplyBodyWeight
    {
        get => PreviewOptions.ApplyBodyWeight;
        set => PreviewOptions = PreviewOptions with
        {
            ApplyBodyWeight = value
        };
    }

    public bool ApplySculpt
    {
        get => PreviewOptions.ApplySculpt;
        set => PreviewOptions = PreviewOptions with { ApplySculpt = value };
    }

    private string previewVariantId = string.Empty;
    public string PreviewVariantId
    {
        get => previewVariantId;
        set
        {
            if (!Set(ref previewVariantId, value ?? string.Empty))
                return;
            if (previewConfiguration is not null)
                previewConfiguration = previewConfiguration with
                {
                    VariantId = string.IsNullOrWhiteSpace(
                        previewVariantId)
                        ? null
                        : previewVariantId
                };
            PreviewIsStale = PreviewImageSha256 is not null;
        }
    }

    private int rerollSeed;
    public int RerollSeed
    {
        get => rerollSeed;
        set => Set(ref rerollSeed, value);
    }

    private bool hasActiveOperation;
    public bool HasActiveOperation
    {
        get => hasActiveOperation;
        private set
        {
            if (!Set(ref hasActiveOperation, value))
                return;
            Raise(nameof(IsNotBusy));
            Raise(nameof(IsBusy));
            RaiseCommandState();
        }
    }

    public bool IsNotBusy => !HasActiveOperation;

    public bool IsBusy => HasActiveOperation;

    private string status =
        "Accept a reviewed copied workspace before loading records.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string placeAtMeText = "No live NPC is resolved.";
    public string PlaceAtMeText
    {
        get => placeAtMeText;
        private set => Set(ref placeAtMeText, value);
    }

    private string? previewImagePath;
    public string? PreviewImagePath
    {
        get => previewImagePath;
        private set => Set(ref previewImagePath, value);
    }

    private Sha256Hash? previewImageHash;
    public string? PreviewImageSha256 => previewImageHash?.Value;

    public string HighFidelityPreviewLabel { get; } =
        "High-fidelity off-engine preview — Skyrim runtime remains authoritative";

    public string? NpcVisualPackageManifestPath =>
        npcVisualPackagePreviewTarget?
            .Overlay.ManifestPath.Value;

    public string? NpcVisualPackageSha256 =>
        npcVisualPackagePreviewTarget?
            .Overlay.ExpectedManifestSha256.Value;

    public string NpcVisualPreviewTargetText =>
        npcVisualPackagePreviewTarget is { } package
            ? $"Verified Manager package target: " +
              $"{package.Identity.OwnerPlugin.Value}|" +
              $"{package.Identity.FormId.Value:X8}"
            : SelectedPreviewRecord is { } selected
                ? $"Reviewed browser target: " +
                  $"{selected.Identity.WinningProvider.Value}|" +
                  $"{selected.Identity.FormId.Value:X8}"
                : "No high-fidelity preview target.";

    private string? referenceComparisonImagePath;
    public string? ReferenceComparisonImagePath
    {
        get => referenceComparisonImagePath;
        set
        {
            if (Set(
                    ref referenceComparisonImagePath,
                    string.IsNullOrWhiteSpace(value)
                        ? null
                        : value.Trim()))
                RaiseCommandState();
        }
    }

    private string? runtimeComparisonImagePath;
    public string? RuntimeComparisonImagePath
    {
        get => runtimeComparisonImagePath;
        set
        {
            if (Set(
                    ref runtimeComparisonImagePath,
                    string.IsNullOrWhiteSpace(value)
                        ? null
                        : value.Trim()))
                RaiseCommandState();
        }
    }

    private string previewRouteText =
        "No composed route";
    public string PreviewRouteText
    {
        get => previewRouteText;
        private set => Set(ref previewRouteText, value);
    }

    private WorkspacePath? previewScenePath;
    public WorkspacePath? PreviewScenePath
    {
        get => previewScenePath;
        private set
        {
            if (!Set(ref previewScenePath, value))
                return;
            RaiseCommandState();
        }
    }

    private Sha256Hash? previewSceneSha256;
    public Sha256Hash? PreviewSceneSha256
    {
        get => previewSceneSha256;
        private set
        {
            if (!Set(ref previewSceneSha256, value))
                return;
            RaiseCommandState();
        }
    }

    private bool previewIsStale;
    public bool PreviewIsStale
    {
        get => previewIsStale;
        private set
        {
            if (!Set(ref previewIsStale, value))
                return;
            Raise(nameof(PreviewAuthorityText));
        }
    }

    public string RuntimeAuthorityText { get; } =
        "STATIC_PASS_RUNTIME_REQUIRED — off-engine preview only; runtime authority: false.";

    public string PreviewAuthorityText => PreviewImageSha256 is null
        ? RuntimeAuthorityText
        : (PreviewIsStale ? "STALE · " : "Hash-accepted · ") +
          RuntimeAuthorityText;

    private Sha256Hash? sessionSha256;
    public Sha256Hash? SessionSha256
    {
        get => sessionSha256;
        private set => Set(ref sessionSha256, value);
    }

    public string SelectedIdentityText =>
        SelectedRow?.IdentityText ?? "No selected record";

    public string SelectedProviderText =>
        SelectedRow?.ProviderText ?? "No provider";

    public string SelectedDetailText =>
        SelectedRow?.DetailText ?? "Choose a row to inspect exact facts.";

    public string SelectedStateText =>
        SelectedRow?.StateText ?? "No selection";

    public string SaveRaceMenuAvailabilityText =>
        SelectedRow?.Source.Kind !=
            SkyrimMainWorkspaceRecordKind.Npc
            ? "Save RaceMenu preset requires one live NPC."
            : TryGetRaceMenuCarrier(
                SelectedRow.Identity,
                out _)
                ? "A verified complete RaceMenu carrier is bound."
                : NoRaceMenuCarrier;

    public int VisibleCount => VisibleRows.Count;

    public int SelectedCount => SelectedRows.Count;

    public void ApplyReviewedIntake(ReviewedGameIntake intake)
    {
        ArgumentNullException.ThrowIfNull(intake);
        acceptedIntake = intake;
        RaiseCommandState();
        Status =
            "Reviewed intake is bound. Load its immutable NPC/LVLN snapshot.";
    }

    public async Task ApplyReviewedIntakeAsync(
        ReviewedGameIntake intake,
        CancellationToken cancellationToken = default)
    {
        ApplyReviewedIntake(intake);
        await LoadAsync(intake, cancellationToken);
    }

    public void ClearReviewedIntake()
    {
        acceptedIntake = null;
        cancellation?.Cancel();
        ClearSnapshot();
        Status =
            "Workspace authority is stale. Review the copied workspace again.";
        RaiseCommandState();
    }

    public async Task LoadAsync(
        ReviewedGameIntake? intake,
        CancellationToken cancellationToken = default)
    {
        if (intake is null)
        {
            PresentError(
                "main-workspace-intake-missing",
                "A reviewed intake is required before loading records.");
            return;
        }
        if (HasActiveOperation)
        {
            PresentError(
                "main-workspace-operation-active",
                "Another workbench operation is active.");
            return;
        }
        CancellationTokenSource source =
            BeginOperation(cancellationToken);
        try
        {
            SkyrimMainWorkspaceSettingsLoadResult loadedSettings =
                await settingsService.LoadAsync(source.Token);
            SkyrimMainWorkspaceCatalogResult loadedCatalog =
                await catalogService.LoadAsync(
                    new SkyrimMainWorkspaceCatalogRequest(intake),
                    source.Token);
            if (!loadedCatalog.Accepted ||
                loadedCatalog.Snapshot is null ||
                loadedCatalog.Snapshot.IntakeFingerprint !=
                    intake.IntakeFingerprint)
            {
                AddDiagnostics(loadedCatalog.Diagnostics);
                Status =
                    "Catalog load was refused; the prior snapshot remains active.";
                return;
            }
            CommitSnapshot(
                intake,
                loadedCatalog.Snapshot,
                loadedSettings.Settings);
            AddDiagnostics(loadedSettings.Diagnostics);
            AddDiagnostics(loadedCatalog.Diagnostics);
            Status =
                $"Loaded {Rows.Count} immutable NPC/LVLN winner record(s).";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-cancelled: Load cancelled; the prior snapshot remains active.");
            Status = "Load cancelled; prior accepted state retained.";
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            PresentOperationFailure(
                "main-workspace-load-failed",
                "Catalog load failed",
                exception);
        }
        finally
        {
            EndOperation(source);
        }
    }

    public void ReplaceSelection(
        ImmutableArray<SkyrimMainWorkspaceIdentity> identities)
    {
        ImmutableHashSet<SkyrimMainWorkspaceIdentity> visible =
            VisibleRows.Select(row => row.Identity).ToImmutableHashSet();
        ImmutableArray<SkyrimMainWorkspaceIdentity> exact =
            identities
                .Where(visible.Contains)
                .Distinct()
                .ToImmutableArray();
        foreach (SkyrimMainWorkspaceRowViewModel row in Rows)
            row.IsSelected = exact.Contains(row.Identity);
        SelectedRows.Clear();
        foreach (SkyrimMainWorkspaceRowViewModel row in VisibleRows)
        {
            if (exact.Contains(row.Identity))
                SelectedRows.Add(row);
        }
        selectedRow = SelectedRows.FirstOrDefault();
        Raise(nameof(SelectedRow));
        Raise(nameof(SelectedCount));
        RaiseSelectedDetails();
        PresentSelectionDiagnostic(selectedRow);
        RaiseCommandState();
    }

    public void SelectVisibleRange(
        SkyrimMainWorkspaceRowViewModel anchor,
        SkyrimMainWorkspaceRowViewModel extent)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(extent);
        ImmutableArray<SkyrimMainWorkspaceRecord> visible =
            VisibleRows.Select(row => row.Source).ToImmutableArray();
        ReplaceSelection(
            SkyrimMainWorkspaceRules.SelectRange(
                visible,
                anchor.Identity,
                extent.Identity));
    }

    public void ConfigurePreview(
        WorkspacePath manifestPath,
        Sha256Hash expectedManifestSha256,
        WorkspacePath assetRoot,
        WorkspacePath scenePath,
        WorkspacePath imagePath,
        string? variantId = null,
        PreviewAnimationSelection? animation = null,
        PreviewLightingPreset? lighting = null)
    {
        previewConfiguration = new SkyrimMainWorkspacePreviewConfiguration(
            manifestPath,
            expectedManifestSha256,
            assetRoot,
            scenePath,
            imagePath,
            variantId,
            animation,
            lighting);
        previewVariantId = variantId ?? string.Empty;
        Raise(nameof(PreviewVariantId));
        PreviewIsStale = PreviewImageSha256 is not null;
        RaiseCommandState();
    }

    public void ConfigureSessionDestination(WorkspacePath destination)
    {
        sessionDestination = destination;
        RaiseCommandState();
    }

    public void ConfigureNifDestination(WorkspacePath destination)
    {
        nifDestination = destination;
        RaiseCommandState();
    }

    public void ConfigurePresetDestination(WorkspacePath destination)
    {
        presetDestination = destination;
        RaiseCommandState();
    }

    public async Task OpenNpcVisualPackageAsync(
        WorkspacePath manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (packageVerifyService is null)
        {
            PresentError(
                "main-workspace-preview-package-unavailable",
                "The packaged Manager-package verifier is unavailable.");
            return;
        }
        if (HasActiveOperation)
        {
            PresentError(
                "main-workspace-operation-active",
                "Another workbench operation is active.");
            return;
        }

        CancellationTokenSource source =
            BeginOperation(cancellationToken);
        try
        {
            PackageVerifyResult verification =
                await packageVerifyService.VerifyAsync(
                    new PackageVerifyRequest(manifestPath),
                    source.Token);
            AddDiagnostics(verification.Diagnostics);
            if (!TryCreateNpcVisualPackagePreviewTarget(
                    manifestPath,
                    verification,
                    out NpcVisualPackagePreviewTarget? target,
                    out Diagnostic? diagnostic))
            {
                if (diagnostic is not null)
                    AddDiagnostics([diagnostic]);
                Status =
                    "Manager package preview was refused; the prior preview target remains unchanged.";
                return;
            }

            NpcVisualPackagePreviewTarget acceptedTarget =
                target!;
            npcVisualPackagePreviewTarget = acceptedTarget;
            PreviewIsStale = PreviewImageSha256 is not null;
            RaiseNpcVisualPackageProperties();
            Status =
                $"Verified Manager package opened for preview: " +
                $"{acceptedTarget.Identity.OwnerPlugin.Value}|" +
                $"{acceptedTarget.Identity.FormId.Value:X8}. " +
                "The reviewed Data tree remains the unchanged lower provider.";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-preview-package-cancelled: Package verification cancelled; the prior preview target remains unchanged.");
            Status =
                "Manager package verification cancelled.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    public void ClearNpcVisualPackagePreview()
    {
        if (HasActiveOperation)
        {
            PresentError(
                "main-workspace-operation-active",
                "Cancel or complete the active operation before clearing the package preview target.");
            return;
        }
        if (npcVisualPackagePreviewTarget is null)
            return;
        npcVisualPackagePreviewTarget = null;
        PreviewIsStale = PreviewImageSha256 is not null;
        RaiseNpcVisualPackageProperties();
        Status =
            "Manager package preview target cleared; Render follows the reviewed browser selection.";
    }

    public Task RenderPreviewAsync() =>
        RenderPreviewAsync(forceRefresh: false);

    private async Task RenderPreviewAsync(bool forceRefresh)
    {
        if (previewConfiguration is not null)
        {
            await RenderLegacyPreviewAsync();
            return;
        }
        await RenderHighFidelityPreviewAsync(forceRefresh);
    }

    private async Task RenderLegacyPreviewAsync()
    {
        if (!CanRenderPreview)
        {
            PresentError(
                "main-workspace-preview-unavailable",
                "One live selected NPC and complete hash-bound preview inputs are required.");
            return;
        }
        CancellationTokenSource source = BeginOperation();
        SkyrimMainWorkspaceRecord selected =
            SelectedPreviewRecord!;
        SkyrimMainWorkspacePreviewConfiguration configuration =
            previewConfiguration!;
        PreviewIsStale = PreviewImageSha256 is not null;
        try
        {
            SkyrimMainWorkspacePreviewResult result =
                await previewService.RenderAsync(
                    configuration.ToRequest(
                        selected,
                        PreviewOptions),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!IsAcceptedPreview(result, selected))
            {
                Status =
                    "Preview was refused; the last accepted image remains identified as stale.";
                return;
            }
            PreviewImagePath = result.ImagePath!.Value.Value;
            SetPreviewImageSha256(result.ImageSha256);
            PreviewScenePath = result.ScenePath;
            PreviewSceneSha256 = result.SceneSha256;
            PreviewIsStale = false;
            Status =
                "Preview PNG and semantic scene accepted by exact hash. Runtime authority remains false.";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-preview-cancelled: Render cancelled; the prior accepted preview and session remain unchanged.");
            PreviewIsStale = PreviewImageSha256 is not null;
            Status =
                "Preview cancelled; prior accepted state retained as stale.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    private async Task RenderHighFidelityPreviewAsync(
        bool forceRefresh)
    {
        SkyrimMainWorkspaceRecord? selected =
            SelectedPreviewRecord;
        SkyrimMainWorkspaceIdentity? previewIdentity =
            npcVisualPackagePreviewTarget?.Identity ??
            selected?.Identity;
        if (previewIdentity is null ||
            acceptedIntake is null ||
            npcVisualPreviewComposer is null ||
            npcVisualOutputParent is null)
        {
            PresentError(
                "main-workspace-preview-unavailable",
                "A reviewed intake, one live selected NPC, and the packaged high-fidelity composer are required.");
            return;
        }

        WorkspacePath outputParent =
            npcVisualOutputParent.Value;
        Directory.CreateDirectory(
            outputParent.Value);
        CancellationTokenSource source = BeginOperation();
        PreviewIsStale = PreviewImageSha256 is not null;
        try
        {
            NpcVisualPreviewPackageOverlay? overlay;
            if (npcVisualPackagePreviewTarget is { } external)
            {
                PackageVerifyResult verification =
                    await packageVerifyService!.VerifyAsync(
                        new PackageVerifyRequest(
                            external.Overlay.ManifestPath),
                        source.Token);
                AddDiagnostics(verification.Diagnostics);
                if (!TryCreateNpcVisualPackagePreviewTarget(
                        external.Overlay.ManifestPath,
                        verification,
                        out NpcVisualPackagePreviewTarget?
                            reopened,
                        out Diagnostic? diagnostic) ||
                    reopened != external)
                {
                    if (diagnostic is not null)
                        AddDiagnostics([diagnostic]);
                    Status =
                        "The opened Manager package changed or no longer verifies; the prior accepted preview remains stale.";
                    return;
                }
                overlay = external.Overlay;
            }
            else
            {
                overlay =
                    await FindNpcVisualPackageOverlayAsync(
                        previewIdentity,
                        source.Token);
            }
            string fingerprint =
                BuildNpcVisualFingerprint(
                    acceptedIntake,
                    previewIdentity,
                    overlay,
                    PreviewOptions);
            if (!forceRefresh &&
                npcVisualCache.TryGetValue(
                    fingerprint,
                    out NpcVisualPreviewBundle? cached) &&
                await ValidateCachedNpcVisualBundleAsync(
                    cached,
                    acceptedIntake,
                    overlay,
                    source.Token))
            {
                ApplyNpcVisualBundle(cached);
                Status =
                    "Reused a hash-identical high-fidelity preview bundle. Skyrim runtime remains authoritative.";
                return;
            }

            string destination = Path.Combine(
                outputParent.Value,
                $"{fingerprint[..16]}-{Guid.NewGuid():N}");
            var request = new NpcVisualPreviewComposeRequest(
                acceptedIntake,
                previewIdentity,
                overlay,
                new NpcVisualPreviewOptions(
                    RenderBody: PreviewOptions.RenderBody,
                    RenderOutfit:
                        PreviewOptions.RenderArmor ||
                        PreviewOptions.RenderUnderarmor,
                    AlternateLighting: true),
                new WorkspacePath(destination));
            NpcVisualPreviewComposeResult result =
                await npcVisualPreviewComposer.ComposeAsync(
                    request, source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Composed ||
                result.Bundle is null)
            {
                Status =
                    "High-fidelity preview was refused; the prior accepted preview remains stale.";
                return;
            }
            npcVisualCache[fingerprint] = result.Bundle;
            ApplyNpcVisualBundle(result.Bundle);
            Status =
                "High-fidelity face/body bundle accepted by exact hashes. Record the human verdict separately; Skyrim runtime remains authoritative.";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-preview-cancelled: Render cancelled; the prior accepted preview remains unchanged.");
            PreviewIsStale = PreviewImageSha256 is not null;
            Status =
                "Preview cancelled; prior accepted state retained as stale.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    public async Task ComparePreviewAsync()
    {
        NpcVisualPreviewViewItem? face =
            PreviewViews.SingleOrDefault(item =>
                string.Equals(
                    item.Id,
                    "face-front",
                    StringComparison.Ordinal));
        if (face is null ||
            npcVisualComparisonService is null ||
            npcVisualComparisonOutputParent is null)
        {
            PresentError(
                "main-workspace-preview-comparison-unavailable",
                "A current face-front render and the packaged comparison service are required.");
            return;
        }
        var authorities =
            new List<(NpcVisualComparisonKind Kind,
                string Label, string Path)>();
        if (!string.IsNullOrWhiteSpace(
                ReferenceComparisonImagePath))
            authorities.Add((
                NpcVisualComparisonKind.Reference,
                "Reference",
                ReferenceComparisonImagePath));
        if (!string.IsNullOrWhiteSpace(
                RuntimeComparisonImagePath))
            authorities.Add((
                NpcVisualComparisonKind.SkyrimRuntime,
                "Skyrim runtime",
                RuntimeComparisonImagePath));
        if (authorities.Count == 0)
        {
            PresentError(
                "main-workspace-preview-comparison-input",
                "Enter at least one K-local reference or Skyrim runtime screenshot path.");
            return;
        }
        if (authorities.Any(item =>
                !File.Exists(item.Path) ||
                Directory.Exists(item.Path)))
        {
            PresentError(
                "main-workspace-preview-comparison-input",
                "Every configured comparison path must be an existing file.");
            return;
        }

        Directory.CreateDirectory(
            npcVisualComparisonOutputParent.Value.Value);
        CancellationTokenSource source = BeginOperation();
        var accepted =
            new List<NpcVisualComparisonViewItem>();
        try
        {
            var previewPath =
                new WorkspacePath(face.ImagePath);
            var expectedPreviewHash =
                new Sha256Hash(face.Sha256);
            if (await HashFileAsync(
                    previewPath,
                    source.Token) != expectedPreviewHash)
            {
                PresentError(
                    "main-workspace-preview-comparison-stale",
                    "The displayed face render changed before comparison.");
                return;
            }
            foreach ((NpcVisualComparisonKind kind,
                         string label,
                         string authorityPath) in authorities)
            {
                source.Token.ThrowIfCancellationRequested();
                var authority =
                    new WorkspacePath(authorityPath);
                Sha256Hash authorityHash =
                    await HashFileAsync(
                        authority,
                        source.Token);
                string destination = Path.Combine(
                    npcVisualComparisonOutputParent.Value.Value,
                    $"{kind.ToString().ToLowerInvariant()}-" +
                    $"{expectedPreviewHash.Value[..12]}-" +
                    $"{authorityHash.Value[..12]}-" +
                    $"{Guid.NewGuid():N}");
                Directory.CreateDirectory(destination);
                NpcVisualComparisonResult result =
                    await npcVisualComparisonService.CompareAsync(
                        new NpcVisualComparisonRequest(
                            previewPath,
                            expectedPreviewHash,
                            authority,
                            authorityHash,
                            label,
                            kind,
                            new WorkspacePath(destination)),
                        source.Token);
                AddDiagnostics(result.Diagnostics);
                if (!result.Produced ||
                    result.SideBySidePath is null ||
                    result.OverlayPath is null ||
                    result.HeatmapPath is null ||
                    result.EvidencePath is null)
                {
                    Status =
                        $"{label} comparison was refused; prior accepted comparison state remains unchanged.";
                    return;
                }
                accepted.Add(new(
                    label,
                    result.SideBySidePath.Value.Value,
                    result.OverlayPath.Value.Value,
                    result.HeatmapPath.Value.Value,
                    result.EvidencePath.Value.Value,
                    result.MeanAbsoluteRgbDifference));
            }
            NpcVisualPreviewViewItem? currentFace =
                PreviewViews.SingleOrDefault(item =>
                    string.Equals(
                        item.Id,
                        "face-front",
                        StringComparison.Ordinal));
            if (currentFace is null ||
                currentFace.ImagePath != face.ImagePath ||
                currentFace.Sha256 != face.Sha256)
            {
                Diagnostics.Add(
                    "main-workspace-preview-comparison-stale: A newer preview superseded the comparison result.");
                return;
            }
            PreviewComparisons.Clear();
            foreach (NpcVisualComparisonViewItem item in accepted)
                PreviewComparisons.Add(item);
            Status =
                "Aligned comparison evidence produced. Numeric difference is regression evidence only; record the human verdict separately.";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-preview-comparison-cancelled: Comparison cancelled; prior accepted comparison state retained.");
            Status =
                "Comparison cancelled; prior accepted state retained.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    public async Task<bool> AcceptArtifactHandoffAsync(
        SkyrimMainWorkspaceArtifactHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (snapshot is null ||
            !rowsByIdentity.TryGetValue(
                handoff.Identity,
                out SkyrimMainWorkspaceRowViewModel? row))
            return RefuseHandoff(
                "main-workspace-handoff-identity",
                "The child artifact identity is absent from the accepted snapshot.");
        if (handoff.RuntimeAuthority)
            return RefuseHandoff(
                "main-workspace-handoff-runtime",
                "A static child artifact cannot claim runtime authority.");
        if (!handoff.Path.IsUnder(labRoot) ||
            !File.Exists(handoff.Path.Value))
            return RefuseHandoff(
                "main-workspace-handoff-path",
                "The child artifact is missing or outside the K-local workspace.");
        Sha256Hash observed = await HashFileAsync(
            handoff.Path,
            cancellationToken);
        if (observed != handoff.Sha256)
            return RefuseHandoff(
                "main-workspace-handoff-stale",
                "The child artifact changed after independent verification.");
        if ((handoff.ProposalPath is null) !=
            (handoff.ProposalSha256 is null))
            return RefuseHandoff(
                "main-workspace-handoff-proposal-pair",
                "Proposal path and hash must be supplied together.");
        if (handoff.ProposalPath is { } proposalPath &&
            handoff.ProposalSha256 is { } proposalHash)
        {
            if (!proposalPath.IsUnder(labRoot) ||
                !File.Exists(proposalPath.Value) ||
                await HashFileAsync(
                    proposalPath,
                    cancellationToken) != proposalHash)
                return RefuseHandoff(
                    "main-workspace-handoff-proposal-stale",
                    "The child proposal changed after verification.");
        }
        artifactsByIdentity[handoff.Identity] = handoff;
        RefreshArtifacts();
        row.ApplyDraft(
            SkyrimMainWorkspaceRules.MarkChanged(row.Draft));
        RefreshVisibleRows();
        RaiseSelectedDetails();
        Status =
            $"Accepted verified {handoff.Kind} for {row.IdentityText}.";
        RaiseCommandState();
        return true;
    }

    public async Task SaveSessionAsync()
    {
        if (snapshot is null || sessionDestination is null)
        {
            PresentError(
                "main-workspace-session-unavailable",
                "Load a snapshot and choose one fresh session destination.");
            return;
        }
        CancellationTokenSource source = BeginOperation();
        Sha256Hash? previous = SessionSha256;
        try
        {
            SkyrimMainWorkspaceSession session = BuildSession();
            SkyrimMainWorkspaceSessionResult result =
                await sessionService.SaveAsync(
                    new SkyrimMainWorkspaceSessionSaveRequest(
                        session,
                        sessionDestination.Value),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted ||
                !SessionsEquivalent(result.Session, session) ||
                result.Sha256 is null)
            {
                Status =
                    "Session save was refused; prior session authority retained.";
                return;
            }
            SessionSha256 = result.Sha256;
            Status = "Session saved, reopened, and hash accepted.";
        }
        catch (OperationCanceledException)
        {
            SessionSha256 = previous;
            Diagnostics.Add(
                "main-workspace-session-cancelled: Save cancelled; prior session authority retained.");
            Status = "Session save cancelled.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    public async Task RerollAsync()
    {
        if (!CanReroll)
        {
            PresentError(
                "main-workspace-reroll-unavailable",
                SelectedRow?.Source.IsEmptyLeveledList == true
                    ? "The selected LVLN is empty; no NPC member can be rerolled."
                    : "Reroll requires a non-empty LVLN or a configured live NPC preview.");
            return;
        }
        if (SelectedRow?.Source.Kind ==
            SkyrimMainWorkspaceRecordKind.LeveledNpc)
        {
            SkyrimMainWorkspaceIdentity[] entries =
                SelectedRow.Source.LeveledNpcEntries
                    .Where(rowsByIdentity.ContainsKey)
                    .ToArray();
            if (entries.Length == 0)
            {
                PresentError(
                    "main-workspace-reroll-unresolved",
                    "No LVLN member resolves to this exact snapshot.");
                return;
            }
            RerollSeed++;
            resolvedLeveledNpc = rowsByIdentity[
                entries[Math.Abs(RerollSeed) % entries.Length]].Source;
            RaiseSelectedDetails();
        }
        if (previewConfiguration is null ||
            SelectedPreviewRecord is null)
            return;
        CancellationTokenSource source = BeginOperation();
        SkyrimMainWorkspaceRecord selected = SelectedPreviewRecord;
        PreviewIsStale = PreviewImageSha256 is not null;
        try
        {
            SkyrimMainWorkspacePreviewResult result =
                await previewService.RerollAsync(
                    new SkyrimMainWorkspaceRerollRequest(
                        previewConfiguration.ToRequest(
                            selected,
                            PreviewOptions),
                        RerollSeed),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!IsAcceptedPreview(result, selected))
            {
                Status =
                    "Reroll was refused; the prior preview remains stale.";
                return;
            }
            PreviewImagePath = result.ImagePath!.Value.Value;
            SetPreviewImageSha256(result.ImageSha256);
            PreviewScenePath = result.ScenePath;
            PreviewSceneSha256 = result.SceneSha256;
            PreviewIsStale = false;
            Status =
                $"Reroll seed {RerollSeed} accepted by exact preview hashes.";
        }
        catch (OperationCanceledException)
        {
            PreviewIsStale = PreviewImageSha256 is not null;
            Diagnostics.Add(
                "main-workspace-reroll-cancelled: Reroll cancelled; prior preview retained.");
            Status = "Reroll cancelled.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    private SkyrimMainWorkspaceRecord? resolvedLeveledNpc;

    private SkyrimMainWorkspaceRecord? SelectedPreviewRecord =>
        SelectedRow?.Source.Kind ==
            SkyrimMainWorkspaceRecordKind.Npc
            ? SelectedRow.Source.IsSourceDeleted
                ? null
                : SelectedRow.Source
            : resolvedLeveledNpc;

    private bool CanRenderPreview =>
        !HasActiveOperation &&
        (previewConfiguration is not null
            ? SelectedPreviewRecord is not null
            : (npcVisualPackagePreviewTarget is not null ||
               SelectedPreviewRecord is not null) &&
              acceptedIntake is not null &&
              npcVisualPreviewComposer is not null &&
              npcVisualOutputParent is not null);

    private bool CanComparePreview =>
        !HasActiveOperation &&
        npcVisualComparisonService is not null &&
        npcVisualComparisonOutputParent is not null &&
        PreviewViews.Any(item =>
            string.Equals(
                item.Id,
                "face-front",
                StringComparison.Ordinal)) &&
        (!string.IsNullOrWhiteSpace(
             ReferenceComparisonImagePath) ||
         !string.IsNullOrWhiteSpace(
             RuntimeComparisonImagePath));

    private bool CanReroll =>
        !HasActiveOperation &&
        SelectedRow is not null &&
        !SelectedRow.Source.IsSourceDeleted &&
        (SelectedRow.Source.Kind ==
             SkyrimMainWorkspaceRecordKind.Npc
            ? previewConfiguration is not null
            : !SelectedRow.Source.IsEmptyLeveledList &&
              !SelectedRow.Source.LeveledNpcEntries.IsDefaultOrEmpty);

    private bool CanSelectRandomNpc =>
        !HasActiveOperation &&
        VisibleRows.Any(row =>
            row.Source.Kind ==
                SkyrimMainWorkspaceRecordKind.Npc &&
            !row.Source.IsSourceDeleted);

    private bool CanSaveRaceMenuPreset =>
        !HasActiveOperation &&
        presetDestination is not null &&
        SelectedRow?.Source.Kind ==
            SkyrimMainWorkspaceRecordKind.Npc &&
        !SelectedRow.Source.IsSourceDeleted &&
        TryGetRaceMenuCarrier(SelectedRow.Identity, out _);

    private bool CanExportSceneNif =>
        !HasActiveOperation &&
        SelectedPreviewRecord is not null &&
        previewConfiguration is not null &&
        PreviewScenePath is not null &&
        PreviewSceneSha256 is not null &&
        nifDestination is not null;

    private MainWorkspaceCommand DraftCommand(
        Func<
            SkyrimMainWorkspaceDraft,
            SkyrimMainWorkspaceDraft> project) =>
        new(
            _ => ApplyDrafts(project),
            _ => !HasActiveOperation &&
                 SelectedRows.Count > 0);

    private MainWorkspaceCommand RouteCommand(
        SkyrimMainWorkspaceRoute route,
        Func<bool>? additional = null) =>
        new(
            _ => Navigate(route),
            _ => CanRoute(route) &&
                 (additional?.Invoke() ?? true));

    private bool CanRoute(SkyrimMainWorkspaceRoute route) =>
        !HasActiveOperation &&
        SelectedRow is not null &&
        SkyrimMainWorkspaceRules
            .AvailableRoutes(SelectedRow.Source)
            .Contains(route);

    private bool CanSaveSelected()
    {
        if (SelectedRows.Count == 0)
            return false;
        foreach (SkyrimMainWorkspaceRowViewModel row in SelectedRows)
        {
            if (!row.IsChanged && !row.IsDeletePending)
                continue;
            if (row.IsDeletePending)
                continue;
            if (!artifactsByIdentity.ContainsKey(row.Identity))
                return false;
        }
        return SelectedRows.Any(row =>
            row.IsChanged || row.IsDeletePending);
    }

    private void ApplyDrafts(
        Func<
            SkyrimMainWorkspaceDraft,
            SkyrimMainWorkspaceDraft> project)
    {
        foreach (SkyrimMainWorkspaceRowViewModel row in
                 SelectedRows.ToArray())
        {
            if (row.Source.IsSourceDeleted)
            {
                Diagnostics.Add(
                    "main-workspace-source-deleted: Source-deleted rows remain read-only evidence.");
                continue;
            }
            row.ApplyDraft(project(row.Draft));
        }
        RefreshVisibleRows();
        RaiseSelectedDetails();
        RaiseCommandState();
    }

    private void Navigate(SkyrimMainWorkspaceRoute route)
    {
        if (!CanRoute(route) || SelectedRow is null)
        {
            PresentError(
                "main-workspace-route-unavailable",
                $"{route} is unavailable for the exact selection.");
            return;
        }
        WorkspacePath? source = FindSourcePlugin(
            SelectedRow.Identity.WinningProvider);
        if (source is null)
        {
            PresentError(
                "main-workspace-route-source",
                "The selected winning provider is absent from the reviewed intake.");
            return;
        }
        artifactsByIdentity.TryGetValue(
            SelectedRow.Identity,
            out SkyrimMainWorkspaceArtifactHandoff? artifact);
        NavigationRequested?.Invoke(
            this,
            new SkyrimWorkspaceNavigationRequest(
                route,
                SelectedRow.Identity,
                source.Value,
                artifact));
        Status = $"Opened {route} for {SelectedRow.IdentityText}.";
    }

    private void CopyAppearance()
    {
        if (SelectedRow is null ||
            !CanRoute(SkyrimMainWorkspaceRoute.CopyAppearance))
            return;
        copiedAppearance = SelectedRow.Identity;
        Status =
            $"Copied appearance source identity {SelectedRow.IdentityText}; no bytes were written.";
        RaiseCommandState();
    }

    private void CopyPlaceAtMe()
    {
        if (SelectedPreviewRecord is null)
        {
            PresentError(
                "main-workspace-placeatme-unavailable",
                "No live NPC identity is resolved.");
            return;
        }
        PlaceAtMeText = SkyrimMainWorkspaceRules.PlaceAtMe(
            SelectedPreviewRecord.Identity);
        Status =
            "PlaceAtMe text uses unresolved XX prefix; runtime plugin index authority remains with the tester.";
    }

    private void SelectRandomNpc()
    {
        SkyrimMainWorkspaceRowViewModel[] candidates =
            VisibleRows.Where(row =>
                row.Source.Kind ==
                    SkyrimMainWorkspaceRecordKind.Npc &&
                !row.Source.IsSourceDeleted).ToArray();
        if (candidates.Length == 0)
        {
            PresentError(
                "main-workspace-random-empty",
                "No live visible NPC satisfies the current filter.");
            return;
        }
        RerollSeed++;
        SelectedRow = candidates[
            Math.Abs(RerollSeed) % candidates.Length];
    }

    private async Task ExportSceneNifAsync()
    {
        if (!CanExportSceneNif)
        {
            PresentError(
                "main-workspace-nif-unavailable",
                "NIF export requires the last accepted scene hash and one fresh destination.");
            return;
        }
        CancellationTokenSource source = BeginOperation();
        try
        {
            SkyrimMainWorkspaceNifExportResult result =
                await previewService.ExportNifAsync(
                    new SkyrimMainWorkspaceNifExportRequest(
                        SelectedPreviewRecord!,
                        PreviewScenePath!.Value,
                        PreviewSceneSha256!.Value,
                        previewConfiguration!.AssetRoot,
                        nifDestination!.Value),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            Status = result.Written &&
                     result.Sha256 is not null &&
                     !result.RuntimeAuthority
                ? $"Scene NIF accepted: {result.Sha256.Value.Value}."
                : "Scene NIF export was refused.";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-nif-cancelled: Export cancelled; no accepted destination was retained.");
            Status = "Scene NIF export cancelled.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    private async Task SaveRaceMenuPresetAsync()
    {
        if (!CanSaveRaceMenuPreset ||
            SelectedRow is null ||
            presetDestination is null ||
            !TryGetRaceMenuCarrier(
                SelectedRow.Identity,
                out WorkspacePath sourcePath))
        {
            PresentError(
                "main-workspace-jslot-authority",
                NoRaceMenuCarrier);
            return;
        }
        CancellationTokenSource source = BeginOperation();
        try
        {
            var exportRequest = new PresetExportRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                sourcePath,
                presetDestination.Value);
            PresetExportResult exported =
                await presetService.ExportAsync(
                    exportRequest,
                    source.Token);
            AddDiagnostics(exported.Diagnostics);
            if (!exported.Written ||
                exported.OutputHash is null)
            {
                Status = "RaceMenu preset export was refused.";
                return;
            }
            PresetParseResult reopened =
                await presetService.InspectAsync(
                    new PresetParseRequest(
                        PresetFormat.RaceMenuJslot,
                        GameEdition.SkyrimSpecialEdition,
                        presetDestination.Value),
                    source.Token);
            PresetDiffResult difference =
                await presetService.DiffAsync(
                    new PresetDiffRequest(
                        new PresetParseRequest(
                            PresetFormat.RaceMenuJslot,
                            GameEdition.SkyrimSpecialEdition,
                            sourcePath),
                        new PresetParseRequest(
                            PresetFormat.RaceMenuJslot,
                            GameEdition.SkyrimSpecialEdition,
                            presetDestination.Value)),
                    source.Token);
            if (reopened.Document is null ||
                !reopened.Document.IsValid ||
                difference.Diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error) ||
                !difference.Differences.IsDefaultOrEmpty)
            {
                Status =
                    "Exported RaceMenu preset failed full typed reopen comparison.";
                return;
            }
            Status =
                $"RaceMenu preset exported and reopened: {exported.OutputHash.Value.Value}.";
        }
        catch (OperationCanceledException)
        {
            Diagnostics.Add(
                "main-workspace-jslot-cancelled: Export cancelled.");
            Status = "RaceMenu preset export cancelled.";
        }
        finally
        {
            EndOperation(source);
        }
    }

    private bool TryGetRaceMenuCarrier(
        SkyrimMainWorkspaceIdentity identity,
        out WorkspacePath source)
    {
        source = default;
        if (!artifactsByIdentity.TryGetValue(
                identity,
                out SkyrimMainWorkspaceArtifactHandoff? artifact))
            return false;
        if (artifact.ProposalPath is { } proposal &&
            string.Equals(
                Path.GetExtension(proposal.Value),
                ".jslot",
                StringComparison.OrdinalIgnoreCase))
        {
            source = proposal;
            return true;
        }
        if (string.Equals(
                Path.GetExtension(artifact.Path.Value),
                ".jslot",
                StringComparison.OrdinalIgnoreCase))
        {
            source = artifact.Path;
            return true;
        }
        return false;
    }

    private SkyrimMainWorkspaceSession BuildSession() =>
        new(
            "1",
            snapshot!.IntakeFingerprint,
            SelectedRows.Select(row => row.Identity).ToImmutableArray(),
            Rows.Where(row =>
                    row.IsChanged || row.IsDeletePending)
                .Select(row => row.Draft)
                .ToImmutableArray(),
            Artifacts.ToImmutableArray(),
            false);

    private static bool SessionsEquivalent(
        SkyrimMainWorkspaceSession? actual,
        SkyrimMainWorkspaceSession expected) =>
        actual is not null &&
        actual.SchemaVersion == expected.SchemaVersion &&
        actual.IntakeFingerprint == expected.IntakeFingerprint &&
        actual.Selection.SequenceEqual(expected.Selection) &&
        actual.Drafts.SequenceEqual(expected.Drafts) &&
        actual.Artifacts.SequenceEqual(expected.Artifacts) &&
        actual.RuntimeAuthority == expected.RuntimeAuthority;

    private void CommitSnapshot(
        ReviewedGameIntake intake,
        SkyrimMainWorkspaceSnapshot accepted,
        SkyrimMainWorkspaceSettings acceptedSettings)
    {
        acceptedIntake = intake;
        snapshot = accepted;
        settings = acceptedSettings;
        rowsByIdentity.Clear();
        Rows.Clear();
        VisibleRows.Clear();
        SelectedRows.Clear();
        artifactsByIdentity.Clear();
        Artifacts.Clear();
        selectedRow = null;
        resolvedLeveledNpc = null;
        foreach (SkyrimMainWorkspaceRecord record in accepted.Records)
        {
            var row =
                new SkyrimMainWorkspaceRowViewModel(record);
            rowsByIdentity.Add(record.Identity, row);
            Rows.Add(row);
        }
        RefreshVisibleRows();
        PreviewImagePath = null;
        SetPreviewImageSha256(null);
        PreviewScenePath = null;
        PreviewSceneSha256 = null;
        PreviewViews.Clear();
        ResolvedPreviewAssets.Clear();
        PreviewWarnings.Clear();
        PreviewComparisons.Clear();
        PreviewRouteText = "No composed route";
        npcVisualCache.Clear();
        PreviewIsStale = false;
        SessionSha256 = null;
        Raise(nameof(Snapshot));
        Raise(nameof(Search));
        Raise(nameof(ShowNpcs));
        Raise(nameof(ShowLeveledNpcs));
        Raise(nameof(Gender));
        Raise(nameof(ChangedOnly));
        Raise(nameof(IncludeDeleted));
        RaisePreviewProperties();
        RaiseCategoryProperties();
        RaiseSelectedDetails();
        RaiseCommandState();
    }

    private void ClearSnapshot()
    {
        snapshot = null;
        rowsByIdentity.Clear();
        artifactsByIdentity.Clear();
        Rows.Clear();
        VisibleRows.Clear();
        SelectedRows.Clear();
        Artifacts.Clear();
        selectedRow = null;
        resolvedLeveledNpc = null;
        copiedAppearance = null;
        previewConfiguration = null;
        PreviewImagePath = null;
        SetPreviewImageSha256(null);
        PreviewScenePath = null;
        PreviewSceneSha256 = null;
        PreviewViews.Clear();
        ResolvedPreviewAssets.Clear();
        PreviewWarnings.Clear();
        PreviewComparisons.Clear();
        PreviewRouteText = "No composed route";
        npcVisualCache.Clear();
        PreviewIsStale = false;
        SessionSha256 = null;
        Raise(nameof(Snapshot));
        Raise(nameof(SelectedRow));
        Raise(nameof(VisibleCount));
        Raise(nameof(SelectedCount));
        RaiseSelectedDetails();
    }

    private void UpdateFilter(SkyrimMainWorkspaceFilter filter)
    {
        settings = settings with { Filter = filter };
        RefreshVisibleRows();
        Raise(nameof(Search));
        Raise(nameof(ShowNpcs));
        Raise(nameof(ShowLeveledNpcs));
        Raise(nameof(Gender));
        Raise(nameof(ChangedOnly));
        Raise(nameof(IncludeDeleted));
        RaiseCategoryProperties();
    }

    private void UpdateCategory(NpcCategory category, bool visible)
    {
        ImmutableHashSet<NpcCategory> categories =
            settings.Filter.Categories;
        categories = visible
            ? categories.Add(category)
            : categories.Remove(category);
        UpdateFilter(settings.Filter with { Categories = categories });
    }

    private void RaiseCategoryProperties()
    {
        Raise(nameof(ShowUnique));
        Raise(nameof(ShowGeneric));
        Raise(nameof(ShowTemplate));
        Raise(nameof(ShowUnused));
    }

    private void RaisePreviewProperties()
    {
        Raise(nameof(PreviewOptions));
        Raise(nameof(PreviewMode));
        Raise(nameof(PreviewGender));
        Raise(nameof(RenderBody));
        Raise(nameof(RenderUnderarmor));
        Raise(nameof(RenderArmor));
        Raise(nameof(RenderHeadwear));
        Raise(nameof(RenderGore));
        Raise(nameof(ApplyBoneMorphs));
        Raise(nameof(ApplyVertexMorphs));
        Raise(nameof(ApplyBodyWeight));
        Raise(nameof(ApplySculpt));
    }

    private void RefreshVisibleRows()
    {
        ImmutableHashSet<SkyrimMainWorkspaceIdentity> selected =
            SelectedRows.Select(row => row.Identity)
                .ToImmutableHashSet();
        ImmutableHashSet<SkyrimMainWorkspaceIdentity> visible =
            SkyrimMainWorkspaceRules.Filter(
                    Rows.Select(row =>
                        row.Source with
                        {
                            ChangeState = row.IsDeletePending
                                ? NpcChangeState.Deleted
                                : row.IsChanged
                                    ? NpcChangeState.Changed
                                    : row.Source.ChangeState
                        }),
                    settings.Filter)
                .Select(record => record.Identity)
                .ToImmutableHashSet();
        VisibleRows.Clear();
        foreach (SkyrimMainWorkspaceRowViewModel row in Rows)
        {
            if (visible.Contains(row.Identity))
                VisibleRows.Add(row);
        }
        ImmutableArray<SkyrimMainWorkspaceIdentity> retained =
            VisibleRows.Where(row => selected.Contains(row.Identity))
                .Select(row => row.Identity)
                .ToImmutableArray();
        foreach (SkyrimMainWorkspaceRowViewModel row in Rows)
            row.IsSelected = retained.Contains(row.Identity);
        SelectedRows.Clear();
        foreach (SkyrimMainWorkspaceRowViewModel row in VisibleRows)
        {
            if (retained.Contains(row.Identity))
                SelectedRows.Add(row);
        }
        if (selectedRow is not null &&
            !visible.Contains(selectedRow.Identity))
            selectedRow = SelectedRows.FirstOrDefault();
        Raise(nameof(VisibleCount));
        Raise(nameof(SelectedCount));
        Raise(nameof(SelectedRow));
        RaiseSelectedDetails();
        RaiseCommandState();
    }

    private void RefreshArtifacts()
    {
        Artifacts.Clear();
        foreach (SkyrimMainWorkspaceArtifactHandoff artifact in
                 Rows.Select(row => row.Identity)
                     .Where(artifactsByIdentity.ContainsKey)
                     .Select(identity =>
                         artifactsByIdentity[identity]))
            Artifacts.Add(artifact);
        Raise(nameof(SaveRaceMenuAvailabilityText));
    }

    private void RaiseSelectedDetails()
    {
        Raise(nameof(SelectedIdentityText));
        Raise(nameof(SelectedProviderText));
        Raise(nameof(SelectedDetailText));
        Raise(nameof(SelectedStateText));
        Raise(nameof(SaveRaceMenuAvailabilityText));
        Raise(nameof(NpcVisualPreviewTargetText));
    }

    private void SetPreviewImageSha256(Sha256Hash? value)
    {
        if (previewImageHash == value)
            return;
        previewImageHash = value;
        Raise(nameof(PreviewImageSha256));
        Raise(nameof(PreviewAuthorityText));
        RaiseCommandState();
    }

    private void PresentSelectionDiagnostic(
        SkyrimMainWorkspaceRowViewModel? row)
    {
        if (row?.Source.IsEmptyLeveledList != true)
            return;
        const string message =
            "main-workspace-empty-lvln: The selected leveled NPC list is empty; random member and reroll are unavailable.";
        if (!Diagnostics.Contains(message))
            Diagnostics.Add(message);
        Status =
            "Empty LVLN retained as evidence; reroll is unavailable.";
    }

    private WorkspacePath? FindSourcePlugin(PluginName plugin) =>
        acceptedIntake?.Plugins.FirstOrDefault(item =>
            item.Enabled &&
            item.Exists &&
            item.ReadSucceeded &&
            item.Plugin == plugin)?.Path;

    private static bool IsAcceptedPreview(
        SkyrimMainWorkspacePreviewResult result,
        SkyrimMainWorkspaceRecord selected) =>
        result.Written &&
        result.SelectedRecord?.Identity == selected.Identity &&
        result.ImagePath is not null &&
        result.ImageSha256 is not null &&
        result.ScenePath is not null &&
        result.SceneSha256 is not null &&
        !result.RuntimeAuthority &&
        !result.Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private CancellationTokenSource BeginOperation(
        CancellationToken external = default)
    {
        if (HasActiveOperation)
            throw new InvalidOperationException(
                "Another workbench operation is already active.");
        cancellation = external.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(external)
            : new CancellationTokenSource();
        HasActiveOperation = true;
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellation, source))
        {
            cancellation.Dispose();
            cancellation = null;
            HasActiveOperation = false;
        }
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic item in source)
            Diagnostics.Add(
                $"{item.Code}: {item.Message}");
    }

    private static bool TryCreateNpcVisualPackagePreviewTarget(
        WorkspacePath requestedManifest,
        PackageVerifyResult verification,
        out NpcVisualPackagePreviewTarget? target,
        out Diagnostic? diagnostic)
    {
        target = null;
        diagnostic = null;
        if (!verification.Verified ||
            verification.Artifact is not { } artifact ||
            verification.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            return false;
        if (!string.Equals(
                artifact.ManifestPath.Value,
                requestedManifest.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = new Diagnostic(
                "main-workspace-preview-package-identity",
                DiagnosticSeverity.Error,
                "The verified package manifest path does not match the requested file.");
            return false;
        }
        try
        {
            var plugin = new PluginName(
                artifact.OutputPlugin);
            target = new NpcVisualPackagePreviewTarget(
                new SkyrimMainWorkspaceIdentity(
                    plugin,
                    plugin,
                    artifact.TargetFormId,
                    "NPC_"),
                new NpcVisualPreviewPackageOverlay(
                    artifact.ManifestPath,
                    artifact.ManifestSha256));
            return true;
        }
        catch (ArgumentException exception)
        {
            diagnostic = new Diagnostic(
                "main-workspace-preview-package-identity",
                DiagnosticSeverity.Error,
                exception.Message);
            return false;
        }
    }

    private void RaiseNpcVisualPackageProperties()
    {
        Raise(nameof(NpcVisualPackageManifestPath));
        Raise(nameof(NpcVisualPackageSha256));
        Raise(nameof(NpcVisualPreviewTargetText));
        RaiseCommandState();
    }

    private async ValueTask<NpcVisualPreviewPackageOverlay?>
        FindNpcVisualPackageOverlayAsync(
            SkyrimMainWorkspaceIdentity identity,
            CancellationToken cancellationToken)
    {
        if (!artifactsByIdentity.TryGetValue(
                identity,
                out SkyrimMainWorkspaceArtifactHandoff? artifact))
            return null;
        string? directory = Directory.Exists(
                artifact.Path.Value)
            ? artifact.Path.Value
            : Path.GetDirectoryName(artifact.Path.Value);
        for (int depth = 0;
             depth < 5 && directory is not null;
             depth++)
        {
            var root = new WorkspacePath(directory);
            if (!root.IsUnder(labRoot))
                break;
            string candidate = Path.Combine(
                directory, "npcmanager-package.json");
            if (File.Exists(candidate) &&
                !Directory.Exists(candidate))
            {
                var path = new WorkspacePath(candidate);
                return new NpcVisualPreviewPackageOverlay(
                    path,
                    await HashFileAsync(
                        path, cancellationToken));
            }
            directory = Directory.GetParent(
                directory)?.FullName;
        }
        return null;
    }

    private static string BuildNpcVisualFingerprint(
        ReviewedGameIntake intake,
        SkyrimMainWorkspaceIdentity identity,
        NpcVisualPreviewPackageOverlay? overlay,
        SkyrimMainWorkspacePreviewOptions options)
    {
        var text = new System.Text.StringBuilder();
        text.Append(intake.IntakeFingerprint.Value)
            .Append('|')
            .Append(identity.OwnerPlugin.Value)
            .Append('|')
            .Append(identity.WinningProvider.Value)
            .Append('|')
            .Append(identity.FormId.Value.ToString(
                "X8", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(overlay?.ExpectedManifestSha256.Value ?? "none")
            .Append('|')
            .Append(options);
        foreach (PluginClosureReviewEntry plugin in
                 intake.Plugins.OrderBy(item => item.Order))
            text.Append('|')
                .Append(plugin.Plugin.Value)
                .Append(':')
                .Append(plugin.SourceHash?.Value ?? "missing");
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                text.ToString())));
    }

    private static async ValueTask<bool>
        ValidateCachedNpcVisualBundleAsync(
            NpcVisualPreviewBundle bundle,
            ReviewedGameIntake intake,
            NpcVisualPreviewPackageOverlay? overlay,
            CancellationToken cancellationToken)
    {
        foreach (PluginClosureReviewEntry plugin in
                 intake.Plugins.Where(item =>
                     item.Enabled &&
                     item.SourceHash is not null))
        {
            if (!File.Exists(plugin.Path.Value) ||
                await HashFileAsync(
                    plugin.Path, cancellationToken) !=
                plugin.SourceHash!.Value)
                return false;
        }
        if (overlay is { } package &&
            (!File.Exists(package.ManifestPath.Value) ||
             await HashFileAsync(
                 package.ManifestPath,
                 cancellationToken) !=
             package.ExpectedManifestSha256))
            return false;
        IEnumerable<(WorkspacePath Path, Sha256Hash Hash)> files =
            bundle.Views.SelectMany(view =>
                new[]
                {
                    (view.ImagePath, view.ImageSha256),
                    (view.RoleMaskPath, view.RoleMaskSha256)
                })
            .Append((
                bundle.ContactSheetPath,
                bundle.ContactSheetSha256))
            .Append((
                bundle.BundlePath,
                bundle.BundleSha256))
            .Append((
                bundle.HashManifestPath,
                bundle.HashManifestSha256))
            .Append((
                bundle.RenderEvidence.StatusPath,
                bundle.RenderEvidence.StatusSha256));
        foreach ((WorkspacePath path, Sha256Hash hash) in files)
        {
            if (!File.Exists(path.Value) ||
                await HashFileAsync(
                    path, cancellationToken) != hash)
                return false;
        }
        return true;
    }

    private void ApplyNpcVisualBundle(
        NpcVisualPreviewBundle bundle)
    {
        PreviewViews.Clear();
        foreach (NpcVisualPreviewView view in bundle.Views)
            PreviewViews.Add(new(
                view.Id,
                view.ImagePath.Value,
                view.ImageSha256.Value));
        ResolvedPreviewAssets.Clear();
        foreach (NpcVisualAsset asset in bundle.Source.Assets
                     .OrderBy(item => item.Role)
                     .ThenBy(
                         item => item.AssetPath.Value,
                         StringComparer.OrdinalIgnoreCase))
            ResolvedPreviewAssets.Add(
                $"{asset.Role} · {asset.Provider} · " +
                $"{asset.AssetPath.Value} · {asset.Sha256.Value}");
        PreviewWarnings.Clear();
        PreviewComparisons.Clear();
        foreach (Diagnostic warning in bundle.Diagnostics.Where(item =>
                     item.Severity != DiagnosticSeverity.Error))
            PreviewWarnings.Add(
                $"{warning.Code}: {warning.Message}");
        PreviewImagePath = bundle.ContactSheetPath.Value;
        SetPreviewImageSha256(bundle.ContactSheetSha256);
        PreviewScenePath = bundle.BundlePath;
        PreviewSceneSha256 = bundle.BundleSha256;
        PreviewRouteText =
            $"{bundle.Source.Route} · {bundle.Source.Race} · " +
            $"{bundle.Source.Sex} · weight {bundle.Source.Weight:0.##}";
        PreviewIsStale = false;
    }

    private void PresentError(string code, string message)
    {
        Diagnostics.Add($"{code}: {message}");
        Status = message;
    }

    private void PresentOperationFailure(
        string code,
        string message,
        Exception exception)
    {
        WorkspacePath? reportPath = operationFailureSink?.Invoke(exception);
        string report = reportPath is { } path
            ? $" Report: {path.Value}"
            : string.Empty;
        PresentError(code, $"{message}: {exception.Message}.{report}");
    }

    private bool RefuseHandoff(string code, string message)
    {
        PresentError(code, message);
        return false;
    }

    private void RaiseCommandState()
    {
        foreach (MainWorkspaceCommand command in new[]
                 {
                     loadCommand,
                     cancelCommand,
                     markChangedCommand,
                     resetCommand,
                     markDeleteCommand,
                     restoreCommand,
                     randomNpcCommand,
                     rerollCommand,
                     renderPreviewCommand,
                     refreshPreviewCommand,
                     comparePreviewCommand,
                     copyPlaceAtMeCommand,
                     editNpcCommand,
                     editHeadPartsCommand,
                     editFaceCommand,
                     editBodyCommand,
                     editOutfitCommand,
                     loadRaceMenuPresetCommand,
                     saveRaceMenuPresetCommand,
                     copyAppearanceCommand,
                     pasteAppearanceCommand,
                     charGenOptionsCommand,
                     buildCharGenCommand,
                     savePackageCommand,
                     exportSceneNifCommand,
                     lightingCommand,
                     animationCommand,
                     saveSessionCommand
                 })
            command.RaiseCanExecuteChanged();
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    private async void RunCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            PresentOperationFailure(
                "main-workspace-command-failed",
                $"Unexpected {exception.GetType().Name}",
                exception);
        }
    }

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        try
        {
            hairRegionsDesktopComposition?.Dispose();
            hairRegionsDesktopComposition = null;
        }
        finally
        {
            npcVisualLifetime?.Dispose();
        }
    }

    private sealed record NpcVisualPackagePreviewTarget(
        SkyrimMainWorkspaceIdentity Identity,
        NpcVisualPreviewPackageOverlay Overlay);

    private sealed record SkyrimMainWorkspacePreviewConfiguration(
        WorkspacePath ManifestPath,
        Sha256Hash ExpectedManifestSha256,
        WorkspacePath AssetRoot,
        WorkspacePath ScenePath,
        WorkspacePath ImagePath,
        string? VariantId,
        PreviewAnimationSelection? Animation,
        PreviewLightingPreset? Lighting)
    {
        public SkyrimMainWorkspacePreviewRequest ToRequest(
            SkyrimMainWorkspaceRecord selected,
            SkyrimMainWorkspacePreviewOptions options) =>
            new(
                selected,
                ManifestPath,
                ExpectedManifestSha256,
                options,
                AssetRoot,
                ScenePath,
                ImagePath,
                VariantId,
                Animation,
                Lighting);
    }

    private sealed class MainWorkspaceCommand(
        Action<object?> execute,
        Predicate<object?> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            canExecute(parameter);

        public void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;
            execute(parameter);
        }

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record NpcVisualPreviewViewItem(
    string Id,
    string ImagePath,
    string Sha256);

public sealed record NpcVisualComparisonViewItem(
    string Label,
    string SideBySidePath,
    string OverlayPath,
    string HeatmapPath,
    string EvidencePath,
    double MeanAbsoluteRgbDifference);
