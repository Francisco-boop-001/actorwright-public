namespace NpcManager.Application;

public enum WeightTriangleAxis
{
    Thin,
    Muscular,
    Fat
}

public static class WeightTriangleAxisExtensions
{
    public static string ToWireName(this WeightTriangleAxis axis) => axis switch
    {
        WeightTriangleAxis.Thin => "thin",
        WeightTriangleAxis.Muscular => "muscular",
        WeightTriangleAxis.Fat => "fat",
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown FO4 weight axis.")
    };

    public static bool TryParseWireName(string value, out WeightTriangleAxis axis)
    {
        foreach (var candidate in Enum.GetValues<WeightTriangleAxis>())
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase))
            {
                axis = candidate;
                return true;
            }
        }

        axis = default;
        return false;
    }
}

/// <summary>Normalized Fallout 4 MWGT barycentric coordinates.</summary>
public readonly record struct WeightTriangle(float Thin, float Muscular, float Fat)
{
    public float Sum => Thin + Muscular + Fat;
}

public static class WeightTriangleMath
{
    private const float Epsilon = 0.0001F;

    /// <summary>Matches WeightTriangleControl.Normalize: clamp to non-negative values,
    /// normalize to a unit simplex, and use (0.5, 0.5, 0) for a degenerate point.</summary>
    public static bool TryNormalize(float thin, float muscular, float fat,
        out WeightTriangle result, out string error)
    {
        result = default;
        error = string.Empty;
        if (!float.IsFinite(thin) || !float.IsFinite(muscular) || !float.IsFinite(fat))
        {
            error = "FO4 weight-triangle values must be finite numbers.";
            return false;
        }

        var t = Math.Max(0.0F, thin);
        var m = Math.Max(0.0F, muscular);
        var f = Math.Max(0.0F, fat);
        var sum = t + m + f;
        result = sum < Epsilon ? new WeightTriangle(0.5F, 0.5F, 0.0F) :
            new WeightTriangle(t / sum, m / sum, f / sum);
        return true;
    }

    /// <summary>Matches EditBody_Form.RedistributeMwgt: clamp one axis and preserve
    /// the current ratio of the other two axes, falling back to an even split at a corner.</summary>
    public static bool TryRedistribute(WeightTriangle current, WeightTriangleAxis axis, float value,
        out WeightTriangle result, out string error)
    {
        result = default;
        error = string.Empty;
        if (!TryNormalize(current.Thin, current.Muscular, current.Fat, out var normalized, out error) ||
            !float.IsFinite(value))
        {
            if (string.IsNullOrEmpty(error)) error = "The requested weight axis value must be finite.";
            return false;
        }

        var changed = Math.Clamp(value, 0.0F, 1.0F);
        var remaining = 1.0F - changed;
        var (a, b) = axis switch
        {
            WeightTriangleAxis.Thin => (normalized.Muscular, normalized.Fat),
            WeightTriangleAxis.Muscular => (normalized.Thin, normalized.Fat),
            WeightTriangleAxis.Fat => (normalized.Thin, normalized.Muscular),
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown FO4 weight axis.")
        };
        var otherSum = a + b;
        var first = otherSum < Epsilon ? remaining * 0.5F : remaining * (a / otherSum);
        var second = otherSum < Epsilon ? remaining * 0.5F : remaining * (b / otherSum);
        result = axis switch
        {
            WeightTriangleAxis.Thin => new WeightTriangle(changed, first, second),
            WeightTriangleAxis.Muscular => new WeightTriangle(first, changed, second),
            WeightTriangleAxis.Fat => new WeightTriangle(first, second, changed),
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown FO4 weight axis.")
        };
        return true;
    }
}
