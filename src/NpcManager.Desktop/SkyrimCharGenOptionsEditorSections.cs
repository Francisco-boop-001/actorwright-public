using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimCharGenTextureSectionViewModel : NotifyViewModel
{
    public IReadOnlyList<FaceGenChannelResolution> Resolutions { get; } =
        Enum.GetValues<FaceGenChannelResolution>();
    public IReadOnlyList<FaceGenDiffuseCompression> DiffuseCompressions { get; } =
        Enum.GetValues<FaceGenDiffuseCompression>();
    public IReadOnlyList<FaceGenNormalSpecularCompression> NormalCompressions { get; } =
        Enum.GetValues<FaceGenNormalSpecularCompression>();

    private bool perLayerResolution;
    public bool PerLayerResolution
    {
        get => perLayerResolution;
        set
        {
            if (!Set(ref perLayerResolution, value)) return;
            Raise(nameof(IsUniformResolution));
        }
    }

    public bool IsUniformResolution => !PerLayerResolution;

    private FaceGenChannelResolution diffuseResolution;
    public FaceGenChannelResolution DiffuseResolution
    {
        get => diffuseResolution;
        set => Set(ref diffuseResolution, value);
    }

    private FaceGenChannelResolution normalResolution;
    public FaceGenChannelResolution NormalResolution
    {
        get => normalResolution;
        set => Set(ref normalResolution, value);
    }

    private FaceGenDiffuseCompression diffuseCompression;
    public FaceGenDiffuseCompression DiffuseCompression
    {
        get => diffuseCompression;
        set => Set(ref diffuseCompression, value);
    }

    private FaceGenNormalSpecularCompression normalCompression;
    public FaceGenNormalSpecularCompression NormalCompression
    {
        get => normalCompression;
        set => Set(ref normalCompression, value);
    }

    private bool generateTga;
    public bool GenerateTga
    {
        get => generateTga;
        set => Set(ref generateTga, value);
    }

    private bool bakeSseRaceMenuOverlays;
    public bool BakeSseRaceMenuOverlays
    {
        get => bakeSseRaceMenuOverlays;
        set => Set(ref bakeSseRaceMenuOverlays, value);
    }

    internal void Load(CharGenOptions options)
    {
        perLayerResolution = options.PerLayerResolution;
        diffuseResolution = options.DiffuseResolution;
        normalResolution = options.NormalResolution;
        diffuseCompression = options.DiffuseCompression;
        normalCompression = options.NormalCompression;
        generateTga = options.GenerateTga;
        bakeSseRaceMenuOverlays = options.BakeSseRaceMenuOverlays;
        Raise(string.Empty);
    }
}

public sealed class SkyrimCharGenConventionSectionViewModel : NotifyViewModel
{
    public IReadOnlyList<FaceTintWorkingSpace> WorkingSpaces { get; } =
        Enum.GetValues<FaceTintWorkingSpace>();
    public IReadOnlyList<FaceTintMaskConversion> MaskConversions { get; } =
        Enum.GetValues<FaceTintMaskConversion>();
    public IReadOnlyList<FaceTintFramework> Frameworks { get; } =
        Enum.GetValues<FaceTintFramework>();
    public IReadOnlyList<FaceTintSoftLightModel> SoftLightModels { get; } =
        Enum.GetValues<FaceTintSoftLightModel>();

    private FaceTintWorkingSpace workingSpace;
    public FaceTintWorkingSpace WorkingSpace
    {
        get => workingSpace;
        set => Set(ref workingSpace, value);
    }

    private FaceTintWorkingSpace compositeSpace;
    public FaceTintWorkingSpace CompositeSpace
    {
        get => compositeSpace;
        set => Set(ref compositeSpace, value);
    }

    private FaceTintWorkingSpace sourceSpace;
    public FaceTintWorkingSpace SourceSpace
    {
        get => sourceSpace;
        set => Set(ref sourceSpace, value);
    }

    private FaceTintWorkingSpace outputSpace;
    public FaceTintWorkingSpace OutputSpace
    {
        get => outputSpace;
        set => Set(ref outputSpace, value);
    }

    private FaceTintMaskConversion maskConversion;
    public FaceTintMaskConversion MaskConversion
    {
        get => maskConversion;
        set => Set(ref maskConversion, value);
    }

    private FaceTintFramework framework;
    public FaceTintFramework Framework
    {
        get => framework;
        set => Set(ref framework, value);
    }

    private FaceTintSoftLightModel softLight;
    public FaceTintSoftLightModel SoftLight
    {
        get => softLight;
        set => Set(ref softLight, value);
    }

    private FaceTintWorkingSpace diffuseTextureSourceSpace;
    public FaceTintWorkingSpace DiffuseTextureSourceSpace
    {
        get => diffuseTextureSourceSpace;
        set => Set(ref diffuseTextureSourceSpace, value);
    }

    private bool seedDiffuseG22;
    public bool SeedDiffuseG22
    {
        get => seedDiffuseG22;
        set => Set(ref seedDiffuseG22, value);
    }

    private FaceTintWorkingSpace replaceSpace;
    public FaceTintWorkingSpace ReplaceSpace
    {
        get => replaceSpace;
        set => Set(ref replaceSpace, value);
    }

