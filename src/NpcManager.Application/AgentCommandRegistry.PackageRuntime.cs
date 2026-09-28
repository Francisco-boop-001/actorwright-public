using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsPackageRuntimeCommand(string name) =>
        PackageRuntimeOptionCatalog.Contains(name);

    private static AgentCommandContract PackageRuntimeContract(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = PackageRuntimeInputs(descriptor.Name);
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = descriptor.SupportedGames,
            Limitations = descriptor.Limitations.AddRange(PackageRuntimeLimitations(descriptor.Name)),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = PackageRuntimeRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = PackageRuntimeOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = PackageRuntimeResult(descriptor.Name),
            Effects = PackageRuntimeEffects(descriptor.Name),
            RetryPolicy = PackageRuntimeRetry(descriptor.Name),
            Determinism = descriptor.Name == "runtime-script build"
                ? AgentDeterminism.PinnedInputsAndTools
                : AgentDeterminism.Deterministic,
            SupportsDryRun = false,
            Authority = PackageRuntimeAuthority(descriptor.Name),
            Transitions = []
        };
    }

    private static ImmutableArray<AgentOptionRelationshipContract>
        PackageRuntimeRelationships(string name)
    {
        if (name == "package verify")
        {
            AgentRelationshipPredicate strictTrue = T8Active("strict-install-dependencies", "true");
            return
            [
                AtLeastOne(["manifest", "archive"]),
                new(AgentOptionRelationshipKind.ForbiddenWhen, ["manifest", "strict-install-dependencies", "data-root", "plugins"], ["archive"],
                    "ZIP-only layout verification does not consume a manifest or install context.")
                    { Trigger = new(AgentPredicateCombination.All, [T8Present("archive")]) },
                new(AgentOptionRelationshipKind.RequiredWhen, ["payload"], ["archive"], "Archive verification requires payload=runtime-only.")
                    { Trigger = new(AgentPredicateCombination.All, [T8Present("archive")]) },
                new(AgentOptionRelationshipKind.ForbiddenWhen, ["payload", "include-runtime-preset"], ["manifest"], "Manifest verification retains its existing contract.")
                    { Trigger = new(AgentPredicateCombination.All, [T8Present("manifest")]) },
                new(AgentOptionRelationshipKind.RequiredWhen, ["data-root", "plugins"],
                    ["strict-install-dependencies"],
                    "Strict install-dependency verification requires both copied-install inputs.")
                    { Trigger = new(AgentPredicateCombination.All, [strictTrue]) },
                T8ForbiddenOutsideStrict("data-root"),
                T8ForbiddenOutsideStrict("plugins"),
                RequiresTogether("workflow-bundle", "workflow-bundle-sha256")
            ];
        }

        if (name == "package archive")
            return
            [
                AtLeastOne(["source-root", "package"]),
                RequiresTogether("workflow-bundle", "workflow-bundle-sha256"),
                new(AgentOptionRelationshipKind.RequiredWhen, ["payload"], ["include-runtime-preset"], "Explicit preset inclusion requires payload=runtime-only.")
                    { Trigger = new(AgentPredicateCombination.All, [T8Present("include-runtime-preset")]) }
            ];

        return name.StartsWith("runtime-script ", StringComparison.Ordinal)
            ? [AtLeastOne(["edition", "game"])]
            : [];
    }

    private static AgentOptionRelationshipContract T8ForbiddenOutsideStrict(string option) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, [option],
            [option, "strict-install-dependencies"],
            $"--{option} is forbidden unless --strict-install-dependencies is exactly true.")
        {
            Trigger = new(AgentPredicateCombination.All,
            [
                T8Present(option),
                new(AgentPredicateSource.OptionValue, "strict-install-dependencies",
                    AgentPredicateMatch.NoneOf, ["true"], true)
            ])
        };

    private static AgentRelationshipPredicate T8Active(string option, params string[] values) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf,
            [.. values], false);

    private static AgentRelationshipPredicate T8Present(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf,
            ["__absent__"], false);

    private static ImmutableArray<AgentArtifactContract> PackageRuntimeInputs(string name) => name switch
    {
        "package build" or "package archive" =>
            [new("verified-package-root", [], "Existing K-local package root verified before use.")],
        "package inspect" or "package verify" =>
        [
            new AgentArtifactContract("package-manifest", [],
                "Existing package manifest; its schemaVersion is not promoted to a formal schema identifier.")
                { Trigger = name == "package verify" ? new(AgentPredicateCombination.All, [T8Present("manifest")]) : null },
            .. name == "package verify"
                ? new[]
                {
                    new AgentArtifactContract("copied-install-context", [],
                        "Strict-mode copied Data root and complete enabled plugin order.")
                        { Trigger = new(AgentPredicateCombination.All,
                            [T8Active("strict-install-dependencies", "true")]) },
                    new AgentArtifactContract("runtime-install-archive", [], "Existing no-wrapper runtime ZIP; verification reports layout/hash inventory only.")
                        { Trigger = new(AgentPredicateCombination.All, [T8Present("archive")]) }
                }
                : []
        ],
        "runtime-script propose" =>
        [
            new("runtime-appearance-document", [],
                "Inline or K-local schemaVersion-1 appearance input; no formal schema identifier is exported."),
            new AgentArtifactContract("source-plugin", [], "Optional exact copied source plugin.")
                { Trigger = new(AgentPredicateCombination.All, [T8Present("plugin")]) }
        ],
        "runtime-script build" or "runtime-script package" =>
            [new("runtime-script-source-root", [], "Pinned game-specific runtime-script source root.")],
        "runtime-script write" =>
        [
            new("source-plugin", [], "Existing copied source plugin."),
            new("runtime-script-proposal", [],
                "Typed proposal whose schemaVersion and artifactKind are not formal schema identifiers.")
        ],
        "runtime-script deploy" =>
            [new("runtime-script-package", [], "Hash-bound package manifest and game-specific apply PEX.")],
        "runtime-script inspect-vmad" =>
            [new("vmad-plugin", [], "Existing copied plugin inspected without mutation.")],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> PackageRuntimeOutputs(string name) => name switch
    {
        "package build" => [new("rebuilt-package-root", [], "Fresh verified package copy.")],
        "package archive" => [new("deterministic-install-archive", [], "Fresh deterministic no-wrapper install ZIP.")],
        "runtime-script propose" => [new("runtime-script-proposal", [], "Fresh typed, hash-bound proposal.")],
        "runtime-script build" =>
        [
            new("runtime-script-build-evidence", [], "Fresh pinned PSC/PEX static evidence."),
            new("runtime-script-pex-inspection-sidecar", [],
                "Retained fresh <output>.pex-inspect.json emitted by the pinned PEX inspector; existing sidecars are refused.")
        ],
        "runtime-script write" =>
        [
            new("vmad-plugin", [],
                "Fresh source-bound plugin. The writer preserves unrelated non-reserved VMAD scripts by construction; output readback checks only selected-script existence and property count.")
        ],
        "runtime-script package" => [new("runtime-script-package", [], "Fresh game-specific apply-PEX package and manifest.")],
        "runtime-script deploy" => [new("copied-data-runtime-script", [], "Copied Data apply PEX; identical bytes are idempotent.")],
        _ => []
    };

    private static ImmutableArray<AgentEffectContract> PackageRuntimeEffects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace,
            "When exact K-local inputs and documents are admitted.", "workspace"));
        if (name == "runtime-script build")
            effects.Add(new(AgentEffectKind.InvokeAdmittedProcess,
                "When the pinned PEX inspector validates the compiled script.", "workspace"));
        if (name == "runtime-script deploy")
            effects.Add(new(AgentEffectKind.DeployToCopiedData,
                "After package/hash/destination preflight; identical bytes leave the destination unchanged.",
                "k-local-output"));
        else if (name is "package build" or "package archive" or
                 "runtime-script propose" or "runtime-script build" or
                 "runtime-script write" or "runtime-script package")
        {
            effects.Add(new(AgentEffectKind.WriteNewArtifact,
                "Only after validation succeeds; the destination must be fresh.", "k-local-output"));
            if (name == "runtime-script build")
                effects.Add(new(AgentEffectKind.WriteNewArtifact,
                    "The pinned inspector retains a second fresh <output>.pex-inspect.json sidecar; preexistence is refused.",
                    "k-local-output"));
        }
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static AgentRetryPolicy PackageRuntimeRetry(string name) => name switch
    {
        "package inspect" or "package verify" or "runtime-script deploy" or
            "runtime-script inspect-vmad" => AgentRetryPolicy.SafeUnchanged,
        _ => AgentRetryPolicy.RequiresFreshOutput
    };

    private static ImmutableArray<AgentAuthorityContract> PackageRuntimeAuthority(string name)
    {
        bool readOnly = name is "package inspect" or "package verify" or
            "runtime-script inspect-vmad";
        bool staticEstablished = name is not ("package inspect" or
            "runtime-script propose" or "runtime-script write" or
            "runtime-script package");
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established,
                "Exact V1 options and K-local inputs are admitted before work begins."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established,
                name == "package verify" ? "Manifest mode binds declared source authority; archive mode identifies only the observed ZIP bytes and does not establish an absent source manifest." :
                    "Manifest, hash, source-plugin, or pinned game-specific identity binds the selected source."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                readOnly ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Established,
                readOnly ? "This command is read-only." : "Persistent output is derived from admitted inputs under the declared determinism boundary."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                staticEstablished ? AgentAuthorityState.Established : AgentAuthorityState.Required,
                staticEstablished ? "The route independently verifies or reads back its static result." : "A separate static verification phase remains required."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable,
                "This family does not create an off-engine visual preview."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required,
                "Human visual acceptance is outside this static command."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required,
                "Static package, VMAD, PEX, or deployment evidence is not game-runtime proof."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required,
                "Release or consumer promotion requires separate human approval.")
        ];
    }

    private static string PackageRuntimeResult(string name) => name switch
    {
        "package build" => "Written state, copied package-root identity, output artifact inventory and diagnostics after source verification and output byte readback; no grounded result schema identifier or runtime proof is published.",
        "package inspect" => "Validity, manifest identity fields, declared artifact paths/sizes/hashes and diagnostics without reading artifact bytes; no grounded result schema identifier is published.",
        "package verify" => "Manifest mode reports source-bound byte/hash/size/closure evidence and optional strict install authority. Archive mode reports runtime layout and streamed hash inventory with sourceManifestBound=false, installDependencyAuthority=false and runtimeProof=false; readable byte changes cannot be authenticated without expected source authority.",
        "package archive" => "Written state, deterministic no-wrapper ZIP path/hash/entries and independent source-plan readback. Default full includes runtime instructions; optional runtime-only emits admitted install files without evidence or generated instructions. No deployment or runtime proof.",
        "runtime-script propose" => "Written state, typed proposal artifact, source/emitter hash binding, output SHA-256 and diagnostics; schemaVersion and artifactKind values are not promoted to formal schema identifiers.",
        "runtime-script build" => "Written state, pinned PSC/PEX/API/compiler-manifest hashes, retained fresh <output>.pex-inspect.json path/hash, output SHA-256 and diagnostics; preexisting evidence or sidecar paths are refused, and valid PEX evidence is static rather than runtime proof.",
        "runtime-script write" => "Written state, fresh plugin path, target FormID, output SHA-256 and diagnostics after readback confirms only selected-script existence and property count; property semantic equality and unrelated-VMAD preservation are not independently verified, and no runtime proof is published.",
        "runtime-script package" => "Written state, fresh package root, game-specific PEX source/install paths and hashes, manifest SHA-256 and diagnostics; packaging does not establish PEX validity or runtime proof.",
        "runtime-script deploy" => "Installed/alreadyPresent state, copied Data root, destination path/hash and diagnostics; identical bytes are idempotent, conflicts are refused, and deployment is not runtime proof.",
        "runtime-script inspect-vmad" => "Resolved state, copied plugin hash, target NPC, selected/default game-specific script, property names/count, no-write and runtime-proof flags, and diagnostics; inspection is read-only and static.",
        _ => throw new InvalidOperationException($"Unknown package/runtime contract: {name}")
    };

    private static ImmutableArray<string> PackageRuntimeLimitations(string name) => name switch
    {
        "package verify" =>
        [
            "The 64-plugin maximum, provider-presence check, and master-before-dependent ordering apply only when the verified package exposes external install-dependency evidence; ordinary packages still require the strict pair but do not enter that external-order branch.",
            "Archive mode independently reads at most 10000 files, 512 MiB per file and 8 GiB total. It proves install layout/readability and hashes, not equality to an absent source manifest, plugin semantics, external dependency installation or game runtime."
        ],
        "package archive" =>
        [
            "Runtime-only admits root plugins/BSA/BA2, meshes NIF/TRI/HKX/XML, textures DDS, SEQ, compiled scripts, materials, sound, strings and bounded SKSE/F4SE plugin libraries/configs. Evidence/manifests/preset directories are excluded except one explicitly named SKSE CharGen .jslot or F4EE .json preset. Limits: 10000 files, 512 MiB per file, 8 GiB total; source verification still covers excluded files."
        ],
        "runtime-script propose" or "runtime-script write" =>
        [
            "Fallout 4 property profile for NPCM_Manolov_ApplyFO4: IsFemale=bool; SchemaVersion=int; OvlTemplate=string[]; OvlPriority=int[]; OvlRed=float[]; OvlGreen=float[]; OvlBlue=float[]; OvlAlpha=float[]; OvlOffsetU=float[]; OvlOffsetV=float[]; OvlScaleU=float[]; OvlScaleV=float[]; SkinTemplate=string.",
            "Skyrim SE property profile for NPCM_Manolov_ApplySSE: IsFemale=bool; SchemaVersion=int; OvlNode=string[]; OvlDiffuse=string[]; OvlNormal=string[]; OvlHasTint=bool[]; OvlTint=int[]; OvlHasAlpha=bool[]; OvlAlpha=float[]; SkinSlot=int[]; SkinDiffuse=string[]; SkinNormal=string[]; SkinHasTint=bool[]; SkinTint=int[]; NodeName=string[]; NodeHasScale=bool[]; NodeScale=float[]; NodeHasPos=bool[]; NodePosX=float[]; NodePosY=float[]; NodePosZ=float[]; NodeHasRot=bool[]; NodeRotM0=float[]; NodeRotM1=float[]; NodeRotM2=float[]; NodeRotM3=float[]; NodeRotM4=float[]; NodeRotM5=float[]; NodeRotM6=float[]; NodeRotM7=float[]; NodeRotM8=float[]; NodeScaleMode=int[]."
        ],
        "runtime-script build" or "runtime-script package" or
            "runtime-script deploy" or "runtime-script inspect-vmad" =>
        [
            "The selected game binds the default apply script to NPCM_Manolov_ApplyFO4 or NPCM_Manolov_ApplySSE; static evidence, packaging, deployment, and inspection do not establish Papyrus execution."
        ],
        _ => []
    };
}
