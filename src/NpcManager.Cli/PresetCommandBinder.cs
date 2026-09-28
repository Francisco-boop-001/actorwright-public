using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal static class PresetCommandBinder
{
    public static bool TryBind(
        ParsedCommand command,
        string pathOption,
        out PresetParseRequest request,
        out string errorMessage)
    {
        request = default!;
        if (!TryBindFormatEdition(
                command,
                out PresetFormat format,
                out GameEdition edition,
                out errorMessage))
            return false;
        if (!command.Options.TryGetValue(pathOption, out string? path))
        {
            errorMessage = $"preset command requires --{pathOption}.";
            return false;
        }
        try
        {
            request = new PresetParseRequest(
                format,
                edition,
                new WorkspacePath(path));
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    public static bool TryBindFormatEdition(
        ParsedCommand command,
        out PresetFormat format,
        out GameEdition edition,
        out string errorMessage)
    {
        format = default;
        edition = default;
        if (!command.Options.TryGetValue("format", out string? formatValue) ||
            !PresetFormatExtensions.TryParseWireName(formatValue, out format))
        {
            errorMessage =
                "The command requires --format looksmenu|racemenu-jslot.";
            return false;
        }
        if (!command.Options.TryGetValue("edition", out string? editionValue) ||
            !GameEditionExtensions.TryParseWireName(editionValue, out edition))
        {
            errorMessage =
                "The command requires --edition fallout4|skyrimse.";
            return false;
        }
        if ((format == PresetFormat.LooksMenu &&
             edition != GameEdition.Fallout4) ||
            (format == PresetFormat.RaceMenuJslot &&
             edition != GameEdition.SkyrimSpecialEdition))
        {
            errorMessage =
                "LooksMenu is currently bound to fallout4 and RaceMenu .jslot to skyrimse.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }
}
