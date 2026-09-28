# Actorwright product capabilities

This is the repository-facing index for humans and fresh agents. It identifies
the product, the supported binary release, the machine-readable command
surface, and the difference between implementation, static verification, and
Skyrim runtime authority.

## Product identity

Actorwright is an independent GPL-3.0-only,
source-informed C#/.NET Windows reimplementation of the pinned FO4 NPC Manager
feature surface. It supports Fallout 4 and Skyrim SE/AE bounded editor
operations and is the default workspace author for supported Skyrim RaceMenu
JSlot-to-new-NPC requests.

It is not:

- the old FO4 NPC Manager 1.3.4 research executable;
- the separate `SkyrimNpcCli` Option-A plugin utility;
- a general Creation Kit replacement;
- evidence that Skyrim loaded or rendered a statically verified artifact.

## Authority order

When two files disagree, use this order:

1. `src/NpcManager.Application/BuildInfo.cs`, `tools/build/package.ps1`, and
   `tools/release/verify_release.py` for current source/package identity and
   package gates.
2. `src/NpcManager.Application/CommandContracts.cs` and
   `actorwright capabilities --json` for the command surface of the source tree being
   executed.
3. `src/NpcManager.Application/AgentCommandRegistry.cs` and
   `capabilities --protocol 2 --json` for protocol 2 readiness.
4. `tests/fixtures/` and their named verification reports for product-owned
   reproducible inputs; these do not grant consumer runtime authority.
5. `docs/releases/` for frozen release history and the exact candidate manifest
   for package identity; separate consumer evidence governs game-facing state.
6. `tasks/todo.md` plus the newest paired verification report for unfinished
   source-candidate work.

Historical plans and reports explain decisions but do not silently supersede
these current authorities.

The approved [September 2 implementation plan](superpowers/plans/2026-09-02-v1-v2-command-surface-and-npc-production.md)
records preview.266 as packaged, approved on 2026-08-26, and installed by the
consumer at that time. That historical baseline has release evidence retained in
[the preview.266 release note](releases/1.0.0-preview.266.md). Consumer installation
is governed by separate current consumer receipts; this source index does not
assert which package is installed.

The current source contract is `1.0.0-preview.281` with source line
`preview.281-public`, as declared by `BuildInfo`. It retains six protocol-1
custom voice/dialogue commands for 142 total and the eleven-command
protocol-2-ready set. Phase 3 adds an opt-in policy-shadow comparison for
K-only workspace-policy decisions while the existing policy remains
authoritative. The compiled selector inventory was introduced in Preview.280;
its 221-route evidence across 12 executables is historical and is not
attributed to Preview.281. The release-attached verification receipt records
Preview.281 candidate-specific build, package, and gate results. Preview.280 is
the previous private source identity; its local release and installation are
documented by its retained artifacts and receipt. Its NTFS execution and remote
CI were explicitly deferred and are not claimed as passed. Preview.279 remains
an older private rollback identity. Preview.277 is a tagged, independently
verified, candidate-published private release; approval and installation
status require their corresponding receipts. Preview.276 is the tagged,
independently verified, candidate-published, approved private release with a
frozen 880-passed/four-skipped Python acceptance. Its package and note remain
historical and its results are not attributed to the current Preview.281
source.
Preview.275 remains an immutable tagged, independently verified, candidate-
published, and approved package at 142 commands and 317 expected Python passes.
Preview.274 remains a historical tagged rollback package at 142 commands.
Previews 267 through 271
remain immutable failed release-builder checkpoints. Preview.272 remains a
tagged, independently verified private release directory and deterministic ZIP
at 136 commands. Preview.277 has a published candidate; promotion and current
consumer installation status require separate receipts.
Neither source nor package evidence establishes runtime, visual, pathing,
conflict-free, recruitment, or public-release authority.

## Current versions and command counts

