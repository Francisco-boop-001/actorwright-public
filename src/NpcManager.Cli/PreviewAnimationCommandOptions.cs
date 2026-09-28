using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal static class PreviewAnimationCommandOptions
{
    internal static bool TryParseRequest(ParsedCommand command, string commandName,
        out PreviewAnimationListRequest request, out string message)
    {
        request = default!;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out var edition))
        { message = $"{commandName} requires --edition|--game fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("manifest", out var manifest))
        { message = $"{commandName} requires --manifest <json>."; return false; }
        if (!TryParseBoolean(command, "female", false, out var isFemale, out message)) return false;
        if (!TryParseBoolean(command, "first-person", false, out var showFirstPerson, out message)) return false;
        if (!TryParseFilter(command, out var filter, out message)) return false;
        try
        {
            request = new PreviewAnimationListRequest(edition, new WorkspacePath(manifest), isFemale, showFirstPerson, filter);
        }
        catch (ArgumentException exception)
        {
            message = exception.Message;
            return false;
        }
        message = string.Empty;
        return true;
    }

    private static bool TryParseBoolean(ParsedCommand command, string option, bool defaultValue,
        out bool value, out string message)
    {
        value = defaultValue;
        if (!command.Options.TryGetValue(option, out var text))
        { message = string.Empty; return true; }
        if (!bool.TryParse(text, out value))
        { message = $"{command.Name} --{option} accepts true or false."; return false; }
        message = string.Empty;
        return true;
    }

    private static bool TryParseFilter(ParsedCommand command, out string? filter, out string message)
    {
        filter = null;
        if (!command.Options.TryGetValue("filter", out var text))
        { message = string.Empty; return true; }
        if (text.Length > 256 || text.Any(char.IsControl) || text != text.Trim())
        { message = $"{command.Name} --filter must be at most 256 characters without control characters."; return false; }
        filter = text;
        message = string.Empty;
        return true;
    }
}
