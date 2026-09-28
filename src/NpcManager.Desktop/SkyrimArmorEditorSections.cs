using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimArmorIdentitySectionViewModel : NotifyViewModel
{
    public SkyrimArmorIdentitySectionViewModel(SkyrimArmorEditorDocument document) =>
        Load(document);

    public SkyrimArmorEditorIntent Intent { get; private set; }
    public ArmorProposalMode Mode { get; private set; }
    public WorkspacePath SourcePlugin { get; private set; }
    public FormId SourceFormId { get; private set; }
    public FormId? TargetFormId { get; private set; }
    public bool CanEditEditorId => Mode == ArmorProposalMode.New;
    public string IntentLabel => Intent switch
    {
        SkyrimArmorEditorIntent.BlankNew => "New blank ARMO",
        SkyrimArmorEditorIntent.NewFromTemplate => "New ARMO from template",
        SkyrimArmorEditorIntent.OverrideExisting => "Override existing ARMO",
        SkyrimArmorEditorIntent.EditAuthored => "Continue authored ARMO",
        _ => "Invalid intent"
    };
    public string SourceIdentity => $"{Path.GetFileName(SourcePlugin.Value)}|{SourceFormId}";
    public string TargetIdentity => Mode == ArmorProposalMode.New
        ? $"new plugin|{TargetFormId}"
        : SourceIdentity;

    private string editorIdText = string.Empty;
    public string EditorIdText
    {
        get => editorIdText;
        set => Set(ref editorIdText, value ?? string.Empty);
    }

    public void Load(SkyrimArmorEditorDocument document)
    {
        Intent = document.Intent;
        Mode = document.Mode;
        SourcePlugin = document.SourcePlugin;
        SourceFormId = document.SourceFormId;
        TargetFormId = document.TargetFormId;
        editorIdText = document.EditorId.Value;
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorCoreSectionViewModel : NotifyViewModel
{
    private string name = string.Empty;
    private string description = string.Empty;
    private bool nonPlayable;
    private string valueText = "0";
    private string weightText = "0";
    private string armorRatingText = "0";
    private string slotMaskText = "0x00000000";

    public string Name { get => name; set => Set(ref name, value ?? string.Empty); }
    public string Description { get => description; set => Set(ref description, value ?? string.Empty); }
    public bool NonPlayable { get => nonPlayable; set => Set(ref nonPlayable, value); }
    public string ValueText { get => valueText; set => Set(ref valueText, value ?? string.Empty); }
    public string WeightText { get => weightText; set => Set(ref weightText, value ?? string.Empty); }
    public string ArmorRatingText { get => armorRatingText; set => Set(ref armorRatingText, value ?? string.Empty); }
    public string SlotMaskText { get => slotMaskText; set => Set(ref slotMaskText, value ?? string.Empty); }

    public void Load(SkyrimArmorEditorDocument document)
    {
        name = document.Name;
        description = document.Description;
        nonPlayable = document.NonPlayable;
        valueText = document.Value.ToString(CultureInfo.InvariantCulture);
        weightText = document.Weight.ToString("R", CultureInfo.InvariantCulture);
        armorRatingText = document.ArmorRating.ToString("R", CultureInfo.InvariantCulture);
        slotMaskText = $"0x{document.SlotMask:X8}";
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorReferenceSectionViewModel : NotifyViewModel
{
    private string race = string.Empty;
    private string enchantment = string.Empty;
    private string pickupSound = string.Empty;
    private string dropSound = string.Empty;
    private string equipmentType = string.Empty;
    private string alternateBlockMaterial = string.Empty;
    private string templateArmor = string.Empty;
    private string maleWorldModel = string.Empty;
    private string femaleWorldModel = string.Empty;

    public string Race { get => race; set => Set(ref race, value ?? string.Empty); }
    public string Enchantment { get => enchantment; set => Set(ref enchantment, value ?? string.Empty); }
    public string PickupSound { get => pickupSound; set => Set(ref pickupSound, value ?? string.Empty); }
    public string DropSound { get => dropSound; set => Set(ref dropSound, value ?? string.Empty); }
    public string EquipmentType { get => equipmentType; set => Set(ref equipmentType, value ?? string.Empty); }
    public string AlternateBlockMaterial { get => alternateBlockMaterial; set => Set(ref alternateBlockMaterial, value ?? string.Empty); }
    public string TemplateArmor { get => templateArmor; set => Set(ref templateArmor, value ?? string.Empty); }
    public string MaleWorldModel { get => maleWorldModel; set => Set(ref maleWorldModel, value ?? string.Empty); }
    public string FemaleWorldModel { get => femaleWorldModel; set => Set(ref femaleWorldModel, value ?? string.Empty); }

    public void Load(SkyrimArmorEditorDocument document)
    {
        race = document.Race.ToString();
        enchantment = document.Enchantment?.ToString() ?? string.Empty;
        pickupSound = document.PickupSound?.ToString() ?? string.Empty;
        dropSound = document.DropSound?.ToString() ?? string.Empty;
        equipmentType = document.EquipmentType?.ToString() ?? string.Empty;
        alternateBlockMaterial = document.AlternateBlockMaterial?.ToString() ?? string.Empty;
        templateArmor = document.TemplateArmor?.ToString() ?? string.Empty;
        maleWorldModel = document.MaleWorldModel ?? string.Empty;
        femaleWorldModel = document.FemaleWorldModel ?? string.Empty;
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorBoundsSectionViewModel : NotifyViewModel
{
    private string minimumX = "0";
    private string minimumY = "0";
    private string minimumZ = "0";
    private string maximumX = "0";
    private string maximumY = "0";
    private string maximumZ = "0";

    public string MinimumX { get => minimumX; set => Set(ref minimumX, value ?? string.Empty); }
    public string MinimumY { get => minimumY; set => Set(ref minimumY, value ?? string.Empty); }
    public string MinimumZ { get => minimumZ; set => Set(ref minimumZ, value ?? string.Empty); }
    public string MaximumX { get => maximumX; set => Set(ref maximumX, value ?? string.Empty); }
    public string MaximumY { get => maximumY; set => Set(ref maximumY, value ?? string.Empty); }
    public string MaximumZ { get => maximumZ; set => Set(ref maximumZ, value ?? string.Empty); }

    public void Load(ArmorObjectBounds bounds)
    {
        minimumX = bounds.MinimumX.ToString(CultureInfo.InvariantCulture);
        minimumY = bounds.MinimumY.ToString(CultureInfo.InvariantCulture);
        minimumZ = bounds.MinimumZ.ToString(CultureInfo.InvariantCulture);
        maximumX = bounds.MaximumX.ToString(CultureInfo.InvariantCulture);
        maximumY = bounds.MaximumY.ToString(CultureInfo.InvariantCulture);
        maximumZ = bounds.MaximumZ.ToString(CultureInfo.InvariantCulture);
        Raise(string.Empty);
    }
}

public sealed class SkyrimArmorCollectionSectionViewModel : NotifyViewModel
{
    public ObservableCollection<string> ArmorAddons { get; } = [];
    public ObservableCollection<string> Keywords { get; } = [];
    public ImmutableArray<SkyrimArmorAddonReferenceRow> AuthoredArmorAddons { get; private set; } = [];

    private string candidateReference = string.Empty;
    private int selectedArmorAddonIndex = -1;
    private int selectedKeywordIndex = -1;

    public string CandidateReference
    {
        get => candidateReference;
        set => Set(ref candidateReference, value ?? string.Empty);
    }
    public int SelectedArmorAddonIndex
    {
        get => selectedArmorAddonIndex;
        set => Set(ref selectedArmorAddonIndex, value);
    }
    public int SelectedKeywordIndex
    {
        get => selectedKeywordIndex;
        set => Set(ref selectedKeywordIndex, value);
    }

    public void Load(SkyrimArmorEditorDocument document)
    {
        ArmorAddons.Clear();
        foreach (FormReference reference in document.ArmorAddons)
            ArmorAddons.Add(reference.ToString());
        Keywords.Clear();
        foreach (FormReference reference in document.Keywords)
            Keywords.Add(reference.ToString());
        AuthoredArmorAddons = document.AuthoredArmorAddons.IsDefault
            ? []
            : document.AuthoredArmorAddons;
        CandidateReference = string.Empty;
        SelectedArmorAddonIndex = -1;
        SelectedKeywordIndex = -1;
    }
}
