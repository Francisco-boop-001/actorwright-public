using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimOutfitChoiceKind
{
    RecordDefault,
    None,
    Existing,
    Authored
}

public sealed record SkyrimOutfitChoice(
    SkyrimOutfitChoiceKind Kind,
    FormReference? Outfit = null);

public enum SkyrimOutfitEditorItemKind
{
    Armor,
    LeveledList
}

/// <summary>One terminal ARMO used only by the advisory preview.</summary>
public sealed record SkyrimOutfitPreviewArmor(
    FormReference Reference,
    uint SlotMask);

/// <summary>
/// One authored OTFT entry. A leveled list remains an LVLI in the saved item
/// sequence; its seed-bound terminal armor realization is preview-only.
/// </summary>
public sealed record SkyrimOutfitEditorItem(
    FormReference Reference,
    SkyrimOutfitEditorItemKind Kind,
    string DisplayName,
    uint EffectiveSlotMask,
    long? RealizationSeed = null,
    ImmutableArray<SkyrimOutfitPreviewArmor> PreviewRealization = default,
    SkyrimLeveledListEditorDocument? AuthoredLeveledList = null,
    SkyrimArmorEditorDocument? AuthoredArmor = null)
{
    public RecordSignature Signature => new(
        Kind == SkyrimOutfitEditorItemKind.Armor ? "ARMO" : "LVLI");
}

public sealed record SkyrimOutfitEditorDraft(
    OutfitProposalMode Mode,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    EditorId? EditorId,
    FormId? TargetFormId,
    ImmutableArray<SkyrimOutfitEditorItem> Items);

public sealed record SkyrimOutfitPreviewResolution(
    ImmutableArray<SkyrimOutfitEditorItem> Winners,
    ImmutableArray<SkyrimOutfitEditorItem> Losers,
    ImmutableArray<SkyrimOutfitPreviewArmor> RenderItems);

