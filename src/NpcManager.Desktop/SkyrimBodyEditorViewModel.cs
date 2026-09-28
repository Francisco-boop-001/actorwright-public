using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record SkyrimBodyEditorCatalogs(
    ImmutableArray<string> BodySlideNames,
    ImmutableArray<string> NodeNames,
    ImmutableDictionary<SkyrimRaceMenuPaintCategory, SkyrimRaceMenuPaintChoiceResult> Paints,
    ImmutableDictionary<BodyOverlayTarget, int> OverlaySlotLimits);

/// <summary>
/// Thin transaction coordinator for the five Skyrim body sections. Child view
/// models own presentation state; this type owns only the immutable baseline,
/// current document, validation, picker composition, and commit/cancel result.
/// </summary>
public sealed class SkyrimBodyEditorViewModel : NotifyViewModel
{
    private readonly SkyrimBodyEditorCatalogs catalogs;
    private readonly string title;
    private SkyrimBodyEditorDocument currentDocument;

    public SkyrimBodyEditorViewModel(
        SkyrimBodyEditorDocument baseline,
        string subject,
        SkyrimBodyEditorCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(catalogs);
        SkyrimBodyEditorValidationResult validation = SkyrimBodyEditorDocumentRules.Validate(baseline);
        if (!validation.Accepted)
            throw new ArgumentException(validation.Diagnostics[0].Message, nameof(baseline));
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl))
            throw new ArgumentException("A safe subject label is required.", nameof(subject));
        ValidateCatalogs(catalogs);

        BaselineDocument = baseline;
        currentDocument = baseline;
        Subject = subject.Trim();
        this.catalogs = catalogs;
        title = $"Edit Skyrim body — {Subject}";
        Weight = new SkyrimBodyWeightEditorViewModel(this);
        BodySlide = new SkyrimBodySlideEditorViewModel(this, catalogs.BodySlideNames);
        Transforms = new SkyrimBodyTransformEditorViewModel(this, catalogs.NodeNames);
        SkinOverrides = new SkyrimSkinOverrideEditorViewModel(this);
        BodyOverlays = new SkyrimBodyOverlayEditorViewModel(this, catalogs.OverlaySlotLimits);
        RefreshAll();
    }

    public string Title => title;
    public string Subject { get; }
    public string Context => $"{Subject} · immutable Skyrim body transaction";
    public string AuthorityNotice =>
        $"Static body document for {Subject} only · BodySlide build, texture rendering, runtime, and visual authority false";
    public SkyrimBodyEditorDocument BaselineDocument { get; }
    public SkyrimBodyEditorDocument CurrentDocument => currentDocument;
    public SkyrimBodyEditorDocument? AcceptedDocument { get; private set; }
    public bool IsDirty => !SkyrimBodyEditorDocumentRules.Equivalent(currentDocument, BaselineDocument);
    public bool IsAccepted => AcceptedDocument is not null;
    public SkyrimBodyWeightEditorViewModel Weight { get; }
    public SkyrimBodySlideEditorViewModel BodySlide { get; }
    public SkyrimBodyTransformEditorViewModel Transforms { get; }
    public SkyrimSkinOverrideEditorViewModel SkinOverrides { get; }
    public SkyrimBodyOverlayEditorViewModel BodyOverlays { get; }

    private SkyrimBodyEditorSection selectedSection;
    public SkyrimBodyEditorSection SelectedSection
    {
        get => selectedSection;
        set
        {
            if (!Set(ref selectedSection, value)) return;
            ValidationMessage = string.Empty;
        }
    }

    private string status = "Edit one section, reset it independently, or accept one complete body document.";
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        internal set => Set(ref validationMessage, value);
    }

    public void ResetSelectedSection()
    {
        SkyrimBodyEditorDocument reset = SkyrimBodyEditorDocumentRules.ResetSection(
            currentDocument, BaselineDocument, SelectedSection);
        string suffix = SelectedSection == SkyrimBodyEditorSection.BodySlide
            ? "cleared to zero using the pinned upstream behavior"
            : "restored to the pre-open baseline";
        Replace(reset, SelectedSection, $"{SectionName(SelectedSection)} {suffix}.");
    }

    public bool TryAccept()
    {
        SkyrimBodyEditorValidationResult validation = SkyrimBodyEditorDocumentRules.Validate(currentDocument);
        if (!validation.Accepted)
        {
            AcceptedDocument = null;
            ValidationMessage = validation.Diagnostics[0].Message;
            Status = "The body document was not accepted.";
            Raise(nameof(IsAccepted));
            return false;
        }
        AcceptedDocument = currentDocument;
        ValidationMessage = string.Empty;
        Status = "Complete typed body document accepted; no preset, plugin, mesh, or texture was written.";
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        AcceptedDocument = null;
        currentDocument = BaselineDocument;
        ValidationMessage = string.Empty;
        Status = "Body editing cancelled; the exact pre-open baseline was restored.";
        RefreshAll();
        Raise(nameof(CurrentDocument));
        Raise(nameof(IsDirty));
        Raise(nameof(IsAccepted));
    }

    public SkyrimRaceMenuPaintPickerViewModel CreateBodyPaintPicker(BodyOverlayTarget target)
    {
        SkyrimRaceMenuPaintCategory category = target switch
        {
            BodyOverlayTarget.Body => SkyrimRaceMenuPaintCategory.Body,
            BodyOverlayTarget.Hands => SkyrimRaceMenuPaintCategory.Hands,
            BodyOverlayTarget.Feet => SkyrimRaceMenuPaintCategory.Feet,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
        if (!catalogs.Paints.TryGetValue(category, out SkyrimRaceMenuPaintChoiceResult? result))
            throw new InvalidOperationException($"No reviewed {category.ToWireName()} paint catalog is available.");
        return new SkyrimRaceMenuPaintPickerViewModel(result, category, null, allowNone: false);
    }

    public bool ApplyBodyPaintPicker(
        BodyOverlayTarget target,
        SkyrimRaceMenuPaintPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted || picker.AcceptedEntry is not { } paint) return false;
        int limit = catalogs.OverlaySlotLimits[target];
        Replace(SkyrimBodyEditorDocumentRules.AddBodyOverlay(
                currentDocument, target, paint, limit),
            SkyrimBodyEditorSection.BodyOverlays,
            $"{target} paint added to the lowest free reviewed slot.");
        return true;
    }

    internal void Replace(
        SkyrimBodyEditorDocument document,
        SkyrimBodyEditorSection section,
        string message,
        bool refreshSection = true)
    {
        currentDocument = document;
        AcceptedDocument = null;
        ValidationMessage = string.Empty;
        Status = message;
        if (refreshSection) Refresh(section);
        Raise(nameof(CurrentDocument));
        Raise(nameof(IsDirty));
        Raise(nameof(IsAccepted));
    }

    private void RefreshAll()
    {
        Weight.Refresh();
        BodySlide.Refresh();
        Transforms.Refresh();
        SkinOverrides.Refresh();
        BodyOverlays.Refresh();
    }

    private void Refresh(SkyrimBodyEditorSection section)
    {
        switch (section)
        {
            case SkyrimBodyEditorSection.Weight: Weight.Refresh(); break;
            case SkyrimBodyEditorSection.BodySlide: BodySlide.Refresh(); break;
            case SkyrimBodyEditorSection.Transforms: Transforms.Refresh(); break;
            case SkyrimBodyEditorSection.SkinOverrides: SkinOverrides.Refresh(); break;
            case SkyrimBodyEditorSection.BodyOverlays: BodyOverlays.Refresh(); break;
            default: throw new ArgumentOutOfRangeException(nameof(section), section, null);
        }
    }

    private static void ValidateCatalogs(SkyrimBodyEditorCatalogs value)
    {
        if (value.BodySlideNames.IsDefault || value.NodeNames.IsDefault ||
            value.Paints is null || value.OverlaySlotLimits is null)
            throw new ArgumentException("Body-editor catalogs must be initialized.", nameof(value));
        if (value.BodySlideNames.Any(item => string.IsNullOrWhiteSpace(item)) ||
            value.NodeNames.Any(item => string.IsNullOrWhiteSpace(item)))
            throw new ArgumentException("Catalog names must be non-empty.", nameof(value));
        foreach (BodyOverlayTarget target in Enum.GetValues<BodyOverlayTarget>())
        {
            SkyrimRaceMenuPaintCategory category = target switch
            {
                BodyOverlayTarget.Body => SkyrimRaceMenuPaintCategory.Body,
                BodyOverlayTarget.Hands => SkyrimRaceMenuPaintCategory.Hands,
                BodyOverlayTarget.Feet => SkyrimRaceMenuPaintCategory.Feet,
                _ => throw new ArgumentOutOfRangeException(nameof(value))
            };
            if (!value.Paints.TryGetValue(category, out SkyrimRaceMenuPaintChoiceResult? paint) ||
                !paint.Accepted || paint.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ||
                paint.Candidates.Any(item => item.Category != category ||
                                             item.Sources.IsDefaultOrEmpty ||
                                             item.TextureSlots.IsDefault))
                throw new ArgumentException($"The {category.ToWireName()} paint catalog is not accepted.", nameof(value));
            if (!value.OverlaySlotLimits.TryGetValue(target, out int limit) ||
                limit is < 1 or > SkyrimBodyEditorDocumentRules.MaximumBodyOverlays)
                throw new ArgumentException($"The {target} overlay-slot limit is not reviewed.", nameof(value));
        }
    }

    private static string SectionName(SkyrimBodyEditorSection section) => section switch
    {
        SkyrimBodyEditorSection.Weight => "Weight",
        SkyrimBodyEditorSection.BodySlide => "BodySlide",
        SkyrimBodyEditorSection.Transforms => "Node transforms",
        SkyrimBodyEditorSection.SkinOverrides => "Skin overrides",
        SkyrimBodyEditorSection.BodyOverlays => "Body paint",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };
}
