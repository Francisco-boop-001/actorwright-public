using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaPluginVerifier
{
    private static string? ResolveListExpectation(string? source, string? expected)
    {
        if (expected is null || !expected.StartsWith("add:", StringComparison.Ordinal)) return expected;
        var pieces = expected.Split(';', 2);
        var values = (source ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var value in pieces[0][4..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
        if (pieces.Length == 2 && pieces[1].StartsWith("remove:", StringComparison.Ordinal))
            values.RemoveAll(value => pieces[1][7..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(value, StringComparer.OrdinalIgnoreCase));
        return string.Join(",", values);
    }

    private static string? ResolveFactionExpectation(string? source, string? expected)
    {
        if (expected is null || !expected.StartsWith("add:", StringComparison.Ordinal)) return expected;
        var values = ParseFactionValues(source);
        foreach (var item in expected.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf(':');
            if (separator < 0) continue;
            var operation = item[..separator];
            foreach (var token in item[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (operation == "remove") values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], token, StringComparison.OrdinalIgnoreCase));
                else
                {
                    var faction = token[..token.LastIndexOf('=')];
                    values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], faction, StringComparison.OrdinalIgnoreCase));
                    values.Add(token);
                }
            }
        }
        return string.Join(",", values);
    }

    private static List<string> ParseFactionValues(string? source) =>
        (source ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? ResolveInventoryExpectation(string? source, string? expected)
    {
        if (expected is null) return null;
        if (!expected.StartsWith("add:", StringComparison.Ordinal))
            return string.Join(",", NormalizeInventoryValues(ParseInventoryValues(expected)));
        var values = NormalizeInventoryValues(ParseInventoryValues(source));
        foreach (var item in expected.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf(':');
            if (separator < 0) continue;
            var operation = item[..separator];
            foreach (var token in item[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (operation == "remove") values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], token, StringComparison.OrdinalIgnoreCase));
                else if (operation == "update")
                {
                    var itemId = token[..token.LastIndexOf('=')];
                    values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], itemId, StringComparison.OrdinalIgnoreCase));
                    values.Add(token);
                }
                else
                {
                    var itemId = token[..token.LastIndexOf('=')];
                    var count = int.Parse(token[(token.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                    var index = values.FindIndex(value => string.Equals(value[..value.LastIndexOf('=')], itemId, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) values.Add(token);
                    else
                    {
                        var current = int.Parse(values[index][(values[index].LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                        values[index] = $"{itemId}={checked(current + count)}";
                    }
                }
            }
        }
        return string.Join(",", values);
    }

    private static List<string> ParseInventoryValues(string? source) =>
        (source ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? ResolvePerkExpectation(string? source, string? expected)
    {
        if (expected is null || !expected.StartsWith("add:", StringComparison.Ordinal)) return expected;
        var values = ParsePerkValues(source);
        foreach (var item in expected.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf(':');
            if (separator < 0) continue;
            var operation = item[..separator];
            foreach (var token in item[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (operation == "remove") values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], token, StringComparison.OrdinalIgnoreCase));
                else
                {
                    var perk = token[..token.LastIndexOf('=')];
                    values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], perk, StringComparison.OrdinalIgnoreCase));
                    values.Add(token);
                }
            }
        }
        return string.Join(",", values);
    }

    private static List<string> ParsePerkValues(string? source) =>
        (source ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? ResolvePropertyExpectation(string? source, string? expected)
    {
        if (expected is null || !expected.StartsWith("add:", StringComparison.Ordinal)) return expected;
        var values = ParsePerkValues(source);
        foreach (var item in expected.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf(':');
            if (separator < 0) continue;
            var operation = item[..separator];
            foreach (var token in item[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var keySeparator = token.LastIndexOf('=');
                if (keySeparator <= 0 || keySeparator == token.Length - 1) throw new InvalidDataException("Property expectation is malformed.");
                var actorValue = token[..keySeparator];
                if (operation == "remove") values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], actorValue, StringComparison.OrdinalIgnoreCase));
                else
                {
                    values.RemoveAll(value => string.Equals(value[..value.LastIndexOf('=')], actorValue, StringComparison.OrdinalIgnoreCase));
                    values.Add(token);
                }
            }
        }
        return string.Join(",", values);
    }

    private static string? ResolveBodyMorphExpectation(string? source, string? expected)
    {
        if (expected is null || !expected.StartsWith("patch:", StringComparison.Ordinal)) return expected;
        var values = ParseBodyMorphValues(source);
        foreach (var token in expected[6..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1 ||
                !Fallout4BodyRegionCatalog.TryParseWireName(token[..separator], out var region) ||
                !float.TryParse(token[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("Body morph expectation is malformed.");
            values[region] = value;
        }
        return Fallout4BodyRegionCatalog.FormatValues(values);
    }

    private static Dictionary<Fallout4BodyRegion, float> ParseBodyMorphValues(string? source)
    {
        var values = Fallout4BodyRegionCatalog.Ordered.ToDictionary(region => region, _ => 0F);
        foreach (var token in (source ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0 || separator == token.Length - 1 ||
                !Fallout4BodyRegionCatalog.TryParseWireName(token[..separator], out var region) ||
                !float.TryParse(token[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("MRSV value is malformed.");
            values[region] = value;
        }
        return values;
    }

    private static List<string> NormalizeInventoryValues(IEnumerable<string> entries)
    {
        var values = new List<string>();
        foreach (var token in entries)
        {
            var separator = token.LastIndexOf('=');
            if (separator <= 0 || separator == token.Length - 1) throw new InvalidDataException("Inventory expectation is malformed.");
            var item = token[..separator];
            var count = int.Parse(token[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var index = values.FindIndex(value => string.Equals(value[..value.LastIndexOf('=')], item, StringComparison.OrdinalIgnoreCase));
            if (index < 0) values.Add(token);
            else
            {
                var current = int.Parse(values[index][(values[index].LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                values[index] = $"{item}={checked(current + count)}";
            }
        }
        return values;
    }
}
