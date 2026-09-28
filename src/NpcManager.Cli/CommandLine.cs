using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Cli;

public sealed record ParsedCommand(
    string Name,
    ImmutableArray<string> Positionals,
    ImmutableDictionary<string, string> Options,
    ImmutableArray<string> DuplicateOptions,
    bool Json,
    bool Help,
    string? RequestedProtocol = null,
    string? Correlation = null,
    ImmutableArray<string> ValuelessOptions = default);

public static class CommandLine
{
    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        var positionals = ImmutableArray.CreateBuilder<string>();
        var options = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicateOptions = ImmutableArray.CreateBuilder<string>();
        var valuelessOptions = ImmutableArray.CreateBuilder<string>();
        var seenOptions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var help = false;
        var json = false;
        string? requestedProtocol = null;
        string? correlation = null;

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var key = token[2..];
            if (!seenOptions.Add(key))
                duplicateOptions.Add(key);
            if (string.Equals(key, "help", StringComparison.OrdinalIgnoreCase))
            {
                help = true;
                continue;
            }

            if (string.Equals(key, "json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }

            if (string.Equals(key, "protocol", StringComparison.OrdinalIgnoreCase))
            {
                requestedProtocol = ReadOptionValue(args, ref index);
                continue;
            }

            if (string.Equals(key, "correlation", StringComparison.OrdinalIgnoreCase))
            {
                correlation = ReadOptionValue(args, ref index);
                continue;
            }

            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++index];
            }
            else
            {
                options[key] = "true";
                valuelessOptions.Add(key);
            }
        }

        var commandName = ResolveCommandName(positionals);
        return new ParsedCommand(
            commandName,
            positionals.ToImmutable(),
            options.ToImmutable(),
            duplicateOptions.ToImmutable(),
            json,
            help,
            requestedProtocol,
            correlation,
            valuelessOptions.ToImmutable());
    }

    private static string ReadOptionValue(
        IReadOnlyList<string> args,
        ref int index)
    {
        if (index + 1 < args.Count &&
            !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return args[++index];

        return "true";
    }

    private static string ResolveCommandName(ImmutableArray<string>.Builder positionals)
    {
        if (positionals.Count == 0)
        {
            return "help";
        }

        foreach (var descriptor in CommandCatalog.All
                     .OrderByDescending(item => item.Name.Count(character => character == ' ')))
        {
            var words = descriptor.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (positionals.Count >= words.Length &&
                words.SequenceEqual(positionals.Take(words.Length), StringComparer.OrdinalIgnoreCase))
            {
                return descriptor.Name;
            }
        }

        return positionals.Count == 1
            ? positionals[0]
            : $"{positionals[0]} {positionals[1]}";
    }
}
