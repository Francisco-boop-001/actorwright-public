using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsPreviewPluginCommand(string name) =>
        PreviewPluginOptionCatalog.Contains(name);

    private static AgentCommandContract PreviewPluginContract(
        CommandDescriptor descriptor)
    {
        string canonical = descriptor.Name == "render npc"
            ? "preview render"
            : descriptor.Name;
        ImmutableArray<AgentArtifactContract> inputs =
            PreviewPluginInputs(canonical);
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            CanonicalCommand = descriptor.Name == "render npc"
                ? "preview render"
                : null,
            SupportedGames = canonical == "preview npc"
                ? [GameEdition.SkyrimSpecialEdition]
                : descriptor.SupportedGames,
            Purpose = descriptor.Name == "render npc"
                ? CommandCatalog.All.Single(item => item.Name == "preview render").Description
                : descriptor.Description,
            Limitations = PreviewPluginLimitations(canonical),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = PreviewPluginRelationships(canonical),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = PreviewPluginOutputs(canonical),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = PreviewPluginResult(canonical),
            Effects = PreviewPluginEffects(canonical),
            RetryPolicy = PreviewPluginRetry(canonical),
            Determinism = PreviewPluginDeterminism(canonical),
            SupportsDryRun = false,
            Authority = PreviewPluginAuthority(canonical),
            Transitions = []
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract>
        PreviewPluginRelationships(string name) => name switch
        {
            "preview render" =>
            [
                AtLeastOne(["edition", "game"]),
                RequiresTogether("outfit", "variant"),
                RequiresTogether("asset-root", "image-output"),
                T7Forbidden(["frame", "time"],
                    "Frame and time cannot both be present.",
                    T7AllPresent("frame", "time")),
                T7Required(["animation"], ["frame", "time", "fps", "play"],
                    "Frame, time, FPS, and play require --animation.",
                    T7AnyPresent("frame", "time", "fps", "play")),
                T7Forbidden(["animation"],
                    "Animation requires exactly one of --frame or --time.",
                    T7PresentAndAbsent("animation", "frame", "time")),
                T7Required(["asset-root", "image-output"], ["width", "height"],
                    "Width or height requires the complete image-render pair.",
                    T7AnyPresent("width", "height")),
                T7Forbidden(["render-headwear"],
                    "Literal --render-headwear true is forbidden without --hair-slots.",
                    new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
                    [
                        T7Truthy("render-headwear", "true").Predicates[0],
                        T7AbsentPredicate("hair-slots")
                    ])),
                T7Forbidden(["hair-slots"],
                    "Supplying --hair-slots is forbidden without --render-headwear true|false.",
                    new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
                    [
                        T7PresentPredicate("hair-slots"),
                        T7AbsentPredicate("render-headwear")
                    ]))
            ],
            "preview npc" =>
                [RequiresTogether("package-manifest", "expected-package-sha256")],
            "preview reroll" or "preview export-nif" or "plugin write" =>
                [AtLeastOne(["edition", "game"])],
            "plugin verify" => PluginVerifyRelationships(),
            "plugin audit" =>
            [
                AtLeastOne(["edition", "game"]),
                T7IncompletePair("plugins-root", "load-order", "loadorder"),
                T7IncompletePair("data-root", "load-order", "loadorder"),
                T7IncompletePair("load-order", "plugins-root", "data-root"),
                T7IncompletePair("loadorder", "plugins-root", "data-root")
            ],
            "plugin deploy" =>
            [
                AtLeastOne(["edition", "game"]),
                AtLeastOne(["plugin", "input-plugin"]),
                AtLeastOne(["expected-sha256", "sha256"])
            ],
            _ => []
        };

    private static ImmutableArray<AgentOptionRelationshipContract>
        PluginVerifyRelationships()
    {
        ImmutableArray<string> presentExpectations = PreviewPluginOptionCatalog
            .For("plugin verify").Skip(9).Select(item => item.Name)
            .Where(name => name is not ("set-flag" or "clear-flag" or "clear-skin" or "whole-skin-sha256"))
            .ToImmutableArray();
        return
        [
            AtLeastOne(["edition", "game"]),
            AtLeastOne(["proposal", "source-plugin"]),
            AtLeastOne(["proposal", "output-plugin"]),
            AtLeastOne(["proposal", "form-id", "npc"]),
            RequiresTogether("whole-skin", "whole-skin-sha256"),
            T7Required(["before", "after"], ["proposal"],
                "The proposal-selected dialect requires --before and --after.",
                Present("proposal")),
            T7Forbidden(["level", "level-mult"],
                "--level and --level-mult cannot both be present.",
                T7AllPresent("level", "level-mult")),
            T7Forbidden(["weight", "weight-triangle"],
                "--weight and --weight-triangle cannot both be present.",
                T7AllPresent("weight", "weight-triangle")),
            T7Forbidden(["skin", "clear-skin"],
                "--skin cannot be combined with active --clear-skin true or 1.",
                new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
                [
                    T7PresentPredicate("skin"),
                    T7ActivePredicate("clear-skin", "true", "1")
                ])),
            T7Forbidden(["source-plugin", "output-plugin", "form-id", "npc"],
                "The direct dialect requires at least one parser-producing expectation; --clear-skin counts only when true or 1.",
                new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
                [
                    T7AbsentPredicate("proposal"),
                    .. presentExpectations.Select(T7AbsentPredicate),
                    T7AbsentOrEmptyPredicate("set-flag"),
                    T7AbsentOrEmptyPredicate("clear-flag"),
                    T7InactivePredicate("clear-skin", "true", "1")
                ]))
        ];
    }

    private static AgentOptionRelationshipContract T7Required(
        ImmutableArray<string> options,
        ImmutableArray<string> references,
        string condition,
        AgentOptionRelationshipTrigger trigger) =>
        new(AgentOptionRelationshipKind.RequiredWhen, options, references,
            condition) { Trigger = trigger };

    private static AgentOptionRelationshipContract T7Forbidden(
        ImmutableArray<string> options,
        string condition,
        AgentOptionRelationshipTrigger trigger) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, options,
            trigger.Predicates.Select(item => item.Subject)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            condition) { Trigger = trigger };

    private static AgentOptionRelationshipContract T7IncompletePair(
        string present,
        string alternativeA,
        string alternativeB) =>
        T7Forbidden([present],
            $"--{present} is invalid unless --{alternativeA} or --{alternativeB} is present.",
            new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
            [
                T7PresentPredicate(present),
                T7AbsentPredicate(alternativeA),
                T7AbsentPredicate(alternativeB)
            ]));

    private static AgentOptionRelationshipTrigger T7AllPresent(
        params string[] options) => new(AgentPredicateCombination.All,
        options.Select(T7PresentPredicate).ToImmutableArray());

    private static AgentOptionRelationshipTrigger T7AnyPresent(
        params string[] options) => new(AgentPredicateCombination.Any,
        options.Select(T7PresentPredicate).ToImmutableArray());

    private static AgentOptionRelationshipTrigger T7PresentAndAbsent(
        string present,
        params string[] absent) => new(AgentPredicateCombination.All,
        [T7PresentPredicate(present), .. absent.Select(T7AbsentPredicate)]);

    private static AgentOptionRelationshipTrigger T7Truthy(
        string option,
        params string[] values) => new(AgentPredicateCombination.All,
        [T7ActivePredicate(option, values)]);

    private static AgentRelationshipPredicate T7ActivePredicate(
        string option,
        params string[] values) =>
        new(AgentPredicateSource.OptionValue, option,
            AgentPredicateMatch.AnyOf, [.. values], false);

    private static AgentRelationshipPredicate T7InactivePredicate(
        string option,
        params string[] activeValues) =>
        new(AgentPredicateSource.OptionValue, option,
            AgentPredicateMatch.NoneOf, [.. activeValues], true);

    private static AgentRelationshipPredicate T7PresentPredicate(string option) =>
        new(AgentPredicateSource.OptionValue, option,
            AgentPredicateMatch.NoneOf, ["__absent__"], false);

    private static AgentRelationshipPredicate T7AbsentPredicate(string option) =>
        new(AgentPredicateSource.OptionValue, option,
            AgentPredicateMatch.AnyOf, ["__absent__"], true);

    private static AgentRelationshipPredicate T7AbsentOrEmptyPredicate(
        string option) =>
        new(AgentPredicateSource.OptionValue, option,
            AgentPredicateMatch.AnyOf, ["__absent__", ""], true);

    private static ImmutableArray<AgentArtifactContract>
        PreviewPluginInputs(string name) => name switch
        {
            "preview render" =>
            [
                new("preview-scene-manifest", [],
                    "Existing semantic scene manifest; no schema identifier is declared by its loader."),
                new AgentArtifactContract("copied-asset-root", [],
                    "Optional copied asset root for off-engine PNG rendering.")
                    { Trigger = Present("asset-root") }
            ],
            "preview npc" =>
            [
                new("reviewed-game-intake", ["npcmanager-reviewed-game-intake/2"],
                    "Existing codec-validated reviewed Skyrim intake."),
                new AgentArtifactContract(WorkflowArtifactKinds.NpcPackageManifest, [],
                    "Optional exact hash-bound Manager package overlay; its artifact kind is not promoted to a document schema identifier.")
                    { Trigger = Present("package-manifest") }
            ],
            "preview reroll" =>
                [new("preview-scene-manifest", [], "Existing validated variant manifest.")],
            "preview export-nif" =>
            [
                new("preview-scene-artifact", [], "Existing semantic preview scene."),
                new AgentArtifactContract("copied-asset-root", [],
                    "Copied asset root consumed only in binary export mode.")
                    { Trigger = Present("asset-root") }
            ],
            "plugin write" =>
                [new("npc-patch-proposal", [NpcMutationProposal.SchemaIdentifier, "record-proposal.hdpt.v1"],
                    "Existing NPC scalar mutation proposal or output-owned Skyrim HDPT record proposal.")],
            "plugin verify" =>
            [
                new AgentArtifactContract("plugin-verification-proposal", [],
                    "Proposal selected by presence; accepted proposal artifact kinds are validated by the service and are not collapsed into one invented schema.")
                    { Trigger = Present("proposal") },
                new AgentArtifactContract("before-plugin", [],
                    "Exact proposal-bound before plugin.") { Trigger = Present("proposal") },
                new AgentArtifactContract("after-plugin", [],
                    "Exact proposal-bound after plugin.") { Trigger = Present("proposal") },
                new AgentArtifactContract("source-plugin", [],
                    "Direct mutation-expectation source plugin when --proposal is absent.")
                    { Trigger = T7Absent("proposal") },
                new AgentArtifactContract("output-plugin", [],
                    "Direct mutation-expectation output plugin when --proposal is absent.")
                    { Trigger = T7Absent("proposal") }
            ],
            "plugin audit" =>
            [
                new("before-plugin", [], "Existing before plugin."),
                new("after-plugin", [], "Existing after plugin."),
                new AgentArtifactContract("copied-plugin-provider-context", [],
                    "Optional copied plugin root plus load order for provider resolution.")
                    { Trigger = T7AnyPresent("plugins-root", "data-root") }
            ],
            "plugin deploy" =>
                [new("hash-bound-plugin", [], "Existing exact-hash-bound source plugin.")],
            _ => []
        };

    private static ImmutableArray<AgentArtifactContract>
        PreviewPluginOutputs(string name) => name switch
        {
            "preview render" =>
            [
                new("preview-scene-artifact", [], "Fresh semantic preview artifact."),
                new AgentArtifactContract("rendered-preview-png", [],
                    "Optional fresh off-engine PNG.") { Trigger = Present("image-output") }
            ],
            "preview npc" =>
                [new("npc-preview-bundle", [],
                    "Fresh persisted six-view off-engine bundle; its version string is not promoted to a document schema identifier.")],
            "preview reroll" =>
                [new("preview-reroll-artifact", [], "Fresh deterministic semantic choice artifact.")],
            "preview export-nif" =>
            [
                new AgentArtifactContract("preview-nif-export-plan", [],
                    "Fresh sandbox plan when --asset-root is absent.")
                    { Trigger = T7Absent("asset-root") },
                new AgentArtifactContract("preview-nif-binary", [],
                    "Fresh independently block-table-verified binary NIF when --asset-root is present.")
                    { Trigger = Present("asset-root") }
            ],
            "plugin write" =>
                [new("written-plugin", [], "Fresh independently verified output plugin.")],
            "plugin deploy" =>
                [new("copied-data-plugin", [],
                    "Copied Data destination plugin; an existing identical file is an idempotent success.")],
            _ => []
        };

    private static ImmutableArray<AgentEffectContract>
        PreviewPluginEffects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace,
            "When admitting exact K-local input paths and documents.", "workspace"));
        switch (name)
        {
            case "preview render":
                effects.Add(new(AgentEffectKind.WriteNewArtifact,
                    "After semantic scene validation succeeds.", "k-local-output"));
                effects.Add(new AgentEffectContract(AgentEffectKind.InvokeAdmittedProcess,
                    "Only when --image-output selects off-engine Blender/PyNifly rendering.",
                    "workspace") { Trigger = Present("image-output") });
                effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                    "Only when --image-output selects a fresh PNG.", "k-local-output")
                    { Trigger = Present("image-output") });
                break;
            case "preview npc":
                effects.Add(new(AgentEffectKind.InvokeAdmittedProcess,
                    "When the admitted renderer composes six off-engine views.", "workspace"));
                effects.Add(new(AgentEffectKind.WriteNewArtifact,
                    "Only after preview composition and output validation succeed.", "k-local-output"));
                break;
            case "preview reroll" or "preview export-nif" or "plugin write":
                if (name == "preview export-nif")
                    effects.Add(new AgentEffectContract(AgentEffectKind.InvokeAdmittedProcess,
                        "Only when --asset-root selects binary Blender/PyNifly export.",
                        "workspace") { Trigger = Present("asset-root") });
                effects.Add(new(AgentEffectKind.WriteNewArtifact,
                    "Only after the selected mode validates and succeeds.", "k-local-output"));
                break;
            case "plugin deploy":
                effects.Add(new(AgentEffectKind.DeployToCopiedData,
                    "After source/hash/destination preflight; an identical destination is unchanged success.",
                    "k-local-output"));
                break;
        }
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static AgentRetryPolicy PreviewPluginRetry(string name) => name switch
    {
        "plugin verify" or "plugin audit" or "plugin deploy" =>
            AgentRetryPolicy.SafeUnchanged,
        _ => AgentRetryPolicy.RequiresFreshOutput
    };

    private static AgentDeterminism PreviewPluginDeterminism(string name) =>
        name is "preview render" or "preview npc" or "preview export-nif"
            ? AgentDeterminism.PinnedInputsAndTools
            : AgentDeterminism.Deterministic;

    private static ImmutableArray<AgentAuthorityContract>
        PreviewPluginAuthority(string name)
    {
        bool materializes = name is not ("plugin verify" or "plugin audit");
        bool preview = name is "preview render" or "preview npc";
        AgentAuthorityState staticVerification = name switch
        {
            "preview npc" or "plugin write" or "plugin verify" or
                "plugin audit" or "plugin deploy" => AgentAuthorityState.Established,
            _ => AgentAuthorityState.Required
        };
        AgentAuthorityState provider = name == "plugin audit"
            ? AgentAuthorityState.Required
            : AgentAuthorityState.Established;
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                "The selected V1 dialect and exact K-local inputs are admitted before work begins."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity,
                provider,
                provider == AgentAuthorityState.Established
                    ? "The selected source, manifest, proposal, or hash binding establishes the available provider identity."
                    : "Provider precedence is established only when plugin audit receives the optional copied-root/load-order pair."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                materializes ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable,
                materializes
                    ? "Persistent output is derived from admitted inputs under the declared determinism boundary."
                    : "This read-only verification command does not materialize a persistent artifact."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                staticVerification,
                staticVerification == AgentAuthorityState.Established
                    ? "The selected route independently validates or reopens its static result."
                    : "The command does not establish independent verification across every supported mode."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview,
                preview ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable,
                preview
                    ? "Only off-engine preview evidence is established."
                    : "This command does not render preview pixels."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance,
                AgentAuthorityState.Required,
                "Static or off-engine evidence does not establish human visual acceptance."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification,
                AgentAuthorityState.Required,
                "No Task 7 V1 result establishes game-runtime behavior or appearance."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval,
                AgentAuthorityState.Required,
                "Command success never grants release or consumer promotion approval.")
        ];
    }

    private static ImmutableArray<string> PreviewPluginLimitations(string name) =>
        name switch
        {
            "preview npc" =>
            [
                "V1 is Skyrim-only and keeps package overlay optional; it does not adopt protocol-2 uppercase-hash or required-workflow gates.",
                "V1 workflow-bundle, workflow-bundle-sha256, and workflow-output are accepted and bound but currently unconsumed by the handler."
            ],
            "preview export-nif" =>
            ["Without --asset-root the command writes only a semantic plan; presence selects binary NIF export and an admitted external process."],
            "plugin verify" =>
            [
                "Presence of --proposal selects proposal-bound verification; absence selects the direct mutation-expectation dialect. There is no second command alias.",
                "An empty --set-flag or --clear-flag token retains legacy parser acceptance but produces no direct verification expectation."
            ],
            "plugin deploy" =>
            ["Deployment targets only an explicit copied K-local Data root; identical bytes are idempotent and conflicting bytes are refused."],
            _ => CommandCatalog.All.Single(item => item.Name == name).Limitations
        };

    private static string PreviewPluginResult(string name) => name switch
    {
        "preview render" => "Written state, semantic scene/variant/camera/lighting/animation/hair-zap evidence, optional off-engine PNG metadata, hashes, and diagnostics; no grounded result schema identifier is published.",
        "preview npc" => "Composed state, persisted preview bundle/scene versions, source route, off-engine label, contact-sheet path/hash, six view paths/hashes, and diagnostics; V1 workflow options are accepted but unconsumed and no grounded result schema identifier is published.",
        "preview reroll" => "Written state, semantic reroll artifact kind, NPC FormID, signed seed, candidate counts, selected variant/index/outfit, output SHA-256, and diagnostics; the SplitMix64 choice is reproducible and no grounded result schema identifier is published.",
        "preview export-nif" => "Written state plus plan-mode scene/NPC/asset metadata or binary-mode output path/hash/byte length/mesh/exporter/import/morph/armature/hair-zap/face-cull evidence and diagnostics; neither mode grants runtime authority and no grounded result schema identifier is published.",
        "plugin write" => "Applied state, proposal/output paths, optional output SHA-256, selected changes, and diagnostics; output is always fresh and no grounded result schema identifier is published.",
        "plugin verify" => "Validity, observed typed changes, and diagnostics from either the proposal-bound before/after route or the direct mutation-expectation route; no grounded result schema identifier is published.",
        "plugin audit" => "Validity, schemaVersion 1 surface counts, masters, risky signatures, record changes, optional provider resolutions, and diagnostics; no grounded result schema identifier is published.",
        _ => "Deployed/alreadyPresent state, edition, source/destination paths and hashes, byte length, and diagnostics; an identical destination is idempotent, conflicts are refused, and no grounded result schema identifier is published."
    };

    private static AgentOptionRelationshipTrigger T7Absent(string option) =>
        new(AgentPredicateCombination.All, [T7AbsentPredicate(option)]);
}
