using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class SkyrimFaceEditorDocumentRules
{
    public static SkyrimFaceEditorDocument AddFaceOverlay(
        SkyrimFaceEditorDocument document,
        SkyrimRaceMenuPaintChoiceCandidate paint,
        int slotLimit)
    {
        RequireValid(document);
        ArgumentNullException.ThrowIfNull(paint);
        if (paint.Category != SkyrimRaceMenuPaintCategory.Face)
            throw new ArgumentException("Only a face-paint catalog entry can create a face overlay.", nameof(paint));
        if (slotLimit is < 1 or > MaximumBodyOverlays)
            throw new ArgumentOutOfRangeException(nameof(slotLimit));
        var used = document.BodyOverlays
            .Select(item => TryGetFaceOverlaySlot(item.Node, out int slot) ? slot : -1)
            .Where(slot => slot >= 0)
            .ToHashSet();
        int free = Enumerable.Range(0, slotLimit).FirstOrDefault(slot => !used.Contains(slot), -1);
        if (free < 0) throw new InvalidOperationException("No reviewed RaceMenu face-overlay slot is free.");
        SkyrimRaceMenuPaintTextureSlot? normal = paint.TextureSlots
            .FirstOrDefault(item => item.Index == 1 &&
                                    item.Kind == SkyrimRaceMenuPaintSlotKind.Texture);
        var overlay = new RaceMenuBodyOverlay(
            FaceOverlayNode(free),
            paint.RegisteredPath.Value,
            normal?.RegisteredValue,
            [1F, 1F, 1F, 1F],
            1F,
            []);
        return document with { BodyOverlays = document.BodyOverlays.Add(overlay) };
    }

    public static SkyrimFaceEditorDocument RemoveFaceOverlay(
        SkyrimFaceEditorDocument document,
        int slot)
    {
        RequireValid(document);
        return document with
        {
            BodyOverlays = document.BodyOverlays
                .Where(item => !TryGetFaceOverlaySlot(item.Node, out int current) || current != slot)
                .ToImmutableArray()
        };
    }

    public static SkyrimFaceEditorDocument MoveFaceOverlay(
        SkyrimFaceEditorDocument document,
        int slot,
        int adjacentSlot)
    {
        RequireValid(document);
        int first = FindIndex(document.BodyOverlays, item =>
            TryGetFaceOverlaySlot(item.Node, out int current) && current == slot);
        int second = FindIndex(document.BodyOverlays, item =>
            TryGetFaceOverlaySlot(item.Node, out int current) && current == adjacentSlot);
        if (first < 0 || second < 0)
            throw new ArgumentException("Both face-overlay slots must exist before they can be swapped.");
        var rows = document.BodyOverlays.ToBuilder();
        rows[first] = rows[first] with { Node = FaceOverlayNode(adjacentSlot) };
        rows[second] = rows[second] with { Node = FaceOverlayNode(slot) };
        return document with { BodyOverlays = rows.ToImmutable() };
    }

    public static SkyrimFaceEditorDocument SetFaceOverlayAppearance(
        SkyrimFaceEditorDocument document,
        int slot,
        bool tintEnabled,
        float red,
        float green,
        float blue,
        float opacity)
    {
        RequireValid(document);
        if (new[] { red, green, blue, opacity }.Any(value =>
                !float.IsFinite(value) || value is < 0F or > 1F))
        {
            throw new ArgumentOutOfRangeException(nameof(red));
        }
        int row = FindIndex(document.BodyOverlays, item =>
            TryGetFaceOverlaySlot(item.Node, out int current) && current == slot);
        if (row < 0) throw new ArgumentException("The face-overlay slot does not exist.", nameof(slot));
        RaceMenuBodyOverlay current = document.BodyOverlays[row];
        ImmutableArray<float> tint = tintEnabled
            ? [red, green, blue, 1F]
            : ImmutableArray<float>.Empty;
        return document with
        {
            BodyOverlays = document.BodyOverlays.SetItem(
                row,
                current with { Tint = tint, Alpha = opacity })
        };
    }

    public static ImmutableArray<RaceMenuBodyOverlay> FaceOverlaysInDrawOrder(
        SkyrimFaceEditorDocument document)
    {
        RequireValid(document);
        return document.BodyOverlays
            .Where(item => TryGetFaceOverlaySlot(item.Node, out _))
            .OrderByDescending(item =>
            {
                _ = TryGetFaceOverlaySlot(item.Node, out int slot);
                return slot;
            })
            .ToImmutableArray();
    }

    public static bool TryGetFaceOverlaySlot(string? node, out int slot)
    {
        slot = -1;
        const string prefix = "Face [Ovl";
        if (node is null || !node.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !node.EndsWith(']')) return false;
        ReadOnlySpan<char> number = node.AsSpan(prefix.Length, node.Length - prefix.Length - 1);
        return int.TryParse(number, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out slot) &&
               slot is >= 0 and < MaximumBodyOverlays;
    }

    private static void ValidateOverlays(
        ImmutableArray<RaceMenuBodyOverlay> overlays,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (overlays.IsDefault || overlays.Length > MaximumBodyOverlays)
        {
            diagnostics.Add(Error("face-editor-overlay-shape",
                $"Body overlays must be initialized and contain at most {MaximumBodyOverlays} rows."));
            return;
        }
        var faceSlots = overlays
            .Where(item => item is not null)
            .Select(item => TryGetFaceOverlaySlot(item.Node, out int slot) ? slot : -1)
            .Where(slot => slot >= 0)
            .ToArray();
        if (faceSlots.Distinct().Count() != faceSlots.Length)
            diagnostics.Add(Error("face-editor-overlay-slot-duplicate", "Face-overlay slots must be unique."));
        if (overlays.Any(item => item is null || string.IsNullOrWhiteSpace(item.Node) ||
                item.Node.Length > 256 || item.Tint.IsDefault || item.Values.IsDefault ||
                item.Tint.Any(value => !float.IsFinite(value) || value is < 0F or > 1F) ||
                item.Alpha is { } alpha && (!float.IsFinite(alpha) || alpha is < 0F or > 1F)))
        {
            diagnostics.Add(Error("face-editor-overlay-value", "A body-overlay row is invalid."));
        }
    }

    private static string FaceOverlayNode(int slot) => $"Face [Ovl{slot}]";

    private static bool OverlayEquivalent(
        ImmutableArray<RaceMenuBodyOverlay> left,
        ImmutableArray<RaceMenuBodyOverlay> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            RaceMenuBodyOverlay a = left[index];
            RaceMenuBodyOverlay b = right[index];
            if (!string.Equals(a.Node, b.Node, StringComparison.Ordinal) ||
                !string.Equals(a.Diffuse, b.Diffuse, StringComparison.Ordinal) ||
                !string.Equals(a.Normal, b.Normal, StringComparison.Ordinal) ||
                a.Alpha != b.Alpha ||
                !a.Tint.SequenceEqual(b.Tint) ||
                !a.Values.SequenceEqual(b.Values)) return false;
        }
        return true;
    }
}
