using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimArmorAddonIdentitySectionViewModel : NotifyViewModel
{
    private string editorIdText = string.Empty;

    public SkyrimArmorAddonEditorIntent Intent { get; private set; }
    public ArmorAddonProposalMode Mode { get; private set; }
    public WorkspacePath SourcePlugin { get; private set; }
    public FormId SourceFormId { get; private set; }
    public FormId? TargetFormId { get; private set; }
    public PluginName? TargetPlugin { get; private set; }
    public bool SeedFromSource { get; private set; }
    public bool CanEditEditorId => Mode == ArmorAddonProposalMode.New;
    public string EditorIdText
    {
        get => editorIdText;
        set => Set(ref editorIdText, value ?? string.Empty);
    }
    public string IntentLabel => Intent switch
    {
        SkyrimArmorAddonEditorIntent.BlankNew => "New blank ARMA",
        SkyrimArmorAddonEditorIntent.NewFromTemplate =>
            "New ARMA from template",
        SkyrimArmorAddonEditorIntent.OverrideExisting =>
            "Override existing ARMA",
        SkyrimArmorAddonEditorIntent.EditAuthored =>
            "Continue authored ARMA",
        _ => "Invalid intent"
    };
    public string SourceIdentity =>
        $"{Path.GetFileName(SourcePlugin.Value)}|{SourceFormId}";
    public string TargetIdentity => Mode == ArmorAddonProposalMode.New
        ? $"{TargetPlugin?.Value ?? "missing plugin"}|{TargetFormId}"
        : SourceIdentity;

    public SkyrimArmorAddonIdentitySectionViewModel(
        SkyrimArmorAddonEditorDocument document) => Load(document);

    public void Load(SkyrimArmorAddonEditorDocument document)
    {
        Intent = document.Intent;
        Mode = document.Mode;
        SourcePlugin = document.SourcePlugin;
        SourceFormId = document.SourceFormId;
        TargetFormId = document.TargetFormId;
        TargetPlugin = document.TargetPlugin;
        SeedFromSource = document.SeedFromSource;
        editorIdText = document.EditorId.Value;
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorAddonModelSectionViewModel : NotifyViewModel
{
    private string maleModel = string.Empty;
    private string femaleModel = string.Empty;
    private string maleFirstPersonModel = string.Empty;
    private string femaleFirstPersonModel = string.Empty;

    public string MaleModel
    {
        get => maleModel;
        set => Set(ref maleModel, value ?? string.Empty);
    }
    public string FemaleModel
    {
        get => femaleModel;
        set => Set(ref femaleModel, value ?? string.Empty);
    }
    public string MaleFirstPersonModel
    {
        get => maleFirstPersonModel;
        set => Set(ref maleFirstPersonModel, value ?? string.Empty);
    }
    public string FemaleFirstPersonModel
    {
        get => femaleFirstPersonModel;
        set => Set(ref femaleFirstPersonModel, value ?? string.Empty);
    }

    public void Load(SkyrimArmorAddonEditorDocument document)
    {
        maleModel = document.MaleModel ?? string.Empty;
        femaleModel = document.FemaleModel ?? string.Empty;
        maleFirstPersonModel = document.MaleFirstPersonModel ?? string.Empty;
        femaleFirstPersonModel = document.FemaleFirstPersonModel ?? string.Empty;
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorAddonSlotItem : NotifyViewModel
{
    private bool isSelected;

    public SkyrimArmorAddonSlotItem(int slotNumber, string name)
    {
        SlotNumber = slotNumber;
        Name = name;
        Mask = 1u << (slotNumber - 30);
    }

    public int SlotNumber { get; }
    public string Name { get; }
    public string Label => $"{SlotNumber} — {Name}";
    public uint Mask { get; }
    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }
}

public sealed class SkyrimArmorAddonSlotSectionViewModel : NotifyViewModel
{
    public SkyrimArmorAddonSlotSectionViewModel()
    {
        for (int slot = 30; slot <= 61; slot++)
        {
            var item = new SkyrimArmorAddonSlotItem(slot, SlotName(slot));
            item.PropertyChanged += (_, _) => Raise(nameof(SelectedMask));
            Slots.Add(item);
        }
    }

    public ObservableCollection<SkyrimArmorAddonSlotItem> Slots { get; } = [];
    public uint SelectedMask => Slots.Aggregate(
        0u, (mask, item) => item.IsSelected ? mask | item.Mask : mask);
    public string MaskText => $"0x{SelectedMask:X8}";

    public void Load(uint mask)
    {
        foreach (SkyrimArmorAddonSlotItem item in Slots)
            item.IsSelected = (mask & item.Mask) != 0;
        Raise(nameof(SelectedMask));
        Raise(nameof(MaskText));
    }

    private static string SlotName(int slot) => slot switch
    {
        30 => "Head",
        31 => "Hair",
        32 => "Body",
        33 => "Hands",
        34 => "Forearms",
        35 => "Amulet",
        36 => "Ring",
        37 => "Feet",
        38 => "Calves",
        39 => "Shield",
        40 => "Tail",
        41 => "Long hair",
        42 => "Circlet",
        43 => "Ears",
        50 => "Decapitated head",
        51 => "Decapitate",
        61 => "FX01",
        _ => "Reserved"
    };
}

public sealed class SkyrimArmorAddonRaceAndSkinSectionViewModel : NotifyViewModel
{
    private string race = string.Empty;
    private string maleSkinTexture = string.Empty;
    private string femaleSkinTexture = string.Empty;
    private string maleSkinTextureSwapList = string.Empty;
    private string femaleSkinTextureSwapList = string.Empty;
    private string footstepSet = string.Empty;
    private string artObject = string.Empty;
    private string candidateAdditionalRace = string.Empty;
    private int selectedAdditionalRaceIndex = -1;

    public ObservableCollection<string> AdditionalRaces { get; } = [];
    public string Race { get => race; set => Set(ref race, value ?? string.Empty); }
    public string MaleSkinTexture
    {
        get => maleSkinTexture;
        set => Set(ref maleSkinTexture, value ?? string.Empty);
    }
    public string FemaleSkinTexture
    {
        get => femaleSkinTexture;
        set => Set(ref femaleSkinTexture, value ?? string.Empty);
    }
    public string MaleSkinTextureSwapList
    {
        get => maleSkinTextureSwapList;
        set => Set(ref maleSkinTextureSwapList, value ?? string.Empty);
    }
    public string FemaleSkinTextureSwapList
    {
        get => femaleSkinTextureSwapList;
        set => Set(ref femaleSkinTextureSwapList, value ?? string.Empty);
    }
    public string FootstepSet
    {
        get => footstepSet;
        set => Set(ref footstepSet, value ?? string.Empty);
    }
    public string ArtObject
    {
        get => artObject;
        set => Set(ref artObject, value ?? string.Empty);
    }
    public string CandidateAdditionalRace
    {
        get => candidateAdditionalRace;
        set => Set(ref candidateAdditionalRace, value ?? string.Empty);
    }
    public int SelectedAdditionalRaceIndex
    {
        get => selectedAdditionalRaceIndex;
        set => Set(ref selectedAdditionalRaceIndex, value);
    }

    public void Load(SkyrimArmorAddonEditorDocument document)
    {
        race = document.Race?.ToString() ?? string.Empty;
        maleSkinTexture = document.MaleSkinTexture?.ToString() ?? string.Empty;
        femaleSkinTexture = document.FemaleSkinTexture?.ToString() ?? string.Empty;
        maleSkinTextureSwapList =
            document.MaleSkinTextureSwapList?.ToString() ?? string.Empty;
        femaleSkinTextureSwapList =
            document.FemaleSkinTextureSwapList?.ToString() ?? string.Empty;
        footstepSet = document.FootstepSet?.ToString() ?? string.Empty;
        artObject = document.ArtObject?.ToString() ?? string.Empty;
        AdditionalRaces.Clear();
        foreach (FormReference additionalRace in document.AdditionalRaces)
            AdditionalRaces.Add(additionalRace.ToString());
        candidateAdditionalRace = string.Empty;
        selectedAdditionalRaceIndex = -1;
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorAddonDataSectionViewModel : NotifyViewModel
{
    private string malePriorityText = "0";
    private string femalePriorityText = "0";
    private bool maleWeightSliderEnabled;
    private bool femaleWeightSliderEnabled;
    private string detectionSoundText = "0";
    private string weaponAdjustText = "0";

    public string MalePriorityText
    {
        get => malePriorityText;
        set => Set(ref malePriorityText, value ?? string.Empty);
    }
    public string FemalePriorityText
    {
        get => femalePriorityText;
        set => Set(ref femalePriorityText, value ?? string.Empty);
    }
    public bool MaleWeightSliderEnabled
    {
        get => maleWeightSliderEnabled;
        set => Set(ref maleWeightSliderEnabled, value);
    }
    public bool FemaleWeightSliderEnabled
    {
        get => femaleWeightSliderEnabled;
        set => Set(ref femaleWeightSliderEnabled, value);
    }
    public string DetectionSoundText
    {
        get => detectionSoundText;
        set => Set(ref detectionSoundText, value ?? string.Empty);
    }
    public string WeaponAdjustText
    {
        get => weaponAdjustText;
        set => Set(ref weaponAdjustText, value ?? string.Empty);
    }

    public void Load(SkyrimArmorAddonEditorDocument document)
    {
        malePriorityText = document.MalePriority.ToString(
            CultureInfo.InvariantCulture);
        femalePriorityText = document.FemalePriority.ToString(
            CultureInfo.InvariantCulture);
        maleWeightSliderEnabled = document.MaleWeightSliderEnabled;
        femaleWeightSliderEnabled = document.FemaleWeightSliderEnabled;
        detectionSoundText = document.DetectionSound.ToString(
            CultureInfo.InvariantCulture);
        weaponAdjustText = document.WeaponAdjust.ToString(
            "R", CultureInfo.InvariantCulture);
        Raise(string.Empty);
    }
}

public enum SkyrimArmorAddonPreviewScope
{
    ModelOnly,
    FullArmor,
    FullOutfit
}

public sealed class SkyrimArmorAddonPreviewSectionViewModel : NotifyViewModel
{
    private SkyrimArmorAddonPreviewScope scope;
    private bool includeBody;
    private bool oppositeGender;

    public IReadOnlyList<SkyrimArmorAddonPreviewScope> Scopes { get; } =
        Enum.GetValues<SkyrimArmorAddonPreviewScope>();
    public SkyrimArmorAddonPreviewScope Scope
    {
        get => scope;
        set => Set(ref scope, value);
    }
    public bool IncludeBody
    {
        get => includeBody;
        set => Set(ref includeBody, value);
    }
    public bool OppositeGender
    {
        get => oppositeGender;
        set => Set(ref oppositeGender, value);
    }

    public void Reset()
    {
        scope = SkyrimArmorAddonPreviewScope.ModelOnly;
        includeBody = false;
        oppositeGender = false;
        Raise(string.Empty);
    }
}
