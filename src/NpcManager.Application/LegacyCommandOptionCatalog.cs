using System.Collections.Immutable;

namespace NpcManager.Application;

public sealed record LegacyCommandOption(
    string Name,
    bool Required,
    string ValueSyntax,
    ImmutableArray<string> AcceptedValues,
    string Description);

public static class LegacyCommandOptionCatalog
{
    private static readonly ImmutableArray<string> PreferredSkyrimFlagValues =
    [
        "female", "essential", "ischargenfacepreset", "respawn", "autocalcstats",
        "unique", "doesntaffectstealthmeter", "skyrimusetemplate", "protected",
        "summonable", "doesnotbleed", "bleedoutoverride", "oppositegenderanims",
        "simpleactor", "skyrimloopedscript", "skyrimloopedaudio", "isghost",
        "invulnerable", "pc-level-mult"
    ];

    private static readonly ImmutableArray<LegacyCommandOption> PreviewRenderOptions =
    [
        Option("edition", false, "fallout4|skyrimse",
            "One of --edition or --game is required; canonical --edition wins when both are supplied.", "fallout4", "skyrimse"),
        Option("game", false, "fallout4|skyrimse",
            "One of --edition or --game is required; canonical --edition wins when both are supplied.", "fallout4", "skyrimse"),
        Option("manifest", true, "<preview-manifest.json>",
            "Preview-scene manifest to render."),
        Option("output", true, "<new-preview-artifact.json>",
            "New semantic preview artifact output."),
        Option("visible", false, "<face,body,hair,outfit,accessory|all>",
            "Optional nonempty comma-separated visible-layer selection.",
            "face", "body", "hair", "outfit", "accessory", "all"),
        Option("morphs", false, "<bone,vertex,weight,sculpt|all>",
            "Optional nonempty comma-separated morph-category selection.",
            "bone", "vertex", "weight", "sculpt", "all"),
        Option("outfit", false, "<Plugin.esp|0xFormID>",
            "Optional outfit reference; --outfit and --variant must be supplied together."),
        Option("variant", false, "<identifier>",
            "Optional semantic variant identifier; --outfit and --variant must be supplied together."),
        Option("camera", false, "<identifier>",
            "Optional versioned manifest camera-preset identifier."),
        Option("lighting", false, "<identifier>",
            "Optional versioned manifest lighting-preset identifier."),
        Option("animation", false, "<identifier>",
            "Optional animation identifier; requires exactly one of --frame or --time."),
        Option("frame", false, "<non-negative-integer>",
            "Animation frame; requires --animation and is mutually exclusive with --time."),
        Option("time", false, "<non-negative-seconds>",
            "Animation time; requires --animation and is mutually exclusive with --frame."),
        Option("fps", false, "<1..240>",
            "Optional finite playback rate; requires --animation."),
        Option("play", false, "true|false",
            "Optional playback state; requires --animation.", "true", "false"),
        Option("asset-root", false, "<K-local-data-root>",
            "Optional import root; --asset-root and --image-output must be supplied together."),
        Option("image-output", false, "<new-png>",
            "Optional rendered PNG; --asset-root and --image-output must be supplied together."),
        Option("width", false, "<64..2048>",
            "Optional image width; requires --asset-root and --image-output."),
        Option("height", false, "<64..2048>",
            "Optional image height; requires --asset-root and --image-output."),
        Option("hair-slots", false, "<slot,...>",
            "Optional comma-separated slots from 0 to 255; requires --render-headwear true|false."),
        Option("render-headwear", false, "true|false",
            "Optional headwear toggle; true requires --hair-slots.", "true", "false")
    ];

