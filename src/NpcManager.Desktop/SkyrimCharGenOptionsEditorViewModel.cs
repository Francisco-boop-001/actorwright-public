using System.Collections.Immutable;
using System.Collections.Specialized;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimCharGenOptionsEditorViewModel : NotifyViewModel
{
    private readonly CharGenOptions openingOptions;
    private FaceTintBucketConvention preservedNormalSpecular;
    private FaceTintBucketConvention preservedSwap;
    private FaceTintSeedMode preservedSeedMode;
    private ImmutableArray<double> preservedSeedConstant;
    private bool loading;

    public SkyrimCharGenOptionsEditorViewModel(CharGenOptions openingOptions)
    {
        ArgumentNullException.ThrowIfNull(openingOptions);
        SkyrimCharGenOptionsEditorResult admitted =
            SkyrimCharGenOptionsEditorRules.Save(openingOptions);
        if (!admitted.Accepted)
            throw new ArgumentException(
                FirstError(admitted.Diagnostics),
                nameof(openingOptions));
        this.openingOptions = openingOptions;
        preservedNormalSpecular = openingOptions.Convention.NormalSpecular;
        preservedSwap = openingOptions.Convention.Swap;
        preservedSeedMode = openingOptions.Convention.SeedMode;
        preservedSeedConstant = openingOptions.Convention.SeedConstant;
        Texture = new SkyrimCharGenTextureSectionViewModel();
        Convention = new SkyrimCharGenConventionSectionViewModel();
        Sort = new SkyrimCharGenSortSectionViewModel();
        Texture.PropertyChanged += (_, _) => OnTextureChanged();
        Convention.PropertyChanged += (_, _) => RefreshValidation();
        Sort.PropertyChanged += (_, _) => RefreshValidation();
        Sort.TintRules.CollectionChanged += OnRulesChanged;
        Sort.OverlayRules.CollectionChanged += OnRulesChanged;
        Load(openingOptions);
    }

    public SkyrimCharGenTextureSectionViewModel Texture { get; }
    public SkyrimCharGenConventionSectionViewModel Convention { get; }
    public SkyrimCharGenSortSectionViewModel Sort { get; }
    public CharGenOptions? AcceptedOptions { get; private set; }
    public bool IsAccepted { get; private set; }
    public string AuthorityNotice { get; } =
        "Save returns one complete in-memory Skyrim options document. Persistence, bake consumption, generated pixels, runtime rendering, and appearance authority require separate evidence.";

    private bool canSave;
    public bool CanSave
    {
        get => canSave;
        private set => Set(ref canSave, value);
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

    public bool TryAddSortRule(SkyrimCharGenSortList list)
    {
        CharGenOptions current = BuildDocument();
        FaceTintSortRule rule = list == SkyrimCharGenSortList.Tint
            ? new FaceTintSortRule(
                (int)Sort.TintCandidateKey,
                Sort.TintCandidateDescending)
            : new FaceTintSortRule(
                (int)Sort.OverlayCandidateKey,
                Sort.OverlayCandidateDescending);
        SkyrimCharGenOptionsEditorResult result =
            SkyrimCharGenOptionsEditorRules.AddSortRule(
                current,
                list,
                rule);
        return ApplySort(result, list, "Added one Skyrim sort rule.");
    }

    public bool TryRemoveSortRule(SkyrimCharGenSortList list)
    {
        int index = SelectedIndex(list);
        SkyrimCharGenOptionsEditorResult result =
            SkyrimCharGenOptionsEditorRules.RemoveSortRule(
                BuildDocument(),
                list,
                index);
        if (!ApplySort(result, list, "Removed one Skyrim sort rule."))
            return false;
        SetSelectedIndex(list, Math.Min(index, RuleCount(list) - 1));
        return true;
    }

    public bool TryMoveSortRule(SkyrimCharGenSortList list, int delta)
    {
        int index = SelectedIndex(list);
        int target = index + delta;
        SkyrimCharGenOptionsEditorResult result =
            SkyrimCharGenOptionsEditorRules.MoveSortRule(
                BuildDocument(),
                list,
                index,
                target);
        if (!ApplySort(result, list, "Moved one Skyrim sort rule."))
            return false;
        SetSelectedIndex(list, target);
        return true;
    }

    public void ResetTexture()
    {
        CharGenOptions reset = SkyrimCharGenOptionsEditorRules.ResetTexture(
            BuildDocument());
        LoadTexture(reset);
        StatusMessage = "Texture output settings reset to Skyrim defaults.";
    }

    public void ResetConvention()
    {
        CharGenOptions reset =
            SkyrimCharGenOptionsEditorRules.ResetConvention(BuildDocument());
        LoadConvention(reset.Convention);
        StatusMessage = "FaceTint conventions reset to Skyrim defaults.";
    }

    public void ResetSort()
    {
        CharGenOptions reset =
            SkyrimCharGenOptionsEditorRules.ResetSort(BuildDocument());
        LoadSort(reset.TintSort);
        StatusMessage = "Tint and overlay ordering reset to Skyrim defaults.";
    }

    public bool TrySave()
    {
        SkyrimCharGenOptionsEditorResult result =
            SkyrimCharGenOptionsEditorRules.Save(BuildDocument());
        if (!result.Accepted || result.Options is null)
        {
            ValidationMessage = FirstError(result.Diagnostics);
            return false;
        }
        AcceptedOptions = result.Options;
        IsAccepted = true;
        ValidationMessage = string.Empty;
        StatusMessage =
            "Complete Skyrim CharGen options accepted in memory; no file was written.";
        Raise(nameof(AcceptedOptions));
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        _ = SkyrimCharGenOptionsEditorRules.Cancel();
        AcceptedOptions = null;
        IsAccepted = false;
        Load(openingOptions);
        StatusMessage =
            "CharGen options cancelled; the exact opening document was restored.";
        Raise(nameof(AcceptedOptions));
        Raise(nameof(IsAccepted));
    }

    private void Load(CharGenOptions options)
    {
        loading = true;
        try
        {
            preservedNormalSpecular = options.Convention.NormalSpecular;
            preservedSwap = options.Convention.Swap;
            preservedSeedMode = options.Convention.SeedMode;
            preservedSeedConstant = options.Convention.SeedConstant;
            Texture.Load(options);
            Convention.Load(options.Convention);
            Sort.Load(options.TintSort);
        }
        finally { loading = false; }
        RefreshValidation();
    }

    private void LoadTexture(CharGenOptions options)
    {
        loading = true;
        try { Texture.Load(options); }
        finally { loading = false; }
        RefreshValidation();
    }

    private void LoadConvention(FaceTintConventionSettings settings)
    {
        loading = true;
        try
        {
            preservedNormalSpecular = settings.NormalSpecular;
            preservedSwap = settings.Swap;
            preservedSeedMode = settings.SeedMode;
            preservedSeedConstant = settings.SeedConstant;
            Convention.Load(settings);
        }
        finally { loading = false; }
        RefreshValidation();
    }

    private void LoadSort(FaceTintSortSettings settings)
    {
        loading = true;
        try { Sort.Load(settings); }
        finally { loading = false; }
        RefreshValidation();
    }

    private void OnTextureChanged()
    {
        if (loading) return;
        if (!Texture.PerLayerResolution)
        {
            loading = true;
            try
            {
                Texture.NormalResolution = Texture.DiffuseResolution;
                Texture.NormalCompression =
                    FaceGenNormalSpecularCompression.Uncompressed;
            }
            finally { loading = false; }
        }
        RefreshValidation();
    }

    private void OnRulesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        if (!loading) RefreshValidation();
    }

    private bool ApplySort(
        SkyrimCharGenOptionsEditorResult result,
        SkyrimCharGenSortList list,
        string success)
    {
        if (!result.Accepted || result.Options is null)
        {
            ValidationMessage = FirstError(result.Diagnostics);
            StatusMessage = string.Empty;
            return false;
        }
        LoadSort(result.Options.TintSort);
        SetSelectedIndex(list, RuleCount(list) - 1);
        StatusMessage = success;
        return true;
    }

    private void RefreshValidation()
    {
        if (loading) return;
        AcceptedOptions = null;
        IsAccepted = false;
        SkyrimCharGenOptionsEditorResult result =
            SkyrimCharGenOptionsEditorRules.Save(BuildDocument());
        CanSave = result.Accepted;
        ValidationMessage = result.Accepted
            ? string.Empty
            : FirstError(result.Diagnostics);
        Raise(nameof(AcceptedOptions));
        Raise(nameof(IsAccepted));
    }

    private CharGenOptions BuildDocument()
    {
        var diffuse = new FaceTintBucketConvention(
            Convention.WorkingSpace,
            Convention.CompositeSpace,
            Convention.SourceSpace,
            Convention.OutputSpace,
            Convention.MaskConversion,
            Convention.Framework,
            Convention.SoftLight,
            FaceTintMaskChannel.R);
        var convention = new FaceTintConventionSettings(
            diffuse,
            preservedNormalSpecular,
            preservedSwap,
            new FaceTintBlendWorkingSpaces(
                Convention.ReplaceSpace,
                Convention.MultiplySpace,
                Convention.OverlaySpace,
                Convention.SoftLightSpace,
                Convention.HardLightSpace),
            Convention.DiffuseTextureSourceSpace,
            Convention.SeedDiffuseG22,
            preservedSeedMode,
            preservedSeedConstant);
        var sort = new FaceTintSortSettings(
            Sort.TintRules.Select(item =>
                    new FaceTintSortRule(item.Key, item.Descending))
                .ToImmutableArray(),
            Sort.OverlayRules.Select(item =>
                    new FaceTintSortRule(item.Key, item.Descending))
                .ToImmutableArray(),
            Sort.SkinTonePlacement);
        CharGenOptions document = openingOptions with
        {
            PerLayerResolution = Texture.PerLayerResolution,
            DiffuseResolution = Texture.DiffuseResolution,
            NormalResolution = Texture.NormalResolution,
            DiffuseCompression = Texture.DiffuseCompression,
            NormalCompression = Texture.NormalCompression,
            GenerateTga = Texture.GenerateTga,
            BakeSseRaceMenuOverlays = Texture.BakeSseRaceMenuOverlays,
            Convention = convention,
            TintSort = sort
        };
        return SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(document);
    }

    private int SelectedIndex(SkyrimCharGenSortList list) =>
        list == SkyrimCharGenSortList.Tint
            ? Sort.SelectedTintIndex
            : Sort.SelectedOverlayIndex;

    private int RuleCount(SkyrimCharGenSortList list) =>
        list == SkyrimCharGenSortList.Tint
            ? Sort.TintRules.Count
            : Sort.OverlayRules.Count;

    private void SetSelectedIndex(SkyrimCharGenSortList list, int value)
    {
        if (list == SkyrimCharGenSortList.Tint)
            Sort.SelectedTintIndex = value;
        else
            Sort.SelectedOverlayIndex = value;
    }

    private static string FirstError(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(item =>
            item.Severity == DiagnosticSeverity.Error)?.Message ??
        "The Skyrim CharGen options document was refused.";
}
