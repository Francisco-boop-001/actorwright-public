---
name: actorwright-preset-pipeline
description: >
  Convert or audit a RaceMenu preset and exported geometry for a Skyrim NPC,
  preserving provider identity, supported geometry, and proof boundaries.
license: GPL-3.0-only
---

# Preset to NPC

Use this workflow only after the user has authorized the requested project work and the source appearance has passed the appropriate acceptance step. Follow [startup and safety](../actorwright-startup-and-safety/SKILL.md) and [change control](../actorwright-change-control/SKILL.md).

## Establish the executable and authority

Use the exact Actorwright executable configured by the user. This source-only repository does not provide or certify an executable build. Check that executable's version, capabilities, command help, and exported schemas before relying on a command. Capability discovery does not grant permission to mutate files or promote a package.

Set the workspace and protected-root values required by that build. If a workspace-bound command requires a local K: volume, use a compliant project location; do not bypass its refusal. Keep game, mod-manager, save, and other protected roots read-only. Follow [gates and validation](../actorwright-gates-and-validation/SKILL.md).

Before construction, record dependency enablement and the operator's Step Zero result for the exact preset loaded in the current game. Do not invent missing screenshots, provider hashes, or formal runtime authority. If Step Zero is outside the authorized scope, state that runtime proof is still required rather than asking again for already-given routine authorization.

## Preserve the route

Bind immutable source JSlot/NIF/DDS hashes, selected variant, target identity and local ID, head system, body route, outfit intent, and providers. COtR, UBE, HPH, CBBE 3BA, and other providers are distinct routes. Fixed geometry and runtime OBody or other morph ownership are separate; never apply the same morph layer twice.

A JSlot is not a complete plugin specification. Resolve its headparts and identifiers from the current enabled load order and inspect records, model paths, textures, morphs, and helper closure independently. Presence is not enablement or winning-provider proof.

## Decompose the operation

1. Preserve the failed or proposed request, hashes, exit status, diagnostics, and resulting files.
2. Inspect each relevant command's mode, schema, effects, and refusal behavior. Record creation, patching, geometry transport, texture routing, assembly, and verification are separate operations.
3. Search for successful same-route artifacts and executed commands. Verify the executable and input hashes still match. Plans and descriptions do not prove execution.
4. Qualify the smallest end-to-end operation with the selected provider families: create one host, reopen it independently, inspect asset references, and establish which system owns each runtime layer.
5. Use only a supported command advertised by the current executable. If a required operation is absent, a bounded target-specific patcher may be considered only within the user's authorization and with independent verification. This does not authorize a replacement executable or permission bypass.

A refusal at one stage does not prove later stages are unavailable. Distinguish shell quoting, protected-root refusal, missing provider, unsupported contract, and implementation failure. Do not scale an unqualified operation into a production build.

## Preserve appearance and records

- Reserve compatible new light-plugin identifiers only when that is the chosen format. Re-resolve the current load order; ESL space, local IDs, and JSlot form identifiers are not interchangeable. Never compact or rename FaceGen files by guessing.
- Preserve the original preset, accepted exports, and provider inputs. For a baked route, treat the accepted NIF/DDS as appearance authorities and verify the plugin references those exact assets.
- Never generically reserialize a FaceGen NIF as a preservation operation. For a scoped texture-string edit, verify block boundaries, lengths, and every unaffected block against the original.
- DDS header masks and payload byte order must agree. Check decoded pixels, alpha, and color-space handling. Do not copy a header onto unrelated payload bytes.
- Keep race tint indexes, JSlot tint array positions, tint color, and strength separate. Verify the actual winning RACE list and target convention; do not copy another character's numeric values.
- Preserve the full saved-preset morph state when the runtime route owns it. A disposable intermediate may omit unsupported fields only when explicitly labeled and independently checked against the accepted source.
- Bind matching FaceGeom, FaceTint, texture paths, plugin, and local ID. Verify reachable record references and asset closure from the written plugin, not an in-memory plan.
- Keep body, head, outfit, skin, and runtime morph ownership explicit. Do not alter an approved body system or substitute a provider to hide a failed face route.
- Prefer existing-marker quest/alias placement when suitable. Do not copy exterior world/cell records as a default. Preserve sidecars and inspect outfit slots for conflicts with selected hair or headparts.

Use current Mutagen/provider writers and independent readers where available; see [Mutagen patchers](../actorwright-mutagen-patchers/SKILL.md). Historical record templates, FormIDs, tint lists, and placement examples are target-specific, not reusable constants.

## Verify, package, and finish

Run intake, record, dependency, and FaceGen checks on written files. Select the exact package root when multiple outputs exist. Use typed and independent raw readers for fragile records; compare preserved NIF blocks and decoded DDS content where relevant. Keep synthetic construction aliases out of final payloads.

Package only required runtime/install files and required user-operation files. Keep unused source copies, private provenance, and build evidence in the project rather than the install archive. Include licenses and notices for redistributed components. A static pass does not establish likeness, follower recruitment, AI/combat behavior, physics, or in-game appearance.

Follow [complete NPC authoring](../actorwright-complete-npc-authoring/SKILL.md), [follower kit authoring](../actorwright-follower-kit-authoring/SKILL.md), and [NPC transformation](../actorwright-npc-transformation/SKILL.md) when those lifecycle tasks are in scope. Follow [runtime proof](../actorwright-runtime-proof/SKILL.md) for game claims and [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md) for the handoff record.