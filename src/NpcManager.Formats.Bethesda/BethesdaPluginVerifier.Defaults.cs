using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaPluginVerifier
{
    private static readonly (string Label, string Field, bool Reference)[] DefaultAuditFields =
    [
        ("class", "Class", true), ("combatStyle", "CombatStyle", true), ("aidt", "AIDT", false),
        ("packages", "Packages", true), ("defaultPackageList", "DefaultPackageList", true),
        ("defaultOutfit", "DefaultOutfit", true), ("voice", "Voice", true), ("level", "Level", false),
        ("namePlaceholder", "Name", false)
    ];

    /// <summary>Observed equality to caller-authenticated template bytes; this does not establish historical inheritance.</summary>
    public static ImmutableArray<string> CompareBlankNpcDefaults(byte[] pluginBytes, PluginName filePlugin, FormReference actor,
        byte[] templateBytes, PluginName templatePlugin, FormId templateFormId, CancellationToken cancellationToken)
    {
        var actorRecord = Parse(pluginBytes, actor.FormId, actor.Plugin, filePlugin, cancellationToken);
        var template = Parse(templateBytes, templateFormId, templatePlugin, templatePlugin, cancellationToken);
        return DefaultAuditFields.Where(field => string.Equals(
                actorRecord.ReadValue(GameEdition.SkyrimSpecialEdition, field.Field),
                template.ReadValue(GameEdition.SkyrimSpecialEdition, field.Field),
                field.Reference ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Select(field => field.Label).ToImmutableArray();
    }
}
