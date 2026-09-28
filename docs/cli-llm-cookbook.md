# Actorwright CLI cookbook for LLM operators

Actorwright is CLI-first. The current source build exposes eleven protocol-2
commands for agents. Protocol 1 is the complete current 142-command product
interface; the tagged Preview.272 package remains immutable at 136. Query the
exact executable before using either surface.

## Select the dispatcher explicitly

Exact `--protocol 1` is an alias for the existing legacy dispatcher. Omitting
`--protocol` selects that same legacy path; the two forms have
the same exit code and output and neither constructs a protocol-2 operation
journal. Exact `--protocol 2` selects the strict typed dispatcher. Any other
supplied protocol value is unsupported; there is no protocol 3. Exact
`--protocol 1` requires preview.266 or later; omit the flag for older binaries.

Use the exact executable that will perform the work for every discovery and
execution step. For example, these two legacy calls select the same dispatcher:

```powershell
& $Actorwright version --json
& $Actorwright version --protocol 1 --json
```

## Discover the callable protocol 2 surface

Every protocol 2 call must opt in with both `--protocol 2` and `--json`:

```powershell
actorwright capabilities --protocol 2 --json
actorwright version --protocol 2 --json
actorwright schema export --protocol 2 --json --command "npc create-from-jslot"
actorwright schema export --protocol 2 --json --command "npc finish analyze"
```

Each invocation writes exactly one JSON envelope to standard output. Inspect
`result.commands` from `capabilities` before composing another call. Only
commands whose registry entry has `readiness: "v2"` are callable under
protocol 2. The current source-ready set is exactly `capabilities`, `version`, `schema export`, `npc assembly preflight`,
`npc create-from-jslot`, `npc finish analyze`, `npc finish apply`,
`npc finish verify`, `preset inspect`, `preview npc`, and `workspace preflight`.
`gui` remains `legacy` and is refused under protocol 2. An exact receipt-free
Finish workflow may undergo static package verification and archive creation.
Human visual acceptance, game runtime verification, and promotion approval
remain unestablished. Any supplied review receipt is validated against the
exact workflow.

The workflow commands persist fresh, hash-bound evidence under the admitted
K-local workspace. `workspace preflight` requires a fresh `--intake-output` in
addition to its reviewed workspace inputs. `preset inspect` requires
`--format racemenu-jslot`, `--edition skyrimse`, an absolute K-local `--input`,
its uppercase `--input-sha256`, and a fresh `--inspection-output`. Preset
inspection establishes strict source and inspection evidence only; its next
`npc create-from-jslot` action remains blocked on the request, request hash,
data root, plugin set, companion root, and preflight output. Neither workflow
establishes human-visual, game-runtime, or promotion authority.

The package gate executes the self-contained workspace, preset, NPC preflight,
and Finish Verify probes. It validates the exact `preview npc` contract and
schema without claiming an authentic render, because the required modlist
closure and third-party renderer installation belong to the consumer.

The schema command may describe a protocol 1 command even when that command is
not callable through protocol 2. For example, `facegen hair-regions preview`
exports its input schemas while retaining `readiness: "legacy"`. Finish Core
analyze and apply now export protocol-2 result schemas as callable commands.

## Author a hash-bound custom voice and dialogue overlay

The six voice/dialogue commands are discoverable through the protocol-2
registry but have `readiness: "legacy"`, so invoke them through protocol 1.
Export their schemas before composing documents:

```powershell
& $Actorwright version --protocol 2 --json
& $Actorwright capabilities --json
& $Actorwright capabilities --protocol 2 --json
& $Actorwright schema export --protocol 2 --json --command "npc voice synthesize"
& $Actorwright schema export --protocol 2 --json --command "npc dialogue analyze"
& $Actorwright schema export --protocol 2 --json --command "npc dialogue apply"
```

Discover an already-running loopback XTTS service, import one authorized WAV,
then create or review a dialogue manifest before synthesis:

```powershell
& $Actorwright npc voice discover --protocol 1 --endpoint http://127.0.0.1:8020 --output <fresh-services.json> --json
& $Actorwright npc voice import --protocol 1 --sample <reference.wav> --plugin <npc.esp> --form-id <id> --editor-id <npc-edid> --voice-prefix <prefix> --output <fresh-voice-root> --json
& $Actorwright npc voice synthesize --protocol 1 --manifest <reviewed-manifest.json> --manifest-sha256 <sha256> --sample-authority <sample-authority.json> --sample-authority-sha256 <sha256> --output <fresh-synthesis-root> --resume --json
```

Synthesis uses the manifest language by default. `npc voice synthesize --language`
overrides it and regenerates changed-language rows on resume. Review the effective
language in the synthesis ledger before passing its exact `--synthesis-sha256`
to apply; all rows must have one non-empty language, and the output manifest
retains this binding. Normal `npc dialogue analyze` reads identity and language
from the reviewed manifest; `--editor-id` and `--language` belong only to its
template mode.

