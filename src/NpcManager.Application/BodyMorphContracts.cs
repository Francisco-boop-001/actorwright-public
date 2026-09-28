using System.Collections.Immutable;
using System.Globalization;

namespace NpcManager.Application;

/// <summary>The fixed Fallout 4 NPC.MRSV body-region order used by LooksMenu.</summary>
public enum Fallout4BodyRegion
{
    Head,
    UpperTorso,
    Arms,
    LowerTorso,
    Legs
}

public static class Fallout4BodyRegionCatalog
{
    public static ImmutableArray<Fallout4BodyRegion> Ordered { get; } =
        [Fallout4BodyRegion.Head, Fallout4BodyRegion.UpperTorso, Fallout4BodyRegion.Arms,
         Fallout4BodyRegion.LowerTorso, Fallout4BodyRegion.Legs];

    public static string ToWireName(this Fallout4BodyRegion region) => region switch
    {
        Fallout4BodyRegion.Head => "head",
        Fallout4BodyRegion.UpperTorso => "upperTorso",
        Fallout4BodyRegion.Arms => "arms",
        Fallout4BodyRegion.LowerTorso => "lowerTorso",
        Fallout4BodyRegion.Legs => "legs",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown Fallout 4 body region.")
    };

    public static string ToDisplayName(this Fallout4BodyRegion region) => region switch
    {
        Fallout4BodyRegion.Head => "Head",
        Fallout4BodyRegion.UpperTorso => "Upper Torso",
        Fallout4BodyRegion.Arms => "Arms",
        Fallout4BodyRegion.LowerTorso => "Lower Torso",
        Fallout4BodyRegion.Legs => "Legs",
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown Fallout 4 body region.")
    };

    public static bool TryParseWireName(string value, out Fallout4BodyRegion region)
    {
        foreach (var candidate in Ordered)
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.ToDisplayName(), value, StringComparison.OrdinalIgnoreCase))
            {
                region = candidate;
                return true;
            }
        }

        region = default;
        return false;
    }

    public static string FormatValues(IReadOnlyDictionary<Fallout4BodyRegion, float> values) =>
        string.Join(",", Ordered.Select(region =>
            $"{region.ToWireName()}={values[region].ToString("R", CultureInfo.InvariantCulture)}"));
}

/// <summary>Typed five-value Fallout 4 body-region snapshot in canonical MRSV order.</summary>
public sealed record Fallout4BodyMorphValues(float Head, float UpperTorso, float Arms, float LowerTorso, float Legs)
{
    public float Get(Fallout4BodyRegion region) => region switch
    {
        Fallout4BodyRegion.Head => Head,
        Fallout4BodyRegion.UpperTorso => UpperTorso,
        Fallout4BodyRegion.Arms => Arms,
        Fallout4BodyRegion.LowerTorso => LowerTorso,
        Fallout4BodyRegion.Legs => Legs,
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown Fallout 4 body region.")
    };

    public Fallout4BodyMorphValues Apply(IReadOnlyDictionary<Fallout4BodyRegion, float> patch) =>
        new(patch.GetValueOrDefault(Fallout4BodyRegion.Head, Head),
            patch.GetValueOrDefault(Fallout4BodyRegion.UpperTorso, UpperTorso),
            patch.GetValueOrDefault(Fallout4BodyRegion.Arms, Arms),
            patch.GetValueOrDefault(Fallout4BodyRegion.LowerTorso, LowerTorso),
            patch.GetValueOrDefault(Fallout4BodyRegion.Legs, Legs));

    public ImmutableDictionary<Fallout4BodyRegion, float> ToDictionary() =>
        Fallout4BodyRegionCatalog.Ordered.ToImmutableDictionary(region => region, Get);

    public static Fallout4BodyMorphValues Zero { get; } = new(0, 0, 0, 0, 0);
}

public sealed record NpcBodyMorphPatch(ImmutableDictionary<Fallout4BodyRegion, float> Values)
{
    public bool IsEmpty => Values.Count == 0;
}
