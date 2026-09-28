# CLI contract v1

The executable is `actorwright`. Every command supports `--help` and `--json`.
JSON uses camelCase, explicit schema version `1`, invariant strings, and stable
exit codes:

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | General failure |
| 2 | Usage or schema error |
| 3 | Security refusal |
| 4 | Domain validation failure |
| 5 | Cancellation |

## Protocol selection

An omitted `--protocol` and exact ordinal `--protocol 1` both select the
existing legacy dispatcher. Exact ordinal `--protocol 2` selects the strict
typed dispatcher; every other supplied value is unsupported. The protocol
selector is global transport metadata and is not forwarded as a legacy command
option. Protocol 1 retains the established no-flag behavior and exhaustive
current 142-command surface. The frozen Preview.272 package remains complete at
136 commands. Exact `--protocol 1` requires preview.266 or later;
omit the flag for older binaries. There is no protocol 3.

Protocol-2 capabilities are readiness authority for the exact executable.
Discovery of a command or its schema does not make a `readiness: "legacy"`
command callable through protocol 2. Strict protocol 2 refuses such a call
with `protocol-command-legacy` before legacy dispatch. Discovery grants no
mutation approval, visual authority, runtime authority, or promotion approval.

M1 exposes `version`, `capabilities`, `diagnose`, `workspace preflight`,
`schema export`, and `gui` shell commands. Later milestones add the
feature-ledger commands. A
write-capable command must always accept explicit `--output`, `--dry-run`, and
hash-bound proposal options; it may never infer a live Data root from saved
configuration.

The CLI and desktop shell call the same application use cases. The desktop
shell never shells out to the CLI, and the CLI never contains binary format or
mutation logic.

## Command inventory authority

`capabilities --json` is the exhaustive machine-readable command inventory for
the exact executable being run. Registered provisional `1.0.0-preview.227`
reports 136 unique catalogued commands, including `npc create-from-jslot`,
`preview npc`, five `facegen hair-regions` phases, static `npc assembly
preflight`, the three Finish Core commands, and the three interior-placement
commands. Direct packaged comparison proves every one of `preview.226`'s 133
command names remains and only `npc placement interior analyze`, `npc placement
interior apply`, and `npc placement interior verify` are added.
Preserved `preview.224` remains immutable
at 122 catalogued
commands; its already-operational `npc create-from-jslot` route predates the
catalogue correction. Historical `preview.222` reports 123 commands and lacks
that correction even though it contains the first viewer implementation.

The exact previous feature source is preserved on branch
`codex/npcmanager-dual-tone-hair` at packaged commit
`4b097b3d9c7c945a5396c5137bbb9a9beb17ceb8`. The registered packaged CLI is
the current command authority; preview.227 records the captured K-local
working-tree source snapshot based on `solveig-build` commit
`64ce5c524fc18e4dfb5475e7e17c806035f53c11`.
`version --json`
reports internal executable version `0.1.0-m1` separately from that source
line. Registration does not grant visual or Skyrim runtime authority.

The historical 101-leaf feature ledger is an implementation/evidence taxonomy,
not a command count. See `docs/product-capabilities.md` for the registered
release, source-candidate boundary, command-family counts, route authorities,
and fresh-context startup order.

## Compatibility firewall

The public `publicSynthetic` v2 baseline freezes the ordered 142-command
protocol-1 surface and two structured package journeys. Source checks the
source-built command contract and its selectors; Package checks the staged CLI
against the source and baseline. Full includes Source checks and independently
verifies the sealed release and package, voice probes, complete standalone
selector matrix, both journeys (create-to-package and Wave B), and its other
Full-only gates. It does not consume or require a prior Package-tier report.
All 142 commands remain required, including `npc voice
discover|import|synthesize` and `npc dialogue analyze|apply|verify`. The
voice/dialogue selectors remain mandatory; a matching command count alone does
not establish behavior compatibility.

`tools/verification/compatibility-firewall.ps1 verify` accepts `-Tier Source`,
`Package`, or `Full`, with `-Baseline publicSynthetic`, a source-built Release
CLI (`-SourceCli`), and a fresh absolute `-Output` report file directly under
the repository's `artifacts/test-work`. Package additionally requires the
staged `-ReleaseRoot`; Full requires both `-ReleaseRoot` and the deterministic
`-ReleaseZip`. See `docs/build.md` for runnable examples and the build/release
order. The legacy Preview.275 v1 validator remains available for historical
sealed-release verification, but its private raw journey evidence is not part
of the public baseline registry. `capture-synthetic` builds a fresh private-only
candidate through the ordinary package script and emits a separate v2 snapshot;
normal verification never rewrites a baseline.

Only `PASS` satisfies a required gate. A successful report uses
`SOURCE_COMPATIBLE`, `PACKAGE_COMPATIBLE`, or `FULL_COMPATIBLE` for its tier;
otherwise the verdict is `INCOMPATIBLE` or the command fails before producing a
report. Reports record gate evidence and differences. Current fixture-backed
tests cover Candidate, Admission, and Activation logic; this producer `verify`
command does not expose those tiers. Test reports do not prove a real consumer
install, Skyrim runtime, or visual result. A future real Candidate, Admission,
or Activation proceeds through its separate approval and consumer workflow.
The firewall never installs, elevates, activates, or promotes a release, and it
adds no per-step approval to CLI use.

## Custom NPC voice and dialogue (Preview.273 source)

Preview.273 adds six protocol-1 commands. They remain `legacy` in the
protocol-2 readiness registry:

```text
actorwright npc voice discover --endpoint <loopback-url> --output <services.json> --json
actorwright npc voice import --sample <reference.wav> --plugin <npc.esp> --form-id <id> --voice-prefix <prefix> --output <fresh-root> --json
actorwright npc voice synthesize --manifest <manifest.json> --manifest-sha256 <sha256> --sample-authority <sample.json> --sample-authority-sha256 <sha256> --output <fresh-root> --resume --json
actorwright npc dialogue analyze --manifest <manifest.json> --manifest-sha256 <sha256> --plugin <npc.esp> --plugin-sha256 <sha256> --data-root <copied-Data> --plugins <ordered.csv> --sample-authority <sample.json> --sample-authority-sha256 <sha256> --output <proposal.json> --json
actorwright npc dialogue apply --proposal <proposal.json> --proposal-sha256 <sha256> --synthesis <synthesis.json> --synthesis-sha256 <sha256> --output <fresh-package-root> --lip-tools <tool-root> --json
actorwright npc dialogue verify --manifest <output-manifest.json> --manifest-sha256 <sha256> --json
```

`npc dialogue analyze` accepts only a source NPC plugin that owns no `INFO`
records. Existing dialogue and audio import is outside this slice and is
refused as `dialogue-source-dialogue-unsupported`. The proposal binds
`masterOrder` as an ordered string array, `copiedMasterSha256` as a plugin-name
to SHA-256 object, and `sourceSeqPath` / `sourceSeqSha256` as nullable strings.
The SEQ fields bind either the selected bytes or their absence. A
source-plugin-adjacent SEQ takes precedence over the copied Data-root SEQ.

The copied master closure is strict: every master copied into the output must
match the analyzed hash. Extra plugins may be present in the reviewed load
order, but they neither become masters nor resolve dialogue conditions unless
the analyzed plan names them. Apply writes a dialogue overlay: the rewritten
plugin, newly synthesized voice assets, the shared PEX, and preserved plus
appended SEQ data. The original NPC package remains required for FaceGen, skin,
and other inherited assets.

Actorwright does not embed, install, start, or administer XTTS. Discovery and
synthesis address an already-running compatible loopback service; synthesis
serializes requests, writes a resumable ledger, and does not establish
listening, voice-likeness, visual, or game-runtime authority. LIP/FUZ generation
uses separately supplied external tools. Static analyze, apply, and verify
results do not prove that Skyrim loaded or played the output.

## Static Actor Assembly preflight (registered preview.227)

Registered preview.227 exposes `npc assembly preflight`:

```text
actorwright npc assembly preflight --contract <absolute-K-local.json> --contract-sha256 <SHA256> --json
```

It admits one strict schema-1 Skyrim SE contract, verifies the hash-bound
package manifest, reads base `NPC_` and persistent `ACHR` identity through
independent typed/raw paths, and evaluates explicit OBody/BodyGen/
BodySlide/runtime-script ownership and outfit receipts. The command is
read-only: it never runs BodySlide, infers geometry, writes a plugin/package,
launches Skyrim, or grants visual/runtime authority. Admitted results contain
`noWrite: true` and `runtimeAuthority: false`; preview.225 remains immutable at
130 commands while preview.227 carries this static-only command and the Finish
Core surface.

Exit 0 means `pass` or `notApplicable`, exit 4 means `blocked` or `unknown`,
and unsafe outside-K/reparse paths use the existing security exit 3. A
top-level contract admission failure emits the `actor-assembly-preflight-error`
envelope with `contractAdmitted: false`; nested evidence failures remain in the
normal admitted result envelope.

## Finish Core (registered provisional preview.227)

Registered provisional `1.0.0-preview.227` carries the three Finish Core commands:

```text
actorwright npc finish analyze --request <request.json> --output <proposal.json> --json
actorwright npc finish apply --request <request.json> --proposal <proposal.json> --output <package-root> --json
actorwright npc finish verify --manifest <manifest.json> --json
```

Finish requests may set `outfitRacePolicy` to `refuse` (the default) or
`clone`. The default refuses an outfit whose selected ARMA does not admit the
actor race and reports `finish-core-outfit-armature-race-excluded`. `clone`
requires review and creates output-owned ARMA, ARMO, and OTFT records for the
admitted race; it does not mutate provider records. An omitted
`existingOutfit` preserves the source NPC's default outfit, including LVLI
members, and an NPC with no default outfit remains valid.

They implement the current closed `npc.finish-core.request.v2` and
`npc.finish-core.proposal.v2` documents and share one
hash-bound transaction: source package and authority admission, deterministic
proposal, no-overwrite typed/raw write, and independent package/plugin
verification. The admitted surface is limited to follower flags/factions,
relationship, defensive combat style, an existing or private outfit, exact
inventory policy, and one self-contained `NearEditorLocation` sandbox package.
The request has no cell, worldspace, transform, anchor, placement, or ACHR
member; source and output must contain zero world or placed-reference records.
Explicit legacy `npc.finish-core.request.v1` and
`npc.finish-core.proposal.v1` documents remain readable and replayable without
injected v2 members; manifest and verification documents remain v1.

The registered package is static-only (`runtimeAuthority=false`,
`visualAuthority=false`, `placementIncluded=false`) and is authoritative for
private authoring so it can produce the NPC needed by a separate user-run
runtime checkpoint. A passing checkpoint is still required before any runtime,
visual, placement, recruitment, or release promotion; `preview.226` remains the
preserved provisional rollback authority and `preview.225` remains the immutable
rollback authority.

## Optional interior placement (registered provisional preview.227)

Preview.227 adds a separate three-phase CLI transaction:

```text
actorwright npc placement interior analyze --request <request.json> --request-sha256 <SHA256> --proposal <proposal.json> --json
actorwright npc placement interior apply --request <request.json> --request-sha256 <SHA256> --proposal <proposal.json> --proposal-sha256 <SHA256> --json
actorwright npc placement interior verify --manifest <manifest.json> --manifest-sha256 <SHA256> --json
```

The request is bound to a static Finish Core manifest, a complete copied
load-order provider set, a terminal interior-cell provider, and either explicit
or reviewed existing-reference transform values. Analyze writes a canonical
proposal; apply writes a new ESL patch and adjacent archive only; verify
reopens the manifest, patch, archive, and independent raw topology. The admitted
topology is one EDID-only interior `CELL` with one persistent `ACHR` under the
reviewed block/sub-block. It does not edit the source Finish Core plugin and
refuses overwrite, incomplete providers, ambiguous owners, unsafe paths, and
unbound hashes.

This is static/off-engine authoring only. The result may report
`conflictContained=true` because the patch is isolated in its own archive, but
it must report `conflictFree=false`, `pathingAuthority=false`,
`runtimeAuthority=false`, and `visualAuthority=false`. The command is CLI-only
in preview.227; the desktop wizard does not expose it yet. Skyrim cell loading,
placement, collision, pathing, follower behavior, and release still require
their own runtime/conflict gates.

## M2 NPC browse, search, and inspection commands

`npc list` and `npc search` require `--edition|--game fallout4|skyrimse` plus
either an explicit K-local `--data-root` or one K-local `--plugin` path. `--search`
matches EditorID and display name invariantly; `--filter`/`--category` accepts a
comma-separated additive set of `unique,generic,template,unused`; and
`--changed-only` keeps only the winning record from an explicit override chain.
The JSON list includes stable plugin/FormID identity, explicit nullable sex/race
and weight fields, category, change state, and provenance.

`npc inspect` additionally requires `--npc <hex-form-id>` and returns schema `1`
with one typed NPC object. Null fields are emitted explicitly, provenance includes
the override chain, and `unsupportedFields` names record families not yet decoded.
Inspection is read-only and never infers a profile or writes a plugin. Full
properties, appearance, and script surfaces remain planned.

## M2 FormID choice search

`forms search` is the CLI/LLM-facing core of the upstream reusable FormID picker. It
requires `--edition|--game`, an explicit K-local `--data-root` or `--plugin`, and
`--type <signature[,signature...]>`. `--query` matches name, EditorID, or the
formatted FormID; `--form-id` narrows to one global ID; `--plugins` supplies an
explicit winning order; and `--allow-null` declares that the caller may use FormID
zero as a null/none choice. Signatures are normalized to uppercase and must be four
TES characters (`NPC_` is valid).

The schema `1` JSON result contains one winning candidate per FormID, stable
signature/name/EditorID fields, `isDeleted`, and `provenanceKind` plus the complete
override chain. Candidate enumeration is read-only and deterministic. It does not
persist drafts, apply field-specific compatibility predicates, or replace the
desktop modal picker yet.

## M2 load-order and plugin validation commands

## M2 persisted asset index

`assets index` retains its read-only summary mode. Supplying `--output <path>`
writes a new schema-versioned JSON artifact under K. The artifact groups each
normalized relative asset path, lists every indexed loose/archive provider, and
sets the deterministic winner to loose providers first, then archive providers
ordered by source label and SHA-256. It never extracts or rewrites archives,
never overwrites an existing artifact, and writes through a same-directory
temporary file before promotion. `tools/verification/verify_asset_index.py`
independently checks containment, path normalization, hashes, ordering, and
winner identity. P01-005 is accepted against valid BA2 and BSA fixtures;
malformed-archive handling remains a negative test and archive-order inference
is intentionally outside this command.

## M2 selected-plugin archive consistency

`workspace preflight --game|--edition <fallout4|skyrimse> --plugin <path>
--asset-index <path>` performs a read-only consistency check before a build.
The selected plugin and the persisted index's `dataRoot` must be the same
K-local copied Data root. Every edition-supported archive on disk must appear
in the index, every indexed archive must still exist on disk, and indexed
archives must use `.ba2` for Fallout 4 or `.bsa` for Skyrim SE. The JSON schema
`1` response reports `isConsistent`, the aligned roots, each archive's
`presentOnDisk`, `indexed`, and camel-case `status`, plus actionable diagnostics.
Exit `0` means consistent; exit `3` is a security refusal and exit `4` is an
archive or schema validation failure. The command does not infer live profiles,
archive load order, archive ownership, or repair any file. The independent
proof is `tools/verification/verify_archive_consistency.py`.

## M2 generated-plugin and FaceGen-sidecar scan

