using System.Collections.Immutable;
using System.Globalization;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class SkyrimBodyEditorDocumentRules
{
    public static SkyrimBodyEditorDocument AddBodyOverlay(
        SkyrimBodyEditorDocument document,
        BodyOverlayTarget target,
        SkyrimRaceMenuPaintChoiceCandidate paint,
        int slotLimit)
    {
        RequireValid(document);
        ArgumentNullException.ThrowIfNull(paint);
        if (paint.Category != PaintCategory(target))
            throw new ArgumentException("The paint category does not match the selected body zone.", nameof(paint));
        if (paint.Sources.IsDefaultOrEmpty || paint.TextureSlots.IsDefault)
            throw new ArgumentException("Body-zone paint requires registration evidence and initialized texture slots.", nameof(paint));
        if (slotLimit is < 1 or > MaximumBodyOverlays)
            throw new ArgumentOutOfRangeException(nameof(slotLimit));
        var used = document.BodyOverlays
            .Select(item => TryGetBodyOverlayIdentity(item.Node, out BodyOverlayTarget zone, out int slot) &&
                            zone == target ? slot : -1)
            .Where(slot => slot >= 0)
            .ToHashSet();
        int free = Enumerable.Range(0, slotLimit).FirstOrDefault(slot => !used.Contains(slot), -1);
        if (free < 0) throw new InvalidOperationException("No reviewed overlay slot is free in that body zone.");
        SkyrimRaceMenuPaintTextureSlot? normal = paint.TextureSlots.FirstOrDefault(item =>
            item.Index == 1 && item.Kind == SkyrimRaceMenuPaintSlotKind.Texture);
        var overlay = new RaceMenuBodyOverlay(
            OverlayNode(target, free), paint.RegisteredPath.Value, normal?.RegisteredValue,
            [1F, 1F, 1F, 1F], 1F,
            BuildOverlayValues([], paint.RegisteredPath.Value, normal?.RegisteredValue,
                [1F, 1F, 1F, 1F], 1F));
        return document with { BodyOverlays = document.BodyOverlays.Insert(0, overlay) };
    }

    public static SkyrimBodyEditorDocument RemoveBodyOverlay(
        SkyrimBodyEditorDocument document,
        BodyOverlayTarget target,
        int slot)
    {
        RequireValid(document);
        return document with
        {
            BodyOverlays = document.BodyOverlays.Where(item =>
                    !TryGetBodyOverlayIdentity(item.Node, out BodyOverlayTarget zone, out int current) ||
                    zone != target || current != slot)
                .ToImmutableArray()
        };
    }

    public static SkyrimBodyEditorDocument MoveBodyOverlay(
        SkyrimBodyEditorDocument document,
        BodyOverlayTarget target,
        int slot,
        int adjacentSlot)
    {
        RequireValid(document);
        int first = OverlayIndex(document, target, slot);
        int second = OverlayIndex(document, target, adjacentSlot);
        if (first < 0 || second < 0)
            throw new ArgumentException("Both body-zone overlay slots must exist before they can be swapped.");
        var rows = document.BodyOverlays.ToBuilder();
        rows[first] = rows[first] with { Node = OverlayNode(target, adjacentSlot) };
        rows[second] = rows[second] with { Node = OverlayNode(target, slot) };
        return document with { BodyOverlays = rows.ToImmutable() };
    }

    public static SkyrimBodyEditorDocument SetBodyOverlayAppearance(
        SkyrimBodyEditorDocument document,
        BodyOverlayTarget target,
        int slot,
        bool tintEnabled,
        float red,
        float green,
        float blue,
        float opacity)
    {
        RequireValid(document);
        if (new[] { red, green, blue, opacity }.Any(value => !ValidUnit(value)))
            throw new ArgumentOutOfRangeException(nameof(red));
        int index = OverlayIndex(document, target, slot);
        if (index < 0) throw new ArgumentException("The body-zone overlay slot does not exist.", nameof(slot));
        RaceMenuBodyOverlay current = document.BodyOverlays[index];
        ImmutableArray<float> tint = tintEnabled ? [red, green, blue, 1F] : [];
        return document with
        {
            BodyOverlays = document.BodyOverlays.SetItem(index,
                current with
                {
                    Tint = tint,
                    Alpha = opacity,
                    Values = BuildOverlayValues(
                        current.Values, current.Diffuse, current.Normal, tint, opacity)
                })
        };
    }

    public static ImmutableArray<RaceMenuBodyOverlay> BodyOverlaysInDrawOrder(
        SkyrimBodyEditorDocument document)
    {
        RequireValid(document);
        return document.BodyOverlays
            .Select(item => new
            {
                Item = item,
                Parsed = TryGetBodyOverlayIdentity(item.Node, out BodyOverlayTarget target, out int slot),
                Target = target,
                Slot = slot
            })
            .Where(item => item.Parsed)
            .OrderBy(item => item.Target)
            .ThenByDescending(item => item.Slot)
            .Select(item => item.Item)
            .ToImmutableArray();
    }

    public static bool TryGetBodyOverlayIdentity(
        string? node,
        out BodyOverlayTarget target,
        out int slot)
    {
        target = default;
        slot = -1;
        if (string.IsNullOrWhiteSpace(node) || !node.EndsWith(']')) return false;
        int open = node.IndexOf(" [Ovl", StringComparison.OrdinalIgnoreCase);
        if (open <= 0) return false;
        string prefix = node[..open];
        if (prefix.Equals("Body", StringComparison.OrdinalIgnoreCase)) target = BodyOverlayTarget.Body;
        else if (prefix.Equals("Hands", StringComparison.OrdinalIgnoreCase)) target = BodyOverlayTarget.Hands;
        else if (prefix.Equals("Feet", StringComparison.OrdinalIgnoreCase)) target = BodyOverlayTarget.Feet;
        else return false;
        ReadOnlySpan<char> digits = node.AsSpan(open + 5, node.Length - open - 6);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out slot) &&
               slot is >= 0 and < MaximumBodyOverlays;
    }

    private static int OverlayIndex(
        SkyrimBodyEditorDocument document,
        BodyOverlayTarget target,
        int slot) =>
        FindIndex(document.BodyOverlays, item =>
            TryGetBodyOverlayIdentity(item.Node, out BodyOverlayTarget zone, out int current) &&
            zone == target && current == slot);

    private static SkyrimRaceMenuPaintCategory PaintCategory(BodyOverlayTarget target) => target switch
    {
        BodyOverlayTarget.Body => SkyrimRaceMenuPaintCategory.Body,
        BodyOverlayTarget.Hands => SkyrimRaceMenuPaintCategory.Hands,
        BodyOverlayTarget.Feet => SkyrimRaceMenuPaintCategory.Feet,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
    };

    private static string OverlayNode(BodyOverlayTarget target, int slot) =>
        $"{target} [Ovl{slot}]";

    private static ImmutableArray<RaceMenuValue> BuildOverlayValues(
        ImmutableArray<RaceMenuValue> source,
        string? diffuse,
        string? normal,
        ImmutableArray<float> tint,
        float? alpha)
    {
        var values = source.Where(value =>
                value.Key != 7 && value.Key != 8 &&
                (value.Key != 9 || value.Index is not (0 or 1)))
            .ToImmutableArray()
            .ToBuilder();
        if (diffuse is not null)
            values.Add(new RaceMenuValue(9, 2, 0, RaceMenuScalar.FromText(diffuse)));
        if (normal is not null)
            values.Add(new RaceMenuValue(9, 2, 1, RaceMenuScalar.FromText(normal)));
        if (tint.Length == 4)
            values.Add(new RaceMenuValue(7, 3, -1, RaceMenuScalar.FromInteger(PackTint(tint))));
        if (alpha is { } opacity)
            values.Add(new RaceMenuValue(8, 4, -1, RaceMenuScalar.FromNumber(opacity)));
        return values.ToImmutable();
    }

    private static void ValidateOverlays(
        ImmutableArray<RaceMenuBodyOverlay> overlays,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (overlays.IsDefault || overlays.Length > MaximumBodyOverlays)
        {
            diagnostics.Add(Error("body-editor-overlay-shape",
                $"Body overlays must be initialized and contain at most {MaximumBodyOverlays} rows."));
            return;
        }
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RaceMenuBodyOverlay? overlay in overlays)
        {
            if (overlay is null || !ValidName(overlay.Node) || !nodes.Add(overlay.Node) ||
                overlay.Tint.IsDefault || overlay.Values.IsDefault ||
                overlay.Tint.Length is not (0 or 4) || overlay.Tint.Any(value => !ValidUnit(value)) ||
                overlay.Alpha is { } alpha && !ValidUnit(alpha) ||
                overlay.Diffuse is { } diffuse && !ValidTexture(diffuse) ||
                overlay.Normal is { } normal && !ValidTexture(normal) ||
                overlay.Values.Any(value => value is null || value.Data is null))
            {
                diagnostics.Add(Error("body-editor-overlay-value", "A RaceMenu overlay row is invalid."));
            }
        }
    }

    private static bool OverlayArrayEquivalent(
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
                a.Alpha != b.Alpha || !a.Tint.SequenceEqual(b.Tint) ||
                !a.Values.SequenceEqual(b.Values)) return false;
        }
        return true;
    }
}
