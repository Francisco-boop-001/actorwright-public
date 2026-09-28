using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class OmittedFaceRuntimeOptionCatalog
{
    private static readonly ImmutableArray<string> Games = ["fallout4", "skyrimse"];
    private static readonly ImmutableArray<string> Skyrim = ["skyrimse"];
    private static readonly ImmutableArray<string> ResetSections =
        ["face-parts", "tints", "vertex-morphs", "bone-regions", "skyrim-morphs", "skyrim-tints"];

    private static readonly ImmutableHashSet<string> Names = new[]
    {
        "face tint patch", "face morph patch", "face morph extended", "face sculpt patch",
        "face pose resolve", "face reset", "animation list", "animation tree",
        "runtime smoke verify", "runtime smoke verify-all", "pipeline preset-to-npc"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Names.Contains(name);

    public static ImmutableArray<LegacyCommandOption> For(string name) => name switch
    {
        "face tint patch" => PluginPatch("layers", "<inline-JSON-array|@K-local-file>"),
        "face morph patch" =>
        [
            .. PluginPatchPrefix(),
            O("vanilla", false, "<inline-JSON-object|@K-local-file>", "Canonical native NAM9/NAMA JSON; at least one alias is required and it wins over --morphs."),
            O("morphs", false, "<inline-JSON-object|@K-local-file>", "Compatibility alias used only when --vanilla is absent."),
            .. MixedTail()
        ],
        "face morph extended" => PresetPatch("extended"),
        "face sculpt patch" => PresetPatch("sculpt"),
        "face pose resolve" =>
        [
            .. GamePair(gameWins: true, Games),
            O("npc", true, "<nonzero-hexadecimal-form-id>", "Nonzero target NPC FormID."),
            O("preset", true, "<K-local-face-pose.json>", "Existing K-local closed face-pose JSON document.")
        ],
        "face reset" =>
        [
            .. GamePair(gameWins: true, Games),
            O("npc", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --npc wins."),
            O("form-id", false, "<hexadecimal-form-id>", "Compatibility alias used only when --npc is absent."),
            O("current", true, "<K-local-current.face.json>", "Existing current face-editor snapshot."),
            O("baseline", true, "<K-local-baseline.face.json>", "Existing construction baseline snapshot."),
            O("output", true, "<new-face.json>", "Fresh face snapshot output; existing files are refused."),
            O("section", true, string.Join('|', ResetSections), "Exact face-editor section to replace.", ResetSections),
            .. MixedTail()
        ],
        "animation list" or "animation tree" =>
        [
            .. GamePair(gameWins: false, Games),
            O("manifest", true, "<K-local-preview-manifest.json>", "Existing K-local preview animation manifest."),
            O("female", false, "true|false", "Exact Boolean text; omission defaults to false.", "true", "false"),
            O("first-person", false, "true|false", "Exact Boolean text; omission defaults to false.", "true", "false"),
            O("filter", false, "<0..256-printable-trimmed-characters>", "Optional filter of at most 256 characters, with no control characters and no leading or trailing whitespace.")
        ],
        "runtime smoke verify" =>
        [
            .. GamePair(gameWins: false, Games),
            O("runtime-report", true, "<K-local-runtime-report.json>", "Operator-supplied runtime-smoke report for the selected game."),
            O("package-acceptance", true, "<K-local-package-acceptance.json>", "Package acceptance report whose archive hash and false runtimeReleaseClaim are checked.")
        ],
        "runtime smoke verify-all" =>
        [
            O("fallout4-report", true, "<K-local-fallout4-runtime-report.json>", "Fallout 4 runtime-smoke report."),
            O("skyrimse-report", true, "<K-local-skyrimse-runtime-report.json>", "Skyrim SE runtime-smoke report."),
            O("package-acceptance", true, "<K-local-package-acceptance.json>", "One package acceptance report shared by both game reports.")
        ],
        "pipeline preset-to-npc" => Pipeline(),
        _ => []
    };

    public static AgentValueKind ValueKindFor(string option) => option switch
    {
        "edition" or "game" or "format" or "section" => AgentValueKind.Enum,
        "female" or "first-person" or "dry-run" or "apply" => AgentValueKind.Boolean,
        "expected-sha256" => AgentValueKind.Sha256,
        "npc" or "form-id" => AgentValueKind.ArtifactReference,
        "layers" or "vanilla" or "morphs" or "extended" or "sculpt" => AgentValueKind.Json,
        "mod-name" or "name" or "filter" => AgentValueKind.String,
        "editor-id" => AgentValueKind.Identifier,
        _ => AgentValueKind.Path
    };

    public static AgentValueKind ValueKindFor(string command, string option) =>
        command == "pipeline preset-to-npc" && option == "plugin"
            ? AgentValueKind.Identifier
            : ValueKindFor(option);

    public static string? AliasFor(string command, string option) => (command, option) switch
    {
        ("face tint patch" or "face morph patch" or "face morph extended" or
            "face sculpt patch" or "face pose resolve" or "face reset", "edition") => "game",
        ("animation list" or "animation tree" or "runtime smoke verify" or
            "pipeline preset-to-npc", "game") => "edition",
        ("face tint patch" or "face morph patch", "npc") => "form-id",
        ("face reset", "form-id") => "npc",
        ("face morph patch", "morphs") => "vanilla",
        _ => null
    };

    private static ImmutableArray<LegacyCommandOption> PluginPatch(string payload, string syntax) =>
        [.. PluginPatchPrefix(), O(payload, true, syntax, "Required inline JSON or @existing-K-local-file patch payload."), .. MixedTail()];

    private static ImmutableArray<LegacyCommandOption> PluginPatchPrefix() =>
    [
        .. GamePair(gameWins: true, Games),
        O("plugin", true, "<K-local-source-plugin>", "Existing copied source plugin."),
        O("output", true, "<new-plugin>", "Fresh same-name plugin output; existing files are refused."),
        O("npc", false, "<hexadecimal-form-id>", "Compatibility spelling used only when canonical --form-id is absent."),
        O("form-id", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --form-id wins.")
    ];

    private static ImmutableArray<LegacyCommandOption> PresetPatch(string payload) =>
    [
        .. GamePair(gameWins: true, Skyrim),
        O("input", true, "<K-local-source.jslot>", "Existing Skyrim RaceMenu JSlot input."),
        O("output", true, "<new-preset.jslot>", "Fresh RaceMenu JSlot output; existing files are refused."),
        O(payload, true, "<inline-JSON-object|@K-local-file>", "Required inline JSON or @existing-K-local-file patch payload."),
        .. MixedTail()
    ];

    private static ImmutableArray<LegacyCommandOption> MixedTail() =>
    [
        O("expected-sha256", false, "<64-hex-sha256>", "Optional during analysis; active apply requires the exact source SHA-256."),
        O("proposal", false, "<new-proposal.json>", "Optional fresh persisted proposal; analysis otherwise remains in memory."),
        O("dry-run", false, "true|false|1", "Only true or 1 activates proposal-only mode.", "true", "false", "1"),
        O("apply", false, "true|false|1", "Only true or 1 applies; active apply requires --expected-sha256.", "true", "false", "1")
    ];

    private static ImmutableArray<LegacyCommandOption> GamePair(bool gameWins, ImmutableArray<string> values) => gameWins
        ?
        [
            O("edition", false, string.Join('|', values), "Compatibility alias used only when --game is absent.", values),
            O("game", false, string.Join('|', values), "One of --game or --edition is required; canonical --game wins.", values)
        ]
        :
        [
            O("edition", false, string.Join('|', values), "One of --edition or --game is required; canonical --edition wins.", values),
            O("game", false, string.Join('|', values), "Compatibility alias used only when --edition is absent.", values)
        ];

    private static ImmutableArray<LegacyCommandOption> Pipeline() =>
    [
        O("format", true, "looksmenu|racemenu-jslot", "Exact preset format; looksmenu pairs only with fallout4 and racemenu-jslot only with skyrimse.", "looksmenu", "racemenu-jslot"),
        .. GamePair(gameWins: false, Games),
        O("preset", true, "<K-local-preset>", "Existing K-local preset in the selected format."),
        O("source-plugin", true, "<K-local-source-plugin>", "Existing copied source plugin."),
        O("plugin", true, "<output-plugin-name>", "Fresh output plugin filename."),
        O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."),
        O("mod-name", true, "<mod-name>", "Package/mod identity."),
        O("output-root", true, "<existing-K-local-output-root>", "Existing K-local root; the output plugin, manifest, and generated artifact paths must be fresh."),
        O("editor-id", false, "<editor-id>", "Optional replacement EditorID."),
        O("name", false, "<npc-name>", "Optional replacement display name."),
        O("facegeom-manifest", false, "<K-local-facegeom-manifest.json>", "Optional FaceGeom evidence copied into the package when admitted."),
        O("facetint-manifest", false, "<K-local-facetint-manifest.json>", "Optional FaceTint evidence copied into the package when admitted."),
        O("runtime-script-build", false, "<K-local-runtime-script-build.json>", "Optional runtime-script static build evidence copied into the package."),
        O("runtime-script-package", false, "<K-local-runtime-script-package.json>", "Optional hash-bound apply-PEX package installed into the generated Data tree.")
    ];

    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, ImmutableArray<string> values) =>
        new(name, required, syntax, values, description);
}
