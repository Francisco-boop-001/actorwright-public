using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class PatchReadonlyFaceGenOptionCatalog
{
    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static readonly ImmutableArray<LegacyCommandOption> Diagnose =
    [
        O("edition", false, "fallout4|skyrimse", "Selects the game edition; canonical --edition wins over --game.", "fallout4", "skyrimse"),
        O("game", false, "fallout4|skyrimse", "Alias for --edition used only when canonical --edition is absent.", "fallout4", "skyrimse"),
        O("manifest", true, "<K-local-manifest.json>", "Existing FaceGen shape manifest."),
        O("npc", false, "<hexadecimal-form-id>", "Optional NPC FormID filter.")
    ];

    private static readonly ImmutableArray<string> SkyrimFlags =
    [
        "female", "essential", "ischargenfacepreset", "respawn", "autocalcstats",
        "unique", "doesntaffectstealthmeter", "skyrimusetemplate", "protected",
        "summonable", "doesnotbleed", "bleedoutoverride", "oppositegenderanims",
        "simpleactor", "skyrimloopedscript", "skyrimloopedaudio", "isghost",
        "invulnerable", "pc-level-mult"
    ];

    private static readonly ImmutableArray<LegacyCommandOption> FacePatch =
    [
        O("game", false, "fallout4|skyrimse", "One of --game or --edition is required; canonical --game wins when both are supplied.", "fallout4", "skyrimse"),
        O("edition", false, "fallout4|skyrimse", "One of --game or --edition is required; canonical --game wins when both are supplied.", "fallout4", "skyrimse"),
        O("plugin", true, "<K-local-plugin>", "Source plugin to inspect."),
        O("output", true, "<new-plugin>", "New patched plugin output."),
        O("data-root", true, "<K-local-data-root>", "Data root used for provider resolution."),
        O("npc", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --form-id wins when both are supplied."),
        O("form-id", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --form-id wins when both are supplied."),
        O("headparts", false, "<JSON-array|type=Plugin|FormID,...>", "At least one of --headparts or --hair-color is required."),
        O("headpart-replace", false, "<old-Plugin|FormID=new-local-FormID>", "Replace exactly one PNAM with an output-owned HDPT; exclusive with --headparts."),
        O("hair-color", false, "<Plugin|FormID|none>", "At least one of --headparts or --hair-color is required."),
        O("expected-sha256", false, "<64-hex-sha256>", "Optional exact source-plugin SHA-256."),
        O("proposal", false, "<new-proposal.json>", "Optional proposal artifact path."),
        O("dry-run", false, "true|false|1", "Optional proposal-only flag; true or 1 activates it and cannot be combined with --apply true or 1.", "true", "false", "1"),
        O("apply", false, "true|false|1", "Optional mutation flag; true or 1 activates it and cannot be combined with --dry-run true or 1.", "true", "false", "1")
    ];

    private static readonly ImmutableArray<LegacyCommandOption> EditPackage =
    [
        O("consolidate", false, "<copied-provider.esp[,copied-provider.esp]>", "Fresh-plugin mode imports every provider-owned record with proven reference relocation; use --output and the input hash. Exclusive with scalar/package edit options."),
        O("esl-flag", false, "true|1", "Fresh-plugin mode sets ESL only for Form44 plugins with owned IDs and next ID at most 0xFFF and no CELL/WRLD/ACHR/REFR/NAVM/NAVI. Never compacts existing IDs.", "true", "1"),
        O("output", false, "<fresh-K-local-plugin.esp>", "Fresh plugin for --consolidate or --esl-flag; writes bound evidence and any rebased SEQ beside it."),
        O("edition", false, "skyrimse", "One of --edition or --game is required; canonical --edition wins when both are supplied.", "skyrimse"),
        O("game", false, "skyrimse", "One of --edition or --game is required; canonical --edition wins when both are supplied.", "skyrimse"),
        O("input-plugin", true, "<K-local-plugin>", "Copied source plugin to inspect and edit."),
        O("input-sha256", false, "<64-hex-sha256>", "One of --input-sha256 or --expected-sha256 is required; canonical --input-sha256 wins when both are supplied."),
        O("expected-sha256", false, "<64-hex-sha256>", "One of --input-sha256 or --expected-sha256 is required; canonical --input-sha256 wins when both are supplied."),
        O("npc", false, "<hexadecimal-form-id>", "Required target NPC FormID for scalar/package edit mode."),
        O("output-root", false, "<new-K-local-directory>", "Required fresh package directory for scalar edit mode; existing output is refused."),
        O("plugin", false, "<new-plugin.esp>", "Required output plugin filename for scalar/package edit mode."),
        O("editor-id", false, "<ASCII-letter[A-Za-z0-9_]{0,63}>", "Replacement NPC EditorID: 1 through 64 ASCII letters, digits, or underscores, starting with a letter."),
        O("name", false, "<0..255-non-control-characters>", "Replacement full display name; an empty value clears it."),
        O("short-name", false, "<0..255-non-control-characters>", "Replacement short display name; an empty value clears it."),
        O("race", false, "<Plugin.esp|0xFormID|none>", "Source-master race reference or none."),
        O("voice", false, "<Plugin.esp|0xFormID|none>", "Source-master voice reference or none."),
        O("class", false, "<Plugin.esp|0xFormID|none>", "Source-master class reference or none."),
        O("combat-style", false, "<Plugin.esp|0xFormID|none>", "Source-master combat-style reference or none."),
        O("level", false, "<invariant-decimal-integer-0..65535>", "Fixed NPC level encoded as an invariant decimal integer from 0 through 65535."),
        O("level-mult", false, "<invariant-decimal-0..65.535-max-3-fraction-digits>", "NPC level multiplier from 0 through 65.535 with at most three fractional digits."),
        O("magicka-offset", false, "<signed-16-bit-integer>", "Magicka offset."),
        O("stamina-offset", false, "<signed-16-bit-integer>", "Stamina offset."),
        O("health-offset", false, "<signed-16-bit-integer>", "Health offset."),
        O("calc-min", false, "<unsigned-16-bit-integer>", "Minimum calculated level."),
        O("calc-max", false, "<unsigned-16-bit-integer>", "Maximum calculated level."),
        O("speed-multiplier", false, "<signed-16-bit-integer>", "Movement speed multiplier."),
        O("disposition", false, "<signed-16-bit-integer>", "Base disposition."),
        O("bleedout", false, "<signed-16-bit-integer>", "Bleedout override."),
        O("player-health", false, "<unsigned-16-bit-integer>", "Base player-health value."),
        O("player-magicka", false, "<unsigned-16-bit-integer>", "Base player-magicka value."),
        O("player-stamina", false, "<unsigned-16-bit-integer>", "Base player-stamina value."),
        O("skill-values", false, "<skill=unsigned-byte,...>", "Nonempty comma-separated unique known skill names with byte values."),
        O("skill-offsets", false, "<skill=unsigned-byte,...>", "Nonempty comma-separated unique known skill names with byte offsets."),
        O("far-model-distance", false, "<invariant-single-precision-number>", "Far-model distance."),
        O("geared-weapons", false, "<unsigned-byte>", "Geared-weapons value from 0 through 255."),
        new("set-flag", false, "<flag,...>", SkyrimFlags, "Comma-separated edition-compatible flags to set; pc-level-mult is accepted as an alias."),
        new("clear-flag", false, "<flag,...>", SkyrimFlags, "Comma-separated edition-compatible flags to clear; pc-level-mult is accepted as an alias."),
        O("keywords", false, "<Plugin.esp|0xFormID,...>", "Replace with a nonempty comma-separated unique reference list."),
        O("add-keyword", false, "<Plugin.esp|0xFormID,...>", "Add a nonempty comma-separated unique reference list."),
        O("remove-keyword", false, "<Plugin.esp|0xFormID,...>", "Remove a nonempty comma-separated unique reference list."),
        O("factions", false, "<Plugin.esp|0xFormID=signed-byte-rank,...>", "Replace with nonempty comma-separated unique faction and signed-byte rank entries."),
        O("add-faction", false, "<Plugin.esp|0xFormID=signed-byte-rank,...>", "Add nonempty comma-separated unique faction and signed-byte rank entries."),
        O("update-faction", false, "<Plugin.esp|0xFormID=signed-byte-rank,...>", "Update nonempty comma-separated unique faction and signed-byte rank entries."),
        O("remove-faction", false, "<Plugin.esp|0xFormID,...>", "Remove a nonempty comma-separated unique faction reference list."),
        O("inventory", false, "<Plugin.esp|0xFormID=signed-32-bit-count,...>", "Replace with nonempty comma-separated item and signed 32-bit count entries."),
        O("add-inventory", false, "<Plugin.esp|0xFormID=signed-32-bit-count,...>", "Add nonempty comma-separated item and signed 32-bit count entries."),
        O("update-inventory", false, "<Plugin.esp|0xFormID=signed-32-bit-count,...>", "Update nonempty comma-separated item and signed 32-bit count entries."),
        O("remove-inventory", false, "<Plugin.esp|0xFormID,...>", "Remove a nonempty comma-separated unique item reference list."),
        O("default-outfit", false, "<Plugin.esp|0xFormID|none>", "Default outfit reference or none."),
        O("sleep-outfit", false, "<Plugin.esp|0xFormID|none>", "Sleep outfit reference or none."),
        O("perks", false, "<Plugin.esp|0xFormID=unsigned-byte-rank,...>", "Replace with nonempty comma-separated perk and unsigned-byte rank entries."),
        O("add-perk", false, "<Plugin.esp|0xFormID=unsigned-byte-rank,...>", "Add nonempty comma-separated perk and unsigned-byte rank entries."),
        O("update-perk", false, "<Plugin.esp|0xFormID=unsigned-byte-rank,...>", "Update nonempty comma-separated perk and unsigned-byte rank entries."),
        O("remove-perk", false, "<Plugin.esp|0xFormID,...>", "Remove a nonempty comma-separated perk reference list."),
        O("actor-effects", false, "<Plugin.esp|0xFormID,...>", "Replace with a nonempty comma-separated unique actor-effect reference list."),
        O("add-actor-effect", false, "<Plugin.esp|0xFormID,...>", "Add a nonempty comma-separated unique actor-effect reference list."),
        O("remove-actor-effect", false, "<Plugin.esp|0xFormID,...>", "Remove a nonempty comma-separated unique actor-effect reference list."),
        O("output-kind", false, "source-mastered-override|standalone-copy", "Required output semantics for scalar/package edit mode.", "source-mastered-override", "standalone-copy")
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["npc patch"] = CoreBodyRecordOptionCatalog.For("body patch"),
            ["npc face-patch"] = FacePatch,
            ["npc edit-package"] = EditPackage,
            ["facegen diagnose"] = Diagnose,
            ["facegen analyze"] = Diagnose,
            ["facegen verify"] = [.. Diagnose,
                O("strict-shapes", false, "<presence-toggle>", "Presence enables strict shape verification; even --strict-shapes false is active.")],
            ["facegen resolve-providers"] =
            [
                .. Diagnose[..2],
                O("data-root", true, "<copied-Data-root>", "Existing copied Data root."),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."),
                O("plugins", true, "<plugin,...>", "Nonempty comma-separated plugins in load order; empty entries are ignored."),
                O("shared-neutral-detail", false, "<presence-toggle>", "Presence includes the shared neutral-detail provider; even the text false is active.")
            ],
            ["facegen plan-pack"] =
            [
                .. Diagnose[..2],
                O("data-root", true, "<copied-Data-root>", "Existing copied Data root."),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."),
                O("plugins", true, "<plugin,...>", "Nonempty comma-separated plugins in load order; empty entries are ignored."),
                O("anchor-plugin", true, "<plugin-name>", "Anchor plugin included in the planned package."),
                O("debug-sandbox", false, "<presence-toggle>", "Presence selects debug-sandbox planning; even the text false is active."),
                O("shared-neutral-detail", false, "<presence-toggle>", "Presence includes the shared neutral-detail provider; even the text false is active.")
            ]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Rows.ContainsKey(name);
    public static bool IsFamily(string name) => name is
        "npc patch" or "body patch" or "npc face-patch" or "npc edit-package" or
        "facegen diagnose" or "facegen analyze" or "facegen verify" or
        "facegen resolve-providers" or "facegen plan-pack";
    public static ImmutableArray<LegacyCommandOption> For(string name) => Rows[name];

    public static AgentValueKind ValueKindFor(string commandName, LegacyCommandOption option)
    {
        if (commandName is "npc patch" or "body patch")
            return CoreBodyRecordOptionCatalog.ValueKindFor("body patch", option);
        if (commandName == "npc face-patch" && option.Name == "plugin")
            return AgentValueKind.Path;
        return option.Name switch
    {
        "apply" or "dry-run" or "clear-skin" or "esl-flag" => AgentValueKind.Boolean,
        "edition" or "game" => AgentValueKind.Enum,
        "manifest" or "data-root" or "input-plugin" or "output-root" or
            "output" or "proposal" => AgentValueKind.Path,
        "npc" or "form-id" or "race" or "voice" or "class" or
            "combat-style" or "default-outfit" or "sleep-outfit" or
            "hair-color" => AgentValueKind.ArtifactReference,
        "expected-sha256" or "input-sha256" => AgentValueKind.Sha256,
        "output-kind" or "sex" => AgentValueKind.Enum,
        "headparts" or "keywords" or "add-keyword" or "remove-keyword" or
            "factions" or "add-faction" or "update-faction" or "remove-faction" or
            "inventory" or "add-inventory" or "update-inventory" or "remove-inventory" or
            "perks" or "add-perk" or "update-perk" or "remove-perk" or
            "actor-effects" or "add-actor-effect" or "remove-actor-effect" => AgentValueKind.String,
        "magicka-offset" or "stamina-offset" or "health-offset" or
            "calc-min" or "calc-max" or "disposition" or "bleedout" or
            "player-health" or "player-magicka" or "player-stamina" or
            "speed-multiplier" or "geared-weapons" => AgentValueKind.Integer,
        "set-flag" or "clear-flag" => AgentValueKind.EnumList,
        "editor-id" or "plugin" or "anchor-plugin" => AgentValueKind.Identifier,
        _ => AgentValueKind.String
    };
    }
}
