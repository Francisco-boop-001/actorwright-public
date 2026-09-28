using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimSkinOverrideEditorViewModel : NotifyViewModel
{
    private readonly SkyrimBodyEditorViewModel owner;

    internal SkyrimSkinOverrideEditorViewModel(SkyrimBodyEditorViewModel owner) => this.owner = owner;

    public ObservableCollection<SkyrimSkinOverrideRowViewModel> Rows { get; } = [];
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => $"No RaceMenu skin override is authored for {owner.Subject}. Add one only with an explicit biped slot mask and diffuse path.";
    public bool CanEdit => SelectedRow is not null;

    private SkyrimSkinOverrideRowViewModel? selectedRow;
    public SkyrimSkinOverrideRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            LoadSelection(value);
            Raise(nameof(CanEdit));
        }
    }

    private string newSlotMask = "0x00000020";
    public string NewSlotMask { get => newSlotMask; set => Set(ref newSlotMask, value ?? string.Empty); }
    private bool newFirstPerson;
    public bool NewFirstPerson { get => newFirstPerson; set => Set(ref newFirstPerson, value); }
    private string newDiffuse = string.Empty;
    public string NewDiffuse { get => newDiffuse; set => Set(ref newDiffuse, value ?? string.Empty); }

    private string diffuse = string.Empty;
    public string Diffuse { get => diffuse; set => Set(ref diffuse, value ?? string.Empty); }
    private string normal = string.Empty;
    public string Normal { get => normal; set => Set(ref normal, value ?? string.Empty); }
    private string subsurface = string.Empty;
    public string Subsurface { get => subsurface; set => Set(ref subsurface, value ?? string.Empty); }
    private string specular = string.Empty;
    public string Specular { get => specular; set => Set(ref specular, value ?? string.Empty); }
    private bool tintEnabled;
    public bool TintEnabled { get => tintEnabled; set => Set(ref tintEnabled, value); }
    private float red = 1F;
    public float Red { get => red; set => Set(ref red, value); }
    private float green = 1F;
    public float Green { get => green; set => Set(ref green, value); }
    private float blue = 1F;
    public float Blue { get => blue; set => Set(ref blue, value); }
    private float tintAlpha = 1F;
    public float TintAlpha { get => tintAlpha; set => Set(ref tintAlpha, value); }
    private bool alphaEnabled;
    public bool AlphaEnabled { get => alphaEnabled; set => Set(ref alphaEnabled, value); }
    private float alpha = 1F;
    public float Alpha { get => alpha; set => Set(ref alpha, value); }

    public bool Add()
    {
        if (!TryParseSlotMask(NewSlotMask, out uint slotMask))
        {
            owner.ValidationMessage = "Enter a non-zero 32-bit slot mask such as 0x00000020.";
            return false;
        }
        try
        {
            owner.Replace(SkyrimBodyEditorDocumentRules.AddSkinOverride(
                    owner.CurrentDocument, slotMask, NewFirstPerson, NewDiffuse),
                SkyrimBodyEditorSection.SkinOverrides,
                $"Skin override 0x{slotMask:X8} added.");
            SelectedRow = Rows.FirstOrDefault(item => item.SlotMask == slotMask &&
                                                       item.FirstPerson == NewFirstPerson);
            return true;
        }
        catch (ArgumentException exception)
        {
            owner.ValidationMessage = exception.Message;
            return false;
        }
        catch (InvalidOperationException exception)
        {
            owner.ValidationMessage = exception.Message;
            return false;
        }
    }

    public bool Apply()
    {
        if (SelectedRow is null) return false;
        try
        {
            var textures = ImmutableDictionary.CreateBuilder<int, string>();
            AddTexture(textures, 0, Diffuse);
            AddTexture(textures, 1, Normal);
            AddTexture(textures, 2, Subsurface);
            AddTexture(textures, 7, Specular);
            ImmutableArray<float> tint = TintEnabled ? [Red, Green, Blue, TintAlpha] : [];
            SkyrimBodyEditorDocument changed = SkyrimBodyEditorDocumentRules.SetSkinOverrideAppearance(
                owner.CurrentDocument, SelectedRow.SlotMask, SelectedRow.FirstPerson,
                textures.ToImmutable(), tint, AlphaEnabled ? Alpha : null);
            uint mask = SelectedRow.SlotMask;
            bool fp = SelectedRow.FirstPerson;
            owner.Replace(changed, SkyrimBodyEditorSection.SkinOverrides,
                $"Skin override 0x{mask:X8} appearance updated.");
            SelectedRow = Rows.FirstOrDefault(item => item.SlotMask == mask && item.FirstPerson == fp);
            return true;
        }
        catch (ArgumentException exception)
        {
            owner.ValidationMessage = exception.Message;
            return false;
        }
    }

    public void RemoveSelected()
    {
        if (SelectedRow is null) return;
        uint mask = SelectedRow.SlotMask;
        bool fp = SelectedRow.FirstPerson;
        owner.Replace(SkyrimBodyEditorDocumentRules.RemoveSkinOverride(
                owner.CurrentDocument, mask, fp),
            SkyrimBodyEditorSection.SkinOverrides,
            $"Skin override 0x{mask:X8} removed.");
    }

    internal void Refresh()
    {
        (uint SlotMask, bool FirstPerson)? retained = SelectedRow is null
            ? null
            : (SelectedRow.SlotMask, SelectedRow.FirstPerson);
        Rows.Clear();
        foreach (SkyrimSkinOverride skin in owner.CurrentDocument.SkinOverrides)
            Rows.Add(new SkyrimSkinOverrideRowViewModel(skin));
        SelectedRow = retained is null
            ? null
            : Rows.FirstOrDefault(item => item.SlotMask == retained.Value.SlotMask &&
                                          item.FirstPerson == retained.Value.FirstPerson);
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
        Raise(nameof(CanEdit));
    }

    private void LoadSelection(SkyrimSkinOverrideRowViewModel? row)
    {
        if (row is null) return;
        SkyrimSkinOverride skin = owner.CurrentDocument.SkinOverrides.Single(item =>
            item.SlotMask == row.SlotMask && item.FirstPerson == row.FirstPerson);
        Diffuse = Texture(skin, 0);
        Normal = Texture(skin, 1);
        Subsurface = Texture(skin, 2);
        Specular = Texture(skin, 7);
        TintEnabled = skin.Tint.Length == 4;
        Red = TintEnabled ? skin.Tint[0] : 1F;
        Green = TintEnabled ? skin.Tint[1] : 1F;
        Blue = TintEnabled ? skin.Tint[2] : 1F;
        TintAlpha = TintEnabled ? skin.Tint[3] : 1F;
        AlphaEnabled = skin.Alpha is not null;
        Alpha = skin.Alpha ?? 1F;
        owner.ValidationMessage = string.Empty;
    }

    private static string Texture(SkyrimSkinOverride skin, int slot) =>
        skin.Textures.TryGetValue(slot, out string? value) ? value : string.Empty;

    private static void AddTexture(ImmutableDictionary<int, string>.Builder values, int slot, string raw)
    {
        string normalized = raw.Trim();
        if (normalized.Length > 0) values.Add(slot, normalized);
    }

    private static bool TryParseSlotMask(string value, out uint slotMask)
    {
        string normalized = value.Trim();
        NumberStyles style = NumberStyles.Integer;
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        return uint.TryParse(normalized, style, CultureInfo.InvariantCulture, out slotMask) &&
               slotMask != 0;
    }
}

public sealed class SkyrimSkinOverrideRowViewModel
{
    internal SkyrimSkinOverrideRowViewModel(SkyrimSkinOverride skin)
    {
        SlotMask = skin.SlotMask;
        FirstPerson = skin.FirstPerson;
        EditableTextureCount = skin.Textures.Count(pair => pair.Key is 0 or 1 or 2 or 7);
        PreservedTextureCount = skin.Textures.Count - EditableTextureCount;
        HasTint = skin.Tint.Length == 4;
        HasAlpha = skin.Alpha is not null;
    }

    public uint SlotMask { get; }
    public bool FirstPerson { get; }
    public int EditableTextureCount { get; }
    public int PreservedTextureCount { get; }
    public bool HasTint { get; }
    public bool HasAlpha { get; }
    public string SlotMaskText => $"0x{SlotMask:X8}";
    public string Perspective => FirstPerson ? "First person" : "Third person";
    public string Summary => $"{EditableTextureCount} editable texture(s) · {PreservedTextureCount} preserved other slot(s) · " +
                             $"tint {(HasTint ? "on" : "off")} · alpha {(HasAlpha ? "set" : "unset")}";
}
