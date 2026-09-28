using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace NpcManager.TestInfrastructure;

internal static class StandaloneSelectorInventory
{
    private const string ListArgument = "--list-selectors";
    // Selector_* constants are the shared dispatch and inventory contract.
    private const string SelectorFieldPrefix = "Selector_";

    internal static bool TryList(
        string[] args,
        Type programType,
        IEnumerable<string>? delegatedSelectors = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(programType);
        if (args.Length != 1 || !string.Equals(
                args[0], ListArgument, StringComparison.Ordinal))
            return false;

        var selectors = new List<string>();
        foreach (FieldInfo field in programType.GetFields(
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (!field.Name.StartsWith(
                    SelectorFieldPrefix, StringComparison.Ordinal))
                continue;
            if (field.FieldType != typeof(string) || !field.IsLiteral)
                throw new InvalidOperationException(
                    $"Selector field '{field.Name}' must be a string constant.");
            selectors.Add((string?)field.GetRawConstantValue() ?? string.Empty);
        }

        if (delegatedSelectors is not null)
            selectors.AddRange(delegatedSelectors);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? selector in selectors)
        {
            if (string.IsNullOrWhiteSpace(selector) ||
                !selector.StartsWith("--", StringComparison.Ordinal) ||
                selector.Any(char.IsWhiteSpace))
                throw new InvalidOperationException(
                    "Standalone selector inventory contains a malformed selector.");
            if (!seen.Add(selector))
                throw new InvalidOperationException(
                    $"Standalone selector inventory contains duplicate selector '{selector}'.");
        }

        string[] ordered = selectors
            .OrderBy(selector => selector, StringComparer.Ordinal)
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(ordered));
        return true;
    }
}