public sealed record SkyrimOutfitDraftEditResult(
    bool Accepted,
    SkyrimOutfitEditorDraft? Draft,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimOutfitEditorCommitResult(
    bool Accepted,
    SkyrimOutfitChoice? Choice,
    OutfitProposalRequest? Proposal,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<SkyrimLeveledListEditorDocument> AuthoredLeveledLists = default,
    ImmutableArray<SkyrimArmorEditorDocument> AuthoredArmors = default);

/// <summary>
/// Pure immutable operations for the Skyrim outfit browser/editor. They never
/// read or write plugins, resolve leveled lists, render assets, or mutate a
/// caller-owned collection.
/// </summary>
public static class SkyrimOutfitEditorRules
{
    public static SkyrimOutfitChoice RecordDefault() =>
        new(SkyrimOutfitChoiceKind.RecordDefault);

    public static SkyrimOutfitChoice NoOutfit() =>
        new(SkyrimOutfitChoiceKind.None);

    public static SkyrimOutfitEditorCommitResult AcceptRecordDefault() =>
        new(true, RecordDefault(), null, []);

    public static SkyrimOutfitEditorCommitResult AcceptNoOutfit() =>
        new(true, NoOutfit(), null, []);

    public static SkyrimOutfitEditorCommitResult UseExisting(
        OutfitChoiceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        FormReference reference = new(candidate.Provenance.SourcePlugin, candidate.FormId);
        ValidateReference(reference, "outfit", diagnostics);
        if (candidate.IsDeleted)
            diagnostics.Add(Error("outfit-editor-existing-deleted",
                "A deleted winning outfit record cannot be selected."));
        if (HasErrors(diagnostics)) return RefusedCommit(diagnostics);
        return new(true, new SkyrimOutfitChoice(
            SkyrimOutfitChoiceKind.Existing, reference), null, diagnostics.ToImmutable());
    }

    public static SkyrimOutfitDraftEditResult BeginNew(
        WorkspacePath sourcePlugin,
        FormId sourceTemplateFormId,
        EditorId editorId,
        FormId targetFormId) =>
        ValidateDraft(new SkyrimOutfitEditorDraft(
            OutfitProposalMode.New,
            sourcePlugin,
            sourceTemplateFormId,
            editorId,
            targetFormId,
            []),
            requireItems: false);

    public static SkyrimOutfitDraftEditResult BeginOverride(
        OutfitChoiceCandidate candidate,
        WorkspacePath providerPluginPath,
        ImmutableArray<SkyrimOutfitEditorItem> itemCatalog)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (candidate.IsDeleted)
            diagnostics.Add(Error("outfit-editor-override-deleted",
                "A deleted winning outfit record cannot be overridden."));
        ImmutableArray<FormReference> references = candidate.ItemReferences.IsDefault
            ? []
            : candidate.ItemReferences;
        if (references.IsDefaultOrEmpty && candidate.Items.Length > 0)
            diagnostics.Add(Error("outfit-editor-override-items-unqualified",
                "The selected outfit exposes local FormIDs without provider identity."));

        var catalog = itemCatalog.IsDefault
            ? ImmutableDictionary<FormReference, SkyrimOutfitEditorItem>.Empty
            : itemCatalog
                .GroupBy(item => item.Reference)
                .Where(group => group.Count() == 1)
                .ToImmutableDictionary(group => group.Key, group => group.Single());
        var items = ImmutableArray.CreateBuilder<SkyrimOutfitEditorItem>();
        foreach (FormReference reference in references)
        {
            if (!catalog.TryGetValue(reference, out SkyrimOutfitEditorItem? item))
            {
                diagnostics.Add(Error("outfit-editor-override-item-unresolved",
                    $"Outfit item {reference} is missing from the typed ARMO/LVLI catalog."));
                continue;
            }
            items.Add(item);
        }
        if (HasErrors(diagnostics)) return RefusedDraft(diagnostics);

        return ValidateDraft(new SkyrimOutfitEditorDraft(
            OutfitProposalMode.Override,
            providerPluginPath,
            candidate.FormId,
            null,
            null,
            items.ToImmutable()),
            requireItems: true);
    }

    public static SkyrimOutfitDraftEditResult Add(
        SkyrimOutfitEditorDraft draft,
        SkyrimOutfitEditorItem item)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateItem(item, diagnostics);
        if (!draft.Items.IsDefault && draft.Items.Any(existing =>
                SameReference(existing.Reference, item.Reference)))
            diagnostics.Add(Error("outfit-editor-item-duplicate",
                $"Outfit item {item.Reference} is already in the authored sequence."));
        if (HasErrors(diagnostics)) return RefusedDraft(diagnostics);
        ImmutableArray<SkyrimOutfitEditorItem> items = draft.Items.IsDefault
            ? [item]
            : draft.Items.Add(item);
        return ValidateDraft(draft with { Items = items }, requireItems: true);
    }

    public static SkyrimOutfitDraftEditResult Remove(
        SkyrimOutfitEditorDraft draft,
        int index)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (draft.Items.IsDefault || index < 0 || index >= draft.Items.Length)
        {
            diagnostics.Add(Error("outfit-editor-index-invalid",
                "The selected outfit position does not exist."));
            return RefusedDraft(diagnostics);
        }
        return ValidateDraft(draft with { Items = draft.Items.RemoveAt(index) },
            requireItems: false);
    }

    public static SkyrimOutfitDraftEditResult Replace(
        SkyrimOutfitEditorDraft draft,
        int index,
        SkyrimOutfitEditorItem replacement)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (draft.Items.IsDefault || index < 0 || index >= draft.Items.Length)
            diagnostics.Add(Error("outfit-editor-index-invalid",
                "The selected outfit position does not exist."));
        ValidateItem(replacement, diagnostics);
        if (!draft.Items.IsDefault && draft.Items.Where((_, current) => current != index)
                .Any(item => SameReference(item.Reference, replacement.Reference)))
            diagnostics.Add(Error("outfit-editor-item-duplicate",
                $"Outfit item {replacement.Reference} is already in the authored sequence."));
        if (HasErrors(diagnostics)) return RefusedDraft(diagnostics);
        return ValidateDraft(draft with
        {
            Items = draft.Items.SetItem(index, replacement)
        }, requireItems: true);
    }

    public static SkyrimOutfitDraftEditResult Move(
        SkyrimOutfitEditorDraft draft,
        int index,
        int delta)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        int destination = index + delta;
        if (draft.Items.IsDefault || index < 0 || index >= draft.Items.Length ||
            delta is not (-1 or 1) || destination < 0 || destination >= draft.Items.Length)
        {
            diagnostics.Add(Error("outfit-editor-move-invalid",
                "The selected outfit item cannot move in that direction."));
            return RefusedDraft(diagnostics);
        }
        SkyrimOutfitEditorItem item = draft.Items[index];
        ImmutableArray<SkyrimOutfitEditorItem> reordered = draft.Items
            .RemoveAt(index)
            .Insert(destination, item);
        return ValidateDraft(draft with { Items = reordered }, requireItems: true);
    }

    public static SkyrimOutfitDraftEditResult Reroll(
        SkyrimOutfitEditorDraft draft,
        int index,
        long seed,
        ImmutableArray<SkyrimOutfitPreviewArmor> realization)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (draft.Items.IsDefault || index < 0 || index >= draft.Items.Length)
        {
            diagnostics.Add(Error("outfit-editor-reroll-index-invalid",
                "The selected leveled-list position does not exist."));
            return RefusedDraft(diagnostics);
        }
        SkyrimOutfitEditorItem item = draft.Items[index];
        if (item.Kind != SkyrimOutfitEditorItemKind.LeveledList)
            diagnostics.Add(Error("outfit-editor-reroll-type-invalid",
                "Only an LVLI outfit item can be rerolled."));
        ValidateRealization(realization, diagnostics);
        if (HasErrors(diagnostics)) return RefusedDraft(diagnostics);

        uint effectiveMask = realization.Aggregate(
            0U, (mask, armor) => mask | armor.SlotMask);
        SkyrimOutfitEditorItem rerolled = item with
        {
            EffectiveSlotMask = effectiveMask,
            RealizationSeed = seed,
            PreviewRealization = realization
        };
        return ValidateDraft(draft with
        {
            Items = draft.Items.SetItem(index, rerolled)
        }, requireItems: true);
    }

    public static SkyrimOutfitEditorDraft Reset(
        SkyrimOutfitEditorDraft original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original;
    }

    public static SkyrimOutfitPreviewResolution ResolvePreview(
        SkyrimOutfitEditorDraft draft)
    {
        SkyrimOutfitDraftEditResult validation = ValidateDraft(
            draft, requireItems: false);
        if (!validation.Accepted)
            return new([], draft.Items.IsDefault ? [] : draft.Items, []);

        var winnerIndexes = new HashSet<int>();
        uint occupied = 0;
        for (int index = draft.Items.Length - 1; index >= 0; index--)
        {
            uint mask = draft.Items[index].EffectiveSlotMask;
            if (mask != 0 && (occupied & mask) != 0) continue;
            winnerIndexes.Add(index);
            occupied |= mask;
        }

        ImmutableArray<SkyrimOutfitEditorItem> winners = draft.Items
            .Where((_, index) => winnerIndexes.Contains(index)).ToImmutableArray();
        ImmutableArray<SkyrimOutfitEditorItem> losers = draft.Items
            .Where((_, index) => !winnerIndexes.Contains(index)).ToImmutableArray();
        ImmutableArray<SkyrimOutfitPreviewArmor> renderItems = winners
            .SelectMany(PreviewArmors)
            .ToImmutableArray();
        return new(winners, losers, renderItems);
    }

    public static SkyrimOutfitEditorCommitResult Save(
        GameEdition edition,
        SkyrimOutfitEditorDraft draft,
        WorkspacePath outputProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("outfit-editor-edition-invalid",
                "The Skyrim outfit editor accepts only Skyrim Special Edition."));
        SkyrimOutfitDraftEditResult validation = ValidateDraft(
            draft, requireItems: true);
        diagnostics.AddRange(validation.Diagnostics);
        if (!outputProposal.Value.EndsWith(
                ".outfit-proposal.json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("outfit-editor-output-extension",
                "The outfit proposal must use the .outfit-proposal.json extension."));
        if (HasErrors(diagnostics)) return RefusedCommit(diagnostics);

        var request = new OutfitProposalRequest(
            edition,
            draft.SourcePlugin,
            draft.SourceFormId,
            draft.Mode,
            draft.Mode == OutfitProposalMode.New ? draft.EditorId : null,
            draft.Items.Select(item => item.Reference).ToImmutableArray(),
            outputProposal,
            draft.Mode == OutfitProposalMode.New ? draft.TargetFormId : null);
        ImmutableArray<SkyrimLeveledListEditorDocument> authoredLeveledLists =
            draft.Items
                .Where(item => item.AuthoredLeveledList is not null)
                .Select(item => item.AuthoredLeveledList!)
                .ToImmutableArray();
        ImmutableArray<SkyrimArmorEditorDocument> authoredArmors = draft.Items
            .Where(item => item.AuthoredArmor is not null)
            .Select(item => item.AuthoredArmor!)
            .ToImmutableArray();
        return new(
            true,
            new SkyrimOutfitChoice(SkyrimOutfitChoiceKind.Authored),
            request,
            diagnostics.ToImmutable(),
            authoredLeveledLists,
            authoredArmors);
    }

    public static SkyrimOutfitEditorCommitResult Cancel() =>
        new(false, null, null, []);

    public static bool Matches(
        OutfitChoiceCandidate candidate,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        string value = search.Trim();
        return (candidate.Name?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (candidate.EditorId?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
               candidate.Plugin.Value.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               candidate.FormId.ToString().Contains(value, StringComparison.OrdinalIgnoreCase) ||
               (!candidate.ItemReferences.IsDefault && candidate.ItemReferences.Any(item =>
                   item.ToString().Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static SkyrimOutfitDraftEditResult ValidateDraft(
        SkyrimOutfitEditorDraft? draft,
        bool requireItems)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (draft is null)
        {
            diagnostics.Add(Error("outfit-editor-draft-missing",
                "The outfit working draft is missing."));
            return RefusedDraft(diagnostics);
        }
        if (draft.Mode is not (OutfitProposalMode.New or OutfitProposalMode.Override))
            diagnostics.Add(Error("outfit-editor-mode-invalid",
                "Outfit authoring mode must be new or override."));
        if (draft.SourceFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("outfit-editor-source-form-invalid",
                "The source OTFT must use a nonzero plugin-local 24-bit FormID."));
        if (draft.Mode == OutfitProposalMode.New)
        {
            if (draft.EditorId is null)
                diagnostics.Add(Error("outfit-editor-editor-id-required",
                    "A new outfit requires an EditorID."));
            if (draft.TargetFormId is not { Value: > 0 and <= 0x00FF_FFFF })
                diagnostics.Add(Error("outfit-editor-target-form-required",
                    "A new outfit requires a nonzero plugin-local 24-bit target FormID."));
        }
        else if (draft.EditorId is not null || draft.TargetFormId is not null)
            diagnostics.Add(Error("outfit-editor-override-identity-invalid",
                "An override retains the source OTFT FormID and EditorID."));

        if (draft.Items.IsDefault || requireItems && draft.Items.IsEmpty)
            diagnostics.Add(Error("outfit-editor-items-required",
                "Add at least one ARMO or LVLI item to the outfit."));
        else
        {
            foreach (SkyrimOutfitEditorItem item in draft.Items)
                ValidateItem(item, diagnostics);
            if (draft.Items.Select(item => item.Reference.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                draft.Items.Length)
                diagnostics.Add(Error("outfit-editor-item-duplicate",
                    "Each qualified outfit item may appear at most once."));
            string[] authoredEditorIds = draft.Items
                .Where(item => item.AuthoredLeveledList is not null)
                .Select(item => item.AuthoredLeveledList!.EditorId.Value)
                .ToArray();
            if (authoredEditorIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                authoredEditorIds.Length)
                diagnostics.Add(Error("outfit-editor-lvli-editor-id-duplicate",
                    "Each authored leveled-list EditorID may appear at most once in the outfit transaction."));
            string[] authoredArmorEditorIds = draft.Items
                .Where(item => item.AuthoredArmor is not null)
                .Select(item => item.AuthoredArmor!.EditorId.Value)
                .ToArray();
            if (authoredArmorEditorIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                authoredArmorEditorIds.Length)
                diagnostics.Add(Error("outfit-editor-armor-editor-id-duplicate",
                    "Each authored armor EditorID may appear at most once in the outfit transaction."));
        }
        return HasErrors(diagnostics)
            ? RefusedDraft(diagnostics)
            : new(true, draft, diagnostics.ToImmutable());
    }

    private static void ValidateItem(
        SkyrimOutfitEditorItem? item,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (item is null)
        {
            diagnostics.Add(Error("outfit-editor-item-missing",
                "An outfit item is missing."));
            return;
        }
        ValidateReference(item.Reference, "item", diagnostics);
        if (!Enum.IsDefined(item.Kind))
            diagnostics.Add(Error("outfit-editor-item-kind-invalid",
                "An outfit item must be ARMO or LVLI."));
        if (string.IsNullOrWhiteSpace(item.DisplayName) ||
            item.DisplayName.Length > 512 || item.DisplayName.Any(char.IsControl))
            diagnostics.Add(Error("outfit-editor-item-name-invalid",
                "Outfit item labels must contain 1-512 visible characters."));
        if (item.Kind == SkyrimOutfitEditorItemKind.Armor &&
            (item.RealizationSeed is not null || !item.PreviewRealization.IsDefaultOrEmpty))
            diagnostics.Add(Error("outfit-editor-armor-realization-invalid",
                "An ARMO item may not carry an LVLI realization."));
        if (item.Kind == SkyrimOutfitEditorItemKind.Armor &&
            item.AuthoredLeveledList is not null)
            diagnostics.Add(Error("outfit-editor-armor-leveled-draft-invalid",
                "An ARMO item may not carry an authored LVLI document."));
        if (item.Kind == SkyrimOutfitEditorItemKind.LeveledList &&
            item.AuthoredArmor is not null)
            diagnostics.Add(Error("outfit-editor-lvli-armor-draft-invalid",
                "An LVLI item may not carry an authored ARMO document."));
        if (item.Kind == SkyrimOutfitEditorItemKind.Armor &&
            item.AuthoredArmor is not null)
        {
            diagnostics.AddRange(SkyrimArmorEditorValidation.Validate(
                item.AuthoredArmor, []));
            FormId expected = item.AuthoredArmor.Mode == ArmorProposalMode.New
                ? item.AuthoredArmor.TargetFormId ?? default
                : item.AuthoredArmor.SourceFormId;
            if (item.Reference.FormId != expected)
                diagnostics.Add(Error("outfit-editor-authored-armor-identity",
                    "The outfit ARMO reference does not match its authored armor document identity."));
        }
        if (item.Kind == SkyrimOutfitEditorItemKind.LeveledList)
        {
            if (item.PreviewRealization.IsDefault)
                diagnostics.Add(Error("outfit-editor-lvli-realization-uninitialized",
                    "An LVLI preview realization must be initialized, even when empty."));
            if (item.RealizationSeed is null && !item.PreviewRealization.IsDefaultOrEmpty)
                diagnostics.Add(Error("outfit-editor-lvli-seed-required",
                    "A nonempty LVLI preview realization requires its deterministic seed."));
            ValidateRealization(item.PreviewRealization, diagnostics);
            uint realizationMask = item.PreviewRealization.IsDefault
                ? 0
                : item.PreviewRealization.Aggregate(
                    0U, (mask, armor) => mask | armor.SlotMask);
            if (item.EffectiveSlotMask != realizationMask)
                diagnostics.Add(Error("outfit-editor-lvli-slot-mask-mismatch",
                    "The LVLI effective slot mask must equal its current terminal ARMO realization."));
            if (item.AuthoredLeveledList is not null)
                diagnostics.AddRange(
                    SkyrimLeveledListEditorRules.ValidateDocument(
                        item.AuthoredLeveledList));
        }
    }

    private static void ValidateRealization(
        ImmutableArray<SkyrimOutfitPreviewArmor> realization,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (realization.IsDefault) return;
        foreach (SkyrimOutfitPreviewArmor? armor in realization)
        {
            if (armor is null)
            {
                diagnostics.Add(Error("outfit-editor-realization-item-missing",
                    "An LVLI realization contains a missing terminal armor."));
                continue;
            }
            ValidateReference(armor.Reference, "realized armor", diagnostics);
        }
        if (realization.Select(item => item.Reference.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            realization.Length)
            diagnostics.Add(Error("outfit-editor-realization-duplicate",
                "An LVLI realization contains a duplicate terminal armor."));
    }

    private static IEnumerable<SkyrimOutfitPreviewArmor> PreviewArmors(
        SkyrimOutfitEditorItem item) =>
        item.Kind == SkyrimOutfitEditorItemKind.Armor
            ? [new SkyrimOutfitPreviewArmor(item.Reference, item.EffectiveSlotMask)]
            : item.PreviewRealization;

    private static void ValidateReference(
        FormReference reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference.FormId.Value is > 0 and <= 0x00FF_FFFF &&
            !string.IsNullOrWhiteSpace(reference.Plugin.Value)) return;
        diagnostics.Add(Error("outfit-editor-reference-invalid",
            $"The {role} reference must have a provider and a nonzero plugin-local 24-bit FormID."));
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value,
            right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static SkyrimOutfitDraftEditResult RefusedDraft(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static SkyrimOutfitEditorCommitResult RefusedCommit(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, diagnostics.ToImmutable());

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