| Surface | Status | Machine-catalogued commands | Notes |
|---|---|---:|---|
| `1.0.0-preview.281` source | Current source identity; candidate-specific status is recorded in the release-attached receipt | 142 | Uses source line `preview.281-public`. The 142-command catalogue and eleven protocol-2-ready commands remain part of the source contract. Public publication requires NTFS-positive verification and remote CI for this exact candidate; the receipt records their results. |
| `1.0.0-preview.280` source | Previous private source release; local release and installation are documented by its retained receipt | 142 | Its 221-route selector inventory is historical. NTFS execution and remote CI were explicitly deferred for Preview.280 and are not claimed as passed. |
| `1.0.0-preview.279` package | Older private rollback release; its status remains bound to its own verified artifacts and receipts | 142 | Historical source identity. Its Phase 3 policy shadow and Phases 4-8 bounded reuse work remain part of that release evidence; no Preview.280 test result is attributed to it. |
| `1.0.0-preview.278` candidate | Immutable, independently verified private candidate; not promoted or installed | 142 | Its Phase 3 policy-shadow source and package evidence remain bound to the exact Preview.278 identity; it is historical and does not describe current source. |
| `1.0.0-preview.277` package | Tagged, independently verified, and candidate-published private release; approval and installation status require receipts | 142 | Historical package for current-source purposes. Its release evidence remains bound to its exact source and package; current consumer installation is governed by separate receipts. |
| `1.0.0-preview.276` package | Tagged, independently verified, candidate-published, and approved private release; historical for current-source purposes | 142 | Frozen acceptance is 880 passed and four skipped Python cases. Its tag, release note, package evidence, and verifier pins remain unchanged; current consumer installation is governed by separate receipts. |
| `1.0.0-preview.275` package | Immutable tagged, independently verified, candidate-published, and approved private package | 142 | Historical package baseline with six protocol-1 voice/dialogue commands, eleven protocol-2-ready commands, and 317 expected Python passes. No current installation status is asserted. |
| `1.0.0-preview.274` package | Historical tagged rollback package | 142 | Immutable pre-repair package with six protocol-1 voice/dialogue commands, eleven protocol-2-ready commands, and 317 expected Python passes. |
| `1.0.0-preview.272` package | Tagged and independently verified private release directory and deterministic ZIP; not Exchange-promoted or consumer-installed | 136 | The exact tagged tree passed the canonical gate twice, the public dependency audit reported zero vulnerable packages across 25 projects, the SPDX SBOM contains 36 packages, and the final verifier accepted 28 release files plus the ZIP. No runtime or visual authority is inferred. |
| `1.0.0-preview.271` source/tag | Failed unpublished final release-verifier checkpoint; never finalized, zipped, or promoted | 136 | The canonical gate and release assembly reached final verification, which refused truthful `pathConventions` because the verifier still expected the older schema-export field set. The tag is retained unchanged as historical evidence. |
| `1.0.0-preview.270` source/tag | Failed unpublished vulnerability-audit transport checkpoint; never finalized, zipped, or promoted | 136 | Canonical verification and binary staging passed, but the release builder omitted the audited NuGet configuration from the vulnerability query, which then required unavailable direct network access. The tag is retained unchanged as historical evidence. |
| `1.0.0-preview.269` source/tag | Failed unpublished vulnerability-scan lease checkpoint; never final-rooted, zipped, or promoted | 136 | Canonical verification and package staging passed, but the scanner requested unnecessary `DELETE` access while pinning the live repository root and collided with ordinary non-delete-shared directory handles. The tag is retained unchanged as historical evidence. |
| `1.0.0-preview.268` source/tag | Failed unpublished release-evidence checkpoint; never packaged or promoted | 136 | The canonical gate passed and the builder correctly tolerated native stderr, but its CRLF-formatted evidence stream did not match the line-ending-sensitive stamp parser. The tag is retained unchanged as historical evidence. |
| `1.0.0-preview.267` source/tag | Failed unpublished release-builder checkpoint; never packaged or promoted | 136 | Windows PowerShell treated a harmless native stderr diagnostic as terminating before the builder could inspect the child exit code. The same source passed the separate canonical gate; the tag is retained unchanged as historical evidence. |
| Frozen `1.0.0-preview.266` baseline | Packaged, approved 2026-08-26, and consumer-installed according to the approved implementation plan | 136 | Historical eight-command protocol-v2-ready contract. The release note records 231 passed standalone Python cases and 3 skipped cases, making 234 collected. These counts describe the frozen release, not subsequent source changes. |
| `1.0.0-preview.265` source/tag | Failed unpublished final-verifier checkpoint; never final-rooted, zipped, candidate-published, or promoted | 136 | Package staging, vulnerability scanning, and SBOM generation passed, but the final verifier rejected the summary because the identity expected 234 collected cases while the builder truthfully recorded 231 passed cases. It is retained unchanged as historical evidence. |
| `1.0.0-preview.264` source/tag | Failed unpublished exact-tag package checkpoint; never final-rooted, zipped, candidate-published, or promoted | 136 | Its staging package passed only when checked by the post-tag verifier fix, which was not part of the immutable tag. It is retained unchanged as historical evidence and is not an accepted package or promotion identity. |
| `1.0.0-preview.263` source/tag | Failed exact-tag package checkpoint; never candidate-published or promoted | 136 | The immutable local tag reached the release builder, where a missing current-identity workspace reviewed-intake verifier rule refused packaging. It is retained unchanged as historical evidence and is not an accepted package or promotion identity. |
| `1.0.0-preview.262` source/tag | Failed pre-release checkpoint; never packaged or promoted | 136 | The immutable local tag predates the aligned Finish Verify and neighboring workflow-resumption fixtures. It is retained unchanged as historical evidence and is not an accepted package or promotion identity. |
| `1.0.0-preview.261` source/tag | Failed pre-release checkpoint; never packaged or promoted | 136 | The immutable local tag predates the corrected reviewed-intake readiness expectations. It is retained unchanged as historical evidence and is not an accepted package or promotion identity. |
| `1.0.0-preview.260` source/package contract | Preserved previous private source checkpoint and rollback identity | 136 | Preview.260 remains immutable historical source/package evidence with exactly 224 standalone Python cases. It is not current source truth and no new package, candidate, promotion, consumer, runtime, or visual claim is inferred. |
| `1.0.0-preview.259` source tag | Failed pre-release checkpoint; never packaged or promoted | 136 | The exact annotated source tag is retained at `89ddd7f`, but sealed package verification refused the stale 66-byte RACE-only `npc-create-preflight` fixture because it had no hash-bound NPC `NAM9`/`NAMA` payload. It is not an accepted binary-release identity. |
| `1.0.0-preview.258` source/tag | Preserved previous private source checkpoint and rollback identity | 136 | Preview.258 remains immutable rollback evidence with exactly 219 standalone Python cases, its six-command protocol-v2 workflow projection, and an independent `preview258` probe snapshot. It is not current source truth. No consumer activation, runtime result, visual acceptance, or public-release authority is implied. |
| `1.0.0-preview.257` source/tag | Preserved previous private source checkpoint and rollback identity | 136 | Preview.257 remains immutable rollback evidence, including the HDPT RNAM/ValidRaces FLST compatibility repair and its six-command protocol-v2 workflow projection. No consumer activation, runtime result, visual acceptance, or public-release authority is implied. |
| `1.0.0-preview.256` source/tag | Preserved previous private source checkpoint and rollback identity, including its package and candidate evidence | 136 | Preview.256 remains immutable rollback evidence and is not current source truth. Its six-command protocol-v2 workflow projection remains accepted independently of Preview.260. No consumer activation, runtime result, visual acceptance, or public-release authority is implied. |
| `1.0.0-preview.255` source/tag | Preserved previous private source checkpoint and rollback identity, including its package and candidate evidence | 136 | Preview.255 remains immutable rollback evidence and is not current source truth. Its six-command protocol-v2 workflow projection remains accepted independently of Preview.260. No consumer activation, runtime result, visual acceptance, or public-release authority is implied. |
| `1.0.0-preview.254` source/tag | Preserved previous private source checkpoint and rollback identity | 136 | Preview.254 remains immutable rollback evidence. Its compatible protocol-v2 probe root is retained, and no package, candidate, promotion, consumer activation, runtime result, or visual acceptance is implied. |
| `1.0.0-preview.253` source/tag | Preserved previous private source checkpoint | 136 | Immutable source/tag bytes remain available as rollback evidence; no verified package, candidate, promotion, consumer activation, runtime result, or visual acceptance is implied. |
| `1.0.0-preview.252` source/package contract | Preserved private rollback identity | 136 | Historical release/tag/probe bytes remain available for rollback and are not current source truth. |
| `1.0.0-preview.251` source/package contract | Preserved private rollback identity | 136 | Historical release/tag/probe bytes remain available for rollback and are not current source truth. |
| `1.0.0-preview.250` source/package contract | Preserved private rollback identity | 136 | Historical release/tag/candidate bytes remain available for rollback and are not current source truth. |
| `1.0.0-preview.249` source tag | Failed pre-release checkpoint; never promoted | 136 | The canonical matrix exposed stale Finish Verify and neighboring Task 6 workflow-resumption fixtures. The registry and adapters were already authoritative; their direct fixtures now require and strictly reopen workflow inputs/outputs. The tag remains immutable and is not installable. |
| `1.0.0-preview.248` source tag | Failed pre-release checkpoint; never promoted | 136 | The canonical solution build rejected a casing assertion under CA1862 before selector execution. The corrected assertion preserves both semantic equality and uppercase wire enforcement; the tag remains immutable and is not installable. |
| `1.0.0-preview.247` source tag | Failed pre-release checkpoint; never promoted | 136 | The canonical gate exposed a stale reviewed-intake fixture that omitted Task 6 workflow options and compared a protocol SHA against lowercase domain storage. The tag remains immutable and is not installable. |
| `1.0.0-preview.246` source tag | Failed pre-release checkpoint; never promoted | 136 | The canonical gate exposed that the adapter semantics fixture omitted 19 already-authoritative workflow diagnostics. Production classification was unchanged; the tag remains immutable and is not installable. |
| `1.0.0-preview.245` source tag | Failed pre-release checkpoint; never promoted | 136 | The canonical gate exposed an ordinal-order drift in the journal's closed diagnostic test fixture. The production vocabulary was unchanged; the tag remains immutable and is not installable. |
| `1.0.0-preview.244` source tag | Failed pre-release checkpoint; never promoted | 136 | The canonical gate exposed a stale golden-transition expectation before any candidate or promotion was created. The tag remains immutable and is not an installable release. |
| `1.0.0-preview.243` package | Preserved previous private release | 136 | Immutable previous candidate retained for rollback; it does not describe the preview.260 agentic surface. |
| `1.0.0-preview.230` package | Preserved earlier provisional authoring/static authority | 136 | Historical private package retained as earlier evidence; it does not describe current source identity. |
| `1.0.0-preview.229` package | Preserved provisional rollback authority | 136 | Previous registered candidate retained byte-for-byte after its downstream Ruby HDPT authority refusal. |
| `1.0.0-preview.228` package | Preserved prior provisional rollback authority | 136 | Previous candidate retained byte-for-byte after its first live run exposed a downstream custom-PNAM comparator refusal. |
| `1.0.0-preview.227` package | Preserved provisional rollback authority | 136 | Previous registered binary retained byte-for-byte for rollback. |
| `1.0.0-preview.226` package | Preserved provisional rollback authority | 133 | Contains every `preview.225` command plus Finish Core analyze/apply/verify. Its bytes remain preserved for rollback. |
| `1.0.0-preview.225` package | Immutable rollback authority | 130 | Contains every `preview.224` command plus static `npc assembly preflight`. Its bytes remain preserved for rollback; it is no longer the selected authoring binary. |
| `1.0.0-preview.224` package | Preserved previous registered authority | 129 | Contains every `preview.223` command plus exactly five strict `facegen hair-regions` phases and the matching desktop wizard. Exact source is frozen at `4b097b3`; visual and runtime authority remain false. |
| `1.0.0-preview.223` package | Preserved previous registered authority | 124 | Contains the corrected `preview npc` viewer and catalogued `npc create-from-jslot` route. Its five authority hashes remain unchanged. |
| `1.0.0-preview.221` package | Preserved previous supported binary | 122 | Its `npc create-from-jslot` handler is operational and documented, but the frozen package predates the catalogue correction that exposes it through `capabilities --json`. |
| Immutable `1.0.0-preview.222` package | Historical superseded viewer package; do not use as corrected viewer | 123 | Built from older commit `79017d0` before the truthfulness correction at `1b9a6dd`; retained byte-for-byte as evidence. |
| Historical feature ledger | Planning/evidence taxonomy | 101 leaves | A ledger leaf is not a CLI command. Do not use 101 as the command count. |
| Current source working tree | Reports preview.281-public | 142 | The declared source contract keeps eleven protocol-v2-ready commands and all six protocol-1 voice/dialogue routes. Candidate-specific compiled-selector and release outcomes are recorded in the release-attached verification receipt. Receipt-optional Finish packaging retains an outstanding human-review requirement. Package/probe execution and immutable candidate gates remain separate; authentic preview rendering, listening, and runtime use require separate evidence with independently installed tools and copied inputs. |

