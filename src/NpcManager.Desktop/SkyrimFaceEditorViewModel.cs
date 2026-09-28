using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record SkyrimFaceEditorCatalogs(
    FormChoiceSearchResult TypedForms,
    ImmutableDictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult> HeadParts,
    ImmutableDictionary<SkyrimRaceMenuPaintCategory, SkyrimRaceMenuPaintChoiceResult> Paints,
    SkyrimRaceMenuSliderCatalog? SliderCatalog,
    ImmutableArray<NpcHeadPartSelection> RaceDefaultHeadParts = default);

/// <summary>
/// Thin transaction coordinator for the six Skyrim face sections. Child view
/// models own presentation state; this class owns only the immutable baseline,
/// current document, validation, picker composition, and commit/cancel result.
/// </summary>
public sealed class SkyrimFaceEditorViewModel : NotifyViewModel
{
    private readonly WorkspacePath dataRoot;
    private readonly SkyrimFaceEditorCatalogs catalogs;
    private readonly ISkyrimHeadPartPreviewService? previewService;
    private readonly int faceOverlaySlotLimit;
    private readonly string title;
    private readonly string authorityNotice;

    public SkyrimFaceEditorViewModel(
        SkyrimFaceEditorDocument baseline,
        WorkspacePath dataRoot,
        FormReference race,
        string raceEditorId,
        NpcSex sex,
        SkyrimFaceEditorCatalogs catalogs,
        int faceOverlaySlotLimit,
        ISkyrimHeadPartPreviewService? previewService = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(catalogs);
        SkyrimFaceEditorValidationResult validation =
            SkyrimFaceEditorDocumentRules.Validate(baseline);
        if (!validation.Accepted)
            throw new ArgumentException(validation.Diagnostics[0].Message, nameof(baseline));
        if (string.IsNullOrWhiteSpace(raceEditorId) || raceEditorId.Length > 256 ||
            raceEditorId.Any(char.IsControl))
            throw new ArgumentException("Race EditorID is required.", nameof(raceEditorId));
        if (faceOverlaySlotLimit is < 1 or > SkyrimFaceEditorDocumentRules.MaximumBodyOverlays)
            throw new ArgumentOutOfRangeException(nameof(faceOverlaySlotLimit));
        if (catalogs.HeadParts is null || catalogs.Paints is null)
            throw new ArgumentException("Picker catalogs must be initialized.", nameof(catalogs));
        ValidateCatalogs(catalogs);

        BaselineDocument = baseline;
        currentDocument = baseline;
        this.dataRoot = dataRoot;
        Race = race;
        RaceEditorId = raceEditorId;
        Sex = sex;
        this.catalogs = catalogs;
        this.faceOverlaySlotLimit = faceOverlaySlotLimit;
        this.previewService = previewService;
        title = $"Edit Skyrim face — {raceEditorId}";
        authorityNotice =
            $"Static {sex.ToString().ToLowerInvariant()} face document only · " +
            "FaceGen, texture-render, runtime, and visual authority false";

        Parts = new SkyrimFacePartsEditorViewModel(this);
        NativeMorphs = new SkyrimNativeFaceMorphEditorViewModel(this);
        CustomMorphs = new SkyrimCustomFaceMorphEditorViewModel(this, CatalogSliders());
        Tints = new SkyrimFaceTintEditorViewModel(this);
        Sculpt = new SkyrimFaceSculptEditorViewModel(this);
        FaceOverlays = new SkyrimFaceOverlayEditorViewModel(this, faceOverlaySlotLimit);
        RefreshAll();
    }

    public string Title => title;
    public string Context => $"{RaceEditorId} · {Sex.ToString().ToLowerInvariant()} · immutable transaction";
    public string AuthorityNotice => authorityNotice;
    public FormReference Race { get; }
    public string RaceEditorId { get; }
    public NpcSex Sex { get; }
    public SkyrimFaceEditorDocument BaselineDocument { get; }
    public SkyrimFaceEditorDocument CurrentDocument => currentDocument;
    public SkyrimFaceEditorDocument? AcceptedDocument { get; private set; }
    public bool IsDirty => !SkyrimFaceEditorDocumentRules.Equivalent(
        currentDocument, BaselineDocument);
    public bool IsAccepted => AcceptedDocument is not null;
    public SkyrimFacePartsEditorViewModel Parts { get; }
    public SkyrimNativeFaceMorphEditorViewModel NativeMorphs { get; }
    public SkyrimCustomFaceMorphEditorViewModel CustomMorphs { get; }
    public SkyrimFaceTintEditorViewModel Tints { get; }
    public SkyrimFaceSculptEditorViewModel Sculpt { get; }
    public SkyrimFaceOverlayEditorViewModel FaceOverlays { get; }
    internal ImmutableArray<NpcHeadPartSelection> RaceDefaultHeadParts =>
        catalogs.RaceDefaultHeadParts.IsDefault
            ? ImmutableArray<NpcHeadPartSelection>.Empty
            : catalogs.RaceDefaultHeadParts;