`workspace scan-generated --game|--edition <fallout4|skyrimse> --data-root <path>`
reads one explicit K-local copied Data root and reports only generated plugins
whose TES4 `CNAM` author is the pinned reference marker `NPC Manager`. For each
marked plugin it reports a source SHA-256, whether the typed Bethesda reader
accepted it, and the discovered NPC FormIDs. It then inventories known loose
sidecars below that plugin's FaceGeom and game-specific FaceGen directories:
common `FaceGeom` NIFs, Fallout 4 `FaceCustomization` diffuse/normal/specular
DDS files, and Skyrim SE `FaceTint`, `FaceDiffuse`, `FaceNormal`, and
`facedetailneutral` DDS files. Canonical names and the `_2` debug-sandbox
variant are explicit in schema `1` output, including relative path, size, and
SHA-256.

The scan is read-only, deterministic, cancellable, K-only, and refuses the
protected live root and reparse traversal. Unrecognized files are warnings;
malformed headers, unreadable marked plugins, unsafe paths, and safety-limit
violations are errors. It does not scan archives, parse `.bssliders`, infer a
profile or load order, generate FaceGen, mutate assets, or prove runtime
appearance. The independent proof is
`tools/verification/verify_generated_artifacts.py`.

## M2 mesh and headpart choice search

`assets search` is a read-only, schema-versioned choice enumerator. It requires
`--edition|--game fallout4|skyrimse`, `--kind mesh|headpart`, and an explicit
K-local `--data-root` (or a K-local `--plugin` whose parent is the Data root).
`--query` matches normalized mesh paths; headpart search also matches the
typed HDPT EditorID, name, FormID, and model path. Mesh results are normalized
relative `.nif` paths grouped once per path with every indexed provider's kind,
source label, size, and SHA-256. Headpart results are one winning HDPT per
FormID, with explicit plugin override provenance and provider status. A missing
model provider is retained as `providerStatus: "missing"` with a warning rather
than silently guessed or discarded. The command never previews, mutates, or
deploys assets; race/gender/part-type predicates and archive precedence remain
planned.

`plugins resolve-load-order` requires `--edition|--game fallout4|skyrimse`, an
explicit K-local `--plugins` directory, and a K-local `--loadorder` (or
`--load-order`) JSON manifest with schema version `1`:

```json
{"schemaVersion":1,"edition":"skyrimse","plugins":[{"name":"Example.esp","order":0,"enabled":true}]}
```

The command reports deterministic order, enabled state, file existence, source
hash, and unlisted files. Duplicate names or order indexes, missing enabled
files, invalid paths, and cross-game manifests fail closed.

`plugins validate` takes the same edition and load-order manifest plus a
K-local `--plugin`. It reads the enabled plugin set through the typed Bethesda
reader and reports missing, disabled, after-target, malformed, or cyclic master
dependencies. It never infers a live profile or writes a plugin. This is a
read-only compatibility gate; it does not claim complete record-tree parity.

## M3 mutation commands

`npc patch` is a fail-closed identity/name/sex/weight/archetype/statistics/keyword/faction/inventory/outfit/perk/actor-effect/property vertical slice. It requires
`--edition|--game fallout4|skyrimse`, an explicit K-local `--input-plugin`, a new
`--output` path, and `--form-id`. Supply one or more of `--editor-id`, `--name`,
`--sex male|female`, `--weight` (Skyrim scalar), or `--weight-triangle
thin=<n>,muscular=<n>,fat=<n>`.

Statistics use `--level` or `--level-mult` (mutually exclusive), the typed ACBS
offset options, Skyrim-only `--height`/player-skill options, and comma-separated
`--skill-values name=value`/`--skill-offsets name=value` maps. `--set-flag` and
`--clear-flag` accept game-aware ACBS flags; `pc-level-mult` is the edition-neutral
bit-0x80 alias. Unsupported game fields, duplicate/conflicting flags, non-finite
heights, and out-of-range values fail before writing.

Keyword lists use ordered comma-separated qualified FormIDs: `--keywords` replaces
KWDA, while `--add-keyword` and `--remove-keyword` apply duplicate-checked list
operations. Fallout 4 also exposes `--appr`, `--add-appr`, and `--remove-appr`
for APPR attach-parent slots; Skyrim rejects those options before writing.

Faction memberships use `Plugin.esp|0x00000801=rank` entries with signed-byte ranks
from -128 through 127. `--factions` replaces the ordered SNAM list;
`--add-faction`, `--update-faction`, and `--remove-faction` apply deterministic
incremental operations. Replacement cannot be combined with incremental options,
and every faction reference must belong to the input plugin.

NPC inventory uses `Plugin.esp|0x00000801=count` entries with signed 32-bit CNTO
counts from `-2147483648` through `2147483647`. `--inventory` replaces the
ordered CNTO list; `--add-inventory`, `--update-inventory`, and
`--remove-inventory` apply deterministic incremental operations. Duplicate
replacement/add entries merge counts in input order with checked overflow;
duplicate updates and removals are rejected. Replacement cannot be combined
with incremental options, and every item reference must belong to the input
plugin. Optional COED data is retained for inventory entries that remain.

NPC outfits use qualified OTFT references. `--default-outfit` maps to DOFT and
`--sleep-outfit` maps to SOFT; each accepts `Plugin.esp|0x00000801` or `none`
to clear the link. Unspecified fields are preserved, and every supplied
reference must belong to the input plugin.

NPC perks use `Plugin.esp|0x00000801=rank` entries with unsigned-byte ranks
from 0 through 255. `--perks` replaces the ordered PRKR list;
`--add-perk`, `--update-perk`, and `--remove-perk` apply deterministic
incremental operations. Duplicate, null, external, conflicting, and malformed
entries fail before writing, and every perk reference must belong to the input
plugin.

Actor effects use ordered qualified SPEL references. `--actor-effects` replaces
the SPLO list, while `--add-actor-effect` and `--remove-actor-effect` apply
duplicate-checked incremental operations. Null, external, conflicting, and
malformed references fail before writing, and every spell reference must belong
to the input plugin.

Fallout 4 properties use explicit PRPS actor-value/IEEE-754 single entries:
`Plugin.esp|0x00000801=float`. `--properties` replaces the ordered list;
`--add-property`, `--update-property`, and `--remove-property` apply deterministic
incremental operations. Skyrim SE rejects PRPS properties because the pinned
upstream editor hides that tab and its NPC record model has no PRPS field. Null,
external, duplicate, conflicting, malformed, and non-finite values fail before
writing; every actor-value reference must belong to the input plugin.

Archetype references use `--race`, `--voice`, `--class`, and `--combat-style`
with a qualified value such as `Example.esp|0x00000801`. Optional voice and
combat-style links accept `none` to clear the link; race and class are required
references when selected. The bounded writer accepts only references whose
plugin matches the input plugin, so unresolved master/load-order references
fail closed until an explicit resolver is added.

Without `--apply`, the command only analyzes and returns a typed proposal. A
write requires `--apply --expected-sha256 <64-hex>`; `--proposal <path>` may
persist the proposal, and `--dry-run` is mutually exclusive with `--apply`.
Outputs and proposals must be new paths under the K-only workspace. The
service writes a same-directory temporary plugin, promotes it without
overwrite, and independently verifies it before reporting `applied=true`.

`npc edit-package` is the bounded mutation surface shared with the desktop Edit
NPC task. It requires Skyrim SE, a K-local `--input-plugin`, its
`--input-sha256`, `--npc`, a fresh `--output-root`, one `.esp` `--plugin`
filename, and the exact `--output-kind source-mastered-override|standalone-copy`
selector. The application request remains source-compatible by defaulting to
`SourceMasteredOverride`, but the CLI refuses an omitted or unknown kind.

The legacy discovery surface is scoped for this command: `npc edit-package
--help` prints only its command name and its complete option rows, followed by
the separate global parser line `Global options: --help, --json.`. The
corresponding legacy `schema export --command "npc edit-package"` row adds an
`options` array with each option's `name`, `required`, `valueSyntax`,
`acceptedValues`, and `description`. Schema `options` rows are command-specific;
the global parser flags `--help` and `--json` are intentionally documented
separately and are not catalogue rows. This is additive discovery metadata;
`npc edit-package` remains `legacy` in protocol-2 capabilities and is not
promoted to protocol-2 execution readiness.

Identity changes use `--editor-id` and `--name`. The complete safe
Skyrim statistics surface accepts `--level` or `--level-mult`, the three actor-
value offsets, `--calc-min`, `--calc-max`, `--speed-multiplier`, `--disposition`,
`--bleedout`, base `--player-health|--player-magicka|--player-stamina`, all 18
skills through `--skill-values` and `--skill-offsets`, `--far-model-distance`,
`--geared-weapons`, and the supported Skyrim gameplay flags through
`--set-flag|--clear-flag`. It also accepts typed keyword, faction, inventory,
outfit, perk, and actor-effect operations. XP offset, height, sex, and appearance
remain excluded from this transaction because they are unsupported or owned by
the appearance/FaceGen path. Any unsupported option is rejected instead of
ignored.

`source-mastered-override` preserves the existing one-record writer. It retains
the source plugin's master order, appends that source plugin as the final
required master, and writes exactly one source-owned NPC override. Its package
evidence declares the source plugin name and exact SHA-256, and its runtime
instructions require installing both the source and override plugins; this
kind is explicitly not standalone. Its five-artifact package contains the
plugin, exact apply proposal, durable package-relative proposal, independent
verification report, and runtime-test instructions.

`standalone-copy` recursively copies the source TES4/group/record byte tree in
order, mutates only the selected NPC, and retains the input TES4 master list
without appending the input plugin as a master. A lossless ordered inventory
records group path, ordinal, signature, raw FormID, flags, byte length, and
record SHA-256. Package completion reopens the output and independently checks
inventory identity/order, unchanged non-target bytes, and target mutation. Any
present loose `FaceGeom`, `FaceTint`, `FaceDiffuse`, or `FaceNormal` sidecar for
the target is copied under the output plugin key and added to the manifest.
Localized sources, compressed target records, malformed TES4 bounds, and
matching plugin-keyed `.bsa` archives fail closed because relocation support is
not yet available. Standalone runtime instructions require only the copied
plugin and declared loose sidecars. Package verification re-hashes every
declared file and rejects undeclared files. These are static package proofs;
runtime and visual authority remain separate.

`plugin verify` independently checks a source/output pair. It requires
`--edition|--game`, `--before`, `--after`, and `--proposal`. For an
`npc edit-package` result, `--proposal` is the durable
`evidence/npc-edit-proposal.json` whose `artifactKind` is
`existing-npc-edit-proposal`; the sibling `npc-override-apply-proposal.json` is
the typed internal write authority and is not the public verifier input. For
sex changes the verifier proves that only the game-specific Female bit changed;
archetype verification proves the raw RNAM/VTCK/CNAM/ZNAM
subrecords and all non-target subrecords; faction verification reads game-specific
SNAM entry widths (FO4 5-byte, Skyrim SE 8-byte) and signed ranks; inventory
verification reads 8-byte CNTO item/count entries and the paired COCT count.
Outfit verification reads the raw 4-byte DOFT/SOFT FormIDs.
Perk verification reads game-specific PRKR entry widths (FO4 5-byte, Skyrim SE
8-byte) and unsigned ranks.
Actor-effect verification reads 4-byte SPLO spell FormIDs.
Fallout 4 property verification reads 8-byte PRPS AVIF FormID/single entries;
Skyrim PRPS requests are rejected as unsupported.
Unsupported or
malformed TES4 shapes fail closed.

`npc materialize-template` resolves the pinned per-category template chain for
an NPC and produces a typed proposal. It accepts `--game|--edition`,
`--plugin` (or `--input-plugin`), a new `--output`, and `--form-id`; omit
`--categories` to select every inherited category, or provide a unique
comma-separated list such as `stats,factions,spell-list,keywords`. The
supported write surface copies resolved ACBS stats, SNAM factions, SPLO spell
lists, and KWDA keywords, then clears only those category bits. FO4
category-specific TPTA links and Skyrim's base TPLT link are followed with a
32-record cycle bound. External masters, missing links, cycles, stale hashes,
existing destinations, and unsupported traits/AI/script/attack/base/inventory
surfaces fail closed without writing. Apply requires `--apply
--expected-sha256`; `--proposal` persists the evidence document. An independent
raw verifier checks the target's ACBS template flags, resolved subrecords, and
non-target byte surface.

`npc reset` mirrors the pinned editor's discard-to-open-snapshot behavior with an
explicit stateless CLI baseline. It requires `--game|--edition`, `--plugin`
(or `--current-plugin`), `--baseline`, a new `--output`, `--npc|--form-id`, and
one `--section` (`identity`, `archetype`, `weight`, `stats`, `keywords`,
`factions`, `inventory`, `outfits`, `perks`, `actor-effects`, or `properties`).
Current and baseline files must be distinct copies with the same plugin filename;
only the selected typed section is copied from baseline and every other section is
verified against the current copy. `--apply --expected-sha256` is required for a
write; no-op resets return success without creating an output. Unsupported null
transitions, Skyrim PRPS, path collisions, stale hashes, existing outputs, and
reparse traversal fail closed. The fixture's independent raw TES4 verifier checks
the selected subrecord set and all unrelated NPC subrecords.

## M4 typed headpart and hair-color patch

`npc face-patch` is the bounded static equivalent of the pinned face editor's
headpart and hair-color controls. It requires `--game|--edition`, a K-local
`--plugin`, new same-filename `--output`, K-local `--data-root`, `--npc|--form-id`,
and either `--headparts` or `--hair-color`. Headparts accept compact
`face=Plugin.esp|0x811,eyes=...` syntax or a JSON array of
`{"form":"Plugin.esp|0x811","type":"face"}` objects. A full headpart patch
requires exactly one of each non-Misc type (`face`, `eyes`, `hair`,
`facial-hair`, `scar`, `eyebrows`, `meatcaps`, `teeth`, `head-rear`).

Before proposal creation, the service rejects duplicate or external references,
HNAM extras used as roots, race-list or sex mismatches, unsafe provider paths,
missing mesh providers, compressed target NPC records, stale hashes, existing
outputs, reparse traversal, and path collisions. `--hair-color none` clears the
TES4 `HCLF` field; a `Plugin.esp|FormID` value sets it. Apply requires
`--apply --expected-sha256`; `--proposal`, `--dry-run`, and `--json` are supported.
The independent verifier checks ordered PNAM/HCLF values and byte preservation of
all unrelated NPC subrecords for both Fallout 4 and Skyrim SE. This slice does not
generate FaceGen, morph/tint data, previews, or runtime appearance.

## M4 typed CharGen options

`facegen options` validates a complete, game-scoped CharGen/FaceGen options
document from an explicit K-local `--input|--options` JSON file. The typed schema
covers all controls persisted by the pinned `CharGenOptionsForm.vb`: all/per-layer
resolution, diffuse/normal/specular compression, TGA emission, FO4-only fixes,
SSE RaceMenu overlay baking, diffuse/normal/specular convention buckets,
per-blend working spaces, seed policy, and game-specific tint-order rules.

`--game|--edition fallout4|skyrimse` must match the document. All-mode channel
resolution and compression derivation is checked with the upstream game rules;
FO4-only and SSE-only combinations fail with stable diagnostics. JSON parsing is
strict about duplicate/unknown fields and non-finite seed values. Without
`--apply` the command is read-only. With `--apply --output <path>`, an optional
`--expected-sha256` binds the source, canonical JSON is written atomically under
K, and existing/protected/reparse destinations are refused. The independent
proof is `tools/verification/verify_facegen_options.py`.

This command persists deterministic settings only. It does not encode DDS/NIF
pixels, build FaceGeom/FaceTint, preview meshes, deploy files, or claim runtime
appearance.

## M4 typed Fallout 4 face tints and paints

