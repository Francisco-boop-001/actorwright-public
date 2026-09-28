# Preview qualification

An offline render is useful only for the question its inputs and renderer can answer. Inspect the exact Actorwright build's current capabilities and schemas before using any preview command. A command that inspects an existing FaceGeom is not independent preset reconstruction unless it actually consumes the saved preset and relevant morph/default/sculpt data, creates verified geometry, and preserves ancillary parts.

## Assemble before judging

When geometry is transferred into a carrier, inspect actual imported local and world coordinates for every component. Include evaluated deformation/modifiers at render time, or say that final evaluated coordinates remain unverified. Importer skinning can alter local coordinates; applying a reported object matrix to stored vertices may not reproduce the rendered result.

Preserved normals, skinning, and shader bytes are donor data, not proof of target shading. A same-importer probe verifies shared code paths, not game-engine parity. Fix preview assembly before changing likeness controls or sculpting to compensate for it.

## Use previews for controlled questions

For a one-factor appearance comparison:

1. Hold geometry, host, camera, lights, and other materials fixed. Bind the original reference, retained preset, and current candidate to their hashes.
2. Substitute only the reviewed appearance input in a fresh diagnostic copy. Verify unchanged geometry/plugin bytes and the exact copied texture. This tests that input, not that a saved headpart or tint selection is reconstructed correctly.
3. Keep renders, provider/material receipts, warnings, images, and masks. Repeat an identical-input control before relying on small differences.
4. Present reference / retained / current triples for every comparable view, clean and annotated, with regional measurements. Use uniform scaling and translation. Label a comparison diagnostic when the camera is not qualified.
5. Check the target region under more than one relevant lighting condition and inspect regressions elsewhere. Pixel color is an image proxy, not physical color or overall likeness.

A preview mask may combine hair, head, eyes, lashes, brows, and mouth. Inspect the current output contract and actual pixels before calling it head-only, an eye-aperture mask, or a luminance measurement. Landmark counts are not coordinate measurements. A default view name does not establish camera pose.

## COtR and other head ecosystems

COtR, UBE, HPH, and other head ecosystems are distinct provider routes. Recheck the current Blender/PyNifly versions and options for the chosen route; old importer behavior is a warning to test, not a universal current defect.

Before likeness tuning, verify:

- the selected head, brows, eyes, morphs, textures, UVs, and all declared hair/helper components;
- whether the importer loads shape keys automatically, and whether explicit morph application would double-apply them;
- that each component receives only its own supported morphs, including eye-position controls where applicable;
- actual diffuse and alpha bindings, texture color spaces, and the preview's color conversion;
- that a known nonzero control moves the intended component and that restoring the baseline restores the baseline output.

Missing UVs, stale textures, absent helper geometry, color-space errors, or a misplaced component are preview defects. Correct them and revert compensating preset edits before reassessing likeness. A nonblank render, a successful import log, or a close camera match alone does not qualify the preview.

## Keep proof levels separate

A renderer-only inspection can answer a bounded appearance question. Independent preset reconstruction requires a verified saved-preset input and output. In-game Step Zero and runtime behavior remain separate. Do not use a tool limitation to claim a head-system limitation.