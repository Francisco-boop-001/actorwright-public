using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record SkyrimArmorPreviewResult(bool Rendered, string Message);
public sealed record SkyrimArmorDeletionResult(bool Accepted, string Message);

public delegate SkyrimArmorEditorDocument? SkyrimArmorIntentLoader(
    SkyrimArmorEditorIntent intent);
public delegate ImmutableArray<SkyrimArmorAddonSlotEvidence> SkyrimArmorSlotEvidenceProvider(
    SkyrimArmorEditorDocument document);
public delegate SkyrimArmorPreviewResult SkyrimArmorPreviewer(
    SkyrimArmorEditorDocument document,
    bool fullOutfit,
    bool includeBody,
    bool alternateGender);
public delegate SkyrimArmorDeletionResult SkyrimArmorDeletionHandler(
    SkyrimArmorEditorDocument document);
public delegate SkyrimArmorAddonReferenceRow? SkyrimArmorAddonRowPicker(
    SkyrimArmorEditorDocument document,
    int? selectedIndex);

public sealed class SkyrimArmorEditorViewModel : NotifyViewModel
{
    private readonly string authorityNotice =
        "Document acceptance is static only. Preview, equipment, runtime, and visual authority require separate evidence.";
    private readonly SkyrimArmorEditorDocument openingDocument;
    private readonly ImmutableArray<EditorId> existingEditorIds;
    private readonly SkyrimArmorIntentLoader intentLoader;
    private readonly SkyrimArmorSlotEvidenceProvider slotEvidenceProvider;
    private readonly SkyrimArmorPreviewer previewer;
    private readonly SkyrimArmorDeletionHandler deletionHandler;
    private readonly SkyrimMeshPathPicker meshPathPicker;
    private readonly SkyrimArmorAddonRowPicker? armorAddonPicker;

    public SkyrimArmorEditorViewModel(
        SkyrimArmorEditorDocument openingDocument,
        ImmutableArray<EditorId> existingEditorIds,
        SkyrimArmorIntentLoader intentLoader,
        SkyrimArmorSlotEvidenceProvider slotEvidenceProvider,
        SkyrimArmorPreviewer previewer,
        SkyrimArmorDeletionHandler deletionHandler,
        SkyrimMeshPathPicker? meshPathPicker = null,
        SkyrimArmorAddonRowPicker? armorAddonPicker = null)
    {
        this.openingDocument = openingDocument ??
            throw new ArgumentNullException(nameof(openingDocument));
        this.existingEditorIds = existingEditorIds.IsDefault
            ? []
            : openingDocument.Intent == SkyrimArmorEditorIntent.EditAuthored
                ? existingEditorIds.Where(item => !string.Equals(
                        item.Value, openingDocument.EditorId.Value,
                        StringComparison.OrdinalIgnoreCase))
                    .ToImmutableArray()
                : existingEditorIds;
        this.intentLoader = intentLoader ?? throw new ArgumentNullException(nameof(intentLoader));
        this.slotEvidenceProvider = slotEvidenceProvider ??
            throw new ArgumentNullException(nameof(slotEvidenceProvider));
        this.previewer = previewer ?? throw new ArgumentNullException(nameof(previewer));
        this.deletionHandler = deletionHandler ??
            throw new ArgumentNullException(nameof(deletionHandler));
        this.meshPathPicker = meshPathPicker ??
            ((_, _) => SkyrimMeshPickerRules.Cancel());
        this.armorAddonPicker = armorAddonPicker;

        Identity = new SkyrimArmorIdentitySectionViewModel(openingDocument);
        Core = new SkyrimArmorCoreSectionViewModel();
        References = new SkyrimArmorReferenceSectionViewModel();
        Bounds = new SkyrimArmorBoundsSectionViewModel();
        Collections = new SkyrimArmorCollectionSectionViewModel();
        SubscribeSections();
        Load(openingDocument);
    }

    public SkyrimArmorIdentitySectionViewModel Identity { get; }
    public SkyrimArmorCoreSectionViewModel Core { get; }
    public SkyrimArmorReferenceSectionViewModel References { get; }
    public SkyrimArmorBoundsSectionViewModel Bounds { get; }
    public SkyrimArmorCollectionSectionViewModel Collections { get; }
    public SkyrimArmorEditorDocument? AcceptedDocument { get; private set; }
    public bool IsAccepted { get; private set; }
    public string AuthorityNotice => authorityNotice;

    private bool canSave;
    public bool CanSave { get => canSave; private set => Set(ref canSave, value); }

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

    private bool previewFullOutfit;
    public bool PreviewFullOutfit
    {
        get => previewFullOutfit;
        set => Set(ref previewFullOutfit, value);
    }

    private bool includeBody = true;
    public bool IncludeBody
    {
        get => includeBody;
        set => Set(ref includeBody, value);
    }

    private bool alternateGender;
    public bool AlternateGender
    {
        get => alternateGender;
        set => Set(ref alternateGender, value);
    }

