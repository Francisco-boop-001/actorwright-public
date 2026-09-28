---
name: actorwright-image-to-preset
description: >
  Create or refine a Skyrim RaceMenu face preset from reference images using
  qualified previews, feature-level comparison, and explicit likeness limits.
license: GPL-3.0-only
---

# Image to RaceMenu preset

Create the closest supported face likeness from the supplied evidence. Measurements help locate differences; they do not replace looking at the rendered face or establish a likeness score.

## Boundaries

- Treat this folder as workflow guidance. It does not include fitters, renderers, game assets, templates, or an Actorwright executable.
- Use the exact Actorwright executable configured by the user. Verify its version, capabilities, help, and schemas before relying on a command. A source checkout or release note is not a binary qualification.
- Follow [startup and safety](../actorwright-startup-and-safety/SKILL.md), [change control](../actorwright-change-control/SKILL.md), and [gates and validation](../actorwright-gates-and-validation/SKILL.md). Use only the active project workspace for copied inputs, previews, reports, and outputs. Do not write to a live game, mod-manager, or other protected root.
- If the selected Actorwright build requires a K-local workspace, use the configured project on a local K: volume and honor its protected-root policy. Do not work around a refusal.
- Read the supplied reference image at full resolution. Do not regenerate, edit, or silently crop it. Preserve the original and record its dimensions and hash.
- A single view cannot establish hidden depth or profile shape. Generated or separately illustrated views do not prove a shared model or camera.
- Keep face, body, headpart, texture, and runtime ownership distinct. COtR, UBE, and other head systems are separate routes; a BodySlide preset is not a face preset.
- Separate offline appearance, explicit user acceptance, in-game Step Zero, and runtime behavior. An offline pass does not prove RaceMenu reproduction.

## Workflow

1. **Define the requested result.** Record the target race and sex, head system, available views, required headparts, output type, and whether the request ends at a draft preset or continues to NPC authoring.
2. **Pin the target environment read-only.** Identify enabled plugins, winning providers, RACE tint list, headparts, TRI files, textures, and relevant identifiers from the current load order. File presence alone does not prove a provider is enabled or winning. Copy only authorized inputs into the project.
3. **Start from a real baseline.** Use a RaceMenu-written preset for the same race, sex, and head ecosystem. Preserve the original preset and exports. Do not invent JSON fields, copy another race's tint ordering, or assume that saved morphs are supported by the target head.
4. **Map the likeness before changing controls.** Read the [facial likeness map](references/facial-likeness-map.md). Record each user-marked and identity-defining feature, its source view, visibility, ambiguity, and a check that can distinguish a match from a miss.
5. **Record source and rendered landmarks.** Before selecting a candidate, annotate the original reference and the actual image rendered from the saved preset. Keep original-resolution pixel coordinates and definitions, uncertainty, image hashes, and a numbered overlay. Normalize only after recording pixels and naming the denominator. See [measurement and camera qualification](references/measurement-and-camera-qualification.md).
6. **Qualify the preview.** Use copied, hash-bound providers and the same saved preset for every comparison. Keep camera, lighting, framing, materials, and render settings controlled. Inspect full-face context and feature close-ups. Label a renderer-only inspection as such; Actorwright preview is not automatically independent preset reconstruction or game rendering. See [preview qualification](references/preview-qualification.md).
7. **Compare and refine.** Present reference, last retained viable candidate when one exists, and current candidate for every comparable view. Use uniform scale and translation only. Record concrete differences and the smallest supported change. Re-read the saved preset and render it again after changes. Preserve approved regions and reject regressions to marked features.
8. **Stop on evidence.** Continue while a specific supported adjustment visibly improves the face without unacceptable regressions. Stop when no actionable improvement remains or a documented limit blocks it. Do not claim a global optimum or convert measurements into a likeness percentage.
9. **Gate and hand off.** Run relevant structural and dependency checks on the written preset and exact preview inputs. Keep a project record binding the selected preset hash, comparison images, feature verdicts, user acceptance, and known preview-to-game differences. Follow [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md).

## Feature selection rules

- Inspect both eyes independently: anatomical opening corners, lid arcs, aperture, brow relation, and asymmetry. Measure eyeliner, lashes, and painted shadow separately from anatomy.
- Check nose tip, alae, nostrils, and profile projection together; bridge width alone is insufficient.
- Check lip seam, each mouth corner, contour, and profile projection; do not treat a painted edge as the anatomical boundary.
- Compare continuous cheek-to-jaw-to-chin contour, not only widths at selected heights. Distinguish a preferred locked region from a requested open correction.
- Separate geometry from pose, expression, hair framing, makeup, texture, lighting, and camera. Do not distort geometry to compensate for an unqualified preview.
- A user-marked feature that remains visibly wrong blocks a “close” or “best” likeness claim even when broad measurements improve.

## Format cautions

- Resolve identifiers from the current enabled load order. ESL-space identifiers, plugin records, and JSlot slot numbers have different meanings.
- JSlot tint indexes, race tint-list indexes, RGBA/ARGB fields, and tint strengths are separate contracts. Verify them against the target RaceMenu preset and current RACE/provider data.
- NAM9 morph order and sentinels, NAMA signed values, overlays, and headpart slots are format-sensitive. Validate with the target writer/parser rather than importing constants from an old project.
- Do not write a preset from in-memory assumptions alone: save it, re-read it, and derive the preview from that saved artifact.

When the draft becomes an NPC/follower precursor, record the in-game Step Zero result for that exact preset, then continue with [the preset-to-NPC pipeline](../actorwright-preset-pipeline/SKILL.md). Runtime claims follow [runtime proof](../actorwright-runtime-proof/SKILL.md).