using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private const string ObjectCombinationDialect =
        "When --properties is absent, --combinations is a no-comments/no-trailing-commas, maximum-depth-16, duplicate-free case-sensitive inline or @file JSON object; @files must be lexically beneath the configured K root, exist, have a non-reparse leaf, and be at most 262144 bytes. Exact root fields are mode,editorId,targetFormId,items, where mode is new|override and items contains 1..4096 objects with only displayName,isDefault,isEditorOnly,parentCombinationIndex,levelMin,levelMax,minLevelForRanks,altLevelsPerTier,keywords. --includes has the same file/JSON bounds and is an array containing only {combinationIndex,mod,attachPointIndex,optional,dontUseAll}. Byte fields are 0..255; parentCombinationIndex is a nonnegative Int16 referring to an item; combinationIndex is a nonnegative Int32 referring to an item. New requires editorId and a nonzero plugin-local 24-bit targetFormId; override inherits EditorID and any targetFormId must equal source. Names are at most 4096 characters without controls; per-combination keywords/includes are at most 4096 unique non-null source/master references; levelMin may not exceed levelMax; at least one supported field must change.";

    private const string ObjectPropertyDialect =
        "--properties presence wins over --combinations/--includes and selects a no-comments/no-trailing-commas, maximum-depth-12, duplicate-free case-sensitive inline or @file JSON array; @files must be lexically beneath the configured K root, exist, have a non-reparse leaf, and be at most 262144 bytes. It contains 1..4096 objects with only valueType,functionType,propertyIndex,value1Integer,value1Float,value1FormId,value2Integer,value2Float,stepValue,combinationIndex. valueType is IntType|FloatType|BoolType|StringType|FormIDInt|EnumType|FormIDFloat; functionType is byte, propertyIndex is UInt16, integer values are Int32, combinationIndex is 0..4095, and floating values are finite. At proposal time FormID types require a parsed source/master value1FormId and admit Plugin|0 when Plugin is the source or a master; the writer later refuses a zero FormID. Other types forbid value1FormId.";

    private const string ObjectProposePathAdmissionLimitation =
        "Object-template propose @file checks establish lexical containment beneath the configured K root and reject a reparse-point leaf, but do not independently refuse or pin a junction or symbolic link in the parent chain; ordinary-path input admission therefore remains outstanding.";

    private const string ObjectWritePathAdmissionLimitation =
        "Object-template write walks existing proposal, properties, and source path ancestry and refuses observed reparses, and evaluates output-parent ancestry. Those paths are not handle-pinned against a later swap, and the proposal/property documents are not admitted by an independent expected hash, so input admission remains outstanding.";

    private const string ObjectProposeInputAdmissionReason =
        "Parent-chain junctions or symbolic links are not independently refused or pinned by object-template propose @file checks, so ordinary-path input admission remains required.";

    private const string ObjectWriteInputAdmissionReason =
        "Observed reparses in existing proposal, properties, and source ancestry are refused, but the paths are not handle-pinned against a later swap and proposal/property documents lack independent expected-hash admission.";

    private const string AppearanceExpansion =
        "Sections are a nonempty comma-separated case-insensitive list. all expands for Fallout 4 to body-weight,body-regions,body-sliders,overlays,lm-skin-template,face-parts,hair-color,face-tints,face-morphs,face-bone-regions; for Skyrim SE it expands to body-weight,body-sliders,overlays,face-parts,hair-color,face-tints,face-morphs,sculpt. skin-override, outfit, and chargen-flag require an NPC record carrier and are refused by preset-file copying; game-incompatible sections fail visibly and duplicates are refused by the service.";

    private static bool IsObjectPresetCommand(string name) =>
        ObjectPresetOptionCatalog.Contains(name);

    private static AgentCommandContract ObjectPresetContract(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = ObjectPresetInputs(descriptor.Name);
        bool readOnly = descriptor.Name is "preset inspect" or "preset diff" or "preset resolve";
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = descriptor.Name.StartsWith("object-template", StringComparison.Ordinal)
                ? [GameEdition.Fallout4]
                : [GameEdition.Fallout4, GameEdition.SkyrimSpecialEdition],
            Limitations = descriptor.Limitations.AddRange(ObjectPresetLimitations(descriptor.Name)),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = ObjectPresetRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = ObjectPresetOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = ObjectPresetResult(descriptor.Name),
            Effects = ObjectPresetEffects(descriptor.Name),
            RetryPolicy = readOnly ? AgentRetryPolicy.SafeUnchanged : AgentRetryPolicy.RequiresFreshOutput,
            Determinism = AgentDeterminism.Deterministic,
            SupportsDryRun = false,
            Authority = ObjectPresetAuthority(descriptor.Name, readOnly),
            Transitions = []
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract> ObjectPresetRelationships(string name)
    {
        if (name == "object-template propose")
            return
            [
                AtLeastOne(["edition", "game"]),
                new AgentOptionRelationshipContract(AgentOptionRelationshipKind.RequiredWhen,
                    ["combinations", "includes"], ["properties"],
                    "--combinations and --includes are required only when --properties is absent.")
                { Trigger = ObjectPresetTrigger(ObjectPresetAbsent("properties")) }
            ];
        if (name == "object-template write") return [AtLeastOne(["edition", "game"])];
        if (name is "preset inspect" or "preset export" or "preset diff" or "appearance copy")
            return
            [
                InvalidPair("looksmenu", "skyrimse"),
                InvalidPair(["racemenu-jslot", "jslot"], "fallout4")
            ];
        return [];
    }

    private static AgentOptionRelationshipContract InvalidPair(string format, string edition) =>
        InvalidPair([format], edition);

    private static AgentOptionRelationshipContract InvalidPair(ImmutableArray<string> formats, string edition) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, ["format", "edition"], ["format", "edition"],
            "The selected preset format and edition pair is invalid.")
        {
            Trigger = new(AgentPredicateCombination.All,
                [ObjectPresetActiveValues("format", formats), ObjectPresetActive("edition", edition)])
        };

    private static AgentRelationshipPredicate ObjectPresetActive(string option, params string[] values) =>
        ObjectPresetActiveValues(option, [.. values]);

    private static AgentRelationshipPredicate ObjectPresetActiveValues(string option, ImmutableArray<string> values) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf, values, false);

    private static AgentRelationshipPredicate ObjectPresetPresent(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf, ["__absent__"], false);

    private static AgentRelationshipPredicate ObjectPresetAbsent(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf, ["__absent__"], true);

    private static AgentOptionRelationshipTrigger ObjectPresetTrigger(AgentRelationshipPredicate predicate) =>
        new(AgentPredicateCombination.All, [predicate]);

    private static ImmutableArray<AgentArtifactContract> ObjectPresetInputs(string name) => name switch
    {
        "object-template propose" =>
        [
            new("object-template-source-plugin", [], "Copied Fallout 4 source plugin containing the selected ARMO."),
            new AgentArtifactContract("object-template-combinations-and-includes", [], "Exact combinations object and includes array used when --properties is absent.") { Trigger = ObjectPresetTrigger(ObjectPresetAbsent("properties")) },
            new AgentArtifactContract("object-template-properties", [], "Exact OMOD property rows selected whenever --properties is present.") { Trigger = ObjectPresetTrigger(ObjectPresetPresent("properties")) }
        ],
        "object-template write" =>
        [
            new("object-template-combinations-proposal", [], "Required typed hash-bound OBTS combinations proposal."),
            new AgentArtifactContract("object-template-properties-proposal", [], "Optional typed OMOD property proposal.") { Trigger = ObjectPresetTrigger(ObjectPresetPresent("properties")) }
        ],
        "preset inspect" or "preset export" => [new("preset-file", [], "Explicit typed LooksMenu or RaceMenu preset input.")],
        "preset diff" => [new("preset-left-and-right", [], "Two explicit typed preset inputs using one admitted format/edition pair.")],
        "preset resolve" => [new("portable-preset-identifier-and-load-order", [], "Portable Plugin|FormID plus exact explicit load-order document.")],
        "appearance copy" => [new("preset-copy-source-and-target", [], "Explicit source and target presets using one admitted format/edition pair.")],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> ObjectPresetOutputs(string name) => name switch
    {
        "object-template propose" =>
        [
            new AgentArtifactContract("object-template-combinations-proposal", [], "Fresh hash-bound OBTS combinations proposal when --properties is absent.") { Trigger = ObjectPresetTrigger(ObjectPresetAbsent("properties")) },
            new AgentArtifactContract("object-template-properties-proposal", [], "Fresh typed OMOD property proposal whenever --properties is present.") { Trigger = ObjectPresetTrigger(ObjectPresetPresent("properties")) }
        ],
        "object-template write" => [new("object-template-plugin", [], "Fresh ordinary plugin containing one materialized ARMO object-template record.")],
        "preset export" => [new("preset-copy", [], "Fresh deterministic typed preset reserialization.")],
        "appearance copy" => [new("appearance-copied-preset", [], "Fresh target preset with selected preset-file appearance sections replaced.")],
        _ => []
    };

    private static ImmutableArray<AgentEffectContract> ObjectPresetEffects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace,
            name switch
            {
                "object-template propose" => "After lexical K-local path checks and typed validation.",
                "object-template write" => "After existing path-ancestry reparse checks and typed validation.",
                _ => "After exact K-local input admission."
            },
            "workspace"));
        if (name == "object-template propose")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Fresh combinations proposal only when --properties is absent.", "k-local-output") { Trigger = ObjectPresetTrigger(ObjectPresetAbsent("properties")) });
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Fresh properties proposal whenever --properties is present.", "k-local-output") { Trigger = ObjectPresetTrigger(ObjectPresetPresent("properties")) });
        }
        else if (name is "object-template write" or "preset export" or "appearance copy")
            effects.Add(new(AgentEffectKind.WriteNewArtifact,
                "Only after validation succeeds; the destination must be fresh.", "k-local-output"));
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static ImmutableArray<AgentAuthorityContract> ObjectPresetAuthority(string name, bool readOnly)
    {
        bool inspect = name == "preset inspect";
        bool objectTemplate = name.StartsWith("object-template", StringComparison.Ordinal);
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission,
                objectTemplate ? AgentAuthorityState.Required : AgentAuthorityState.Established,
                name switch
                {
                    "object-template propose" => ObjectProposeInputAdmissionReason,
                    "object-template write" => ObjectWriteInputAdmissionReason,
                    _ => "Exact V1 options, branches, format pairs, and K-local inputs are admitted before work begins."
                }),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "Explicit paths, source hashes, record identity, portable identifier, or load-order bytes bind the selected source."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, readOnly ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Established, readOnly ? "This command writes no artifact." : "Fresh output is deterministically derived from admitted typed inputs."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, readOnly ? AgentAuthorityState.Established : AgentAuthorityState.Required, readOnly ? "The typed read result is the bounded static inspection." : "The fresh output is not independently structurally reopened by this command."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "This family emits no off-engine visual preview."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, inspect ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Required, inspect ? "V1 inspection does not request visual acceptance." : "Human visual acceptance is outside this static workflow."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Typed parsing, proposal, copying, and binary readback are not game-runtime proof."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval, inspect ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Required, inspect ? "V1 inspection performs no promotion." : "Promotion requires separate human approval.")
        ];
    }

    private static string ObjectPresetResult(string name) => name switch
    {
        "object-template propose" => "Written state and diagnostics for exactly one Fallout 4 proposal branch: properties presence wins and returns typed OMOD property rows; otherwise combinations plus includes return a typed hash-bound OBTS proposal. Artifact kind/schemaVersion metadata is not a formal schema identifier.",
        "object-template write" => "Written state, fresh ordinary-plugin path, target ARMO FormID, output SHA-256, and diagnostics after materialization; the final OBTS/OMOD structure is not independently reparsed, and static, visual, game-runtime, and promotion verification remain separate.",
        "preset inspect" => "Format, edition, source SHA-256, validity, typed in-memory appearance, and diagnostics from one explicit preset; V1 writes no receipt, workflow, or artifact and publishes no formal result schema identifier.",
        "preset export" => "Written state, format, source SHA-256, fresh output SHA-256, and diagnostics for a deterministic typed preset reserialization; the output is not independently reopened and has no formal result schema identifier.",
        "preset diff" => "Equality, ordered typed field differences, and diagnostics from two explicit read-only preset inputs; no artifact, visual, runtime, or formal result-schema claim is published.",
        "preset resolve" => "Portable identifier, optional resolved FormID, resolution state, and diagnostics from the exact --load-order document; no live profile, artifact, runtime, or formal result-schema claim is published.",
        "appearance copy" => "Written state, format, edition, expanded selected sections, source/target SHA-256 values, fresh output SHA-256, and diagnostics; preset-only copying is not visual, game-runtime, or promotion authority and publishes no formal result schema identifier.",
        _ => throw new InvalidOperationException($"Unknown object/preset command: {name}")
    };

    private static ImmutableArray<string> ObjectPresetLimitations(string name) => name switch
    {
        "object-template propose" => [ObjectCombinationDialect, ObjectPropertyDialect, ObjectProposePathAdmissionLimitation],
        "object-template write" => ["The required combinations proposal must use artifactKind object-template-combinations-proposal and may be augmented only by an optional object-template-properties-proposal; both are duplicate-free schemaVersion 1 JSON bound to the same Fallout 4 source plugin/FormID and current hashes. The fresh .esp writer emits one ARMO with bounded OBTE/OBTF/FULL/OBTS/STOP data and independently checks the written bytes; artifact metadata is not a formal schema identifier.", ObjectWritePathAdmissionLimitation],
        "preset inspect" => ["V1 accepts only format, edition, and input and returns an in-memory inspection. It does not accept the strict protocol-2 digest, receipt-output, or workflow options and does not publish the protocol-2 result schema."],
        "preset export" or "preset diff" => ["LooksMenu pairs only with fallout4; racemenu-jslot and its jslot alias pair only with skyrimse. Inputs are explicit K-local JSON presets bounded by the preset parser; export writes a fresh typed reserialization while diff is read-only."],
        "preset resolve" => ["Read an explicit schema-1 load-order JSON file with edition and plugins (name/order/enabled), or the legacy plugin-to-byte-index map. Schema-1 enabled entries are ordered by order; disabled targets refuse. Without --data-root all schema-1 entries use full indexes and legacy map indexes are preserved. Optional copied --data-root uses actual TES4 flags to assign separate full and light indexes and mask light local IDs to 12 bits. No live profile is inferred."],
        "appearance copy" => ["LooksMenu pairs only with fallout4; racemenu-jslot and its jslot alias pair only with skyrimse.", AppearanceExpansion],
        _ => []
    };
}