    public bool TrySwitchIntent(SkyrimArmorEditorIntent intent)
    {
        SkyrimArmorEditorDocument? loaded;
        try { loaded = intentLoader(intent); }
        catch (Exception exception)
        {
            StatusMessage = $"Could not load {intent}: {exception.Message}";
            return false;
        }
        if (loaded is null)
        {
            StatusMessage = $"{intent} was cancelled or unavailable; the current armor was preserved.";
            return false;
        }
        Load(loaded);
        StatusMessage = $"Loaded {Identity.IntentLabel}.";
        return true;
    }

    public bool TryAddArmorAddon() => armorAddonPicker is null
        ? TryReferenceTransition((document, reference) =>
            SkyrimArmorEditorCollectionRules.AddArmorAddon(document, reference))
        : TryPickArmorAddon(null);

    public bool TryReplaceArmorAddon() => armorAddonPicker is null
        ? TryReferenceTransition((document, reference) =>
            SkyrimArmorEditorCollectionRules.ReplaceArmorAddon(
                document, Collections.SelectedArmorAddonIndex, reference))
        : TryPickArmorAddon(Collections.SelectedArmorAddonIndex);

    public bool TryAddArmorAddonReferenceRow(
        SkyrimArmorAddonReferenceRow row) =>
        TryDocumentTransition(document =>
            SkyrimArmorEditorCollectionRules.AddArmorAddonReferenceRow(
                document, row));

    public bool TryApplyArmorAddonReferenceRow(
        int index,
        SkyrimArmorAddonReferenceRow row) =>
        TryDocumentTransition(document =>
            SkyrimArmorEditorCollectionRules.ApplyArmorAddonReferenceRow(
                document, index, row));

    public bool TryRemoveArmorAddon() =>
        TryDocumentTransition(document =>
            SkyrimArmorEditorCollectionRules.RemoveArmorAddon(
                document, Collections.SelectedArmorAddonIndex));

    public bool TryMoveArmorAddon(int delta) =>
        TryDocumentTransition(document =>
            SkyrimArmorEditorCollectionRules.MoveArmorAddon(
                document,
                Collections.SelectedArmorAddonIndex,
                Collections.SelectedArmorAddonIndex + delta));

    public bool TryAddKeyword() =>
        TryReferenceTransition((document, reference) =>
            SkyrimArmorEditorCollectionRules.AddKeyword(document, reference));

    public bool TryRemoveKeyword() =>
        TryDocumentTransition(document =>
            SkyrimArmorEditorCollectionRules.RemoveKeyword(
                document, Collections.SelectedKeywordIndex));

    public bool TryRecalculateSlots()
    {
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
            return Refuse(error);
        try
        {
            ImmutableArray<SkyrimArmorAddonSlotEvidence> evidence =
                slotEvidenceProvider(document);
            return Apply(SkyrimArmorEditorCollectionRules.RecalculateSlots(
                document, evidence), "BOD2 now equals the union of every referenced ARMA slot mask.");
        }
        catch (Exception exception)
        {
            return Refuse($"Slot evidence failed: {exception.Message}");
        }
    }

    public bool TryPickWorldModel(SkyrimMeshTargetField field)
    {
        if (field is not (SkyrimMeshTargetField.ArmorMaleWorld or
                          SkyrimMeshTargetField.ArmorFemaleWorld))
        {
            StatusMessage =
                "The Armor editor refused a mesh field it does not own.";
            return false;
        }
        string current = field == SkyrimMeshTargetField.ArmorMaleWorld
            ? References.MaleWorldModel
            : References.FemaleWorldModel;
        SkyrimMeshPickerSelection selection;
        try { selection = meshPathPicker(field, current); }
        catch (Exception exception)
        {
            StatusMessage = $"Mesh picker failed: {exception.Message}";
            return false;
        }
        if (!selection.Accepted)
        {
            StatusMessage =
                "The mesh picker was cancelled; the world model was preserved.";
            return false;
        }
        if (!SkyrimMeshPickerSelectionBoundary.TryGetRelativePath(
                selection, out string relativePath))
        {
            StatusMessage =
                "The Armor editor refused an unbound mesh-picker result.";
            return false;
        }
        if (field == SkyrimMeshTargetField.ArmorMaleWorld)
            References.MaleWorldModel = relativePath;
        else
            References.FemaleWorldModel = relativePath;
        StatusMessage = $"Selected Meshes-relative world model {relativePath}.";
        return true;
    }

    public bool TryPreview()
    {
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
            return Refuse(error);
        try
        {
            SkyrimArmorPreviewResult result = previewer(
                document, PreviewFullOutfit, IncludeBody, AlternateGender);
            StatusMessage = result.Message;
            return result.Rendered;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Preview failed: {exception.Message}";
            return false;
        }
    }

    public bool TryDeleteOrRevert()
    {
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
            return Refuse(error);
        try
        {
            SkyrimArmorDeletionResult result = deletionHandler(document);
            StatusMessage = result.Message;
            return result.Accepted;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Delete or revert failed: {exception.Message}";
            return false;
        }
    }

