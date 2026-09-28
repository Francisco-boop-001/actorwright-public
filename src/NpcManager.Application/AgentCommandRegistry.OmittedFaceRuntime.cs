using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static bool IsOmittedFaceRuntimeCommand(string name) =>
        OmittedFaceRuntimeOptionCatalog.Contains(name);

    private static AgentCommandContract OmittedFaceRuntimeContract(CommandDescriptor descriptor)
    {
        ImmutableArray<AgentArtifactContract> inputs = OfrInputs(descriptor.Name);
        return Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = OfrGames(descriptor.Name),
            Limitations = descriptor.Limitations.AddRange(OfrLimitations(descriptor.Name)),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = OfrRelationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = OfrOutputs(descriptor.Name),
            ResultSchemaIds = [],
            ResultShape = "object",
            ResultDescription = OfrResult(descriptor.Name),
            Effects = OfrEffects(descriptor.Name),
            RetryPolicy = OfrRetry(descriptor.Name),
            Determinism = descriptor.Name.StartsWith("runtime smoke ", StringComparison.Ordinal)
                ? AgentDeterminism.EnvironmentDependent
                : AgentDeterminism.Deterministic,
            SupportsDryRun = false,
            Authority = OfrAuthority(descriptor.Name),
            Transitions = []
        };
    }

    private static ImmutableArray<GameEdition> OfrGames(string name) => name switch
    {
        "face morph patch" or "face morph extended" or "face sculpt patch" =>
            [GameEdition.SkyrimSpecialEdition],
        _ => [GameEdition.Fallout4, GameEdition.SkyrimSpecialEdition]
    };

    private static ImmutableArray<AgentOptionRelationshipContract> OfrRelationships(string name)
    {
        if (name == "runtime smoke verify") return [AtLeastOne(["edition", "game"])];
        if (name is "animation list" or "animation tree" or "face pose resolve")
            return [AtLeastOne(["edition", "game"])];
        if (name == "pipeline preset-to-npc")
            return
            [
                AtLeastOne(["edition", "game"]),
                OfrForbiddenPair("looksmenu", "skyrimse"),
                OfrForbiddenPair("racemenu-jslot", "fallout4")
            ];
        if (name == "runtime smoke verify-all") return [];

        var rows = ImmutableArray.CreateBuilder<AgentOptionRelationshipContract>();
        rows.Add(AtLeastOne(["edition", "game"]));
        if (name is "face tint patch" or "face morph patch" or "face reset")
            rows.Add(AtLeastOne(["npc", "form-id"]));
        if (name == "face morph patch")
            rows.Add(AtLeastOne(["vanilla", "morphs"]));
        rows.Add(new AgentOptionRelationshipContract(
            AgentOptionRelationshipKind.ForbiddenWhen,
            ["dry-run", "apply"], ["dry-run", "apply"],
            "Only simultaneously active true or 1 values conflict.")
        {
            Trigger = new(AgentPredicateCombination.All,
            [
                OfrActive("dry-run", "true", "1"),
                OfrActive("apply", "true", "1")
            ])
        });
        rows.Add(new AgentOptionRelationshipContract(
            AgentOptionRelationshipKind.RequiredWhen,
            ["expected-sha256"], ["apply"],
            "Active apply requires the exact expected source hash.")
        {
            Trigger = new(AgentPredicateCombination.All,
                [OfrActive("apply", "true", "1")])
        });
        return rows.ToImmutable();
    }

    private static AgentOptionRelationshipContract OfrForbiddenPair(string format, string edition) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen,
            ["format", "edition"], ["format", "edition"],
            $"Preset format {format} is incompatible with {edition}.")
        {
            Trigger = new(AgentPredicateCombination.All,
            [
                OfrActive("format", format),
                OfrActive("edition", edition)
            ])
        };

    private static AgentRelationshipPredicate OfrActive(string option, params string[] values) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf,
            [.. values], false);

    private static AgentRelationshipPredicate OfrPresent(string option) =>
        new(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf,
            ["__absent__"], false);

    private static ImmutableArray<AgentArtifactContract> OfrInputs(string name) => name switch
    {
        "face tint patch" or "face morph patch" =>
            [new("face-patch-source", [], "Existing copied plugin plus inline or K-local JSON patch payload.")],
        "face morph extended" or "face sculpt patch" =>
            [new("racemenu-preset", [], "Existing RaceMenu JSlot plus inline or K-local JSON patch payload.")],
        "face reset" =>
            [new("face-reset-current-and-baseline", [], "Existing current and construction-baseline face snapshots.")],
        "face pose resolve" =>
            [new("face-pose-preset", [], "Closed schema-version-1 face-pose interchange JSON; its numeric version is not a formal schema identifier.")],
        "animation list" or "animation tree" =>
            [new("preview-animation-manifest", [], "Existing preview manifest containing animation rows; in-memory artifactKind values are not formal schema identifiers.")],
        "runtime smoke verify" =>
        [
            new("runtime-smoke-report", [], "Operator-supplied schemaVersion-1 report for the selected game; no formal schema identifier is registered."),
            new("package-acceptance-report", [], "Package archive hash/status/non-runtime acceptance report; no formal schema identifier is registered.")
        ],
        "runtime smoke verify-all" =>
        [
            new("fallout4-runtime-smoke-report", [], "Fallout 4 schemaVersion-1 runtime-smoke report."),
            new("skyrimse-runtime-smoke-report", [], "Skyrim SE schemaVersion-1 runtime-smoke report."),
            new("package-acceptance-report", [], "Single package acceptance report shared by both game reports.")
        ],
        "pipeline preset-to-npc" =>
        [
            new("preset-and-source-plugin", [], "Exact selected preset and copied source plugin."),
            OfrOptional("optional-facegeom-evidence", "facegeom-manifest", "Optional FaceGeom evidence."),
            OfrOptional("optional-facetint-evidence", "facetint-manifest", "Optional FaceTint evidence."),
            OfrOptional("optional-runtime-script-build", "runtime-script-build", "Optional static runtime-script build evidence."),
            OfrOptional("optional-runtime-script-package", "runtime-script-package", "Optional hash-bound apply-PEX package.")
        ],
        _ => []
    };

    private static AgentArtifactContract OfrOptional(string kind, string option, string description) =>
        new(kind, [], description)
        {
            Trigger = new(AgentPredicateCombination.All, [OfrPresent(option)])
        };

    private static ImmutableArray<AgentArtifactContract> OfrOutputs(string name)
    {
        if (name is "face pose resolve" or "animation list" or "animation tree" or
            "runtime smoke verify" or "runtime smoke verify-all") return [];
        if (name == "pipeline preset-to-npc")
            return [new("preset-to-npc-package", [], "Fresh plugin/package artifacts and root npcmanager-package.json manifest.")];
        return
        [
            new AgentArtifactContract("face-patch-proposal", [], "Optional fresh persisted proposal; no formal schema identifier is published.")
                { Trigger = new(AgentPredicateCombination.All, [OfrPresent("proposal")]) },
            new AgentArtifactContract("face-patch-output", [], "Fresh active-apply plugin or preset/snapshot output.")
                { Trigger = new(AgentPredicateCombination.All, [OfrActive("apply", "true", "1")]) }
        ];
    }

    private static ImmutableArray<AgentEffectContract> OfrEffects(string name)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        effects.Add(new(AgentEffectKind.ReadWorkspace,
            "When exact K-local inputs are admitted.", "workspace"));
        if (name == "pipeline preset-to-npc")
            effects.Add(new(AgentEffectKind.WriteNewArtifact,
                "After full validation, to fresh package outputs only.", "k-local-output"));
        else if (name is not ("face pose resolve" or "animation list" or "animation tree" or
                     "runtime smoke verify" or "runtime smoke verify-all"))
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "When --proposal names a fresh persisted proposal.", "k-local-output")
            {
                Trigger = new(AgentPredicateCombination.All, [OfrPresent("proposal")])
            });
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact,
                "Only when --apply is exactly true or 1 and all validation succeeds.", "k-local-output")
            {
                Trigger = new(AgentPredicateCombination.All, [OfrActive("apply", "true", "1")])
            });
        }
        if (name == "face tint patch")
            effects.Add(new(AgentEffectKind.AppendLocalOperationJournal,
                "When Skyrim SE input validation refuses the command, append a redacted usage record.", JournalScope));
        return effects.ToImmutable();
    }

    private static AgentRetryPolicy OfrRetry(string name) => name switch
    {
        "face pose resolve" or "animation list" or "animation tree" or
            "runtime smoke verify" or "runtime smoke verify-all" => AgentRetryPolicy.SafeUnchanged,
        "pipeline preset-to-npc" => AgentRetryPolicy.RequiresFreshOutput,
        _ => AgentRetryPolicy.RequiresReanalysis
    };

    private static ImmutableArray<AgentAuthorityContract> OfrAuthority(string name)
    {
        bool read = name is "face pose resolve" or "animation list" or "animation tree";
        bool smoke = name.StartsWith("runtime smoke ", StringComparison.Ordinal);
        bool pipeline = name == "pipeline preset-to-npc";
        return
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established,
                "Exact options and K-local inputs are admitted before execution."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity,
                !read && !smoke && !pipeline ? AgentAuthorityState.Required : AgentAuthorityState.Established,
                !read && !smoke && !pipeline
                    ? "Analysis computes source identity, but only active apply requires a caller-supplied expected hash."
                    : "Selected source bytes, hashes, edition, and target identity remain explicit."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                read || smoke ? AgentAuthorityState.NotApplicable : pipeline ? AgentAuthorityState.Established : AgentAuthorityState.Required,
                read || smoke ? "This command writes no artifact." : pipeline ? "Fresh package outputs are derived from admitted inputs." : "Only active apply materializes final output; analysis may stay in memory."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                read || smoke ? AgentAuthorityState.Established : AgentAuthorityState.Required,
                read ? "The bounded typed read result is the static inspection." : smoke ? "The report structure, identities, hashes, environment, and image signatures are checked." : "A distinct complete static verification remains required."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable,
                "No command in this family renders an off-engine preview."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance,
                read ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Required,
                read ? "This read-only calculation emits no visual acceptance surface."
                    : "Structured and image-signature checks do not establish human visual acceptance."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification,
                smoke ? AgentAuthorityState.Established : AgentAuthorityState.Required,
                smoke ? "Valid operator-supplied runtime evidence establishes this bounded runtime-smoke gate; the command does not run the game." : "Static patching or packaging is not game-runtime proof."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval,
                read ? AgentAuthorityState.NotApplicable : AgentAuthorityState.Required,
                read ? "Read-only calculation grants no promotion decision." : "Runtime evidence and static outputs never grant promotion approval.")
        ];
    }

    private static ImmutableArray<string> OfrLimitations(string name) => name switch
    {
        "face tint patch" =>
        [
            "--layers is inline or @existing-K-local-file JSON: at most 1 MiB, maximum depth 16, comments/trailing commas refused, root array at most 256. Fallout 4 rows are case-sensitive duplicate-free closed objects with dataType,optionIndex,value,color,templateColorIndex,rawTendBase64: dataType aliases value-color|valuecolor|palette and texture-set|textureset; optionIndex is unique UInt16; value is 0..100; raw base64 text is at most 64 characters and decodes to exactly 1, 5, or 7 bytes. Texture-set requires raw and forbids color/template; value-color effective length >=5 requires color and <5 forbids it, >=7 requires templateColorIndex and <7 forbids it; color is a closed duplicate-free red,green,blue byte object and templateColorIndex is -1..32767. Skyrim SE rows are case-sensitive duplicate-free closed index,red,green,blue,alpha,coverage,presetIndex objects: index is unique UInt16, color/alpha are bytes, coverage is 0..100, presetIndex is Int16, and texture fields are explicitly refused.",
            OfrMixedMode
        ],
        "face morph patch" =>
        [
            "Canonical --vanilla wins over --morphs; both are optional aliases but at least one is required. The selected inline or @existing-K-local-file JSON is at most 1 MiB, maximum depth 8, comments/trailing commas refused, with one case-sensitive duplicate-free closed root containing exactly nam9,nam9Trailing,nama. Skyrim service validation requires exactly 18 finite nam9 numbers in [-1,1], one finite nam9Trailing number, and exactly four UInt32 nama integers.",
            OfrMixedMode
        ],
        "face morph extended" =>
        [
            "--extended is inline or @existing-K-local-file JSON: at most 1 MiB, maximum depth 8, comments/trailing commas refused, with a case-sensitive duplicate-free closed root containing only morphs. morphs has at most 2048 entries; every entry requires string name and finite numeric value, while unknown and duplicate entry members are tolerated by current last-property lookup. Service validation requires a nonempty printable name at most 256 characters, names unique case-insensitively, and finite values in [-1,1]; absolute values below 0.0001 remove an existing morph, and uncatalogued nonzero names are admitted with a warning.",
            OfrMixedMode
        ],
        "face sculpt patch" =>
        [
            "--sculpt is inline or @existing-K-local-file JSON: at most 16 MiB, maximum depth 16, comments/trailing commas refused. Case-sensitive closed fields are root divisor,parts; part host,vertices,verts; vertex index,dx,dy,dz; duplicate allowed fields are tolerated by current last-property lookup. Service validation requires divisor 1..1000000, at most 64 parts, printable nonempty host at most 512 characters, host vertex count 1..2147483647, 1..200000 vertices per part, unique indices in 0..hostVertexCount-1, finite deltas in [-1000,1000], and every delta times divisor within signed Int32.",
            OfrMixedMode
        ],
        "face reset" =>
        [
            "Face reset snapshots are existing .face.json files at most 4 MiB, no comments/trailing commas, depth at most 64, object/array collections at most 8192, duplicate keys refused case-insensitively, and UTF-8 BOM accepted. Root lookup is case-insensitive and requires schemaVersion 1 plus matching game and npcFormId; unknown root values are preserved, and any recognized section must be an object or array. Fallout 4 sections are face-parts,tints,vertex-morphs,bone-regions; Skyrim SE sections are face-parts,skyrim-morphs,skyrim-tints. Current, baseline, and fresh output are distinct same-filename paths; reset replaces only the selected section, active apply is current/baseline/proposal/hash bound, and never overwrites output or proposal artifacts.",
            OfrMixedMode + " Canonical --npc wins over --form-id; section support is game-specific and the service rejects invalid section/game pairs."
        ],
        "face pose resolve" =>
        ["Face-pose JSON accepts a UTF-8 BOM but refuses comments/trailing commas; it is at most 4 MiB and depth 16. Every object is case-sensitive, duplicate-free, closed, and requires exactly its listed fields: root version,game,npc,facialMorphIntensity,regions,faceMorphs,vertexMorphs; region id,name,default,bones; transform position,rotation,scale; bone bone,min,max; face morph regionId,position,rotation,scale; vertex morph resolver,name,weight,vertices; vertex index,delta. version is exactly 1; game is fallout4|skyrimse and npc a matching nonzero FormID; strings are printable/nonempty with game/npc at most 32 and names at most 256; intensity and face-morph scale are finite [-100,100]. regions has 1..4096 unique ids in 0..1000000; each has 1..4096 case-insensitively unique bones and total bones <=100000; all vectors contain exactly three finite values in [-100000,100000]. faceMorphs may be empty and has at most 4096 rows; unresolved regionId rows are skipped with warnings. vertexMorphs may be empty and has at most 2048 channels; weight is finite [-1,1], each channel and the total have at most 200000 vertices, with unique indices per channel in 0..10000000. Resolution is read-only and writes no persistent artifact."],
        "animation list" or "animation tree" =>
        ["--female and --first-person accept only Boolean text true or false; 1 is refused. --filter is at most 256 characters, contains no controls, and must already be trimmed. Returned schemaVersion and in-memory artifactKind values describe the result object but are not formal schema identifiers. Unknown command options and positional tokens retain legacy parser behavior."],
        "runtime smoke verify" =>
        ["The legacy projection supports --edition/--game for fallout4 and skyrimse. Runtime and package-acceptance JSON are independently supplied dialects, each at most 2 MiB; required screenshots differ by game and are bounded to 64 MiB. The validator checks identities, hashes, environment files, and image signatures but does not judge pixels or run the game. Unknown/duplicate JSON members and positional tokens retain permissive legacy behavior."],
        "runtime smoke verify-all" =>
        ["Distinct three-report dialect: one Fallout 4 runtime report, one Skyrim SE runtime report, and one shared package-acceptance report. Each game report is validated through the single-game service; no aggregate document schema identifier is published, screenshot pixels are not judged, and permissive unknown/duplicate JSON members remain unchanged."],
        "pipeline preset-to-npc" =>
        ["Exact pairs are looksmenu+fallout4 and racemenu-jslot+skyrimse. The K-local output root must already exist; it is not required to be empty or fresh. The output plugin and root npcmanager-package.json must be absent, and generated FaceGen files, copied runtime-script evidence, runtime-test-instructions.json, and other generated artifact destinations use no-overwrite writes; optional runtime-script deployment may report an identical artifact already present. Static composition and hash binding do not establish human visual acceptance, game runtime, or promotion approval; unknown options and positional tokens retain legacy parser behavior."],
        _ => []
    };

    private const string OfrMixedMode =
        "--game wins over --edition. Only true or 1 activates --dry-run/--apply; inactive supplied values do not conflict. Active apply requires --expected-sha256. Proposal persistence is conditional on --proposal and final output is conditional on active apply.";

    private static string OfrResult(string name) => name switch
    {
        "face tint patch" or "face morph patch" or "face morph extended" or
            "face sculpt patch" or "face reset" =>
            "Applicable/applied state, selected edition/source/target, input and optional output hashes, typed patch or section, changes, preserved fields, and diagnostics; no formal result schema, preview, runtime, or promotion claim is published.",
        "face pose resolve" => "Resolved state, edition/NPC/source hash, ordered bone poses, vertex deltas, applied channels, combination order, and diagnostics; the result remains in-memory and no persistent artifact or formal result schema is published.",
        "animation list" => "Succeeded state, typed in-memory animation-list artifact and diagnostics after deterministic manifest filtering; artifactKind/schemaVersion values are not formal schema identifiers and no persistent output is written.",
        "animation tree" => "Succeeded state, typed in-memory animation-tree hierarchy and diagnostics after deterministic manifest filtering; artifactKind/schemaVersion values are not formal schema identifiers and no persistent output is written.",
        "runtime smoke verify" => "Validated runtime evidence state, report schemaVersion text, edition, report/acceptance paths, validated screenshot role names, and diagnostics; the command admits operator evidence but does not run the game or judge pixels.",
        "runtime smoke verify-all" => "Aggregate validity, complete Fallout 4 and Skyrim SE single-game results, and combined diagnostics for the distinct three-report dialect; no aggregate artifact or formal result schema is published.",
        "pipeline preset-to-npc" => "Completed state, manifest path/hash, output plugin, BodyGen files, package artifacts and diagnostics from fresh bounded composition; static package creation is not visual, game-runtime, or promotion authority.",
        _ => throw new InvalidOperationException($"Unknown omitted face/runtime command: {name}")
    };
}