    private FaceTintWorkingSpace multiplySpace;
    public FaceTintWorkingSpace MultiplySpace
    {
        get => multiplySpace;
        set => Set(ref multiplySpace, value);
    }

    private FaceTintWorkingSpace overlaySpace;
    public FaceTintWorkingSpace OverlaySpace
    {
        get => overlaySpace;
        set => Set(ref overlaySpace, value);
    }

    private FaceTintWorkingSpace softLightSpace;
    public FaceTintWorkingSpace SoftLightSpace
    {
        get => softLightSpace;
        set => Set(ref softLightSpace, value);
    }

    private FaceTintWorkingSpace hardLightSpace;
    public FaceTintWorkingSpace HardLightSpace
    {
        get => hardLightSpace;
        set => Set(ref hardLightSpace, value);
    }

    internal void Load(FaceTintConventionSettings settings)
    {
        FaceTintBucketConvention diffuse = settings.Diffuse;
        FaceTintBlendWorkingSpaces spaces =
            settings.DiffuseWorkingSpaceByBlend;
        workingSpace = diffuse.WorkingSpace;
        compositeSpace = diffuse.CompositeSpace;
        sourceSpace = diffuse.SourceSpace;
        outputSpace = diffuse.OutputSpace;
        maskConversion = diffuse.MaskConversion;
        framework = diffuse.Framework;
        softLight = diffuse.SoftLight;
        diffuseTextureSourceSpace = settings.DiffuseTextureSourceSpace;
        seedDiffuseG22 = settings.SeedDiffuseG22;
        replaceSpace = spaces.Replace;
        multiplySpace = spaces.Multiply;
        overlaySpace = spaces.Overlay;
        softLightSpace = spaces.SoftLight;
        hardLightSpace = spaces.HardLight;
        Raise(string.Empty);
    }
}

public sealed record SkyrimCharGenSortRuleViewModel(
    int Key,
    string KeyName,
    bool Descending)
{
    public string Direction => Descending ? "Descending" : "Ascending";
}

public sealed class SkyrimCharGenSortSectionViewModel : NotifyViewModel
{
    public IReadOnlyList<FaceTintSseSortKey> TintKeys { get; } =
        Enum.GetValues<FaceTintSseSortKey>();
    public IReadOnlyList<FaceTintSseOverlaySortKey> OverlayKeys { get; } =
        Enum.GetValues<FaceTintSseOverlaySortKey>();
    public IReadOnlyList<FaceTintSkinTonePlacement> SkinTonePlacements { get; } =
        Enum.GetValues<FaceTintSkinTonePlacement>();
    public ObservableCollection<SkyrimCharGenSortRuleViewModel> TintRules { get; } = [];
    public ObservableCollection<SkyrimCharGenSortRuleViewModel> OverlayRules { get; } = [];

    private FaceTintSseSortKey tintCandidateKey;
    public FaceTintSseSortKey TintCandidateKey
    {
        get => tintCandidateKey;
        set => Set(ref tintCandidateKey, value);
    }

    private bool tintCandidateDescending;
    public bool TintCandidateDescending
    {
        get => tintCandidateDescending;
        set => Set(ref tintCandidateDescending, value);
    }

    private FaceTintSseOverlaySortKey overlayCandidateKey;
    public FaceTintSseOverlaySortKey OverlayCandidateKey
    {
        get => overlayCandidateKey;
        set => Set(ref overlayCandidateKey, value);
    }

    private bool overlayCandidateDescending;
    public bool OverlayCandidateDescending
    {
        get => overlayCandidateDescending;
        set => Set(ref overlayCandidateDescending, value);
    }

    private int selectedTintIndex = -1;
    public int SelectedTintIndex
    {
        get => selectedTintIndex;
        set => Set(ref selectedTintIndex, value);
    }

    private int selectedOverlayIndex = -1;
    public int SelectedOverlayIndex
    {
        get => selectedOverlayIndex;
        set => Set(ref selectedOverlayIndex, value);
    }

    private FaceTintSkinTonePlacement skinTonePlacement;
    public FaceTintSkinTonePlacement SkinTonePlacement
    {
        get => skinTonePlacement;
        set => Set(ref skinTonePlacement, value);
    }

    internal void Load(FaceTintSortSettings settings)
    {
        TintRules.Clear();
        foreach (FaceTintSortRule rule in settings.TintRules)
            TintRules.Add(new SkyrimCharGenSortRuleViewModel(
                rule.Key,
                Enum.GetName(typeof(FaceTintSseSortKey), rule.Key) ?? "?",
                rule.Descending));
        OverlayRules.Clear();
        foreach (FaceTintSortRule rule in settings.SwapRules)
            OverlayRules.Add(new SkyrimCharGenSortRuleViewModel(
                rule.Key,
                Enum.GetName(typeof(FaceTintSseOverlaySortKey), rule.Key) ?? "?",
                rule.Descending));
        selectedTintIndex = -1;
        selectedOverlayIndex = -1;
        skinTonePlacement = settings.SkinTonePlacement;
        Raise(string.Empty);
    }
}
