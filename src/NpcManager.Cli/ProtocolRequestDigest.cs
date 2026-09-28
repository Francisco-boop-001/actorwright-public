using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NpcManager.Cli;

public static class ProtocolRequestDigest
{
    private static readonly HashSet<string> ExcludedOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "correlation",
            "protocol",
            "json",
            "help"
        };

    public static string Compute(ParsedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var duplicateCounts = CountsByOption(command.DuplicateOptions);
        var valuelessCounts = CountsByOption(command.ValuelessOptions);
        var canonicalBytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonicalBytes))
        {
            writer.WriteStartObject();
            writer.WriteString("command", command.Name.ToLowerInvariant());
            writer.WritePropertyName("positionals");
            writer.WriteStartArray();
            foreach (var positional in RemainingPositionals(command))
                writer.WriteStringValue(positional);
            writer.WriteEndArray();
            writer.WritePropertyName("options");
            writer.WriteStartObject();
            foreach (var option in command.Options
                         .Where(item => !ExcludedOptions.Contains(item.Key))
                         .Select(item => new KeyValuePair<string, string>(
                             item.Key.ToLowerInvariant(),
                             item.Value))
                         .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                duplicateCounts.TryGetValue(option.Key, out var duplicateCount);
                valuelessCounts.TryGetValue(option.Key, out var valuelessCount);
                writer.WritePropertyName(option.Key);
                if (duplicateCount == 0 && valuelessCount == 0)
                {
                    writer.WriteStringValue(option.Value);
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("value", option.Value);
                writer.WriteNumber("occurrences", duplicateCount + 1);
                writer.WriteNumber("valuelessOccurrences", valuelessCount);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(canonicalBytes.WrittenSpan));
    }

    private static Dictionary<string, int> CountsByOption(
        IEnumerable<string> options) =>
        options
            .Where(option => !ExcludedOptions.Contains(option))
            .Select(option => option.ToLowerInvariant())
            .OrderBy(option => option, StringComparer.Ordinal)
            .GroupBy(option => option, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);

    private static IEnumerable<string> RemainingPositionals(ParsedCommand command)
    {
        var commandWordCount = command.Name.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries).Length;
        return command.Positionals.Skip(commandWordCount);
    }
}
