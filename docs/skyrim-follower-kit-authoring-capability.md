# Skyrim Follower-Kit Authoring Capability

## Purpose

This document preserves the follower/outfit/inventory authoring work so a new
context extends it instead of rebuilding it. It is separate from the accepted
preset-to-NPC route registry: preset appearance creation and follower behavior
authoring have different authorities and release gates.

## Where The Implementation Lives

- Branch: `codex/chel-light-follower-kit`
- Last implementation-hardening commit: `63e6f07`
- Implementation commit chain:
  `65d7a3b`, `4c8e877`, `5c6e935`, `0dbb898`, `bf75c79`,
  `29a842e`, `63e6f07`
- Contracts:
  `src/NpcManager.Application/SkyrimFollowerKitContracts.cs`
- Analyze/write/verify service:
  `src/NpcManager.Infrastructure/SkyrimFollowerKitService*.cs`
- Bethesda source reader, writer adapter, and verifier:
  `src/NpcManager.Formats.Bethesda/BethesdaSkyrimFollowerKit*.cs`
- Architecture tests:
  `tests/NpcManager.Architecture.Tests/SkyrimFollowerKit*Tests.cs`

The branch contains later Chel packaging work, but that packaging is not a
final accepted release. Commit `63e6f07` is the stable handoff point for the
implemented authoring mechanism.

## Preview.227 Finish Core successor

The registered follower-kit mechanism remains the authority for its broader
single and paired follower surfaces. The provisionally registered `preview.227`
Finish Core slice is a narrower additive transaction for an already appearance-approved
NPC package:

- Contracts: `src/NpcManager.Application/SkyrimNpcFinishCoreContracts.cs`
- Source admission: `src/NpcManager.Infrastructure/SkyrimNpcFinishCoreSourcePackageReader.cs`
- Typed/raw writer and verifier: `src/NpcManager.Formats.Bethesda/BethesdaSkyrimNpcFinishCoreWriter.cs`
  and `BethesdaSkyrimNpcFinishCoreVerifier.cs`
- CLI: `npc finish analyze`, `npc finish apply`, and `npc finish verify`
- Desktop: the four-step `Finish NPC` wizard

Finish Core deliberately does not import Chel identities, allocations, outfit
choices, inventory, placement, dialogue, scripts, or support behavior. Its
request has no cell/worldspace/transform/anchor/placement field, and its source
and output must contain zero world or placed-reference records. Static output
is `STATIC_PASS_RUNTIME_REQUIRED`; preview.227 is authoring/static authority,
while a separate runtime checkpoint is still required for any runtime or
release promotion. Preview.226 remains the preserved provisional rollback and
preview.225 remains the immutable rollback binary.

Positive Finish Core integration selectors require ACTORWRIGHT_TEST_SKYRIM_MASTER, an explicit read-only
ordinary local Skyrim.esm. They validate the unique uncompressed canonical PACK record at
PACK:00000000/0x0001B217 (532 raw bytes), then build a small synthetic test master. The external
full Skyrim.esm is read-only and not distributed; only the validated raw record is used in owned scratch. These selectors remain fixture-bound static checks.

## Preview.227 optional interior placement

Preview.227 also exposes a separate, CLI-only placement transaction:

- `npc placement interior analyze`
- `npc placement interior apply`
- `npc placement interior verify`

This is not the broader historical `PlacementLifecycle` slice above and does
not import Chel-specific identities, allocations, outfits, inventory, dialogue,
scripts, or support behavior. It binds a static Finish Core manifest, a complete
copied load order, a terminal interior-cell provider, and an explicit or
reviewed transform, then emits a new ESL patch and a separate archive. The
writer admits only one EDID-only interior `CELL` and one persistent `ACHR` under
the reviewed block/sub-block; the independent verifier reopens the raw topology
and archive inventory.

The package boundary is deliberately narrow: `conflictContained=true` means
the patch is isolated in its own archive, not that it is conflict-free. The
manifest must keep `conflictFree=false`, `pathingAuthority=false`,
`runtimeAuthority=false`, and `visualAuthority=false`. Cell loading, exact
placement, collision, pathing, follower behavior, and release remain separate
runtime/conflict gates. Desktop placement is not exposed in preview.227.

## 2026-07-27 Player-Dialogue Runtime Correction

Chel Slice 4 v1.2 exposed a static-verification hole. Its treatment DIAL and
nested INFO parsed and passed the original verifier, but the player topic had
no `BNAM` branch link and the plugin had no owning `DLBR`. CrashLoggerSSE
localized the resulting access violation to `NPCM_ChelTreatmentDIAL`.

The reusable correction on `codex/chel-light-follower-kit` authors a closed
dialogue graph:

- `DIAL.BNAM -> DLBR`
- `DLBR.QNAM -> owning QUST`
- `DLBR.TNAM = Player`
- `DLBR.DNAM = TopLevel`
- `DLBR.SNAM -> starting DIAL`
- INFO remains in the topic's type-7 child group

Typed and independent raw verification now check all links and raw
`BNAM/QNAM/TNAM/DNAM/SNAM` fields, plus null-link tamper controls. v1.2 is
quarantined and must not be installed. v1.2.1 is the bounded correction and
still requires a fresh-save runtime checkpoint.

The current product also has a public, hash-bound paired-follower finishing
transaction:

- Contracts:
  `src/NpcManager.Application/SkyrimFollowerFinishPairContracts.cs`
- Bethesda implementation:
  `src/NpcManager.Formats.Bethesda/BethesdaSkyrimFollowerFinishPairService.cs`
- CLI adapter:
  `src/NpcManager.Cli/SkyrimFollowerFinishPairCommandHandler.cs`