Safety exception: preserved `preview.223` must not be used to apply single or
paired exterior follower placement. That package's follower-finish writer
predates the 2026-07-30 correction and can emit an 81-byte partial master
`WRLD 0000003C` record. Registered `preview.229` contains the statically
verified serialization correction. That structural proof does not establish
Skyrim placement, pathing, recruitment, or follower behavior.

Obtain the inventory from the exact pinned Exchange candidate or local
staging executable selected for the work:

```powershell
& $Actorwright version --protocol 2 --json
& $Actorwright capabilities --json
& $Actorwright capabilities --protocol 2 --json
```

The current build reports 142 commands, grouped as follows:

| Family | Count | Family | Count |
|---|---:|---|---:|
| animation | 2 | appearance | 1 |
| armor | 3 | armor-addon | 2 |
| assets | 2 | body | 12 |
| bodygen | 2 | capabilities | 1 |
| changes | 2 | diagnose | 1 |
| face | 6 | facegen | 23 |
| forms | 1 | gui | 1 |
| headpart | 1 | leveled-list | 3 |
| load-order | 1 | material-swap | 2 |
| npc | 31 | object-template | 2 |
| outfit | 4 | package | 4 |
| paint | 1 | pipeline | 1 |
| plugin | 4 | plugins | 2 |
| preset | 7 | preview | 4 |
| profile | 1 | records | 2 |
| render | 1 | runtime | 2 |
| runtime-script | 6 | schema | 1 |
| version | 1 | workspace | 2 |

