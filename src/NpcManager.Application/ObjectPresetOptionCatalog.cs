using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class ObjectPresetOptionCatalog
{
    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static LegacyCommandOption[] FalloutGame() =>
    [
        O("edition", false, "fallout4",
            "Selects Fallout 4; canonical --edition wins over --game.", "fallout4"),
        O("game", false, "fallout4",
            "Alias for --edition used only when canonical --edition is absent.", "fallout4")
    ];

    private static LegacyCommandOption[] PresetPair() =>
    [
        O("format", true, "looksmenu|racemenu-jslot|jslot",
            "Preset dialect: looksmenu pairs only with fallout4; racemenu-jslot and its jslot alias pair only with skyrimse.",
            "looksmenu", "racemenu-jslot", "jslot"),
        O("edition", true, "fallout4|skyrimse",
            "Exact game edition paired with --format.", "fallout4", "skyrimse")
    ];

    private static LegacyCommandOption[] PresetInput() =>
    [
        .. PresetPair(),
        O("input", true, "<K-local-preset.json>",
            "Existing explicit preset input under the configured K-local workspace.")
    ];

    private static readonly ImmutableArray<string> Sections =
    [
        "body-weight", "body-regions", "body-sliders", "overlays",
        "skin-override", "lm-skin-template", "outfit", "face-parts",
        "hair-color", "face-tints", "face-morphs", "face-bone-regions",
        "sculpt", "chargen-flag", "all"
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["object-template propose"] =
            [
                .. FalloutGame(),
                O("plugin", true, "<K-local-plugin>", "Existing copied source plugin."),
                O("source", true, "<non-null-hexadecimal-form-id>", "Non-null source ARMO FormID."),
                O("combinations", false, "<inline-JSON-object|@K-local-file>",
                    "Combination branch JSON; required with --includes when --properties is absent and ignored when --properties is present."),
                O("includes", false, "<inline-JSON-array|@K-local-file>",
                    "Combination include rows; required with --combinations when --properties is absent and ignored when --properties is present."),
                O("properties", false, "<inline-JSON-array|@K-local-file>",
                    "Properties-presence branch JSON; option presence wins over --combinations and --includes."),
                O("output", true, "<name.object-template-proposal.json|name.object-template-properties-proposal.json>",
                    "Fresh branch-specific proposal output; the selected service validates the exact suffix.")
            ],
            ["object-template write"] =
            [
                .. FalloutGame(),
                O("proposal", true, "<name.object-template-proposal.json>",
                    "Existing typed hash-bound combinations proposal."),
                O("properties", false, "<name.object-template-properties-proposal.json>",
                    "Optional typed OMOD properties proposal consumed with the required combinations proposal."),
                O("output", true, "<new-plugin.esp>",
                    "Fresh ordinary-plugin output; existing destinations are refused.")
            ],
            ["preset inspect"] = [.. PresetInput()],
            ["preset export"] =
            [
                .. PresetInput(),
                O("output", true, "<new-preset.json>",
                    "Fresh deterministic preset copy; source and destination must differ and existing output is refused.")
            ],
            ["preset diff"] =
            [
                .. PresetPair(),
                O("left", true, "<K-local-left-preset.json>", "Existing left preset input."),
                O("right", true, "<K-local-right-preset.json>", "Existing right preset input.")
            ],
            ["preset resolve"] =
            [
                O("identifier", true, "<Plugin|FormID>",
                    "Portable preset identifier; an invalid portable split remains unresolved rather than selecting a live profile."),
                O("load-order", true, "<K-local-load-order.json>",
                    "Schema-1 edition/plugins document (name, order, enabled), or legacy plugin-to-byte-index map; no live-profile inference."),
                O("data-root", false, "<K-local-data-root>",
                    "Optional copied plugin directory. Exact TES4 light flags determine separate full/light indexes and the 12-bit light local-ID mask.")
            ],
            ["appearance copy"] =
            [
                .. PresetPair(),
                O("from", true, "<K-local-source-preset.json>", "Existing source appearance preset."),
                O("to", true, "<K-local-target-preset.json>", "Existing target appearance preset."),
                O("output", true, "<new-preset.json>", "Fresh merged preset output; existing destinations are refused."),
                new LegacyCommandOption("sections", true, "<section,...|all>", Sections,
                    "Nonempty comma-separated case-insensitive section list or the exact all expansion; duplicates and unsupported game sections are refused.")
            ]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Rows.ContainsKey(name);
    public static ImmutableArray<LegacyCommandOption> For(string name) => Rows[name];

    public static AgentValueKind ValueKindFor(LegacyCommandOption option) => option.Name switch
    {
        "edition" or "game" or "format" => AgentValueKind.Enum,
        "source" or "identifier" => AgentValueKind.ArtifactReference,
        "combinations" or "includes" or "properties" when option.ValueSyntax.Contains("JSON", StringComparison.Ordinal) => AgentValueKind.Json,
        "sections" => AgentValueKind.EnumList,
        _ => AgentValueKind.Path
    };

    public static string? AliasFor(string optionName) => optionName == "game" ? "edition" : null;
}
