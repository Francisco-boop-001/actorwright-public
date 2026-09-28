using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class SkyrimBodyEditorDocumentRules
{
    public static SkyrimBodyEditorDocument SetWeight(
        SkyrimBodyEditorDocument document,
        float weight)
    {
        RequireValid(document);
        if (!float.IsFinite(weight) || weight is < 0F or > 100F)
            throw new ArgumentOutOfRangeException(nameof(weight));
        return document with { Weight = weight };
    }

    public static SkyrimBodyEditorDocument SetBodySlideValue(
        SkyrimBodyEditorDocument document,
        string name,
        float value)
    {
        RequireValid(document);
        string normalized = name?.Trim() ?? string.Empty;
        if (!ValidName(normalized)) throw new ArgumentException("A valid BodySlide name is required.", nameof(name));
        if (!ValidUnit(value)) throw new ArgumentOutOfRangeException(nameof(value));
        int index = FindIndex(document.BodySlide, item =>
            string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase));
        ImmutableArray<BodySlideSliderValue> result;
        if (Math.Abs(value) < BodySlideZeroEpsilon)
        {
            result = index < 0
                ? document.BodySlide
                : document.BodySlide.RemoveAt(index);
        }
        else if (index < 0)
        {
            if (document.BodySlide.Length >= MaximumBodySlideRows)
                throw new InvalidOperationException("The BodySlide row limit has been reached.");
            result = document.BodySlide.Add(new BodySlideSliderValue(normalized, value));
        }
        else
        {
            BodySlideSliderValue current = document.BodySlide[index];
            result = document.BodySlide.SetItem(index, current with { Value = value });
        }
        return document with { BodySlide = result };
    }

    public static ImmutableArray<BodySlideSliderValue> FilterBodySlide(
        SkyrimBodyEditorDocument document,
        IEnumerable<string> catalogNames,
        string? search)
    {
        RequireValid(document);
        ArgumentNullException.ThrowIfNull(catalogNames);
        string query = search?.Trim() ?? string.Empty;
        var names = catalogNames
            .Where(ValidName)
            .Concat(document.BodySlide.Select(item => item.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToImmutableArray();
        return names.Select(name => new BodySlideSliderValue(
                name,
                document.BodySlide.FirstOrDefault(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? 0F))
            .ToImmutableArray();
    }

    private static void ValidateWeight(float weight, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!float.IsFinite(weight) || weight is < 0F or > 100F)
            diagnostics.Add(Error("body-editor-weight", "Skyrim weight must be finite and between 0 and 100."));
    }

    private static void ValidateBodySlide(
        ImmutableArray<BodySlideSliderValue> values,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (values.IsDefault || values.Length > MaximumBodySlideRows)
        {
            diagnostics.Add(Error("body-editor-bodyslide-shape",
                $"BodySlide values must be initialized and contain at most {MaximumBodySlideRows} rows."));
            return;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BodySlideSliderValue? value in values)
        {
            if (value is null || !ValidName(value.Name) || !names.Add(value.Name) ||
                !ValidUnit(value.Value) || Math.Abs(value.Value) < BodySlideZeroEpsilon)
            {
                diagnostics.Add(Error("body-editor-bodyslide-value",
                    "BodySlide rows require unique safe names and non-zero unit values."));
            }
        }
    }
}