Protocol 1 `capabilities --json` is the exhaustive current 142-command list.
The tagged Preview.272 binary remains exhaustive at its historical 136-command
surface.
Exact `--protocol 1` selects the same legacy dispatcher as omitting the
protocol flag; no-flag compatibility is retained. Exact `--protocol 1`
requires preview.266 or later; omit the flag for older binaries. Exact `--protocol 2`
selects the strict typed dispatcher, unsupported values remain refused, and no
protocol 3 is defined.
`capabilities --protocol 2 --json` is the readiness registry: only entries
with `readiness: "v2"` are callable under protocol 2. The current source-ready
set contains exactly `capabilities`, `version`, `schema export`, `npc assembly preflight`,
`npc create-from-jslot`, `npc finish analyze`, `npc finish apply`,
`npc finish verify`, `preset inspect`, `preview npc`, and `workspace preflight`.
`gui` remains `legacy` and is refused as `protocol-command-legacy` before
legacy dispatch. A successful source gate does not establish package-probe,
consumer, runtime, or visual results. `docs/cli-llm-cookbook.md` gives the agent workflow, and
`docs/cli-contract.md` describes protocol 1 options, outputs, and limitations.

The supported Hair Regions orchestration is hybrid, not a readiness promotion.
Using the exact binary, agents first inspect protocol-2 capabilities and schema
exports. Protocol-2 `workspace preflight` persists a fresh reviewed intake via
`--intake-output`; its exported `reviewed-intake` output schema is
`npcmanager-reviewed-game-intake/2`. Legacy `facegen hair-regions preview`
consumes that same schema through `--intake` and a reviewed, hash-bound request
and proposal. The Hair command remains `legacy`, so discovering it or its
schema does not make it protocol-2 callable. Neither discovery nor static
preview grants mutation approval, visual authority, runtime authority,
consumer activation, or promotion approval.