`face tint patch` is the bounded static equivalent of the pinned FO4 tint and
paint editor. It requires `--game fallout4`, a K-local input plugin, a new
same-filename output, `--npc|--form-id`, and `--layers`. The layer document is
an ordered JSON array, or `@K:\\...\\layers.json` when the JSON is too large for
an inline shell argument. Each layer uses `dataType` (`value-color` or
`texture-set`), `optionIndex`, and `value` (0..100). Value/color layers carry
RGB plus an optional `templateColorIndex` according to their source TEND shape;
TextureSet layers must carry `rawTendBase64` so unknown payload bytes remain
unchanged. The adapter preserves the FO4 1-, 5-, and 7-byte TEND forms and
re-emits layers in the requested order. An explicit empty array performs the
upstream Remove-all operation.

Apply requires `--apply --expected-sha256`; `--proposal`, `--dry-run`, and
`--json` are supported. Duplicate option indexes, values above 100, malformed
raw payloads, stale hashes, existing/protected outputs, compressed target NPCs,
and Skyrim requests fail closed. This is persisted plugin data only: RACE/CLFM
provider resolution, live composition, preview rendering, FaceTint generation,
deployment, and runtime appearance remain separate. The independent proof is
`tools/verification/verify_npc_face_tints.py`.

## M4 typed Skyrim face-tint layers

The same `face tint patch` command accepts `--game skyrimse` for the persisted
authored Skyrim layer sequence. It requires a K-local input plugin, a new
same-filename `--output`, `--npc|--form-id`, and `--layers` as an ordered JSON
array (or `@K:\\...\\layers.json`). Each layer contains `index`, `red`,
`green`, `blue`, `alpha`, `coverage` (0..100, the TINV value), and
`presetIndex` (signed 16-bit TIAS). The writer replaces only `TINI/TINC/TINV/TIAS`
fields on the target NPC and preserves all other fields and records.

Apply requires `--apply --expected-sha256`; `--proposal`, `--dry-run`, and
`--json` are supported. Duplicate indexes, malformed or out-of-range values,
compressed NPCs, stale hashes, existing/protected outputs, and Fallout 4
requests fail closed. A `texture` layer field is refused explicitly because
RaceMenu custom mask paths have no NPC-record home; they require a later
`.jslot`/sidecar route. This slice proves persisted tint bytes only and does
not resolve RACE/TIND/CLFM providers, generate FaceTint assets, render, deploy,
or claim runtime appearance. The independent proof is
`tools/verification/verify_skyrim_face_tints.py`.

## M4 typed Skyrim vanilla face morphs

`face morph patch` is the bounded static equivalent of the pinned SSE Morphs tab.
It requires `--game skyrimse`, a K-local input plugin, a new same-filename
`--output`, `--npc|--form-id`, and a strict JSON object passed through
`--vanilla` (or `--vanilla @K:\\...\\morphs.json`). The object contains exactly
18 finite `nam9` slider values in `[-1,1]`, a finite `nam9Trailing` engine value,
and four unsigned `nama` family values in `0..15` or `4294967295` (the upstream
unset sentinel). Apply requires `--apply --expected-sha256`; `--proposal`,
`--dry-run`, and `--json` are supported.

The adapter rewrites only the target NPC's `NAM9` and `NAMA` fields, preserving
the trailing float, sentinel values, unrelated fields, non-target records, and
group sizes. Duplicate/malformed or compressed records, stale hashes,
existing/protected outputs, reparse traversal, path collisions, and Fallout 4
requests fail closed. This slice proves raw persisted morph bytes only; it does
not map extended RaceMenu morph names, generate FaceGeom, render, deploy, or
claim runtime appearance. The independent proof is
`tools/verification/verify_skyrim_face_morphs.py`.

## M4 typed Skyrim RaceMenu extended morphs

`face morph extended` patches the Skyrim RaceMenu `.jslot` `customMorphs`
name/value channels used by the pinned face editor. It requires `--game skyrimse`,
K-local same-filename `.jslot` `--input` and new `--output` paths, and an
`--extended` object containing a `morphs` array of `{name,value}` entries (or an
`@K:\\...\\morphs.json` file). Names are case-insensitively keyed, values are
finite and bounded to `[-1,1]`, and values within `0.0001` of zero remove a
channel as in the upstream UI. Existing names update deterministically;
uncatalogued names are retained as direct channels and reported with a warning.

Apply requires `--apply --expected-sha256`; `--proposal`, `--dry-run`, and
`--json` are supported. The patcher rewrites only `customMorphs`, preserving
unknown root fields and all other preset values. Duplicate/invalid entries,
stale hashes, existing/protected outputs, reparse traversal, path collisions,
and Fallout 4 requests fail closed. UTF-8 BOM input is accepted. This slice does
not resolve installed RaceMenu `.slider` catalogs to TRI meshes, generate
FaceGeom, render, deploy, or claim runtime deformation. The independent proof is
`tools/verification/verify_skyrim_extended_morphs.py`.

## M5 typed Skyrim RaceMenu sculpt blocks

`face sculpt patch` patches the Skyrim RaceMenu `.jslot` `morphs.sculpt`
sidecar. `--sculpt` accepts an object with `divisor` and `parts`; each part has
a `host` chargen TRI identifier, a positive `vertices` count, and `verts`
entries with `index`, `dx`, `dy`, and `dz` world-unit coordinates. The service
rounds each coordinate using the pinned RaceMenu rule (value × divisor,
ties-to-even) and writes integer `[index, dx, dy, dz]` rows while preserving all
unrelated root and `morphs` properties. Hosts, counts, indices, finite
coordinate bounds, duplicate indices, divisor range, stale hashes, output
collisions, and K-only paths are validated. The command is a sidecar patch
only: it does not resolve TRI topology, generate FaceGeom/NIF assets, render,
deploy, or claim runtime appearance. The independent proof is
`tools/verification/verify_skyrim_sculpt.py`.

## M5 face bone-region and vertex-morph pose resolution

`face pose resolve` consumes a version-1 K-local JSON pose document with
`--game fallout4|skyrimse`, a non-zero `--npc` FormID, and `--preset`. The
document declares facial-bone regions (`min`/`max` position, rotation, and
scale bounds), FMRS slider rows, and declaration-ordered vertex morph
channels. FMRS values are clamped to `[-1,1]`, mapped toward signed min/max
offsets, multiplied by `FMIN` (`<=0` becomes `1`), summed per `skin_` bone,
and converted to the pinned axis-angle pose representation. Vertex channels
are appended in declaration order, then weighted deltas are summed by vertex
index. JSON output includes the source hash, typed bone and vertex results,
and the explicit combination order used by both CLI and GUI callers. Unknown
FMRS region IDs are warned and skipped to match the upstream resolver. This
artifact is mesh-independent: it does not load TRI topology, skin a skeleton,
write NIF/FaceGeom, render, deploy, or prove runtime appearance. The
independent proof is `tools/verification/verify_face_pose.py`.

## M5 independent face-section reset

`face reset` is the stateless CLI equivalent of the pinned `EditFace_Form`
section-reset buttons. It requires `--game|--edition`, `--npc|--form-id`,
`--current`, `--baseline`, `--output`, and one section: `face-parts`, `tints`,
`vertex-morphs`, `bone-regions`, `skyrim-morphs`, or `skyrim-tints`. The
current and baseline inputs are explicit version-1 `.face.json` snapshots of
the upstream overlay, with `schemaVersion`, game, and NPC identity metadata.
Fallout 4 exposes face-parts, tints, vertex-morphs, and bone-regions; Skyrim
SE exposes face-parts, skyrim-morphs, and skyrim-tints. The selected payload is
replaced from baseline (or removed when absent there), while all other root
properties—including unknown JSON values—are copied from current. Apply is
hash-bound (`--apply --expected-sha256`) and never overwrites an output. The
artifact is an overlay snapshot only: it does not write a plugin or `.jslot`,
generate FaceGen, render, deploy, or prove runtime appearance. The independent
proof is `tools/verification/verify_face_reset.py` and the rational dual-game
fixture is `tools/fixtures/m5/p04-face-reset.ps1`.

## M5 Fallout 4 body-weight triangle

The pinned `WeightTriangleControl` stores MWGT as barycentric coordinates:
values are clamped non-negative, normalized to sum to one, and the all-zero
case becomes `(0.5, 0.5, 0)`. `EditBody_Form.RedistributeMwgt` clamps a moved
axis and preserves the existing ratio of the other two axes, using an even
split when both are zero. `npc patch --weight-triangle
thin=<n>,muscular=<n>,fat=<n>` now applies the same normalization before the
typed FO4 MWGT write; partial or duplicate tuples and non-finite values fail
closed. The LLM-friendly math helpers are also exposed without a file write:

```text
actorwright body weight normalize --game fallout4 --triangle thin=<n>,muscular=<n>,fat=<n> --json
actorwright body weight redistribute --game fallout4 --current thin=<n>,muscular=<n>,fat=<n> --axis thin|muscular|fat --value <n> --json
```

These commands are math artifacts only; plugin mutation remains hash-bound and
K-local through `npc patch`. The independent proof is
`tools/verification/verify_weight_triangle.py` and the dual-game safety fixture
is `tools/fixtures/m5/p05-weight-triangle.ps1`.

## M5 Fallout 4 body skin routing

`body patch` is the body-oriented alias for the hash-bound NPC mutation boundary.
Fallout 4 accepts a typed same-plugin ARMO reference through `--skin
Plugin.esp|0xXXXXXXXX`, or the explicit `--clear-skin` race-default sentinel:

```text
actorwright body patch --game fallout4 --input-plugin <input.esp> --output <new.esp> --npc 0x800 --skin <Plugin.esp|0xXXXXXXXX> --expected-sha256 <hash> --apply --json
actorwright body patch --game fallout4 --input-plugin <input.esp> --output <new.esp> --npc 0x800 --clear-skin --expected-sha256 <hash> --apply --json
```

The writer maps this to NPC `WNAM`/Mutagen `Npc.Skin`; the independent verifier
reads the written four-byte subrecord. External master references, Skyrim WNAM
requests, and `--preset-skin <id>` are refused before a write. LooksMenu skin
templates are F4SE runtime JSON bundles, not NPC record fields; template/provider
catalog support and Skyrim RaceMenu skin-overrides persistence remain separate
planned surfaces. Proof: `tools/verification/verify_skin_wname.py` and
`tools/fixtures/m5/p05-skin.ps1`.

## M5 Fallout 4 body-region morphs

The same `body patch`/`npc patch` boundary accepts the five pinned Fallout 4
NPC.MRSV regions through `--regions`. Values are named for LLM callers and are
serialized positionally as `Head`, `UpperTorso`, `Arms`, `LowerTorso`, `Legs`:

```text
actorwright body patch --game fallout4 --input-plugin <input.esp> --output <new.esp> --npc 0x800 --regions '{"head":0.25,"upperTorso":-0.5,"arms":0.1,"lowerTorso":0,"legs":1}' --expected-sha256 <hash> --apply --json
actorwright body patch --game fallout4 --input-plugin <input.esp> --output <new.esp> --npc 0x800 --regions @K:\\path\\regions.json --expected-sha256 <hash> --apply --json
```

The `@` form is recommended for PowerShell because it preserves JSON quoting;
the referenced file must be under the K-only workspace and cannot traverse a
reparse point. Values must be finite and within `[-1,1]`; unknown or duplicate
regions, Skyrim requests, stale hashes, and existing destinations fail closed.
LooksMenu `Morphs.Values` uses this same typed contract in
`pipeline preset-to-npc`, including deterministic five-slot export. Optional
`--facegeom-manifest`, `--facetint-manifest`, and `--runtime-script-build`
inputs add the corresponding hash-bound appearance/evidence artifacts to the
package. `--runtime-script-package <runtime-script-package.json>` instead
installs the hash-bound game-specific apply PEX into the generated package's
`Data/Scripts` tree and records it as a package artifact; the copied package
root is still K-local and never the protected live installation. BodySlide
sidecars are derived from typed preset body morphs. A runtime-test instruction
kit is always emitted. This is
NPC record evidence only: BodySlide TRI deformation, mesh generation, texture
providers, and runtime appearance are not claimed. Proof:
`tools/verification/verify_body_morph_regions.py` and
`tools/fixtures/m5/p05-body-morph-regions.ps1`.

## M5 BodySlide TRI slider resolution

`body sliders resolve` is a read-only catalog command. It accepts an explicit
K-local `.tri` file and a matching K-local preset:

```text
actorwright body sliders resolve --game fallout4 --tri <body.tri> --preset <looksmenu.json> --json
actorwright body sliders resolve --game skyrimse --tri <body.tri> --preset <racemenu.jslot> --json
```

The resolver parses the pinned PIRT position and UV sections, preserves exact
shape-name matching and case-insensitive morph-name matching, warns on duplicate
morphs using first-wins semantics, skips near-zero values and the upstream
`WeightThin`, `WeightMuscular`, `WeightFat`, and numeric `MorphRegion` helper
names, and reports sparse channels, missing sliders, and SHA-256 provenance.
Malformed/truncated/non-finite/trailing data, unsafe paths, and missing files
fail closed with versioned JSON diagnostics. This command does not discover
NIF `BODYTRI` paths, invoke BodySlide, generate meshes, mutate plugins, or prove
runtime deformation. Proof: `tools/verification/verify_bodyslide_tri.py` and
`tools/fixtures/m4/p05-bodyslide-tri.ps1`.

## M5 BodySlide SliderPreset XML inspection

`body sliders inspect-preset` is a read-only Skyrim SE SliderPreset XML
inspector:

```text
actorwright body sliders inspect-preset --game skyrimse --preset-xml <K-local.xml> --json
```

JSON output uses schema version `1` and reports `isValid`, diagnostics, source
SHA-256, parsed preset name, target SliderSet `set`, groups, and the ordered
`SetSlider` rows. BodySlide-native values are retained as finite percent floats;
the command does not normalize them to BodyGen units. It fails closed for paths
outside K, reparse traversal, non-`.xml` inputs, malformed XML, empty preset
name or set, duplicate `name + size` rows, and oversized files. The inspector
does not invoke BodySlide, inspect TRI/NIF geometry, generate meshes, mutate
plugins, deploy files, or prove runtime appearance.

## M5 Skyrim body-weight interpolation

`body weight resolve` proves the pinned Skyrim `_0`/`_1` interpolation over an
explicit K-local JSON manifest:

```text
actorwright body weight resolve --game skyrimse --input <body-weight-manifest.json> --json
```

The version-1 manifest carries `gender`, `weightPercent`, the selected ARMA
gender's `weightSliderFlags`, `baseDigit` (`0` or `1`), and equal-length
`baseVertices`/`twinVertices` arrays. The resolver requires flag `0x02`, clamps
`weightPercent/100` to `[0,1]`, emits `twin - base` sparse deltas using the
upstream `1e-7` squared-length threshold, and uses `t` for `_0` or `1-t` for
`_1`. A disabled flag is a warning no-op; malformed JSON, non-finite values,
count mismatches, unsafe paths, and cross-game requests fail closed. This is a
mesh-independent math proof: it does not parse NIF/ARMA files, discover twin
paths, mutate plugins, or claim runtime rendering. Proof:
`tools/verification/verify_sse_body_weight.py` and
`tools/fixtures/m5/p05-sse-body-weight.ps1`.

## M5 Body-overlay layer routing

`body overlay patch` is a read-only, versioned proposal command. It accepts a FormID and
inline JSON or an explicit K-local `@file` layer array:

```text
actorwright body overlay patch --game fallout4 --npc 0x800 --layers <fo4-layers.json> --json
actorwright body overlay patch --game skyrimse --npc 0x800 --layers <sse-layers.json> --json
```

