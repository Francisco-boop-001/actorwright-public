using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class SkyrimBodyEditorDocumentRules
{
    private static readonly ImmutableHashSet<int> EditableSkinTextureSlots =
        ImmutableHashSet.Create(0, 1, 2, 7);

    public static SkyrimBodyEditorDocument AddSkinOverride(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson,
        string diffuse)
    {
        RequireValid(document);
        ArgumentOutOfRangeException.ThrowIfZero(slotMask);
        if (!ValidTexture(diffuse))
            throw new ArgumentException("A safe relative diffuse DDS path is required.", nameof(diffuse));
        if (document.SkinOverrides.Any(item => item.SlotMask == slotMask &&
                                               item.FirstPerson == firstPerson))
            throw new ArgumentException("That skin slot-mask identity already exists.", nameof(slotMask));
        if (document.SkinOverrides.Length >= MaximumSkinOverrides)
            throw new InvalidOperationException("The skin-override row limit has been reached.");
        var textures = ImmutableDictionary<int, string>.Empty.Add(0, diffuse.Trim());
        var values = ImmutableArray.Create(
            new RaceMenuValue(9, 2, 0, RaceMenuScalar.FromText(diffuse.Trim())));
        var added = new SkyrimSkinOverride(
            slotMask, firstPerson, values, textures, [], null);
        return document with { SkinOverrides = document.SkinOverrides.Insert(0, added) };
    }

    public static SkyrimBodyEditorDocument RemoveSkinOverride(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson)
    {
        RequireValid(document);
        return document with
        {
            SkinOverrides = document.SkinOverrides
                .Where(item => item.SlotMask != slotMask || item.FirstPerson != firstPerson)
                .ToImmutableArray()
        };
    }

    public static SkyrimBodyEditorDocument SetSkinTexture(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson,
        int textureSlot,
        string? texture)
    {
        RequireValid(document);
        if (!EditableSkinTextureSlots.Contains(textureSlot))
            throw new ArgumentOutOfRangeException(nameof(textureSlot));
        string? normalized = string.IsNullOrWhiteSpace(texture) ? null : texture.Trim();
        if (normalized is not null && !ValidTexture(normalized))
            throw new ArgumentException("The texture must be a safe relative DDS path.", nameof(texture));
        int index = SkinIndex(document, slotMask, firstPerson);
        SkyrimSkinOverride current = document.SkinOverrides[index];
        ImmutableDictionary<int, string> textures = normalized is null
            ? current.Textures.Remove(textureSlot)
            : current.Textures.SetItem(textureSlot, normalized);
        ImmutableArray<RaceMenuValue> values = current.Values
            .Where(value => value.Key != 9 || value.Index != textureSlot)
            .ToImmutableArray();
        if (normalized is not null)
            values = values.Add(new RaceMenuValue(9, 2, textureSlot,
                RaceMenuScalar.FromText(normalized)));
        return ReplaceSkin(document, index, current with { Textures = textures, Values = values });
    }

    public static SkyrimBodyEditorDocument SetSkinOverrideAppearance(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson,
        ImmutableDictionary<int, string> editableTextures,
        ImmutableArray<float> tint,
        float? alpha)
    {
        RequireValid(document);
        ArgumentNullException.ThrowIfNull(editableTextures);
        if (editableTextures.Any(pair => !EditableSkinTextureSlots.Contains(pair.Key) ||
                                         !ValidTexture(pair.Value)))
            throw new ArgumentException("Editable skin textures must use slots 0, 1, 2, or 7 and safe DDS paths.", nameof(editableTextures));
        if (tint.IsDefault || tint.Length is not (0 or 4) || tint.Any(value => !ValidUnit(value)))
            throw new ArgumentException("Tint must be empty or four unit values.", nameof(tint));
        if (alpha is { } opacity && !ValidUnit(opacity)) throw new ArgumentOutOfRangeException(nameof(alpha));

        int index = SkinIndex(document, slotMask, firstPerson);
        SkyrimSkinOverride current = document.SkinOverrides[index];
        ImmutableDictionary<int, string> textures = current.Textures
            .Where(pair => !EditableSkinTextureSlots.Contains(pair.Key))
            .ToImmutableDictionary()
            .SetItems(editableTextures);
        var values = current.Values
            .Where(value => value.Key != 7 && value.Key != 8 &&
                            (value.Key != 9 || !EditableSkinTextureSlots.Contains(value.Index)))
            .ToImmutableArray()
            .ToBuilder();
        foreach ((int textureSlot, string texture) in editableTextures.OrderBy(pair => pair.Key))
            values.Add(new RaceMenuValue(9, 2, textureSlot, RaceMenuScalar.FromText(texture)));
        if (tint.Length == 4)
            values.Add(new RaceMenuValue(7, 3, -1, RaceMenuScalar.FromInteger(PackTint(tint))));
        if (alpha is { } alphaValue)
            values.Add(new RaceMenuValue(8, 4, -1, RaceMenuScalar.FromNumber(alphaValue)));
        if (values.Count == 0)
            throw new ArgumentException("A skin override must retain at least one texture, tint, or alpha value.", nameof(editableTextures));
        return ReplaceSkin(document, index, current with
        {
            Textures = textures,
            Tint = tint,
            Alpha = alpha,
            Values = values.ToImmutable()
        });
    }

    public static SkyrimBodyEditorDocument SetSkinTint(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson,
        ImmutableArray<float> tint)
    {
        RequireValid(document);
        if (tint.IsDefault || tint.Length is not (0 or 4) || tint.Any(value => !ValidUnit(value)))
            throw new ArgumentException("Tint must be empty or four unit values.", nameof(tint));
        int index = SkinIndex(document, slotMask, firstPerson);
        SkyrimSkinOverride current = document.SkinOverrides[index];
        ImmutableArray<RaceMenuValue> values = current.Values
            .Where(value => value.Key != 7)
            .ToImmutableArray();
        if (tint.Length == 4)
            values = values.Add(new RaceMenuValue(7, 3, -1,
                RaceMenuScalar.FromInteger(PackTint(tint))));
        return ReplaceSkin(document, index, current with { Tint = tint, Values = values });
    }

    public static SkyrimBodyEditorDocument SetSkinAlpha(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson,
        float? alpha)
    {
        RequireValid(document);
        if (alpha is { } value && !ValidUnit(value)) throw new ArgumentOutOfRangeException(nameof(alpha));
        int index = SkinIndex(document, slotMask, firstPerson);
        SkyrimSkinOverride current = document.SkinOverrides[index];
        ImmutableArray<RaceMenuValue> values = current.Values
            .Where(value => value.Key != 8)
            .ToImmutableArray();
        if (alpha is { } opacity)
            values = values.Add(new RaceMenuValue(8, 4, -1,
                RaceMenuScalar.FromNumber(opacity)));
        return ReplaceSkin(document, index, current with { Alpha = alpha, Values = values });
    }

    private static SkyrimBodyEditorDocument ReplaceSkin(
        SkyrimBodyEditorDocument document,
        int index,
        SkyrimSkinOverride updated) =>
        document with { SkinOverrides = document.SkinOverrides.SetItem(index, updated) };

    private static int SkinIndex(
        SkyrimBodyEditorDocument document,
        uint slotMask,
        bool firstPerson)
    {
        int index = FindIndex(document.SkinOverrides, item =>
            item.SlotMask == slotMask && item.FirstPerson == firstPerson);
        return index >= 0
            ? index
            : throw new ArgumentException("The skin-override identity does not exist.", nameof(slotMask));
    }

    private static long PackTint(ImmutableArray<float> tint)
    {
        uint red = Byte(tint[0]);
        uint green = Byte(tint[1]);
        uint blue = Byte(tint[2]);
        uint alpha = Byte(tint[3]);
        return unchecked((int)((alpha << 24) | (red << 16) | (green << 8) | blue));
    }

    private static uint Byte(float value) =>
        (uint)Math.Clamp(Math.Round(value * 255F, MidpointRounding.ToEven), 0D, 255D);

    private static void ValidateSkinOverrides(
        ImmutableArray<SkyrimSkinOverride> skins,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (skins.IsDefault || skins.Length > MaximumSkinOverrides)
        {
            diagnostics.Add(Error("body-editor-skin-shape",
                $"Skin overrides must be initialized and contain at most {MaximumSkinOverrides} rows."));
            return;
        }
        var identities = new HashSet<(bool FirstPerson, uint SlotMask)>();
        foreach (SkyrimSkinOverride? skin in skins)
        {
            if (skin is null || skin.SlotMask == 0 ||
                !identities.Add((skin.FirstPerson, skin.SlotMask)) ||
                skin.Values.IsDefault || skin.Textures is null || skin.Tint.IsDefault ||
                skin.Tint.Length is not (0 or 4) || skin.Tint.Any(value => !ValidUnit(value)) ||
                skin.Alpha is { } alpha && !ValidUnit(alpha) ||
                skin.Textures.Any(pair => pair.Key is < 0 or >= 512 || !ValidTexture(pair.Value)) ||
                skin.Values.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error("body-editor-skin-value", "A skin-override row is invalid."));
                continue;
            }
            var identitiesByValue = new HashSet<(int Key, int Index)>();
            foreach (RaceMenuValue? value in skin.Values)
                if (value is null || value.Data is null || !ValidSkinValue(value) ||
                    !identitiesByValue.Add((value.Key, value.Index)))
                    diagnostics.Add(Error("body-editor-skin-key-value", "A skin value is invalid or duplicated."));
        }
    }

    private static bool ValidSkinValue(RaceMenuValue value) => value.Key switch
    {
        7 => value.Type == 3 && value.Index == -1 &&
             value.Data.Kind == RaceMenuScalarKind.SignedInteger &&
             value.Data.IntegerValue is >= int.MinValue and <= uint.MaxValue,
        8 => value.Type == 4 && value.Index == -1 &&
             value.Data.Kind == RaceMenuScalarKind.FloatingPoint &&
             double.IsFinite(value.Data.NumberValue) && value.Data.NumberValue is >= 0D and <= 1D,
        9 => value.Type == 2 && value.Index is >= 0 and < 512 &&
             value.Data.Kind == RaceMenuScalarKind.Text && ValidTexture(value.Data.StringValue),
        _ => false
    };

    private static bool SkinArrayEquivalent(
        ImmutableArray<SkyrimSkinOverride> left,
        ImmutableArray<SkyrimSkinOverride> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            SkyrimSkinOverride a = left[index];
            SkyrimSkinOverride b = right[index];
            if (a.SlotMask != b.SlotMask || a.FirstPerson != b.FirstPerson || a.Alpha != b.Alpha ||
                !a.Values.SequenceEqual(b.Values) || !a.Tint.SequenceEqual(b.Tint) ||
                a.Textures.Count != b.Textures.Count || a.Textures.Any(pair =>
                    !b.Textures.TryGetValue(pair.Key, out string? value) ||
                    !string.Equals(pair.Value, value, StringComparison.Ordinal))) return false;
        }
        return true;
    }
}