## Supported product surfaces

| Surface | What Manager can do | Authority boundary |
|---|---|---|
| Workspace and provider intake | Preflight copied K-local Data, bind load order, index/search loose and archive providers, inspect NPCs and records. | Read-only evidence; never infer or modify the live modlist. |
| Presets | Inspect, catalogue, diff, export, selectively copy, resolve portable FormIDs, and author a reviewed reference-derived JSlot. | Static output does not prove player/NPC likeness in Skyrim. |
| Preset to new NPC | `npc create-from-jslot` authors the plugin, FaceGeom, FaceTint, supported sidecars, manifest, package tree, and verification evidence through one typed hash-bound transaction. | Default supported Skyrim authoring route; runtime appearance remains route- and package-specific. |
| NPC editing | Propose/apply bounded identity, statistics, references, inventory, outfit, face, material, and related typed changes. | Each command's descriptor names its admitted record surface; unlisted fields are not implied. |
| FaceGen | Diagnose, resolve providers, build/verify FaceGeom and FaceTint, batch native pairs, pack, and deploy only to copied K-local Data roots. | Static structure and hashes are not Skyrim rendering proof. |
| Body and BodyGen | Inspect/resolve BodySlide inputs, write sidecars, process bounded weight/overlay/transform routes, and emit BodyGen configuration. | Does not imply BodySlide/OBody/MDNR runtime execution or a particular active body/skin provider. |
| Record writers | Hash-bound bounded writers exist for NPC scalar slices, OTFT, LVLI, ARMO, ARMA, MSWP, OBTS, VMAD/runtime-script, and package/archive operations. | These are typed slices, not unrestricted plugin editing. |
| Follower finishing | Registered `preview.229` has single and paired analyze/apply/verify transactions for the admitted simple vanilla-framework follower surfaces and deterministic packages. | The partial-WRLD parent defect is statically corrected, but runtime recruitment, trading, equipping, pathing, dismissal, save/load, dialogue, and support magic still require their own evidence. Never use preserved `preview.223` for exterior apply. |
| Finish Core follower completion | Registered `preview.229` adds `npc finish analyze|apply|verify` and a matching four-step desktop wizard for a bounded simple-follower completion pass over an appearance-approved source package. | The request excludes cells, worldspaces, transforms, anchors, placement, and ACHR records; typed/raw output census is zero-world. Static success is `STATIC_PASS_RUNTIME_REQUIRED`; this registration is authoring/static authority only. Positive copied-master integration selectors require an explicit read-only `ACTORWRIGHT_TEST_SKYRIM_MASTER`, extract the canonical PACK record into a synthetic test master, and do not distribute the full game master; only the validated raw record is used in owned scratch. |
| Interior placement patch | Registered `preview.229` adds `npc placement interior analyze|apply|verify` for an optional separate ESL patch bound to a Finish Core manifest, complete copied load order, terminal interior cell provider, and explicit transform. | Independent raw verification admits one EDID-only interior CELL and one persistent ACHR under the reviewed block/sub-block, and the archive contains only the new patch. `conflictContained=true` is a packaging boundary, not `conflictFree`; pathing, runtime loading, placement, collision, follower behavior, visual, and release authority remain false. The desktop surface is not exposed in 229. |
| FaceGeom HairTint regions | Registered `preview.229` analyzes every structurally recognized HairTint shape, proposes explicit Primary/Accent/Preserve assignments, previews exact proposed bytes, applies only authorized fixed-width tint envelopes, and independently verifies the written NIF. | Shape names never imply roles, shared shaders cannot receive conflicting roles, and neither preview nor byte verification grants runtime authority. UBE FaceGeom byte authoring is admitted; full UBE MDNR/OBody composition remains unsupported. |
| Semantic preview | `preview render`, reroll, animation selection, and NIF export operate on explicit preview-scene inputs. | Off-engine inspection only. |
| High-fidelity NPC viewer | Registered provisional `preview.229` command `preview npc` composes reviewed plugin/package/provider evidence into textured face/body views and a hash-bound bundle. | Off-engine inspection only; Skyrim remains authoritative. UBE runtime composition is explicitly refused. |
| Desktop | Registered provisional `preview.229` WPF shell exposes accepted production workspaces, including the virtualized main browser, manual NPC Render/Refresh path, the five-step HairTint wizard, and the four-step Finish Core wizard. Interior placement remains CLI-only in 229. | A visible control is not proof that every workflow has runtime acceptance. |

