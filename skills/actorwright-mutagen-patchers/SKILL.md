---
name: actorwright-mutagen-patchers
description: "Use Mutagen.Bethesda for typed, bounded Skyrim plugin inspection or an explicitly authorized patch when the registered Actorwright operation does not cover the required record work."
license: GPL-3.0-only
---

# Mutagen plugin patchers

Use Mutagen.Bethesda for typed record analysis and a narrowly scoped,
authorized patch. Actorwright remains the default for operations its current
executable supports. A local patcher is not an Actorwright command, does not
grant authority, and must not recreate or replace the application.

## Establish the dependency and write boundary

Read the task’s current tool inventory and any library manifest, then verify
available checksum records against installed files. Inspect the exact source
and caller contract for a helper before reusing it. Do not rely on historical
file counts, cached DLLs from another project, or an example version number.
If a qualified library closure, SDK or policy is not supplied, stay read-only
and resolve that prerequisite; do not download an arbitrary replacement.

Use a reusable helper location only when the task permits it; otherwise keep
the patcher with its owning mod project. Reference registered libraries with
paths relative to that project. Select the target framework from the qualified
local SDK/library closure. Keep source, reports and build output under the
assigned workspace; keep copied masters and bin/obj out of the installable
package.

Discover the exact Actorwright executable, capabilities, help and schema using
[startup and safety](../actorwright-startup-and-safety/SKILL.md). For an
authorized operation unsupported by its registered interface, follow
[preset decomposition](../actorwright-preset-pipeline/SKILL.md), define the
specific gap and allowed record surface, then use existing authorization or
seek new approval only if the work exceeds it. Do not infer an undocumented
operation, modify product code or replace the selected executable.

## Choose the correct read mode

| Need | Mutagen approach | Boundary |
|---|---|---|
| Inspect a master or other large/read-only input | binary overlay API | read-only; dispose the overlay and never write it back |
| Inspect a small owned plugin that will be edited | mutable binary load | write only to a new approved output |
| Resolve links across masters | immutable link cache over admitted masters | preserve explicit master order and verify resolved identity |

Use an overlay for anything not owned for writing. A mutable load is not itself
permission to write. Never direct a writer to an active game install, source
plugin or protected input.

## Shape a bounded patcher

A small command-line helper can expose separate operations for:

- **analyze:** read-only input summary, header/masters, selected records,
  dependencies and unresolved links;
- **propose:** a human-reviewed list of exact records, fields, references and
  files permitted to change;
- **write:** create a new sandbox artifact and refuse if preconditions fail;
- **audit:** reopen the output and check plugin and package invariants.

These may be separate registered tools rather than modes in one binary. Do not
refactor a proven target-specific tool just to fit this shape. Give each
failure a nonzero result and a useful diagnostic; do not silently repair a
rejected input. Save reports beside the owning project and bind the exact
source/output hashes and lengths.

Keep project configuration minimal: executable type only when needed, nullable
and implicit-using settings consistent with the repository, relative references
to the verified library closure, and the framework actually supported by that
closure. A template with a guessed target framework or machine-specific
absolute path is not reusable guidance.

## Verify serialization independently

A writer’s own reload is useful but is not independent verification. Reopen the
exact output with a typed reader and a separate raw reader. Compare the actual
signature/FormID delta with the proposal; inspect header version, flags, master
order, record count, links, VMAD, dialogue and package sidecars as relevant.
Recheck protected input bytes and output archive contents. If the requested
format conversion requires new header or record stamps, set every required
field explicitly and verify it from raw bytes; do not assume the library
upgraded a loaded file just because a target release was selected.

For a Python gate that needs typed data unavailable to its byte parser, a small
Mutagen probe may emit temporary JSON. The probe’s successful exit means only
that it ran and emitted data; the gate must evaluate the returned fields.
Keep temporary exchange isolated, bound execution and surface parser failures.
Distinguish an optional/unknown check from a required check: missing input may
be reported as skipped only where the gate contract permits it, and a required
gate must fail closed. Catch process-launch errors as well as nonzero exits.

## FormID changes and light plugins

Before compaction, inventory every owned record, incoming/outgoing reference,
master link and filename or sidecar keyed by the record ID. For a light plugin,
verify the supported compact range and all master/index encoding constraints.
Build a deterministic old-to-new map, refuse collisions or exhaustion, remap
every internal link, and update plugin flags and next-ID bookkeeping. Preserve
IDs that anchor external files or contracts unless an approved migration maps
those files too. A light flag by itself is not compaction and does not prove
save compatibility.

If a legacy-format conversion is explicitly in scope, qualify target-format
requirements first. Verify header and every relevant record’s format stamp
independently; do not rely on a same-library round trip to prove conversion.

## Verification checklist

- Input path, hashes, release, masters, ownership and write authority recorded.
- Only admitted records and files are mutable; read-only masters use overlays.
- Analysis and proposal precede any authorized write.
- Output is new, reopened and compared with both typed and raw readers.
- FormID references, master order, sidecars, dialogue/SEQ and package closure
  are checked where affected.
- Applicable gates and runtime checks are reported at their actual evidence
  level; no static result is called game proof.
- Reports and package contain only task-required material.

For lifecycle and placement contracts see
[complete NPC authoring](../actorwright-complete-npc-authoring/SKILL.md).
For approvals and artifact preservation see
[change control](../actorwright-change-control/SKILL.md) and
[evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md).