Analyze the dialogue-only overlay against copied plugin bytes and the reviewed
load order, review the proposal, then apply and independently verify it:

```powershell
& $Actorwright npc dialogue analyze --protocol 1 --manifest <reviewed-manifest.json> --manifest-sha256 <sha256> --plugin <npc.esp> --plugin-sha256 <sha256> --data-root <copied-Data> --plugins <ordered.csv> --sample-authority <sample-authority.json> --sample-authority-sha256 <sha256> --output <fresh-proposal.json> --json
& $Actorwright npc dialogue apply --protocol 1 --proposal <reviewed-proposal.json> --proposal-sha256 <sha256> --synthesis <synthesis.json> --synthesis-sha256 <sha256> --output <fresh-package-root> --lip-tools <external-tool-root> --json
& $Actorwright npc dialogue verify --protocol 1 --manifest <output-manifest.json> --manifest-sha256 <sha256> --json
```

Analyze refuses source plugins that already own `INFO` records with
`dialogue-source-dialogue-unsupported`; importing inherited dialogue or audio
is unsupported. The proposal binds the strict copied-master closure and the
selected source SEQ path/hash, or exact absence. A plugin-adjacent SEQ has
precedence over the copied Data-root SEQ. Extra reviewed load-order plugins are
allowed, but do not become masters or resolve conditions unless the plan uses
them.

The output is an overlay that must be installed with the original NPC package,
which continues to supply FaceGen, skin, and inherited assets. Actorwright has
no XTTS runtime dependency and does not administer the service. It records
synthesis evidence and generates new dialogue assets; listening, voice
likeness, game loading, playback, and visual behavior require separate review.

### Run a reviewed-intake hybrid Hair Regions preview

First query the exact binary and export both command contracts. Confirm
`workspace preflight` has `readiness: "v2"`, while `facegen hair-regions
preview` remains `readiness: "legacy"`. The schema exports identify the same
closed `npcmanager-reviewed-game-intake/2` document as a workspace-preflight
output and Hair Regions preview input:

```powershell
& $Actorwright version --protocol 2 --json
& $Actorwright capabilities --protocol 2 --json
& $Actorwright schema export --protocol 2 --json `
  --command "workspace preflight"
& $Actorwright schema export --protocol 2 --json `
  --command "facegen hair-regions preview"
```

Persist the reviewed intake through strict protocol 2, using fresh output
paths:

```powershell
& $Actorwright workspace preflight --protocol 2 --json `
  --game skyrimse `
  --workspace-root <K-local-workspace> `
  --data-root <K-local-copied-Data> `
  --output-root <fresh-reserved-output-root> `
  --load-order <reviewed-load-order.json> `
  --intake-output <fresh-reviewed-intake.json> `
  --npc-editor-id <npc-editor-id> `
  --workflow-output <fresh-workflow-bundle.json>
```

After reviewing that persisted intake and the hash-bound Hair Regions request
and proposal, pass the same intake file to the legacy preview handler:

```powershell
& $Actorwright facegen hair-regions preview --protocol 1 --json `
  --request <request.json> `
  --request-sha256 <64-hex-sha256> `
  --proposal <proposal.json> `
  --proposal-sha256 <64-hex-sha256> `
  --intake <fresh-reviewed-intake.json> `
  --output-root <fresh-preview-bundle-root>
```

If intake loading fails, create a fresh reviewed intake with `workspace
preflight --protocol 2` and `--intake-output`; do not derive or weaken one.
Discovering the Hair Regions command or its schema does not make it
protocol-2 callable and grants no mutation approval, human-visual acceptance,
game-runtime verification, or release-promotion approval. A successful legacy
preview remains off-engine evidence and does not establish those authorities.

### Discover an NPC request document

`npc create-from-jslot` publishes its request contract through the existing
`schema export` command; it does not add a validator or another command name.
Export the command and select the `result.documentSchemas` entry whose
`name` is `request`, `direction` is `input`, and `schemaIdentifier` is
`npc.create-from-jslot.request.v1`. Use that entry's closed Draft 2020-12
`jsonSchema` as the request-document contract, including its version-specific
requirements. Do not reconstruct the request shape from older projects or
infer fields from a successful build.

```powershell
actorwright schema export --protocol 2 --json `
  --command "npc create-from-jslot" --output <new-schema-envelope.json>