Fallout 4 layers use `template`, optional integer `priority`, `tint` `[r,g,b,a]`,
`offsetUV`, `scaleUV`, and optional validated `slots` `{slot,material}` entries.
The result orders layers by ascending priority while retaining input order for ties.
Skyrim SE layers use `node` (`Body [OvlN]`, `Hands [OvlN]`, or `Feet [OvlN]`),
`diffuse`, optional `normal`, optional RGBA `tint`, and optional `alpha`; the result
orders by node index and derives the target zone from the node. Vectors are finite,
unit-ranged where applicable, and texture/material paths are relative, extension-checked,
and traversal-free. Duplicate slots/nodes, mixed-game fields, malformed JSON, and
protected or outside-K files fail closed. The response includes source and canonical
SHA-256 hashes and never mutates a plugin or preset sidecar. It does not decode or bake
textures, resolve providers, or prove runtime appearance; Skyrim pixel folding is P05-007.
Proof: `tools/verification/verify_body_overlays.py` and
`tools/fixtures/m5/p05-body-overlays.ps1`.

## M5 Skyrim overlay layer folding

`body overlay bake` is the deterministic Skyrim SE pixel-fold command:

```text
actorwright body overlay bake --game skyrimse --layers <manifest.json|@K:\\...\\manifest.json> --output <new-K-local.dds> --json
```

The manifest contains a `base` raster (`width`, `height`, and normalized RGBA
`pixels`), optional same-sized `facetint` and `detail` rasters, and typed `layers`.
Skee layers use `source: "skeeMask"`, `layerType` 0/1/2, a four-value color,
opacity, blend mode, and a same-sized texture except for solids. RaceMenu decals
use `source: "faceOverlay"`, an exact `Face [OvlN]` node, color, opacity, and a
same-sized texture. The service applies the upstream sequence: facetint/detail
fold, skee source order, then Face overlays by node index. Facetint uses the
engine's sRGB-to-linear/linear-to-sRGB fold; overlay RGB is composited in the
stored accumulator space and base alpha is preserved.

The output is a new, uncompressed BGRA8 DDS with deterministic header, pixel,
canonical, source, and output hashes. The command refuses malformed/duplicate
fields, non-finite or out-of-range values, dimension mismatches, unsafe input or
output paths, reparse traversal, existing outputs, cancellation, and non-Skyrim
requests. It deliberately consumes an explicit raster manifest: provider lookup,
arbitrary BCn decode/compression, NIF/FaceGen generation, and runtime rendering
are not implied. Proof: `tools/verification/verify_sse_overlay_fold.py` and
`tools/fixtures/m5/p05-sse-layer-fold.ps1`.

## M5 Skyrim RaceMenu body transforms and skin overrides

`body transforms apply` replaces the typed Skyrim RaceMenu metadata sections in a
new `.jslot` without overwriting the source or destination:

```text
actorwright body transforms apply --game skyrimse --npc 0x800 --preset <K:\\...\\source.jslot> \
  --transforms <json|@K:\\...\\transforms.json> \
  --skin-overrides <json|@K:\\...\\skins.json> \
  --output <new-K-local.jslot> --json
```

The bounded contract maps `RSMTransform` key 30 to scale, 31 to three-value
position, 32 to a nine-value row-major rotation matrix, and 33 to scale mode.
Existing source scale and scale-mode values are accepted at any bounded non-negative
index, matching the pinned index-agnostic patch behavior; replacement payloads emit
the canonical index-0 form.
Skin key 9 retains every indexed `.dds` texture, key 7 retains signed ARGB tint,
and key 8 retains alpha. Replacement sections are explicit and deterministic;
unrelated top-level JSON is preserved. Duplicate keys, incomplete component sets,
non-finite or unsafe values, unsupported key/type/index variants, protected or
outside-K paths, stale hashes, and existing outputs fail closed with versioned
diagnostics. This command writes metadata only: it does not execute NiOverride,
write a co-save or plugin, discover providers/skeletons, generate meshes, or prove
runtime appearance. Proof: `tools/verification/verify_sse_body_transforms.py` and
`tools/fixtures/m5/p05-sse-body-transforms.ps1`.

## M5 body-section reset

`body reset` is the CLI equivalent of the pinned `EditBody_Form` per-tab reset.
Because the source resets an in-memory overlay captured when the form opened, the
CLI uses explicit version-1 `.body.json` construction snapshots and never presents
the output as a plugin, `.jslot`, co-save, or runtime state file.

```text
actorwright body reset --game <fallout4|skyrimse> --npc <form> \
  --current <K:\\...\\Npc.body.json> --baseline <K:\\...\\Npc.body.json> \
  --output <new K:\\...\\Npc.body.json> --section <section> \
  --apply --expected-sha256 <current-hash> --json
```

Fallout 4 sections are `weight`, `morphs`, `sliders`, `skin`, and `overlays`.
Skyrim SE sections are `weight`, `sliders`, `overlays`, `transforms`, and
`skin-overrides`. The service replaces only the selected root value, removes it
when the baseline omits it, preserves every unrelated or unknown root value, and
refuses malformed snapshots, stale hashes, cross-game sections, unsafe paths,
reparse points, and existing outputs. The source-to-section mapping is recorded in
`05-reports/m5-body-reset-acceptance-2026-07-17.md`.

## M4 preset commands

`preset inspect` requires `--format looksmenu|racemenu-jslot`, the matching
`--edition fallout4|skyrimse`, and an explicit K-local `--input`. It is
read-only, reports the source SHA-256, typed appearance fields, and every
unsupported field or loss as a diagnostic. Strict JSON rejects comments,
trailing commas, duplicate keys, oversized input, and malformed numeric
shapes.

`preset export` takes the same format/edition/input options plus a new explicit
K-local `--output`. It writes deterministic canonical JSON and never overwrites
an existing artifact. `preset diff` takes the format/edition and two explicit
K-local paths (`--left`, `--right`) and returns stable field-level differences for
every typed appearance field. Paths include presence flags, ordered headparts,
tints and overlays, scalar arrays/maps, Fallout 4 nested morph channels, and
Skyrim RaceMenu nested body morphs, sculpt parts, body overlays, node transforms,
skin overrides, and raw value-table rows. Missing map keys remain missing; they
are never coerced to numeric zero. Warning diagnostics classify unknown JSON fields
as `preset-diff-unsupported-field` and malformed `Plugin|FormID` references as
`preset-diff-unresolved-identifier`; the command does not infer load order or live
providers. Proof: `tools/verification/verify_preset_diff.py` and
`tools/fixtures/m5/p06-preset-diff.ps1`.

`preset resolve --identifier Plugin.esp|FormID --load-order <json>` resolves a
portable identifier only against the supplied K-local map of plugin names to
load-order byte indexes. Missing or ambiguous mappings fail closed; no plugin
name or FormID is guessed from the environment.

Resolution follows the pinned LooksMenu/RaceMenu rule: the serialized local form
portion is masked to 24 bits and combined with the explicit plugin slot. Plugin
names are matched case-insensitively, while malformed identifiers, invalid map
indexes, alternate-data-stream paths, reparse files, and protected/outside-K
maps are refused. Proof:
`tools/verification/verify_preset_resolve.py` and
`tools/fixtures/m5/p06-preset-resolve.ps1`.

`appearance copy` implements the pinned selective Copy/Paste Look merge over
explicit source and target preset files. `--sections` accepts a comma-separated
set of `body-weight`, `body-regions`, `body-sliders`, `overlays`, `skin-override`,
`lm-skin-template`, `outfit`, `face-parts`, `hair-color`, `face-tints`,
`face-morphs`, `face-bone-regions`, `sculpt`, or `chargen-flag` (or `all`). Only
selected typed fields come from the source; unselected fields remain from the
target. The command writes a new K-local destination and refuses duplicate,
empty, cross-game, or NPC-record-only sections. Proof:
`tools/verification/verify_appearance_copy.py` and
`tools/fixtures/m5/p06-appearance-copy.ps1`.

`facegen build-geom` consumes `--edition|--game`, an explicit K-local
`--manifest`, `--npc` (optional when the manifest is self-bound), and a new
K-local `--output`. Strict mode rejects zero applicable head shapes and included
hair/collider/body shapes. The output is a deterministic JSON semantic build
artifact with `artifactKind: facegeom-semantic-build`, source hash, shape
decisions, headparts, morphs, and texture routes. It is intentionally not a NIF
writer; TRI/NIF bytes, live plugin/provider resolution, deployment, and runtime
appearance remain outside this boundary. Proof:
`tools/verification/verify_facegeom_build.py` and
`tools/fixtures/m5/p07-facegeom-build.ps1`.

The Fallout 4 LooksMenu reader is strict and typed. It reports `Gender`,
`HeadParts`, `HairColor`, `Weight`, `BodyMorphs`, `Morphs.Values`,
`Morphs.Presets`, `Morphs.Regions`, `Morphs.Intensity`, ordered `Tints`,
`Overlays`, and `Skin`. `TintOrder` controls the typed tint sequence; unknown root
fields remain diagnostics. Duplicate keys, malformed finite numbers, invalid
shapes, and out-of-range gender/tint values fail closed. Proof:
`tools/verification/verify_looksmenu_load.py` and
`tools/fixtures/m5/p06-looksmenu-load.ps1`.

LooksMenu export validates required `Gender`, `HeadParts`, and three-axis `Weight`
before writing. It omits empty optional sections, pads `Morphs.Values` to five and
`Morphs.Regions` to eight values, emits unpadded uppercase hexadecimal keys, sorts
the tint dictionary while preserving `TintOrder`, and drops zero-percent tint
layers as the pinned save path does. The output is re-inspected as a schema-valid
typed preset; incomplete inputs fail without creating an output. Proof:
`tools/verification/verify_looksmenu_save.py` and
`tools/fixtures/m5/p06-looksmenu-save.ps1`.

The Skyrim SE RaceMenu reader also materializes nested `morphs.default` slider and
face-preset arrays, sculpt divisor/shape blocks, keyed body-morph channels,
`headTexture`, body `overrides`, and the typed `transforms`/`skinOverrides` value
tables. An explicit `morphs.sculpt: null` is accepted as "no sculpt data"; any
malformed non-null sculpt payload remains a fail-closed shape diagnostic.
Portable IDs remain unresolved until `preset resolve`; unsafe texture paths,
invalid weight/hair-color ranges, malformed sculpt rows, and unsupported transform
variants fail closed. Proof:
`tools/verification/verify_racemenu_load.py` and
`tools/fixtures/m5/p06-racemenu-load.ps1`.

RaceMenu export emits a deterministic canonical nested `.jslot`. The writer
preserves actor/headpart/tint data, nested `morphs.default`/`custom`/`sculpt`,
keyed `bodyMorphs`, and the typed `overrides`, `transforms`, and `skinOverrides`
value tables. It writes only a new K-local destination, validates finite/range
and safe-path constraints, and the output is re-inspected through the same typed
reader. Proof:
`tools/verification/verify_racemenu_save.py` and
`tools/fixtures/m5/p06-racemenu-save.ps1`.

## M5 FaceGen safety commands

`facegen diagnose` and `facegen verify` accept `--game|--edition fallout4|skyrimse`
and an explicit K-local `--manifest` using schema version `1`. The manifest is a
typed inventory of candidate shapes, roles, source asset paths, vertex counts,
topology hashes, and applicability/output flags. Diagnosis is read-only;
`facegen verify --strict-shapes` rejects zero applicable head shapes and any
included hair, collider, body, or other non-head shape. These commands verify
applicability metadata only; they do not claim to generate or render NIF, DDS,
FaceGeom, or FaceTint artifacts.

`facegen build-geom` applies that strict check and writes the new semantic JSON
artifact described above. It records every candidate shape's inclusion decision
plus explicit headpart, morph, and texture-route metadata from the manifest,
never overwrites, and refuses protected roots. A game-loadable FaceGeom NIF is
not claimed until a vetted NIF codec and provider binding are available.

`facegen build-geom-nif --edition|--game fallout4|skyrimse --asset-root
<K-local-root> --source <head.nif> --morphs <JSON-array|@K-local-file> --output
<new.nif>` is the separate morph-aware sandbox route. It binds the source NIF
and adjacent `.tri`/`chargen.tri` hashes, imports them through the pinned
Blender/PyNifly profile, applies any finite morph values unchanged (with a
`facegeom-binary-morph-extended-range` warning per value outside `-1..1`), bakes the
evaluated mesh, and refuses zero/unknown/duplicate morphs, unsafe paths,
reparse paths, stale tools, existing outputs, and malformed inputs. The typed
response includes `baseVertexSha256`, `bakedVertexSha256`, `importMode`, and
`runtimeAuthority:false`. Independent proof is
`tools/verification/verify_facegeom_binary.py` plus
`tools/fixtures/m9/p10-facegeom-binary.ps1`. This remains a K-local sandbox:
winning plugin/race/headpart provider resolution, game-specific loadability,
deployment, and runtime appearance are separate gates.

The same command exposes the explicit finished-head transport route:
`facegen build-geom-nif --mode transport --transport-profile
complete-carrier|geometry-into-carrier --edition skyrimse --asset-root
<K-local-root> --source <head.nif> --source-sha256 <sha256> --output <new.nif>`.
For legacy discovery, `facegen build-geom-nif --help` prints only this command's
complete bake and transport option rows. `schema export --command
"facegen build-geom-nif"` publishes the same rows, including the accepted
`mode` and `transport-profile` values and the conditional carrier options;
the command remains `legacy` in protocol-2 capabilities.
Transport is an independent Bethesda NIF byte/readback operation: it requires
`--source-sha256` and `--transport-profile`, supports only `skyrimse`, never
invokes Blender or PyNifly, accepts an omitted, empty, or all-zero `--morphs`
list, and refuses any non-zero morph. `complete-carrier` forbids `--carrier`,
`--carrier-sha256`, and `--shape`; it qualifies the source as
`RaceMenuExportedComplete`, copies its bytes exactly through a temporary file,
reopens the result, and never overwrites an existing destination. Its typed
response uses `importMode:nif-transport-complete-carrier`, empty `triFiles`,
independently equal `baseVertexSha256`/`bakedVertexSha256`, and
`runtimeAuthority:false`.

`geometry-into-carrier` additionally requires `--carrier <carrier.nif>
--carrier-sha256 <sha256> --shape <exact-shape-name>`. Bake rejects all of
these transport-only options. The source and carrier
must pass independent readback, exact count/descriptor/stride/payload-length,
and triangle-topology checks; only the selected contiguous source vertex array
is copied into the carrier. The output is reopened independently and all bytes
outside that range must remain carrier-identical. Its response uses
`importMode:nif-transport-vertex-array` and additive `carrierVertexSha256`
for the carrier preimage. Transport is Skyrim SE-only and refuses Fallout 4
with a typed edition diagnostic; Fallout 4's existing bake path is unchanged.

`facegen build-geom-bound --edition|--game fallout4|skyrimse --data-root
<copied-Data> --plugins <load-order> --npc <form> --morphs
<JSON-array|@K-local-file> --output-root <K-local-root>` is the provider-bound
follow-on route. It resolves the canonical FaceGeom NIF for the explicit
winning plugin, requires exactly one copied loose source plus adjacent TRI
evidence, rechecks source hashes, and delegates to the pinned exporter. The
new NIF is written at the same canonical relative path below a distinct output
root; the response records provenance, source/output/TRI hashes, morph digests,
and `runtimeAuthority:false`. It refuses duplicate plugins, missing/reparse
sources, root overlap, unsafe roots, and existing outputs. Independent proof is
`tools/verification/verify_facegeom_bound.py` plus
`tools/fixtures/m9/p13-facegeom-bound.ps1`. It does not serialize or deploy
plugins/archives, prove live loadability, or claim runtime appearance.