- Commands:
  `npc follower-finish pair-analyze`, `pair-apply`, and `pair-verify`
- Independent package oracle:
  `tools/verify_skyrim_follower_finish_pair_package.py`

The loader accepts legacy schema 2 and current schema 3 under the same three
commands. Schema 2 preserves the accepted companion package byte-for-byte.
Schema 3 retains that behavior for every undeclared surface but may finish the
companion's explicitly bound HairTint and private outfit records. This is a
closed additive request shape, not permission for a whole-plugin rewrite.

This transaction is generic request/proposal/application infrastructure. The
Brigitte/Sofia names, hashes, providers, outfit, locations, and record
allocations live in the request and evidence artifacts, not in the reusable
contract.

## What The Mechanism Does

### Hash-Bound Analysis And Proposal

The service admits only ordinary K-local files and binds every source to its
path, SHA-256, and length. It uses Mutagen typed inspection plus a bounded raw
TES4/GRUP/record/subrecord reader to establish exact plugin and record closure.
It emits a deterministic canonical proposal, creates it with no-overwrite
semantics, and rereads the proposal before any write.

### Protected-Byte-Preserving Write

The writer creates typed changed records in a Mutagen scratch plugin, then
splices only the admitted records and groups into the accepted source plugin.
This avoids promoting an uncontrolled whole-plugin rewrite. Output promotion is
atomic and no-overwrite; rollback is allowed only for the output hash owned by
the current operation.

### Independent Verification

Verification reopens the final output path, binds its exact hash and length,
checks typed semantics, and separately parses the binary through verifier-owned
raw logic. The verifier does not depend on the writer's parser. It refuses
substitution, source or proposal drift, path aliases, malformed record shapes,
unexpected signatures, forbidden records, and undeclared change surfaces.

### Direct-Install Archive Projection

The verified transaction tree and the install archive have separate roots.
Manager retains `Data/`, request/proposal/post-write evidence, README, and its
manifest in the K-local transaction tree. It projects only the contents of
`Data/` plus the runtime README into the deterministic ZIP. Both linked plugins
therefore sit at ZIP root for direct Mod Organizer recognition; transaction-only
evidence and the Manager manifest are not installed as game data.

## Admitted Slices

`FollowerCore` proves a closed follower baseline:

- ESL flag
- Unique and Protected actor flags
- PotentialFollowerFaction and CurrentFollowerFaction
- player relationship
- zero-offense defensive combat style

`OutfitInventory` is cumulative with `FollowerCore` and additionally proves:

- one output-owned OTFT record
- NPC DOFT routing to that outfit
- a conservative CNTO inventory baseline
- one direct outfit master
- exact owner-index relocation handling

`PlacementLifecycle` and `SupportMagic` are reserved later slices. They are not
implemented and remain behind the mandatory Slice 2 runtime checkpoint.

## Reusable Versus Chel-Specific

Reusable architecture:

- K-only path, hash, and length admission
- exact source/provider/record closure
- deterministic typed proposal
- bounded raw Bethesda parsing
- hybrid typed-record/raw-splice writing
- protected-byte and change-surface checks
- atomic no-overwrite promotion and owned rollback
- independent typed plus raw verification
- hostile drift, substitution, malformed-input, and alias tests

Chel-specific policy and data:

- source roles such as `AcceptedChelPlugin`, `J3OutfitPlugin`, and
  `J3UbeWinnerPlugin`
- accepted package/provider paths and hashes
- local FormID allocation range and descriptions
- `ChelNpcManager.esp` output name
- J3 Goth Chel master, OTFT choice, and exact CNTO inventory
- Chel proposal/report filenames and packaging scripts
- MDNR/OBody/UBE appearance preservation rules

For another NPC, retain the analyzer/writer/verifier architecture and replace
the second list with typed request data and validation policy. Do not copy Chel
hashes, FormIDs, output names, masters, or inventory into a new actor.

## Adaptation Procedure

1. Start from `codex/chel-light-follower-kit`, using `63e6f07` as the last
   implementation-only checkpoint.
2. Separate generic contracts from Chel/J3 enums, paths, IDs, and validation.
3. Express actor, provider, allocation, outfit, and inventory choices as
   hash-bound request data.
4. Keep the raw parser bounds, deterministic proposal, no-overwrite behavior,
   protected-byte checks, and independent verifier intact.
5. Add hostile tests for the new policy before admitting another slice.
6. Stop after `OutfitInventory` for runtime recruitment, trading, equipping,
   save/reload, and cell-transition proof.
7. Add placement or support magic only after that checkpoint passes.

## Current Boundary

The original follower-kit slices remain historical source work from
`codex/chel-light-follower-kit`. The newer paired-follower finishing transaction
has a supported public CLI surface in the registered provisional
`npcmanager-1.0.0-preview.223` package. That package exposes three bounded
single-follower commands and three bounded paired-follower commands, including
legacy schema 2 and current schema 3 request loading. Broader
Chel/J3-specific source work on `codex/chel-light-follower-kit` is not silently
promoted into the registered product.

Static tests and architecture checks establish bounded authoring behavior, not
in-game behavior. Recruitment, trading, equipping, save/reload, cell
transition, placement, dismissal, healing, and cure behavior remain runtime
questions. Brigitte/Sofia v0.5 and v0.4.1 describe private historical static and
appearance evidence. Their exact character packages, game-derived inputs,
and review artifacts are not included in this public snapshot. v0.5 is not
a current directly installable public candidate, and neither package
establishes current public runtime authority. Any new public candidate needs
its own admitted inputs and in-game checkpoint. Chel's later package is not
final release authority. Preserve her accepted v0.9 appearance baseline
independently when work resumes.
