using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public delegate ImmutableArray<SkyrimOutfitPreviewArmor>
    SkyrimOutfitLeveledPreviewResolver(FormReference list, long seed);

public delegate SkyrimOutfitEditorItem? SkyrimOutfitChildEditor(
    SkyrimOutfitEditorItemKind kind,
    SkyrimOutfitEditorItem? existing);

public sealed class SkyrimOutfitEditorViewModel : NotifyViewModel
{
    private readonly ImmutableArray<OutfitChoiceCandidate> outfitCatalog;
    private readonly ImmutableArray<SkyrimOutfitEditorItem> itemCatalog;
    private readonly WorkspacePath dataRoot;
    private readonly WorkspacePath templatePlugin;
    private readonly FormId templateFormId;
    private readonly FormId newTargetFormId;
    private readonly WorkspacePath outputProposal;
    private readonly SkyrimOutfitLeveledPreviewResolver rerollResolver;
    private readonly SkyrimOutfitChildEditor childEditor;
    private readonly string authorityNotice =
        "Preview is advisory only. Plugin write, independent readback, runtime, and visual authority remain false.";
    private SkyrimOutfitEditorDraft? originalDraft;
    private SkyrimOutfitEditorDraft? workingDraft;

    public SkyrimOutfitEditorViewModel(
        ImmutableArray<OutfitChoiceCandidate> outfits,
        ImmutableArray<SkyrimOutfitEditorItem> items,
        WorkspacePath dataRoot,
        WorkspacePath templatePlugin,
        FormId templateFormId,
        FormId newTargetFormId,
        WorkspacePath outputProposal,
        SkyrimOutfitLeveledPreviewResolver rerollResolver,
        SkyrimOutfitChildEditor childEditor)
    {
        outfitCatalog = outfits.IsDefault ? [] : outfits;
        itemCatalog = items.IsDefault ? [] : items;
        this.dataRoot = dataRoot;
        this.templatePlugin = templatePlugin;
        this.templateFormId = templateFormId;
        this.newTargetFormId = newTargetFormId;
        this.outputProposal = outputProposal;
        this.rerollResolver = rerollResolver ??
            throw new ArgumentNullException(nameof(rerollResolver));
        this.childEditor = childEditor ??
            throw new ArgumentNullException(nameof(childEditor));
        RefreshBrowseRows();
        RefreshAvailableItems();
        SelectedBrowseRow = BrowseRows.FirstOrDefault();
    }

    public ObservableCollection<SkyrimOutfitBrowseRowViewModel> BrowseRows { get; } = [];
    public ObservableCollection<SkyrimOutfitAvailableItemViewModel> AvailableItems { get; } = [];
    public ObservableCollection<SkyrimOutfitDraftItemViewModel> DraftItems { get; } = [];
    public SkyrimOutfitEditorCommitResult? AcceptedResult { get; private set; }
    public bool IsAccepted { get; private set; }
    public bool IsAuthoring => workingDraft is not null;
    public bool IsOverride => workingDraft?.Mode == OutfitProposalMode.Override;
    public bool HasDraft => workingDraft is not null;
    public bool HasDraftItems => workingDraft is { Items.Length: > 0 };
    public string ModeTitle => workingDraft switch
    {
        { Mode: OutfitProposalMode.Override } => "Override existing outfit",
        not null => "Create new outfit",
        _ => "Choose an outfit"
    };
    public string ModeDescription => workingDraft switch
    {
        { Mode: OutfitProposalMode.Override } draft =>
            $"Keeps {draft.SourceFormId} and its original EditorID; Save returns one override proposal.",
        not null =>
            $"Creates local {newTargetFormId}; every authored entry is retained in equip order.",
        _ => "Use the record default, choose no outfit, select an existing OTFT, or begin authoring."
    };
    public string DraftSummary => workingDraft is null
        ? "No authoring transaction is open."
        : $"{workingDraft.Items.Length} saved item(s) · " +
          $"{SkyrimOutfitEditorRules.ResolvePreview(workingDraft).Winners.Length} preview winner(s)";
    public string PreviewSummary
    {
        get
        {
            if (workingDraft is null)
                return SelectedBrowseRow?.PreviewSummary ?? "Select an outfit to inspect its qualified entries.";
            SkyrimOutfitPreviewResolution resolution =
                SkyrimOutfitEditorRules.ResolvePreview(workingDraft);
            IEnumerable<SkyrimOutfitPreviewArmor> renderItems = PreviewSelectedPiece &&
                SelectedDraftItem is { } selected
                ? PreviewFor(selected.Item)
                : resolution.RenderItems;
            string body = string.Join(Environment.NewLine,
                renderItems.Select(item => $"{item.Reference} · slots 0x{item.SlotMask:X8}"));
            return string.IsNullOrEmpty(body)
                ? "The current realization renders no terminal ARMO records."
                : body;
        }
    }
    public string AuthorityNotice => authorityNotice;