    private SkyrimFaceEditorDocument currentDocument;

    private SkyrimFaceEditorSection selectedSection;
    public SkyrimFaceEditorSection SelectedSection
    {
        get => selectedSection;
        set
        {
            if (!Set(ref selectedSection, value)) return;
            ValidationMessage = string.Empty;
        }
    }

    private string status = "Edit one section, reset it independently, or save one complete face document.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public void ResetSelectedSection()
    {
        SkyrimFaceEditorDocument reset = SkyrimFaceEditorDocumentRules.ResetSection(
            currentDocument, BaselineDocument, SelectedSection);
        Replace(reset, SelectedSection,
            $"{SectionName(SelectedSection)} restored to the pre-open baseline.");
    }

    public bool TryAccept()
    {
        SkyrimFaceEditorValidationResult validation =
            SkyrimFaceEditorDocumentRules.Validate(currentDocument);
        if (!validation.Accepted)
        {
            AcceptedDocument = null;
            ValidationMessage = validation.Diagnostics[0].Message;
            Status = "The face document was not committed.";
            Raise(nameof(IsAccepted));
            return false;
        }
        AcceptedDocument = currentDocument;
        ValidationMessage = string.Empty;
        Status = "Complete typed face document accepted; no plugin or asset was written.";
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        AcceptedDocument = null;
        currentDocument = BaselineDocument;
        ValidationMessage = string.Empty;
        Status = "Face editing cancelled; the exact pre-open baseline was restored.";
        RefreshAll();
        Raise(nameof(CurrentDocument));
        Raise(nameof(IsDirty));
        Raise(nameof(IsAccepted));
    }

    public SkyrimHeadPartPickerViewModel CreateHeadPartPicker(NpcHeadPartType type)
    {
        if (!catalogs.HeadParts.TryGetValue(type, out SkyrimHeadPartChoiceResult? result))
            throw new InvalidOperationException($"No reviewed {type.ToWireName()} catalog is available.");
        return new SkyrimHeadPartPickerViewModel(
            result, dataRoot, Race, Sex, type, previewService);
    }