    public bool TrySave()
    {
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
            return Refuse(error);
        SkyrimArmorEditorResult result = SkyrimArmorEditorRules.Save(
            document, existingEditorIds);
        if (!result.Accepted || result.Document is null)
            return Refuse(FirstError(result.Diagnostics));
        AcceptedDocument = result.Document;
        IsAccepted = true;
        ValidationMessage = string.Empty;
        StatusMessage = "The immutable Skyrim ARMO document is ready for the outer outfit transaction.";
        Raise(nameof(AcceptedDocument));
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        SkyrimArmorEditorRules.Cancel();
        AcceptedDocument = null;
        IsAccepted = false;
        Load(openingDocument);
        StatusMessage = string.Empty;
        Raise(nameof(AcceptedDocument));
        Raise(nameof(IsAccepted));
    }

    private void SubscribeSections()
    {
        Identity.PropertyChanged += (_, _) => RefreshValidation();
        Core.PropertyChanged += (_, _) => RefreshValidation();
        References.PropertyChanged += (_, _) => RefreshValidation();
        Bounds.PropertyChanged += (_, _) => RefreshValidation();
        Collections.ArmorAddons.CollectionChanged += OnCollectionChanged;
        Collections.Keywords.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshValidation();

    private void Load(SkyrimArmorEditorDocument document)
    {
        Identity.Load(document);
        Core.Load(document);
        References.Load(document);
        Bounds.Load(document.ObjectBounds);
        Collections.Load(document);
        AcceptedDocument = null;
        IsAccepted = false;
        Raise(nameof(AcceptedDocument));
        Raise(nameof(IsAccepted));
        RefreshValidation();
    }

    private bool TryReferenceTransition(
        Func<SkyrimArmorEditorDocument, FormReference, SkyrimArmorEditorResult> transition)
    {
        if (!FormReference.TryParse(Collections.CandidateReference.Trim(), out FormReference reference))
            return Refuse("Enter a qualified Plugin|FormID reference.");
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
            return Refuse(error);
        return Apply(transition(document, reference), "The ordered armor document was updated.");
    }

    private bool TryDocumentTransition(
        Func<SkyrimArmorEditorDocument, SkyrimArmorEditorResult> transition)
    {
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
            return Refuse(error);
        return Apply(transition(document), "The ordered armor document was updated.");
    }

    private bool TryPickArmorAddon(int? selectedIndex)
    {
        if (armorAddonPicker is null)
            throw new InvalidOperationException(
                "The Armor-addon picker is not configured.");
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document,
                out string error))
            return Refuse(error);
        if (selectedIndex is int index &&
            (index < 0 || index >= document.ArmorAddons.Length))
            return Refuse("Choose one Armor-addon row to replace.");
        SkyrimArmorAddonReferenceRow? row;
        try { row = armorAddonPicker(document, selectedIndex); }
        catch (Exception exception)
        {
            return Refuse($"Armor-addon picker failed: {exception.Message}");
        }
        if (row is null)
        {
            StatusMessage =
                "The Armor-addon picker was cancelled; the ordered rows were preserved.";
            return false;
        }
        SkyrimArmorEditorResult result = selectedIndex is int selected
            ? SkyrimArmorEditorCollectionRules.ApplyArmorAddonReferenceRow(
                document, selected, row)
            : SkyrimArmorEditorCollectionRules.AddArmorAddonReferenceRow(
                document, row);
        return Apply(result,
            "The reviewed Armor-addon row was accepted into the ordered armor document.");
    }

    private bool Apply(SkyrimArmorEditorResult result, string success)
    {
        if (!result.Accepted || result.Document is null)
            return Refuse(FirstError(result.Diagnostics));
        Load(result.Document);
        StatusMessage = success;
        return true;
    }

    private void RefreshValidation()
    {
        AcceptedDocument = null;
        IsAccepted = false;
        Raise(nameof(AcceptedDocument));
        Raise(nameof(IsAccepted));
        if (!TryBuildDocument(out SkyrimArmorEditorDocument? document, out string error))
        {
            CanSave = false;
            ValidationMessage = error;
            return;
        }
        ImmutableArray<Diagnostic> diagnostics =
            SkyrimArmorEditorRules.ValidateDocument(document, existingEditorIds);
        CanSave = !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        ValidationMessage = CanSave ? string.Empty : FirstError(diagnostics);
    }

    private bool TryBuildDocument(
        [NotNullWhen(true)] out SkyrimArmorEditorDocument? document,
        out string error) =>
        SkyrimArmorEditorDocumentBuilder.TryBuild(
            Identity, Core, References, Bounds, Collections,
            out document, out error);

    private bool Refuse(string message)
    {
        ValidationMessage = message;
        StatusMessage = string.Empty;
        return false;
    }

    private static string FirstError(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ??
        "The Skyrim armor document was refused.";
}