    private string outfitFilter = string.Empty;
    public string OutfitFilter
    {
        get => outfitFilter;
        set
        {
            if (!Set(ref outfitFilter, value ?? string.Empty)) return;
            RefreshBrowseRows();
        }
    }

    private string itemFilter = string.Empty;
    public string ItemFilter
    {
        get => itemFilter;
        set
        {
            if (!Set(ref itemFilter, value ?? string.Empty)) return;
            RefreshAvailableItems();
        }
    }

    private string newEditorId = "NpcManager_NewOutfit";
    public string NewEditorId
    {
        get => newEditorId;
        set => Set(ref newEditorId, value ?? string.Empty);
    }

    private string rerollSeed = "0";
    public string RerollSeed
    {
        get => rerollSeed;
        set => Set(ref rerollSeed, value ?? string.Empty);
    }

    private bool previewSelectedPiece;
    public bool PreviewSelectedPiece
    {
        get => previewSelectedPiece;
        set
        {
            if (!Set(ref previewSelectedPiece, value)) return;
            Raise(nameof(PreviewSummary));
        }
    }

    private SkyrimOutfitBrowseRowViewModel? selectedBrowseRow;
    public SkyrimOutfitBrowseRowViewModel? SelectedBrowseRow
    {
        get => selectedBrowseRow;
        set
        {
            if (!Set(ref selectedBrowseRow, value)) return;
            ClearMessagesAndAcceptance();
            Raise(nameof(PreviewSummary));
        }
    }

    private SkyrimOutfitAvailableItemViewModel? selectedAvailableItem;
    public SkyrimOutfitAvailableItemViewModel? SelectedAvailableItem
    {
        get => selectedAvailableItem;
        set => Set(ref selectedAvailableItem, value);
    }