`facegen build-tint` accepts the same explicit game/manifest boundary plus an
optional `--npc`, `--resolution`, `--format bgra8|bc3|bc7|uncompressed`, `--mips 1`,
`--alpha preserve|opaque`, `--provider-root <K-local-texture-root>`, and
`--dds-output <new-dds>`. It writes a new
`facetint-semantic-build` JSON artifact with square dimensions, storage/channel
conventions, the exact ordered layer inputs, deterministic semantic pixel probes,
and (when requested) the output DDS path/hash. BGRA8 is emitted directly; BC3 and
BC7 are encoded by the hash-pinned K-local DirectXTex boundary and independently
checked for dimensions, block payload, FourCC, and BC7 DXGI format. The operation
fails closed on bad dimensions, unsupported formats, duplicate keys, unsafe paths,
existing outputs, missing codec admission, or codec/header mismatch. The encoded
bytes represent either the explicit composed raster or, when `--provider-root`
is supplied, the sampled raster from the manifest's relative DDS layers. The
artifact records `rasterSource` and ordered source hashes/dimensions. Live
plugin/RACE/CLFM provider resolution, deployment, and runtime appearance remain
separate.

The independent fixture verifier decodes the bounded BGRA8/BC3 provider inputs,
recomputes the sampled blend and semantic raster hash, and compares the BGRA8
payload; BC7 remains structurally verified until an independent BC7 decoder is
admitted.

`facegen build-tint-bound` is the provider-bound follow-on route. It requires
`--edition|--game`, a copied K-local `--data-root`, one `--npc`, an explicit
comma-separated `--plugins` order, a K-local `--manifest`, a new semantic
`--output`, and a distinct K-local `--output-root`. The manifest must reference
the canonical FaceGen source selected for the edition (FO4
`FaceCustomization/<plugin>/<local>_d.dds`; SSE `FaceTint/<plugin>/<local>.dds`).
The command resolves the originating/winning plugin and ESL-aware local FormID,
requires exactly one copied loose provider with an unchanged SHA-256, samples
that provider through the existing hash-pinned decoder, and writes the DDS at
the same canonical relative path below the separate output root. It refuses
duplicate load-order entries, missing/reparse providers, manifest/path
mismatches, root overlap, unsafe roots, existing outputs, and malformed
manifests. The response records provider/output paths, provenance, source hash,
semantic build evidence, and DDS hashes. It does not write the copied Data
root, serialize a plugin/archive, deploy to a live game, or claim runtime
loadability or appearance. Independent proof:
`tools/verification/verify_facegen_tint_bound.py` and
`tools/fixtures/m9/p11-facegen-tint-bound.ps1`.

`facegen build` accepts `--corrections auto`, the explicit FaceGen manifest, an
optional NPC FormID, and a new K-local JSON output. The manifest may carry one
entry for each named correction: Fallout 4 `ghoul-head-rear`,
`eyebrows-fixed-color`, and `mouth-vanilla-fix`, or Skyrim SE
`sse-neutral-detail` and `sse-overlay-fold`. Each entry has an explicit boolean
trigger plus typed before/after values. Triggered entries must change their
value; non-triggered entries are preserved byte-for-byte in the semantic plan.
The command reuses strict zero/poison-shape safety and does not mutate NIFs,
resolve providers, deploy assets, or claim runtime appearance.

`facegen bake-all` consumes `--edition|--game fallout4|skyrimse`, an explicit
K-local `--manifests|--batch` JSON file, and a new K-local `--output`. The batch
schema is version `1` with an ordered, unique `manifests` array. Every item is
classified as `passed`, `skipped` (currently only a zero-shape safety result),
or `failed`, and the output records each item's diagnostics and FormID when
available. The report is written even when an item fails, but the CLI returns a
validation failure and emits `facegen-batch-items-failed`; it never hides a
partial batch behind process success. Batch paths, duplicate keys, existing
outputs, and protected roots fail closed. This command writes semantic JSON,
not plugin-target enumeration, NIF/DDS bytes, deployment, or runtime proof.
Independent proof: `tools/verification/verify_facegen_batch.py` and
`tools/fixtures/m5/p07-facegen-batch.ps1`.

`facegen build-plugin` consumes `--edition|--game fallout4|skyrimse`, an
explicit K-local `--manifest|--target` JSON file, `--plugin|--target-plugin`,
and a new K-local `--output`. The version-1 manifest contains `targetPlugin`
and ordered entries with `winningPlugin`, `npcFormId`, and `manifestPath`.
Only entries whose winning plugin matches the requested target are attempted;
other entries are marked `excluded` with no derived output path. Selected paths
are deterministic `FaceGen/<target-plugin-stem>/<FormID>.json` values, and a
selected failure writes the report but returns a validation failure. Duplicate
keys/FormIDs, target mismatches, unsafe paths, existing outputs, and protected
roots fail closed. This is semantic JSON only: live load-order resolution,
plugin enumeration, NIF/DDS/BCn bytes, deployment, and runtime proof remain
outside this boundary. Independent proof:
`tools/verification/verify_facegen_plugin_target.py` and
`tools/fixtures/m5/p07-facegen-plugin-target.ps1`.

`facegen resolve-providers` is a read-only provider-path audit. It requires
`--edition|--game fallout4|skyrimse`, a copied K-local `--data-root`, one
`--npc` FormID, and an explicit comma-separated `--plugins` load order. The
resolver follows the NPC's originating plugin (the first entry in the explicit
override chain), applies the upstream ESL-aware local FormID rule (`0xFE`
light plugins retain only the low 12 bits), and emits canonical FaceGen paths:
FO4 FaceGeom plus `_d`, `_msn`, and `_s` FaceCustomization textures; SSE
FaceGeom plus per-NPC FaceTint, optional FaceDiffuse/FaceNormal, and an
optional shared `facedetailneutral.dds` when `--shared-neutral-detail` is set.
Every path carries loose/archive provider kind, size, and content hash when
present. Schema 2 also carries `npcContext` with the winning copied record's
typed sex, race FormID, and ordered headpart FormIDs. This identity context
explains the inputs used for the path audit; missing providers are explicit
warnings, and the artifact never claims game loadability, deployment, or
runtime appearance. Independent proof:
`tools/verification/verify_facegen_provider_paths.py` and
`tools/fixtures/m9/p08-facegen-provider-paths.ps1`.

`facegen plan-pack` is a read-only dry run of the pinned packer's one-NPC
bundle contract. It requires the same explicit copied-Data `--game`, `--npc`,
and `--plugins` values plus an existing `--anchor-plugin`; `--debug-sandbox`
selects the `_2` loose sources while preserving canonical archive entry names,
and `--shared-neutral-detail` adds the shared SSE neutral-detail entry. The
artifact records source and canonical paths, required/optional status, source
size/hash, and main/textures archive roles. Missing required sources, duplicate
load-order entries, unsafe roots, reparse points, and missing anchors fail
closed. It never compresses, writes, mounts, or deletes BA2/BSA archives or
loose files. Independent proof:
`tools/verification/verify_facegen_pack_plan.py` and
`tools/fixtures/m9/p09-facegen-pack-plan.ps1`.

`facegen pack` materializes that explicit plan into a new K-local package. It
requires the same `--edition|--game`, `--data-root`, `--npc`, `--plugins`, and
`--anchor-plugin` options plus a new `--output-root`. The package contains a
`Data/` tree with the anchor plugin and every present canonical FaceGeom or
FaceTint provider, alongside `facegen-pack.json` containing source-relative
paths, archive roles, sizes, and SHA-256 hashes. The service streams each file,
refuses reparse points, source/output overlap, missing sources, unsafe roots,
empty or oversized files, and existing destinations, then commits the complete
directory atomically. The source Data root is never modified. This is a
K-local installable package artifact only: it does not compress archives,
deploy to a live profile, prove provider precedence, or claim in-game runtime
appearance. Independent proof: `tools/verification/verify_facegen_pack.py`
and `tools/fixtures/m9/p14-facegen-pack.ps1`.

`facegen deploy` consumes a previously verified `facegen-pack.json` with
`--edition|--game`, `--package`, and an explicit K-local copied `--data-root`.
It validates the manifest's edition, source paths, sizes, and SHA-256 hashes,
then preflights every declared plugin, FaceGeom, FaceCustomization, or FaceTint
destination before copying. Identical destinations are idempotent; differing
files, directories, reparse points, source/output overlap, unsafe roots, and
partial manifest sets fail closed. The source package is never modified. This
is an authorized copied-root deployment proof only: it does not prove plugin
provider precedence, game loadability, or runtime appearance. Independent
proof: `tools/verification/verify_facegen_deploy.py` and
`tools/fixtures/m9/p19-facegen-deploy.ps1`.

## Skyrim FaceGeom HairTint region authoring

Registered provisional `preview.227` carries five strict, independently catalogued commands
for assigning two colors across the physical HairTint fields in one exact
Skyrim FaceGeom NIF:

```text
facegen hair-regions analyze
facegen hair-regions propose
facegen hair-regions preview
facegen hair-regions apply
facegen hair-regions verify
```

They analyze an exact hash-bound K-local FaceGeom, require every structurally
recognized HairTint shape to be explicitly assigned Primary, Accent, or
Preserve, preview the proposed bytes off-engine, write only authorized
fixed-width 12-byte tint envelopes to a new NIF, and independently verify all
selected and preserved fields plus every byte outside those envelopes. Shared
physical shader fields cannot receive conflicting roles; shape names never
imply roles; the NIF is never re-exported. UBE FaceGeom byte authoring is
admitted when structurally supported, but full UBE MDNR/OBody NPC composition
remains unsupported. See
`docs/facegeom-hair-regions-cli-llm-cookbook.md` on the recorded source branch
or in the registered package source snapshot. The transaction uses these
closed versioned documents:
The transaction uses these closed versioned documents:

- `npcmanager-facegeom-hair-regions-analysis/1`
- `npcmanager-facegeom-hair-regions-request/1`
- `npcmanager-facegeom-hair-regions-proposal/1`
- `npcmanager-facegeom-hair-regions-manifest/1`

The supported agent workflow is deliberately hybrid. Query `version`,
`capabilities`, and `schema export` against the exact binary with `--protocol
2 --json`. `workspace preflight` remains protocol-2 ready and persists its
reviewed result through a fresh `--intake-output`; schema export publishes that
document as output `npcmanager-reviewed-game-intake/2`. The legacy `facegen
hair-regions preview` command consumes the same reviewed document through its
required `--intake`, alongside required `--request`, `--request-sha256`,
`--proposal`, `--proposal-sha256`, and fresh `--output-root` options. Its
schema export publishes `npcmanager-reviewed-game-intake/2` as an input, but
the command remains legacy and strict protocol 2 still refuses it. An intake
load refusal points back to `workspace preflight --protocol 2` and a fresh
`--intake-output` without changing the existing validation/security
classification.

`analyze` requires `--source`, `--expected-source-sha256`, a new `--analysis`,
and a new `--assignment-template`. It accepts only an ordinary, non-reparse
K-local NIF from 1 byte through 128 MiB and inventories 1 through 64
structurally recognized `BSTriShape`, `BSDynamicTriShape`, or
`BSSubIndexTriShape` owners routed to a `BSLightingShaderProperty` whose shader
type is `HairTint`. Each row carries a stable structural ID, duplicate-name
ordinal, shape/shader/texture-set block IDs, texture routes, exact float bits,
12-byte tint offset, and shared-shader ownership. Every generated role is
`preserve`; names are never used to infer a role.

`propose` requires exact hashes for both the analysis and completed request.
The request must classify every structural ID as `primary`, `accent`, or
`preserve`, provide distinct canonical `#RRGGBB` colors, and assign at least
one physical shader group to each selected color. Shapes sharing one physical
tint field remain separate inventory rows but must have one linked role.
Conflicting shared-shader roles and a completely no-op transaction are
refused. The proposal records every authorized 12-byte envelope, old/new
IEEE-754 bits, actual predicted changed-byte offsets, source and expected
fingerprints, expected output hash/length, and exact output/manifest paths.

`preview` re-hashes the exact request and proposal, requires one reviewed
`--intake`, and publishes one new `--output-root` only after the complete
private bundle is validated. It renders the exact proposed NIF bytes once,
not a cosmetically corrected surrogate. The bundle contains a combined
face-front image, one isolated thumbnail and one role mask per structural ID,
a contact sheet, provider/texture/renderer authority, and a canonical
`npcmanager-facegeom-hair-regions-preview-evidence/1` document. The render
must show the visible label **Off-engine HairTint preview — Skyrim runtime
remains authoritative**. A successful preview still reports
`visualAuthority:false` and `runtimeAuthority:false`.

`apply` requires `--output` and `--manifest` to equal the request/proposal
paths exactly. It reopens and revalidates the authority chain, clones the
bounded source once, changes only the authorized tint envelopes, independently
re-hashes the temporary output, and promotes without overwrite. `verify`
reopens the written output and manifest read-only through the separate
Bethesda Formats parser lineage. It proves selected and Preserve bits,
whole-file change boundaries, exact output hash/length, and unchanged
topology, geometry, skinning, shader ownership, texture paths, and block
routing.

Every JSON response includes artifact paths and hashes, a phase verdict,
stable diagnostics, `visualAuthority:false`, and `runtimeAuthority:false`.
The commands refuse UNC/device/ADS/live-root paths, source/output aliases,
missing output parents, existing destinations, reparse traversal, stale
hashes, unknown or duplicate JSON properties, and surviving rollback
artifacts. The byte transaction can operate on any admitted FaceGeom,
including UBE; full UBE NPC preview composition remains separately unsupported.
The copy/paste automation sequence and golden envelopes are in
`docs/facegeom-hair-regions-cli-llm-cookbook.md`.

## M6 semantic preview scene

`preview render` consumes `--edition|--game fallout4|skyrimse`, a K-local
`--manifest` JSON file, and a new K-local `--output`. The version-1 manifest
contains `npcFormId` and an ordered `assets` array. Each asset has one of the
typed categories `face`, `body`, `hair`, `outfit`, or `accessory`, plus a
normalized relative asset path, explicit provider label, and SHA-256 hash.
The command emits `preview-scene-semantic-build` JSON with category counts and
a deterministic scene hash, refuses duplicate/unsafe assets and existing
outputs, and remains CLI/LLM accessible. Optional `--visible face,body,hair,outfit,accessory`
(or `all`) changes only the named per-asset visibility flags and reports the
visible count. Paired `--asset-root <K-local-root>` and `--image-output <new-png>`
resolve included visible NIFs by hash and invoke the pinned K-local
Blender/PyNifly import-only renderer; the artifact records the canonical PNG
path, hash, dimensions, and imported mesh count. It does not load live providers,
export NIFs, deploy, or prove runtime appearance. Optional
`--morphs bone,vertex,weight,sculpt` (or `all`) independently marks typed morph
entries as applied and reports their values/count without deforming geometry.
Independent proof:
`tools/verification/verify_preview_scene.py`,
`tools/verification/verify_preview_render.py`,
`tools/fixtures/m6/p08-preview-scene.ps1`, and
`tools/fixtures/m6/p08-preview-render.ps1`.

The same manifest may declare a bounded `variants` array. Each variant has a
unique identifier, a qualified outfit reference, explicit base-scene asset
paths, and typed morph overrides. Paired `--outfit Plugin.esp|0xID`
`--variant <id>` options select one variant; the artifact records the selected
identity plus every included/excluded asset and morph. Unknown, duplicate,
cross-bound, or partial selections fail before writing. Independent proof:
`tools/verification/verify_preview_variants.py` and
`tools/fixtures/m6/p08-preview-variants.ps1`.

`preview reroll` accepts the same scene manifest plus `--npc`, a signed
64-bit `--seed`, and a new JSON `--output`. It validates the manifest before
selection and uses a fixed-width SplitMix64 mapping over the declared variant
order, so repeated inputs produce the same selected index and artifact. The
result records every eligible candidate and the selected outfit/variant;
malformed seeds, NPC mismatches, empty catalogs, unsafe paths, and existing
outputs fail closed. This is semantic choice evidence, not live randomization
or pixel rendering. Independent proof:
`tools/verification/verify_preview_reroll.py` and
`tools/fixtures/m6/p08-preview-reroll.ps1`.

