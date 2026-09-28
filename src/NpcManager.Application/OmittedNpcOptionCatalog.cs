using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class OmittedNpcOptionCatalog
{
    private static readonly ImmutableArray<string> Games = ["fallout4", "skyrimse"];
    private static readonly ImmutableArray<string> InventoryCategories =
        ["unique", "generic", "template", "unused"];
    private static readonly ImmutableArray<string> ResetSections =
        ["identity", "archetype", "weight", "stats", "keywords", "factions", "inventory", "outfits", "perks", "actor-effects", "properties"];
    private static readonly ImmutableArray<string> TemplateCategories =
        ["traits", "stats", "factions", "spell-list", "ai-data", "ai-packages", "model-animation", "base-data", "inventory", "script", "default-package-list", "attack-data", "keywords"];

    private static readonly ImmutableHashSet<string> Names = new[]
    {
        "npc list", "npc search", "npc inspect", "npc reset", "npc materialize-template",
        "npc follower-finish analyze", "npc follower-finish apply", "npc follower-finish verify",
        "npc follower-finish pair-analyze", "npc follower-finish pair-apply", "npc follower-finish pair-verify",
        "npc placement interior analyze", "npc placement interior apply", "npc placement interior verify",
        "preset catalog"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Names.Contains(name);

    public static ImmutableArray<LegacyCommandOption> For(string name) => name switch
    {
        "npc list" or "npc search" => InventoryOptions(),
        "npc inspect" => [.. InventoryOptions(), Option("npc", true, "<hexadecimal-form-id>", "Required inspection target. It is parsed after --form-id and overwrites that inventory filter.")],
        "npc reset" => ResetOptions(),
        "npc materialize-template" => TemplateOptions(),
        "npc follower-finish analyze" or "npc follower-finish pair-analyze" or "npc placement interior analyze" => PhaseOptions("analyze"),
        "npc follower-finish apply" or "npc follower-finish pair-apply" or "npc placement interior apply" => PhaseOptions("apply"),
        "npc follower-finish verify" or "npc follower-finish pair-verify" => PhaseOptions("verify"),
        "npc placement interior verify" =>
        [
            Option("manifest", true, "<manifest.json>", "Exact interior-placement manifest."),
            Option("manifest-sha256", true, "<64-hex-sha256>", "Exact SHA-256 of the interior-placement manifest.")
        ],
        "preset catalog" => CatalogOptions(),
        _ => []
    };

    public static AgentValueKind ValueKindFor(string command, LegacyCommandOption option) => option.Name switch
    {
        "edition" or "game" or "section" or "sex" => AgentValueKind.Enum,
        "filter" when command == "preset catalog" => AgentValueKind.String,
        "filter" or "category" or "categories" => AgentValueKind.EnumList,
        "changed-only" or "dry-run" or "apply" or "compatible-only" => AgentValueKind.Boolean,
        "request-sha256" or "proposal-sha256" or "manifest-sha256" or "expected-sha256" => AgentValueKind.Sha256,
        "npc" or "form-id" or "race" => AgentValueKind.ArtifactReference,
        "data-root" or "plugin" or "current-plugin" or "baseline" or "output" or "proposal" or
            "input-plugin" or "request" or "manifest" or "directory" => AgentValueKind.Path,
        _ => AgentValueKind.String
    };

    public static string? AliasFor(string command, string option) => (command, option) switch
    {
        ("npc list" or "npc search" or "npc inspect", "game") => "edition",
        ("npc list" or "npc search" or "npc inspect", "category") => "filter",
        ("npc reset", "edition") => "game",
        ("npc materialize-template", "edition") => "game",
        _ => null
    };

    public static ImmutableArray<string> ConflictsFor(string command, string option) =>
        [];

    private static ImmutableArray<LegacyCommandOption> InventoryOptions() =>
    [
        Option("edition", false, "fallout4|skyrimse", "One of --edition or --game is required; canonical --edition wins when both are supplied.", Games),
        Option("game", false, "fallout4|skyrimse", "Used only when --edition is absent.", Games),
        Option("data-root", false, "<K-local-copied-Data-root>", "One of --data-root or --plugin is required; explicit --data-root wins when both are supplied."),
        Option("plugin", false, "<K-local-plugin>", "When --data-root is absent, its parent becomes the inventory root and its filename becomes the default plugin selection."),
        Option("plugins", false, "<plugin,...>", "Optional comma-separated plugin selection; it wins over the plugin-derived filename."),
        Option("search", false, "<search-text>", "Optional culture-invariant EditorID/name search; npc search does not require this option."),
        Option("form-id", false, "<hexadecimal-form-id>", "Optional inventory FormID filter."),
        Option("filter", false, "<unique,generic,template,unused>", "Optional comma-separated NPC categories; canonical --filter wins over --category.", InventoryCategories),
        Option("category", false, "<unique,generic,template,unused>", "Used only when --filter is absent.", InventoryCategories),
        Option("changed-only", false, "<value>", "Only the case-insensitive literal true activates changed-only filtering; every other supplied value is treated as false.")
    ];

    private static ImmutableArray<LegacyCommandOption> ResetOptions() =>
    [
        Option("game", false, "fallout4|skyrimse", "One of --game or --edition is required; canonical --game wins when both are supplied.", Games),
        Option("edition", false, "fallout4|skyrimse", "Used only when --game is absent.", Games),
        Option("current-plugin", false, "<K-local-current-plugin>", "One of --current-plugin or --plugin is required; canonical --current-plugin wins when both are supplied."),
        Option("plugin", false, "<K-local-current-plugin>", "Used only when --current-plugin is absent."),
        Option("baseline", true, "<K-local-baseline-plugin>", "Explicit same-plugin baseline."),
        Option("output", true, "<new-plugin>", "Fresh reset plugin output."),
        Option("section", true, string.Join('|', ResetSections), "Exact reset section.", ResetSections),
        Option("npc", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --form-id wins when both are supplied."),
        Option("form-id", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --form-id wins when both are supplied."),
        Option("expected-sha256", false, "<64-hex-sha256>", "Optional during proposal analysis; active apply requires it."),
        Option("proposal", false, "<new-proposal.json>", "Optional fresh persisted proposal path; analysis otherwise remains in memory."),
        BoolOption("dry-run", "True or 1 selects proposal-only mode and cannot be combined with active --apply."),
        BoolOption("apply", "True or 1 applies the analyzed proposal and requires --expected-sha256; false or any other value does not apply.")
    ];

    private static ImmutableArray<LegacyCommandOption> TemplateOptions() =>
    [
        Option("game", false, "fallout4|skyrimse", "One of --game or --edition is required; canonical --game wins when both are supplied.", Games),
        Option("edition", false, "fallout4|skyrimse", "Used only when --game is absent.", Games),
        Option("input-plugin", false, "<K-local-input-plugin>", "One of --input-plugin or --plugin is required; canonical --input-plugin wins when both are supplied."),
        Option("plugin", false, "<K-local-input-plugin>", "Used only when --input-plugin is absent."),
        Option("output", true, "<new-plugin>", "Fresh materialized plugin output."),
        Option("form-id", true, "<hexadecimal-form-id>", "Exact NPC FormID; no --npc spelling is parsed by this handler."),
        Option("categories", false, "<category,...>", "Optional nonempty duplicate-free category set. The service supports stats, factions, spell-list, and keywords and fails closed for the other admitted model categories.", TemplateCategories),
        Option("expected-sha256", false, "<64-hex-sha256>", "Optional during proposal analysis; active apply requires it."),
        Option("proposal", false, "<new-proposal.json>", "Optional fresh persisted proposal path; analysis otherwise remains in memory."),
        BoolOption("dry-run", "True or 1 selects proposal-only mode and cannot be combined with active --apply."),
        BoolOption("apply", "True or 1 applies the analyzed proposal and requires --expected-sha256; false or any other value does not apply.")
    ];

    private static ImmutableArray<LegacyCommandOption> PhaseOptions(string phase) => phase switch
    {
        "analyze" =>
        [
            Option("request", true, "<request.json>", "Exact request document."),
            Option("request-sha256", true, "<64-hex-sha256>", "Exact request-file SHA-256."),
            Option("proposal", true, "<new-proposal.json>", "Fresh proposal output.")
        ],
        "apply" =>
        [
            Option("request", true, "<request.json>", "Exact request document."),
            Option("request-sha256", true, "<64-hex-sha256>", "Exact request-file SHA-256."),
            Option("proposal", true, "<proposal.json>", "Exact reviewed proposal document."),
            Option("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal hash binding.")
        ],
        "verify" =>
        [
            Option("request", true, "<request.json>", "Exact request document."),
            Option("request-sha256", true, "<64-hex-sha256>", "Exact request-file SHA-256."),
            Option("proposal", true, "<proposal.json>", "Exact reviewed proposal document."),
            Option("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal hash binding."),
            Option("manifest", true, "<manifest.json>", "Exact generated manifest; follower and pair verify do not accept a separate manifest hash option.")
        ],
        _ => []
    };

    private static ImmutableArray<LegacyCommandOption> CatalogOptions() =>
    [
        Option("directory", true, "<K-local-preset-directory>", "Flat RaceMenu JSlot catalog directory."),
        Option("filter", false, "<display-name-filter>", "Optional case-insensitive display-name filter."),
        Option("data-root", false, "<K-local-copied-Data-root>", "Static compatibility requires --data-root, --plugins, --race, and --sex together."),
        Option("plugins", false, "<plugin,...>", "Static compatibility requires the complete comma-separated ascending plugin order quartet."),
        Option("race", false, "<Plugin|FormID>", "Static compatibility requires the complete provider quartet."),
        Option("sex", false, "female|male", "Static compatibility requires the complete provider quartet.", "female", "male"),
        Option("compatible-only", false, "<presence>", "Option presence activates compatible-only filtering regardless of its supplied value; it requires the compatibility quartet.")
    ];

    private static LegacyCommandOption BoolOption(string name, string description) =>
        Option(name, false, "true|false|1", description, "true", "false", "1");

    private static LegacyCommandOption Option(string name, bool required, string syntax, string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static LegacyCommandOption Option(string name, bool required, string syntax, string description, ImmutableArray<string> values) =>
        new(name, required, syntax, values, description);
}