    public bool ApplyHeadPartPicker(SkyrimHeadPartPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted || picker.AcceptedReference is not { } reference)
            return false;
        SkyrimFaceEditorDocument changed = SkyrimFaceEditorDocumentRules.ReplaceHeadPart(
            currentDocument, new NpcHeadPartSelection(reference, picker.Type));
        Replace(changed, SkyrimFaceEditorSection.FaceParts,
            $"Selected {picker.Type.ToWireName()} {reference}.");
        return true;
    }

    public TypedFormIdPickerViewModel CreateHairColorPicker()
    {
        FormReference? current = currentDocument.Parts.HairColor.IsSpecified
            ? currentDocument.Parts.HairColor.Value
            : null;
        return TypedPicker("Choose hair color", "NPC hair color (HCLF)",
            new RecordSignature("CLFM"), current, allowNull: true);
    }

    public bool ApplyHairColorPicker(TypedFormIdPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted) return false;
        OptionalFormReference value = picker.AcceptedReference is { } selected
            ? OptionalFormReference.Set(selected)
            : OptionalFormReference.Clear();
        Replace(SkyrimFaceEditorDocumentRules.SetHairColor(currentDocument, value),
            SkyrimFaceEditorSection.FaceParts, "Hair-color override updated.");
        return true;
    }

    public void PreserveHairColor()
    {
        Replace(SkyrimFaceEditorDocumentRules.SetHairColor(currentDocument, default),
            SkyrimFaceEditorSection.FaceParts, "Hair color will preserve the source record behavior.");
    }

    public TypedFormIdPickerViewModel CreateHeadTexturePicker() =>
        TypedPicker("Choose head texture", "NPC face texture set (FTST)",
            new RecordSignature("TXST"), currentDocument.Parts.HeadTexture, allowNull: true);

    public bool ApplyHeadTexturePicker(TypedFormIdPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted) return false;
        Replace(SkyrimFaceEditorDocumentRules.SetHeadTexture(
                currentDocument, picker.AcceptedReference),
            SkyrimFaceEditorSection.FaceParts, "Head-texture override updated.");
        return true;
    }

    public SkyrimRaceMenuPaintPickerViewModel CreateTintMaskPicker(ushort tintIndex)
    {
        SkyrimFaceEditorTintLayer layer = currentDocument.Tints.Single(item =>
            item.Value.Index == tintIndex);
        string? current = layer.MaskOverride?.Value ?? layer.RaceDefaultMask?.Value;
        return PaintPicker(SkyrimRaceMenuPaintCategory.Warpaint, current, allowNone: true);
    }

    public bool ApplyTintMaskPicker(
        ushort tintIndex,
        SkyrimRaceMenuPaintPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted) return false;
        AssetPath? selected = string.IsNullOrWhiteSpace(picker.AcceptedPath)
            ? null
            : new AssetPath(picker.AcceptedPath);
        Replace(SkyrimFaceEditorDocumentRules.SetTintMask(
                currentDocument, tintIndex, selected),
            SkyrimFaceEditorSection.Tints, $"Tint {tintIndex} mask updated.");
        return true;
    }

    public SkyrimRaceMenuPaintPickerViewModel CreateFacePaintPicker() =>
        PaintPicker(SkyrimRaceMenuPaintCategory.Face, null, allowNone: false);

    public bool ApplyFacePaintPicker(SkyrimRaceMenuPaintPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted || picker.AcceptedEntry is not { } paint) return false;
        Replace(SkyrimFaceEditorDocumentRules.AddFaceOverlay(
                currentDocument, paint, faceOverlaySlotLimit),
            SkyrimFaceEditorSection.FaceOverlays, "Face paint added to the lowest free reviewed slot.");
        return true;
    }

    internal void Replace(
        SkyrimFaceEditorDocument document,
        SkyrimFaceEditorSection section,
        string message,
        bool refreshSection = true)
    {
        // Internal callers receive documents only from validated rule transitions.
        // Final acceptance validates once more at the transaction boundary.
        currentDocument = document;
        AcceptedDocument = null;
        ValidationMessage = string.Empty;
        Status = message;
        if (refreshSection) Refresh(section);
        Raise(nameof(CurrentDocument));
        Raise(nameof(IsDirty));
        Raise(nameof(IsAccepted));
    }

    internal ImmutableArray<FormReference> ProvenOrphanedMisc(FormReference parent)
    {
        SkyrimHeadPartChoiceCandidate? source = FindHeadPartCandidate(parent);
        if (source is null || source.ExtraParts.IsDefaultOrEmpty) return [];
        var currentParents = currentDocument.Parts.OrderedHeadParts
            .Where(item => item.Type != NpcHeadPartType.Misc &&
                           !SameReference(item.Reference, parent))
            .Select(item => FindHeadPartCandidate(item.Reference))
            .Where(item => item is not null)
            .Cast<SkyrimHeadPartChoiceCandidate>()
            .ToImmutableArray();
        return source.ExtraParts
            .Where(extra => currentDocument.Parts.OrderedHeadParts.Any(item =>
                                item.Type == NpcHeadPartType.Misc &&
                                SameReference(item.Reference, extra)) &&
                            currentParents.All(candidate =>
                                !candidate.ExtraParts.Any(item => SameReference(item, extra))))
            .ToImmutableArray();
    }

    internal SkyrimHeadPartChoiceCandidate? FindHeadPartCandidate(FormReference reference) =>
        catalogs.HeadParts.Values
            .SelectMany(item => item.Candidates)
            .FirstOrDefault(item => SameReference(item.Reference, reference));

    private TypedFormIdPickerViewModel TypedPicker(
        string title,
        string purpose,
        RecordSignature signature,
        FormReference? current,
        bool allowNull) => new(
        catalogs.TypedForms,
        new TypedFormIdPickerOptions(
            title,
            purpose,
            [signature],
            current,
            allowNull));

    private SkyrimRaceMenuPaintPickerViewModel PaintPicker(
        SkyrimRaceMenuPaintCategory category,
        string? current,
        bool allowNone)
    {
        if (!catalogs.Paints.TryGetValue(category, out SkyrimRaceMenuPaintChoiceResult? result))
            throw new InvalidOperationException(
                $"No reviewed {category.ToWireName()} paint catalog is available.");
        return new SkyrimRaceMenuPaintPickerViewModel(result, category, current, allowNone);
    }

    private ImmutableArray<SkyrimRaceMenuSliderDefinition> CatalogSliders()
    {
        if (catalogs.SliderCatalog is null) return [];
        SkyrimRaceMenuSliderGender gender = Sex == NpcSex.Female
            ? SkyrimRaceMenuSliderGender.Female
            : SkyrimRaceMenuSliderGender.Male;
        return catalogs.SliderCatalog.Sliders
            .Where(item => item.Gender == gender &&
                           item.Type != SkyrimRaceMenuSliderType.HeadPart &&
                           string.Equals(item.RaceEditorId, RaceEditorId,
                               StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
    }

    private void RefreshAll()
    {
        Parts.Refresh();
        NativeMorphs.Refresh();
        CustomMorphs.Refresh();
        Tints.Refresh();
        Sculpt.Refresh();
        FaceOverlays.Refresh();
    }

    private void Refresh(SkyrimFaceEditorSection section)
    {
        switch (section)
        {
            case SkyrimFaceEditorSection.FaceParts: Parts.Refresh(); break;
            case SkyrimFaceEditorSection.NativeMorphs: NativeMorphs.Refresh(); break;
            case SkyrimFaceEditorSection.CustomMorphs: CustomMorphs.Refresh(); break;
            case SkyrimFaceEditorSection.Tints: Tints.Refresh(); break;
            case SkyrimFaceEditorSection.Sculpt: Sculpt.Refresh(); break;
            case SkyrimFaceEditorSection.FaceOverlays: FaceOverlays.Refresh(); break;
            default: throw new ArgumentOutOfRangeException(nameof(section), section, null);
        }
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value, StringComparison.OrdinalIgnoreCase);

    private static void ValidateCatalogs(SkyrimFaceEditorCatalogs value)
    {
        if (value.TypedForms.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("Typed form choices contain an error diagnostic.", nameof(value));
        foreach ((NpcHeadPartType type, SkyrimHeadPartChoiceResult result) in value.HeadParts)
        {
            if (!result.Accepted || result.Diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error) ||
                result.Candidates.Any(item => item.Type != type))
            {
                throw new ArgumentException(
                    $"The {type.ToWireName()} head-part catalog is not an accepted typed catalog.",
                    nameof(value));
            }
        }
        foreach ((SkyrimRaceMenuPaintCategory category,
                 SkyrimRaceMenuPaintChoiceResult result) in value.Paints)
        {
            if (!result.Accepted || result.Diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error) ||
                result.Candidates.Any(item => item.Category != category))
            {
                throw new ArgumentException(
                    $"The {category.ToWireName()} paint catalog is not an accepted typed catalog.",
                    nameof(value));
            }
        }
        ImmutableArray<NpcHeadPartSelection> defaults = value.RaceDefaultHeadParts.IsDefault
            ? ImmutableArray<NpcHeadPartSelection>.Empty
            : value.RaceDefaultHeadParts;
        if (defaults.Any(item => item.Type == NpcHeadPartType.Misc) ||
            defaults.GroupBy(item => item.Type).Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Race-default display head parts must contain at most one non-Misc row per type.",
                nameof(value));
        }
    }

    private static string SectionName(SkyrimFaceEditorSection section) => section switch
    {
        SkyrimFaceEditorSection.FaceParts => "Face parts",
        SkyrimFaceEditorSection.NativeMorphs => "Native morphs",
        SkyrimFaceEditorSection.CustomMorphs => "RaceMenu sliders",
        SkyrimFaceEditorSection.Tints => "Tints",
        SkyrimFaceEditorSection.Sculpt => "Sculpt inspection",
        SkyrimFaceEditorSection.FaceOverlays => "Face paint",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };
}
