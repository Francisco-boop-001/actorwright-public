using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimNativeFaceMorphEditorViewModel : NotifyViewModel
{
    private static readonly string[] SliderNames =
    [
        "Nose length", "Nose up / down", "Jaw up / down", "Jaw narrow / wide",
        "Jaw forward / back", "Cheeks up / down", "Cheeks forward / back",
        "Eyes up / down", "Eyes in / out", "Brows up / down", "Brows in / out",
        "Brows forward / back", "Lips up / down", "Lips in / out",
        "Chin narrow / wide", "Chin up / down", "Chin underbite / overbite",
        "Eyes forward / back"
    ];
    private static readonly string[] FamilyNames = ["Nose", "Unknown", "Eyes", "Mouth"];
    private readonly SkyrimFaceEditorViewModel owner;
    private bool refreshing;

    internal SkyrimNativeFaceMorphEditorViewModel(SkyrimFaceEditorViewModel owner)
    {
        this.owner = owner;
        var options = new List<SkyrimNativeMorphFamilyOption>
        {
            new(SkyrimFaceEditorDocumentRules.NamaUnset, "Default (preserve unset)"),
            new(0, "Type 0 (explicit default)")
        };
        options.AddRange(Enumerable.Range(1, 15)
            .Select(value => new SkyrimNativeMorphFamilyOption((uint)value, $"Type {value}")));
        FamilyOptions = options.ToImmutableArray();
    }

    public ObservableCollection<SkyrimNativeMorphSliderRowViewModel> Sliders { get; } = [];
    public ObservableCollection<SkyrimNativeMorphFamilyRowViewModel> Families { get; } = [];
    public ImmutableArray<SkyrimNativeMorphFamilyOption> FamilyOptions { get; }
    public string TrailingAuthority =>
        $"Preserved NAM9 trailing value: {owner.CurrentDocument.NativeMorphs.Nam9Trailing:G9}";

    internal void Refresh()
    {
        refreshing = true;
        try
        {
            Sliders.Clear();
            for (int index = 0; index < SliderNames.Length; index++)
            {
                int captured = index;
                Sliders.Add(new SkyrimNativeMorphSliderRowViewModel(
                    SliderNames[index],
                    owner.CurrentDocument.NativeMorphs.Nam9Sliders[index],
                    value => SetSlider(captured, value)));
            }
            Families.Clear();
            for (int index = 0; index < FamilyNames.Length; index++)
            {
                int captured = index;
                uint value = owner.CurrentDocument.NativeMorphs.NamaValues[index];
                Families.Add(new SkyrimNativeMorphFamilyRowViewModel(
                    FamilyNames[index], FamilyOptions,
                    FamilyOptions.Single(item => item.Value == value),
                    option => SetFamily(captured, option.Value)));
            }
            Raise(nameof(TrailingAuthority));
        }
        finally
        {
            refreshing = false;
        }
    }

    private void SetSlider(int index, float value)
    {
        if (refreshing) return;
        owner.Replace(
            SkyrimFaceEditorDocumentRules.SetNativeSlider(owner.CurrentDocument, index, value),
            SkyrimFaceEditorSection.NativeMorphs,
            $"Native morph {SliderNames[index]} updated.",
            refreshSection: false);
    }

    private void SetFamily(int index, uint value)
    {
        if (refreshing) return;
        owner.Replace(
            SkyrimFaceEditorDocumentRules.SetNativeFamily(owner.CurrentDocument, index, value),
            SkyrimFaceEditorSection.NativeMorphs,
            $"Native {FamilyNames[index]} family updated.",
            refreshSection: false);
    }
}

public sealed class SkyrimNativeMorphSliderRowViewModel : NotifyViewModel
{
    private readonly Action<float> changed;

    internal SkyrimNativeMorphSliderRowViewModel(
        string name,
        float value,
        Action<float> changed)
    {
        Name = name;
        this.value = value;
        this.changed = changed;
    }

    public string Name { get; }
    private float value;
    public float Value
    {
        get => value;
        set
        {
            float bounded = Math.Clamp(value, -1F, 1F);
            if (!Set(ref this.value, bounded)) return;
            changed(bounded);
        }
    }
}

public sealed record SkyrimNativeMorphFamilyOption(uint Value, string Label);

public sealed class SkyrimNativeMorphFamilyRowViewModel : NotifyViewModel
{
    private readonly Action<SkyrimNativeMorphFamilyOption> changed;

    internal SkyrimNativeMorphFamilyRowViewModel(
        string name,
        ImmutableArray<SkyrimNativeMorphFamilyOption> options,
        SkyrimNativeMorphFamilyOption selected,
        Action<SkyrimNativeMorphFamilyOption> changed)
    {
        Name = name;
        Options = options;
        this.selected = selected;
        this.changed = changed;
    }

    public string Name { get; }
    public ImmutableArray<SkyrimNativeMorphFamilyOption> Options { get; }
    private SkyrimNativeMorphFamilyOption selected;
    public SkyrimNativeMorphFamilyOption Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            changed(value);
        }
    }
}

public sealed class SkyrimCustomFaceMorphEditorViewModel : NotifyViewModel
{
    private const int MaximumPresetChoices = 1024;
    private readonly SkyrimFaceEditorViewModel owner;
    private readonly ImmutableArray<SkyrimRaceMenuSliderDefinition> definitions;