    private static readonly ImmutableDictionary<
        string,
        ImmutableArray<LegacyCommandOption>> OptionsByCommand =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["npc voice discover"] =
            [
                Option("endpoint", false, "<http-loopback-url>", "Optional exact loopback XTTS endpoint."),
                Option("timeout-seconds", false, "<1..30>", "Per-probe timeout in seconds."),
                Option("output", false, "<new-json-file>", "Optional fresh service-inventory output."),
                Option("json", false, "true|false|1", "Emit the structured JSON envelope.", "true", "false", "1")
            ],
            ["npc voice import"] =
            [
                Option("sample", true, "<K-local.wav>", "Existing source WAV inside the workspace."),
                Option("plugin", true, "<Plugin.esp>", "NPC owner plugin."),
                Option("form-id", true, "<hexadecimal-form-id>", "NPC FormID."),
                Option("editor-id", false, "<editor-id>", "Optional NPC EditorID."),
                Option("voice-prefix", true, "<identifier>", "Prefix for output-owned dialogue records."),
                Option("output", true, "<new-K-local-directory>", "Fresh sample-authority output directory."),
                Option("json", false, "true|false|1", "Emit the structured JSON envelope.", "true", "false", "1")
            ],
            ["npc voice synthesize"] =
            [
                Option("manifest", true, "<dialogue-manifest.json>", "Exact reviewed dialogue manifest."),
                Option("manifest-sha256", true, "<64-hex-sha256>", "Expected manifest SHA-256."),
                Option("sample-authority", true, "<voice-sample.json>", "Exact sample-authority document."),
                Option("sample-authority-sha256", true, "<64-hex-sha256>", "Expected sample-authority SHA-256."),
                Option("output", true, "<K-local-directory>", "Fresh or resumable synthesis output directory."),
                Option("endpoint", false, "<http-loopback-url>", "Optional exact loopback XTTS endpoint."),
                Option("language", false, "<language-code>", "Optional backend language-code override."),
                Option("max-lines", false, "<positive-integer>", "Maximum pending lines attempted in this run."),
                Option("resume", false, "true|false|1", "Reuse only hash-valid unchanged successes.", "true", "false", "1"),
                Option("json", false, "true|false|1", "Emit the structured JSON envelope.", "true", "false", "1")
            ],
            ["npc dialogue analyze"] =
            [
                Option("template", false, "<template-name>", "Template mode coverage template."), Option("profile", false, "<profile.json>", "Template mode NPC profile."),
                Option("manifest-output", false, "<new-manifest.json>", "Template mode fresh draft output."), Option("manifest", false, "<manifest.json>", "Normal mode reviewed manifest."),
                Option("manifest-sha256", false, "<64-hex-sha256>", "Normal mode exact manifest hash."), Option("plugin", false, "<K-local-plugin>", "Normal mode source NPC plugin."),
                Option("plugin-sha256", false, "<64-hex-sha256>", "Normal mode source plugin hash."), Option("data-root", false, "<K-local-data-root>", "Copied master data root."),
                Option("plugins", false, "<plugin,...>", "Ascending copied-master load order."), Option("sample-authority", false, "<voice-sample.json>", "Exact sample authority."),
                Option("sample-authority-sha256", false, "<64-hex-sha256>", "Expected sample-authority SHA-256."), Option("output", false, "<new-proposal.json>", "Normal mode fresh proposal output."),
                Option("npc-plugin", false, "<Plugin.esp>", "Template NPC plugin."), Option("form-id", false, "<hexadecimal-form-id>", "Template NPC FormID."),
                Option("editor-id", false, "<editor-id>", "Template NPC EditorID."), Option("voice-prefix", false, "<identifier>", "Template record prefix."),
                Option("female", false, "true|false|1", "Template NPC sex flag.", "true", "false", "1"), Option("language", false, "<language-code>", "Template language."),
                Option("json", false, "true|false|1", "Emit the structured JSON envelope.", "true", "false", "1")
            ],
            ["npc dialogue apply"] =
            [
                Option("proposal", true, "<proposal.json>", "Exact reviewed proposal."), Option("proposal-sha256", true, "<64-hex-sha256>", "Expected proposal SHA-256."),
                Option("synthesis", true, "<voice-synthesis.json>", "Exact completed synthesis ledger."), Option("synthesis-sha256", true, "<64-hex-sha256>", "Expected synthesis SHA-256."),
                Option("output", true, "<new-K-local-directory>", "Fresh package root."), Option("lip-tools", false, "<K-local-directory>", "Optional admitted lip/FUZ tool directory."),
                Option("json", false, "true|false|1", "Emit the structured JSON envelope.", "true", "false", "1")
            ],
            ["npc dialogue verify"] =
            [
                Option("manifest", true, "<dialogue-output-manifest.json>", "Exact output manifest."), Option("manifest-sha256", true, "<64-hex-sha256>", "Expected output-manifest SHA-256."),
                Option("json", false, "true|false|1", "Emit the structured JSON envelope.", "true", "false", "1")
            ],
            ["workspace preflight"] =
            [
                Option("edition", false, "fallout4|skyrimse",
                    "Selects an archive, reviewed-intake, or game-root branch; canonical --edition wins over --game.", "fallout4", "skyrimse"),
                Option("game", false, "fallout4|skyrimse",
                    "Selects an archive, reviewed-intake, or game-root branch when --edition is absent.", "fallout4", "skyrimse"),
                Option("workspace-root", false, "<K-local-root>",
                    "Required with --output-root only in the basic branch; otherwise defaults to the configured workspace where supported."),
                Option("data-root", false, "<K-local-data-root>",
                    "Required in game-root and reviewed-intake branches."),
                Option("output-root", false, "<K-local-output-root>",
                    "Required in basic, game-root, and reviewed-intake branches."),
                Option("plugin", false, "<K-local-plugin>",
                    "Selects archive consistency and must be supplied with --asset-index."),
                Option("asset-index", false, "<asset-index.json>",
                    "Selects archive consistency and must be supplied with --plugin."),
                Option("load-order", false, "<load-order.json>",
                    "Selects reviewed intake; canonical --load-order wins over --loadorder."),
                Option("loadorder", false, "<load-order.json>",
                    "Alias for --load-order used only when canonical --load-order is absent."),
                Option("selected", false, "<plugin,...>",
                    "Optional comma- or semicolon-separated selected plugins in reviewed intake."),
                Option("intake-output", false, "<new-reviewed-intake.json>",
                    "Required by protocol-2 reviewed-intake mode; fresh reviewed-intake output."),
                Option("npc-editor-id", false, "<editor-id>",
                    "Required by protocol-2 reviewed-intake mode; workflow NPC identity."),
                Option("workflow-output", false, "<new-workflow.json>",
                    "Required by protocol-2 reviewed-intake mode; fresh workflow state output.")
            ],
            ["npc face-patch"] =
            [
                Option("game", false, "fallout4|skyrimse",
                    "One of --game or --edition is required; canonical --game wins when both are supplied.", "fallout4", "skyrimse"),
                Option("edition", false, "fallout4|skyrimse",
                    "One of --game or --edition is required; canonical --game wins when both are supplied.", "fallout4", "skyrimse"),
                Option("plugin", true, "<K-local-plugin>", "Source plugin to inspect."),
                Option("output", true, "<new-plugin>", "New patched plugin output."),
                Option("data-root", true, "<K-local-data-root>", "Data root used for provider resolution."),
                Option("npc", false, "<hexadecimal-form-id>",
                    "One of --npc or --form-id is required; canonical --form-id wins when both are supplied."),
                Option("form-id", false, "<hexadecimal-form-id>",
                    "One of --npc or --form-id is required; canonical --form-id wins when both are supplied."),
                Option("headparts", false, "<JSON-array|type=Plugin|FormID,...>",
                    "At least one of --headparts or --hair-color is required."),
                Option("headpart-replace", false, "<old-Plugin|FormID=new-local-FormID>",
                    "Replace exactly one PNAM with an output-owned HDPT; exclusive with --headparts."),
                Option("hair-color", false, "<Plugin|FormID|none>",
                    "At least one of --headparts or --hair-color is required."),
                Option("expected-sha256", false, "<64-hex-sha256>", "Optional exact source-plugin SHA-256."),
                Option("proposal", false, "<new-proposal.json>", "Optional proposal artifact path."),
                Option("dry-run", false, "true|false|1",
                    "Optional proposal-only flag; true or 1 activates it and cannot be combined with --apply true or 1.", "true", "false", "1"),
                Option("apply", false, "true|false|1",
                    "Optional mutation flag; true or 1 activates it and cannot be combined with --dry-run true or 1.", "true", "false", "1")
            ],
            ["preview render"] = PreviewRenderOptions,
            ["render npc"] = PreviewRenderOptions,
            ["npc edit-package"] =
            [
                Option("edition", false, "skyrimse",
                    "One of --edition or --game is required; canonical --edition wins when both are supplied.", "skyrimse"),
                Option("game", false, "skyrimse",
                    "One of --edition or --game is required; canonical --edition wins when both are supplied.", "skyrimse"),
                Option("input-plugin", true, "<K-local-plugin>",
                    "Copied source plugin to inspect and edit."),
                Option("input-sha256", false, "<64-hex-sha256>",
                    "One of --input-sha256 or --expected-sha256 is required; canonical --input-sha256 wins when both are supplied."),
                Option("expected-sha256", false, "<64-hex-sha256>",
                    "One of --input-sha256 or --expected-sha256 is required; canonical --input-sha256 wins when both are supplied."),
                Option("npc", true, "<hexadecimal-form-id>",
                    "Target NPC FormID."),
                Option("output-root", true, "<new-K-local-directory>",
                    "Fresh package directory; existing output is refused."),
                Option("plugin", true, "<new-plugin.esp>",
                    "Output plugin filename."),
                Option("editor-id", false, "<string>",
                    "Replacement NPC EditorID."),
                Option("name", false, "<string>",
                    "Replacement full display name."),
                Option("short-name", false, "<string>",
                    "Replacement short display name."),
                Option("race", false, "<Plugin.esp|0xFormID>",
                    "Source-master race reference."),
                Option("voice", false, "<Plugin.esp|0xFormID|none>",
                    "Source-master voice reference or none."),
                Option("class", false, "<Plugin.esp|0xFormID>",
                    "Source-master class reference."),
                Option("combat-style", false, "<Plugin.esp|0xFormID|none>",
                    "Source-master combat-style reference or none."),
                Option("level", false, "<signed-integer>",
                    "Explicit NPC level."),
                Option("level-mult", false, "<number>",
                    "NPC level multiplier."),
                Option("magicka-offset", false, "<signed-integer>",
                    "Magicka offset."),
                Option("stamina-offset", false, "<signed-integer>",
                    "Stamina offset."),
                Option("health-offset", false, "<signed-integer>",
                    "Health offset."),
                Option("calc-min", false, "<unsigned-integer>",
                    "Minimum calculated level."),
                Option("calc-max", false, "<unsigned-integer>",
                    "Maximum calculated level."),
                Option("speed-multiplier", false, "<number>",
                    "Movement speed multiplier."),
                Option("disposition", false, "<signed-integer>",
                    "Base disposition."),
                Option("bleedout", false, "<unsigned-integer>",
                    "Bleedout override."),
                Option("player-health", false, "<unsigned-integer>",
                    "Base player-health value."),
                Option("player-magicka", false, "<unsigned-integer>",
                    "Base player-magicka value."),
                Option("player-stamina", false, "<unsigned-integer>",
                    "Base player-stamina value."),
                Option("skill-values", false, "<skill=value,...>",
                    "Comma-separated skill values."),
                Option("skill-offsets", false, "<skill=value,...>",
                    "Comma-separated skill offsets."),
                Option("far-model-distance", false, "<number>",
                    "Far-model distance."),
                Option("geared-weapons", false, "<byte>",
                    "Geared-weapons byte value."),
                new LegacyCommandOption("set-flag", false, "<flag,...>",
                    PreferredSkyrimFlagValues,
                    "Preferred Skyrim flags to set; accepted values include the pc-level-mult alias."),
                new LegacyCommandOption("clear-flag", false, "<flag,...>",
                    PreferredSkyrimFlagValues,
                    "Preferred Skyrim flags to clear; accepted values include the pc-level-mult alias."),
                Option("keywords", false, "<JSON-array|@K-local-file>",
                    "Replace the ordered keyword list."),
                Option("add-keyword", false, "<JSON-array|@K-local-file>",
                    "Add ordered keyword entries."),
                Option("remove-keyword", false, "<JSON-array|@K-local-file>",
                    "Remove keyword entries."),
                Option("factions", false, "<JSON-array|@K-local-file>",
                    "Replace faction memberships."),
                Option("add-faction", false, "<JSON-array|@K-local-file>",
                    "Add faction memberships."),
                Option("update-faction", false, "<JSON-array|@K-local-file>",
                    "Update faction memberships."),
                Option("remove-faction", false, "<JSON-array|@K-local-file>",
                    "Remove faction memberships."),
                Option("inventory", false, "<JSON-array|@K-local-file>",
                    "Replace inventory entries."),
                Option("add-inventory", false, "<JSON-array|@K-local-file>",
                    "Add inventory entries."),
                Option("update-inventory", false, "<JSON-array|@K-local-file>",
                    "Update inventory entries."),
                Option("remove-inventory", false, "<JSON-array|@K-local-file>",
                    "Remove inventory entries."),
                Option("default-outfit", false, "<Plugin.esp|0xFormID|none>",
                    "Default outfit reference or none."),
                Option("sleep-outfit", false, "<Plugin.esp|0xFormID|none>",
                    "Sleep outfit reference or none."),
                Option("perks", false, "<JSON-array|@K-local-file>",
                    "Replace perk entries."),
                Option("add-perk", false, "<JSON-array|@K-local-file>",
                    "Add perk entries."),
                Option("update-perk", false, "<JSON-array|@K-local-file>",
                    "Update perk entries."),
                Option("remove-perk", false, "<JSON-array|@K-local-file>",
                    "Remove perk entries."),
                Option("actor-effects", false, "<JSON-array|@K-local-file>",
                    "Replace actor-effect references."),
                Option("add-actor-effect", false, "<JSON-array|@K-local-file>",
                    "Add actor-effect references."),
                Option("remove-actor-effect", false, "<JSON-array|@K-local-file>",
                    "Remove actor-effect references."),
                Option("output-kind", true,
                    "source-mastered-override|standalone-copy",
                    "Output package semantics; the value is required.",
                    "source-mastered-override", "standalone-copy")
            ]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static ImmutableArray<LegacyCommandOption> For(string commandName) =>
        OmittedFaceRuntimeOptionCatalog.Contains(commandName)
            ? OmittedFaceRuntimeOptionCatalog.For(commandName)
            : OmittedNpcOptionCatalog.Contains(commandName)
            ? OmittedNpcOptionCatalog.For(commandName)
            : ObjectPresetOptionCatalog.Contains(commandName)
            ? ObjectPresetOptionCatalog.For(commandName)
            : P09RecordOptionCatalog.Contains(commandName)
            ? P09RecordOptionCatalog.For(commandName)
            : PackageRuntimeOptionCatalog.Contains(commandName)
            ? PackageRuntimeOptionCatalog.For(commandName)
            : PreviewPluginOptionCatalog.Contains(commandName)
            ? PreviewPluginOptionCatalog.For(commandName)
            : NpcCreationOptionCatalog.Contains(commandName)
            ? NpcCreationOptionCatalog.For(commandName)
            : FaceGenMaterializationOptionCatalog.Contains(commandName)
            ? FaceGenMaterializationOptionCatalog.For(commandName)
            : ReferenceHairFinishOptionCatalog.Contains(commandName)
            ? ReferenceHairFinishOptionCatalog.For(commandName)
            : PatchReadonlyFaceGenOptionCatalog.Contains(commandName)
            ? PatchReadonlyFaceGenOptionCatalog.For(commandName)
            : OptionsByCommand.TryGetValue(commandName, out var options)
            ? options
            : CoreBodyRecordOptionCatalog.For(commandName);

    private static LegacyCommandOption Option(
        string name,
        bool required,
        string valueSyntax,
        string description,
        params string[] acceptedValues) =>
        new(
            name,
            required,
            valueSyntax,
            acceptedValues.ToImmutableArray(),
            description);

    private static LegacyCommandOption Option(
        string name,
        bool required,
        string valueSyntax,
        ImmutableArray<string> acceptedValues,
        string description) =>
        new(name, required, valueSyntax, acceptedValues, description);
}
