using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsReferenceHairFinishCommand(string name) =>
        name is "preset design-propose" or "preset create-from-reference" or
            "npc create-from-reference" or "facegen hair-regions analyze" or
            "facegen hair-regions propose" or "facegen hair-regions preview" or
            "facegen hair-regions apply" or "facegen hair-regions verify";

    private static AgentCommandContract ReferenceHairFinishContract(
        CommandDescriptor descriptor) => descriptor.Name switch
        {
            "preset design-propose" => ReferenceDesign(descriptor),
            "preset create-from-reference" => ReferencePreset(descriptor),
            "npc create-from-reference" => ReferenceNpc(descriptor),
            "facegen hair-regions analyze" => HairAnalyze(descriptor),
            "facegen hair-regions propose" => HairPropose(descriptor),
            "facegen hair-regions preview" => HairPreview(descriptor),
            "facegen hair-regions apply" => HairApply(descriptor),
            "facegen hair-regions verify" => HairVerify(descriptor),
            _ => throw new InvalidOperationException(
                $"Unknown reference/hair/Finish command '{descriptor.Name}'.")
        };

    private static AgentCommandContract ReferenceDesign(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [new("reference-preset-intake", ["reference-authoring.intake.v1"], "Hash-bound intake session selected by --intake outside template mode.")
            { Trigger = NpcAllAbsent("template-output") }];
        return CompleteFamily(descriptor, inputs,
            [
                new("reference-intake-template", ["reference-authoring.intake.v1"], "Canonical intake session with placeholders at --template-output. Fill them before normal schema and typed validation.")
                    { Trigger = NpcAnyPresent("template-output") },
                new("reference-design-proposal", ["reference-authoring.inference-proposal.v1"], "Fresh landmark-proposal.json session under --output.")
                    { Trigger = NpcAllAbsent("template-output") }
            ],
            [new(AgentEffectKind.ReadWorkspace, "Normal mode reads the admitted intake; exclusive template mode requires no intake or inference runtime.", "workspace"),
                new(AgentEffectKind.WriteNewArtifact, "Writes a fresh canonical template file or normal design proposal transaction; existing destinations are refused.", "k-local-output"),
                JournalEffect()],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
            ReferenceAuthority(writes: true, staticVerified: false)) with
        {
            OptionRelationships =
            [
                RequiredWhen(["intake", "intake-sha256", "output"], ["template-output"],
                    "The complete intake/hash/output group is required outside exclusive template mode.", NpcAllAbsent("template-output")),
                ForbiddenWhen(["template-output"], ["intake", "intake-sha256", "output"],
                    "Template mode cannot be mixed with normal inference inputs or output.", NpcAnyPresent("intake", "intake-sha256", "output"))
            ]
        };
    }

    private static AgentCommandContract ReferencePreset(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [
            new("reference-inference-proposal", ["reference-authoring.inference-proposal.v1"], "Exact hash-bound inference proposal selected by --proposal."),
            ReferenceIntakeFromProposal(),
            new("reference-reviewed-design", ["reference-authoring.reviewed-design.v1"], "Exact hash-bound human review selected by --review."),
            new("reference-resource-snapshot", ["reference-authoring.resource-snapshot.v1"], "Exact hash-bound reviewed resource snapshot selected by --resource.")
        ];
        return CompleteFamily(descriptor, inputs,
            [
                new("reference-preset-evidence-root", [], "Fresh proposal/evidence transaction root committed before the handler compares --jslot-output."),
                new("reference-authoring-proposal", ["reference-authoring.authoring-proposal.v1"], "Canonical authoring-proposal.json session within the evidence transaction."),
                new AgentArtifactContract("verified-reference-preset", [], "Apply-only verified RaceMenu JSlot at the transaction's derived target filename under --evidence-root; --jslot-output does not direct this write.")
                {
                    Trigger = TruthyOnly("apply", "true")
                }
            ],
            [
                new(AgentEffectKind.ReadWorkspace, "When admitting the exact proposal-derived intake, proposal, review, and resource documents.", "workspace"),
                new(AgentEffectKind.WriteNewArtifact, "Atomically promotes the fresh --evidence-root transaction, including the derived target-named JSlot only for apply=true; the --jslot-output parse/equality check is post-promotion and can still fail.", "k-local-output"),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
            ReferenceAuthority(writes: true, staticVerified: false)) with
        {
            OptionRelationships =
            [
                new AgentOptionRelationshipContract(
                    AgentOptionRelationshipKind.RequiredWhen,
                    ["accepted-proposal-sha256"], ["apply"],
                    "--accepted-proposal-sha256 is required when --apply is true.")
                {
                    Trigger = TruthyOnly("apply", "true")
                },
                new AgentOptionRelationshipContract(
                    AgentOptionRelationshipKind.ForbiddenWhen,
                    ["accepted-proposal-sha256", "apply"],
                    ["accepted-proposal-sha256", "apply"],
                    "--accepted-proposal-sha256 is forbidden when --apply is absent or false.")
                {
                    Trigger = PresentAndNotTruthy(
                        "accepted-proposal-sha256", "apply", "true")
                }
            ]
        };
    }

    private static AgentCommandContract ReferenceNpc(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [
            new("reference-inference-proposal", ["reference-authoring.inference-proposal.v1"], "Exact hash-bound inference proposal selected by --proposal."),
            ReferenceIntakeFromProposal(),
            new("reference-reviewed-design", ["reference-authoring.reviewed-design.v1"], "Exact hash-bound human review selected by --review."),
            new("reference-resource-snapshot", ["reference-authoring.resource-snapshot.v1"], "Exact hash-bound reviewed resource snapshot selected by --resource."),
            new("npc-create-from-jslot-request", [RaceMenuNpcExecutionRequestSchemas.Request], "Exact hash-bound reviewed NPC execution request selected by --request.")
        ];
        return CompleteFamily(descriptor, inputs,
            [
                new("verified-reference-preset", [], "Fresh verified RaceMenu JSlot emitted inside the transaction root."),
                new("reference-authoring-proposal", ["reference-authoring.authoring-proposal.v1"], "Canonical authoring-proposal.json session within the preset transaction."),
                new("verified-reference-npc-handoff", [], "Returned typed static handoff. The published verified-npc-handoff session schema describes explicit persistence; this command does not write that envelope file.")
            ],
            WriteEffects("the exact reviewed reference and NPC request inputs", "the fresh preset and NPC transaction artifacts"),
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
            ReferenceAuthority(writes: true, staticVerified: true));
    }

    private static AgentCommandContract HairAnalyze(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [new("facegeom-nif", [], "Exact hash-bound existing FaceGeom NIF selected by --source.")];
        return CompleteFamily(descriptor, inputs,
            [
                new("facegeom-hair-regions-analysis", [FaceGeomHairRegionSchemas.Analysis], "Fresh canonical analysis document."),
                new("facegeom-hair-regions-request", [FaceGeomHairRegionSchemas.Request], "Fresh Preserve-default assignment template.")
            ],
            WriteEffects("the exact source NIF", "fresh analysis and assignment-template documents"),
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
            HairAuthority(writes: true, staticVerified: false));
    }

    private static AgentCommandContract HairPropose(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [
            new("facegeom-hair-regions-analysis", [FaceGeomHairRegionSchemas.Analysis], "Exact hash-bound analysis selected by --analysis."),
            new("facegeom-hair-regions-request", [FaceGeomHairRegionSchemas.Request], "Exact hash-bound request selected by --request.")
        ];
        return CompleteFamily(descriptor, inputs,
            [new("facegeom-hair-regions-proposal", [FaceGeomHairRegionSchemas.Proposal], "Fresh canonical fixed-width proposal.")],
            WriteEffects("the exact analysis and request", "the fresh proposal"),
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
            HairAuthority(writes: true, staticVerified: false));
    }

    private static AgentCommandContract HairPreview(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [
            new("facegeom-hair-regions-request", [FaceGeomHairRegionSchemas.Request], "Exact hash-bound request selected by --request."),
            new("facegeom-hair-regions-proposal", [FaceGeomHairRegionSchemas.Proposal], "Exact hash-bound proposal selected by --proposal."),
            new(WorkflowArtifactKinds.ReviewedWorkspaceIntake, [], "Reviewed intake selected by --intake; no grounded schema identifier is published.")
        ];
        return CompleteFamily(descriptor, inputs,
            [
                new("facegeom-hair-regions-preview-bundle", [], "Fresh atomically published preview bundle; no schema identifies the directory as a whole."),
                new("facegeom-hair-regions-preview-evidence", [FaceGeomHairRegionSchemas.PreviewEvidence], "Canonical evidence document inside the published preview bundle.")
            ],
            [
                new(AgentEffectKind.ReadWorkspace, "When admitting exact request, proposal, intake, providers, and renderer inputs.", "workspace"),
                new(AgentEffectKind.InvokeAdmittedProcess, "When invoking the packaged pinned off-engine renderer.", "workspace"),
                new(AgentEffectKind.WriteNewArtifact, "Only after the complete private preview bundle verifies and publishes atomically.", "k-local-output"),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
            HairPreviewAuthority());
    }

    private static AgentCommandContract HairApply(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = HairTransactionInputs();
        return CompleteFamily(descriptor, inputs,
            [
                new("facegeom-nif", [], "Fresh fixed-width HairTint output NIF selected by --output."),
                new("facegeom-hair-regions-manifest", [FaceGeomHairRegionSchemas.Manifest], "Fresh canonical apply manifest selected by --manifest.")
            ],
            WriteEffects("the exact request and proposal plus source NIF", "the fresh output NIF and manifest"),
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.Deterministic,
            HairAuthority(writes: true, staticVerified: true));
    }

    private static AgentCommandContract HairVerify(CommandDescriptor descriptor) =>
        CompleteFamily(descriptor,
            [
                .. HairTransactionInputs(),
                new("facegeom-nif", [], "Applied output NIF selected by --output."),
                new("facegeom-hair-regions-manifest", [FaceGeomHairRegionSchemas.Manifest], "Canonical apply manifest selected by --manifest.")
            ],
            [], ReadEffects("the request, proposal, applied NIF, and manifest"),
            AgentRetryPolicy.SafeUnchanged, AgentDeterminism.Deterministic,
            HairVerifyAuthority());

    private static AgentCommandContract FinishAnalyze(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [new(WorkflowArtifactKinds.NpcFinishCoreRequest,
            [
                SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
                SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier
            ], "Exact hash-bound Finish Core request.")];
        AgentOptionRelationshipTrigger nonValidation =
            NotTruthy("validate-all", "true", "1");
        return CompleteFamily(descriptor, inputs,
            [new AgentArtifactContract(WorkflowArtifactKinds.NpcFinishCoreProposal,
                [
                    SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                    SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
                    SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier
                ], "Fresh Finish Core proposal outside validation mode.")
            {
                Trigger = nonValidation
            }],
            [
                new(AgentEffectKind.ReadWorkspace, "When admitting the exact request and optional paired install context.", "workspace"),
                new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                    "Only outside --validate-all true or 1 after request analysis succeeds.",
                    "k-local-output") { Trigger = nonValidation },
                new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                    "Only in --validate-all true or 1 mode for disposable validation output; nothing is promoted.",
                    "k-local-output") { Trigger = TruthyOnly("validate-all", "true", "1") },
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput, AgentDeterminism.PinnedInputsAndTools,
            FinishAuthority(staticVerified: false, writes: true)) with
        {
            OptionRelationships = [RequiresTogether("data-root", "plugins")]
        };
    }

    private static AgentCommandContract FinishApplyV1(CommandDescriptor descriptor)
    {
        AgentCommandContract contract = FinishApplyLegacy(descriptor);
        AgentOptionRelationshipTrigger nonValidation =
            NotTruthy("validate-all", "true", "1");
        AgentOptionRelationshipTrigger validation =
            TruthyOnly("validate-all", "true", "1");
        return contract with
        {
            OutputArtifacts = contract.OutputArtifacts.Select(artifact => artifact with
            {
                Trigger = nonValidation
            }).ToImmutableArray(),
            Effects = contract.Effects.SelectMany(effect =>
                effect.Kind == AgentEffectKind.WriteNewArtifact
                    ? new[]
                    {
                        effect with
                        {
                            Condition = "Only outside --validate-all true or 1 after all reviewed bindings pass.",
                            Trigger = nonValidation
                        },
                        effect with
                        {
                            Condition = "Only in --validate-all true or 1 mode for disposable validation output; nothing is promoted.",
                            Trigger = validation
                        }
                    }
                    : [effect]).ToImmutableArray()
        };
    }

    private static AgentCommandContract FinishVerifyV1(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs =
        [new(WorkflowArtifactKinds.NpcFinishCoreManifest,
            [
                SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier
            ], "Exact hash-bound published Finish Core manifest.")];
        return CompleteFamily(descriptor, inputs, [],
            ReadEffects("the manifest, package, archive, and optional paired install context"),
            AgentRetryPolicy.SafeUnchanged, AgentDeterminism.PinnedInputsAndTools,
            FinishAuthority(staticVerified: true, writes: false)) with
        {
            OptionRelationships = [RequiresTogether("data-root", "plugins")]
        };
    }

    private static AgentCommandContract CompleteFamily(
        CommandDescriptor descriptor,
        ImmutableArray<AgentArtifactContract> inputs,
        ImmutableArray<AgentArtifactContract> outputs,
        ImmutableArray<AgentEffectContract> effects,
        AgentRetryPolicy retry,
        AgentDeterminism determinism,
        ImmutableArray<AgentAuthorityContract> authority) =>
        Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = [GameEdition.SkyrimSpecialEdition],
            Options = LegacyOptions(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = outputs,
            ResultShape = "object",
            ResultDescription = ResultDescriptionForFamily(descriptor.Name),
            Effects = effects,
            RetryPolicy = retry,
            Determinism = determinism,
            SupportsDryRun = false,
            Authority = authority
        };

    private static ImmutableArray<AgentArtifactContract> HairTransactionInputs() =>
    [
        new("facegeom-hair-regions-request", [FaceGeomHairRegionSchemas.Request], "Exact hash-bound request selected by --request."),
        new("facegeom-hair-regions-proposal", [FaceGeomHairRegionSchemas.Proposal], "Exact hash-bound proposal selected by --proposal."),
        new("facegeom-source-nif", [], "Exact source NIF bound by the request and proposal; no schema identifier applies to the binary file.")
    ];

    private static ImmutableArray<AgentEffectContract> WriteEffects(
        string read, string write) =>
    [
        new(AgentEffectKind.ReadWorkspace, $"When admitting {read}.", "workspace"),
        new(AgentEffectKind.WriteNewArtifact, $"Only after validation succeeds for {write}.", "k-local-output"),
        JournalEffect()
    ];

    private static ImmutableArray<AgentEffectContract> ReadEffects(string read) =>
    [
        new(AgentEffectKind.ReadWorkspace, $"When admitting {read}.", "workspace"),
        JournalEffect()
    ];

    private static string ResultDescriptionForFamily(string name) => name switch
    {
        "preset design-propose" or "npc create-from-reference" =>
            "Reference-authoring completion/status, exact hashes or handoff fields when available, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published.",
        "preset create-from-reference" =>
            "Reference-authoring status and hashes. The transaction first promotes fresh evidence under --evidence-root; accepted apply derives the JSlot path from the reviewed target name. --jslot-output is only parsed and compared with that returned path after promotion, so a mismatch response can follow committed evidence artifacts. Runtime authority remains false; no grounded result schema identifier is published.",
        _ when name.StartsWith("facegen hair-regions", StringComparison.Ordinal) =>
            "Phase verdict, observed or produced artifact evidence, diagnostics, visualAuthority false, and runtimeAuthority false; preview also returns off-engine preview evidence. No grounded result schema identifier is published.",
        "npc finish analyze" =>
            "Proposal or validation status and diagnostics; validation mode emits phase diagnostics without promotion. No grounded result schema identifier is published.",
        "npc finish verify" =>
            "Static verification status, verification evidence, and diagnostics; no grounded result schema identifier is published.",
        _ => "Typed command outcome and diagnostics; no grounded result schema identifier is published."
    };

    private static AgentArtifactContract ReferenceIntakeFromProposal() =>
        new("reference-preset-intake", ["reference-authoring.intake.v1"],
            "Hash-bound sibling authoring-intake.json derived from --proposal and reopened with the proposal's canonical intake SHA-256.");

    private static AgentOptionRelationshipTrigger TruthyOnly(
        string option, params string[] values) =>
        new(AgentPredicateCombination.All,
            [new(AgentPredicateSource.OptionValue, option,
                AgentPredicateMatch.AnyOf, values.ToImmutableArray(), false)]);

    private static AgentOptionRelationshipTrigger NotTruthy(
        string option, params string[] values) =>
        new(AgentPredicateCombination.All,
            [new(AgentPredicateSource.OptionValue, option,
                AgentPredicateMatch.NoneOf, values.ToImmutableArray(), true)]);

    private static AgentOptionRelationshipTrigger PresentAndNotTruthy(
        string presentOption, string booleanOption, params string[] trueValues) =>
        new(AgentPredicateCombination.All,
        [
            new(AgentPredicateSource.OptionValue, presentOption,
                AgentPredicateMatch.NoneOf, ["__absent__"], false),
            new(AgentPredicateSource.OptionValue, booleanOption,
                AgentPredicateMatch.NoneOf, trueValues.ToImmutableArray(), true)
        ]);

    private static ImmutableArray<AgentAuthorityContract> ReferenceAuthority(
        bool writes, bool staticVerified) =>
    [
        AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Exact paths, hashes, review bindings, and command options are admitted before execution."),
        AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "Reviewed reference resources bind their source/provider identity."),
        AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, writes ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, "Fresh outputs are bound to admitted reviewed inputs and pinned tools."),
        AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, staticVerified ? AgentAuthorityState.Established : AgentAuthorityState.Required, staticVerified ? "The NPC transaction reopens and hash-verifies the static preset and handoff evidence it reports." : "Proposal output and conditional preset materialization do not establish an unconditional independent verification claim."),
        AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "These reference commands do not establish off-engine preview authority."),
        AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static reference output does not establish human likeness acceptance."),
        AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Static output does not establish game-runtime behavior or appearance."),
        AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Command success never grants promotion approval.")
    ];

    private static ImmutableArray<AgentAuthorityContract> HairAuthority(
        bool writes, bool staticVerified) =>
    [
        AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Exact hash-bound documents and K-local paths are admitted."),
        AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "The analysis/request/proposal chain binds the exact source NIF."),
        AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, writes ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, "Canonical documents or fixed-width NIF output are deterministic for admitted inputs."),
        AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, staticVerified ? AgentAuthorityState.Established : AgentAuthorityState.Required, staticVerified ? "The exact output and manifest are verified against the authorized fixed-width transaction." : "Analysis/proposal output does not independently verify a game artifact."),
        AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "This phase does not render an off-engine preview."),
        AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static hair-region evidence does not establish human visual acceptance."),
        AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Static hair-region evidence does not establish Skyrim runtime appearance."),
        AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Command success never grants promotion approval.")
    ];

    private static ImmutableArray<AgentAuthorityContract> HairPreviewAuthority() =>
    [
        AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Exact request, proposal, reviewed intake, providers, and renderer inputs are admitted."),
        AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "The preview evidence binds materialized provider and renderer identities."),
        AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, AgentAuthorityState.Established, "The complete bundle is bound to exact inputs and pinned tools."),
        AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, AgentAuthorityState.Established, "The private bundle is verified before atomic publication."),
        AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.Established, "The command establishes only off-engine preview evidence."),
        AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Off-engine evidence does not establish human visual acceptance."),
        AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Off-engine evidence does not establish Skyrim runtime appearance."),
        AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Preview publication never grants promotion approval.")
    ];

    private static ImmutableArray<AgentAuthorityContract> HairVerifyAuthority() =>
        HairAuthority(writes: false, staticVerified: true);

    private static ImmutableArray<AgentAuthorityContract> FinishAuthority(
        bool staticVerified, bool writes) =>
    [
        AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Exact hash-bound Finish documents and optional paired install context are admitted."),
        AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "Finish documents bind the source/provider identity used by the transaction."),
        AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, writes ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, writes ? "Fresh Finish output is bound to admitted inputs and pinned tools." : "Read-only verification materializes no persistent game artifact."),
        AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, staticVerified ? AgentAuthorityState.Established : AgentAuthorityState.Required, staticVerified ? "The package, archive, and manifest are independently reopened and verified." : "Analyze/apply output still requires the independent verify phase."),
        AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "Finish Core does not render an off-engine preview."),
        AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static Finish evidence does not establish human visual acceptance."),
        AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Static Finish evidence does not establish game-runtime behavior."),
        AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Finish Core success never grants promotion approval.")
    ];
}
