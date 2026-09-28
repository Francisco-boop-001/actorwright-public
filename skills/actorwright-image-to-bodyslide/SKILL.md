---
name: actorwright-image-to-bodyslide
description: >
  Create or refine a Skyrim BodySlide/OBody body preset from reference images
  using the actual target SliderSet, measured silhouettes, and explicit
  clothing, pose, and runtime limits.
license: GPL-3.0-only
---

# Image to BodySlide preset

Build an evidence-backed body preset from the supplied images. Measurements constrain proportions; they do not justify translating descriptive words into arbitrary sliders.

## Boundaries

- This workflow does not include a solver, renderer, BodySlide installation, game assets, or OBody tools. Discover and qualify the tools available in the user's environment before relying on them.
- Work in the active project. Copy only authorized presets, SliderSets, ShapeData, and provider evidence into it. Do not write to a live game or mod-manager root.
- Determine the actual body ecosystem first. CBBE 3BA, other CBBE variants, and other body systems are not interchangeable. Do not substitute a route the user did not choose.
- Separate body shape from height, pose, camera perspective, clothing structure, normal-map detail, and physics. Images with covered anatomy cannot define the hidden silhouette.
- Offline silhouette checks do not establish in-game appearance, OBody selection, or runtime readiness.

## Workflow

1. **Define the target.** Record body ecosystem, available views, pose and clothing limits, weight behavior, intended preset format, and whether OBody assignment is in scope.
2. **Measure the references.** Preserve originals. Annotate visible front/profile landmarks at original resolution, record camera and perspective uncertainty, and mark covered or cropped anatomy unavailable.
3. **Pin the provider truth.** Resolve the target SliderSet by matching its exact name against several real preset XML files. Check slider names, group names, defaults, and small/big semantics against the selected set. Do not choose an OSP by filename or a preset by resemblance alone.
4. **Resolve ShapeData independently.** Follow the actual OSP data folder and provider order to the source NIF and morph-diff files. Confirm that the chosen ShapeData belongs to the intended set; a similarly named NIF may be a renamed copy with different references.
5. **Calibrate round zero.** Render the unmodified base mesh first. Establish body-height normalization and silhouette bands on that render before solving sliders.
6. **Build and bound the candidate.** Compare multiple compatible presets when initializing. Record slider bounds and corpus outliers. Use the actual OSD/BSD diffs on the actual ShapeData vertices; preserve SliderSet defaults for omitted values. A numeric optimizer result is a proposal, not a visual decision.
7. **Iterate from written XML.** Keep front/profile renders, side-by-side sheets, measurements, slider diffs, and residuals per round. Re-read the written XML, rerender it through the same route, and inspect the actual silhouette. Stop after six rounds unless a new testable hypothesis justifies another round.
8. **Gate and package.** Validate every slider against the selected set, parse and rerender the final XML, and place it at `CalienteTools/BodySlide/SliderPresets/<Name>.xml` in the package. Verify requirements and bind a SHA-256 manifest. Follow [gates and validation](../actorwright-gates-and-validation/SKILL.md) and [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md).

## Format and measurement invariants

- The canonical OSP is the one whose SliderSet name exactly matches known-good preset set attributes.
- For OSD, validate magic, version, named diff records, exact EOF, vertex indices, and sane offsets. For BSD, treat it as headerless only after record-size divisibility and index/offset checks succeed.
- A skinned source NIF may store vertices in a skin partition even when the shape header reports zero. Validate the actual vertex layout and coordinate magnitudes.
- Morphologically close a binary silhouette and measure the central contiguous run so arms do not inflate torso width. Keep bust bands below armpit merges.
- Express bands as fractions of the rendered silhouette's own height. State the row selection and width rule for each band.
- Use small == big for weight-invariant presets by default only when that interpretation matches the selected route; preserve deliberate weight interpolation when requested.
- Omit zero-valued sliders only when absence has been verified to mean the SliderSet default. Never assume absent and zero are equivalent.

Keep face likeness, face geometry, body proportions, outfit fit, and runtime physics as separate decisions. OBody assignment or distribution is a follower-stage choice unless the user explicitly includes it.