## Preset-to-NPC route families

The route registry keeps these families separate:

1. **CBBE 3BA** — primary supported body route. User-confirmed qualitative
   success exists, but the exact accepted package/provider/runtime fixture has
   not been retained; formal authority remains incomplete.
2. **COtR**: private historical evidence recorded runtime confirmation for
   Sophia v0.4.1 and Brigitte v0.1. Those character packages, game-derived
   inputs, and review artifacts are not included in this public snapshot;
   their results do not establish current public package or runtime
   authority. Preserve the qualified COtR providers and accepted
   face-head material rule: provider diffuse in slot 0 and actor FaceTint in
   slot 6.
3. **UBE** — a separate specialized success. The accepted Chel v0.9 mechanism
   uses Manager plus MDNR exact-JSlot runtime application and OBody ownership.
   It is not the default product identity and cannot be inferred from CBBE or
   COtR evidence.

Never transfer body, skin, race, headpart, texture, morph, or runtime authority
from one route family to another.

The `preview.229` FaceGeom HairTint transaction is body-route independent: an
UBE FaceGeom may use it when its NIF exposes the admitted structural HairTint
shapes. That does not make the high-fidelity NPC viewer capable of reproducing
UBE MDNR/OBody runtime composition; that separate limitation remains explicit.

## High-fidelity viewer status