Camera and lighting configuration is available through optional
`--camera <id>` and `--lighting <id>` selectors. A version-1 manifest may
declare bounded `cameraPresets` (`id`, `version`, yaw, pitch, distance, FOV) and
`lightingPresets` (`id`, `version`, ambient, and bounded RGB light sources).
When catalogs are omitted, deterministic built-in defaults are used; when a
catalog is present, omitted selectors choose its first declared entry. The
selected versioned presets are included in the scene hash and output metadata.
Duplicate/unknown IDs, non-finite or out-of-range values, unsafe paths, and
existing outputs fail closed. Independent proof:
`tools/verification/verify_preview_presets.py` and
`tools/fixtures/m6/p08-preview-presets.ps1`. Without the optional image route,
this is semantic configuration only; it does not drive a live camera/light rig
or render pixels.

## High-fidelity NPC inspection preview

Registered provisional `preview.227` exposes:

```text
actorwright preview npc --intake <reviewed-intake.json> --plugin <name> --form <id> \
  [--package-manifest <file> --expected-package-sha256 <hash>] \
  --output-root <new-K-path>
```

The command composes the selected Manager-authored NPC from the reviewed
K-local intake and optional highest-priority package overlay. It resolves
plugin records, FaceGeom, FaceTint, hair, eyes, body, outfit, BodyGen evidence,
materials, and loose/BSA providers; records the winning path and SHA-256 for
each input; imports the final baked FaceGeom graph exactly once; and emits six
deterministic 900 by 900 face/body views, role masks, a contact sheet,
diagnostics, and a hash manifest.

CBBE 3BA and COtR are distinct admitted composition routes. UBE runtime
composition is refused with
`npc-preview-runtime-composed-route-unsupported` because Manager cannot
faithfully reproduce MDNR/OBody runtime state off-engine. The result is always
labelled **High-fidelity off-engine preview - Skyrim runtime remains
authoritative**. It never assigns an automatic likeness PASS; a human and an
exact-package Skyrim runtime comparison remain required.

The manifest may also declare an `animations` array. Each clip has a unique
`id`, normalized animation and skeleton paths, a frame count from 1 to
1,000,000, a native FPS from 1 to 240, and an optional additive flag. Select a
clip with `--animation <id>` and provide exactly one `--frame <number>` or
`--time <seconds>`; `--fps <rate>` and `--play true|false` are optional. Time
selections resolve to a deterministic frame, and the artifact records the clip
identity, paths, skeleton, frame/time, playback rate, additive flag, and play
state. Unknown, duplicate, unsafe, malformed, and out-of-range values fail
closed. Independent semantic proof:
`tools/verification/verify_preview_animation.py` and
`tools/fixtures/m6/p08-preview-animation.ps1`. When paired with
`--asset-root <K-local-root>` and `--image-output <new-png>`, the renderer also
resolves copied `.hkx`/`.kf` and skeleton files, imports the selected clip onto
the copied NIF armature, evaluates the requested frame, and records
`animationApplied`, `animationFrame`, and `animationPoseDeformed`; the
independent proof is `tools/verification/verify_preview_animation_render.py`
and `tools/fixtures/m6/p08-preview-animation-render.ps1`. The copied-HKX route
refuses additive clips rather than silently flattening their semantics and does
not claim FO4 behavior-graph/hair-zap semantics, game-loadable export,
deployment, or runtime animation.

`animation list` is the read-only discovery route for the pinned animation
picker taxonomy. It requires `--edition|--game fallout4|skyrimse` and a
K-local `--manifest` containing the same bounded `animations` array. Optional
`--female true|false` applies the upstream gender visibility rule,
`--first-person true|false` opts into first-person-only clips, and
`--filter <terms>` applies an AND match across display name, normalized path,
folder, role, state axes, and gesture category. The schema-version-1 JSON artifact records
the input manifest hash, filter state, total/visible counts, deterministic
ordering, role/folder/category metadata, additive state, gender and
perspective flags, and behavior-graph provenance. Missing behavior-graph
metadata is inferred as false for category-bearing IDLE/dialogue rows and true
for ordinary clips. Independent proof is
`tools/verification/verify_preview_animation_list.py` and
`tools/fixtures/m6/p20-preview-animation-list.ps1`. The route never executes
HKX behavior graphs, loads a game profile, or claims runtime reachability.

`animation tree` accepts the same options and projects the filtered rows into
the pinned picker hierarchy. Ordinary clips are grouped by role and nested
folder, while category-bearing IDLE clips are grouped under `Gestures &
Dialogue (IDLE)` and their event category. Each leaf retains the typed clip
row and its additive, female-only, and non-behavior-graph markers. The
schema-version-1 artifact is deterministic and reuses `animation list`'s
manifest hash binding and fail-closed validation. Independent proof is
`tools/verification/verify_preview_animation_tree.py` and
`tools/fixtures/m6/p21-preview-animation-tree.ps1`; it remains a static
hierarchy contract and does not execute HKX behavior graphs or claim runtime
reachability.

Headwear preview coverage is explicit through optional `--render-headwear
true|false` and `--hair-slots <comma-separated-slots>`. The accepted slots are
FO4 `30,31,32` (Hair Top/Hair Long plus the FaceGen head cull) and Skyrim
`31,41,131,141` (the paired BSDismember top/long groups). Supplying slots
requires the headwear toggle; enabling headwear requires slots. The typed plan
is preserved in the scene, image, and binary-export artifacts. FO4 slot 32 is
kept out of the hair mask and culls declared face-category meshes; the copied
Blender/PyNifly adapters report affected mesh/face counts and fail closed when
a requested edition partition or face asset is absent. Independent hair proof
is `tools/verification/verify_preview_hair_zap.py` and
`tools/fixtures/m6/p08-preview-hair-zap.ps1`; slot-32 proof is
`tools/verification/verify_preview_face_cull.py` and
`tools/fixtures/m6/p19-preview-face-cull.ps1`. This proves copied-asset
partition semantics only; it does not prove a real FO4 asset, game-loadable
serialization, deployment, or runtime appearance.

`preview export-nif` accepts `--edition|--game`, a completed semantic scene
`--scene`, and a new K-local `--output` ending in `.nif.plan.json`. It verifies
the scene kind, edition, NPC FormID, included asset paths/providers/hashes, and
source hash before writing a deterministic `preview-nif-export-plan` artifact.
Add `--asset-root <K-local-root>` and use a new `.nif` output to select the
optional hash-bound Blender/PyNifly binary sandbox route. That route pins the
Blender executable, imports copied NIF assets with their source armature/skin
path intact, evaluates and bakes the mesh before export, and optionally applies
selected vertex morphs from adjacent hash-bound TRI files. It validates the
Gamebryo 20.2.0.7 block table and writes a new output without overwriting.
Responses include `armatureCount` and `deformationMode`, while morphed
responses additionally include the selected names, TRI dependency hashes, and
base/baked vertex digests.
Existing outputs, unsafe roots, reparse inputs, malformed scenes, and provider
hash mismatches fail closed. Independent proof uses
`tools/verification/verify_preview_nif_binary.py` and
`tools/fixtures/m6/p08-preview-nif-binary.ps1`. Neither route claims game-loadable
provider semantics, deployment, or runtime appearance.

## M6 outfit browse/list

`outfit list` accepts `--edition|--game`, an explicit copied `--data-root` (or
one `--plugin` path), optional ordered `--plugins plugin1,plugin2`, and
`--search`. It enumerates `OTFT` records for both games and emits FormID,
EditorID, name, ordered item FormIDs, deletion state, source plugin, and the
winning override chain. Search is deterministic and matches identity or item
FormIDs. Missing or reparse plugins fail closed; no live profile inference,
race/gender compatibility, rendering, or mutation is performed.

`outfit propose` accepts `--edition|--game`, `--plugin`, `--source`, explicit
`--mode new|override`, `--items` as a JSON array or comma-separated list of references,
and a new K-local `--output` ending in `.outfit-proposal.json`. New proposals
also require `--target-form` with a nonzero plugin-local 24-bit FormID. It
verifies the source OTFT, binds the source SHA-256, checks item plugins against
the source and its declared masters, and records only the selected item
references. New proposals require `--editor-id`; overrides inherit the source
EditorID and FormID. Existing outputs, unknown masters, duplicate/null items,
unsafe roots, and malformed values fail closed.

`outfit write` accepts `--edition|--game`, a hash-bound `--proposal`, and a new
K-local `--output` ending in `.esp`. It revalidates the source
hash, source filename, edition, target, item links, masters, and path/reparse
policy, writes exactly one OTFT record, reads the temporary plugin back with
Mutagen, and refuses to overwrite an existing destination. Race/gender
compatibility, asset deployment, and runtime equipment behavior remain outside
this command.

## M6 leveled-list proposal

`leveled-list propose` accepts `--edition|--game fallout4|skyrimse`, a K-local
`--plugin`, a non-null `--list` FormID, `--entries` as a strict JSON array of
`{"item":"Plugin.esp|0x00000801","level":1,"count":1,"chanceNone":0}`
objects, and a new K-local `--output` ending in
`.leveled-list-proposal.json`. Optional `--editor-id`, `--chance-none`,
`--max-count`, `--calc-all-levels`, `--calc-each-in-count`, and `--use-all`
preserve the typed LVLI flags. Entries are ordered, unique, limited to 4096,
and must reference the source plugin or a declared master. Source bytes are
SHA-256 bound; missing/unsafe/reparse sources, null or out-of-range values,
duplicate entries, existing outputs, and malformed JSON fail closed. The
artifact remains a typed proposal boundary until explicitly materialized by
`leveled-list write`.

`leveled-list write` accepts `--edition|--game`, a hash-bound
`.leveled-list-proposal.json` `--proposal`, and a new K-local `.esp` `--output`.
It revalidates the source hash and LVLI record, ordered entry links, flags,
field ranges, and path/reparse policy, writes exactly one ordinary-plugin LVLI
override, reads the temporary binary back with Mutagen, and refuses to overwrite
an existing destination. Skyrim SE `maxCount` and per-entry `chanceNone` must
be zero because those fields are not represented by the supported record model;
nested expansion, FormID allocation, deployment, and runtime behavior remain
outside this command.

## M6 leveled-list preview resolution

`leveled-list resolve` accepts `--edition|--game fallout4|skyrimse`, a K-local
`--list` path ending in `.leveled-list-proposal.json`, a signed 64-bit
`--seed`, and a new K-local `--output` ending in
`.leveled-list-resolution.json`. It verifies the proposal hash and its original
source-plugin hash, then applies deterministic preview-compatible whole-list
ChanceNone, Use All versus one-entry selection, entry ChanceNone, and
Calculate Each In Count draws. The artifact records every decision and the
ordered resolved references. Level/CalculateAllLevels and MaxCount are retained
as metadata because no player-level input is supplied. Reparse, stale/missing,
unsafe, malformed, and existing paths fail closed; no binary plugin or runtime
state is changed.

## M6 armor proposal

`armor propose` accepts `--edition|--game fallout4|skyrimse`, a K-local
`--plugin`, an ARMO `--source` FormID, a strict JSON-object `--patch` (or an
explicit K-local `@file`), and a new K-local `--output` ending in
`.armor-proposal.json`. The patch requires `mode: "new"|"override"`; new
records require `editorId`, while overrides inherit the source EditorID. The
supported typed fields are `name`, `slotMask`, `race`, `maleWorldModel`,
`femaleWorldModel`, `value`, `weight`, `health` (Fallout 4 only),
`armorRating`, `keywords`, and indexed `armorAddons`. Unknown properties,
duplicate references, unsafe model paths, unknown masters, unsupported game
fields, stale/reparse sources, existing outputs, and malformed values fail
closed. New records require an explicit plugin-local 24-bit `targetFormId`;
overrides retain the source FormID. The resulting hash-bound proposal can be
materialized by `armor write --edition|--game <game> --proposal <proposal>
--output <new.esp>`. The writer emits exactly one ordinary-plugin ARMO record,
revalidates the source hash and source/master ownership, refuses overwrites,
and independently reads the temporary binary back before atomic promotion.
It does not allocate FormIDs, emit ARMA/DDS/NIF dependencies, deploy, or prove
runtime rendering.

`armor damage-resist --edition fallout4 --plugin <source-plugin> --source <form>
--damage-resist <json|@K-local-file> --output <new.armor-damage-resist-proposal.json>`
creates an ordered typed Fallout 4 ARMO damage-resistance proposal. The JSON
array uses `damageType` FormReferences and unsigned `value` fields. The command
requires a source ARMO, verifies source/master ownership and the input hash,
refuses duplicate or null damage types, and never overwrites or writes outside
the K-only lab. It is a proposal boundary; binary ARMO mutation and runtime
application remain separate.

`armor-addon propose --edition|--game fallout4|skyrimse --plugin <source-plugin>
--source <form> --patch <json|@K-local-file> --output <new.armor-addon-proposal.json>`
creates a typed ARMA create/override proposal. The patch supports model paths,
slot/race/footstep references, priorities, weight flags, skin textures and
swap lists, plus Fallout 4 material/art-object/sculpt routes. It verifies the
source ARMA and source/master ownership, binds the source hash, refuses
unsupported Skyrim fields, unsafe or non-finite values, duplicate properties,
reparse inputs, and existing outputs, and never mutates a binary plugin.

New-record patches carry an explicit nonzero 24-bit `targetFormId`; override
patches may repeat only the source FormID. `armor-addon write --edition|--game
fallout4|skyrimse --proposal <proposal.json> --output <new-plugin.esp>` consumes
that proposal, writes exactly one ordinary ARMA record, carries forward the
declared masters needed by the selected references, and performs an independent
semantic read-back before atomic promotion. Source-hash, K-only, reparse,
unsupported-field, invalid-weight, and no-overwrite failures remain fail-closed;
provider resolution, deployment, and runtime rendering are separate gates.

With `--models <json|@K-local-file>` instead of `--patch`, the same command
creates an ordered typed ARMO addon-entry proposal. Each object contains
`index` and an ARMA `addon` FormReference; duplicate indexes, null or unknown
masters, malformed properties, unsafe paths, existing outputs, and nonzero
Skyrim indexes fail closed. The artifact binds the source ARMO hash and is
still a proposal boundary, not binary serialization or provider/runtime proof.

`material-swap propose --edition fallout4 --plugin <source-plugin> --source
<form> --patch <json|@K-local-file> --output <new.material-swap-proposal.json>`
creates a typed Fallout 4 MSWP proposal. The patch contains `mode` (`new` or
`override`), optional `targetFormId` (required for `new`), and an ordered
`entries` array; each entry may set
`originalMaterial`, `replacementMaterial`, finite `colorRemapIndex` from 0 to
1, and `treeFolder`. New records require an EditorID; overrides inherit the
source EditorID. Relative material paths are slash-normalized and
traversal-free. Source/hash, K-only, reparse, strict JSON, and no-overwrite
checks fail closed. The command never mutates a plugin or resolves provider
palettes. The paired
`material-swap write --edition fallout4 --proposal <proposal> --output
<new-plugin.esp>` command writes one ordinary MSWP record, refuses stale or
existing inputs, and independently reads back the EditorID, tree folder, ordered
substitutions, and remap values before promotion. It is Fallout 4-only and does
not allocate FormIDs, deploy dependencies, or prove runtime appearance.

