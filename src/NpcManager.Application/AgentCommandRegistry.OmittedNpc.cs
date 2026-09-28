using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsOmittedNpcCommand(string name) =>
        OmittedNpcOptionCatalog.Contains(name);

    private static AgentCommandContract OmittedNpcContract(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = OmittedNpcInputs(descriptor.Name);
        bool readOnly = descriptor.Name is "npc list" or "npc search" or "npc inspect" or
            "preset catalog" or "npc follower-finish verify" or
            "npc follower-finish pair-verify" or "npc placement interior verify";
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = descriptor.Name.StartsWith("npc follower-finish", StringComparison.Ordinal) ||
                             descriptor.Name.StartsWith("npc placement interior", StringComparison.Ordinal) ||
                             descriptor.Name == "preset catalog"
                ? [GameEdition.SkyrimSpecialEdition]
                : [GameEdition.Fallout4, GameEdition.SkyrimSpecialEdition],
            Limitations = descriptor.Limitations.AddRange(OmittedNpcLimitations(descriptor.Name)),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = OmittedNpcRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = OmittedNpcOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = OmittedNpcResult(descriptor.Name),
            Effects = OmittedNpcEffects(descriptor.Name),
            RetryPolicy = OmittedNpcRetry(descriptor.Name),
            Determinism = OmittedNpcDeterminism(descriptor.Name),
            SupportsDryRun = descriptor.Name is "npc reset" or "npc materialize-template",
            Authority = OmittedNpcAuthority(descriptor.Name, readOnly),
            Transitions = []
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract> OmittedNpcRelationships(string name)
    {
        if (name is "npc list" or "npc search" or "npc inspect")
            return [AtLeastOneNpc(["edition", "game"]), AtLeastOneNpc(["data-root", "plugin"])];
        if (name == "npc reset")
            return
            [
                AtLeastOneNpc(["game", "edition"]),
                AtLeastOneNpc(["current-plugin", "plugin"]),
                AtLeastOneNpc(["npc", "form-id"]),
                ActiveConflict("dry-run", "apply"),
                RequiredWhenActive("expected-sha256", "apply")
            ];
        if (name == "npc materialize-template")
            return
            [
                AtLeastOneNpc(["game", "edition"]),
                AtLeastOneNpc(["input-plugin", "plugin"]),
                ActiveConflict("dry-run", "apply"),
                RequiredWhenActive("expected-sha256", "apply")
            ];
        if (name == "preset catalog")
            return
            [
                RequiresTogetherFour(["data-root", "plugins", "race", "sex"]),
                new AgentOptionRelationshipContract(
                    AgentOptionRelationshipKind.RequiredWhen,
                    ["data-root", "plugins", "race", "sex"],
                    ["compatible-only"],
                    "The complete provider-compatibility quartet is required whenever --compatible-only is present.")
                { Trigger = PresentTrigger("compatible-only") }
            ];
        return [];
    }

    private static AgentOptionRelationshipContract AtLeastOneNpc(ImmutableArray<string> options) =>
        new(AgentOptionRelationshipKind.AtLeastOne, options, [],
            $"At least one of {string.Join(" or ", options.Select(item => "--" + item))} is required.");

    private static AgentOptionRelationshipContract RequiresTogetherFour(ImmutableArray<string> options) =>
        new(AgentOptionRelationshipKind.RequiresTogether, options, [],
            "--data-root, --plugins, --race, and --sex must be supplied together.");

    private static AgentOptionRelationshipContract ActiveConflict(string first, string second) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, [first, second], [first, second],
            $"Active --{first} true or 1 and --{second} true or 1 cannot be combined.")
        {
            Trigger = new(AgentPredicateCombination.All,
            [
                ActiveTruePredicate(first),
                ActiveTruePredicate(second)
            ])
        };

    private static AgentOptionRelationshipContract RequiredWhenActive(string required, string boolean) =>
        new(AgentOptionRelationshipKind.RequiredWhen, [required], [boolean],
            $"--{required} is required when --{boolean} is true or 1.")
        { Trigger = ActiveTrueTrigger(boolean) };

    private static AgentRelationshipPredicate ActiveTruePredicate(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf, ["true", "1"], false);

    private static AgentOptionRelationshipTrigger ActiveTrueTrigger(string option) =>
        new(AgentPredicateCombination.All, [ActiveTruePredicate(option)]);

    private static AgentOptionRelationshipTrigger PresentTrigger(string option) =>
        new(AgentPredicateCombination.All,
            [new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf, ["__absent__"], false)]);

    private static ImmutableArray<AgentArtifactContract> OmittedNpcInputs(string name) => name switch
    {
        "npc list" or "npc search" or "npc inspect" =>
            [new("copied-game-inventory", [], "Explicit copied Data root or plugin-derived inventory root and optional plugin selection.")],
        "npc reset" =>
            [new("npc-current-and-baseline-plugins", [], "Explicit current and same-plugin baseline inputs for one NPC section.")],
        "npc materialize-template" =>
            [new("npc-template-source-plugin", [], "Explicit input plugin containing the target NPC and followed template chain.")],
        "npc follower-finish analyze" => [new("follower-finish-request", [], "Exact hash-bound numeric-private schemaVersion 1 follower-finish request.")],
        "npc follower-finish apply" =>
            [new("follower-finish-request", [], "Exact hash-bound numeric-private schemaVersion 1 follower-finish request."), new("follower-finish-proposal", [], "Exact hash-bound numeric-private schemaVersion 1 follower-finish proposal.")],
        "npc follower-finish verify" =>
            [new("follower-finish-request", [], "Exact hash-bound numeric-private schemaVersion 1 follower-finish request."), new("follower-finish-proposal", [], "Exact hash-bound numeric-private schemaVersion 1 follower-finish proposal."), new("follower-finish-manifest", [], "Exact manifest consumed by post-write verification; it uses a private numeric schemaVersion and declares no public schema identifier.")],
        "npc follower-finish pair-analyze" => [new("follower-finish-pair-request", [], "Exact hash-bound numeric-private schemaVersion 2 or 3 paired request.")],
        "npc follower-finish pair-apply" =>
            [new("follower-finish-pair-request", [], "Exact hash-bound numeric-private schemaVersion 2 or 3 paired request."), new("follower-finish-pair-proposal", [], "Exact hash-bound numeric-private schemaVersion 2 or 3 paired proposal.")],
        "npc follower-finish pair-verify" =>
            [new("follower-finish-pair-request", [], "Exact hash-bound numeric-private schemaVersion 2 or 3 paired request."), new("follower-finish-pair-proposal", [], "Exact hash-bound numeric-private schemaVersion 2 or 3 paired proposal."), new("follower-finish-pair-manifest", [], "Exact paired manifest consumed by verification; it uses private numeric schemaVersion 2 or 3 and declares no public schema identifier.")],
        "npc placement interior analyze" =>
            [new("interior-placement-request", [SkyrimInteriorPlacementRequest.SchemaIdentifier], "Exact hash-bound interior-placement request.")],
        "npc placement interior apply" =>
            [new("interior-placement-request", [SkyrimInteriorPlacementRequest.SchemaIdentifier], "Exact hash-bound interior-placement request."), new("interior-placement-proposal", [SkyrimInteriorPlacementProposal.SchemaIdentifier], "Exact hash-bound interior-placement proposal.")],
        "npc placement interior verify" =>
            [new("interior-placement-manifest", [SkyrimInteriorPlacementManifest.SchemaIdentifier], "Exact separately hash-bound interior-placement manifest.")],
        "preset catalog" =>
            [new("racemenu-preset-directory", [], "Explicit flat K-local RaceMenu JSlot directory and, conditionally, a copied provider closure.")],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> OmittedNpcOutputs(string name) => name switch
    {
        "npc reset" =>
        [
            new AgentArtifactContract("npc-reset-proposal", [], "Fresh proposal when --proposal is present.") { Trigger = PresentTrigger("proposal") },
            new AgentArtifactContract("npc-reset-plugin", [], "Fresh verified output plugin when --apply is true or 1.") { Trigger = ActiveTrueTrigger("apply") }
        ],
        "npc materialize-template" =>
        [
            new AgentArtifactContract("npc-template-proposal", [], "Fresh proposal when --proposal is present.") { Trigger = PresentTrigger("proposal") },
            new AgentArtifactContract("npc-template-plugin", [], "Fresh verified output plugin when --apply is true or 1.") { Trigger = ActiveTrueTrigger("apply") }
        ],
        "npc follower-finish analyze" => [new("follower-finish-proposal", [], "Fresh follower-finish proposal; no formal schema identifier is published.")],
        "npc follower-finish apply" => [new("follower-finish-package-and-archive", [], "Fresh statically verified follower package and archive.")],
        "npc follower-finish pair-analyze" => [new("follower-finish-pair-proposal", [], "Fresh paired proposal; no formal schema identifier is published.")],
        "npc follower-finish pair-apply" => [new("follower-finish-pair-package", [], "Fresh deterministic two-plugin package and archive.")],
        "npc placement interior analyze" => [new("interior-placement-proposal", [SkyrimInteriorPlacementProposal.SchemaIdentifier], "Fresh interior-placement proposal.")],
        "npc placement interior apply" => [new("interior-placement-manifest", [SkyrimInteriorPlacementManifest.SchemaIdentifier], "Fresh optional light-patch package manifest and archive.")],
        _ => []
    };

    private static ImmutableArray<AgentEffectContract> OmittedNpcEffects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace, "After exact command-specific K-local input validation.", "workspace"));
        if (name is "npc reset" or "npc materialize-template")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Only when --proposal is present and the proposal path is fresh.", "k-local-output") { Trigger = PresentTrigger("proposal") });
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Only when --apply is true or 1 and validation succeeds.", "k-local-output") { Trigger = ActiveTrueTrigger("apply") });
        }
        else if (name.EndsWith("analyze", StringComparison.Ordinal) || name.EndsWith("apply", StringComparison.Ordinal))
            effects.Add(new(AgentEffectKind.WriteNewArtifact, "Only after exact hash-bound validation succeeds.", "k-local-output"));
        return effects.ToImmutable();
    }

    private static AgentRetryPolicy OmittedNpcRetry(string name) => name switch
    {
        "npc list" or "npc search" or "npc inspect" or "preset catalog" or
        "npc follower-finish verify" or "npc follower-finish pair-verify" or
        "npc placement interior verify" => AgentRetryPolicy.SafeUnchanged,
        "npc reset" or "npc materialize-template" => AgentRetryPolicy.RequiresReanalysis,
        _ => AgentRetryPolicy.RequiresFreshOutput
    };

    private static AgentDeterminism OmittedNpcDeterminism(string name) =>
        name is "npc list" or "npc search" or "npc inspect" or "preset catalog"
            ? AgentDeterminism.EnvironmentDependent
            : AgentDeterminism.Deterministic;

    private static ImmutableArray<AgentAuthorityContract> OmittedNpcAuthority(string name, bool readOnly)
    {
        bool mixed = name is "npc reset" or "npc materialize-template";
        bool verify = name.EndsWith("verify", StringComparison.Ordinal);
        bool catalog = name == "preset catalog";
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established,
                "The handler validates the exact options and explicit K-local paths before service work."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity,
                catalog || mixed ? AgentAuthorityState.Required : AgentAuthorityState.Established,
                catalog
                    ? "Directory entries are source-hashed, but provider identity exists only when the complete compatibility quartet is supplied."
                    : mixed
                        ? "Proposal-only mode may omit the expected source SHA-256; active apply requires it."
                        : "Explicit copied roots, plugin selections, or hash-bound documents identify the source/provider snapshot."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                readOnly ? AgentAuthorityState.NotApplicable : mixed ? AgentAuthorityState.Required : AgentAuthorityState.Established,
                readOnly ? "This phase writes no artifact." : mixed ? "Materialization is established only in active apply mode; proposal-only mode may remain in memory." : "Fresh output is derived from exact hash-bound inputs."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                verify || name is "npc list" or "npc search" or "npc inspect" or "preset catalog"
                    ? AgentAuthorityState.Established
                    : AgentAuthorityState.Required,
                verify ? "The verify phase independently reopens and checks the bounded static output." : mixed ? "Apply verifies its supported slice, but proposal-only mode writes no final plugin." : readOnly ? "The bounded typed read result is the static inspection." : "A later distinct verify phase remains required."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable,
                "This family emits no off-engine visual preview."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance,
                readOnly && name is "npc list" or "npc search" or "npc inspect" ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Required,
                "Static command output does not establish human visual acceptance."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required,
                "Static parsing, writing, and verification are not game-runtime proof."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval,
                readOnly && name is "npc list" or "npc search" or "npc inspect" or "preset catalog" ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Required,
                "No command in this family grants release promotion approval.")
        ];
    }

    private static ImmutableArray<string> OmittedNpcLimitations(string name) => name switch
    {
        "npc list" or "npc search" => ["List and search share one inventory handler but remain distinct command names, not aliases. Search terms are optional. Explicit --data-root wins over a --plugin-derived root; --plugins wins over the plugin-derived filename. Only the literal true activates --changed-only."],
        "npc inspect" => ["The handler requires --npc. The shared optional --form-id is parsed first, then its filter is overwritten by the parsed --npc value. The two options are not aliases."],
        "npc reset" => ["One mixed proposal/apply command: --current-plugin wins over --plugin and --form-id wins over --npc. Only true or 1 activates --dry-run/--apply; inactive supplied values are not conflicts. Proposal persistence is conditional on --proposal; apply requires --expected-sha256. Conservative mixed-mode authority and retry metadata do not claim one uniform mutation mode."],
        "npc materialize-template" => ["One mixed proposal/apply command: --input-plugin wins over --plugin. Only simultaneously active true or 1 values conflict for --dry-run/--apply. The model admits traits, stats, factions, spell-list, ai-data, ai-packages, model-animation, base-data, inventory, script, default-package-list, attack-data, and keywords, but the current service supports only stats, factions, spell-list, and keywords and fails closed for unsupported inherited categories."],
        "npc follower-finish analyze" or "npc follower-finish apply" or "npc follower-finish verify" => ["Follower phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No follower response schema identifier is published."],
        "npc follower-finish pair-analyze" or "npc follower-finish pair-apply" or "npc follower-finish pair-verify" => ["Paired phases are distinct Skyrim-only hash-bound handlers. Verify consumes a distinct private-numeric --manifest without a separate manifest hash option. No pair response schema identifier is published."],
        "npc placement interior analyze" or "npc placement interior apply" or "npc placement interior verify" => ["Interior phases are distinct Skyrim-only handlers. Analyze/apply bind request/proposal hashes; verify accepts only --manifest/--manifest-sha256 and writes no artifact. The handler response discriminators are npc.interior-placement.proposal.v1, npc.interior-placement.manifest.v1, and npc.interior-placement.verification.v1 respectively; only request/proposal/manifest documents are projected as artifacts, and no registered protocol result schema is claimed."],
        "preset catalog" => ["--data-root, --plugins, --race, and --sex are an all-or-none compatibility quartet. --compatible-only is activated by option presence, not by a boolean value, and requires that quartet. Without it, provider identity is not established and no compatibility claim is made."],
        _ => []
    };

    private static string OmittedNpcResult(string name) => name switch
    {
        "npc list" or "npc search" => "Edition, copied provider inventory, filtered NPC summaries, and diagnostics; no artifact, journal entry, formal result schema, runtime, or promotion claim is produced.",
        "npc inspect" => "Schema-version-1 structured NPC summary, diagnostics, authenticated blank-npc-v1 inheritedDefaults equality list for Skyrim, and the explicit unsupported sections list; the required --npc value overwrites any parsed --form-id filter.",
        "npc reset" => "Applicable/applied state, section, input/output identities and hashes, changes, preserved fields, and diagnostics for proposal-only or active apply mode; no formal result schema identifier is published.",
        "npc materialize-template" => "Applicable/applied state, input/output identities and hashes, per-category evidence, changes, preserved fields, and diagnostics for proposal-only or active apply mode.",
        "npc follower-finish analyze" => "Proposal state/path/hash, allocation and static verdict with diagnostics; no follower response schema identifier or runtime authority is published.",
        "npc follower-finish apply" => "Completed state, package/plugin/archive evidence, static verdict and diagnostics; runtime authority remains false.",
        "npc follower-finish verify" => "Verified state, package/plugin evidence, static verdict and diagnostics from the distinct private-numeric manifest input; the command writes no artifact and runtime authority remains false.",
        "npc follower-finish pair-analyze" => "Succeeded state, verdict, proposal path/hash and diagnostics for the exact paired request; no pair response schema identifier is published.",
        "npc follower-finish pair-apply" => "Succeeded state, verdict, manifest/plugin/ZIP paths and hashes, runtimeAuthority=false, and diagnostics for the reviewed paired transaction.",
        "npc follower-finish pair-verify" => "Succeeded state, verdict, manifest/plugin/ZIP evidence, runtimeAuthority=false, and diagnostics from the distinct private-numeric paired manifest input; the command writes no artifact.",
        "npc placement interior analyze" => "Proposed state, status, proposal path/hash, diagnostics, and response schema discriminator npc.interior-placement.proposal.v1; the fresh proposal is the only output artifact.",
        "npc placement interior apply" => "Applied state, status, output root, archive, manifest path, diagnostics, and response schema discriminator npc.interior-placement.manifest.v1.",
        "npc placement interior verify" => "Verified state, status, typed verification object, diagnostics, and response schema discriminator npc.interior-placement.verification.v1; no persistent verification artifact is written and no registered protocol result schema is claimed.",
        "preset catalog" => "Accepted state, directory/filter, presence-active compatible-only state, conditional target authority, admitted hashed entries, omitted count and diagnostics; no formal result schema is published.",
        _ => throw new InvalidOperationException($"Unknown omitted-NPC command: {name}")
    };
}
