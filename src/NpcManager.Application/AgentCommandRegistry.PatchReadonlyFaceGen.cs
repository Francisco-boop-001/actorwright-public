using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsPatchReadonlyFaceGenCommand(string name) => name is
        "npc patch" or "body patch" or "npc face-patch" or "npc edit-package" or
        "facegen diagnose" or "facegen analyze" or "facegen verify" or
        "facegen resolve-providers" or "facegen plan-pack";

    private static AgentCommandContract PatchReadonlyFaceGenContract(CommandDescriptor descriptor)
    {
        bool patch = descriptor.Name is "npc patch" or "body patch" or "npc face-patch";
        bool edit = descriptor.Name == "npc edit-package";
        bool writes = patch || edit;
        ImmutableArray<AgentArtifactContract> inputs = PatchInputs(descriptor.Name);
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            CanonicalCommand = descriptor.Name == "facegen analyze" ? "facegen diagnose" : null,
            SupportedGames = descriptor.Name == "npc edit-package"
                ? [GameEdition.SkyrimSpecialEdition]
                : descriptor.SupportedGames,
            Purpose = descriptor.Name == "body patch"
                ? "Create or apply the same unrestricted hash-bound NPC mutation proposal as npc patch."
                : descriptor.Description,
            Limitations = descriptor.Name switch
            {
                "npc patch" => [.. descriptor.Limitations, "wholeSkin is female Skyrim SE only. headPolicy preserve requires head to be absent, a declared FaceGeom/FaceTint/hair/TRI hash inventory and the same logical output filename; it allocates six skin records and changes only WNAM. An absent headPolicy retains legacy eight-record private-head replacement of WNAM, FTST and selected face PNAM. Schema-7 existingNpcTarget is a separate full-appearance/body-mesh route, not head-preservation evidence. FaceGen is not rebuilt."],
                "body patch" => ["Shares the full permissive V1 npc patch handler; discovery does not grant protocol-2 callability.", "wholeSkin is female Skyrim SE only and does not rebuild FaceGen."],
                "npc edit-package" =>
                [
                    "Skyrim-only existing-NPC edit transaction: appearance-affecting fields, Fallout 4 XP offset and height remain refused; supported gameplay fields are independently verified with static completion capped at STATIC_PASS_RUNTIME_REQUIRED.",
                    "Fresh-plugin mode uses --consolidate and/or --esl-flag with --output, exclusively of scalar/package options. It imports provider-owned top-level records, refuses provider overrides, localized plugins and unproven compressed FormLink relocation, preserves existing IDs, and records static evidence. ESL requires Form44, owned and next IDs at most 0xFFF and no world/cell/placed/navigation records.",
                    "Unknown options are rejected; repeated options retain the legacy command-line parser's last value."
                ],
                _ => descriptor.Limitations
            },
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = PatchRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = PatchOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = ResultFor(descriptor.Name),
            Effects = PatchEffects(descriptor.Name, writes),
            RetryPolicy = writes ? AgentRetryPolicy.RequiresFreshOutput : AgentRetryPolicy.SafeUnchanged,
            Determinism = AgentDeterminism.Deterministic,
            SupportsDryRun = patch,
            Authority = PatchAuthority(descriptor.Name, writes)
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract> PatchRelationships(string name) => name switch
    {
        "npc patch" or "body patch" =>
        [
            AtLeastOne(["edition", "game"]), AtLeastOne(["form-id", "npc"]),
            RequiresTogether("whole-skin", "whole-skin-sha256"),
            ForbiddenActive("apply", "dry-run"), ForbiddenActiveAndPresent("clear-skin", "skin"),
            ForbiddenPresent("level", "level-mult"), ForbiddenPresent("weight", "weight-triangle")
        ],
        "npc face-patch" =>
        [
            AtLeastOne(["game", "edition"]), AtLeastOne(["npc", "form-id"]),
            AtLeastOne(["headparts", "headpart-replace", "hair-color"]), ForbiddenPresent("headparts", "headpart-replace"), ForbiddenActive("apply", "dry-run")
        ],
        "npc edit-package" =>
        [AtLeastOne(["edition", "game"]), AtLeastOne(["input-sha256", "expected-sha256"]), AtLeastOne(["output-root", "output"]), ForbiddenPresent("output", "output-root"), ForbiddenPresent("level", "level-mult")],
        "facegen diagnose" or "facegen analyze" or "facegen verify" or
            "facegen resolve-providers" or "facegen plan-pack" => [AtLeastOne(["edition", "game"])],
        _ => []
    };

    private static AgentOptionRelationshipContract ForbiddenPresent(string first, string second) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, [first, second], [first, second],
            $"--{first} and --{second} cannot both be present.")
        {
            Trigger = AllPresent(first, second)
        };

    private static ImmutableArray<AgentArtifactContract> PatchInputs(string name) => name switch
    {
        "npc patch" or "body patch" => [new("npc-patch-source-plugin", [], "Exact optional-hash-bound copied source plugin selected by --input-plugin."), new AgentArtifactContract("npc-whole-skin", ["npc.whole-skin.request.v1"], "Exact hash-bound copied texture/provider authority.") { Trigger = Present("whole-skin") }],
        "npc face-patch" => [new("npc-face-patch-source-plugin", [], "Exact optional-hash-bound copied source plugin selected by --plugin."), new("npc-face-patch-data-root", [], "Copied Data root used for provider resolution.")],
        "npc edit-package" => [new("existing-npc-source-plugin", [], "Exact hash-bound copied Skyrim source plugin selected by --input-plugin.")],
        "facegen diagnose" or "facegen analyze" or "facegen verify" => [new("facegen-shape-manifest", [], "Existing FaceGen shape manifest; no document schema identifier is published.")],
        "facegen resolve-providers" or "facegen plan-pack" => [new("copied-data-root", [], "Copied Data tree containing plugins and FaceGen providers.")],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> PatchOutputs(string name) => name switch
    {
        "npc patch" or "body patch" =>
        [
            new AgentArtifactContract("npc-patch-proposal", [NpcMutationProposal.SchemaIdentifier, "npc.patch.proposal.v2"], "Optional grounded mutation proposal selected by --proposal; wholeSkin uses schema2 and legacy requests retain schema1.") { Trigger = Present("proposal") },
            new AgentArtifactContract("npc-patch-output-plugin", [], "Fresh patched plugin selected by --output when apply is active.") { Trigger = Truthy("apply") }
        ],
        "npc face-patch" =>
        [
            new AgentArtifactContract("npc-face-patch-proposal", [], "Optional fresh face-patch proposal selected by --proposal; no grounded schema identifier is published.") { Trigger = Present("proposal") },
            new AgentArtifactContract("npc-face-patch-output-plugin", [], "Fresh patched plugin selected by --output when apply is active.") { Trigger = Truthy("apply") }
        ],
        "npc edit-package" =>
        [
            new AgentArtifactContract("existing-npc-edit-package", [], "Fresh source-mastered override or standalone-copy package under --output-root.") { Trigger = Present("output-root") },
            new AgentArtifactContract("plugin-consolidation-output", [], "Fresh plugin and any rebased SEQ after complete static verification.") { Trigger = Present("output") },
            new AgentArtifactContract("plugin-consolidation-evidence", [], "Input SHA-256 bindings, owner-qualified record mapping, output and SEQ hashes; no runtime authority.") { Trigger = Present("output") }
        ],
        _ => []
    };

    private static ImmutableArray<AgentEffectContract> PatchEffects(string name, bool writes)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace, "When admitting copied source files, manifests, providers, or Data roots.", "workspace"));
        if (name is "npc patch" or "body patch" or "npc face-patch")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when --proposal is present and analysis succeeds.", "k-local-output") { Trigger = Present("proposal") });
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when --apply is true or 1 and validation succeeds.", "k-local-output") { Trigger = Truthy("apply") });
        }
        else if (writes)
            effects.Add(new(AgentEffectKind.WriteNewArtifact, "Only after exact source admission and edit verification succeed.", "k-local-output"));
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static string ResultFor(string name) => name switch
    {
        "npc face-patch" => "FacePatchResponse applicability and applied state, edition, source and output plugin paths, target FormID, input and optional output SHA-256 hashes, changes, preserved fields, providers, and diagnostics; no grounded result schema identifier is published.",
        "npc edit-package" => "ExistingNpcEditResult completion and verdict, output-plugin and manifest paths and SHA-256 hashes, mutation changes, package verification, optional consolidationEvidencePath for fresh-plugin mode, and diagnostics; no grounded result schema identifier is published.",
        "facegen diagnose" or "facegen analyze" or "facegen verify" => "Applicability, accepted shape evidence, and diagnostics; no grounded result schema identifier is published.",
        "facegen resolve-providers" => "Provider-resolution verdict, artifact-kind/version evidence, provider paths and hashes, and diagnostics; artifact version is not promoted to a document schema identifier.",
        "facegen plan-pack" => "Pack-plan verdict, artifact-kind/version evidence, planned files and hashes, and diagnostics; artifact version is not promoted to a document schema identifier.",
        _ => $"{name} proposal or apply outcome, exact hashes, changes, preserved fields, and diagnostics; no grounded result schema identifier is published."
    };

    private static ImmutableArray<AgentAuthorityContract> PatchAuthority(string name, bool writes)
    {
        bool provider = name is "npc face-patch" or "facegen resolve-providers" or "facegen plan-pack";
        bool faceGen = name.StartsWith("facegen ", StringComparison.Ordinal);
        bool verifies = name is "facegen verify" or "facegen resolve-providers";
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Typed options and admitted K-local inputs are validated."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, provider ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, provider ? "The command resolves provider identity from copied inputs." : "This route does not establish provider identity."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, writes ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, writes ? "Fresh output is materialized from admitted inputs." : "This read-only command does not materialize a persistent artifact."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, verifies || name == "npc edit-package" ? AgentAuthorityState.Established : writes ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, "Only explicit static verification or the verified edit transaction establishes this authority."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "No off-engine preview is rendered."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, writes || faceGen ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, "Static evidence does not establish human visual acceptance."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, writes || faceGen ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, "Static evidence does not establish game-runtime behavior."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval, writes || faceGen ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, "Command success never grants promotion approval.")
        ];
    }
}
