using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record SkyrimArmorAddonPreviewRequest(
    SkyrimArmorAddonEditorDocument Document,
    SkyrimArmorAddonPreviewScope Scope,
    bool IncludeBody,
    bool OppositeGender);

public sealed record SkyrimArmorAddonPreviewResult(
    bool Rendered,
    string Message);

public sealed record SkyrimArmorAddonDeletionResult(
    bool Accepted,
    string Message);

public enum SkyrimArmorAddonReferenceField
{
    PrimaryRace,
    MaleSkinTexture,
    FemaleSkinTexture,
    MaleSkinTextureSwapList,
    FemaleSkinTextureSwapList,
    FootstepSet,
    ArtObject
}

public sealed record SkyrimArmorAddonReferencePickResult(
    bool Accepted,
    FormReference? Reference);

public delegate SkyrimArmorAddonEditorDocument? SkyrimArmorAddonIntentLoader(
    SkyrimArmorAddonEditorIntent intent);
public delegate SkyrimArmorAddonPreviewResult SkyrimArmorAddonPreviewer(
    SkyrimArmorAddonPreviewRequest request);
public delegate SkyrimArmorAddonDeletionResult SkyrimArmorAddonDeletionEvaluator(
    SkyrimArmorAddonEditorDocument document);
public delegate SkyrimArmorAddonReferencePickResult SkyrimArmorAddonReferencePicker(
    SkyrimArmorAddonReferenceField field,
    FormReference? current);

public sealed class SkyrimArmorAddonEditorViewModel : NotifyViewModel
{
    private readonly SkyrimArmorAddonEditorDocument openingDocument;
    private readonly FormReference owningArmorRace;
    private readonly ImmutableArray<EditorId> existingEditorIds;
    private readonly WorkspacePath outputProposal;
    private readonly SkyrimArmorAddonIntentLoader intentLoader;
    private readonly SkyrimArmorAddonPreviewer previewer;
    private readonly SkyrimArmorAddonDeletionEvaluator deletionEvaluator;
    private readonly SkyrimArmorAddonReferencePicker referencePicker;
    private readonly SkyrimMeshPathPicker meshPathPicker;
    private readonly FormChoiceSearchResult? referenceCatalog;
    private string validationMessage = string.Empty;
    private string statusMessage = string.Empty;

    public SkyrimArmorAddonEditorViewModel(
        SkyrimArmorAddonEditorDocument openingDocument,
        FormReference owningArmorRace,
        ImmutableArray<EditorId> existingEditorIds,
        WorkspacePath outputProposal,
        SkyrimArmorAddonIntentLoader intentLoader,
        SkyrimArmorAddonPreviewer previewer,
        SkyrimArmorAddonDeletionEvaluator deletionEvaluator,
        SkyrimArmorAddonReferencePicker? referencePicker = null,
        SkyrimMeshPathPicker? meshPathPicker = null,
        FormChoiceSearchResult? referenceCatalog = null)
    {
        this.openingDocument = openingDocument ??
            throw new ArgumentNullException(nameof(openingDocument));
        this.owningArmorRace = owningArmorRace;
        this.existingEditorIds = existingEditorIds.IsDefault
            ? []
            : existingEditorIds;
        this.outputProposal = outputProposal;
        this.intentLoader = intentLoader ??
            throw new ArgumentNullException(nameof(intentLoader));
        this.previewer = previewer ??
            throw new ArgumentNullException(nameof(previewer));
        this.deletionEvaluator = deletionEvaluator ??
            throw new ArgumentNullException(nameof(deletionEvaluator));
        this.referencePicker = referencePicker ??
            ((_, _) => new SkyrimArmorAddonReferencePickResult(false, null));
        this.meshPathPicker = meshPathPicker ??
            ((_, _) => SkyrimMeshPickerRules.Cancel());
        this.referenceCatalog = referenceCatalog;
        Identity = new SkyrimArmorAddonIdentitySectionViewModel(openingDocument);
        Models = new SkyrimArmorAddonModelSectionViewModel();
        Slots = new SkyrimArmorAddonSlotSectionViewModel();
        RaceAndSkin = new SkyrimArmorAddonRaceAndSkinSectionViewModel();
        Data = new SkyrimArmorAddonDataSectionViewModel();
        Preview = new SkyrimArmorAddonPreviewSectionViewModel();
        foreach (NotifyViewModel section in new NotifyViewModel[]
                 { Identity, Models, Slots, RaceAndSkin, Data, Preview })
            section.PropertyChanged += (_, _) => Raise(nameof(CanSave));
        Load(openingDocument);
    }

