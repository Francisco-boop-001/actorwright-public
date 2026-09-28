using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed partial class MutationCommandHandler
{
    private static bool TryReferenceOption(ParsedCommand command, string option,
        out OptionalFormReference reference, out string errorMessage)
    {
        reference = default;
        if (!command.Options.TryGetValue(option, out var value))
        {
            errorMessage = string.Empty;
            return true;
        }
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            reference = OptionalFormReference.Clear();
            errorMessage = string.Empty;
            return true;
        }
        if (!FormReference.TryParse(value, out var parsed))
        {
            errorMessage = $"--{option} must be Plugin.esp|0xXXXXXXXX or none.";
            return false;
        }
        reference = OptionalFormReference.Set(parsed);
        errorMessage = string.Empty;
        return true;
    }

    private static void AddReferenceExpectation(ImmutableArray<MutationChange>.Builder changes, string field,
        OptionalFormReference reference)
    {
        if (!reference.IsSpecified) return;
        changes.Add(new MutationChange(field, null, reference.Value?.ToString() ?? "none"));
    }

    private static bool TryGetEdition(ParsedCommand command, out GameEdition edition, out string errorMessage)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { errorMessage = "The command requires --edition|--game fallout4|skyrimse."; return false; }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryPath(ParsedCommand command, string key, out WorkspacePath path, out string errorMessage)
    {
        path = default;
        if (!command.Options.TryGetValue(key, out var value)) { errorMessage = $"The command requires --{key}."; return false; }
        try { path = new WorkspacePath(value); errorMessage = string.Empty; return true; }
        catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
    }

    private static bool TryFormId(ParsedCommand command, out FormId formId, out string errorMessage)
    {
        formId = default;
        var value = command.Options.GetValueOrDefault("form-id") ?? command.Options.GetValueOrDefault("npc");
        if (value is null || !FormId.TryParse(value, out formId))
        { errorMessage = "The command requires a hexadecimal --form-id|--npc, for example 0x00000800."; return false; }
        errorMessage = string.Empty;
        return true;
    }

}
