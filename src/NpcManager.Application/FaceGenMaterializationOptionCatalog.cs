using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class FaceGenMaterializationOptionCatalog
{
    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static readonly ImmutableArray<LegacyCommandOption> Edition =
    [
        O("edition", false, "fallout4|skyrimse", "One of --edition or --game is required; canonical --edition wins when both are supplied.", "fallout4", "skyrimse"),
        O("game", false, "fallout4|skyrimse", "Alias for --edition used only when canonical --edition is absent.", "fallout4", "skyrimse")
    ];
    private static readonly ImmutableArray<LegacyCommandOption> SkyrimEdition =
    [
        O("edition", false, "skyrimse", "One of --edition or --game is required; canonical --edition wins when both are supplied; only skyrimse is accepted.", "skyrimse"),
        O("game", false, "skyrimse", "Alias for --edition used only when canonical --edition is absent; only skyrimse is accepted.", "skyrimse")
    ];
    private static readonly ImmutableArray<LegacyCommandOption> TintDialect =
    [
        O("format", false, "bgra8|bc3|bc7|uncompressed", "Optional DDS format; uncompressed aliases bgra8.", "bgra8", "bc3", "bc7", "uncompressed"),
        O("mips", false, "1", "Optional mip count; the bounded builder accepts exactly one mip level.", "1"),
        O("alpha", false, "preserve|opaque", "Optional alpha mode.", "preserve", "opaque")
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["facegen build-geom"] = [.. Edition,
                O("manifest", true, "<K-local-manifest.json>", "Existing semantic FaceGen shape manifest."),
                O("output", true, "<new-semantic-facegeom.json>", "Fresh semantic FaceGeom JSON output; this is not a NIF."),
                O("npc", false, "<hexadecimal-form-id>", "Optional NPC FormID filter."),
                O("allow-poison", false, "<presence-toggle>", "Presence relaxes strict poisoned-shape refusal; even the text false is active.")],
            ["facegen build-geom-nif"] = [.. Edition,
                O("asset-root", true, "<K-local-root>", "K-local root used to resolve relative NIF and @morph paths."),
                O("source", true, "<relative-or-K-local-NIF>", "Existing source NIF, resolved relative to --asset-root when not absolute."),
                O("output", true, "<new-NIF>", "Fresh real NIF output."),
                O("mode", false, "bake|transport", "Bake is the default; transport is Skyrim-only and uses hash-bound carrier rules.", "bake", "transport"),
                O("morphs", false, "<JSON-array|@K-local-file>", "Required in bake mode; transport permits omission or only all-zero morph values."),
                O("source-sha256", false, "<64-hex-sha256>", "Required in transport mode to bind the source NIF."),
                O("transport-profile", false, "complete-carrier|geometry-into-carrier", "Required in transport mode.", "complete-carrier", "geometry-into-carrier"),
                O("carrier", false, "<relative-or-K-local-NIF>", "Required by geometry-into-carrier and forbidden by complete-carrier."),
                O("carrier-sha256", false, "<64-hex-sha256>", "Required by geometry-into-carrier and forbidden by complete-carrier."),
                O("shape", false, "<exact-shape-name>", "Required by geometry-into-carrier and forbidden by complete-carrier.")],
            ["facegen build-geom-bound"] = [.. Edition,
                O("data-root", true, "<copied-Data-root>", "Existing copied Data root used for provider resolution."), O("output-root", true, "<K-local-output-root>", "Existing K-local root receiving the canonical provider-relative NIF."),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."), O("plugins", true, "<plugin,...>", "Nonempty comma-separated plugins in ascending load order."), O("morphs", true, "<JSON-array|@K-local-file>", "Selected TRI morph values.")],
            ["facegen build-tint"] = [.. Edition,
                O("manifest", true, "<K-local-manifest.json>", "Existing semantic FaceTint layer manifest."), O("output", true, "<new-semantic-facetint.json>", "Fresh semantic FaceTint JSON output."),
                O("npc", false, "<hexadecimal-form-id>", "Optional NPC FormID filter."), O("resolution", false, "512|1024|2048|4096|8192", "Optional exact square output resolution.", "512", "1024", "2048", "4096", "8192"),
                .. TintDialect, O("dds-output", false, "<new-dds>", "Optional fresh real DDS output."), O("provider-root", false, "<K-local-provider-root>", "Optional admitted provider root; when present, manifest DDS sources are decoded and hash-bound.")],
            ["facegen build-tint-bound"] = [.. Edition,
                O("data-root", true, "<copied-Data-root>", "Existing copied Data root used for provider resolution."), O("manifest", true, "<K-local-manifest.json>", "Existing semantic FaceTint layer manifest."),
                O("output", true, "<new-semantic-facetint.json>", "Fresh semantic FaceTint JSON output."), O("output-root", true, "<K-local-output-root>", "Existing K-local root receiving the canonical provider-relative DDS."),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."), O("plugins", true, "<plugin,...>", "Nonempty comma-separated plugins in ascending load order."), .. TintDialect],
            ["facegen build-tint-native"] = [.. SkyrimEdition,
                O("data-root", true, "<copied-Data-root>", "Existing copied Skyrim Data root."), O("plugins", true, "<plugin,...>", "Nonempty comma-separated Skyrim plugins in ascending load order."),
                O("npc", true, "<Plugin.esp|0xXXXXXXXX>", "Winning NPC form reference."), O("race", true, "<Plugin.esm|0xXXXXXXXX>", "Expected RACE form reference."),
                O("sex", true, "male|female", "Expected NPC sex.", "male", "female"), O("output", true, "<new-K-local-dds>", "Fresh native Skyrim FaceTint DDS output.")],
            ["facegen options"] = [.. Edition,
                O("input", false, "<K-local-options.json>", "One of --input or --options is required; canonical --input wins when both are supplied."), O("options", false, "<K-local-options.json>", "Alias for --input used only when canonical --input is absent."),
                O("output", false, "<new-options.json>", "Optional fresh canonical options output used when --apply is present."), O("expected-sha256", false, "<64-hex-sha256>", "Optional exact input SHA-256."),
                O("apply", false, "<presence-toggle>", "Presence requests output materialization; even the text false is active and cannot coexist with --dry-run."), O("dry-run", false, "<presence-toggle>", "Presence selects validation-only behavior; even the text false is active and cannot coexist with --apply.")],
            ["facegen build"] = [.. Edition,
                O("manifest", true, "<K-local-manifest.json>", "Existing semantic FaceGen manifest."), O("output", true, "<new-semantic-corrections.json>", "Fresh semantic correction report; this is not a game asset."),
                O("corrections", false, "auto", "Optional correction selector; only auto is accepted.", "auto"), O("npc", false, "<hexadecimal-form-id>", "Optional NPC FormID filter."),
                O("allow-poison", false, "<presence-toggle>", "Presence relaxes strict poisoned-shape refusal; even the text false is active.")],
            ["facegen bake-all"] = [.. Edition,
                O("manifests", false, "<K-local-batch.json>", "One of --manifests or --batch is required; canonical --manifests wins when both are supplied."), O("batch", false, "<K-local-batch.json>", "Alias for --manifests used only when canonical --manifests is absent."),
                O("output", true, "<new-semantic-batch.json>", "Fresh semantic batch report; this does not materialize NIF or DDS assets."), O("allow-poison", false, "<presence-toggle>", "Presence relaxes strict poisoned-shape refusal; even the text false is active.")],
            ["facegen bake-all-native"] = [.. SkyrimEdition,
                O("data-root", true, "<copied-Data-root>", "Existing copied Skyrim Data root."), O("plugins", true, "<plugin,...>", "Nonempty comma-separated Skyrim plugins in ascending load order."),
                O("output-root", true, "<K-local-Data-root>", "K-local Data-shaped root receiving canonical NIF/DDS pairs."), O("winning-plugin", false, "<plugin-name>", "Optional winning-plugin target filter.")],
            ["facegen build-plugin"] = [.. Edition,
                O("manifest", false, "<K-local-targets.json>", "One of --manifest or --target is required; canonical --manifest wins when both are supplied."), O("target", false, "<K-local-targets.json>", "Alias for --manifest used only when canonical --manifest is absent."),
                O("plugin", false, "<plugin-name>", "One of --plugin or --target-plugin is required; canonical --plugin wins when both are supplied."), O("target-plugin", false, "<plugin-name>", "Alias for --plugin used only when canonical --plugin is absent."),
                O("output", true, "<new-semantic-plugin-report.json>", "Fresh semantic plugin-target report."), O("allow-poison", false, "<presence-toggle>", "Presence relaxes strict poisoned-shape refusal; even the text false is active.")],
            ["facegen pack"] = [.. Edition,
                O("data-root", true, "<copied-Data-root>", "Read-only copied Data source root."), O("output-root", true, "<new-K-local-package-root>", "Fresh nonexisting package root; overwrite and source overlap are refused."),
                O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."), O("plugins", true, "<plugin,...>", "Nonempty comma-separated plugins in ascending load order."), O("anchor-plugin", true, "<plugin-name>", "Anchor plugin copied into the package."),
                O("debug-sandbox", false, "<presence-toggle>", "Presence selects debug-sandbox planning; even the text false is active."), O("shared-neutral-detail", false, "<presence-toggle>", "Presence includes the shared neutral-detail provider; even the text false is active.")],
            ["facegen deploy"] = [.. Edition,
                O("package", true, "<facegen-pack.json>", "Existing hash-bound facegen-pack manifest."), O("data-root", true, "<copied-Data-root>", "Existing copied Data destination; identical files are idempotent and differing files refuse the whole request.")]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Rows.ContainsKey(name);
    public static bool IsFamily(string name) => Rows.ContainsKey(name);
    public static ImmutableArray<LegacyCommandOption> For(string name) => Rows[name];
    public static AgentValueKind ValueKindFor(LegacyCommandOption option) => option.Name switch
    {
        "edition" or "game" or "mode" or "transport-profile" or "format" or "alpha" or "sex" or "corrections" => AgentValueKind.Enum,
        "resolution" or "mips" => AgentValueKind.Integer,
        "source-sha256" or "carrier-sha256" or "expected-sha256" => AgentValueKind.Sha256,
        "npc" or "race" => AgentValueKind.ArtifactReference,
        "plugins" => AgentValueKind.String,
        "morphs" => AgentValueKind.Json,
        "winning-plugin" or "plugin" or "target-plugin" or "anchor-plugin" or "shape" => AgentValueKind.Identifier,
        "allow-poison" or "apply" or "dry-run" or "debug-sandbox" or "shared-neutral-detail" => AgentValueKind.String,
        _ => AgentValueKind.Path
    };
}