```

The export is discovery and documentation evidence only. It does not grant
runtime, visual, game-behavior, or promotion authority, and the output path is
no-overwrite and must be fresh.

### Prepare a RaceMenu preset with external SMP hair

For the preview.260 external-SMP route, keep the selected SMP hair in the
RaceMenu preset/JSlot and keep its provider `Hair` HDPT graph enabled. The
Manager-owned FaceGeom carrier is intentionally bald with respect to that
external graph: Actorwright excludes its geometry, collider, physics locator,
and physics XML while retaining the HDPT references in the final NPC. Users do
not need to edit the preset into a manually bald workaround, and a manually
stripped carrier is not itself proof. Actorwright does not clone provider
records or redistribute provider assets.

Provide one explicit `DataRoot` and the complete enabled plugin order for the
same build, including required masters and the hair provider. The root must
contain the provider plugin and every declared loose NIF/TRI/DDS/physics asset.
Do not rely on a live profile, filename similarity, or directory scanning. A
changed provider byte, missing asset, disabled provider, or changed order is a
strict refusal.

Treat the package result as static evidence only. The strict verifier must
reopen the provider/plugin bytes, selected dependency manifest, output plugin
masters and PNAM references, and FaceGeom exclusion evidence before reporting
the install dependency as verified. A verified package still reports
`runtimeAuthority: false` and `visualAuthority: false`; load-order activation,
in-game simulation, and visual likeness require a separate consumer/runtime
checkpoint.

### Resolve captured light-plugin head parts

Preview.256 normalized a captured Ruby light-plugin HDPT FormID to its
provider-local value before resolving the exact copied record. Ordinary
provider-plugin references retain their existing IDs and resolution behavior.
A missing provider-local record, changed provider bytes, or any other genuine
plugin/record mismatch remains a strict refusal; no basename or guessed-ID
fallback is admitted. Preview.257 additionally resolves the HDPT RNAM/
ValidRaces FLST and admits the selected Hair only when the routed race is a
member of that list. Deleted, missing, malformed, or incompatible lists remain
strict refusals. These compatibility repairs change no command, protocol,
schema, or wire contract, and do not establish runtime or visual authority.

## Handle an unmigrated command

Do not infer protocol 2 support from the presence of a command in discovery.
An unmigrated call fails before its legacy handler runs:

```powershell
actorwright npc create --protocol 2 --json
```

The single response envelope has exit code `2` and an error diagnostic with
code `protocol-command-legacy`. Use the protocol 1 CLI contract for that
operation, or wait until its complete typed contract and handler are migrated.
Do not remove only `--json`: protocol 2 requires machine output.

## Read the envelope

Use these fields for orchestration:

- `outcome` and `exitCode` are the primary result.
- `diagnostics[].code`, `class`, and `recovery` describe a typed refusal.
- `requestDigest` identifies the canonical invocation without recording raw
  option values.
- `effects`, `artifacts`, `authority`, and `nextActions` report only what the
  operation actually established.
- `result.schemaId` identifies the command-specific result contract on a
  successful kernel command.

Runtime, visual, and game-behavior authority remain separate from a successful
protocol or static-verification envelope.


## Choose a supported authoring route

Discover version, capabilities and the selected command schema from the exact binary before composing arguments. A refusal's optional `recovery.alternativeCommand` names a supported command; `constraint` states the input changes and prerequisites. When `retryUnchangedSafe` is false, retrying the same bytes is not a repair.

| Available authority | Route | Required evidence and limits |
|---|---|---|
| Supplied complete CharGen export | `facegen build-geom-nif` transport, complete-carrier profile | Exact source hash and the discovered carrier contract. Transport preserves supplied geometry; it does not invent missing morph data. |
| Explicit native construction | `npc create-from-jslot` using its preflight and build contract | Exact selected models, TRI roles, slider catalog, copied providers and carrier authority. Unresolved custom morphs identify the inspected catalog files; stage the real provider declarations and TRI tree rather than guessing paths. |
| Runtime-JSlot / MDNR appearance | The discovered `runtime-script` and runtime-appearance workflow | Bound preset, plugin and runtime dependencies. This is a separate appearance route; static generation does not establish in-game appearance or runtime success. |

For `finish-core-combat-seed-required`, set `combatPolicy.seedLocalStyle=true` for the referenced master CSTY, retain its copied master/provider hash authority, rehash the request, and rerun `npc finish analyze` with a fresh proposal output. Missing or duplicate styles, malformed input, hash/topology mismatches and human-review requirements retain their own refusals.

Finite extended morph values are admitted by the current bake route; the historical range refusal is not a reason to switch routes. Nonfinite values and malformed names require input correction. Canonical-equivalent reference/Finish documents use their published digest rules; a route change cannot excuse an incorrect binding.

Overlay enum refusals name the field, rejected value and admitted set. Reviewed-workspace load-order parsing names the actual file and malformed root/schema reason. Preview luminance advisories retain the measured values and threshold, and the existing preview output retains rendered views independently of those advisories.
