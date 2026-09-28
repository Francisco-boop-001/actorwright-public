using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class SkyrimFaceEditorDocumentRules
{
    public static SkyrimFaceEditorDocument SetTint(
        SkyrimFaceEditorDocument document,
        SkyrimFaceEditorTintLayer layer)
    {
        RequireValid(document);
        ValidateTint(layer);
        var rows = document.Tints.ToBuilder();
        int index = FindIndex(rows, item => item.Value.Index == layer.Value.Index);
        if (index < 0)
            throw new ArgumentException(
                "Only a tint layer declared by the immutable race-order baseline can be edited.",
                nameof(layer));
        rows[index] = layer;
        return document with { Tints = rows.ToImmutable() };
    }

    public static SkyrimFaceEditorDocument SetTintMask(
        SkyrimFaceEditorDocument document,
        ushort index,
        AssetPath? selected)
    {
        RequireValid(document);
        int row = FindIndex(document.Tints, item => item.Value.Index == index);
        if (row < 0) throw new ArgumentException("The tint layer does not exist.", nameof(index));
        SkyrimFaceEditorTintLayer current = document.Tints[row];
        AssetPath? effective = selected is not null &&
                               !PathsEquivalent(selected.Value, current.RaceDefaultMask)
            ? selected
            : null;
        return document with
        {
            Tints = document.Tints.SetItem(row, current with { MaskOverride = effective })
        };
    }

    public static ImmutableArray<SkyrimFaceEditorTintLayer> FaceTintDisplayOrder(
        SkyrimFaceEditorDocument document)
    {
        RequireValid(document);
        return document.Tints;
    }

    private static void ValidateTints(
        ImmutableArray<SkyrimFaceEditorTintLayer> tints,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (tints.IsDefault || tints.Length > MaximumTintLayers)
        {
            diagnostics.Add(Error("face-editor-tint-shape",
                $"Tint layers must be initialized and contain at most {MaximumTintLayers} rows."));
            return;
        }

        ImmutableArray<SkyrimFaceEditorTintLayer> validRows = tints
            .Where(item => item?.Value is not null)
            .ToImmutableArray();
        if (validRows.GroupBy(item => item.Value.Index).Any(group => group.Count() > 1))
            diagnostics.Add(Error("face-editor-tint-duplicate", "Tint indices must be unique."));
        foreach (SkyrimFaceEditorTintLayer? tint in tints)
        {
            try { ValidateTint(tint!); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("face-editor-tint-value", exception.Message));
            }
        }
    }

    private static void ValidateTint(SkyrimFaceEditorTintLayer tint)
    {
        ArgumentNullException.ThrowIfNull(tint);
        ArgumentNullException.ThrowIfNull(tint.Value);
        if (tint.Value.Coverage > 100)
            throw new ArgumentOutOfRangeException(nameof(tint), "Tint coverage must be 0 through 100.");
    }

    private static bool PathsEquivalent(AssetPath left, AssetPath? right) =>
        right is not null && string.Equals(
            NormalizePath(left.Value), NormalizePath(right.Value.Value),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string value)
    {
        string normalized = value.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
            ? normalized["textures/".Length..]
            : normalized;
    }
}
