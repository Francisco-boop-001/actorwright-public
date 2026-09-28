---
name: actorwright-npc-transformation
description: "Design or diagnose an NPC that changes appearance or owned abilities while preserving one actor identity, accepted providers and recoverable state."
license: GPL-3.0-only
---

# Same-actor NPC transformation

Use this for a controlled appearance/form transition on one actor. A visual
preset change is not the same as a race conversion, vampire/creature gameplay,
feeding behavior, quest progression or player-controlled transformation.
Diagnosis is read-only unless repair is authorized.

## Define the transition

Record:

- one actor versus replacement actors and how identity persists;
- fixed race versus actual race conversion;
- supplied presets, tints, skin and gear for each form;
- body/morph and provider owners;
- owned gameplay differences, if any;
- trigger, request source, interruption and starting-save policy;
- existing follower/controller owner and recovery behavior.

Preserve the accepted baseline and package unless explicitly changed. Do not
add autonomous triggers or assume a new quest should own follower behavior.
Confirm the workspace, protected root, exact executable and registered tool
contracts using [startup and safety](../actorwright-startup-and-safety/SKILL.md).
A skill does not grant write or runtime authority; carry forward authorization
already granted for its scope and seek new authority only for an out-of-scope
action. Inspect the exact native API and provider contract required for the
chosen route. Similar API names or historical code do not establish equivalent
semantics. If an API or local helper is unavailable, report the gap instead of
inventing flags or rebuilding product behavior.

## Qualify appearance and material providers

Resolve saved headparts by stable plugin/record identity and actual HDPT type,
not array position alone. Validate the complete mapped list: intended face,
eyes, mouth/sculpt and retained parts. Bind the actor, every preset, private
face node and fallback geometry consistently. Review tint/texture providers
separately from mesh/headpart providers.

Distinguish actor-wide nodes from outfit-local nodes and empty local roots.
Reject ambiguous broad-slot matches. Inspect the actual supported equipment:
covered feet may need no bare-foot update; glove skin and fabric are different
surfaces. Treat absent, covered, unreadable and unsupported parts as distinct
states. One qualified outfit does not prove all outfits.

Capture original rendered values, whether an override existed, prior values,
owner identity and capture schema before changing anything. Reject foreign
ownership conflicts and stale captures. Version capture semantics independently
from a mod release.

## Use an explicit transition state machine

Separate requested, pending and committed form. Acquire busy ownership before
yielding; assign a generation/token so stale asynchronous work cannot commit.
Use one request path for scripts, console and player controls. Define whether
unsafe requests are rejected, queued or deferred.

A transaction should follow a declared sequence such as:

1. snapshot owned state and validate the request;
2. load/map the qualified preset and allow attachments to settle;
3. apply only owned material overrides;
4. read back rendered appearance where supported;
5. synchronize only declared owned abilities/style deltas;
6. commit the new form and publish diagnostics.

A native return or brief visible change is not a committed transition. Stored
overrides are not rendered readback. Preserve prior overrides; if none existed,
remove only the owned persistent entry and restore the observed original value.
Handle newly exposed supported skin before mutation. Refresh equipment without
unnecessary preset reloads.

On failure, retain the initiating cause, reconcile the previous committed form
and fault clearly if recovery fails. Do not erase evidence when rollback
succeeds. Define load/unload, combat, interruption and save behavior. A paused
menu is not evidence that work is progressing.

## Verify failure and recovery

Use fixtures for valid and malformed preset mappings, native-return failure,
interrupted requests, stale generations, ownership conflicts and recovery
failure. Compare independent binary/asset data. Source-string checks cannot
prove compiled alias behavior or runtime execution; use compiled script/runtime
evidence when the task requires it.

Diagnostics should retain actor identity, requested/pending/committed form,
generation, capture version, selected route, expected/observed material values,
failure cause and recovery result without exposing unrelated personal paths.

Qualify only the agreed cycle, such as base form to alternate form and back,
supported gear, declared ability changes and load/return behavior. Recheck
follower ownership and any save/load requirement. Keep operator acceptance,
visual review, static provider closure and game-runtime proof distinct.

Preserve plugin identity and inspect VMAD, SEQ, preset references and FaceGen
paths for the scoped output. A light flag is not proof of compaction or save
migration. Ship runtime files only; keep build evidence in the project.

Use [preset pipeline](../actorwright-preset-pipeline/SKILL.md),
[follower-kit authoring](../actorwright-follower-kit-authoring/SKILL.md),
[change control](../actorwright-change-control/SKILL.md),
[gates and validation](../actorwright-gates-and-validation/SKILL.md) and
[runtime proof](../actorwright-runtime-proof/SKILL.md) for those layers.
