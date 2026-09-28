using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class PreviewPluginOptionCatalog
{
    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static readonly ImmutableArray<string> NpcFlags =
    [
        "female", "essential", "ischargenfacepreset", "respawn",
        "autocalcstats", "unique", "doesntaffectstealthmeter",
        "fallout4calcforeachtemplate", "skyrimusetemplate", "protected",
        "summonable", "doesnotbleed", "bleedoutoverride",
        "oppositegenderanims", "simpleactor", "fallout4noactivationorhellos",
        "fallout4diffusealphatest", "skyrimloopedscript", "skyrimloopedaudio",
        "isghost", "invulnerable", "pc-level-mult"
    ];

    private static LegacyCommandOption[] Games() =>
    [
        O("edition", false, "fallout4|skyrimse",
            "Selects the game edition; canonical --edition wins over --game.",
            "fallout4", "skyrimse"),
        O("game", false, "fallout4|skyrimse",
            "Alias for --edition used only when canonical --edition is absent.",
            "fallout4", "skyrimse")
    ];

    private static readonly ImmutableArray<LegacyCommandOption> PreviewRender =
    [
        .. Games(),
        O("manifest", true, "<preview-manifest.json>", "Existing preview-scene manifest."),
        O("output", true, "<new-preview-artifact.json>", "Fresh semantic preview artifact."),
        O("visible", false,
            "<face,body,hair,outfit,underarmor,armor,headwear,gore,accessory|all>",
            "Optional nonempty comma-separated visible-category list.",
            "face", "body", "hair", "outfit", "underarmor", "armor",
            "headwear", "gore", "accessory", "all"),
        O("morphs", false, "<bone,vertex,weight,sculpt|all>",
            "Optional nonempty comma-separated morph-category list.",
            "bone", "vertex", "weight", "sculpt", "all"),
        O("outfit", false, "<Plugin.esp|0xFormID>",
            "Optional outfit reference; --outfit and --variant must be supplied together."),
        O("variant", false, "<identifier>",
            "Optional semantic variant identifier; --outfit and --variant must be supplied together."),
        O("camera", false, "<identifier>", "Optional versioned manifest camera-preset identifier."),
        O("lighting", false, "<identifier>", "Optional versioned manifest lighting-preset identifier."),
        O("animation", false, "<identifier>",
            "Optional animation identifier; requires exactly one of --frame or --time."),
        O("frame", false, "<non-negative-integer>",
            "Animation frame; requires --animation and is mutually exclusive with --time."),
        O("time", false, "<finite-non-negative-seconds>",
            "Finite single-precision animation time; requires --animation and is mutually exclusive with --frame."),
        O("fps", false, "<finite-1..240>",
            "Finite single-precision playback rate from 1 through 240; requires --animation."),
        O("play", false, "true|false", "Optional playback state; requires --animation.", "true", "false"),
        O("asset-root", false, "<K-local-data-root>",
            "Optional copied asset root; --asset-root and --image-output must be supplied together."),
        O("image-output", false, "<new-png>",
            "Optional fresh off-engine PNG; --asset-root and --image-output must be supplied together."),
        O("width", false, "<64..2048>",
            "Optional image width; requires --asset-root and --image-output."),
        O("height", false, "<64..2048>",
            "Optional image height; requires --asset-root and --image-output."),
        O("hair-slots", false, "<integer-0..255,...>",
            "Optional nonempty comma-separated slot list; requires --render-headwear true|false."),
        O("render-headwear", false, "true|false",
            "Optional headwear toggle; literal true requires --hair-slots while false may be supplied alone.",
            "true", "false")
    ];

    private static readonly ImmutableArray<LegacyCommandOption> PreviewNpc =
    [
        O("intake", true, "<reviewed-intake.json>", "Existing reviewed Skyrim intake document."),
        O("plugin", true, "<plugin-name>", "Selected Skyrim NPC owner plugin name."),
        O("form", true, "<nonzero-hexadecimal-form-id>", "Selected nonzero NPC FormID."),
        O("package-manifest", false, "<npc-package-manifest.json>",
            "Optional exact package overlay manifest; must be paired with --expected-package-sha256."),
        O("expected-package-sha256", false, "<64-hex-sha256>",
            "Optional exact package-overlay hash; must be paired with --package-manifest."),
        O("output-root", true, "<new-K-local-output-root>", "Fresh preview bundle output root."),
        O("workflow-bundle", false, "<accepted-but-unconsumed-workflow-bundle.json>",
            "Accepted and path-validated by V1 binding but currently unconsumed by the V1 handler."),
        O("workflow-bundle-sha256", false, "<accepted-but-unconsumed-string>",
            "Accepted but currently unconsumed and not hash-validated by the V1 handler."),
        O("workflow-output", false, "<accepted-but-unconsumed-output-path>",
            "Accepted and path-validated by V1 binding but currently unconsumed by the V1 handler.")
    ];

    private static readonly ImmutableArray<LegacyCommandOption> PluginVerify =
    [
        .. Games(),
        O("before", false, "<K-local-before>",
            "Required by the proposal-selected dialect; exact before plugin."),
        O("after", false, "<K-local-after>",
            "Required by the proposal-selected dialect; exact after plugin."),
        O("proposal", false, "<K-local-proposal>",
            "Presence selects proposal-bound verification and requires --before and --after."),
        O("source-plugin", false, "<K-local-source-plugin>",
            "Required by the direct mutation-expectation dialect when --proposal is absent."),
        O("output-plugin", false, "<K-local-output-plugin>",
            "Required by the direct mutation-expectation dialect when --proposal is absent."),
        .. VerificationExpectationOptions()
    ];

    private static ImmutableArray<LegacyCommandOption> VerificationExpectationOptions()
    {
        ImmutableArray<LegacyCommandOption> patch =
            CoreBodyRecordOptionCatalog.For("body patch");
        ImmutableArray<LegacyCommandOption> rows = patch.Where(item => item.Name is not
                ("edition" or "game" or "input-plugin" or "output" or
                 "input-sha" or "input-sha256" or "expected-sha256" or
                 "proposal" or "dry-run" or "apply" or "preset-skin"))
            .Select(item => item with
            {
                Required = false,
                Description = $"Direct mutation-expectation dialect: {item.Description}"
            }).ToImmutableArray();
        return rows.Select(item => item.Name is "set-flag" or "clear-flag"
            ? item with
            {
                ValueSyntax = "<edition-compatible-flag,...> (empty string accepted but nonproducing)",
                AcceptedValues = NpcFlags,
                Description = "Direct mutation-expectation dialect: nonempty comma-separated edition-compatible flags; pc-level-mult is an accepted alias. The current parser also accepts an empty token, which produces no expectation and cannot satisfy the semantic at-least-one requirement."
            }
            : item).ToImmutableArray();
    }

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["preview render"] = PreviewRender,
            ["render npc"] = PreviewRender,
            ["preview npc"] = PreviewNpc,
            ["preview reroll"] = [.. Games(),
                O("manifest", true, "<preview-manifest.json>", "Existing preview-scene manifest."),
                O("npc", true, "<hexadecimal-form-id>", "NPC FormID that must match the manifest."),
                O("seed", true, "<signed-64-bit-integer>", "Signed 64-bit SplitMix64 selection seed."),
                O("output", true, "<new-preview-reroll.json>", "Fresh semantic reroll artifact.")],
            ["preview export-nif"] = [.. Games(),
                O("scene", true, "<preview-scene.json>", "Existing semantic preview-scene artifact."),
                O("output", true, "<new-*.nif.plan.json|new-*.nif>", "Fresh plan output when --asset-root is absent or fresh binary NIF when it is present."),
                O("asset-root", false, "<K-local-asset-root>", "Presence selects the admitted Blender/PyNifly binary export mode.")],
            ["plugin write"] = [.. Games(),
                O("proposal", true, "<proposal.json>", "Existing hash-bound NPC scalar proposal or Skyrim HDPT record proposal."),
                O("plugin", false, "<K-local-plugin>", "HDPT composition source plugin; omit to create a new plugin."),
                O("expected-sha256", false, "<64-hex-sha256>", "Required exact input hash for HDPT composition into --plugin."),
                O("data-root", false, "<K-local-data-root>", "HDPT source assets or copied clone provider."),
                O("private-root", false, "<K-local-data-root>", "Copy four HDPT assets into meshes/actors/character/EditorID; requires --data-root and fresh destinations."),
                O("output", true, "<new-plugin>", "Fresh output plugin; existing files are refused."),
                O("no-overwrite", false, "true|1", "Optional safety spelling; only true or 1 is accepted and omission still enforces no-overwrite.", "true", "1")],
            ["plugin verify"] = PluginVerify,
            ["plugin audit"] = [.. Games(),
                O("normalize-master-index", false, "true|1", "Compare Skyrim record bytes after proven FormID relocation into a common master layout.", "true", "1"),
                O("before", true, "<K-local-before-plugin>", "Existing before plugin."),
                O("after", true, "<K-local-after-plugin>", "Existing after plugin."),
                O("plugins-root", false, "<K-local-plugins-root>", "Canonical optional provider root; wins over --data-root and requires a load-order spelling."),
                O("data-root", false, "<K-local-plugins-root>", "Legacy alias for --plugins-root, used only when the canonical spelling is absent."),
                O("load-order", false, "<K-local-load-order.json>", "Canonical optional provider load order; wins over --loadorder and requires a root spelling."),
                O("loadorder", false, "<K-local-load-order.json>", "Legacy alias for --load-order, used only when the canonical spelling is absent.")],
            ["plugin deploy"] = [.. Games(),
                O("plugin", false, "<K-local-plugin>", "Canonical source plugin; wins over --input-plugin."),
                O("input-plugin", false, "<K-local-plugin>", "Alias for --plugin used only when the canonical spelling is absent."),
                O("data-root", true, "<existing-K-local-copied-Data-root>", "Existing copied Data destination root."),
                O("expected-sha256", false, "<64-hex-sha256>", "Canonical exact source hash; wins over --sha256."),
                O("sha256", false, "<64-hex-sha256>", "Alias for --expected-sha256 used only when the canonical spelling is absent.")]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Rows.ContainsKey(name);

    public static ImmutableArray<LegacyCommandOption> For(string name) => Rows[name];

    public static AgentValueKind ValueKindFor(string commandName, LegacyCommandOption option)
    {
        if (commandName == "plugin verify" && option.Name is not
            ("edition" or "game" or "before" or "after" or "proposal" or
             "source-plugin" or "output-plugin"))
        {
            if (option.Name is "set-flag" or "clear-flag")
                return AgentValueKind.EnumList;
            return CoreBodyRecordOptionCatalog.ValueKindFor("body patch", option);
        }
        return option.Name switch
        {
            "edition" or "game" => AgentValueKind.Enum,
            "visible" or "morphs" => AgentValueKind.EnumList,
            "play" or "render-headwear" or "no-overwrite" or "normalize-master-index" => AgentValueKind.Boolean,
            "frame" or "width" or "height" or "seed" => AgentValueKind.Integer,
            "expected-package-sha256" or "expected-sha256" or "sha256" => AgentValueKind.Sha256,
            "outfit" or "form" or "npc" or "form-id" => AgentValueKind.ArtifactReference,
            "variant" or "camera" or "lighting" or "animation" => AgentValueKind.Identifier,
            "plugin" when commandName == "preview npc" => AgentValueKind.Identifier,
            "plugin" => AgentValueKind.Path,
            "manifest" or "output" or "asset-root" or "image-output" or
                "intake" or "package-manifest" or "output-root" or
                "workflow-bundle" or "workflow-output" or "scene" or
                "proposal" or "before" or "after" or "source-plugin" or
                "output-plugin" or "plugins-root" or "data-root" or
                "load-order" or "loadorder" or "input-plugin" or "private-root" => AgentValueKind.Path,
            _ => AgentValueKind.String
        };
    }

    public static ImmutableArray<string> ConflictsFor(
        string commandName,
        string optionName) => (commandName, optionName) switch
        {
            ("preview render" or "render npc", "frame") => ["time"],
            ("preview render" or "render npc", "time") => ["frame"],
            ("plugin verify", "level") => ["level-mult"],
            ("plugin verify", "level-mult") => ["level"],
            ("plugin verify", "weight") => ["weight-triangle"],
            ("plugin verify", "weight-triangle") => ["weight"],
            _ => []
        };

    public static string? AliasFor(string commandName, string optionName) =>
        (commandName, optionName) switch
        {
            (_, "game") => "edition",
            ("plugin verify", "npc") => "form-id",
            ("plugin audit", "data-root") => "plugins-root",
            ("plugin audit", "loadorder") => "load-order",
            ("plugin deploy", "input-plugin") => "plugin",
            ("plugin deploy", "sha256") => "expected-sha256",
            _ => null
        };
}
