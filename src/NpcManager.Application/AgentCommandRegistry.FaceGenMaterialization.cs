using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsFaceGenMaterializationCommand(string name) =>
        FaceGenMaterializationOptionCatalog.IsFamily(name);

    private static AgentCommandContract FaceGenMaterializationContract(
        CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = FaceGenMaterializationInputs(descriptor.Name);
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = descriptor.Name is "facegen build-tint-native" or "facegen bake-all-native"
                ? [GameEdition.SkyrimSpecialEdition]
                : descriptor.SupportedGames,
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = FaceGenMaterializationRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = FaceGenMaterializationOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = FaceGenMaterializationResult(descriptor.Name),
            Effects = FaceGenMaterializationEffects(descriptor.Name),
            RetryPolicy = FaceGenMaterializationRetry(descriptor.Name),
            Determinism = FaceGenMaterializationDeterminism(descriptor.Name),
            Limitations = descriptor.Name == "facegen build-geom-nif"
                ? descriptor.Limitations.Add("Choose the route from its actual inputs: supplied complete CharGen carriers use hash-bound transport; explicit native construction through npc create-from-jslot requires selected model, TRI and slider-catalog authority; runtime-JSlot/MDNR remains a separate runtime appearance route. A sibling route never repairs nonfinite morphs, missing assets, hash/topology drift or absent review authority.")
                : descriptor.Limitations,
            SupportsDryRun = descriptor.Name == "facegen options",
            Authority = FaceGenMaterializationAuthority(descriptor.Name)
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract>
        FaceGenMaterializationRelationships(string name)
    {
        var rows = ImmutableArray.CreateBuilder<AgentOptionRelationshipContract>();
        rows.Add(AtLeastOne(["edition", "game"]));
        switch (name)
        {
            case "facegen options":
                rows.Add(AtLeastOne(["input", "options"]));
                rows.Add(FaceGenRequiredWhen(["output"], ["apply"],
                    "Presence of --apply requires --output.",
                    FaceGenPredicate("apply", AgentPredicateMatch.NoneOf, ["__absent__"], false)));
                rows.Add(FaceGenForbiddenWhen(["apply", "dry-run"], ["apply", "dry-run"],
                    "--apply and --dry-run cannot both be present.", FaceGenAllPresent("apply", "dry-run")));
                break;
            case "facegen bake-all":
                rows.Add(AtLeastOne(["manifests", "batch"]));
                break;
            case "facegen build-plugin":
                rows.Add(AtLeastOne(["manifest", "target"]));
                rows.Add(AtLeastOne(["plugin", "target-plugin"]));
                break;
            case "facegen build-geom-nif":
                rows.Add(FaceGenRequiredWhen(["morphs"], ["mode"],
                    "Bake mode, including omitted --mode, requires --morphs.",
                    FaceGenPredicate("mode", AgentPredicateMatch.NoneOf, ["transport"], true)));
                rows.Add(FaceGenRequiredWhen(["source-sha256", "transport-profile"], ["mode"],
                    "Transport mode requires source hash and profile.",
                    FaceGenPredicate("mode", AgentPredicateMatch.AnyOf, ["transport"], false)));
                rows.Add(FaceGenRequiredWhen(["carrier", "carrier-sha256", "shape"], ["transport-profile"],
                    "Geometry-into-carrier transport requires the complete carrier binding.",
                    FaceGenPredicate("transport-profile", AgentPredicateMatch.AnyOf, ["geometry-into-carrier"], false)));
                rows.Add(FaceGenForbiddenWhen(
                    ["carrier", "carrier-sha256", "shape"], ["transport-profile"],
                    "Complete-carrier transport forbids geometry-into-carrier options.",
                    FaceGenPredicate("transport-profile", AgentPredicateMatch.AnyOf, ["complete-carrier"], false)));
                rows.Add(FaceGenForbiddenWhen(
                    ["source-sha256", "transport-profile", "carrier", "carrier-sha256", "shape"], ["mode"],
                    "Transport-only options are forbidden in bake mode, including omitted --mode.",
                    FaceGenPredicate("mode", AgentPredicateMatch.NoneOf, ["transport"], true)));
                break;
        }
        return rows.ToImmutable();
    }

    private static AgentOptionRelationshipContract FaceGenRequiredWhen(
        ImmutableArray<string> options, ImmutableArray<string> references,
        string condition, AgentOptionRelationshipTrigger trigger) =>
        new(AgentOptionRelationshipKind.RequiredWhen, options, references, condition)
        { Trigger = trigger };

    private static AgentOptionRelationshipContract FaceGenForbiddenWhen(
        ImmutableArray<string> options, ImmutableArray<string> references,
        string condition, AgentOptionRelationshipTrigger trigger) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, options, references, condition)
        { Trigger = trigger };

    private static AgentOptionRelationshipTrigger FaceGenPredicate(
        string option, AgentPredicateMatch match, ImmutableArray<string> values,
        bool matchesWhenAbsent) => new(AgentPredicateCombination.All,
        [new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, option,
            match, values, matchesWhenAbsent)]);

    private static AgentOptionRelationshipTrigger FaceGenAllPresent(params string[] options) =>
        new(AgentPredicateCombination.All, options.Select(option =>
            new AgentRelationshipPredicate(AgentPredicateSource.OptionValue,
                option, AgentPredicateMatch.NoneOf, ["__absent__"], false)).ToImmutableArray());

    private static ImmutableArray<AgentArtifactContract> FaceGenMaterializationInputs(string name) => name switch
    {
        "facegen build-geom" or "facegen build" => [new("facegen-shape-manifest", [], "Existing semantic FaceGen shape manifest; artifact kind/version is descriptive, not a schema identifier.")],
        "facegen build-geom-nif" =>
        [
            new("facegeom-source-nif", [], "Existing source NIF selected by --source."),
            new AgentArtifactContract("facegeom-morph-selection", [], "Inline JSON or admitted @file morph selection.") { Trigger = Present("morphs") },
            new AgentArtifactContract("facegeom-carrier-nif", [], "Hash-bound carrier NIF used only by geometry-into-carrier transport.") { Trigger = Present("carrier") }
        ],
        "facegen build-geom-bound" or "facegen build-tint-native" or "facegen bake-all-native" or "facegen pack" => [new("copied-data-root", [], "Existing copied Data tree containing the admitted plugins and provider files.")],
        "facegen build-tint" => [new("facetint-layer-manifest", [], "Existing semantic FaceTint layer manifest; artifact kind/version is descriptive, not a schema identifier.")],
        "facegen build-tint-bound" => [new("copied-data-root", [], "Existing copied Data tree containing the admitted plugins and provider files."), new("facetint-layer-manifest", [], "Existing semantic FaceTint layer manifest.")],
        "facegen options" => [new("chargen-facegen-options", [], "Existing options JSON selected by canonical --input or its --options alias.")],
        "facegen bake-all" => [new("facegen-batch-manifest", [], "Existing semantic batch selected by canonical --manifests or its --batch alias.")],
        "facegen build-plugin" => [new("facegen-plugin-target-manifest", [], "Existing target manifest selected by canonical --manifest or its --target alias.")],
        "facegen deploy" => [new("facegen-package", [], "Existing hash-bound facegen-pack manifest and package payload.")],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> FaceGenMaterializationOutputs(string name) => name switch
    {
        "facegen build-geom" => [new("semantic-facegeom-json", [], "Fresh deterministic semantic FaceGeom JSON; not a NIF or game-ready asset.")],
        "facegen build-geom-nif" => [new("facegeom-nif", [], "Fresh real NIF with hash, byte length, geometry evidence, import mode, and runtimeAuthority false.")],
        "facegen build-geom-bound" => [new("provider-bound-facegeom-nif", [], "Fresh real NIF at the resolved canonical provider-relative output path.")],
        "facegen build-tint" =>
        [
            new("semantic-facetint-json", [], "Fresh deterministic semantic FaceTint JSON."),
            new AgentArtifactContract("facetint-dds", [], "Fresh real DDS selected by --dds-output.") { Trigger = Present("dds-output") },
            new AgentArtifactContract("facetint-provider-evidence", [], "Hash-bound decoded provider-source evidence selected by --provider-root.") { Trigger = Present("provider-root") }
        ],
        "facegen build-tint-bound" => [new("semantic-facetint-json", [], "Fresh semantic FaceTint JSON."), new("provider-bound-facetint-dds", [], "Fresh real DDS at the resolved canonical provider-relative output path.")],
        "facegen build-tint-native" => [new("native-skyrim-facetint-dds", [], "Fresh native Skyrim DDS with plugin/mask authorities and independent readback evidence; runtimeAuthority remains false.")],
        "facegen options" => [new AgentArtifactContract("chargen-facegen-options", [], "Fresh canonical options JSON only when --apply is present.") { Trigger = Present("apply") }],
        "facegen build" => [new("semantic-facegen-correction-report", [], "Fresh semantic correction report; not a game asset.")],
        "facegen bake-all" => [new("semantic-facegen-batch-report", [], "Fresh semantic per-entry analysis report; not NIF/DDS materialization.")],
        "facegen bake-all-native" => [new("native-skyrim-facegen-pairs", [], "Zero or more per-NPC canonical NIF/DDS pairs; partial success is retained and reported.")],
        "facegen build-plugin" => [new("semantic-facegen-plugin-report", [], "Fresh semantic per-target plugin report; not NIF/DDS materialization.")],
        "facegen pack" => [new("facegen-package", [], "Fresh Data-shaped package plus hash-bound facegen-pack manifest; runtimeProof remains false.")],
        "facegen deploy" => [new("copied-data-facegen-files", [], "Files copied into the explicit copied Data root, or identical files retained idempotently.")],
        _ => []
    };

    private static ImmutableArray<AgentEffectContract> FaceGenMaterializationEffects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace, "Admitted inputs are read before any output is committed.", "workspace"));
        if (name == "facegen build-geom-nif")
            effects.Add(new AgentEffectContract(AgentEffectKind.InvokeAdmittedProcess, "Bake mode, including omitted --mode, invokes the pinned Blender/PyNifly adapter; transport stays in-process.", "workspace")
            {
                Trigger = FaceGenPredicate("mode", AgentPredicateMatch.NoneOf, ["transport"], true)
            });
        else if (name == "facegen build-geom-bound")
            effects.Add(new(AgentEffectKind.InvokeAdmittedProcess, "The provider-bound bake invokes the pinned Blender/PyNifly adapter after provider admission.", "workspace"));
        else if (name == "facegen build-tint")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.InvokeAdmittedProcess, "When --provider-root is present, the pinned DirectXTex decoder is admitted for provider DDS sampling.", "workspace")
            {
                Trigger = Present("provider-root")
            });
            effects.Add(new AgentEffectContract(AgentEffectKind.InvokeAdmittedProcess, "When --dds-output is present and the explicit or manifest-resolved format is BC3/BC7, the pinned DirectXTex encoder is admitted; explicit BGRA8/uncompressed remains in-process.", "workspace")
            {
                Trigger = new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
                [
                    new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, "dds-output",
                        AgentPredicateMatch.NoneOf, ["__absent__"], false),
                    new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, "format",
                        AgentPredicateMatch.AnyOf, ["bc3", "bc7"], true)
                ])
            });
        }
        else if (name == "facegen build-tint-bound")
            effects.Add(new(AgentEffectKind.InvokeAdmittedProcess, "The pinned DirectXTex process may be invoked for compressed DDS decoding or encoding; BGRA8-only work remains in-process.", "workspace"));
        if (name == "facegen deploy")
            effects.Add(new(AgentEffectKind.DeployToCopiedData, "Only after every package source and destination preflights without conflict.", "k-local-output"));
        else if (name == "facegen options")
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when --apply is present and validation succeeds.", "k-local-output") { Trigger = Present("apply") });
        else
            effects.Add(new(AgentEffectKind.WriteNewArtifact, "Only after command-specific admission and validation succeed.", "k-local-output"));
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static AgentRetryPolicy FaceGenMaterializationRetry(string name) => name switch
    {
        "facegen bake-all-native" or "facegen deploy" => AgentRetryPolicy.SafeUnchanged,
        _ => AgentRetryPolicy.RequiresFreshOutput
    };

    private static AgentDeterminism FaceGenMaterializationDeterminism(string name) => name switch
    {
        "facegen build-geom-nif" or "facegen build-geom-bound" or "facegen build-tint" or "facegen build-tint-bound" or
            "facegen build-tint-native" or "facegen bake-all-native" or "facegen pack" => AgentDeterminism.PinnedInputsAndTools,
        _ => AgentDeterminism.Deterministic
    };

    private static string FaceGenMaterializationResult(string name) => name switch
    {
        "facegen build-geom" => "Written state, semantic artifact kind, edition, NPC FormID, output SHA-256, and diagnostics; the JSON is not a NIF and no grounded result schema identifier is published.",
        "facegen build-geom-nif" => "Written state and real NIF evidence including paths, hashes, byte and vertex counts, TRI/morph inputs, import mode, optional carrier hash, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published.",
        "facegen build-geom-bound" => "Written state, provider-bound FaceGeom artifact, output SHA-256, and diagnostics; plugin loadability and runtime appearance remain separate and no grounded result schema identifier is published.",
        "facegen build-tint" => "Written state, artifact kind, edition, NPC FormID, dimensions, format, mip count, semantic output SHA-256, optional texture output path/SHA-256, and diagnostics; no grounded result schema identifier is published.",
        "facegen build-tint-bound" => "Written state, provider-bound FaceTint artifact, semantic and DDS hashes, and diagnostics; runtime appearance remains separate and no grounded result schema identifier is published.",
        "facegen build-tint-native" => "Written state, native Skyrim DDS artifact, plugin and mask authorities, independent readback evidence, runtimeAuthority false, and diagnostics; no grounded result schema identifier is published.",
        "facegen options" => "SchemaVersion 1 validation/apply state, edition, input/output paths and hashes, typed options, and diagnostics; apply is presence-selected and no grounded result schema identifier is published.",
        "facegen build" => "Written state, semantic correction artifact kind, edition, NPC FormID, correction count, output SHA-256, and diagnostics; no grounded result schema identifier is published.",
        "facegen bake-all" => "Written/failure state, semantic batch artifact kind, attempted/passed/skipped/failed counts, output SHA-256, and diagnostics; no NIF/DDS output or grounded result schema identifier is published.",
        "facegen bake-all-native" => "SchemaVersion 1 native batch status with discovered/baked/skipped/failed counts, ordered per-NPC outcomes, progress, diagnostics, and runtimeAuthority false. Partial outputs are retained; a safe retry skips complete pairs and reports half-pair conflicts per-NPC. No grounded result schema identifier is published.",
        "facegen build-plugin" => "Written/failure state, semantic plugin report kind, target plugin, attempted/selected/excluded/passed/skipped/failed counts, output SHA-256, and diagnostics; no grounded result schema identifier is published.",
        "facegen pack" => "Written state, fresh package root, facegen-pack artifact and manifest SHA-256, and diagnostics. The deterministic Data package is source-preserving, non-overwriting, and runtimeProof false; no grounded result schema identifier is published.",
        "facegen deploy" => "Deployed/alreadyPresent state, edition, package and copied Data paths, per-file hashes and outcomes, and diagnostics. Existing identical files are idempotent; any differing destination conflict refuses the whole preflight. No grounded result schema identifier is published.",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    private static ImmutableArray<AgentAuthorityContract> FaceGenMaterializationAuthority(string name)
    {
        bool provider = name is "facegen build-geom-bound" or "facegen build-tint-bound" or
            "facegen build-tint-native" or "facegen bake-all-native" or "facegen pack" or "facegen deploy";
        bool verified = name is "facegen build-geom-nif" or "facegen build-geom-bound" or
            "facegen build-tint-native" or "facegen bake-all-native" or "facegen deploy";
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Command options and K-local inputs are admitted before execution."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, provider ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, provider ? "Copied providers and their exact identities are resolved or hash-bound." : "This semantic or direct-source route does not establish game-provider identity."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, AgentAuthorityState.Established, "Successful writes are derived from admitted inputs with the declared deterministic boundary."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, verified ? AgentAuthorityState.Established : AgentAuthorityState.Required, verified ? "The command reopens or independently verifies its relevant static output boundary." : "The produced semantic artifact still requires an independent static verification phase."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "This command does not render an off-engine preview."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static materialization does not establish human visual acceptance."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Static files, provider evidence, and readback do not establish game-runtime behavior."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Command success never grants promotion approval.")
        ];
    }
}