`object-template propose --edition fallout4 --plugin <source-plugin> --source
<form> --combinations <json|@K-local-file> --includes <json|@K-local-file>
--output <new.object-template-proposal.json>` creates a typed OBTS proposal.
The combinations object contains `mode`, optional `editorId`, required
nonzero plugin-local 24-bit `targetFormId` for `new` mode, and `items`;
each item carries scalar level/default fields and ordered `keywords`.
`--includes` is an ordered array of `combinationIndex`, `mod`,
`attachPointIndex`, `optional`, and `dontUseAll`. Parent indexes, level ranges,
duplicate/null references, source/master ownership, K-only/reparse paths, and
no-overwrite are fail-closed. `object-template write --edition fallout4
--proposal <proposal.json> [--properties <properties-proposal.json>]
--output <new-plugin.esp>` consumes that artifact, materializes one ordinary
ARMO record, writes the upstream OBTE/OBTF/FULL/OBTS/STOP block, remaps
declared master-local IDs, and independently verifies the raw binary output.
When `--properties` is supplied, its explicit `combinationIndex` rows are
encoded using the upstream 24-byte OMOD property layout and independently
checked. It remains Fallout 4-only and does not allocate FormIDs, deploy, or
prove runtime crafting behavior.

The same command accepts `--properties <json|@K-local-file>` instead of
`--combinations`/`--includes` and writes a new
`*.object-template-properties-proposal.json`. Each row carries `valueType`,
`functionType`, `propertyIndex`, typed `value1Integer`/`value1Float` or
`value1FormId`, `value2Integer`/`value2Float`, and `stepValue`. Supported
Fallout 4 OMOD value types are `IntType`, `FloatType`, `BoolType`, `StringType`,
`FormIDInt`, `EnumType`, and `FormIDFloat`; unknown types, non-finite values,
unsafe roots, and existing outputs fail closed. Optional `combinationIndex`
selects the OBTS combination for later `object-template write --properties`
binding. Integer/boolean/string/enum Value1 and Value2 values are emitted as
the upstream raw signed 32-bit words; FloatType/FormIDFloat values use
single-precision bits.

## M3 change tracking

`changes list --edition <fallout4|skyrimse> --session
<new.changes-session.json> --json` reads an explicit K-local session document
with `baseline` and `working` record arrays. It emits only changed records in
FormID/signature order. Existing records contain stable scalar field diffs;
added or removed records contain a `record` presence marker. Duplicate record
identities, nested field values, malformed identities, unsupported editions,
reparse inputs, unsafe roots, oversized sessions, and existing-output concerns
fail closed. This is a review boundary and does not infer a live editor session,
serialize plugins, or write game-facing records.

`changes update --edition <fallout4|skyrimse> --session
<new.changes-session.json> --record <form-id> [--signature <sig>] --action
reset|delete --output <new.changes-action.json> --json` writes an explicit
reset/delete review proposal. `reset` requires a baseline and records
`restore-baseline`; `delete` records either new-record removal or dropping an
override so the parent wins. The session hash and baseline/working snapshots
are retained in the artifact. No plugin is mutated, and missing input is never
interpreted as an implicit deletion.

`records propose --edition <fallout4|skyrimse> --type <signature> --mode
new|template|override --form-id <24-bit-form-id> --editor-id <id>
[--source <form-id>] [--name <text>] [--masters <json|@K-local-file>] --output
<new.record-proposal.json> --json` emits a typed identity proposal. New records
have no source; template and override records require a source, and overrides
require the source FormID to equal the target. Master names are validated and
deduplicated by the service. The artifact records explicit local allocation and
a canonical request hash; it does not allocate against a target plugin or write
binary records.

`plugin write --edition <fallout4|skyrimse> --proposal <new.npc-proposal.json>
--output <new-plugin.esp> --json` consumes a hash-bound `npc patch` proposal.
The command rechecks the source SHA-256 and proposal/output binding, writes a
new K-local plugin, and independently verifies the selected NPC. The current
typed write boundary accepts EditorID, Name, Sex, SkyrimWeight, Thin, Muscular,
and Fat changes; unsupported richer fields fail closed.

`plugin verify --edition <fallout4|skyrimse> --before <source-plugin.esp>
--after <written-plugin.esp> --proposal <npc-proposal.json> --json` is the
proposal-bound read-only second pass. It checks source hash and path binding,
target field values, preserved subrecords, and TES record/subrecord boundaries.
It returns validation failure on unapproved binary drift.

Absolute `outputPlugin` bindings remain required for ordinary mutation
proposals. A proposal whose `artifactKind` is exactly
`existing-npc-edit-proposal` may use a package-relative output only when the
proposal itself is under that package's `evidence/` directory. The relative
path is parsed as a normalized `AssetPath`, resolved against the package root,
and compared with `--after`; traversal and other proposal kinds fail closed.

`plugin audit --edition <fallout4|skyrimse> --before <source-plugin.esp>
--after <candidate-plugin.esp> --json` is the broader read-only surface pass.
It parses both copied plugins, compares every typed major-record identity and
summary, binds every major record and its containing group path to a canonical
SHA-256 digest,
reports added/removed/changed records, and flags world/scene families
(`WRLD`, `CELL`, `LAND`, `WATR`, `LTEX`, `NAVM`, `NAVI`, `LCTN`, `REGN`, `CLMT`,
`MUSC`, and `IMGS`) for conflict review. Both inputs must already exist under
the K-only workspace; unsafe paths, reparse traversal, malformed plugins, and
duplicate FormIDs fail closed. This command does not resolve live provider
precedence, deploy a plugin, or prove game loadability or runtime appearance.

For `skyrimse`, the reader first performs a bounded raw topology preflight for
the exact interior ancestry `CELL(type=0) -> block(type=2) ->
sub-block(type=3) -> cell-children(type=6)`. A cell-children group whose
immediately preceding sibling is not the matching `CELL` returns the coded
`orphan-cell-children` diagnostic, including the operand role, full path,
offset, expected label, and observed sibling. Truncated headers, invalid group
sizes, parent overruns, and other malformed bytes retain the generic
`plugin-read-failed` diagnostic. This remains static parser evidence only;
it does not prove Skyrim loading or placement.

For an explicit copied-root provider audit, pass both `--plugins-root <K-local
Data-or-plugin-root>` (also accepted as `--data-root`) and `--load-order
<K-local-manifest>` (also accepted as `--loadorder`). The manifest uses the
version-1 `plugins` order contract described above. The response then includes
`providerResolutions`, with one deterministic `(FormID, signature)` winner and
its ordered `overrideChain` for every record found in enabled, existing
plugins. This is a static K-local provider/load-order proof only: it never
infers a live profile, installs files, or proves game execution/runtime
appearance. Supplying only one of the two options fails closed.

`plugin deploy --edition <fallout4|skyrimse> --plugin <K-local-plugin.esp>
--data-root <existing-K-local-copied-Data> --expected-sha256 <sha256> --json`
deploys one hash-bound `.esp`, `.esm`, or `.esl` into the root of an explicit
copied Data tree. The source hash is checked before staging and the temporary
file is re-hashed before same-directory, non-overwriting promotion. An
identical destination is an idempotent success; a different destination,
stale source hash, unsupported extension, missing Data root, or reparse path
is refused without overwriting bytes. The command never edits a load-order
manifest, infers a live profile, writes below the configured protected game root, or claims
game loadability/runtime behavior. `tools/verification/verify_plugin_deploy.py`
and `tools/fixtures/m9/p18-plugin-deploy.ps1` provide an independent dual-game
deployment/conflict/unsafe-root proof.

`plugin write` accepts `--no-overwrite` as an explicit safety flag; disabling
it is rejected. The implementation writes and flushes a same-directory
temporary artifact before non-overwriting promotion and refuses stale hashes,
existing outputs, and reparse traversal.

`runtime smoke verify --edition <fallout4|skyrimse> --runtime-report
<K-local-runtime-smoke.json> --package-acceptance <K-local-package-acceptance.json>
--json` validates one operator-supplied runtime evidence report. It checks the
schema and game, PASS status, package archive hash, target and control-NPC
identity, provider fields, environment-fingerprint path/hash, and the
edition-specific required screenshot names. All referenced files must exist
under the K-only lab and reparse traversal is refused. This is an evidence
contract and safety gate: it does not inspect screenshot pixels, launch a game,
resolve live load order, or turn static/operator input into runtime proof.

`runtime smoke verify-all --fallout4-report <K-local-fo4-runtime-smoke.json>
--skyrimse-report <K-local-sse-runtime-smoke.json> --package-acceptance
<K-local-package-acceptance.json> --json` runs the same typed evidence gate for
both games and returns nested Fallout 4/Skyrim SE results plus one aggregate
status. It is read-only, refuses either game's unsafe or malformed inputs, and
does not turn the aggregate into runtime proof.

`body sidecar write --edition <fallout4|skyrimse> --plugin <plugin.esp>
--npc <plugin-local-form> --sliders <json|@K-local-file> --output
<Plugin.bssliders> --json` emits one typed NPC `bodyMorphs` entry. The plugin
and filename are path-bound, values are finite in `[-1,1]`, and output is
atomic/no-overwrite; the existing sidecar inspector is the reload gate.

`bodygen write --edition <fallout4|skyrimse> --assignments
<json|@K-local-file> --output <root> --json` consumes a version-1 typed
assignment document and writes the edition-specific BodyGen `templates.ini`
and `morphs.ini`. Plugin/FormID syntax, sorted finite morphs, output paths,
encoding, and no-overwrite behavior are validated by the shared generator.

## P11 runtime-script commands

`runtime-script propose --edition <fallout4|skyrimse> --npc <form-id>
--appearance <json|@K-local-file> --plugin <K-local-source-plugin>
--output <new.runtime-script-proposal.json> --json` validates and writes a typed VMAD proposal for the pinned
`NPCM_Manolov_ApplyFO4` or `NPCM_Manolov_ApplySSE` script. The version-1 input
contains a complete typed property array, optional explicitly parsed object
references, and optional non-overlapping instruction fragments. Papyrus arrays
must be non-empty and parallel groups must have equal lengths. The proposal is
bound to the copied `NpcApplyScriptEmitter.vb` hash and, when `--plugin` is
provided, to the source plugin SHA-256 for binary materialization. Proposal
output is atomic/no-overwrite.

`runtime-script write --edition <fallout4|skyrimse> --source <K-local-plugin>
--proposal <runtime-script-proposal.json> --output <new.esp> --json`
rechecks the source binding and materializes the pinned apply script into the
target NPC's VMAD. It preserves non-owned scripts, replaces only the reserved
`NPCM_Manolov_` family, writes the exact game-specific property set, refuses
object-reference/fragmented inputs that cannot be safely bound, and independently
round-trips the copied plugin. It never overwrites an output, installs PEX files,
deploys to a game root, or claims Papyrus/runtime behavior.

`runtime-script build --edition <fallout4|skyrimse> --source-root <K-local
upstream-copy> --output <new.runtime-script-build.json> --json` runs the pinned
Caprica `pex-inspect` tool over the matching `src_*`/`pex_*` apply-script pair,
compares the PSC and PEX property APIs, and records source/PEX/inspection,
compiler-manifest, dependency, and flags evidence. It also writes a persistent
`.pex-inspect.json` sidecar next to the evidence artifact. The route is
precompiled inspection evidence only: no compile, deployment, binary VMAD
mutation, or Papyrus runtime proof is implied.

This command is unavailable in the current public source snapshot: it requires
the exact pinned patched Caprica distribution and manifests described in
[docs/licenses.md](licenses.md), which are not included. Unmodified upstream Caprica is not
a compatible substitute. The `runtime-script package` command can use a supplied
precompiled PEX without Caprica, and the core application build does not
depend on Caprica.

`runtime-script package --edition <fallout4|skyrimse> --source-root <K-local
upstream-copy> --output-root <new-package-directory> --json` copies only the
game-specific `NPCM_Manolov_ApplyFO4.pex` or `NPCM_Manolov_ApplySSE.pex` into
`Data/Scripts/` and writes `runtime-script-package.json` with source and output
SHA-256 hashes. The package is created as a new K-local directory, refuses
existing destinations and unsafe/reparse paths, and never includes native
stub PEX files. `tools/verification/verify_runtime_script_package.py` checks
the source bytes, exact relative path, copied bytes, hashes, lengths, and
sidecar closure independently. It does not deploy to a game root or claim
provider/load-order integration or Papyrus/runtime execution.

`runtime-script deploy --edition <fallout4|skyrimse> --package
<runtime-script-package.json> --data-root <K-local-copied-Data> --json` installs
the source-bound game-specific apply PEX into `Data/Scripts` below the copied
Data root. It checks the package manifest, exact relative path, package/output
hashes, size, K-only/reparse boundaries, and protected-root refusal. An
identical destination is an idempotent success; a differing destination is a
validation failure and is never overwritten. This is copied-root deployment
evidence only, not live provider/load-order integration or Papyrus execution.

`runtime-script inspect-vmad --edition <fallout4|skyrimse> --plugin
<K-local-plugin> --npc <plugin-local-form-id> [--script <NPCM_Manolov_Apply*>]
--json` is a read-only copied-plugin inspection. It locates the requested NPC's
game-specific apply script, reports the plugin SHA-256 and ordered VMAD property
names, and sets `noWrite=true` and `runtimeProof=false` in the typed artifact.
The command refuses wrong-game scripts, missing/reparse/protected plugins, and
non-plugin-local FormIDs. Independent proof is
`tools/verification/verify_runtime_script_vmad_inspect.py` in
`tools/fixtures/m7/p11-runtime-script.ps1`; it does not infer live load order,
deploy PEX, execute Papyrus, or claim in-game behavior.

## M5 BodyGen sidecar command

`bodygen build` accepts `--game|--edition fallout4|skyrimse`, `--plugin`, `--npc`,
`--mod-name`, a K-local `--morphs` JSON file, and a K-local `--output-root`.
The input schema is version `1` with a non-empty `morphs` array of unique safe
names and finite values from `-1` to `1`. The command writes exactly two new
UTF-8, newline-terminated INI files under the game-specific BodyGen path,
using a deterministic template name and ordinal morph ordering. Existing
destinations, reparse-point traversal, malformed or duplicate input, and paths
outside the lab fail closed. The output is only typed sidecar evidence; it does
not generate meshes, invoke BodySlide, edit plugins, deploy to a game root, or
prove runtime application.
The `--npc` value is the plugin-local 24-bit FormID; a load-order byte must be
resolved explicitly before this command and is rejected when still present.

## M4 BodySlide sidecar inspection

`body sidecar inspect` accepts `--game|--edition fallout4|skyrimse` and an
explicit K-local `.bssliders` file. It validates schema versions 1 through 11,
the sidecar/plugin filename relationship, `Master.esp|HEX6` NPC keys, finite
numeric values, safe relative asset paths, duplicate/unknown fields, and
edition-specific SSE data. JSON output is schema-versioned and reports typed
body morph maps plus counts for overlays, transforms, skin overrides, custom
morphs, sculpt data, and tint textures. Valid input receives source and
canonical hashes after a stable in-memory parse/write round-trip; invalid input
returns no partial document or NPC state. The command never writes the source,
invokes BodySlide, resolves TRI catalogs, generates meshes, or proves runtime
application. Sidecar writing remains a separate future command.

## M5 preset-to-NPC package composition

`pipeline preset-to-npc` accepts `--format looksmenu|racemenu-jslot`, matching
`--edition|--game`, K-local `--preset`, K-local `--source-plugin`, a new
`--plugin`, plugin-local `--npc`, safe `--mod-name`, and an existing K-local
`--output-root`. Optional `--editor-id` and `--name` values override the typed
NPC identity fields. The command inspects the preset, creates the existing
hash-bound NPC mutation (including LooksMenu `Morphs.Values` to Fallout 4
NPC.MRSV), emits typed BodyGen sidecars for preset body morphs,
and writes `npcmanager-package.json` with artifact sizes and SHA-256 hashes.
JSON output is versioned through the command protocol and includes completion,
plugin, BodyGen files, manifest, and diagnostics. Any failed stage rolls back
new artifacts; existing outputs, stale inputs, unsafe paths, cross-game format
mismatches, invalid weights, and invalid body morphs fail closed. This bounded
slice does not generate FaceGeom/FaceTint, meshes, overlays, scripts, deploy to
a game root, or prove runtime appearance.