The corrected viewer first registered in `preview.223` and preserved in
registered provisional `preview.229`:

- resolves the selected NPC, race, weight, FaceGeom, FaceTint, hair, body,
  outfit, materials, loose/BSA providers, and package overlay;
- imports the final FaceGeom exactly once instead of duplicating baked
  headparts;
- composes the qualified face diffuse with FaceTint and uses baked HairTint;
- applies skin TXST routes only to qualified SkinTint shapes;
- suppresses naked meshes hidden by outfit biped slots;
- interpolates admitted `_0`/`_1` mesh pairs at NPC weight;
- reports BodyGen assignments that cannot be reproduced without admitted
  generated TRI/body geometry;
- renders six deterministic 900×900 views, role masks, contact sheet,
  diagnostics, and hashes;
- reports `COMPOSED — human visual and Skyrim runtime review required`, never
  an automatic likeness PASS.

Private historical implementation evidence records all 12 executable suites
(491/491) for the preview.222 truthfulness correction in the report named
05-reports/npc-manager-preview222-truthfulness-correction-verification-20260729.md.
The report and private character-package inputs are not included in this
public snapshot. The user provisionally accepted corrected viewer source
commit 1b9a6dd on 2026-07-29 for that source state only; this is not
current public package or runtime clearance.
Fresh exact-package Skyrim comparison and a retained exact Manager-authored
CBBE 3BA fixture remain open.

Private historical evidence records the corrected source plus capability
catalogue at commit 31c5530d7b9afb4b04a16080f9b6f28fefe11e97, sealed
into the immutable preview.223 package. Independent verification covered
all 2,567 manifested files, its desktop and CLI entry points, exact source
snapshot, and 124-command catalogue. A private verification then rendered
the authentic Sofia COtR fixture in 145 seconds: six views, one detected
face, 478 landmarks, all 31 semantic anchors, and bounded eyes/nose/mouth.
Manual review recorded a coherent face and hairstyle. The package, Sofia
fixture, and verification report are not included in this public snapshot;
these results apply only to the preserved private bytes and do not establish
current public installability or runtime authority. Private report reference
(not shipped):
05-reports/npc-manager-preview223-provisional-viewer-verification-20260729.md.

The immutable package already named `preview.222` remains preserved at its
original hashes. It was built from commit `79017d0`, before the truthfulness
correction, and must not be used as the corrected viewer. `preview.221` also
remains byte-identical as the previous supported authority.

## Fresh-context startup

A fresh agent working on NPC Manager should:

1. Follow the root mandatory startup instructions.
2. Read the registered tool manifest and this file.
3. Read the route registry before any preset-to-NPC decision.
4. Run `capabilities --json` against the exact binary or source tree it will
   use; do not quote a historical count.
5. For visual inspection, select registered provisional `preview.229` and use
   desktop Render/Refresh or CLI `preview npc`; do not select `preview.222`.
6. Read the relevant command section in `docs/cli-contract.md`.
7. For dual-tone HairTint work, use the exact registered package and the five
   `facegen hair-regions` commands. If source inspection is required, use
   `codex/npcmanager-dual-tone-hair@4b097b3`; do not assume dirty
   `solveig-build` source contains the feature.
8. Read the newest task/release evidence for unfinished work.
9. Preserve accepted package and route hashes; never replace Manager with a
   manual authoring script without a typed refusal.

Skills remain generic routing and safety mechanisms. They should point to this
index, the tool manifest, and the route registry rather than encode a named NPC
or a temporary package as a reusable rule.
