using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class PackageRuntimeOptionCatalog
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

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["package build"] =
            [
                O("source-root", true, "<verified-K-local-package-root>",
                    "Existing K-local package root; its manifest and every declared byte are verified before copying."),
                O("output-root", true, "<new-K-local-package-root>",
                    "Fresh K-local output root; existing destinations are refused and copied bytes are read back.")
            ],
            ["package inspect"] =
            [
                O("manifest", true, "<K-local-npcmanager-package.json>",
                    "Existing K-local manifest; inspection validates identity and declared metadata without reading artifact bytes.")
            ],
            ["package verify"] =
            [
                O("manifest", false, "<K-local-npcmanager-package.json>",
                    "Existing K-local manifest whose declared bytes, sizes, hashes, paths, and closure are verified."),
                O("archive", false, "<K-local-runtime.zip>",
                    "Mutually exclusive with --manifest and install inputs; independently streams runtime ZIP layout and hash inventory without source-manifest or install-dependency authority."),
                O("payload", false, "runtime-only", "Required with --archive; forbidden with --manifest.", "runtime-only"),
                O("include-runtime-preset", false, "<Data-relative-SKSE-or-F4SE-preset-path>",
                    "Optional exact preset path without Data/ prefix, used only with --archive --payload runtime-only; its presence is required and other presets refuse."),
                O("strict-install-dependencies", false, "true|false",
                    "Exact Boolean mode selector. true requires both --data-root and --plugins; false or omission forbids both.",
                    "true", "false"),
                O("data-root", false, "<absolute-K-local-copied-Data-root>",
                    "Required only with --strict-install-dependencies true; must be an absolute K-local copied Data root."),
                O("plugins", false, "<plugin,...>",
                    "Required only with strict mode: a nonempty comma-separated list of trimmed, unique plugin names. When external descriptor evidence is present, the complete enabled order is limited to 64 and verified provider masters must precede dependents; ordinary packages do not enter that external-order branch."),
                O("workflow-bundle", false, "<reviewed-finish-workflow.json>",
                    "Required with its SHA-256 when the manifest is a Finish Core output; its exact accepted review receipt is validated before verification."),
                O("workflow-bundle-sha256", false, "<sha256>",
                    "Physical SHA-256 of the reviewed Finish workflow bundle.")
            ],
            ["package archive"] =
            [
                O("source-root", false, "<verified-K-local-package-root>",
                    "Existing K-local package root that is independently reverified before archive creation."),
                O("package", false, "<verified-K-local-package-root>",
                    "Alias for --source-root; one is required and conflicting values refuse."),
                O("payload", false, "full|runtime-only",
                    "Default full preserves the existing archive and generated instructions. runtime-only maps admitted Data runtime files to ZIP root, omitting evidence/manifests/presets and historical default-outfit instruction requirements.", "full", "runtime-only"),
                O("include-runtime-preset", false, "<Data-relative-SKSE-or-F4SE-preset-path>",
                    "Only with runtime-only: retain this one declared verified CharGen .jslot or F4EE .json preset path without Data/ prefix."),
                O("output", true, "<new-K-local-install.zip>",
                    "Fresh no-wrapper install ZIP; deterministic layout is reopened and every entry is read back."),
                O("workflow-bundle", false, "<reviewed-finish-workflow.json>",
                    "Required with its SHA-256 when the source is a Finish Core output; its exact accepted review receipt is validated before archive creation."),
                O("workflow-bundle-sha256", false, "<sha256>",
                    "Physical SHA-256 of the reviewed Finish workflow bundle.")
            ],
            ["runtime-script propose"] =
            [
                .. Games(),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."),
                O("appearance", true, "<inline-JSON|@K-local-file>",
                    "Inline JSON or @existing-K-local-file with schemaVersion 1, scriptName, and properties. Named property types are BoolValue, IntValue, FloatValue, StringValue, BoolArray, IntArray, FloatArray, or StringArray; the current Enum.TryParse also admits numeric enum tokens, but those are accidental parser admissions, not supported names."),
                O("plugin", false, "<K-local-source-plugin>",
                    "Optional copied source plugin used to bind the proposal to exact source bytes."),
                O("output", true, "<new-runtime-script-proposal.json>",
                    "Fresh typed proposal document; existing output is refused.")
            ],
            ["runtime-script build"] =
            [
                .. Games(),
                O("source-root", true, "<K-local-runtime-script-source-root>",
                    "K-local root containing the pinned game-specific PSC, PEX, inspection, and compiler evidence."),
                O("output", true, "<new-runtime-script-build.json>",
                    "Fresh static build-evidence document; <output>.pex-inspect.json is a second retained fresh output, and preexistence of either path is refused.")
            ],
            ["runtime-script write"] =
            [
                .. Games(),
                O("source", true, "<K-local-source-plugin>", "Existing copied source plugin."),
                O("proposal", true, "<runtime-script-proposal.json>",
                    "Existing source-bound typed runtime-script proposal."),
                O("output", true, "<new-plugin.esp>",
                    "Fresh ordinary plugin output; unrelated VMAD scripts are preserved and existing output is refused.")
            ],
            ["runtime-script package"] =
            [
                .. Games(),
                O("source-root", true, "<K-local-runtime-script-source-root>",
                    "K-local source root containing the pinned game-specific PSC and PEX."),
                O("output-root", true, "<new-K-local-package-root>",
                    "Fresh package root containing only the bound game-specific apply PEX under Data/Scripts and its manifest.")
            ],
            ["runtime-script deploy"] =
            [
                .. Games(),
                O("package", true, "<runtime-script-package-manifest.json>",
                    "Existing K-local hash-bound runtime-script package manifest."),
                O("data-root", true, "<existing-K-local-copied-Data-root>",
                    "Existing copied K-local Data root; identical bytes are unchanged success and conflicts are refused.")
            ],
            ["runtime-script inspect-vmad"] =
            [
                .. Games(),
                O("plugin", true, "<K-local-plugin>", "Existing copied plugin inspected read-only."),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."),
                O("script", false, "<script-name>",
                    "Optional exact script name; omission selects NPCM_Manolov_ApplyFO4 or NPCM_Manolov_ApplySSE for the selected game.")
            ]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Rows.ContainsKey(name);

    public static ImmutableArray<LegacyCommandOption> For(string name) => Rows[name];

    public static AgentValueKind ValueKindFor(LegacyCommandOption option) => option.Name switch
    {
        "edition" or "game" or "payload" => AgentValueKind.Enum,
        "strict-install-dependencies" => AgentValueKind.Boolean,
        "workflow-bundle-sha256" => AgentValueKind.Sha256,
        "npc" => AgentValueKind.ArtifactReference,
        "appearance" => AgentValueKind.Json,
        "script" => AgentValueKind.Identifier,
        "plugins" => AgentValueKind.String,
        _ => AgentValueKind.Path
    };

    public static string? AliasFor(string commandName, string optionName) =>
        commandName == "package archive" && optionName == "package" ? "source-root" :
        optionName == "game" ? "edition" : null;
}
