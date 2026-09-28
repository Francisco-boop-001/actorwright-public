using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsNpcCreationCommand(string name) =>
        NpcCreationOptionCatalog.Contains(name) &&
        name != "npc create-from-jslot";

    private static AgentCommandContract NpcCreationContract(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = NpcCreationInputs(descriptor.Name);
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = [GameEdition.SkyrimSpecialEdition],
            Limitations = descriptor.Limitations.AddRange(NpcCreationLimitations(descriptor.Name)),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = NpcCreationRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = NpcCreationOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = NpcCreationResultDescription(descriptor.Name),
            Effects = NpcCreationEffects(descriptor.Name),
            RetryPolicy = AgentRetryPolicy.RequiresFreshOutput,
            Determinism = AgentDeterminism.PinnedInputsAndTools,
            SupportsDryRun = false,
            Authority = NpcCreationAuthority(descriptor.Name),
            Transitions = NpcCreationTransitions(descriptor.Name)
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract> NpcCreationRelationships(string name)
    {
        if (name == "npc create") return [];
        if (name != "npc create-from-jslot") return [];
        return
        [
            RequiredWhen(["preflight-output"], ["face-bake-authority-output"],
                "Face-bake authority emission is exclusive to preflight authoring mode.", Present("face-bake-authority-output")),
            RequiredWhen(["preset", "preset-sha256", "data-root", "plugins", "companion-root"],
                ["provider-migration-output", "reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256"],
                "Ordinary preflight/build mode requires the complete preset, copied-Data, plugin-order, and companion binding when no provider-migration selector is present.",
                NpcAllAbsent("provider-migration-output", "reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256")),
            new(AgentOptionRelationshipKind.RequiresTogether,
                ["reviewed-preflight", "reviewed-preflight-sha256"], [],
                "Reviewed-preflight path and hash must be supplied together."),
            ForbiddenWhen(["preflight-output"],
                ["reviewed-preflight", "reviewed-preflight-sha256"],
                "Preflight output and reviewed-preflight input are mutually exclusive.",
                NpcAnyPresent("reviewed-preflight", "reviewed-preflight-sha256")),
            new(AgentOptionRelationshipKind.RequiresTogether,
                ["reviewed-provider-migration", "reviewed-provider-migration-sha256"], [],
                "Canonical reviewed provider-migration path and hash must be supplied together."),
            new(AgentOptionRelationshipKind.RequiresTogether,
                ["provider-migration", "provider-migration-sha256"], [],
                "Legacy reviewed provider-migration path and hash must be supplied together."),
            RequiredWhen(["migrated-request-root"],
                ["provider-migration-output", "reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256"],
                "Every provider-migration mode requires --migrated-request-root.",
                NpcAnyPresent("provider-migration-output", "reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256")),
            ForbiddenWhen(["preset", "preset-sha256", "data-root", "plugins", "companion-root", "preflight-output", "face-bake-authority-output", "reviewed-preflight", "reviewed-preflight-sha256"],
                ["provider-migration-output", "reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256"],
                "Every provider-migration selector forbids ordinary preflight/build options.",
                NpcAnyPresent("provider-migration-output", "reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256")),
            ForbiddenWhen(["provider-migration-output"],
                ["reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256"],
                "Review generation and reviewed acceptance cannot be mixed.",
                NpcAnyPresent("reviewed-provider-migration", "reviewed-provider-migration-sha256", "provider-migration", "provider-migration-sha256")),
            ForbiddenWhen(["reviewed-provider-migration", "reviewed-provider-migration-sha256"],
                ["provider-migration", "provider-migration-sha256"],
                "Canonical and legacy provider-migration pairs cannot be mixed.",
                NpcAnyPresent("provider-migration", "provider-migration-sha256"))
        ];
    }

    private static ImmutableArray<AgentArtifactContract> NpcCreationInputs(string name) => name switch
    {
        "npc create" =>
        [
            new("blank-npc-provider-manifest", [], "Exact hash-bound provider manifest."),
            new("template-plugin", [], "Exact hash-bound template plugin and NPC carrier."),
            new("facegeom-carrier", [], "Exact hash-bound qualified carrier NIF."),
            new("facetint-layer-manifest", [], "Qualified FaceTint manifest and provider root."),
            new("dependency-manifest", [], "Exact dependency closure manifest.")
        ],
        "npc create-from-preset" =>
        [new("npc-creation-request", [RaceMenuNpcExecutionRequestSchemas.Request], "Exact hash-bound schemaVersion 1, 2, or 3 request using the shared execution schema. Schema2 existingNpcTarget may bind wholeSkinAuthority for schema7 body meshes; build fields remain in the request document.")],
        "npc create-from-jslot" =>
        [
            new("npc-creation-request", [RaceMenuNpcExecutionRequestSchemas.Request], "Exact hash-bound npc.create-from-jslot.request.v1 document; its loader admits declared schemaVersion 1, 2, or 3 request variants shared by ordinary and migration modes."),
            new AgentArtifactContract("racemenu-jslot", [], "Exact hash-bound preset in ordinary mode.") { Trigger = Present("preset") },
            new AgentArtifactContract("copied-data-root", [], "Copied Data root and supplied V1 plugin order in ordinary mode.") { Trigger = Present("data-root") },
            new AgentArtifactContract("npc-build-preflight", [NpcBuildPreflightSchemas.Artifact], "Optional exact reviewed preflight path/hash pair.") { Trigger = Present("reviewed-preflight") },
            new AgentArtifactContract("provider-migration-review", [ProviderMigrationSchemas.Review], "Reviewed provider-migration document selected through the canonical pair or legacy aliases.") { Trigger = NpcAnyPresent("reviewed-provider-migration", "provider-migration") }
        ],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> NpcCreationOutputs(string name) => name switch
    {
        "npc create" => PackageOutputs(),
        "npc create-from-preset" => [.. PackageOutputs(), new("bodygen-configuration", [], "Conditional BodyGen INI files reported when request-document policy enables them.")],
        "npc create-from-jslot" =>
        [
            new AgentArtifactContract("npc-build-preflight", [NpcBuildPreflightSchemas.Artifact], "Fresh canonical preflight document.") { Trigger = Present("preflight-output") },
            new AgentArtifactContract("skyrim-face-bake-authority", ["skyrim-face-bake-authority/1"], "Fresh loader-validated wire-version2 authority; bind its path/hash explicitly before ordinary preflight/build.") { Trigger = Present("face-bake-authority-output") },
            new AgentArtifactContract("provider-migration-review", [ProviderMigrationSchemas.Review], "Fresh review document for the proposed provider migration.") { Trigger = Present("provider-migration-output") },
            new AgentArtifactContract("provider-migration-receipt", [ProviderMigrationSchemas.Receipt], "Receipt plus migrated request/bundle files after reviewed migration acceptance.") { Trigger = NpcAnyPresent("reviewed-provider-migration", "provider-migration") },
            new AgentArtifactContract("racemenu-companion-assets", [], "Manager-owned preset, FaceGeom, and FaceTint companion outputs for an ordinary build.") { Trigger = NpcPresentAndAbsent("preset", "preflight-output") },
            new AgentArtifactContract("npc-package-manifest", [], "Independently reopened static NPC package manifest for an ordinary build.") { Trigger = NpcPresentAndAbsent("preset", "preflight-output") }
        ],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> PackageOutputs() =>
    [
        new("npc-plugin", [], "Fresh output plugin."),
        new("facegeom-nif", [], "Fresh package FaceGeom NIF."),
        new("facetint-dds", [], "Fresh package FaceTint DDS."),
        new("npc-package-manifest", [], "Fresh package manifest independently reopened by the transaction.")
    ];

    private static ImmutableArray<AgentEffectContract> NpcCreationEffects(string name)
    {
        if (name != "npc create-from-jslot")
            return WriteEffects("the exact hash-bound request/provider inputs", "the fresh static NPC package");
        return
        [
            new(AgentEffectKind.ReadWorkspace, "When admitting the selected ordinary or provider-migration inputs.", "workspace"),
            new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only in preflight mode after request, preset, provider, and output admission.", "k-local-output") { Trigger = Present("preflight-output") },
            new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only in provider-migration review mode after the recognized legacy request is admitted.", "k-local-output") { Trigger = Present("provider-migration-output") },
            new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only after a reviewed provider migration is hash-verified and accepted.", "k-local-output") { Trigger = NpcAnyPresent("reviewed-provider-migration", "provider-migration") },
            new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only in ordinary build mode after optional reviewed-preflight admission and independent package verification.", "k-local-output") { Trigger = NpcPresentAndAbsent("preset", "preflight-output") },
            JournalEffect()
        ];
    }

    private static ImmutableArray<AgentAuthorityContract> NpcCreationAuthority(string name) =>
    [
        AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Exact command inputs and available hashes are admitted before the selected V1 mode executes."),
        AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "Provider/template or request-document authority is resolved and hash-bound."),
        AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, AgentAuthorityState.Established, "Fresh static outputs are derived from admitted inputs and pinned tools."),
        AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, name == "npc create-from-jslot" ? AgentAuthorityState.Required : AgentAuthorityState.Established, name == "npc create-from-jslot" ? "Ordinary successful packages are independently reopened, but preflight and provider-migration modes do not establish package verification for the command as a whole." : "Successful package paths and declared files are independently reopened."),
        AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "NPC creation does not render an off-engine preview."),
        AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static creation does not establish human visual acceptance."),
        AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "All successful creation results retain runtimeAuthority false and require in-game proof."),
        AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Creation, preflight, and migration success never grant promotion approval.")
    ];

    private static ImmutableArray<AgentTransitionContract> NpcCreationTransitions(string name) => name switch
    {
        "npc create-from-jslot" =>
        [
            new(AgentWorkflowPhase.Analyze, AgentWorkflowPhase.Apply, ["npc-build-preflight"]),
            new(AgentWorkflowPhase.Propose, AgentWorkflowPhase.Review, ["provider-migration-review"]),
            new(AgentWorkflowPhase.Review, AgentWorkflowPhase.Apply, ["provider-migration-review"]),
            new(AgentWorkflowPhase.Apply, AgentWorkflowPhase.Verify, ["npc-package-manifest"])
        ],
        _ => [new(AgentWorkflowPhase.Apply, AgentWorkflowPhase.Verify, ["npc-package-manifest"])]
    };

    private static string NpcCreationResultDescription(string name) => name switch
    {
        "npc create" => "Completion, static-pass-runtime-required verdict, NPC/plugin/FaceGeom/FaceTint/package paths and hashes, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published.",
        "npc create-from-preset" => "Completion, new/existing target mode, static package paths/hashes, optional BodyGen files, VMAD property count, runtimeAuthority false, and diagnostics; request-document build fields are not CLI fields and no grounded result schema identifier is published.",
        _ => "Selected V1 mode result: preflight readiness/document evidence, provider-migration review or migrated outputs, or ordinary companion/static package evidence; every game-facing build retains runtimeAuthority false and no grounded result schema identifier is published."
    };

    private static AgentOptionRelationshipContract RequiredWhen(ImmutableArray<string> options, ImmutableArray<string> references, string condition, AgentOptionRelationshipTrigger trigger) =>
        new(AgentOptionRelationshipKind.RequiredWhen, options, references, condition) { Trigger = trigger };
    private static AgentOptionRelationshipContract ForbiddenWhen(ImmutableArray<string> options, ImmutableArray<string> references, string condition, AgentOptionRelationshipTrigger trigger) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, options, references, condition) { Trigger = trigger };
    private static AgentOptionRelationshipTrigger NpcAllAbsent(params string[] options) =>
        new(AgentPredicateCombination.All, options.Select(option => new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf, ["__absent__"], true)).ToImmutableArray());
    private static AgentOptionRelationshipTrigger NpcAnyPresent(params string[] options) =>
        new(AgentPredicateCombination.Any, options.Select(option => PresentPredicate(option)).ToImmutableArray());
    private static AgentOptionRelationshipTrigger NpcPresentAndAbsent(string present, string absent) =>
        new(AgentPredicateCombination.All,
        [
            PresentPredicate(present),
            new(AgentPredicateSource.OptionValue, absent, AgentPredicateMatch.AnyOf, ["__absent__"], true)
        ]);
    private static AgentRelationshipPredicate PresentPredicate(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf, ["__absent__"], false);

    private static ImmutableArray<string> NpcCreationLimitations(string name) => name switch
    {
        "npc create" =>
        [
            "V1 ignores unknown options and resolves duplicate options by last value; discovery does not tighten that parser behavior.",
            "The command is Skyrim-only, defaults to skyrimse when edition/game is omitted, and fixes role=static-validation and sex=female."
        ],
        "npc create-from-preset" =>
        [
            "Only request path/hash are CLI grammar; schemaVersion 1, 2, or 3 build fields remain inside the closed request document.",
            "A leading @ on --request is optional. The shared execution request schema is exposed by schema export; existing-target body mesh replacement requires wholeSkinAuthority."
        ],
        _ =>
        [
            "This is the permissive V1 union; ordinary mode ignores unknowns, keeps last duplicate values, accepts lowercase SHA-256 spelling, and does not require distinct plugins.",
            "Provider-migration mode accepts only its exact selected arm, forbids canonical/legacy mixing, and retains its narrower duplicate checks. Protocol 2 remains a separate strict contract."
        ]
    };
}
