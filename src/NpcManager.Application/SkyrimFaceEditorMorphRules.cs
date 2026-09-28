using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class SkyrimFaceEditorDocumentRules
{
    public static SkyrimFaceEditorDocument SetNativeSlider(
        SkyrimFaceEditorDocument document,
        int index,
        float value)
    {
        RequireValid(document);
        if (index is < 0 or >= NativeSliderCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (!float.IsFinite(value) || value is < -1F or > 1F)
            throw new ArgumentOutOfRangeException(nameof(value));
        var values = document.NativeMorphs.Nam9Sliders.SetItem(index, value);
        return document with { NativeMorphs = document.NativeMorphs with { Nam9Sliders = values } };
    }

    public static SkyrimFaceEditorDocument SetNativeFamily(
        SkyrimFaceEditorDocument document,
        int index,
        uint value)
    {
        RequireValid(document);
        if (index is < 0 or >= NativeFamilyCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        var values = document.NativeMorphs.NamaValues.SetItem(index, value);
        return document with { NativeMorphs = document.NativeMorphs with { NamaValues = values } };
    }

    public static SkyrimFaceEditorDocument SetCustomMorph(
        SkyrimFaceEditorDocument document,
        string name,
        float value,
        float minimum = -1F,
        float maximum = 1F)
    {
        RequireValid(document);
        string normalized = RequireName(name);
        if (!float.IsFinite(value) || !float.IsFinite(minimum) ||
            !float.IsFinite(maximum) || minimum > maximum ||
            value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        var rows = document.CustomMorphs.ToBuilder();
        int index = FindIndex(rows, item => string.Equals(
            item.Name, normalized, StringComparison.OrdinalIgnoreCase));
        if (Math.Abs(value) < CustomMorphZeroEpsilon)
        {
            if (index >= 0) rows.RemoveAt(index);
        }
        else if (index >= 0)
        {
            rows[index] = new SkyrimRaceMenuCustomMorphValue(normalized, value);
        }
        else
        {
            if (rows.Count >= MaximumCustomMorphs)
                throw new InvalidOperationException("The face document reached its custom-morph limit.");
            rows.Add(new SkyrimRaceMenuCustomMorphValue(normalized, value));
        }
        return document with { CustomMorphs = rows.ToImmutable() };
    }

    private static void ValidateNativeMorphs(
        SkyrimFaceMorphPatch morphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (morphs is null || morphs.Nam9Sliders.IsDefault ||
            morphs.Nam9Sliders.Length != NativeSliderCount ||
            morphs.Nam9Sliders.Any(value => !float.IsFinite(value) || value is < -1F or > 1F) ||
            !float.IsFinite(morphs.Nam9Trailing))
        {
            diagnostics.Add(Error("face-editor-native-nam9",
                "Native morphs require 18 finite sliders in range and one finite preserved trailing value."));
        }
        if (morphs is null || morphs.NamaValues.IsDefault ||
            morphs.NamaValues.Length != NativeFamilyCount)
        {
            diagnostics.Add(Error("face-editor-native-nama",
                "Native morphs require exactly four unsigned NAMA preset indexes; 0xFFFFFFFF remains the unset sentinel."));
        }
    }

    private static void ValidateCustomMorphs(
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> morphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (morphs.IsDefault || morphs.Length > MaximumCustomMorphs)
        {
            diagnostics.Add(Error("face-editor-custom-morph-shape",
                $"Custom morphs must be initialized and contain at most {MaximumCustomMorphs} rows."));
            return;
        }
        if (morphs.Any(item => item is null || !ValidName(item.Name) ||
                !float.IsFinite(item.Value)))
        {
            diagnostics.Add(Error("face-editor-custom-morph-value", "A custom morph row is invalid."));
        }
    }
}
