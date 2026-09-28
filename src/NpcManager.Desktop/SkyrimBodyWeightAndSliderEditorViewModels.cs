using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimBodyWeightEditorViewModel : NotifyViewModel
{
    private readonly SkyrimBodyEditorViewModel owner;
    private bool refreshing;
    private float value;

    internal SkyrimBodyWeightEditorViewModel(SkyrimBodyEditorViewModel owner) => this.owner = owner;

    public float Value
    {
        get => value;
        set
        {
            float admitted = Math.Clamp(value, 0F, 100F);
            if (!Set(ref this.value, admitted) || refreshing) return;
            owner.Replace(SkyrimBodyEditorDocumentRules.SetWeight(owner.CurrentDocument, admitted),
                SkyrimBodyEditorSection.Weight, $"Skyrim NAM7 weight set to {admitted:0.##}.",
                refreshSection: false);
        }
    }

    public string Authority => $"NPC NAM7 {Value:0.##} only · mesh and body-provider consequences are not inferred";

    internal void Refresh()
    {
        refreshing = true;
        Value = owner.CurrentDocument.Weight;
        refreshing = false;
        Raise(nameof(Authority));
    }
}

public sealed class SkyrimBodySlideEditorViewModel : NotifyViewModel
{
    private readonly SkyrimBodyEditorViewModel owner;
    private readonly ImmutableArray<string> catalogNames;

    internal SkyrimBodySlideEditorViewModel(
        SkyrimBodyEditorViewModel owner,
        ImmutableArray<string> catalogNames)
    {
        this.owner = owner;
        this.catalogNames = catalogNames;
    }

    public ObservableCollection<SkyrimBodySlideRowViewModel> Rows { get; } = [];
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => catalogNames.IsEmpty
        ? "No reviewed BODYTRI or BodySlide slider catalog is available; preset-carried rows remain preserved."
        : "No BodySlide slider matches this filter.";
    public string CatalogStatus => catalogNames.IsEmpty
        ? "No reviewed slider catalog · no slider was fabricated"
        : $"{catalogNames.Length} reviewed catalog name(s) · preset-only names remain visible";

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (Set(ref filterText, value ?? string.Empty)) Refresh();
        }
    }

    internal void Refresh()
    {
        Rows.Clear();
        ImmutableArray<BodySlideSliderValue> values = SkyrimBodyEditorDocumentRules.FilterBodySlide(
            owner.CurrentDocument, catalogNames, FilterText);
        foreach (BodySlideSliderValue value in values)
        {
            bool catalogued = catalogNames.Any(item =>
                string.Equals(item, value.Name, StringComparison.OrdinalIgnoreCase));
            Rows.Add(new SkyrimBodySlideRowViewModel(value.Name, value.Value, catalogued, changed =>
                owner.Replace(SkyrimBodyEditorDocumentRules.SetBodySlideValue(
                        owner.CurrentDocument, value.Name, changed),
                    SkyrimBodyEditorSection.BodySlide,
                    $"BodySlide {value.Name} set to {changed * 100F:0.##}%.",
                    refreshSection: false)));
        }
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
        Raise(nameof(CatalogStatus));
    }
}

public sealed class SkyrimBodySlideRowViewModel : NotifyViewModel
{
    private readonly Action<float> changed;
    private float value;

    internal SkyrimBodySlideRowViewModel(
        string name,
        float value,
        bool catalogued,
        Action<float> changed)
    {
        Name = name;
        this.value = value;
        IsCatalogued = catalogued;
        this.changed = changed;
    }

    public string Name { get; }
    public bool IsCatalogued { get; }
    public string Authority => IsCatalogued ? "reviewed catalog" : "preset-carried";
    public float Percent
    {
        get => value * 100F;
        set
        {
            float admitted = Math.Clamp(value, 0F, 100F) / 100F;
            if (!Set(ref this.value, admitted, nameof(Percent))) return;
            changed(admitted);
        }
    }
}
