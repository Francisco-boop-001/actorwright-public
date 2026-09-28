using System.Collections.Immutable;

namespace NpcManager.Application;

internal static class ReferenceHairFinishOptionCatalog
{
    private static readonly ImmutableArray<LegacyCommandOption> HairApplyOptions =
    [
        O("request", true, "<request.json>", "Hash-bound hair-region request document."),
        O("request-sha256", true, "<64-hex-sha256>", "Exact request-document SHA-256."),
        O("proposal", true, "<proposal.json>", "Hash-bound hair-region proposal document."),
        O("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal-document SHA-256."),
        O("output", true, "<output.nif>", "Exact output NIF path bound by request and proposal; apply requires a new path and verify requires the existing applied path."),
        O("manifest", true, "<manifest.json>", "Exact canonical manifest path bound by request and proposal; apply writes it and verify reopens it.")
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<LegacyCommandOption>> Rows =
        new Dictionary<string, ImmutableArray<LegacyCommandOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["preset design-propose"] =
            [
                O("intake", false, "<intake.json>", "Required outside template mode. Hash-bound reference-authoring intake session."),
                O("intake-sha256", false, "<64-hex-sha256>", "Required with --intake. Admits raw or canonical SHA-256 and retains canonical identity."),
                O("output", false, "<new-directory>", "Required with --intake. New deterministic design-proposal output root."),
                O("template-output", false, "<fresh.json>", "Exclusive template mode: writes a canonical intake session with placeholders before runtime admission. Fill placeholders before validation; existing files are refused.")
            ],
            ["preset create-from-reference"] =
            [
                O("proposal", true, "<proposal.json>", "Hash-bound reviewed reference proposal."),
                O("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal-document SHA-256."),
                O("review", true, "<review.json>", "Hash-bound human review document."),
                O("review-sha256", true, "<64-hex-sha256>", "Exact review-document SHA-256."),
                O("resource", true, "<resource.json>", "Hash-bound reviewed resource document."),
                O("resource-sha256", true, "<64-hex-sha256>", "Exact resource-document SHA-256."),
                O("jslot-output", true, "<expected-derived-preset.jslot>", "Expected derived JSlot path; it does not select the write destination and is parsed and compared only after --evidence-root is promoted."),
                O("evidence-root", true, "<new-directory>", "Fresh transaction root promoted before the handler's post-write --jslot-output comparison."),
                O("apply", false, "true|false", "Optional apply flag; true requires --accepted-proposal-sha256, which is forbidden otherwise.", "true", "false"),
                O("accepted-proposal-sha256", false, "<64-hex-sha256>", "Required if and only if --apply is true and must bind the accepted proposal.")
            ],
            ["npc create-from-reference"] =
            [
                O("proposal", true, "<proposal.json>", "Hash-bound reviewed reference proposal."),
                O("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal-document SHA-256."),
                O("review", true, "<review.json>", "Hash-bound human review document."),
                O("review-sha256", true, "<64-hex-sha256>", "Exact review-document SHA-256."),
                O("resource", true, "<resource.json>", "Hash-bound reviewed resource document."),
                O("resource-sha256", true, "<64-hex-sha256>", "Exact resource-document SHA-256."),
                O("accepted-proposal-sha256", true, "<64-hex-sha256>", "Exact accepted proposal SHA-256."),
                O("request", true, "<request.json>", "Reviewed NPC execution request."),
                O("request-sha256", true, "<64-hex-sha256>", "Exact request-document SHA-256."),
                O("data-root", true, "<K-local-data-root>", "Data root used by the existing JSlot transaction."),
                O("plugins", true, "<plugin,...>", "Nonempty duplicate-free ordered plugin list, consumed in the supplied order."),
                O("transaction-root", true, "<new-directory>", "Exact transaction-root path."),
                O("apply", true, "true", "Required and must be true.", "true")
            ],
            ["facegen hair-regions analyze"] =
            [
                O("source", true, "<source.nif>", "Existing FaceGeom source NIF."),
                O("expected-source-sha256", true, "<64-hex-sha256>", "Exact source NIF SHA-256."),
                O("analysis", true, "<new-analysis.json>", "New canonical analysis output."),
                O("assignment-template", true, "<new-request.json>", "New Preserve-default assignment-template output.")
            ],
            ["facegen hair-regions propose"] =
            [
                O("analysis", true, "<analysis.json>", "Hash-bound hair-region analysis document."),
                O("analysis-sha256", true, "<64-hex-sha256>", "Exact analysis-document SHA-256."),
                O("request", true, "<request.json>", "Hash-bound hair-region request document."),
                O("request-sha256", true, "<64-hex-sha256>", "Exact request-document SHA-256."),
                O("proposal", true, "<new-proposal.json>", "New canonical proposal output.")
            ],
            ["facegen hair-regions preview"] =
            [
                O("request", true, "<request.json>", "Hash-bound hair-region request document."),
                O("request-sha256", true, "<64-hex-sha256>", "Exact request-document SHA-256."),
                O("proposal", true, "<proposal.json>", "Hash-bound hair-region proposal document."),
                O("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal-document SHA-256."),
                O("intake", true, "<reviewed-intake.json>", "Reviewed intake used to bind materialized providers and load order."),
                O("output-root", true, "<new-directory>", "New public preview-bundle root.")
            ],
            ["facegen hair-regions apply"] = HairApplyOptions,
            ["facegen hair-regions verify"] = HairApplyOptions,
            ["npc finish analyze"] =
            [
                O("request", true, "<request.json>", "Hash-bound Finish Core request document."),
                O("request-sha256", true, "<64-hex-sha256>", "Exact request-document SHA-256."),
                O("proposal", true, "<new-proposal.json>", "New Finish Core proposal output outside validation mode."),
                O("validate-all", false, "true|false|1", "Optional validation mode; exactly true or 1 accumulates safely reachable phase diagnostics without publishing the proposal.", "true", "false", "1"),
                O("data-root", false, "<absolute-K-local-path>", "Optional external-install context; --data-root and --plugins must be supplied together."),
                O("plugins", false, "<plugin,...>", "Optional complete enabled order; --data-root and --plugins must be supplied together.")
            ],
            ["npc finish apply"] =
            [
                O("request", true, "<request.json>", "Hash-bound Finish Core request document."),
                O("request-sha256", true, "<64-hex-sha256>", "Exact request-document SHA-256."),
                O("proposal", true, "<proposal.json>", "Hash-bound Finish Core proposal document."),
                O("proposal-sha256", true, "<64-hex-sha256>", "Exact proposal-document SHA-256."),
                O("validate-all", false, "true|false|1", "Optional validation mode; true or 1 accumulates safely reachable phase diagnostics.", "true", "false", "1"),
                O("data-root", false, "<absolute-K-local-path>", "Optional for ordinary requests. External-schema non-validation apply requires both --data-root and --plugins; --validate-all true or 1 may omit both, but supplying either still requires the pair."),
                O("plugins", false, "<plugin,...>", "Optional for ordinary requests. External-schema non-validation apply requires both --data-root and --plugins as a complete enabled order; --validate-all true or 1 may omit both, but supplying either still requires the pair.")
            ],
            ["npc finish verify"] =
            [
                O("manifest", true, "<manifest.json>", "Hash-bound Finish Core manifest document."),
                O("manifest-sha256", true, "<64-hex-sha256>", "Exact manifest-document SHA-256."),
                O("data-root", false, "<absolute-K-local-path>", "Optional external-install context; --data-root and --plugins must be supplied together."),
                O("plugins", false, "<plugin,...>", "Optional complete enabled order; --data-root and --plugins must be supplied together.")
            ]
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string commandName) => Rows.ContainsKey(commandName);

    public static ImmutableArray<LegacyCommandOption> For(string commandName) =>
        Rows.TryGetValue(commandName, out ImmutableArray<LegacyCommandOption> rows)
            ? rows
            : [];

    public static AgentValueKind ValueKindFor(LegacyCommandOption option)
    {
        if (option.Name is "apply" or "validate-all") return AgentValueKind.Boolean;
        if (option.Name.EndsWith("sha256", StringComparison.Ordinal)) return AgentValueKind.Sha256;
        if (option.Name == "plugins") return AgentValueKind.String;
        return AgentValueKind.Path;
    }

    private static LegacyCommandOption O(
        string name, bool required, string syntax, string description,
        params string[] acceptedValues) =>
        new(name, required, syntax, acceptedValues.ToImmutableArray(), description);
}
