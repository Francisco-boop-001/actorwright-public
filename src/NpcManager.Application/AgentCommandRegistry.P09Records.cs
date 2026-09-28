using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsP09RecordCommand(string name) =>
        P09RecordOptionCatalog.Contains(name);

    private static AgentCommandContract P09RecordContract(CommandDescriptor descriptor)
    {
        bool alias = descriptor.Name == "outfit create";
        CommandDescriptor effective = alias
            ? CommandCatalog.All.Single(item => item.Name == "outfit propose")
            : descriptor;
        ImmutableArray<AgentArtifactContract> inputs = P09Inputs(effective.Name);
        AgentCommandContract contract = Legacy(effective) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = P09Games(effective.Name),
            Limitations = effective.Limitations.AddRange(P09Limitations(effective.Name)),
            Options = LegacyOptions(effective.Name),
            OptionRelationships = P09Relationships(effective.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = P09Outputs(effective.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = P09Result(effective.Name),
            Effects = P09Effects(effective.Name),
            RetryPolicy = effective.Name == "outfit list"
                ? AgentRetryPolicy.SafeUnchanged
                : AgentRetryPolicy.RequiresFreshOutput,
            Determinism = AgentDeterminism.Deterministic,
            SupportsDryRun = false,
            Authority = P09Authority(effective.Name),
            Transitions = []
        };
        return alias
            ? contract with { Name = descriptor.Name, CanonicalCommand = effective.Name }
            : contract;
    }

    private static ImmutableArray<GameEdition> P09Games(string name) =>
        name is "armor damage-resist" or "material-swap propose" or "material-swap write"
            ? [GameEdition.Fallout4]
            : [GameEdition.Fallout4, GameEdition.SkyrimSpecialEdition];

    private static ImmutableArray<AgentOptionRelationshipContract> P09Relationships(string name)
    {
        var rows = ImmutableArray.CreateBuilder<AgentOptionRelationshipContract>();
        rows.Add(AtLeastOne(["edition", "game"]));
        if (name == "outfit list")
            rows.Add(AtLeastOne(["data-root", "plugin"]));
        if (name == "outfit propose")
        {
            rows.Add(P09Conditional(AgentOptionRelationshipKind.RequiredWhen,
                ["editor-id"], ["mode"], P09Active("mode", "new"),
                "New outfit proposals require --editor-id."));
            rows.Add(new(AgentOptionRelationshipKind.ForbiddenWhen,
                ["target-form", "target-form-id"],
                ["mode", "target-form", "target-form-id"],
                "New outfit proposals forbid both target FormID spellings from being absent." )
            {
                Trigger = new(AgentPredicateCombination.All,
                    [P09Active("mode", "new"), P09Missing("target-form"), P09Missing("target-form-id")])
            });
            rows.Add(P09ForbiddenForMode("editor-id", "override"));
            rows.Add(P09ForbiddenForMode("target-form", "override"));
            rows.Add(P09ForbiddenForMode("target-form-id", "override"));
        }
        if (name == "armor-addon propose")
            rows.Add(AtLeastOne(["patch", "models"]));
        return rows.ToImmutable();
    }

    private static AgentOptionRelationshipContract P09Conditional(
        AgentOptionRelationshipKind kind,
        ImmutableArray<string> options,
        ImmutableArray<string> referenced,
        AgentRelationshipPredicate predicate,
        string condition) =>
        new(kind, options, referenced, condition)
        {
            Trigger = new(AgentPredicateCombination.All, [predicate])
        };

    private static AgentOptionRelationshipContract P09ForbiddenForMode(
        string option, string mode) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, [option], [option, "mode"],
            $"--{option} is forbidden for {mode} outfit proposals.")
        {
            Trigger = new(AgentPredicateCombination.All,
                [P09Present(option), P09Active("mode", mode)])
        };

    private static AgentRelationshipPredicate P09Active(string option, params string[] values) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf,
            [.. values], false);

    private static AgentRelationshipPredicate P09Present(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf,
            ["__absent__"], false);

    private static AgentRelationshipPredicate P09Absent(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf,
            ["__absent__"], true);

    private static AgentRelationshipPredicate P09Missing(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf,
            ["__absent__"], true);

    private static AgentOptionRelationshipTrigger P09Trigger(
        AgentRelationshipPredicate predicate) =>
        new(AgentPredicateCombination.All, [predicate]);

    private static ImmutableArray<AgentArtifactContract> P09Inputs(string name) => name switch
    {
        "outfit list" =>
            [new("copied-data-load-order", [], "Explicit copied Data root and optional ordered plugin selection.")],
        "outfit propose" =>
            [new("outfit-source-and-items", [], "Copied source plugin plus inline ordered OTFT item references.")],
        "outfit write" =>
            [new("outfit-record-proposal", [], "Typed hash-bound OTFT proposal; artifact metadata is not a formal schema identifier.")],
        "leveled-list propose" =>
            [new("leveled-list-source-and-inline-entries", [], "Copied source plugin plus strict inline LVLI entry JSON.")],
        "leveled-list resolve" or "leveled-list write" =>
            [new("leveled-list-record-proposal", [], "Typed hash-bound LVLI proposal; artifact metadata is not a formal schema identifier.")],
        "armor propose" =>
            [new("armor-source-and-patch", [], "Copied source plugin plus exact case-sensitive inline or @file ARMO patch JSON.")],
        "armor damage-resist" =>
            [new("armor-source-and-damage-resist", [], "Copied Fallout 4 source plugin plus exact case-sensitive damage-resist JSON.")],
        "armor-addon propose" =>
        [
            new("armor-addon-source-plugin", [], "Copied source plugin for the selected branch."),
            new AgentArtifactContract("armor-addon-patch", [],
                "ARMA record patch JSON used only when --models is absent.")
                { Trigger = P09Trigger(P09Absent("models")) },
            new AgentArtifactContract("armor-addon-model-entries", [],
                "Ordered ARMO addon-entry JSON selected by --models presence.")
                { Trigger = P09Trigger(P09Present("models")) }
        ],
        "armor write" =>
            [new("armor-record-proposal", [], "Typed hash-bound ARMO proposal; artifact metadata is not a formal schema identifier.")],
        "armor-addon write" =>
            [new("armor-addon-record-proposal", [], "Typed hash-bound ARMA record proposal; model proposals are not accepted by this writer.")],
        "material-swap propose" =>
            [new("material-swap-source-and-patch", [], "Copied Fallout 4 source plugin plus exact case-sensitive MSWP patch JSON.")],
        "material-swap write" =>
            [new("material-swap-record-proposal", [], "Typed hash-bound Fallout 4 MSWP proposal; artifact metadata is not a formal schema identifier.")],
        _ => []
    };

    private static ImmutableArray<AgentArtifactContract> P09Outputs(string name) => name switch
    {
        "outfit propose" => [new("outfit-record-proposal", [], "Fresh hash-bound OTFT proposal.")],
        "outfit write" => [new("outfit-plugin", [], "Fresh ordinary plugin containing exactly one materialized OTFT record.")],
        "leveled-list propose" => [new("leveled-list-record-proposal", [], "Fresh hash-bound LVLI proposal.")],
        "leveled-list resolve" => [new("leveled-list-resolution", [], "Fresh deterministic seed-bound static resolution.")],
        "leveled-list write" => [new("leveled-list-plugin", [], "Fresh ordinary plugin containing exactly one materialized LVLI record.")],
        "armor propose" => [new("armor-record-proposal", [], "Fresh hash-bound ARMO proposal.")],
        "armor damage-resist" => [new("armor-damage-resistance-proposal", [], "Fresh Fallout 4 ARMO damage-resistance proposal.")],
        "armor-addon propose" =>
        [
            new AgentArtifactContract("armor-addon-record-proposal", [],
                "Fresh hash-bound ARMA record proposal when --models is absent.")
                { Trigger = P09Trigger(P09Absent("models")) },
            new AgentArtifactContract("armor-addon-model-entries-proposal", [],
                "Fresh ordered ARMO addon-entry proposal when --models is present.")
                { Trigger = P09Trigger(P09Present("models")) }
        ],
        "armor write" => [new("armor-plugin", [], "Fresh ordinary plugin containing exactly one materialized ARMO record.")],
        "armor-addon write" => [new("armor-addon-plugin", [], "Fresh ordinary plugin containing exactly one materialized ARMA record.")],
        "material-swap propose" => [new("material-swap-record-proposal", [], "Fresh hash-bound Fallout 4 MSWP proposal.")],
        "material-swap write" => [new("material-swap-plugin", [], "Fresh ordinary plugin containing exactly one materialized MSWP record.")],
        _ => []
    };

    private static ImmutableArray<AgentEffectContract> P09Effects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace,
            "After exact K-local input admission.", "workspace"));
        if (name == "armor-addon propose")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Fresh ARMA record proposal only when --models is absent.", "k-local-output")
                { Trigger = P09Trigger(P09Absent("models")) });
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Fresh ARMO addon-entry proposal whenever --models is present.", "k-local-output")
                { Trigger = P09Trigger(P09Present("models")) });
        }
        else if (name != "outfit list")
        {
            effects.Add(new(AgentEffectKind.WriteNewArtifact,
                "Only after validation succeeds; the destination must be fresh.",
                "k-local-output"));
        }
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static ImmutableArray<AgentAuthorityContract> P09Authority(string name)
    {
        bool readOnly = name == "outfit list";
        bool writer = name is "outfit write" or "leveled-list write" or
            "armor write" or "armor-addon write" or "material-swap write";
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established,
                "Exact V1 options, branches, and K-local inputs are admitted before work begins."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established,
                "Explicit source paths, plugin order, record identity, or proposal hashes bind the selected source."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                readOnly ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Established,
                readOnly ? "This command is read-only." : "Fresh output is deterministically derived from admitted inputs; leveled-list resolution is additionally seed-bound."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                readOnly || writer ? AgentAuthorityState.Established : AgentAuthorityState.Required,
                writer ? "The binary writer independently reads back the emitted record." :
                readOnly ? "The typed read result is the bounded static inspection." :
                "A separate static verification phase remains required for this proposal artifact."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable,
                "This family emits no off-engine visual preview."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required,
                "Human visual acceptance is outside this static record workflow."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required,
                "Proposal, resolution, and binary readback evidence is not game-runtime proof."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required,
                "Promotion requires separate human approval.")
        ];
    }

    private static string P09Result(string name) => name switch
    {
        "outfit list" => "Edition, deterministic candidate rows, source/override-chain provenance, item references, and diagnostics from an explicit copied Data root with either an explicit plugin order or deterministic filename ordering; no live-profile, visual, runtime, or formal result-schema claim is published.",
        "outfit propose" => "Written state, typed hash-bound OTFT proposal mode/source/target/editor identity, ordered items, master dependencies, input/output hashes, no-unrelated-records claim, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.",
        "outfit write" => "Written state, fresh ordinary-plugin path, target OTFT FormID, output SHA-256, and diagnostics after independent binary readback; static verification is not game-runtime or promotion authority.",
        "leveled-list propose" => "Written state, typed hash-bound LVLI proposal fields, source/input/output hashes, ordered entries, master dependencies, no-unrelated-records claim, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.",
        "leveled-list resolve" => "Written state, source proposal/hash, signed seed, list chance roll/suppression, ordered selections and resolved items, and diagnostics; the seed makes this static preview resolution deterministic without level-gate, binary, visual, or runtime proof.",
        "leveled-list write" => "Written state, fresh ordinary-plugin path, target LVLI FormID, output SHA-256, and diagnostics after independent binary readback; Skyrim SE requires maxCount and every entry chanceNone to be zero, and static verification is not runtime or promotion authority.",
        "armor propose" => "Written state, typed hash-bound ARMO proposal mode/source/editor identity, input/patch hashes, changed fields, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.",
        "armor damage-resist" => "Written state, Fallout 4 source/input identity, typed unique DMGT/value rows, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.",
        "armor-addon propose" => "Written state and diagnostics for exactly one branch: models presence wins and returns an ordered armor-addon-model proposal; otherwise patch returns a typed ARMA record proposal with source/input/patch identity and changed fields. Neither artifact metadata value is a formal schema identifier.",
        "armor write" => "Written state, fresh ordinary-plugin path, target ARMO FormID, output SHA-256, and diagnostics after independent binary readback; static verification is not game-runtime or promotion authority.",
        "armor-addon write" => "Written state, fresh ordinary-plugin path, target ARMA FormID, output SHA-256, and diagnostics after independent binary readback; only the record-proposal branch is accepted, and static verification is not runtime or promotion authority.",
        "material-swap propose" => "Written state, Fallout 4 typed hash-bound MSWP proposal mode/source/editor identity, input/patch hashes, ordered entries, and diagnostics; artifact kind/schemaVersion metadata is not a formal schema identifier.",
        "material-swap write" => "Written state, fresh Fallout 4 ordinary-plugin path, target MSWP FormID, output SHA-256, and diagnostics after independent binary readback; static verification is not game-runtime or promotion authority.",
        _ => throw new InvalidOperationException($"Unknown P09 record command: {name}")
    };

    private static ImmutableArray<string> P09Limitations(string name) => name switch
    {
        "outfit propose" =>
        [
            "Outfit items contain 1..4096 unique, non-null ARMO/LVLI FormReferences provided by the source plugin or its masters. New mode requires editor-id and one nonzero plugin-local 24-bit target spelling; override inherits a nonempty source EditorID and forbids editor-id and either target spelling.",
            "Optional actor-race is Skyrim-only and resolves direct ARMO armatures against copied providers beside the source. Each ARMA must admit the race through its primary or additional list. Missing records, leveled items, or excluded races refuse. This command does not clone records; Finish Core request outfitRacePolicy=clone reviews and materializes output-owned ARMA, ARMO, and OTFT. Omission retains legacy proposal behavior."
        ],
        "leveled-list propose" =>
        [
            "Leveled-list entries are a strict no-comments/no-trailing-commas JSON array (maximum depth 8) containing 1..4096 case-sensitive {item,level,count,chanceNone} objects: item is a non-null source/master FormReference, level/count are nonzero uint16, and chanceNone is 0..100. List chance-none is 0..100; max-count is uint8; flags are exact true|false."
        ],
        "leveled-list resolve" =>
        [
            "Resolution accepts at most 8 MiB of duplicate-free schemaVersion 1, artifactKind leveled-list-record-proposal JSON, requires current source-plugin hash and matching edition, validates 1..4096 entries and chance ranges, and refuses any realization exceeding 100000 selections. Seed, ChanceNone, UseAll, and CalculateEachInCount determine output; player level and CalculateAllLevels are preserved but not gate-applied."
        ],
        "armor propose" =>
        [
            "Strict duplicate-free case-sensitive armor patch object (no comments/trailing commas, maximum depth 12; @file at most 65536 bytes): mode=new|override; optional editorId, targetFormId, name, slotMask, race, maleWorldModel, femaleWorldModel, value, weight, health, armorRating, keywords, armorAddons[{index:uint16,addon:Plugin|FormID}], description, nonPlayable, enchantment, pickupSound, dropSound, equipmentType, alternateBlockMaterial, templateArmor, objectBounds{minimumX,minimumY,minimumZ,maximumX,maximumY,maximumZ}, completeDocument. New requires editorId and nonzero plugin-local 24-bit targetFormId; override forbids editorId and any targetFormId must equal source. At least one field must change. Names/descriptions are at most 4096 and descriptions allow only tab/newline controls; models are safe .nif paths; value is Skyrim uint32 or Fallout int32; weight is finite 0..1000000; rating is finite 0..65535 and integral in Fallout 4; health is Fallout 4-only; extended fields and completeDocument are Skyrim-only. Keywords are at most 255 unique non-null references; armorAddons are at most 255 non-null references and complete Skyrim rows use index 0. Complete Skyrim documents initialize name, slotMask, race, value, weight, armorRating, keywords, armorAddons, description, nonPlayable, and objectBounds. objectBounds has exactly six int16 properties whose minima do not exceed maxima; all references must resolve to source or masters."
        ],
        "armor damage-resist" =>
        [
            "Fallout 4 damage-resist JSON is a strict no-comments/no-trailing-commas array (maximum depth 8; @file at most 65536 bytes) containing 1..4096 duplicate-free case-sensitive {damageType:Plugin|FormID,value:uint32} objects; damageType references are unique, non-null, and provided by the source plugin or its masters."
        ],
        "armor-addon propose" =>
        [
            "Strict duplicate-free case-sensitive armor-addon patch object (no comments/trailing commas, maximum depth 16; @file at most 131072 bytes): mode=new|override; optional editorId, targetFormId, slotMask:uint32, race, footstepSet, malePriority:uint8, femalePriority:uint8, maleWeightSliderFlags:uint8, femaleWeightSliderFlags:uint8, detectionSound:uint8, weaponAdjust, maleModel, femaleModel, maleFirstPersonModel, femaleFirstPersonModel, maleModelFlags:uint8, femaleModelFlags:uint8, maleColorRemapIndex, femaleColorRemapIndex, maleSkinTexture, femaleSkinTexture, maleSkinTextureSwapList, femaleSkinTextureSwapList, maleMaterialSwap, femaleMaterialSwap, maleFirstPersonMaterialSwap, femaleFirstPersonMaterialSwap, artObject, additionalRaces, sculpt[{gender:uint8,bone,x,y,z}], noUnderarmorScaling, hasSculptData, hiResFirstPersonOnly. New requires editorId and nonzero plugin-local 24-bit targetFormId; override forbids editorId and any targetFormId must equal source. At least one field must change. Model paths are safe assets; weaponAdjust is finite -100000..100000; color-remap indexes are finite 0..255. Additional races are unique non-null references. Sculpt has at most 4096 duplicate-free-property rows with gender 0|1, nonempty bone names at most 256 characters, and finite x/y/z in -100..100. Fallout-only fields are refused for Skyrim; all references must resolve to source or masters.",
            "Case-sensitive models JSON is a strict no-comments/no-trailing-commas array (maximum depth 8; @file at most 65536 bytes) containing 1..4096 duplicate-free-property {index:uint16,addon:Plugin|FormID} objects; indexes are unique, addon references are non-null and provided by source or masters, and Skyrim requires every index to be 0. --models presence selects this branch and takes precedence over --patch."
        ],
        "material-swap propose" =>
        [
            "Fallout 4 strict duplicate-free case-sensitive material-swap patch object (no comments/trailing commas, maximum depth 12; @file at most 131072 bytes): mode=new|override; optional editorId, targetFormId, treeFolder; required entries array of 1..4096 duplicate-free-property {originalMaterial,replacementMaterial,colorRemapIndex,treeFolder} objects. New requires editorId and nonzero plugin-local 24-bit targetFormId; override forbids editorId and any targetFormId must equal source. Each entry supplies at least originalMaterial or replacementMaterial as a safe asset path; colorRemapIndex is finite 0..1; treeFolder values are at most 4096 characters without controls."
        ],
        _ => []
    };
}
