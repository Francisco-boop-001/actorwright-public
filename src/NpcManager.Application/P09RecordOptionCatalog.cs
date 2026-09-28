using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class P09RecordOptionCatalog
{
    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static LegacyCommandOption[] Games() =>
    [
        O("edition", false, "fallout4|skyrimse",
            "Selects the game edition; canonical --edition wins over --game.",
            "fallout4", "skyrimse"),
        O("game", false, "fallout4|skyrimse",
            "Alias for --edition used only when canonical --edition is absent.",
            "fallout4", "skyrimse")
    ];

    private static LegacyCommandOption[] Writer(string proposal) =>
    [
        .. Games(),
        O("proposal", true, proposal,
            "Existing typed, hash-bound record proposal."),
        O("output", true, "<new-plugin.esp>",
            "Fresh ordinary-plugin output; existing destinations are refused.")
    ];

    private static LegacyCommandOption[] PatchProposal(
        string option, string syntax, string output) =>
    [
        .. Games(),
        O("plugin", true, "<K-local-plugin>",
            "Existing copied source plugin."),
        O("source", true, "<non-null-hexadecimal-form-id>",
            "Non-null source record FormID."),
        O(option, true, syntax,
            "Case-sensitive inline JSON or @existing-K-local-file; exact dialect is published in limitations."),
        O("output", true, output,
            "Fresh typed proposal output; existing destinations are refused.")
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["outfit list"] =
            [
                .. Games(),
                O("data-root", false, "<K-local-copied-Data-root>",
                    "Explicit copied Data root; canonical --data-root wins over the --plugin parent fallback data root."),
                O("plugin", false, "<K-local-plugin>",
                    "Alternative source plugin path; its parent is the fallback data root and its filename is the fallback plugin order."),
                O("plugins", false, "<plugin,...>",
                    "Optional comma-separated trimmed plugin order; explicit --plugins wins over the --plugin filename fallback."),
                O("query", false, "<search-text>",
                    "Optional search text; canonical --query wins over --search."),
                O("search", false, "<search-text>",
                    "Alias for --query used only when canonical --query is absent.")
            ],
            ["outfit propose"] = OutfitProposal(),
            ["outfit create"] = OutfitProposal(),
            ["outfit write"] = [.. Writer("<name.outfit-proposal.json>")],
            ["leveled-list propose"] =
            [
                .. Games(),
                O("plugin", true, "<K-local-plugin>", "Existing copied source plugin."),
                O("list", true, "<non-null-hexadecimal-form-id>", "Non-null source LVLI FormID."),
                O("entries", true, "<inline-JSON-array>",
                    "Strict inline array of 1..4096 case-sensitive {item,level,count,chanceNone} objects; item is Plugin|FormID, level/count are nonzero uint16, and chanceNone is uint8 constrained to 0..100 by the service."),
                O("output", true, "<name.leveled-list-proposal.json>", "Fresh typed LVLI proposal output."),
                O("editor-id", false, "<editor-id>", "Optional replacement EditorID."),
                O("chance-none", false, "<0..255>", "Optional list-level byte; service validation restricts it to 0..100."),
                O("max-count", false, "<0..255>", "Optional uint8 maximum count; Skyrim write later requires zero."),
                O("calc-all-levels", false, "true|false", "Exact Boolean CalculateAllLevels flag.", "true", "false"),
                O("calc-each-in-count", false, "true|false", "Exact Boolean CalculateEachInCount flag.", "true", "false"),
                O("use-all", false, "true|false", "Exact Boolean UseAll flag.", "true", "false")
            ],
            ["leveled-list resolve"] =
            [
                .. Games(),
                O("list", true, "<name.leveled-list-proposal.json>", "Existing typed LVLI proposal."),
                O("seed", true, "<signed-64-bit-integer>", "Signed Int64 deterministic preview seed."),
                O("output", true, "<name.leveled-list-resolution.json>", "Fresh deterministic resolution output.")
            ],
            ["leveled-list write"] = [.. Writer("<name.leveled-list-proposal.json>")],
            ["armor propose"] = [.. PatchProposal("patch", "<inline-JSON-object|@K-local-file>", "<name.armor-proposal.json>")],
            ["armor damage-resist"] = [.. PatchProposal("damage-resist", "<inline-JSON-array|@K-local-file>", "<name.armor-damage-resist-proposal.json>")],
            ["armor-addon propose"] =
            [
                .. Games(),
                O("plugin", true, "<K-local-plugin>", "Existing copied source plugin."),
                O("source", true, "<non-null-hexadecimal-form-id>", "Non-null source ARMA/ARMO FormID."),
                O("patch", false, "<inline-JSON-object|@K-local-file>",
                    "Record-proposal branch JSON; required when --models is absent and ignored when --models is present."),
                O("models", false, "<inline-JSON-array|@K-local-file>",
                    "Model-entry branch JSON; option presence takes precedence over --patch."),
                O("output", true, "<name.armor-addon-proposal.json|name.armor-addon-model-proposal.json>",
                    "Fresh branch-specific proposal output; the required suffix is validated by the selected service.")
            ],
            ["armor write"] = [.. Writer("<name.armor-proposal.json>")],
            ["armor-addon write"] = [.. Writer("<name.armor-addon-proposal.json>")],
            ["material-swap propose"] = [.. PatchProposal("patch", "<inline-JSON-object|@K-local-file>", "<name.material-swap-proposal.json>")],
            ["material-swap write"] = [.. Writer("<name.material-swap-proposal.json>")]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    private static ImmutableArray<LegacyCommandOption> OutfitProposal() =>
    [
        .. Games(),
        O("plugin", true, "<K-local-plugin>", "Existing copied source plugin."),
        O("source", true, "<non-null-hexadecimal-form-id>", "Non-null source OTFT FormID."),
        O("actor-race", false, "<Plugin|FormID>", "Skyrim actor race admission through copied ARMO/ARMA providers beside the source. Excluded races refuse; use Finish Core outfitRacePolicy=clone for reviewed composition."),
        O("items", true, "<JSON-array|comma-separated-Plugin|FormID-list>",
            "One to 4096 item FormReferences as a JSON string array or comma-separated list."),
        O("output", true, "<name.outfit-proposal.json>", "Fresh typed OTFT proposal output."),
        O("mode", true, "new|override", "Proposal mode.", "new", "override"),
        O("target-form", false, "<plugin-local-hexadecimal-form-id>",
            "Canonical target FormID; required for new and forbidden for override."),
        O("target-form-id", false, "<plugin-local-hexadecimal-form-id>",
            "Alias for --target-form used only when canonical --target-form is absent."),
        O("editor-id", false, "<editor-id>",
            "Required for new proposals and forbidden for override proposals.")
    ];

    public static bool Contains(string name) => Rows.ContainsKey(name);

    public static ImmutableArray<LegacyCommandOption> For(string name) => Rows[name];

    public static AgentValueKind ValueKindFor(
        string commandName, LegacyCommandOption option) => option.Name switch
    {
        "edition" or "game" or "mode" => AgentValueKind.Enum,
        "source" or "target-form" or "target-form-id" or "actor-race" => AgentValueKind.ArtifactReference,
        "list" when commandName == "leveled-list propose" => AgentValueKind.ArtifactReference,
        "items" or "entries" or "patch" or "models" or "damage-resist" => AgentValueKind.Json,
        "chance-none" or "max-count" or "seed" => AgentValueKind.Integer,
        "calc-all-levels" or "calc-each-in-count" or "use-all" => AgentValueKind.Boolean,
        "editor-id" => AgentValueKind.Identifier,
        "plugins" or "query" or "search" => AgentValueKind.String,
        _ => AgentValueKind.Path
    };

    public static string? AliasFor(string optionName) => optionName switch
    {
        "game" => "edition",
        "search" => "query",
        "target-form-id" => "target-form",
        _ => null
    };
}
