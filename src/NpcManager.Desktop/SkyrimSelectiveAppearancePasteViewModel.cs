using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimSelectiveAppearancePasteViewModel : NotifyViewModel
{
    private readonly SkyrimSelectiveAppearancePasteDocument source;
    private readonly SkyrimSelectiveAppearancePasteDocument target;

    public SkyrimSelectiveAppearancePasteViewModel(
        SkyrimSelectiveAppearancePasteDocument source,
        SkyrimSelectiveAppearancePasteDocument target,
        string sourceLabel,
        string targetLabel)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        this.source = source;
        this.target = target;
        SourceLabel = RequireLabel(sourceLabel, nameof(sourceLabel));
        TargetLabel = RequireLabel(targetLabel, nameof(targetLabel));

        Options =
        [
            Option(SkyrimAppearancePasteCategory.BodyWeight, "Body", "Body weight",
                "Skyrim NAM7 weight."),
            Option(SkyrimAppearancePasteCategory.BodyShape, "Body", "Body shape",
                "BodySlide values plus first- and third-person node transforms."),
            Option(SkyrimAppearancePasteCategory.RaceMenuPaintsAndSkin, "Body",
                "RaceMenu paints and skin",
                "Complete Face, Body, Hands, and Feet paints plus RaceMenu skin overrides."),
            Option(SkyrimAppearancePasteCategory.Outfits, "NPC record",
                "Default and sleeping outfits",
                "Typed DOFT and SOFT references; no armor records are duplicated."),
            Option(SkyrimAppearancePasteCategory.FaceParts, "Face",
                "Face parts and head texture",
                "Ordered head parts plus the face texture-set override."),
            Option(SkyrimAppearancePasteCategory.HairColor, "Face", "Hair color",
                "Typed HCLF color reference."),
            Option(SkyrimAppearancePasteCategory.FaceTints, "Face", "Face tints",
                "Ordered tint layers and their optional mask overrides."),
            Option(SkyrimAppearancePasteCategory.FaceMorphs, "Face", "Face morphs",
                "NAM9, NAMA, and RaceMenu custom morph values."),
            Option(SkyrimAppearancePasteCategory.Sculpt, "Face", "Per-vertex sculpt",
                "Complete bounded RaceMenu sculpt parts."),
            Option(SkyrimAppearancePasteCategory.CharGenFlag, "NPC record",
                "CharGen face preset flag",
                "NPC ACBS bit 0x04 only.")
        ];
        BodyOptions = Options.Where(item => item.Group == "Body").ToImmutableArray();
        FaceOptions = Options.Where(item => item.Group == "Face").ToImmutableArray();
        RecordOptions = Options.Where(item => item.Group == "NPC record").ToImmutableArray();
        RefreshSelectionState();
    }

    public string SourceLabel { get; }
    public string TargetLabel { get; }
    public ImmutableArray<SkyrimSelectiveAppearancePasteOptionViewModel> Options { get; }
    public ImmutableArray<SkyrimSelectiveAppearancePasteOptionViewModel> BodyOptions { get; }
    public ImmutableArray<SkyrimSelectiveAppearancePasteOptionViewModel> FaceOptions { get; }
    public ImmutableArray<SkyrimSelectiveAppearancePasteOptionViewModel> RecordOptions { get; }
    public SkyrimSelectiveAppearancePasteDocument? AcceptedDocument { get; private set; }
    public SkyrimSelectiveAppearancePasteSelection? AcceptedSelection { get; private set; }
    public bool IsAccepted { get; private set; }
    public bool HasAcceptedChanges { get; private set; }
    public int SelectedCount => Options.Count(item => item.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionSummary =>
        $"{SelectedCount} of {Options.Length} Skyrim categories selected";
    public string NoSelectionNotice => HasSelection
        ? "Unchecked categories remain exactly as they are on the target."
        : "Nothing is selected. Paste will close as a clean no-op.";
    public string AuthorityNotice =>
        $"Selection for {TargetLabel} only - the caller must review, write, and independently reopen both carriers; FaceGen, BodySlide-build, runtime, and visual authority remain false.";

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

    public void SelectAll() => SetAll(true);

    public void DeselectAll() => SetAll(false);

    public bool TryAccept()
    {
        ClearAccepted();
        SkyrimSelectiveAppearancePasteSelection selection = BuildSelection();
        SkyrimSelectiveAppearancePasteResult result =
            SkyrimSelectiveAppearancePasteRules.Merge(source, target, selection);
        if (!result.Accepted || result.Document is null)
        {
            ValidationMessage = result.Diagnostics.FirstOrDefault(
                    item => item.Severity == DiagnosticSeverity.Error)?.Message ??
                "Selective appearance paste was refused.";
            StatusMessage = string.Empty;
            return false;
        }

        AcceptedDocument = result.Document;
        AcceptedSelection = selection;
        HasAcceptedChanges = result.HasChanges;
        IsAccepted = true;
        ValidationMessage = string.Empty;
        StatusMessage = result.HasChanges
            ? $"Prepared {SelectedCount} selected categories for the caller."
            : "No categories were selected; the target remains unchanged.";
        return true;
    }

    public void Cancel()
    {
        ClearAccepted();
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    private SkyrimSelectiveAppearancePasteOptionViewModel Option(
        SkyrimAppearancePasteCategory category,
        string group,
        string title,
        string description) =>
        new(category, group, title, description, OnSelectionChanged);

    private SkyrimSelectiveAppearancePasteSelection BuildSelection() =>
        new(Options
            .Where(item => item.IsSelected)
            .Select(item => item.Category)
            .ToImmutableArray());

    private void SetAll(bool value)
    {
        foreach (SkyrimSelectiveAppearancePasteOptionViewModel option in Options)
            option.SetSelected(value, notifyOwner: false);
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        ClearAccepted();
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
        RefreshSelectionState();
    }

    private void RefreshSelectionState()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionSummary));
        Raise(nameof(NoSelectionNotice));
    }

    private void ClearAccepted()
    {
        IsAccepted = false;
        HasAcceptedChanges = false;
        AcceptedDocument = null;
        AcceptedSelection = null;
    }

    private static string RequireLabel(string value, string parameter)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException(
                "Appearance source and target labels must be 1-256 visible characters.",
                parameter);
        return normalized;
    }
}

public sealed class SkyrimSelectiveAppearancePasteOptionViewModel : NotifyViewModel
{
    private readonly Action ownerChanged;
    private bool isSelected = true;

    public SkyrimSelectiveAppearancePasteOptionViewModel(
        SkyrimAppearancePasteCategory category,
        string group,
        string title,
        string description,
        Action ownerChanged)
    {
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category));
        Category = category;
        Group = group;
        Title = title;
        Description = description;
        this.ownerChanged = ownerChanged ??
            throw new ArgumentNullException(nameof(ownerChanged));
    }

    public SkyrimAppearancePasteCategory Category { get; }
    public string Group { get; }
    public string Title { get; }
    public string Description { get; }

    public bool IsSelected
    {
        get => isSelected;
        set => SetSelected(value, notifyOwner: true);
    }

    internal void SetSelected(bool value, bool notifyOwner)
    {
        if (!Set(ref isSelected, value)) return;
        if (notifyOwner) ownerChanged();
    }
}