    private SkyrimOutfitDraftItemViewModel? selectedDraftItem;
    public SkyrimOutfitDraftItemViewModel? SelectedDraftItem
    {
        get => selectedDraftItem;
        set
        {
            if (!Set(ref selectedDraftItem, value)) return;
            Raise(nameof(PreviewSummary));
        }
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    private string statusMessage = string.Empty;
    public string StatusMessage
    {
        get => statusMessage;
        private set => Set(ref statusMessage, value);
    }

    public bool TryBeginNew()
    {
        ClearMessagesAndAcceptance();
        EditorId editorId;
        try { editorId = new EditorId(NewEditorId.Trim()); }
        catch (ArgumentException exception)
        {
            ValidationMessage = exception.Message;
            return false;
        }
        SkyrimOutfitDraftEditResult result = SkyrimOutfitEditorRules.BeginNew(
            templatePlugin, templateFormId, editorId, newTargetFormId);
        if (!InstallDraft(result)) return false;
        originalDraft = workingDraft;
        StatusMessage = "New outfit transaction started. Nothing is written until outer Save.";
        return true;
    }

    public bool TryBeginOverride()
    {
        ClearMessagesAndAcceptance();
        OutfitChoiceCandidate? candidate = SelectedBrowseRow?.Candidate;
        if (candidate is null)
        {
            ValidationMessage = "Select a concrete existing outfit to override.";
            return false;
        }
        WorkspacePath source;
        try
        {
            source = new WorkspacePath(Path.Combine(
                dataRoot.Value, candidate.Plugin.Value));
        }
        catch (ArgumentException exception)
        {
            ValidationMessage = exception.Message;
            return false;
        }
        SkyrimOutfitDraftEditResult result = SkyrimOutfitEditorRules.BeginOverride(
            candidate, source, itemCatalog);
        if (!InstallDraft(result)) return false;
        originalDraft = workingDraft;
        StatusMessage = "Override transaction started. The source identity remains locked.";
        return true;
    }

    public bool TryAddSelectedItem()
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft) ||
            SelectedAvailableItem is not { } selected) return false;
        return InstallDraft(SkyrimOutfitEditorRules.Add(draft, selected.Item));
    }

    public bool TryRemoveSelectedItem()
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft) ||
            SelectedDraftItem is not { } selected) return false;
        return InstallDraft(SkyrimOutfitEditorRules.Remove(draft, selected.Index));
    }

    public bool TryMoveSelectedItem(int delta)
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft) ||
            SelectedDraftItem is not { } selected) return false;
        int destination = selected.Index + delta;
        if (!InstallDraft(SkyrimOutfitEditorRules.Move(draft, selected.Index, delta)))
            return false;
        SelectedDraftItem = DraftItems.FirstOrDefault(item => item.Index == destination);
        return true;
    }

    public bool TryRerollSelected()
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft) ||
            SelectedDraftItem is not { } selected) return false;
        if (!long.TryParse(RerollSeed.Trim(), out long seed))
        {
            ValidationMessage = "Reroll seed must be a signed 64-bit integer.";
            return false;
        }
        try
        {
            ImmutableArray<SkyrimOutfitPreviewArmor> realization =
                rerollResolver(selected.Item.Reference, seed);
            if (!InstallDraft(SkyrimOutfitEditorRules.Reroll(
                    draft, selected.Index, seed, realization))) return false;
            SelectedDraftItem = DraftItems.FirstOrDefault(item =>
                item.Index == selected.Index);
            StatusMessage = $"LVLI preview realization replaced using seed {seed}.";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ValidationMessage = $"Leveled-list preview failed: {exception.Message}";
            return false;
        }
    }

    public bool TryApplyChildEdit(SkyrimOutfitEditorItem replacement)
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft) ||
            SelectedDraftItem is not { } selected) return false;
        return InstallDraft(SkyrimOutfitEditorRules.Replace(
            draft, selected.Index, replacement));
    }

    public bool TryCreateChildItem(SkyrimOutfitEditorItemKind kind)
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft)) return false;
        if (!Enum.IsDefined(kind))
        {
            ValidationMessage = "Choose ARMO or LVLI authoring.";
            return false;
        }
        SkyrimOutfitEditorItem? basis = null;
        if (kind == SkyrimOutfitEditorItemKind.Armor)
        {
            if (SelectedAvailableItem?.Item is not
                {
                    Kind: SkyrimOutfitEditorItemKind.Armor
                } selectedArmor)
            {
                ValidationMessage =
                    "Select one reviewed ARMO explicitly before opening New ARMO.";
                return false;
            }
            basis = selectedArmor;
        }
        try
        {
            SkyrimOutfitEditorItem? created = childEditor(kind, basis);
            if (created is null)
            {
                StatusMessage = "Child editor cancelled; the outfit transaction is unchanged.";
                return false;
            }
            if (created.Kind != kind)
            {
                ValidationMessage = "The child editor returned the wrong record type.";
                return false;
            }
            if (!InstallDraft(SkyrimOutfitEditorRules.Add(draft, created))) return false;
            SelectedDraftItem = DraftItems.FirstOrDefault(item =>
                item.Item.Reference == created.Reference);
            StatusMessage = $"{created.Signature} draft added to the outer transaction.";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ValidationMessage = $"Child editor failed: {exception.Message}";
            return false;
        }
    }

    public bool TryEditSelectedChildItem()
    {
        if (!RequireDraft(out SkyrimOutfitEditorDraft draft) ||
            SelectedDraftItem is not { } selected) return false;
        try
        {
            SkyrimOutfitEditorItem? replacement = childEditor(
                selected.Item.Kind, selected.Item);
            if (replacement is null)
            {
                StatusMessage = "Child editor cancelled; the outfit transaction is unchanged.";
                return false;
            }
            if (replacement.Kind != selected.Item.Kind)
            {
                ValidationMessage = "The child editor changed the record type.";
                return false;
            }
            if (!InstallDraft(SkyrimOutfitEditorRules.Replace(
                    draft, selected.Index, replacement))) return false;
            SelectedDraftItem = DraftItems.FirstOrDefault(item =>
                item.Index == selected.Index);
            StatusMessage = $"{replacement.Signature} draft updated inside the outer transaction.";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ValidationMessage = $"Child editor failed: {exception.Message}";
            return false;
        }
    }

    public void Reset()
    {
        ClearMessagesAndAcceptance();
        if (originalDraft is null)
        {
            ValidationMessage = "No authoring transaction is open.";
            return;
        }
        workingDraft = SkyrimOutfitEditorRules.Reset(originalDraft);
        RefreshDraftItems();
        StatusMessage = "The exact opening authoring snapshot was restored.";
    }

    public bool TryAccept()
    {
        ClearMessagesAndAcceptance();
        SkyrimOutfitEditorCommitResult result;
        if (workingDraft is not null)
        {
            result = SkyrimOutfitEditorRules.Save(
                GameEdition.SkyrimSpecialEdition, workingDraft, outputProposal);
        }
        else
        {
            result = SelectedBrowseRow?.Kind switch
            {
                SkyrimOutfitChoiceKind.RecordDefault =>
                    SkyrimOutfitEditorRules.AcceptRecordDefault(),
                SkyrimOutfitChoiceKind.None =>
                    SkyrimOutfitEditorRules.AcceptNoOutfit(),
                SkyrimOutfitChoiceKind.Existing when SelectedBrowseRow.Candidate is { } candidate =>
                    SkyrimOutfitEditorRules.UseExisting(candidate),
                _ => new SkyrimOutfitEditorCommitResult(false, null, null,
                    [new Diagnostic("outfit-editor-selection-required",
                        DiagnosticSeverity.Error,
                        "Select an outfit choice or begin authoring.")])
            };
        }
        if (!result.Accepted)
        {
            ValidationMessage = FirstError(result.Diagnostics);
            return false;
        }
        AcceptedResult = result;
        IsAccepted = true;
        StatusMessage = result.Proposal is null
            ? "The typed outfit choice is ready for the caller."
            : "The complete ordered outfit proposal is ready for the existing writer pipeline.";
        return true;
    }

    public void Cancel()
    {
        SkyrimOutfitEditorRules.Cancel();
        AcceptedResult = null;
        IsAccepted = false;
        workingDraft = null;
        originalDraft = null;
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
        RefreshDraftItems();
    }

    private bool InstallDraft(SkyrimOutfitDraftEditResult result)
    {
        ClearMessagesAndAcceptance();
        if (!result.Accepted || result.Draft is null)
        {
            ValidationMessage = FirstError(result.Diagnostics);
            return false;
        }
        workingDraft = result.Draft;
        RefreshDraftItems();
        return true;
    }

    private bool RequireDraft(out SkyrimOutfitEditorDraft draft)
    {
        ClearMessagesAndAcceptance();
        if (workingDraft is not null)
        {
            draft = workingDraft;
            return true;
        }
        draft = null!;
        ValidationMessage = "Begin a new outfit or override before editing its items.";
        return false;
    }

    private void RefreshBrowseRows()
    {
        FormReference? retained = SelectedBrowseRow?.Candidate is { } selected
            ? new FormReference(selected.Provenance.SourcePlugin, selected.FormId)
            : null;
        SkyrimOutfitChoiceKind? retainedKind = SelectedBrowseRow?.Kind;
        BrowseRows.Clear();
        BrowseRows.Add(SkyrimOutfitBrowseRowViewModel.RecordDefault());
        BrowseRows.Add(SkyrimOutfitBrowseRowViewModel.NoOutfit());
        foreach (OutfitChoiceCandidate candidate in outfitCatalog
                     .Where(item => SkyrimOutfitEditorRules.Matches(item, OutfitFilter))
                     .OrderBy(item => item.Name ?? item.EditorId ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase))
            BrowseRows.Add(SkyrimOutfitBrowseRowViewModel.Existing(candidate));
        SelectedBrowseRow = retained is { } reference
            ? BrowseRows.FirstOrDefault(row => row.Candidate is { } candidate &&
                candidate.Provenance.SourcePlugin == reference.Plugin &&
                candidate.FormId == reference.FormId)
            : BrowseRows.FirstOrDefault(row => row.Kind == retainedKind) ??
              BrowseRows.FirstOrDefault();
    }

    private void RefreshAvailableItems()
    {
        FormReference? retained = SelectedAvailableItem?.Item.Reference;
        string value = ItemFilter.Trim();
        AvailableItems.Clear();
        foreach (SkyrimOutfitEditorItem item in itemCatalog
                     .Where(item => value.Length == 0 ||
                         item.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                         item.Reference.ToString().Contains(value, StringComparison.OrdinalIgnoreCase) ||
                         item.Signature.Value.Contains(value, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Kind)
                     .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
            AvailableItems.Add(new SkyrimOutfitAvailableItemViewModel(item));
        SelectedAvailableItem = retained is { } reference
            ? AvailableItems.FirstOrDefault(row => row.Item.Reference == reference)
            : AvailableItems.FirstOrDefault();
    }

    private void RefreshDraftItems()
    {
        FormReference? retained = SelectedDraftItem?.Item.Reference;
        DraftItems.Clear();
        if (workingDraft is not null)
        {
            SkyrimOutfitPreviewResolution resolution =
                SkyrimOutfitEditorRules.ResolvePreview(workingDraft);
            var winnerReferences = resolution.Winners.Select(item => item.Reference)
                .ToImmutableHashSet();
            for (int index = 0; index < workingDraft.Items.Length; index++)
                DraftItems.Add(new SkyrimOutfitDraftItemViewModel(
                    index,
                    workingDraft.Items[index],
                    winnerReferences.Contains(workingDraft.Items[index].Reference)));
        }
        SelectedDraftItem = retained is { } reference
            ? DraftItems.FirstOrDefault(row => row.Item.Reference == reference)
            : DraftItems.FirstOrDefault();
        Raise(nameof(IsAuthoring));
        Raise(nameof(IsOverride));
        Raise(nameof(HasDraft));
        Raise(nameof(HasDraftItems));
        Raise(nameof(ModeTitle));
        Raise(nameof(ModeDescription));
        Raise(nameof(DraftSummary));
        Raise(nameof(PreviewSummary));
    }

    private void ClearMessagesAndAcceptance()
    {
        AcceptedResult = null;
        IsAccepted = false;
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    private static ImmutableArray<SkyrimOutfitPreviewArmor> PreviewFor(
        SkyrimOutfitEditorItem item) =>
        item.Kind == SkyrimOutfitEditorItemKind.Armor
            ? [new SkyrimOutfitPreviewArmor(item.Reference, item.EffectiveSlotMask)]
            : item.PreviewRealization.IsDefault ? [] : item.PreviewRealization;

    private static string FirstError(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ??
        "The outfit operation was refused.";
}

public sealed record SkyrimOutfitBrowseRowViewModel(
    SkyrimOutfitChoiceKind Kind,
    string Title,
    string Subtitle,
    OutfitChoiceCandidate? Candidate)
{
    public string PreviewSummary => Candidate is null
        ? Subtitle
        : Candidate.ItemReferences.IsDefaultOrEmpty
            ? "This outfit has no qualified item references available for preview."
            : string.Join(Environment.NewLine,
                Candidate.ItemReferences.Select(reference => reference.ToString()));

    public static SkyrimOutfitBrowseRowViewModel RecordDefault() =>
        new(SkyrimOutfitChoiceKind.RecordDefault, "Record default",
            "Clear the explicit outfit choice and preserve the NPC record default.", null);

    public static SkyrimOutfitBrowseRowViewModel NoOutfit() =>
        new(SkyrimOutfitChoiceKind.None, "No outfit",
            "Set an explicit null outfit choice.", null);

    public static SkyrimOutfitBrowseRowViewModel Existing(
        OutfitChoiceCandidate candidate) =>
        new(SkyrimOutfitChoiceKind.Existing,
            candidate.Name ?? candidate.EditorId ?? candidate.FormId.ToString(),
            $"{candidate.Provenance.SourcePlugin.Value} · {candidate.FormId} · " +
            $"{candidate.ItemReferences.Length} item(s)" +
            (candidate.IsDeleted ? " · deleted" : string.Empty),
            candidate);
}

public sealed record SkyrimOutfitAvailableItemViewModel(
    SkyrimOutfitEditorItem Item)
{
    public string Title => Item.DisplayName;
    public string Subtitle =>
        $"{Item.Signature} · {Item.Reference} · slots 0x{Item.EffectiveSlotMask:X8}";
}

public sealed record SkyrimOutfitDraftItemViewModel(
    int Index,
    SkyrimOutfitEditorItem Item,
    bool IsPreviewWinner)
{
    public int EquipOrder => Index + 1;
    public string Title => Item.DisplayName;
    public string QualifiedReference => Item.Reference.ToString();
    public string Signature => Item.Signature.Value;
    public string SlotMask => $"0x{Item.EffectiveSlotMask:X8}";
    public string PreviewStatus => IsPreviewWinner
        ? "Preview winner"
        : "Preview hidden by a later slot conflict; retained on Save";
    public string Realization => Item.Kind == SkyrimOutfitEditorItemKind.LeveledList
        ? Item.RealizationSeed is { } seed
            ? $"seed {seed} · {Item.PreviewRealization.Length} terminal ARMO"
            : "not yet rolled"
        : "direct ARMO";
}
