using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed partial class MutationCommandHandler
{
    private static bool TryAidt(ParsedCommand command, GameEdition edition, out NpcAidtPatch? patch, out string errorMessage)
    {
        patch = null;
        errorMessage = string.Empty;
        if (!command.Options.TryGetValue("aidt", out string? text)) return true;
        try
        {
            if (edition != GameEdition.SkyrimSpecialEdition) throw new FormatException("--aidt requires Skyrim SE.");
            if (text.StartsWith('@'))
            {
                var path = new WorkspacePath(text[1..]);
                if (!path.IsUnder(ActorwrightWorkspace.ResolveRoot()) || !File.Exists(path.Value) || HasReparsePath(path.Value) ||
                    new FileInfo(path.Value).Length > 16_384)
                    throw new FormatException("An AIDT file must be an ordinary file of at most 16384 bytes under the workspace root.");
                text = File.ReadAllText(path.Value);
            }
            if (text.Length > 16_384) throw new FormatException("--aidt JSON is too large.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--aidt must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var value = new NpcAidtPatch();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new FormatException($"Duplicate AIDT field '{property.Name}'.");
                value = property.Name switch
                {
                    "aggression" => value with { Aggression = AidtEnum<SkyrimNpcFinishCoreAggression>(property) },
                    "confidence" => value with { Confidence = AidtEnum<SkyrimNpcFinishCoreConfidence>(property) },
                    "morality" => value with { Morality = AidtEnum<SkyrimNpcFinishCoreMorality>(property) },
                    "assistance" => value with { Assistance = AidtEnum<SkyrimNpcFinishCoreAssistance>(property) },
                    "energy" when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetByte(out byte energy)
                        => value with { Energy = energy },
                    "energy" => throw new FormatException("AIDT energy must be an integer from 0 through 255."),
                    _ => throw new FormatException($"Unknown AIDT field '{property.Name}'; allowed: aggression, confidence, morality, assistance, energy.")
                };
            }
            if (value.IsEmpty) throw new FormatException("--aidt requires at least one field.");
            patch = value;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static T AidtEnum<T>(JsonProperty property) where T : struct, Enum
    {
        string[] allowed = Enum.GetNames<T>().Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray();
        string? text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        if (text is not null && allowed.Contains(text, StringComparer.Ordinal) && Enum.TryParse(text, true, out T value)) return value;
        throw new FormatException($"AIDT {property.Name} must be one of: {string.Join(", ", allowed)}.");
    }
}