    public SkyrimArmorAddonIdentitySectionViewModel Identity { get; }
    public SkyrimArmorAddonModelSectionViewModel Models { get; }
    public SkyrimArmorAddonSlotSectionViewModel Slots { get; }
    public SkyrimArmorAddonRaceAndSkinSectionViewModel RaceAndSkin { get; }
    public SkyrimArmorAddonDataSectionViewModel Data { get; }
    public SkyrimArmorAddonPreviewSectionViewModel Preview { get; }
    public SkyrimArmorAddonEditorDocument? AcceptedDocument { get; private set; }
    public SkyrimArmorAddonReferenceRow? AcceptedRow { get; private set; }
    public bool IsAccepted { get; private set; }
    public string AuthorityNotice { get; } =
        "This returns one in-memory complete Skyrim ARMA document. It does not write a plugin, equip an actor, prove runtime behavior, or prove appearance.";
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }
    public string StatusMessage
    {
        get => statusMessage;
        private set => Set(ref statusMessage, value);
    }
    public bool CanSave => TryValidate(out _, out _);
    public bool HasTypedReferenceCatalog => referenceCatalog is not null;

    public bool TrySwitchIntent(SkyrimArmorAddonEditorIntent intent)
    {
        SkyrimArmorAddonEditorDocument? loaded = intentLoader(intent);
        if (loaded is null)
        {
            StatusMessage =
                "No source-backed Armor-addon was selected; the current transaction is unchanged.";
            return false;
        }
        Load(loaded);
        StatusMessage = $"Loaded {Identity.IntentLabel}.";
        return true;
    }

    public bool TryAddAdditionalRace()
    {
        if (!FormReference.TryParse(
                RaceAndSkin.CandidateAdditionalRace, out FormReference race))
        {
            ValidationMessage =
                "Additional races require a qualified Plugin|FormID reference.";
            return false;
        }
        if (RaceAndSkin.AdditionalRaces.Any(item =>
                FormReference.TryParse(item, out FormReference existing) &&
                SameReference(existing, race)))
        {
            ValidationMessage =
                "That qualified additional race is already present.";
            return false;
        }
        RaceAndSkin.AdditionalRaces.Add(race.ToString());
        RaceAndSkin.CandidateAdditionalRace = string.Empty;
        ValidationMessage = string.Empty;
        Raise(nameof(CanSave));
        return true;
    }

    public bool TryRemoveAdditionalRace()
    {
        int index = RaceAndSkin.SelectedAdditionalRaceIndex;
        if (index < 0 || index >= RaceAndSkin.AdditionalRaces.Count)
        {
            ValidationMessage = "Choose one additional race to remove.";
            return false;
        }
        RaceAndSkin.AdditionalRaces.RemoveAt(index);
        RaceAndSkin.SelectedAdditionalRaceIndex = -1;
        ValidationMessage = string.Empty;
        Raise(nameof(CanSave));
        return true;
    }

    public bool TryMoveAdditionalRace(int delta)
    {
        if (delta is not (-1 or 1))
        {
            ValidationMessage =
                "Additional races may move exactly one position at a time.";
            return false;
        }
        int index = RaceAndSkin.SelectedAdditionalRaceIndex;
        int target = index + delta;
        if (index < 0 || index >= RaceAndSkin.AdditionalRaces.Count ||
            target < 0 || target >= RaceAndSkin.AdditionalRaces.Count)
        {
            ValidationMessage =
                "Choose an additional race that can move in that direction.";
            return false;
        }
        RaceAndSkin.AdditionalRaces.Move(index, target);
        RaceAndSkin.SelectedAdditionalRaceIndex = target;
        ValidationMessage = string.Empty;
        Raise(nameof(CanSave));
        return true;
    }

    public TypedFormIdPickerViewModel CreateReferencePicker(
        SkyrimArmorAddonReferenceField field)
    {
        if (referenceCatalog is null)
            throw new InvalidOperationException(
                "The reviewed typed Armor-addon reference catalog is unavailable.");
        (RecordSignature signature, string title, string purpose, bool allowNull) =
            field switch
            {
                SkyrimArmorAddonReferenceField.PrimaryRace =>
                    (new RecordSignature("RACE"), "Choose primary race",
                        "Choose the required Skyrim ARMA RNAM from the reviewed copied plugin closure.",
                        false),
                SkyrimArmorAddonReferenceField.MaleSkinTexture =>
                    (new RecordSignature("TXST"), "Choose male skin texture",
                        "Choose or explicitly clear the Skyrim ARMA male NAM0 TXST.",
                        true),
                SkyrimArmorAddonReferenceField.FemaleSkinTexture =>
                    (new RecordSignature("TXST"), "Choose female skin texture",
                        "Choose or explicitly clear the Skyrim ARMA female NAM1 TXST.",
                        true),
                SkyrimArmorAddonReferenceField.MaleSkinTextureSwapList =>
                    (new RecordSignature("FLST"), "Choose male skin-swap list",
                        "Choose or explicitly clear the Skyrim ARMA male NAM2 FLST.",
                        true),
                SkyrimArmorAddonReferenceField.FemaleSkinTextureSwapList =>
                    (new RecordSignature("FLST"), "Choose female skin-swap list",
                        "Choose or explicitly clear the Skyrim ARMA female NAM3 FLST.",
                        true),
                SkyrimArmorAddonReferenceField.FootstepSet =>
                    (new RecordSignature("FSTS"), "Choose footstep set",
                        "Choose or explicitly clear the Skyrim ARMA SNDD FSTS.",
                        true),
                _ =>
                    (new RecordSignature("ARTO"), "Choose art object",
                        "Choose or explicitly clear the Skyrim ARMA ONAM ARTO.",
                        true)
            };
        return new TypedFormIdPickerViewModel(
            referenceCatalog,
            new TypedFormIdPickerOptions(
                title,
                purpose,
                [signature],
                ParseOptional(CurrentReference(field), out _),
                allowNull));
    }

    public bool ApplyReferencePicker(
        SkyrimArmorAddonReferenceField field,
        TypedFormIdPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted)
        {
            StatusMessage =
                "The typed reference picker was cancelled; no value changed.";
            return false;
        }
        if (field == SkyrimArmorAddonReferenceField.PrimaryRace &&
            picker.AcceptedReference is null)
        {
            ValidationMessage = "The primary Armor-addon race cannot be NULL.";
            return false;
        }
        SetReference(field, picker.AcceptedReference?.ToString() ?? string.Empty);
        ValidationMessage = string.Empty;
        StatusMessage = picker.AcceptedReference is null
            ? "The optional reference was explicitly cleared."
            : $"Selected {picker.AcceptedReference}.";
        return true;
    }

    public void ReportReferencePickerCancelled() =>
        StatusMessage =
            "The typed reference picker was cancelled; no value changed.";

    public bool TryPickReference(SkyrimArmorAddonReferenceField field)
    {
        FormReference? current = ParseOptional(CurrentReference(field), out _);
        SkyrimArmorAddonReferencePickResult result =
            referencePicker(field, current);
        if (!result.Accepted)
        {
            StatusMessage = "The reference picker was cancelled; no value changed.";
            return false;
        }
        SetReference(field, result.Reference?.ToString() ?? string.Empty);
        StatusMessage = result.Reference is null
            ? "The optional reference was explicitly cleared."
            : $"Selected {result.Reference}.";
        return true;
    }

    public bool TryPickModel(SkyrimMeshTargetField field)
    {
        if (field is not (SkyrimMeshTargetField.ArmorAddonMaleThirdPerson or
                          SkyrimMeshTargetField.ArmorAddonFemaleThirdPerson or
                          SkyrimMeshTargetField.ArmorAddonMaleFirstPerson or
                          SkyrimMeshTargetField.ArmorAddonFemaleFirstPerson))
        {
            StatusMessage =
                "The Armor-addon editor refused a mesh field it does not own.";
            return false;
        }
        string current = field switch
        {
            SkyrimMeshTargetField.ArmorAddonMaleThirdPerson => Models.MaleModel,
            SkyrimMeshTargetField.ArmorAddonFemaleThirdPerson => Models.FemaleModel,
            SkyrimMeshTargetField.ArmorAddonMaleFirstPerson =>
                Models.MaleFirstPersonModel,
            _ => Models.FemaleFirstPersonModel
        };
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
                "The mesh picker was cancelled; the Armor-addon model was preserved.";
            return false;
        }
        if (!SkyrimMeshPickerSelectionBoundary.TryGetRelativePath(
                selection, out string relativePath))
        {
            StatusMessage =
                "The Armor-addon editor refused an unbound mesh-picker result.";
            return false;
        }
        switch (field)
        {
            case SkyrimMeshTargetField.ArmorAddonMaleThirdPerson:
                Models.MaleModel = relativePath;
                break;
            case SkyrimMeshTargetField.ArmorAddonFemaleThirdPerson:
                Models.FemaleModel = relativePath;
                break;
            case SkyrimMeshTargetField.ArmorAddonMaleFirstPerson:
                Models.MaleFirstPersonModel = relativePath;
                break;
            case SkyrimMeshTargetField.ArmorAddonFemaleFirstPerson:
                Models.FemaleFirstPersonModel = relativePath;
                break;
        }
        StatusMessage = $"Selected Meshes-relative Armor-addon model {relativePath}.";
        return true;
    }

    public bool TryPreview()
    {
        if (!TryBuildDocument(out SkyrimArmorAddonEditorDocument? document,
                out string error))
        {
            ValidationMessage = error;
            return false;
        }
        SkyrimArmorAddonPreviewResult result = previewer(
            new SkyrimArmorAddonPreviewRequest(
                document, Preview.Scope, Preview.IncludeBody,
                Preview.OppositeGender));
        StatusMessage = result.Message;
        return result.Rendered;
    }

    public bool TryDeleteOrRevert()
    {
        if (!TryBuildDocument(out SkyrimArmorAddonEditorDocument? document,
                out string error))
        {
            ValidationMessage = error;
            return false;
        }
        SkyrimArmorAddonDeletionResult result = deletionEvaluator(document);
        StatusMessage = result.Message;
        return result.Accepted;
    }

    public bool TrySave()
    {
        if (!TryBuildDocument(out SkyrimArmorAddonEditorDocument? document,
                out string error))
        {
            ValidationMessage = error;
            return false;
        }
        SkyrimArmorAddonEditorResult saved =
            SkyrimArmorAddonEditorRules.Save(document, existingEditorIds);
        if (!saved.Accepted || saved.Document is null)
        {
            ValidationMessage = JoinDiagnostics(saved.Diagnostics);
            return false;
        }
        SkyrimArmorAddonReferenceTransition nested =
            SkyrimArmorAddonEditorRules.ToReferenceRow(
                saved.Document, owningArmorRace, outputProposal);
        if (!nested.Accepted || nested.Row is null)
        {
            ValidationMessage = JoinDiagnostics(nested.Diagnostics);
            return false;
        }
        AcceptedDocument = saved.Document;
        AcceptedRow = nested.Row;
        IsAccepted = true;
        ValidationMessage = string.Empty;
        StatusMessage =
            "Armor-addon document accepted in memory; no plugin has been written.";
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        _ = SkyrimArmorAddonEditorRules.Cancel();
        AcceptedDocument = null;
        AcceptedRow = null;
        IsAccepted = false;
        Load(openingDocument);
        StatusMessage =
            "Armor-addon edit cancelled; the opening state was restored.";
        Raise(nameof(IsAccepted));
    }

    private void Load(SkyrimArmorAddonEditorDocument document)
    {
        Identity.Load(document);
        Models.Load(document);
        Slots.Load(document.SlotMask);
        RaceAndSkin.Load(document);
        Data.Load(document);
        Preview.Reset();
        ValidationMessage = string.Empty;
        Raise(nameof(CanSave));
    }

    private bool TryValidate(
        [NotNullWhen(true)] out SkyrimArmorAddonEditorDocument? document,
        out string error)
    {
        if (!TryBuildDocument(out document, out error)) return false;
        ImmutableArray<Diagnostic> diagnostics =
            SkyrimArmorAddonEditorRules.ValidateDocument(
                document, existingEditorIds);
        if (diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            error = JoinDiagnostics(diagnostics);
            document = null;
            return false;
        }
        SkyrimArmorAddonReferenceTransition nested =
            SkyrimArmorAddonEditorRules.ToReferenceRow(
                document, owningArmorRace, outputProposal);
        if (!nested.Accepted)
        {
            error = JoinDiagnostics(nested.Diagnostics);
            document = null;
            return false;
        }
        return true;
    }

    private bool TryBuildDocument(
        [NotNullWhen(true)] out SkyrimArmorAddonEditorDocument? document,
        out string error)
    {
        document = null;
        error = string.Empty;
        EditorId editorId;
        try { editorId = new EditorId(Identity.EditorIdText.Trim()); }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
        if (!TryOptionalReference(RaceAndSkin.Race, "primary race",
                out FormReference? race, out error) ||
            !TryOptionalReference(RaceAndSkin.MaleSkinTexture,
                "male skin texture", out FormReference? maleSkin,
                out error) ||
            !TryOptionalReference(RaceAndSkin.FemaleSkinTexture,
                "female skin texture", out FormReference? femaleSkin,
                out error) ||
            !TryOptionalReference(RaceAndSkin.MaleSkinTextureSwapList,
                "male skin-swap list", out FormReference? maleSwap,
                out error) ||
            !TryOptionalReference(RaceAndSkin.FemaleSkinTextureSwapList,
                "female skin-swap list", out FormReference? femaleSwap,
                out error) ||
            !TryOptionalReference(RaceAndSkin.FootstepSet,
                "footstep set", out FormReference? footstep,
                out error) ||
            !TryOptionalReference(RaceAndSkin.ArtObject,
                "art object", out FormReference? artObject,
                out error))
            return false;
        ImmutableArray<FormReference>.Builder additionalRaces =
            ImmutableArray.CreateBuilder<FormReference>();
        foreach (string value in RaceAndSkin.AdditionalRaces)
        {
            if (!FormReference.TryParse(value, out FormReference parsed))
            {
                error =
                    $"Additional race '{value}' is not a qualified Plugin|FormID reference.";
                return false;
            }
            additionalRaces.Add(parsed);
        }
        if (!byte.TryParse(Data.MalePriorityText,
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out byte malePriority) ||
            !byte.TryParse(Data.FemalePriorityText,
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out byte femalePriority) ||
            !byte.TryParse(Data.DetectionSoundText,
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out byte detectionSound) ||
            !double.TryParse(Data.WeaponAdjustText,
                NumberStyles.Float, CultureInfo.InvariantCulture,
                out double weaponAdjust))
        {
            error =
                "Priorities and detection sound must be bytes; weapon adjustment must be a finite number.";
            return false;
        }
        document = new SkyrimArmorAddonEditorDocument(
            GameEdition.SkyrimSpecialEdition,
            Identity.Intent,
            Identity.Mode,
            Identity.SourcePlugin,
            Identity.SourceFormId,
            editorId,
            Identity.TargetFormId,
            Identity.TargetPlugin,
            Identity.SeedFromSource,
            OptionalText(Models.MaleModel),
            OptionalText(Models.FemaleModel),
            OptionalText(Models.MaleFirstPersonModel),
            OptionalText(Models.FemaleFirstPersonModel),
            Slots.SelectedMask,
            race,
            additionalRaces.ToImmutable(),
            maleSkin,
            femaleSkin,
            maleSwap,
            femaleSwap,
            footstep,
            artObject,
            malePriority,
            femalePriority,
            Data.MaleWeightSliderEnabled,
            Data.FemaleWeightSliderEnabled,
            detectionSound,
            weaponAdjust);
        return true;
    }

    private static bool TryOptionalReference(
        string value,
        string role,
        out FormReference? reference,
        out string error)
    {
        reference = ParseOptional(value, out bool valid);
        error = valid
            ? string.Empty
            : $"The {role} must be empty or a qualified Plugin|FormID reference.";
        return valid;
    }

    private static FormReference? ParseOptional(
        string value,
        out bool valid)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            valid = true;
            return null;
        }
        valid = FormReference.TryParse(value.Trim(),
            out FormReference parsed);
        return valid ? parsed : null;
    }

    private string CurrentReference(SkyrimArmorAddonReferenceField field) =>
        field switch
        {
            SkyrimArmorAddonReferenceField.PrimaryRace => RaceAndSkin.Race,
            SkyrimArmorAddonReferenceField.MaleSkinTexture =>
                RaceAndSkin.MaleSkinTexture,
            SkyrimArmorAddonReferenceField.FemaleSkinTexture =>
                RaceAndSkin.FemaleSkinTexture,
            SkyrimArmorAddonReferenceField.MaleSkinTextureSwapList =>
                RaceAndSkin.MaleSkinTextureSwapList,
            SkyrimArmorAddonReferenceField.FemaleSkinTextureSwapList =>
                RaceAndSkin.FemaleSkinTextureSwapList,
            SkyrimArmorAddonReferenceField.FootstepSet =>
                RaceAndSkin.FootstepSet,
            SkyrimArmorAddonReferenceField.ArtObject => RaceAndSkin.ArtObject,
            _ => string.Empty
        };

    private void SetReference(
        SkyrimArmorAddonReferenceField field,
        string value)
    {
        switch (field)
        {
            case SkyrimArmorAddonReferenceField.PrimaryRace:
                RaceAndSkin.Race = value;
                break;
            case SkyrimArmorAddonReferenceField.MaleSkinTexture:
                RaceAndSkin.MaleSkinTexture = value;
                break;
            case SkyrimArmorAddonReferenceField.FemaleSkinTexture:
                RaceAndSkin.FemaleSkinTexture = value;
                break;
            case SkyrimArmorAddonReferenceField.MaleSkinTextureSwapList:
                RaceAndSkin.MaleSkinTextureSwapList = value;
                break;
            case SkyrimArmorAddonReferenceField.FemaleSkinTextureSwapList:
                RaceAndSkin.FemaleSkinTextureSwapList = value;
                break;
            case SkyrimArmorAddonReferenceField.FootstepSet:
                RaceAndSkin.FootstepSet = value;
                break;
            case SkyrimArmorAddonReferenceField.ArtObject:
                RaceAndSkin.ArtObject = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static string? OptionalText(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool SameReference(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static string JoinDiagnostics(
        ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(" ", diagnostics.Select(item => item.Message));
}
