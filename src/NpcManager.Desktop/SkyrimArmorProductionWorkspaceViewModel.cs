using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// Production owner for one reviewed Skyrim ARMO source and one proposal-bound
/// output. The child editor remains an immutable, I/O-free modal.
/// </summary>
public sealed class SkyrimArmorProductionWorkspaceViewModel :
    NotifyViewModel, IDisposable
{
    private const string EmptyResult = "Not available";
    private readonly ISkyrimArmorProductionReader reader;
    private readonly ISkyrimArmorProductionTransactionService transactionService;
    private readonly IFormChoiceService? formChoiceService;
    private readonly IAssetChoiceService? meshChoiceService;
    private readonly SkyrimMeshPickerModal? meshPickerModal;
    private readonly WorkspacePath labRoot;
    private readonly AsyncCommand loadCommand;
    private readonly AsyncCommand reviewCommand;
    private readonly AsyncCommand executeCommand;
    private readonly DelegateCommand cancelCommand;
    private CancellationTokenSource? cancellation;
    private ReviewedGameIntake? intake;
    private SkyrimArmorProductionSource? loaded;
    private FormChoiceSearchResult? armorAddonReferenceCatalog;
    private AssetChoiceSearchResult? meshSearchResult;
    private SkyrimArmorEditorDocument? authoredDocument;
    private SkyrimArmorProductionProposal? reviewedProposal;

    public SkyrimArmorProductionWorkspaceViewModel(
        ISkyrimArmorProductionReader reader,
        ISkyrimArmorProductionTransactionService transactionService,
        WorkspacePath labRoot,
        IFormChoiceService? formChoiceService = null,
        IAssetChoiceService? meshChoiceService = null,
        SkyrimMeshPickerModal? meshPickerModal = null)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.transactionService = transactionService ??
            throw new ArgumentNullException(nameof(transactionService));
        this.labRoot = labRoot;
        this.formChoiceService = formChoiceService;
        this.meshChoiceService = meshChoiceService;
        this.meshPickerModal = meshPickerModal;
        loadCommand = new AsyncCommand(LoadAsync, CanLoad);
        reviewCommand = new AsyncCommand(
            ReviewAsync,
            () => HasAcceptedDocument && !IsBusy);
        executeCommand = new AsyncCommand(
            ExecuteAsync,
            () => IsReviewed && !HasCompleted && !IsBusy);
        cancelCommand = new DelegateCommand(
            _ => cancellation?.Cancel(),
            _ => IsBusy);
    }

    public ObservableCollection<string> Diagnostics { get; } = [];
    public ICommand LoadCommand => loadCommand;
    public ICommand ReviewCommand => reviewCommand;
    public ICommand ExecuteCommand => executeCommand;
    public ICommand CancelCommand => cancelCommand;
    public bool IsLoaded => loaded is not null;
    public bool CanOpenEditor => IsLoaded && !IsBusy;
    public bool HasAcceptedDocument => authoredDocument is not null;
    public string SourceSummary => loaded is null
        ? "No reviewed ARMO source is loaded."
        : $"{loaded.SourceReference.Plugin.Value}|{loaded.SourceReference.FormId} · " +
          $"{loaded.ExistingEditorIds.Length} ARMO EditorID(s) · " +
          $"{loaded.ArmorAddonSlotEvidence.Length} ARMA slot-evidence row(s).";
    public string DocumentSummary => authoredDocument is null
        ? "No accepted immutable ARMO document."
        : $"{authoredDocument.Intent} · {authoredDocument.EditorId.Value} · " +
          $"{authoredDocument.ArmorAddons.Length} ordered ARMA · " +
          $"{authoredDocument.Keywords.Length} unique KWDA.";

    private string sourcePlugin = string.Empty;
    public string SourcePlugin
    {
        get => sourcePlugin;
        set
        {
            if (Set(ref sourcePlugin, value ?? string.Empty)) ClearLoaded();
        }
    }

    private string sourceFormId = "0x00000A00";
    public string SourceFormId
    {
        get => sourceFormId;
        set
        {
            if (Set(ref sourceFormId, value ?? string.Empty)) ClearLoaded();
        }
    }

    private string newTargetFormId = "0x00000B00";
    public string NewTargetFormId
    {
        get => newTargetFormId;
        set
        {
            if (Set(ref newTargetFormId, value ?? string.Empty)) ClearLoaded();
        }
    }

    private string newArmorAddonTargetFormId = "0x00000C00";
    public string NewArmorAddonTargetFormId
    {
        get => newArmorAddonTargetFormId;
        set
        {
            if (!Set(ref newArmorAddonTargetFormId, value ?? string.Empty))
                return;
            InvalidateReview();
        }
    }

    private string armorAddonOutputPlugin = string.Empty;
    public string ArmorAddonOutputPlugin
    {
        get => armorAddonOutputPlugin;
        set
        {
            if (!Set(ref armorAddonOutputPlugin, value ?? string.Empty)) return;
            InvalidateReview();
        }
    }

    private string proposalPath = string.Empty;
    public string ProposalPath
    {
        get => proposalPath;
        set
        {
            if (!Set(ref proposalPath, value ?? string.Empty)) return;
            InvalidateReview();
        }
    }

    private string outputPlugin = string.Empty;
    public string OutputPlugin
    {
        get => outputPlugin;
        set
        {
            if (!Set(ref outputPlugin, value ?? string.Empty)) return;
            InvalidateReview();
        }
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            Raise(nameof(IsNotBusy));
            Raise(nameof(CanOpenEditor));
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
            if (Set(ref isReviewed, value)) RaiseCommandState();
        }
    }

    private bool hasCompleted;
    public bool HasCompleted
    {
        get => hasCompleted;
        private set
        {
            if (Set(ref hasCompleted, value)) RaiseCommandState();
        }
    }

    private int progressPercent;
    public int ProgressPercent
    {
        get => progressPercent;
        private set => Set(ref progressPercent, value);
    }

    private string status =
        "Review a copied Skyrim workspace before loading an ARMO source.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string verdict = "Not loaded";
    public string Verdict
    {
        get => verdict;
        private set => Set(ref verdict, value);
    }

    private string proposalSha256 = EmptyResult;
    public string ProposalSha256
    {
        get => proposalSha256;
        private set => Set(ref proposalSha256, value);
    }

    private string armorAddonProposalSha256 = EmptyResult;
    public string ArmorAddonProposalSha256
    {
        get => armorAddonProposalSha256;
        private set => Set(ref armorAddonProposalSha256, value);
    }

    private string armorAddonResultPlugin = EmptyResult;
    public string ArmorAddonResultPlugin
    {
        get => armorAddonResultPlugin;
        private set => Set(ref armorAddonResultPlugin, value);
    }

    private string armorAddonResultSha256 = EmptyResult;
    public string ArmorAddonResultSha256
    {
        get => armorAddonResultSha256;
        private set => Set(ref armorAddonResultSha256, value);
    }

    private string resultPlugin = EmptyResult;
    public string ResultPlugin
    {
        get => resultPlugin;
        private set => Set(ref resultPlugin, value);
    }

    private string resultSha256 = EmptyResult;
    public string ResultSha256
    {
        get => resultSha256;
        private set => Set(ref resultSha256, value);
    }

    public void ApplyReviewedIntake(ReviewedGameIntake reviewedIntake)
    {
        ArgumentNullException.ThrowIfNull(reviewedIntake);
        intake = reviewedIntake;
        sourcePlugin = reviewedIntake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded)
            .OrderByDescending(item => item.Requested)
            .ThenByDescending(item => item.Order)
            .Select(item => item.Plugin.Value)
            .FirstOrDefault() ?? string.Empty;
        Raise(nameof(SourcePlugin));
        ClearLoaded();
        Status = "Reviewed workspace accepted. Enter one explicit source-owned ARMO FormID.";
    }

    public void ClearReviewedIntake()
    {
        intake = null;
        sourcePlugin = string.Empty;
        Raise(nameof(SourcePlugin));
        ClearLoaded();
        Status = "Workspace authority is stale. Review the copied workspace again.";
    }

    public async Task LoadAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildReadRequest(
                out SkyrimArmorProductionReadRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Loading",
            "Reading one explicit source-owned ARMO from the reviewed copied closure.");
        try
        {
            SkyrimArmorProductionReadResult result = await Task.Run(
                () => reader.Read(request!),
                source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Accepted || result.Source is null)
            {
                Verdict = "Load refused";
                Status = "No Armor authoring transaction was created.";
                return;
            }
            FormChoiceSearchResult? typedReferences = null;
            if (formChoiceService is not null)
            {
                typedReferences = await formChoiceService.SearchAsync(
                    new FormChoiceSearchRequest(
                        GameEdition.SkyrimSpecialEdition,
                        result.Source.Intake.DataRoot,
                        [
                            new RecordSignature("RACE"),
                            new RecordSignature("TXST"),
                            new RecordSignature("FLST"),
                            new RecordSignature("FSTS"),
                            new RecordSignature("ARTO")
                        ],
                        null,
                        null,
                        true,
                        result.Source.PluginOrder),
                    source.Token);
                AddDiagnostics(typedReferences.Diagnostics);
                if (typedReferences.Diagnostics.Any(item =>
                        item.Severity == DiagnosticSeverity.Error))
                {
                    Verdict = "Load refused";
                    Status =
                        "The reviewed typed Armor-addon reference catalog could not be closed.";
                    return;
                }
            }
            AssetChoiceSearchResult? meshSearch = null;
            if (meshChoiceService is not null)
            {
                meshSearch = await meshChoiceService.SearchAsync(
                    new AssetChoiceSearchRequest(
                        GameEdition.SkyrimSpecialEdition,
                        result.Source.Intake.DataRoot,
                        AssetChoiceKind.Mesh,
                        null,
                        result.Source.PluginOrder),
                    source.Token);
                SkyrimMeshPickerCatalogResult validatedMeshCatalog =
                    SkyrimMeshPickerRules.BuildCatalog(
                        new SkyrimMeshPickerCatalogRequest(
                            GameEdition.SkyrimSpecialEdition,
                            result.Source.Intake.DataRoot,
                            meshSearch,
                            null));
                AddDiagnostics(validatedMeshCatalog.Diagnostics);
                if (!validatedMeshCatalog.Accepted)
                {
                    Verdict = "Load refused";
                    Status =
                        "The reviewed Skyrim mesh catalog could not be closed.";
                    return;
                }
            }
            loaded = result.Source;
            armorAddonReferenceCatalog = typedReferences;
            meshSearchResult = meshSearch;
            authoredDocument = null;
            InvalidateReview();
            Verdict = "Source loaded";
            Status = "Reviewed ARMO source loaded. Open the editor to author one immutable document.";
            RaiseLoadedState();
            RaiseDocumentState();
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "ARMO load cancelled; no transaction was retained.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Load failed";
            Status = "No Armor authoring transaction was retained.";
        }
        finally { Release(source); }
    }

    public SkyrimArmorEditorViewModel CreateEditor(
        SkyrimArmorAddonRowPicker? armorAddonPicker = null)
    {
        if (!CanOpenEditor || loaded is null)
            throw new InvalidOperationException(
                "Load one reviewed source-owned ARMO before opening the editor.");
        SkyrimArmorEditorDocument opening = authoredDocument is null
            ? loaded.NewFromTemplate
            : authoredDocument with
            {
                Intent = SkyrimArmorEditorIntent.EditAuthored
            };
        return CreateEditor(opening, authoredDocument, armorAddonPicker);
    }

    public SkyrimArmorAddonReferenceEditorViewModel
        CreateArmorAddonReferenceEditor(
            SkyrimArmorEditorDocument armorDocument,
            int? selectedIndex,
            SkyrimArmorAddonDeepEditor deepEditor)
    {
        ArgumentNullException.ThrowIfNull(armorDocument);
        ArgumentNullException.ThrowIfNull(deepEditor);
        if (!CanOpenEditor || loaded is null)
            throw new InvalidOperationException(
                "Load one reviewed source-owned ARMO before choosing an Armor-addon.");
        SkyrimArmorAddonReferenceRow? openingRow = null;
        if (selectedIndex is int index)
        {
            if (index < 0 || index >= armorDocument.ArmorAddons.Length)
                throw new InvalidOperationException(
                    "The selected Armor-addon row no longer exists.");
            FormReference current = armorDocument.ArmorAddons[index];
            openingRow = (armorDocument.AuthoredArmorAddons.IsDefault
                    ? []
                    : armorDocument.AuthoredArmorAddons)
                .FirstOrDefault(row => SameReference(row.Reference, current));
            if (openingRow is null)
            {
                SkyrimArmorAddonProductionCatalogEntry? catalogEntry =
                    FindWinningArmorAddon(current);
                if (catalogEntry is not null)
                    openingRow = SkyrimArmorAddonReferenceEditorRules.Choose(
                        GameEdition.SkyrimSpecialEdition,
                        armorDocument.Race,
                        catalogEntry.Candidate).Row;
            }
        }
        return new SkyrimArmorAddonReferenceEditorViewModel(
            armorDocument.Race,
            openingRow,
            loaded.ArmorAddonCatalog.Select(item => item.Candidate)
                .ToImmutableArray(),
            deepEditor);
    }

    public SkyrimArmorAddonEditorViewModel CreateArmorAddonEditor(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!CanOpenEditor || loaded is null)
            throw new InvalidOperationException(
                "Load one reviewed source-owned ARMO before deep editing an Armor-addon.");
        FormReference sourceReference = current.AuthoredProposal is { } authoredSource
            ? new FormReference(
                new PluginName(Path.GetFileName(
                    authoredSource.SourcePlugin.Value)),
                authoredSource.SourceFormId)
            : current.Reference;
        SkyrimArmorAddonProductionCatalogEntry? entry =
            FindWinningArmorAddon(sourceReference);
        if (entry?.EditableDocument is not { } editable)
            throw new InvalidOperationException(
                "This winning Armor-addon is not source-owned by its qualified FormKey and cannot be deep edited safely in this transaction. It may still be selected unchanged.");
        SkyrimArmorAddonEditorDocument opening = current.AuthoredProposal is
        { CompleteDocument: true, Mode: ArmorAddonProposalMode.Override } authored
            ? ApplyAuthoredArmorAddon(editable, authored)
            : editable;
        WorkspacePath outputProposal = current.AuthoredProposal is { } existing &&
                                               existing.OutputProposal.IsUnder(labRoot)
            ? existing.OutputProposal
            : BuildNestedArmorAddonProposal(current.Reference);
        SkyrimArmorAddonEditorDocument? blank = BuildNewArmorAddonDocument(
            editable,
            owningArmorRace,
            SkyrimArmorAddonEditorIntent.BlankNew);
        SkyrimArmorAddonEditorDocument? template = BuildNewArmorAddonDocument(
            editable,
            owningArmorRace,
            SkyrimArmorAddonEditorIntent.NewFromTemplate);
        return new SkyrimArmorAddonEditorViewModel(
            opening,
            owningArmorRace,
            loaded.ExistingArmorAddonEditorIds,
            outputProposal,
            intent => intent switch
            {
                SkyrimArmorAddonEditorIntent.BlankNew => blank,
                SkyrimArmorAddonEditorIntent.NewFromTemplate => template,
                SkyrimArmorAddonEditorIntent.OverrideExisting => editable,
                SkyrimArmorAddonEditorIntent.EditAuthored
                    when current.AuthoredProposal is not null => opening with
                    {
                        Intent = SkyrimArmorAddonEditorIntent.EditAuthored
                    },
                _ => null
            },
            _ => new SkyrimArmorAddonPreviewResult(
                false,
                "Armor-addon preview is unavailable in this nested production boundary; no render or runtime authority was granted."),
            _ => new SkyrimArmorAddonDeletionResult(
                false,
                "Armor-addon delete or revert requires separate dependency evidence and is unavailable in this nested transaction."),
            meshPathPicker: CreateMeshPathPicker(),
            referenceCatalog: armorAddonReferenceCatalog);
    }

    public SkyrimArmorEditorViewModel CreateOutfitChildEditor(
        SkyrimOutfitEditorItem basis,
        SkyrimArmorAddonRowPicker? armorAddonPicker = null)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (!CanOpenEditor || loaded is null)
            throw new InvalidOperationException(
                "Load the explicitly selected ARMO source in the Armor task first.");
        if (basis.Kind != SkyrimOutfitEditorItemKind.Armor)
            throw new InvalidOperationException(
                "The Armor child editor requires an explicitly selected ARMO candidate.");

        SkyrimArmorEditorDocument? editDocument = basis.AuthoredArmor;
        SkyrimArmorEditorDocument opening;
        if (editDocument is null)
        {
            if (!SameReference(basis.Reference, loaded.SourceReference))
                throw new InvalidOperationException(
                    "The selected outfit ARMO does not match the reviewed source loaded in the Armor task.");
            opening = loaded.NewFromTemplate;
        }
        else
        {
            if (editDocument.SourcePlugin != loaded.SourcePluginPath ||
                editDocument.SourceFormId != loaded.SourceReference.FormId)
                throw new InvalidOperationException(
                    "The authored outfit ARMO is not bound to the reviewed Armor source.");
            opening = editDocument with
            {
                Intent = SkyrimArmorEditorIntent.EditAuthored
            };
        }
        return CreateEditor(opening, editDocument, armorAddonPicker);
    }

    public SkyrimOutfitEditorItem CreateOutfitChildItem(
        SkyrimArmorEditorViewModel editor,
        SkyrimOutfitEditorItem basis)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(basis);
        if (!editor.IsAccepted || editor.AcceptedDocument is not { } document)
            throw new InvalidOperationException(
                "Accept the Armor editor before returning an outfit child.");
        FormReference reference;
        if (document.Mode == ArmorProposalMode.New)
        {
            if (document.TargetFormId is not { } target ||
                !TryWorkspacePath(OutputPlugin, out WorkspacePath output))
                throw new InvalidOperationException(
                    "A new outfit ARMO requires the fresh Armor task output plugin and target FormID.");
            reference = new FormReference(
                new PluginName(Path.GetFileName(output.Value)),
                target);
        }
        else
        {
            reference = new FormReference(
                new PluginName(Path.GetFileName(document.SourcePlugin.Value)),
                document.SourceFormId);
        }
        string displayName = string.IsNullOrWhiteSpace(document.Name)
            ? document.EditorId.Value
            : document.Name;
        return new SkyrimOutfitEditorItem(
            reference,
            SkyrimOutfitEditorItemKind.Armor,
            displayName,
            document.SlotMask,
            AuthoredArmor: document);
    }

    public bool ApplyEditor(SkyrimArmorEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.IsAccepted || editor.AcceptedDocument is null) return false;
        authoredDocument = editor.AcceptedDocument;
        InvalidateReview();
        Verdict = "Authoring staged";
        Status = "One complete immutable ARMO is staged. Review writes proposal JSON only.";
        RaiseDocumentState();
        return true;
    }

    public void ReportEditorCancelled() =>
        Status = "Armor editor cancelled or closed; the previously accepted document is unchanged.";

    public async Task ReviewAsync()
    {
        Diagnostics.Clear();
        if (!TryBuildTransactionRequest(
                out SkyrimArmorProductionRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Reviewing",
            "Persisting one hash-bound ARMO proposal; the output ESP must remain absent.");
        SkyrimArmorProductionRequest effectiveRequest = request!;
        try
        {
            SkyrimArmorProductionProposal proposal = await transactionService
                .AnalyzeAsync(effectiveRequest, source.Token);
            AddDiagnostics(proposal.Diagnostics);
            Sha256Hash? proposalHash = proposal.ProposalSha256;
            if (!proposal.IsApplicable || proposalHash is null ||
                !File.Exists(effectiveRequest.OutputProposal.Value) ||
                File.Exists(effectiveRequest.OutputPlugin.Value))
            {
                Verdict = "Review refused";
                Status = "No output plugin was written. Correct diagnostics and use fresh paths.";
                return;
            }
            reviewedProposal = proposal;
            ProposalSha256 = proposalHash.Value.Value;
            ArmorAddonProposalSha256 =
                proposal.ArmorAddonProposals.IsDefaultOrEmpty
                    ? EmptyResult
                    : proposal.ArmorAddonProposals.Single().ProposalSha256
                        ?.Value ?? EmptyResult;
            IsReviewed = true;
            Verdict = "Ready to write";
            Status = proposal.ArmorAddonProposals.IsDefaultOrEmpty
                ? "Proposal retained and output ESP absent. Apply is bound to this exact document."
                : "Child ARMA and parent ARMO proposals retained; both output ESPs are absent and Apply is bound to the exact pair.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Armor Review cancelled; no output plugin was written.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Review failed";
            Status = "No output plugin was written.";
        }
        finally { Release(source); }
    }

    public async Task ExecuteAsync()
    {
        Diagnostics.Clear();
        if (!IsReviewed || reviewedProposal is null)
        {
            PresentInputError("Review the exact ARMO document before writing.");
            return;
        }
        if (!TryBuildTransactionRequest(
                out SkyrimArmorProductionRequest? request,
                out string error))
        {
            PresentInputError(error);
            return;
        }
        CancellationTokenSource source = Begin(
            "Writing",
            "Writing one fresh ARMO ESP and reopening its complete supported surface.");
        SkyrimArmorProductionRequest effectiveRequest = request!;
        ProgressPercent = 20;
        try
        {
            SkyrimArmorProductionResult applied = await transactionService
                .ApplyAsync(effectiveRequest, reviewedProposal, source.Token);
            AddDiagnostics(applied.Diagnostics);
            if (!applied.Applied || applied.Verification is not { IsValid: true })
            {
                Verdict = "Write refused";
                Status = "The ARMO failed immediate readback and is not authoritative.";
                return;
            }
            ProgressPercent = 75;
            SkyrimArmorProductionVerification verified = await transactionService
                .VerifyAsync(effectiveRequest, reviewedProposal, source.Token);
            AddDiagnostics(verified.Diagnostics);
            Sha256Hash? outputHash = verified.OutputSha256;
            ImmutableArray<SkyrimArmorAddonProductionVerification>
                addonVerifications = verified.ArmorAddonVerifications.IsDefault
                    ? []
                    : verified.ArmorAddonVerifications;
            int expectedAddonCount =
                effectiveRequest.ArmorAddonOutputs.IsDefault
                    ? 0
                    : effectiveRequest.ArmorAddonOutputs.Length;
            if (!verified.IsValid || verified.ArmorRecordCount != 1 ||
                verified.OtherRecordCount != 0 || !verified.OwnerMatches ||
                !verified.EditorIdMatches || !verified.DocumentMatches ||
                !verified.MasterSetMatches || outputHash is null ||
                addonVerifications.Length != expectedAddonCount ||
                addonVerifications.Any(item =>
                    !item.IsValid ||
                    item.ArmorAddonRecordCount != 1 ||
                    item.OtherRecordCount != 0 ||
                    !item.OwnerMatches ||
                    !item.EditorIdMatches ||
                    !item.DocumentMatches ||
                    !item.MasterSetMatches ||
                    item.OutputSha256 is null ||
                    !File.Exists(item.OutputPlugin.Value)) ||
                !File.Exists(effectiveRequest.OutputPlugin.Value))
            {
                Verdict = "Verification failed";
                Status = "The explicit second reopen did not prove the exact ARMO output.";
                return;
            }
            HasCompleted = true;
            ProgressPercent = 100;
            ResultPlugin = verified.OutputPlugin.Value;
            ResultSha256 = outputHash.Value.Value;
            if (addonVerifications.IsDefaultOrEmpty)
            {
                ArmorAddonResultPlugin = EmptyResult;
                ArmorAddonResultSha256 = EmptyResult;
            }
            else
            {
                SkyrimArmorAddonProductionVerification addon =
                    addonVerifications.Single();
                ArmorAddonResultPlugin = addon.OutputPlugin.Value;
                ArmorAddonResultSha256 =
                    addon.OutputSha256?.Value ?? EmptyResult;
            }
            Verdict = "STATIC_PASS_RUNTIME_REQUIRED";
            Status = addonVerifications.IsDefaultOrEmpty
                ? "Fresh ARMO written and reopened twice. Preview, equipment, runtime, and visual authority remain false."
                : "Fresh ARMA override and parent ARMO written in order and independently reopened twice. Preview, equipment, runtime, and visual authority remain false.";
        }
        catch (OperationCanceledException)
        {
            Verdict = "Cancelled";
            Status = "Armor write cancelled; no partial output is authoritative.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnostics.Add($"Unexpected {exception.GetType().Name}: {exception.Message}");
            Verdict = "Write failed";
            Status = "No unverified output is authoritative.";
        }
        finally { Release(source); }
    }

    public void Dispose()
    {
        CancellationTokenSource? source = cancellation;
        cancellation = null;
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    private SkyrimArmorEditorViewModel CreateEditor(
        SkyrimArmorEditorDocument opening,
        SkyrimArmorEditorDocument? editDocument,
        SkyrimArmorAddonRowPicker? armorAddonPicker) => new(
        opening,
        loaded?.ExistingEditorIds ?? [],
        intent => intent switch
        {
            SkyrimArmorEditorIntent.BlankNew => loaded?.BlankNew,
            SkyrimArmorEditorIntent.NewFromTemplate => loaded?.NewFromTemplate,
            SkyrimArmorEditorIntent.OverrideExisting => loaded?.OverrideExisting,
            SkyrimArmorEditorIntent.EditAuthored when editDocument is not null =>
                editDocument with { Intent = SkyrimArmorEditorIntent.EditAuthored },
            _ => null
        },
        ResolveSlotEvidence,
        (_, _, _, _) => new SkyrimArmorPreviewResult(
            false,
            "Advisory preview is unavailable in this production boundary; no render or runtime authority was granted."),
        _ => new SkyrimArmorDeletionResult(
            false,
            "Delete or revert requires separate dependency evidence and is not part of this new-ARMO transaction."),
        meshPathPicker: CreateMeshPathPicker(),
        armorAddonPicker: armorAddonPicker);

    private SkyrimMeshPathPicker CreateMeshPathPicker() => (field, currentPath) =>
    {
        if (loaded is null || meshSearchResult is null || meshPickerModal is null)
            return SkyrimMeshPickerRules.Cancel();
        SkyrimMeshPickerCatalogResult catalog = SkyrimMeshPickerRules.BuildCatalog(
            new SkyrimMeshPickerCatalogRequest(
                GameEdition.SkyrimSpecialEdition,
                loaded.Intake.DataRoot,
                meshSearchResult,
                currentPath));
        if (!catalog.Accepted)
        {
            AddDiagnostics(catalog.Diagnostics);
            return SkyrimMeshPickerRules.Cancel();
        }
        return meshPickerModal(field, catalog);
    };

    private ImmutableArray<SkyrimArmorAddonSlotEvidence> ResolveSlotEvidence(
        SkyrimArmorEditorDocument document)
    {
        if (loaded is null || document.ArmorAddons.IsDefault) return [];
        ImmutableArray<SkyrimArmorAddonReferenceRow> authored =
            document.AuthoredArmorAddons.IsDefault
                ? []
                : document.AuthoredArmorAddons;
        var result = ImmutableArray.CreateBuilder<SkyrimArmorAddonSlotEvidence>();
        foreach (FormReference reference in document.ArmorAddons
                     .DistinctBy(item =>
                         $"{item.Plugin.Value.ToUpperInvariant()}|{item.FormId.Value:X8}"))
        {
            SkyrimArmorAddonReferenceRow[] authoredMatches = authored
                .Where(row => SameReference(row.Reference, reference))
                .ToArray();
            if (authoredMatches.Length > 1) return [];
            if (authoredMatches.Length == 1)
            {
                uint? mask = authoredMatches[0].AuthoredProposal?.Patch.SlotMask;
                if (mask is null) return [];
                result.Add(new SkyrimArmorAddonSlotEvidence(reference, mask.Value));
                continue;
            }
            SkyrimArmorAddonSlotEvidence[] reviewedMatches =
                loaded.ArmorAddonSlotEvidence.Where(evidence =>
                    SameReference(evidence.ArmorAddon, reference)).ToArray();
            if (reviewedMatches.Length != 1) return [];
            result.Add(reviewedMatches[0]);
        }
        return result.ToImmutable();
    }

    private SkyrimArmorAddonProductionCatalogEntry? FindWinningArmorAddon(
        FormReference reference) => loaded?.ArmorAddonCatalog.FirstOrDefault(item =>
        SameReference(item.Candidate.Reference, reference) &&
        !item.Candidate.IsDeleted &&
        !item.Candidate.IsStale);

    private SkyrimArmorAddonEditorDocument? BuildNewArmorAddonDocument(
        SkyrimArmorAddonEditorDocument source,
        FormReference owningArmorRace,
        SkyrimArmorAddonEditorIntent intent)
    {
        if (loaded is null ||
            intent is not (SkyrimArmorAddonEditorIntent.BlankNew or
                SkyrimArmorAddonEditorIntent.NewFromTemplate) ||
            !FormId.TryParse(
                NewArmorAddonTargetFormId,
                out FormId targetFormId) ||
            targetFormId.Value is 0 or > 0x00FF_FFFF ||
            !TryWorkspacePath(
                ArmorAddonOutputPlugin,
                out WorkspacePath outputPlugin) ||
            !outputPlugin.Value.EndsWith(
                ".esp",
                StringComparison.OrdinalIgnoreCase))
            return null;
        PluginName targetPlugin;
        try
        {
            targetPlugin = new PluginName(Path.GetFileName(outputPlugin.Value));
        }
        catch (ArgumentException)
        {
            return null;
        }
        EditorId editorId = BuildUniqueArmorAddonEditorId(source.EditorId);
        bool fromTemplate =
            intent == SkyrimArmorAddonEditorIntent.NewFromTemplate;
        return source with
        {
            Intent = intent,
            Mode = ArmorAddonProposalMode.New,
            EditorId = editorId,
            TargetFormId = targetFormId,
            TargetPlugin = targetPlugin,
            SeedFromSource = fromTemplate,
            MaleModel = fromTemplate ? source.MaleModel : null,
            FemaleModel = fromTemplate ? source.FemaleModel : null,
            MaleFirstPersonModel =
                fromTemplate ? source.MaleFirstPersonModel : null,
            FemaleFirstPersonModel =
                fromTemplate ? source.FemaleFirstPersonModel : null,
            SlotMask = fromTemplate ? source.SlotMask : 0U,
            Race = fromTemplate ? source.Race : owningArmorRace,
            AdditionalRaces = fromTemplate ? source.AdditionalRaces : [],
            MaleSkinTexture =
                fromTemplate ? source.MaleSkinTexture : null,
            FemaleSkinTexture =
                fromTemplate ? source.FemaleSkinTexture : null,
            MaleSkinTextureSwapList =
                fromTemplate ? source.MaleSkinTextureSwapList : null,
            FemaleSkinTextureSwapList =
                fromTemplate ? source.FemaleSkinTextureSwapList : null,
            FootstepSet = fromTemplate ? source.FootstepSet : null,
            ArtObject = fromTemplate ? source.ArtObject : null,
            MalePriority = fromTemplate ? source.MalePriority : (byte)0,
            FemalePriority = fromTemplate ? source.FemalePriority : (byte)0,
            MaleWeightSliderEnabled =
                fromTemplate && source.MaleWeightSliderEnabled,
            FemaleWeightSliderEnabled =
                fromTemplate && source.FemaleWeightSliderEnabled,
            DetectionSound =
                fromTemplate ? source.DetectionSound : (byte)0,
            WeaponAdjust = fromTemplate ? source.WeaponAdjust : 0D
        };
    }

    private EditorId BuildUniqueArmorAddonEditorId(EditorId source)
    {
        ImmutableArray<EditorId> existing =
            loaded?.ExistingArmorAddonEditorIds ?? [];
        string seed = SkyrimArmorAddonEditorRules.EditorIdPrefix + source.Value;
        string candidate = seed;
        int suffix = 2;
        while (existing.Any(item => string.Equals(
                   item.Value,
                   candidate,
                   StringComparison.OrdinalIgnoreCase)))
            candidate = $"{seed}_{suffix++}";
        return new EditorId(candidate);
    }

    private WorkspacePath BuildNestedArmorAddonProposal(FormReference reference)
    {
        if (!TryWorkspacePath(ProposalPath, out WorkspacePath outerProposal))
            throw new InvalidOperationException(
                "Enter the K-local outer Armor proposal path before deep editing an Armor-addon.");
        string directory = Path.GetDirectoryName(outerProposal.Value) ??
                           throw new InvalidOperationException(
                               "The outer Armor proposal path has no parent directory.");
        string outerName = Path.GetFileNameWithoutExtension(
            outerProposal.Value);
        string nestedName =
            $"{outerName}.{reference.Plugin.Value}.{reference.FormId.Value:X6}.armor-addon-proposal.json";
        var result = new WorkspacePath(Path.Combine(directory, nestedName));
        if (!result.IsUnder(labRoot))
            throw new InvalidOperationException(
                "The nested Armor-addon proposal path escaped the K-local workspace.");
        return result;
    }

    private static SkyrimArmorAddonEditorDocument ApplyAuthoredArmorAddon(
        SkyrimArmorAddonEditorDocument source,
        ArmorAddonProposalRequest authored)
    {
        ArmorAddonProposalPatch patch = authored.Patch;
        return source with
        {
            Intent = SkyrimArmorAddonEditorIntent.EditAuthored,
            Mode = authored.Mode,
            SourcePlugin = authored.SourcePlugin,
            SourceFormId = authored.SourceFormId,
            EditorId = patch.EditorId ?? source.EditorId,
            TargetFormId = authored.TargetFormId,
            TargetPlugin = authored.TargetPlugin,
            SeedFromSource = authored.SeedFromSource,
            MaleModel = patch.MaleModel,
            FemaleModel = patch.FemaleModel,
            MaleFirstPersonModel = patch.MaleFirstPersonModel,
            FemaleFirstPersonModel = patch.FemaleFirstPersonModel,
            SlotMask = patch.SlotMask ?? 0U,
            Race = patch.Race,
            AdditionalRaces = patch.AdditionalRaces ?? [],
            MaleSkinTexture = patch.MaleSkinTexture,
            FemaleSkinTexture = patch.FemaleSkinTexture,
            MaleSkinTextureSwapList = patch.MaleSkinTextureSwapList,
            FemaleSkinTextureSwapList = patch.FemaleSkinTextureSwapList,
            FootstepSet = patch.FootstepSet,
            ArtObject = patch.ArtObject,
            MalePriority = patch.MalePriority ?? 0,
            FemalePriority = patch.FemalePriority ?? 0,
            MaleWeightSliderEnabled = patch.MaleWeightSliderFlags is not null and not 0,
            FemaleWeightSliderEnabled = patch.FemaleWeightSliderFlags is not null and not 0,
            DetectionSound = patch.DetectionSound ?? 0,
            WeaponAdjust = patch.WeaponAdjust ?? 0D
        };
    }

    private bool CanLoad() => intake is not null && !IsBusy &&
                              TryPluginName(SourcePlugin, out _) &&
                              FormId.TryParse(SourceFormId, out _) &&
                              FormId.TryParse(NewTargetFormId, out _);

    private bool TryBuildReadRequest(
        out SkyrimArmorProductionReadRequest? request,
        out string error)
    {
        request = null;
        if (intake is null)
        {
            error = "Review a copied Skyrim workspace first.";
            return false;
        }
        if (!TryPluginName(SourcePlugin, out PluginName plugin) ||
            !FormId.TryParse(SourceFormId, out FormId source) ||
            !FormId.TryParse(NewTargetFormId, out FormId target) ||
            source.Value is 0 or > 0x00FF_FFFF ||
            target.Value is 0 or > 0x00FF_FFFF)
        {
            error = "Enter a reviewed plugin and two nonzero local 24-bit hexadecimal FormIDs.";
            return false;
        }
        request = new SkyrimArmorProductionReadRequest(
            intake,
            plugin,
            source,
            target);
        error = string.Empty;
        return true;
    }

    private bool TryBuildTransactionRequest(
        out SkyrimArmorProductionRequest? request,
        out string error)
    {
        request = null;
        if (loaded is null || authoredDocument is null)
        {
            error = "Load a reviewed ARMO source and accept the Armor editor first.";
            return false;
        }
        if (!TryWorkspacePath(ProposalPath, out WorkspacePath proposal) ||
            !TryWorkspacePath(OutputPlugin, out WorkspacePath output) ||
            !output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
        {
            error = "Enter a K-only .armor-proposal.json path and a fresh K-only .esp path.";
            return false;
        }
        SkyrimArmorProposalAdapterResult adapted =
            SkyrimArmorEditorRules.ToProposal(authoredDocument, proposal);
        if (!adapted.Accepted || adapted.Proposal is null)
        {
            error = adapted.Diagnostics.IsDefaultOrEmpty
                ? "The complete Armor document could not be adapted for Review."
                : string.Join(
                    " ",
                    adapted.Diagnostics.Select(item => item.Message));
            return false;
        }
        ImmutableArray<ArmorAddonProposalRequest> authoredAddons =
            adapted.AuthoredArmorAddons.IsDefault
                ? []
                : adapted.AuthoredArmorAddons;
        if (authoredAddons.Length > 1)
        {
            error = "One Armor production transaction currently supports at most one authored Armor-addon output.";
            return false;
        }
        ImmutableArray<SkyrimArmorAddonProductionRequest> addonOutputs = [];
        if (authoredAddons.Length == 1)
        {
            ArmorAddonProposalRequest addon = authoredAddons[0];
            PluginClosureReviewEntry? reviewedSource = loaded.Intake.Plugins
                .SingleOrDefault(item =>
                    item.Enabled &&
                    item.Exists &&
                    item.ReadSucceeded &&
                    item.SourceHash is not null &&
                    item.Path == addon.SourcePlugin);
            if (reviewedSource?.SourceHash is not { } reviewedHash)
            {
                error = "The authored Armor-addon source is not hash-bound to the reviewed copied closure.";
                return false;
            }
            if (!TryWorkspacePath(
                    ArmorAddonOutputPlugin,
                    out WorkspacePath addonOutput) ||
                !addonOutput.Value.EndsWith(
                    ".esp",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Enter one explicit fresh K-only .esp output for the authored Armor-addon.";
                return false;
            }
            addonOutputs =
            [
                new SkyrimArmorAddonProductionRequest(
                    addon,
                    reviewedHash,
                    addonOutput)
            ];
        }
        request = new SkyrimArmorProductionRequest(
            loaded,
            authoredDocument,
            proposal,
            output,
            addonOutputs);
        error = string.Empty;
        return true;
    }

    private bool TryWorkspacePath(string value, out WorkspacePath path)
    {
        try
        {
            path = new WorkspacePath(value);
            return path.IsUnder(labRoot);
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    private static bool TryPluginName(string value, out PluginName plugin)
    {
        try
        {
            plugin = new PluginName(value);
            return true;
        }
        catch (ArgumentException)
        {
            plugin = default;
            return false;
        }
    }

    private void ClearLoaded()
    {
        loaded = null;
        armorAddonReferenceCatalog = null;
        meshSearchResult = null;
        authoredDocument = null;
        InvalidateReview();
        Verdict = "Not loaded";
        RaiseLoadedState();
        RaiseDocumentState();
    }

    private void InvalidateReview()
    {
        reviewedProposal = null;
        IsReviewed = false;
        HasCompleted = false;
        ProgressPercent = 0;
        ProposalSha256 = EmptyResult;
        ArmorAddonProposalSha256 = EmptyResult;
        ArmorAddonResultPlugin = EmptyResult;
        ArmorAddonResultSha256 = EmptyResult;
        ResultPlugin = EmptyResult;
        ResultSha256 = EmptyResult;
        RaiseCommandState();
    }

    private void RaiseLoadedState()
    {
        Raise(nameof(IsLoaded));
        Raise(nameof(CanOpenEditor));
        Raise(nameof(SourceSummary));
        RaiseCommandState();
    }

    private void RaiseDocumentState()
    {
        Raise(nameof(HasAcceptedDocument));
        Raise(nameof(DocumentSummary));
        RaiseCommandState();
    }

    private CancellationTokenSource Begin(
        string verdictValue,
        string statusValue)
    {
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Verdict = verdictValue;
        Status = statusValue;
        return source;
    }

    private void Release(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellation, source)) cancellation = null;
        source.Dispose();
        IsBusy = false;
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (Diagnostic diagnostic in diagnostics)
            Diagnostics.Add($"{diagnostic.Severity}: {diagnostic.Code}: {diagnostic.Message}");
    }

    private void PresentInputError(string message)
    {
        Diagnostics.Add(message);
        Verdict = "Input required";
        Status = "Nothing was written.";
    }

    private void RaiseCommandState()
    {
        loadCommand.RaiseCanExecuteChanged();
        reviewCommand.RaiseCanExecuteChanged();
        executeCommand.RaiseCanExecuteChanged();
        cancelCommand.RaiseCanExecuteChanged();
    }

    private static bool SameReference(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value,
            right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private sealed class DelegateCommand(
        Action<object?> execute,
        Predicate<object?> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute(parameter);
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