## Gate 3 complete RaceMenu preset to existing NPC

`npc create-from-preset --request @<K-local-json> --request-sha256 <sha256>`
uses the same strict request loader and build service as the desktop. A
schema-version-1 request creates a new NPC. Schema version 2 adds the required
`existingNpcTarget` object with a workspace-relative copied source plugin,
its SHA-256, and a nonzero plugin-local 24-bit target FormID.

Schema version 7 is additive and new-NPC-only. It extends the direct
RaceMenu/CharGen standalone asset route with `bodySlidePresetAuthority` and
`bodyMeshAuthority`. The preset authority binds the exact K-local BodySlide XML,
its SHA-256, parsed preset name, target `set`, groups, and slider count. The mesh
authority binds already-generated K-local body, hands, and feet `_0`/`_1` NIFs,
their package destination paths, SHA-256 values, and the exact BodySlide preset
authority they came from. The manager consumes staged meshes only; it does not
run BodySlide in this mode. When external mesh authority is selected, `.jslot`
BodyGen output is suppressed by default so RaceMenu `bodyMorphs` do not stack on
top of the generated BodySlide body. Schema 7 requests without complete
body/hands/feet mesh authority are refused with
`racemenu-assets-schema7-body-mesh-authority-required`.

For schema 7 visual NPCs, the builder materializes output-owned body, hands, and
feet ARMA records plus a private ARMO naked skin and routes NPC `WNAM` to that
private skin. The whole-skin authority still owns texture/provider closure; the
BodySlide mesh authority owns only the generated body mesh files. Runtime
authority remains false until in-game visual proof is captured.

New-NPC execution requests may independently set
`allowInheritedMeshEmbeddedSkinTextureRoute: true`. This opt-in is for custom
races such as UBE whose inherited naked-skin ARMA records omit female TXST and
instead carry texture paths in their loose NIF shader sets. During the
Manager-owned preset-selection transaction, the whole-skin resolver hash-binds
the winning body, hands, and feet NIF providers, parses their embedded DDS
routes, and resolves the exact copied texture providers. The field defaults to
false, so existing schema 1-7 requests preserve the legacy TXST-only rule.
Missing, archive-provided, malformed, reparse, outside-K, or unresolved routes
still fail closed. This capability does not create a private WNAM skin, copy
body meshes, invoke BodySlide or OBody, or confer runtime authority.

Schema 7 may also carry an optional `externalCharGenExportAuthority` reference.
The referenced schema-1 manifest binds a K-local `.jslot`, genuine RaceMenu
CharGen `.nif`/`.dds` export pair, their SHA-256 values, the exact race and sex,
an explicit operator visual confirmation, and `runtimeAuthority: false`. The
Manager stages a verified same-stem copy for the ordinary selection transaction,
qualifies the exported NIF with the bounded `RaceMenuExportedComplete` profile,
and preserves every routed shape before rewriting only the final head texture
set. Existing schema 7 requests without this authority retain the Manager-built
carrier route. Hash, race, sex, confirmation, malformed graph, unsafe path, or
staging drift is refused; the authority does not prove that Skyrim rendered the
export.

Chel's exact UBE export passed this static contract but produced no visible
runtime likeness movement. Direct `RaceMenuExportedComplete` consumption is
therefore burned as a Chel/UBE likeness route. Chel v0.9 subsequently achieved
user-confirmed qualitative likeness through a materially different external
runtime route: the Manager creates the schema 6 UBE actor host, MDNR 2.1.4 asks
RaceMenu to load the exact JSlot face/headparts, Manager VMAD retains ownership
of overlays and the accepted transform, and OBody owns the exact BodySlide XML.
All MDNR optional `applytype` switches remain false to prevent duplicate
application. This reference procedure is documented in
`docs/ube-preset-to-npc-runtime-procedure.md` and frozen in
the historical consumer-owned record `accepted-chel-ube-runtime-mechanism-v0.9.json`.
MDNR/OBody execution remains outside the CLI contract, and the retained
qualitative verdict does not confer complete formal runtime authority.

RaceMenu new-NPC requests may also set
`faceGeomSkeletonAuthority` to `sourceModelWorldTranslations` or
`identityFaceGenBones`. The source-model mode is the default for every existing
request and preserves legacy behavior. The identity mode is an explicit,
bounded carrier-assembly policy for source meshes whose `NiSkinData`
skin-to-bone transforms already own the bind placement. It writes zero
translations on the Manager-created FaceGen bone
nodes while preserving source skin data, topology, vertices, and materials.
The Manager does not infer this mode from race or filenames. Unknown enum
values fail request loading, and successful use reports
`sse-facegeom-carrier-identity-skeleton-authority`. This remains static
authority only. The Chel UBE `20260725-4` runtime test loaded the exact
sole-provider output but showed no dramatic likeness change, so this policy is
not runtime-qualified for Chel or for UBE generally. It must not be
auto-selected from UBE race, preset, or filename evidence.

`npc create-from-jslot` carries the exact reviewed
`faceGeomSkeletonAuthority` through temporary companion generation and the
final new-NPC transaction. The selection transaction may replace only the
accepted preset path/hash; it preserves this authority field. Explicit
`identityFaceGenBones` requests without a qualifying Manager-owned carrier
still fail through the existing carrier gates rather than falling back.

The same command accepts one exclusive preflight mode. `--preflight-output
<new.json>` writes a read-only canonical NPC build plan and returns before any
companion or final-output directory is created. Execution may instead provide
the exact `--reviewed-preflight <json>` and
`--reviewed-preflight-sha256 <sha256>` pair. Actorwright regenerates the whole
production-derived plan immediately before staging and returns
`preflight-derived-plan-stale` if product identity, input authority, effective
appearance, dependency closure, gates, diagnostics, or planned outputs no
longer byte-match. Existing invocations without either mode run the identical
preflight in memory. Preview prerequisites are optional gates and never turn a
verified game-artifact build into a failure; all preview and runtime authority
remain false until their separate proof.

The existing-NPC route retains source identity/gameplay fields and writes a
fresh ordinary override ESP with the source plugin as a master. The target must
reopen as exactly one source-owned NPC override and zero self-owned duplicates.
The complete preset-derived race, sex, headparts, hair color, private head TXST,
weight, native morphs, tints, QNAM, and product VMAD are proposal-bound and read
back independently. FaceGeom and FaceTint use the canonical source-plugin/local-
FormID path; Skyrim BodyGen uses the same source identity in its folder and
assignment row. All generated/evidence/runtime files are declared in
`npcmanager-package.json`; `package verify` must report no undeclared files and
`runtimeProof=false`.

JSON output includes `targetMode` (`new-npc` or `existing-npc`), the effective
local FormID, override ESP and hashes, origin-keyed FaceGeom/FaceTint, BodyGen
sidecars, manifest, the 36-property VMAD count, and diagnostics. Completion is
strictly `STATIC_PASS_RUNTIME_REQUIRED`; it does not claim that Skyrim loaded,
executed, or rendered the package.

## P12-009 reference/description preset authoring

`preset design-propose --intake @<authoring-intake.json>
--intake-sha256 <sha256> --output <new-root>` reopens the schema-1 intake,
decodes its bounded reference images, runs the admitted offline detector and
landmarker, interprets the bounded description vocabulary, and projects the
fixed semantic anchors. The output root contains canonical
`authoring-intake.json` and `landmark-proposal.json`. It intentionally does not
invent a reviewed headpart/tint selection, create a resource snapshot, write a
JSlot, or claim likeness.

`preset create-from-reference` requires the exact
`--proposal/--proposal-sha256`, `--review/--review-sha256`, and
`--resource/--resource-sha256` chain, plus `--jslot-output` and a new
`--evidence-root`. The intake path/hash are recovered from the canonical
`authoring-intake.json` beside the inference proposal. Without `--apply`, the
transaction writes and reopens only the deterministic authoring proposal,
render comparisons, and declared evidence. With `--apply`, it also requires
`--accepted-proposal-sha256` equal to the proposal-only output and writes the
verified RaceMenu JSlot at exactly `--jslot-output`.

`npc create-from-reference` accepts the same proposal/review/resource and
accepted-proposal hashes, plus the existing reviewed `--request
@<npc-request.json> --request-sha256 <sha256>`, copied `--data-root`, ordered
comma-separated `--plugins`, a new `--transaction-root`, and `--apply`. It
creates no second NPC serializer: the verified JSlot path/hash replaces only
the preset fields in the supplied request before calling the existing
RaceMenu-JSlot NPC build service.

All three commands reject unknown options, malformed hashes, stale authority,
existing destinations, undeclared output, and cancellation. Human and JSON
outputs state `runtimeAuthority=false`. CPU render comparisons, JSlot readback,
and downstream static package verification do not establish Skyrim runtime
likeness or release authority.

## Skyrim paired-follower finishing

`npc follower-finish pair-analyze --request <request.json>
--request-sha256 <sha256> --proposal <new-proposal.json> --json` admits the
`skyrim-paired-follower-finish` request, re-hashes both accepted source
packages and every declared provider, validates exact actor/anchor/outfit and
allocation authority, and writes a deterministic no-overwrite proposal.
Legacy schema 2 preserves the accepted companion byte-for-byte. Schema 3 may
also finish only its explicitly bound HairTint and private outfit surfaces;
all other companion bytes remain protected.

`pair-apply` additionally requires `--proposal-sha256`. It reopens the request
and proposal, writes only the subject plugin and its admitted sidecars into a
new package tree, copies the companion package unchanged, creates a
deterministic direct-install ZIP, and emits a hash manifest plus Manager
post-write evidence. The K-local transaction tree retains `Data/`, evidence,
README, and manifest; the ZIP projects `Data/` contents and both plugins to ZIP
root, adds the runtime README explicitly, and excludes transaction-only
evidence so Mod Organizer does not require manual folder repair.

`pair-verify` requires the same request/proposal binding and the produced
`--manifest`. It reopens the installed tree and both plugins, checks the exact
record/link/master and asset surfaces, and rejects undeclared output,
substitution, drift, forbidden broad-world signatures, path aliases, reparse
points, existing output destinations, and malformed binary shapes.

The paired commands do not deploy or activate mods and always report
`runtimeAuthority=false`. Runtime placement, pathing, appearance, outfit
continuity, recruitment, trading, dismissal, and save/load remain a user-run
Skyrim checkpoint.

## P12 package inspection and verification

`package inspect --manifest <K-local npcmanager-package.json> --json` reads the
typed pipeline manifest and validates its schema, identity fields, safe relative
artifact paths, positive sizes, and SHA-256 shape without modifying the package.
`package verify` performs the stronger written-artifact check: it re-hashes every
declared file below the manifest root, compares sizes and hashes, rejects
reparse paths and undeclared files, and returns `runtimeProof=false`. Both
commands are read-only and refuse manifests outside K.

`package build --source-root <verified-package-root> --output-root
<new-package-root> --json` first runs the same verifier, then copies only the
manifest and declared artifacts into a new K-local directory using streamed
hash-bound writes. It never overwrites an existing destination or changes the
source package. The resulting directory is a staging/installable package, not a
live deployment or runtime proof. Rational proof is in
`tests/NpcManager.Cli.Tests/Program.cs`.


### Skyrim audit master indices and skeletal worlds

`plugin audit --normalize-master-index` compares complete original record bytes after relocating only FormID bytes demonstrated by typed serialization into a common master layout. Unmodeled subrecords and owner-qualified record identities remain part of the comparison. Ordinary audit retains physical master-index differences. The option accepts `true` or `1` and currently applies to Skyrim Special Edition; other editions receive an explicit refusal.

A structurally bounded world-children group without its preceding WRLD parent is reported as `plugin-audit-skeletal-world-parent`. Audit retains every raw record, its full bytes, owner and exact numeric group labels; it does not claim complete world semantics. Ordinary plugin readers and malformed interior CELL guards remain strict. Combining that incomplete world graph with master normalization receives `plugin-audit-normalization-unavailable`, because its typed relocation cannot be proved. Compressed records whose FormLinks require unaligned relocation likewise refuse; their bytes are never guessed.

### Fresh-plugin consolidation and ESL eligibility

`npc edit-package --edition skyrimse --input-plugin <copied.esp> --input-sha256 <sha256> --consolidate <provider.esp[,provider.esp]> --esl-flag --output <fresh.esp> --json` is a separate mode from scalar/package editing. Either `--consolidate` or `--esl-flag` can be used alone. Do not combine it with `--npc`, scalar edits, `--output-root`, `--plugin`, or `--output-kind`. The existing standalone-copy edit retains its full raw record inventory.

Consolidation snapshots and hashes copied providers, imports provider-owned top-level records at fresh IDs, remaps typed FormLinks, preserves existing IDs and master-qualified owners, removes consolidated masters, and rebases copied SEQ quest IDs. Provider overrides, localized plugins, nested provider groups, ambiguous record identities, dangling closure and unproven compressed reference changes are refused. Output-local links and the complete inventory are independently reopened. The output `.consolidation.json` records input hashes, the owner-qualified mapping, plugin/SEQ hashes and static verification; it is evidence, not an installable package manifest. Existing output files are never overwritten.

`--esl-flag` sets TES4 flag0x200 only when all records use Form44, owned object IDs and next object ID fit0x000..0xFFF, and no CELL, WRLD, ACHR, REFR, NAVM or NAVI records exist. It never compacts existing IDs. These checks establish static eligibility, not game-runtime or visual verification.

### Explicit preset load-order resolution

`preset resolve --identifier "Plugin.esp|0x800" --load-order <absolute JSON path>` accepts the workspace schema-1 document (`schemaVersion: 1`, `edition`, and `plugins` rows with `name`, `order`, `enabled`). Enabled rows are sorted by `order`; disabled targets and duplicate names/orders refuse. The legacy `{ "Plugin.esp": 4 }` map retains its explicit byte indexes.

Optional `--data-root <absolute copied plugin directory>` reads ordinary TES4 headers and assigns separate full and light indexes in that declared order. Light FormIDs use the FE prefix, a twelve-bit light index and a twelve-bit local ID; full IDs keep a twenty-four-bit local ID. Without a copied Data root, schema-1 entries are treated as full plugins. This resolves the supplied snapshot and makes no claim about a running game or live profile. Invalid JSON/schema/header/path input returns a typed diagnostic without a resolved ID.

### Inherited blank-fixture defaults and explicit AIDT

Successful Skyrim `npc create-from-jslot` and `npc inspect` results include `inheritedDefaults`: `class`, `combatStyle`, `aidt`, `packages`, `defaultPackageList`, `defaultOutfit`, `voice`, `level`, and `namePlaceholder` appear only when the observed value equals the authenticated `blank-npc-v1` template. References are compared with their plugin owners; AIDT compares its entire subrecord. Equality is an observation, not proof that a value was historically inherited. An unavailable comparison produces an explicit diagnostic rather than an empty list claiming no defaults remain. This is not a runtime or follower-behavior approval.

`npc patch --aidt '{"aggression":"aggressive","confidence":"brave","morality":"noCrime","assistance":"helpsFriendsAndAllies","energy":73}'` supplies one or more Skyrim AI fields. `@<workspace-file>` is also accepted. `plugin verify` accepts the same expectation. The emitted patch proposal includes only supplied AIDT fields; absent `--aidt` keeps the previous proposal shape and byte preservation. Energy is an integer 0–255; enum names, object keys and duplicate fields are validated. Mood, flags and other AI bytes remain unchanged and are independently checked. Patching requires existing source AIDT, never guessed values for missing fields.

`helpsNobody` refuses when the effective post-patch faction list contains Skyrim's PotentialFollowerFaction or CurrentFollowerFaction, including faction additions in the same request. A non-follower may retain `helpsNobody`. Existing apply source-hash binding, fresh output requirements and unrelated-field preservation still apply.
