using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class NpcCreationOptionCatalog
{
    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["npc create"] =
            [
                O("edition", false, "skyrimse", "Optional edition; defaults to skyrimse and wins over --game.", "skyrimse"),
                O("game", false, "skyrimse", "Legacy edition alias used only when --edition is absent; omission defaults to skyrimse.", "skyrimse"),
                O("provider-manifest", true, "<provider-manifest.json>", "Exact provider manifest."),
                O("provider-manifest-sha256", true, "<64-hex-sha256>", "Expected provider-manifest SHA-256; V1 accepts the Sha256Hash value dialect."),
                O("template-plugin", true, "<K-local-template-plugin>", "Exact qualified Skyrim template plugin."),
                O("template-sha256", true, "<64-hex-sha256>", "Expected template-plugin SHA-256."),
                O("template-npc", true, "<nonzero-plugin-local-24-bit-form-id>", "Template NPC must use a nonzero plugin-local 24-bit hexadecimal FormID."),
                O("facegeom-carrier", true, "<K-local-carrier.nif>", "Exact qualified FaceGeom carrier NIF."),
                O("facegeom-sha256", true, "<64-hex-sha256>", "Expected FaceGeom-carrier SHA-256."),
                O("facetint-manifest", true, "<facetint-manifest.json>", "Qualified FaceTint layer manifest."),
                O("provider-root", true, "<K-local-provider-root>", "K-local provider root used for FaceTint materialization."),
                O("dependencies", true, "<dependency-manifest.json>", "Exact dependency manifest."),
                O("output-root", true, "<new-K-local-package-root>", "Fresh package output root."),
                O("plugin", true, "<new-plugin.esp>", "Output plugin filename."),
                O("editor-id", true, "<editor-id>", "New NPC EditorID."),
                O("name", true, "<display-name>", "New NPC display name."),
                O("role", true, "static-validation", "Gate 1 accepts only the fixed static-validation role.", "static-validation"),
                O("sex", true, "female", "Gate 1 accepts only the fixed female carrier sex.", "female"),
                O("race", true, "<Plugin.esp|nonzero-plugin-local-24-bit-form-id>", "Portable race reference with a nonzero plugin-local 24-bit FormID."),
                O("voice", true, "<Plugin.esp|nonzero-plugin-local-24-bit-form-id>", "Portable voice reference with a nonzero plugin-local 24-bit FormID."),
                O("class", true, "<Plugin.esp|nonzero-plugin-local-24-bit-form-id>", "Portable class reference with a nonzero plugin-local 24-bit FormID."),
                O("combat-style", true, "<Plugin.esp|nonzero-plugin-local-24-bit-form-id>", "Portable combat-style reference with a nonzero plugin-local 24-bit FormID."),
                O("default-outfit", true, "<Plugin.esp|nonzero-plugin-local-24-bit-form-id>", "Portable default-outfit reference with a nonzero plugin-local 24-bit FormID."),
                B("unique", true), B("essential", false), B("protected", false),
                B("respawns", false), B("auto-calc-stats", true),
                O("level", false, "<0..32767>", "Invariant integer fixed level admitted by the transaction; defaults to 1."),
                I("magicka-offset", "<-32768..32767>", "0"),
                I("stamina-offset", "<-32768..32767>", "0"),
                I("health-offset", "<-32768..32767>", "0"),
                O("calc-min-level", false, "<0..32767;value<=calc-max-level>", "Invariant integer from 0 through 32767; defaults to 1 and must be less than or equal to --calc-max-level."),
                O("calc-max-level", false, "<0..32767;value>=calc-min-level>", "Invariant integer from 0 through 32767; defaults to 1 and must be greater than or equal to --calc-min-level."),
                O("speed", false, "<1..32767>", "Positive invariant Int16 speed multiplier; defaults to 100."),
                I("disposition", "<-32768..32767>", "35"),
                I("bleedout", "<-32768..32767>", "0"),
                I("base-health", "<0..65535>", "50"),
                I("base-magicka", "<0..65535>", "50"),
                I("base-stamina", "<0..65535>", "50"),
                O("height", false, "<finite-number-equal-to-1.0>", "Finite invariant single-precision height; Gate 1 admits exactly 1.0 and defaults to 1.0."),
                O("weight", false, "<finite-0..100>", "Finite invariant single-precision weight from 0 through 100; defaults to 0."),
                O("nam5", false, "<integer-equal-to-255>", "Invariant integer; Gate 1 admits exactly 255 and defaults to 255.")
            ],
            ["npc create-from-preset"] =
            [
                O("request", true, "<@optional-K-local-request.json>", "Exact schemaVersion 1, 2, or 3 NPC execution-request document; a leading @ is optional. Build grammar remains inside the document."),
                O("request-sha256", true, "<64-hex-sha256>", "Expected request-document SHA-256; V1 retains the Sha256Hash dialect.")
            ],
            ["npc create-from-jslot"] =
            [
                O("request", true, "<@optional-K-local-request.json>", "Exact schemaVersion 1, 2, or 3 NPC execution request; a leading @ is optional in every V1 mode."),
                O("request-sha256", true, "<64-hex-sha256>", "Expected request-document SHA-256; V1 does not impose the protocol-2 uppercase-only spelling."),
                O("preset", false, "<K-local-preset.jslot>", "Required in ordinary preflight/build mode; exact RaceMenu JSlot."),
                O("preset-sha256", false, "<64-hex-sha256>", "Required in ordinary preflight/build mode; V1 accepts lowercase hash spelling."),
                O("data-root", false, "<K-local-data-root>", "Required in ordinary preflight/build mode; copied Data authority."),
                O("plugins", false, "<plugin,...>", "Required in ordinary preflight/build mode; V1 removes empty comma entries and preserves the supplied order without requiring uniqueness."),
                O("companion-root", false, "<new-K-local-companion-root>", "Required in ordinary preflight/build mode; companion transaction root."),
                O("preflight-output", false, "<new-preflight.json>", "Selects preflight mode and is exclusive with either reviewed-preflight option."),
                O("face-bake-authority-output", false, "<new-face-bake-authority.json>", "Only with --preflight-output: derive an exact authority, then explicitly bind its path/hash and rerun ordinary preflight; no existing request is rewritten."),
                O("reviewed-preflight", false, "<reviewed-preflight.json>", "Optional reviewed-build authority; must be paired with --reviewed-preflight-sha256 and is exclusive with --preflight-output."),
                O("reviewed-preflight-sha256", false, "<64-hex-sha256>", "Hash paired with --reviewed-preflight; V1 accepts lowercase hash spelling."),
                O("migrated-request-root", false, "<new-K-local-migrated-root>", "Required in every provider-migration mode."),
                O("provider-migration-output", false, "<new-provider-migration-review.json>", "Selects provider-migration review generation; exclusive with reviewed migration inputs and ordinary build options."),
                O("reviewed-provider-migration", false, "<reviewed-provider-migration.json>", "Canonical reviewed-migration path; pair with its hash and do not mix with legacy aliases."),
                O("reviewed-provider-migration-sha256", false, "<64-hex-sha256>", "Canonical reviewed-migration hash; pair with its path and do not mix with legacy aliases."),
                O("provider-migration", false, "<reviewed-provider-migration.json>", "Legacy alias for --reviewed-provider-migration; canonical and legacy pairs may not be mixed."),
                O("provider-migration-sha256", false, "<64-hex-sha256>", "Legacy alias for --reviewed-provider-migration-sha256; canonical and legacy pairs may not be mixed.")
            ]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Rows.ContainsKey(name);
    public static ImmutableArray<LegacyCommandOption> For(string name) =>
        Rows.TryGetValue(name, out var rows) ? rows : [];

    public static AgentValueKind ValueKindFor(LegacyCommandOption option) => option.Name switch
    {
        "edition" or "game" or "role" or "sex" => AgentValueKind.Enum,
        "unique" or "essential" or "protected" or "respawns" or "auto-calc-stats" => AgentValueKind.Boolean,
        "level" or "magicka-offset" or "stamina-offset" or "health-offset" or "calc-min-level" or
            "calc-max-level" or "speed" or "disposition" or "bleedout" or
            "base-health" or "base-magicka" or "base-stamina" or "nam5" => AgentValueKind.Integer,
        "provider-manifest-sha256" or "template-sha256" or "facegeom-sha256" or
            "request-sha256" or "preset-sha256" or "reviewed-preflight-sha256" or
            "reviewed-provider-migration-sha256" or "provider-migration-sha256" => AgentValueKind.Sha256,
        "template-npc" or "race" or "voice" or "class" or "combat-style" or "default-outfit" => AgentValueKind.ArtifactReference,
        "plugin" or "editor-id" => AgentValueKind.Identifier,
        "name" or "plugins" or "height" or "weight" => AgentValueKind.String,
        _ => AgentValueKind.Path
    };

    private static LegacyCommandOption B(string name, bool fallback) =>
        O(name, false, "true|false", $"Literal Boolean parsed by value; defaults to {fallback.ToString().ToLowerInvariant()}.", "true", "false");
    private static LegacyCommandOption I(string name, string syntax, string fallback) =>
        O(name, false, syntax, $"Invariant integer; defaults to {fallback}.");
    private static LegacyCommandOption O(string name, bool required, string syntax, string description, params string[] accepted) =>
        new(name, required, syntax, accepted.ToImmutableArray(), description);
}
