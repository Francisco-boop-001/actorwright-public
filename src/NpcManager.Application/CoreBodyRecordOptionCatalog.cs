using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class CoreBodyRecordOptionCatalog
{
    private static LegacyCommandOption O(string name, bool required, string syntax,
        string description, params string[] values) =>
        new(name, required, syntax, values.ToImmutableArray(), description);

    private static LegacyCommandOption[] Games(string values = "fallout4|skyrimse") =>
    [
        O("edition", false, values, "Selects the game edition; canonical --edition wins over --game.", values.Split('|')),
        O("game", false, values, "Alias for --edition used only when canonical --edition is absent.", values.Split('|'))
    ];

    private static LegacyCommandOption[] GameFirst(string values = "fallout4|skyrimse") =>
    [
        O("game", false, values, "Selects the game edition; canonical --game wins over --edition.", values.Split('|')),
        O("edition", false, values, "Alias for --game used only when canonical --game is absent.", values.Split('|'))
    ];

    private static ImmutableArray<LegacyCommandOption> R(params LegacyCommandOption[] rows) => [.. rows];
    private static ImmutableArray<LegacyCommandOption> G(params LegacyCommandOption[] rows) => [.. Games(), .. rows];

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["capabilities"] = [],
            ["schema export"] = R(
                O("command", false, "<command-name>", "Optional exact command name; omission exports all commands."),
                O("output", false, "<new-json-file>", "Optional fresh K-local schema contract output; omission returns the contract inline.")),
            ["diagnose"] = [],
            ["gui"] = R(
                O("launch", false, "<presence-flag>", "Presence selects desktop launch; omission reports availability."),
                O("executable", false, "<K-local-executable>", "Required when --launch is present."),
                O("workflow-bundle", false, "<workflow-bundle.json>", "Optional workflow bundle; must be paired with --workflow-bundle-sha256."),
                O("workflow-bundle-sha256", false, "<64-uppercase-hex-sha256>", "Exact workflow bundle hash; must be paired with --workflow-bundle.")),
            ["workspace scan-generated"] = G(O("data-root", true, "<copied-Data-root>", "Copied Data root scanned read-only.")),
            ["profile scan"] = G(
                O("data-root", true, "<copied-Data-root>", "Explicit copied Data root."),
                O("load-order", false, "<load-order-manifest>", "Optional copied load-order manifest; canonical spelling wins."),
                O("loadorder", false, "<load-order-manifest>", "Alias for --load-order.")),
            ["load-order validate"] = ValidatePlugins(),
            ["plugins validate"] = ValidatePlugins(),
            ["plugins resolve-load-order"] = G(
                O("plugins", true, "<plugins-root>", "Explicit copied plugin root."),
                O("load-order", false, "<load-order-manifest>", "Required copied load-order manifest; canonical spelling wins."),
                O("loadorder", false, "<load-order-manifest>", "Alias for --load-order.")),
            ["assets index"] = G(
                O("plugin", false, "<copied-plugin>", "Optional single plugin path; supplies its parent Data root."),
                O("data-root", false, "<copied-Data-root>", "One of --data-root or --plugin is required."),
                O("form-id", false, "<hexadecimal-form-id>", "Optional FormID inventory filter."),
                O("plugins", false, "<plugin,...>", "Optional ordered plugin selection."),
                O("filter", false, "unique|generic|template|unused,...", "Optional comma-separated NPC category list; canonical spelling wins.", "unique", "generic", "template", "unused"),
                O("category", false, "unique|generic|template|unused,...", "Alias for the comma-separated --filter list.", "unique", "generic", "template", "unused"),
                O("changed-only", false, "true|false", "Only literal true enables the changed-only filter.", "true", "false"),
                O("search", false, "<query>", "Optional inventory search text."),
                O("output", false, "<new-asset-index.json>", "When present, writes a fresh asset-index artifact instead of returning the inventory view.")),
            ["assets search"] = G(
                O("kind", true, "mesh|headpart|head-part", "Required asset kind; headpart and head-part are equivalent.", "mesh", "headpart", "head-part"),
                O("plugin", false, "<copied-plugin>", "Optional single plugin path; supplies its parent Data root."),
                O("data-root", false, "<copied-Data-root>", "One of --data-root or --plugin is required."),
                O("plugins", false, "<plugin,...>", "Optional ordered plugin selection."),
                O("query", false, "<query>", "Optional search query; canonical spelling wins."),
                O("search", false, "<query>", "Alias for --query.")),
            ["headpart choices"] = [.. Games("skyrimse"),
                O("data-root", true, "<copied-Data-root>", "Explicit copied Skyrim Data root."),
                O("plugins", true, "<plugin,...>", "Nonempty ascending copied-plugin order."),
                O("race", true, "<Plugin.esp|0xFormID>", "Required race reference."),
                O("sex", true, "female|male", "Required NPC sex.", "female", "male"),
                O("type", true, "<headpart-type>", "Required typed HDPT category."),
                O("search", false, "<query>", "Optional search text; canonical spelling wins."),
                O("query", false, "<query>", "Alias for --search.")],
            ["paint choices"] = [.. Games("skyrimse"),
                O("data-root", true, "<copied-Data-root>", "Explicit copied Skyrim Data root."),
                O("plugins", true, "<plugin,...>", "Nonempty ascending copied-plugin order."),
                O("category", true, "warpaint|body|hands|feet|face", "Required RaceMenu paint category.", "warpaint", "body", "hands", "feet", "face"),
                O("search", false, "<query>", "Optional search text; canonical spelling wins."),
                O("query", false, "<query>", "Alias for --search.")],
            ["forms search"] = G(
                O("plugin", false, "<copied-plugin>", "Optional single plugin path; supplies its parent Data root."),
                O("data-root", false, "<copied-Data-root>", "One of --data-root or --plugin is required."),
                O("type", false, "<signature,...>", "One of --type or --signature is required; canonical --type wins."),
                O("signature", false, "<signature,...>", "Alias for --type."),
                O("plugins", false, "<plugin,...>", "Optional ordered plugin selection."),
                O("form-id", false, "<hexadecimal-form-id>", "Optional exact FormID filter."),
                O("allow-null", false, "true|false", "Optional null-candidate toggle.", "true", "false"),
                O("query", false, "<query>", "Optional search query; canonical spelling wins."),
                O("search", false, "<query>", "Alias for --query.")),
            ["body sliders resolve"] = G(O("tri", true, "<BodySlide.tri>", "PIRT TRI input."), O("preset", true, "<typed-preset.json>", "Typed slider preset input.")),
            ["body sliders inspect-preset"] = G(O("preset-xml", true, "<SliderPreset.xml>", "BodySlide SliderPreset XML input.")),
            ["body sidecar inspect"] = G(O("file", true, "<sidecar.bssliders>", "BodySlide sidecar input.")),
            ["body sidecar write"] = [.. GameFirst(),
                O("plugin", true, "<plugin-name>", "Owning plugin name."), O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."),
                O("output", true, "<new-sidecar.bssliders>", "Fresh BodySlide sidecar output."),
                O("sliders", false, "<JSON-object|@K-local-file>", "One of --sliders or --morphs is required; canonical --sliders wins."),
                O("morphs", false, "<JSON-object|@K-local-file>", "Alias for --sliders."), O("editor-id", false, "<editor-id>", "Optional NPC EditorID.")],
            ["bodygen write"] = G(O("assignments", true, "<JSON-object|@K-local-file>", "SchemaVersion 1 BodyGen assignment document."), O("output", true, "<new-output-root>", "Fresh BodyGen output root.")),
            ["body weight resolve"] = G(O("input", true, "<weight-manifest.json>", "Typed weight-resolution manifest.")),
            ["body overlay patch"] = [.. GameFirst(), O("npc", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --npc wins."), O("form-id", false, "<hexadecimal-form-id>", "Alias for --npc."), O("layers", true, "<JSON-array|@K-local-file>", "Overlay layer array, limited to 1 MiB.")],
            ["body overlay bake"] = [.. GameFirst("skyrimse"), O("layers", true, "<JSON-object|@K-local-file>", "Skyrim overlay fold manifest, limited to 8 MiB."), O("output", true, "<new-K-local.dds>", "Fresh DDS output path.")],
            ["body transforms apply"] = [.. GameFirst("skyrimse"),
                O("npc", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --npc wins."), O("form-id", false, "<hexadecimal-form-id>", "Alias for --npc."),
                O("preset", true, "<K-local.jslot>", "Input RaceMenu preset."), O("output", true, "<new-K-local.jslot>", "Fresh transformed preset."),
                O("transforms", false, "<JSON-array|@K-local-file>", "Optional node-transform patch."), O("skin-overrides", false, "<JSON-array|@K-local-file>", "Optional skin-override patch; canonical spelling wins."),
                O("skins", false, "<JSON-array|@K-local-file>", "Alias for --skin-overrides."), O("expected-sha256", false, "<64-hex-sha256>", "Optional exact preset hash.")],
            ["body reset"] = BodyReset(),
            ["body patch"] = BodyPatch(),
            ["body weight normalize"] = [.. GameFirst("fallout4"), O("triangle", false, "thin=<n>,muscular=<n>,fat=<n>", "One of --triangle or --current is required; canonical --triangle wins."), O("current", false, "thin=<n>,muscular=<n>,fat=<n>", "Alias for --triangle.")],
            ["body weight redistribute"] = [.. GameFirst("fallout4"), O("triangle", false, "thin=<n>,muscular=<n>,fat=<n>", "One of --triangle or --current is required; canonical --triangle wins."), O("current", false, "thin=<n>,muscular=<n>,fat=<n>", "Alias for --triangle."), O("axis", true, "thin|muscular|fat", "Redistribution axis.", "thin", "muscular", "fat"), O("value", true, "<finite-number>", "New finite axis value.")],
            ["bodygen build"] = G(O("plugin", true, "<plugin-name>", "Owning plugin name."), O("npc", true, "<hexadecimal-form-id>", "Target NPC FormID."), O("mod-name", true, "<name>", "BodyGen template name."), O("morphs", true, "<K-local-morphs-file>", "Typed morph input file."), O("output-root", true, "<new-output-root>", "Fresh BodyGen output root.")),
            ["records list"] = G(O("plugin", false, "<copied-plugin>", "Optional single plugin path; exclusive input alternative to --data-root."), O("data-root", false, "<copied-Data-root>", "One of --data-root or --plugin is required."), O("plugins", false, "<plugin,...>", "Optional ordered plugins with --data-root."), O("signature", false, "<up-to-8-chars>", "Optional record signature filter."), O("search", false, "<query>", "Optional trimmed search text.")),
            ["records propose"] = G(O("type", true, "<four-character-signature>", "Record signature."), O("mode", true, "new|template|override", "Proposal mode.", "new", "template", "override"), O("form-id", true, "<nonzero-form-id>", "Allocated or target FormID."), O("editor-id", true, "<editor-id>", "Record EditorID."), O("output", true, "<new-proposal.json>", "Fresh proposal output."), O("source", false, "<nonzero-form-id>", "Optional source FormID."), O("masters", false, "<JSON-array|@K-local-file>", "Optional ordered plugin masters."), O("name", false, "<display-name>", "Optional display name."),
                O("model", false, "<relative-nif>", "Skyrim HDPT model; required unless cloning."),
                O("tri-race", false, "<relative-tri>", "HDPT Race morph role."),
                O("tri-chargen", false, "<relative-tri>", "HDPT Chargen morph role."),
                O("tri-dialogue", false, "<relative-tri>", "HDPT dialogue Mesh morph role."),
                O("valid-races", false, "<Plugin|FormID>", "HDPT FLST reference in the output or unchanged master table."),
                O("extra-parts", false, "<Plugin|FormID,...>", "HDPT HNAM links."),
                O("flags", false, "<flag,...>", "HDPT flags: playable,male,female,extra,solid-tint,use-texture-lighting."),
                O("part-type", false, "misc|face|eyes|hair|facial-hair|scar|eyebrows", "Skyrim HDPT type."),
                O("clone-from", false, "<Plugin|FormID>", "Clone an existing HDPT into a new output-owned local FormID."),
                O("retarget-valid-races", false, "<Plugin|FormID>", "Replace the cloned HDPT RNAM with this FLST.")),
            ["changes list"] = G(O("session", true, "<session.json>", "Explicit change-tracking session.")),
            ["changes update"] = G(O("session", true, "<session.json>", "Explicit change-tracking session."), O("record", true, "<nonzero-form-id>", "Target record FormID."), O("action", true, "reset|delete", "Review action.", "reset", "delete"), O("output", true, "<new-review.json>", "Fresh review artifact."), O("signature", false, "<record-signature>", "Optional expected record signature."))
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static ImmutableArray<LegacyCommandOption> For(string commandName) =>
        Rows.TryGetValue(commandName, out var rows) ? rows : [];

    public static bool Contains(string commandName) => Rows.ContainsKey(commandName);

    public static AgentValueKind ValueKindFor(string commandName, LegacyCommandOption option)
    {
        string syntax = option.ValueSyntax;
        if (commandName == "assets index" && option.Name is "filter" or "category")
            return AgentValueKind.EnumList;
        if (option.Name is "launch" or "changed-only" or "allow-null" or "dry-run" or "apply" or "clear-skin")
            return AgentValueKind.Boolean;
        if (option.Name is "edition" or "game" || option.AcceptedValues.Length > 0 && option.Name is "kind" or "sex" or "filter" or "category" or "section" or "axis" or "action" or "mode")
            return AgentValueKind.Enum;
        if (syntax.Contains("sha256", StringComparison.OrdinalIgnoreCase)) return AgentValueKind.Sha256;
        if (syntax.Contains("JSON", StringComparison.Ordinal)) return AgentValueKind.Json;
        if (option.Name is "executable" or "load-order" or "loadorder" || syntax.Contains("root", StringComparison.OrdinalIgnoreCase) || syntax.Contains("file", StringComparison.OrdinalIgnoreCase) || syntax.Contains("plugin>", StringComparison.OrdinalIgnoreCase) || syntax.Contains(".tri>", StringComparison.OrdinalIgnoreCase) || syntax.Contains(".xml>", StringComparison.OrdinalIgnoreCase) || syntax.Contains(".json>", StringComparison.OrdinalIgnoreCase) || syntax.Contains(".dds>", StringComparison.OrdinalIgnoreCase) || syntax.Contains(".jslot>", StringComparison.OrdinalIgnoreCase) || syntax.Contains(".bssliders>", StringComparison.OrdinalIgnoreCase)) return AgentValueKind.Path;
        if (syntax.Contains("integer", StringComparison.OrdinalIgnoreCase) || syntax == "<byte-0..255>") return AgentValueKind.Integer;
        if (option.Name is "form-id" or "npc" or "race" or "voice" or "class" or "combat-style" or "default-outfit" or "sleep-outfit" or "skin" or "record" or "source") return AgentValueKind.ArtifactReference;
        if (option.Name is "editor-id" or "mod-name" or "preset-skin") return AgentValueKind.Identifier;
        return AgentValueKind.String;
    }

    private static ImmutableArray<LegacyCommandOption> ValidatePlugins() => G(
        O("plugin", true, "<copied-plugin>", "Plugin to validate."),
        O("load-order", false, "<load-order-manifest>", "Required copied load-order manifest; canonical spelling wins."),
        O("loadorder", false, "<load-order-manifest>", "Alias for --load-order."));

    private static ImmutableArray<LegacyCommandOption> BodyReset() => [.. GameFirst(),
        O("npc", false, "<hexadecimal-form-id>", "One of --npc or --form-id is required; canonical --npc wins."), O("form-id", false, "<hexadecimal-form-id>", "Alias for --npc."),
        O("current", true, "<current.body.json>", "Current body snapshot."), O("baseline", true, "<baseline.body.json>", "Construction baseline snapshot."), O("output", true, "<new.body.json>", "Fresh reset snapshot."),
        O("section", true, "weight|morphs|sliders|skin|overlays|transforms|skin-overrides", "Exact section replaced from baseline.", "weight", "morphs", "sliders", "skin", "overlays", "transforms", "skin-overrides"),
        O("expected-sha256", false, "<64-hex-sha256>", "Optional exact current-snapshot hash."), O("proposal", false, "<new-proposal.json>", "Optional proposal artifact."),
        O("dry-run", false, "true|false|1", "true or 1 selects proposal-only behavior and conflicts with active --apply.", "true", "false", "1"), O("apply", false, "true|false|1", "true or 1 applies and conflicts with active --dry-run.", "true", "false", "1")];

    private static ImmutableArray<LegacyCommandOption> BodyPatch()
    {
        string[] names = ["edition", "game", "input-plugin", "output", "form-id", "npc", "editor-id", "name", "sex", "race", "voice", "class", "combat-style", "level", "level-mult", "magicka-offset", "stamina-offset", "health-offset", "calc-min", "calc-max", "speed-multiplier", "disposition", "bleedout", "player-health", "player-magicka", "player-stamina", "skill-values", "skill-offsets", "height", "far-model-distance", "geared-weapons", "xp-offset", "set-flag", "clear-flag", "keywords", "add-keyword", "remove-keyword", "factions", "add-faction", "update-faction", "remove-faction", "inventory", "add-inventory", "update-inventory", "remove-inventory", "default-outfit", "sleep-outfit", "perks", "add-perk", "update-perk", "remove-perk", "actor-effects", "add-actor-effect", "remove-actor-effect", "properties", "add-property", "update-property", "remove-property", "appr", "add-appr", "remove-appr", "skin", "clear-skin", "preset-skin", "regions", "weight", "weight-triangle", "input-sha", "input-sha256", "expected-sha256", "proposal", "dry-run", "apply"];
        var required = new HashSet<string>(["input-plugin", "output"], StringComparer.Ordinal);
        names = [.. names, "whole-skin", "whole-skin-sha256", "aidt"];
        return names.Select(name => name switch
        {
            "edition" or "game" => O(name, false, "fallout4|skyrimse", $"Game selector; --edition wins over --game.", "fallout4", "skyrimse"),
            "input-plugin" => O(name, true, "<K-local-plugin>", "Copied source plugin."),
            "output" => O(name, true, "<new-plugin>", "Fresh patched plugin output."),
            "form-id" or "npc" => O(name, false, "<hexadecimal-form-id>", "One of --form-id or --npc is required; canonical --form-id wins."),
            "sex" => O(name, false, "female|male", "Optional NPC sex.", "female", "male"),
            "dry-run" or "apply" => O(name, false, "true|false|1", "true or 1 activates this mutually exclusive execution mode.", "true", "false", "1"),
            "clear-skin" => O(name, false, "true|false|1", "true or 1 clears the skin and conflicts with --skin; --preset-skin remains independently permitted by the handler.", "true", "false", "1"),
            "input-sha" or "input-sha256" or "expected-sha256" => O(name, false, "<64-hex-sha256>", "Optional equivalent source-hash spelling; supplied values must agree."),
            "level" or "level-mult" => O(name, false, "<invariant-decimal>", $"Optional {name} decimal scalar."),
            "magicka-offset" or "stamina-offset" or "health-offset" or "speed-multiplier" or "disposition" or "bleedout" or "xp-offset" => O(name, false, "<signed-16-bit-integer>", $"Optional {name} scalar."),
            "calc-min" or "calc-max" or "player-health" or "player-magicka" or "player-stamina" => O(name, false, "<unsigned-16-bit-integer>", $"Optional {name} scalar."),
            "geared-weapons" => O(name, false, "<byte-0..255>", "Optional geared-weapons byte."),
            "height" or "far-model-distance" or "weight" => O(name, false, "<invariant-float>", $"Optional {name} scalar."),
            "weight-triangle" => O(name, false, "thin=<n>,muscular=<n>,fat=<n>", "Fallout 4 weight triangle; conflicts with scalar --weight."),
            "proposal" => O(name, false, "<new-proposal.json>", "Optional proposal artifact."),
            "whole-skin" => O(name, false, "<JSON-object|@K-local-file>", "Female Skyrim SE private skin composition, paired with --whole-skin-sha256. headPolicy preserve allocates body/hands TXST, body/hands/feet ARMA and skin ARMO while changing only WNAM; an absent policy retains legacy private-head replacement. FaceGen is not rebuilt."),
            "whole-skin-sha256" => O(name, false, "<64-hex-sha256>", "Required exact UTF-8 whole-skin document SHA-256 when --whole-skin is supplied."),
            "aidt" => O(name, false, "<JSON-object|@K-local-file>", "Skyrim SE explicit aggression, confidence, morality, assistance and energy (0..255). Unspecified fields and remaining AIDT bytes are preserved; effective follower faction membership refuses helpsNobody."),
            "race" or "voice" or "class" or "combat-style" or "default-outfit" or "sleep-outfit" => O(name, false, "<Plugin.esp|0xFormID|none>", $"Optional {name} reference."),
            "skin" => O(name, false, "<Plugin.esp|0xFormID>", "Optional skin reference."),
            "editor-id" or "name" or "preset-skin" => O(name, false, "<non-empty-string>", $"Optional {name} replacement."),
            "skill-values" or "skill-offsets" => O(name, false, "<skill=byte,...>", $"Optional {name} collection."),
            "factions" or "add-faction" or "update-faction" => O(name, false, "<Plugin.esp|0xFormID=signed-byte-rank,...>", $"Optional {name} collection."),
            "inventory" or "add-inventory" or "update-inventory" => O(name, false, "<Plugin.esp|0xFormID=signed-32-bit-count,...>", $"Optional {name} collection."),
            "perks" or "add-perk" or "update-perk" => O(name, false, "<Plugin.esp|0xFormID=unsigned-byte-rank,...>", $"Optional {name} collection."),
            "properties" or "add-property" or "update-property" => O(name, false, "<Plugin.esp|0xFormID=invariant-float,...>", $"Optional {name} collection."),
            "regions" => O(name, false, "<JSON-object|@K-local-file>", "Optional region patch object."),
            "set-flag" or "clear-flag" => O(name, false, "<edition-compatible-flag,...>", $"Optional {name} collection."),
            _ => O(name, required.Contains(name), "<Plugin.esp|0xFormID,...>", $"Optional {name} reference collection.")
        }).ToImmutableArray();
    }
}