    internal SkyrimCustomFaceMorphEditorViewModel(
        SkyrimFaceEditorViewModel owner,
        ImmutableArray<SkyrimRaceMenuSliderDefinition> definitions)
    {
        this.owner = owner;
        this.definitions = definitions.IsDefault
            ? ImmutableArray<SkyrimRaceMenuSliderDefinition>.Empty
            : definitions;
        if (this.definitions.Any(item =>
                string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256 ||
                item.Name.Any(char.IsControl) || !Enum.IsDefined(item.Type) ||
                item.PresetCount is < 0 or > MaximumPresetChoices))
        {
            throw new ArgumentException(
                $"RaceMenu slider definitions must be named, typed, and contain at most {MaximumPresetChoices} preset choices.",
                nameof(definitions));
        }
    }

    public ObservableCollection<SkyrimCustomMorphRowViewModel> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => definitions.IsDefaultOrEmpty
        ? "No RaceMenu slider config covers this race, and the face carries no custom morphs."
        : "The catalog is loaded, but this race has no editable non-headpart slider rows.";
    public string CatalogStatus =>
        $"{definitions.Length} installed definition(s) · uncatalogued preset rows remain editable";

    internal void Refresh()
    {
        Rows.Clear();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimRaceMenuSliderDefinition definition in definitions)
        {
            if (!names.Add(definition.Name)) continue;
            float value = CurrentValue(definition.Name);
            (float minimum, float maximum) = Bounds(definition);
            Rows.Add(Row(definition.Name, definition.Category.ToString(), value,
                minimum, maximum, catalogued: true,
                isPreset: definition.Type == SkyrimRaceMenuSliderType.Preset));
        }
        foreach (SkyrimRaceMenuCustomMorphValue value in owner.CurrentDocument.CustomMorphs
                     .Where(item => names.Add(item.Name))
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(Row(value.Name, "Uncatalogued", value.Value, -1F, 1F,
                catalogued: false, isPreset: false));
        }
        Raise(nameof(HasRows));
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
        Raise(nameof(CatalogStatus));
    }

    private SkyrimCustomMorphRowViewModel Row(
        string name,
        string category,
        float value,
        float minimum,
        float maximum,
        bool catalogued,
        bool isPreset) => new(
        name, category, value, minimum, maximum, catalogued, isPreset,
        changed =>
        {
            bool removed = Math.Abs(changed) <
                           SkyrimFaceEditorDocumentRules.CustomMorphZeroEpsilon;
            owner.Replace(
                SkyrimFaceEditorDocumentRules.SetCustomMorph(
                    owner.CurrentDocument, name, changed, minimum, maximum),
                SkyrimFaceEditorSection.CustomMorphs,
                removed
                    ? $"RaceMenu morph {name} removed at zero."
                    : $"RaceMenu morph {name} updated.",
                refreshSection: removed);
        });

    private float CurrentValue(string name) => owner.CurrentDocument.CustomMorphs
        .FirstOrDefault(item => string.Equals(item.Name, name,
            StringComparison.OrdinalIgnoreCase))?.Value ?? 0F;

    private static (float Minimum, float Maximum) Bounds(
        SkyrimRaceMenuSliderDefinition definition)
    {
        if (definition.Type == SkyrimRaceMenuSliderType.Preset)
            return (0F, definition.PresetCount);
        float minimum = string.IsNullOrEmpty(definition.LowerBound) ? 0F : -1F;
        float maximum = string.IsNullOrEmpty(definition.UpperBound) ? 0F : 1F;
        if (minimum == 0F && maximum == 0F) maximum = 1F;
        return (minimum, maximum);
    }
}

public sealed class SkyrimCustomMorphRowViewModel : NotifyViewModel
{
    private readonly Action<float> changed;

    internal SkyrimCustomMorphRowViewModel(
        string name,
        string category,
        float value,
        float minimum,
        float maximum,
        bool catalogued,
        bool isPreset,
        Action<float> changed)
    {
        Name = name;
        Category = category;
        this.value = value;
        Minimum = minimum;
        Maximum = maximum;
        IsCatalogued = catalogued;
        IsPreset = isPreset;
        IsSlider = !isPreset;
        PresetOptions = isPreset
            ? Enumerable.Range(0, checked((int)maximum + 1))
                .Select(item => new SkyrimCustomMorphPresetOption(item,
                    item == 0 ? "Default" : $"Preset {item}"))
                .ToImmutableArray()
            : [];
        selectedPreset = IsPreset
            ? PresetOptions[Math.Clamp((int)Math.Truncate(value), 0,
                PresetOptions.Length - 1)]
            : null;
        this.changed = changed;
    }

    public string Name { get; }
    public string Category { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public bool IsCatalogued { get; }
    public bool IsPreset { get; }
    public bool IsSlider { get; }
    public ImmutableArray<SkyrimCustomMorphPresetOption> PresetOptions { get; }
    public string Authority => IsCatalogued ? "Installed slider definition" : "Preset-carried direct morph";
    private float value;
    public float Value
    {
        get => value;
        set
        {
            float bounded = Math.Clamp(value, Minimum, Maximum);
            if (!Set(ref this.value, bounded)) return;
            changed(bounded);
        }
    }

    private SkyrimCustomMorphPresetOption? selectedPreset;
    public SkyrimCustomMorphPresetOption? SelectedPreset
    {
        get => selectedPreset;
        set
        {
            if (!Set(ref selectedPreset, value) || value is null) return;
            Value = value.Value;
        }
    }
}

public sealed record SkyrimCustomMorphPresetOption(int Value, string Label);
