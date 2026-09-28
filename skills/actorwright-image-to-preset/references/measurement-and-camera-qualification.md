# Measurement and camera qualification

Measurements describe image projections. They do not recover physical dimensions, prove shared geometry, or establish likeness on their own.

## Make comparisons checkable

- Preserve the original image, dimensions, and hash. Record named pixel coordinates, definitions, ambiguity, annotation method, and a numbered overlay. Distinguish measured points from estimates and detector predictions.
- Measure pixels before ratios. Name the denominator: an eye-to-chin distance is not total face height. Ratios remove uniform scale, not pose, perspective, expression, or camera mismatch.
- Render the actual saved preset. Fitter-projected vertices are not a substitute for visible boundaries in the saved-preset image.
- Keep camera, lighting, geometry, materials, and framing fixed for before/after tests. Use uniform scale and translation, with documented roll correction if needed. Do not nonuniformly stretch or locally warp faces.
- Compare reference, last retained viable candidate, and current candidate. Never independently re-anchor each candidate in a way that hides shape drift.
- Report feature-level differences and uncertainty. Counts, optimizer convergence, low training error, or a successful fit do not become likeness percentages.

## Qualify reference views and camera assumptions

Record whether views are photographs, shared-model renders, separately drawn images, generated interpretations, or of unknown origin. A prompt that requests a profile angle or identity consistency is not camera metadata. Compare decoded pixels and dimensions when source provenance may resolve a disputed claim; file hashes can differ after re-encoding.

Treat multiple views as constraints on one head only when their provenance supports that interpretation. Generated or uncalibrated views can still be useful visual targets, but keep their shared-camera/model assumption uncertain.

A camera-only trial must freeze preset, geometry, and appearance. Declare the projection change, use qualified regions to fit it, and withhold another relevant region. Compare retained and proposed candidates under the same registrations and uncertainty. A nominal gain that reverses under a plausible alignment is not robust selection evidence.

## Resolve semantic boundaries

State exactly what is measured: painted lip edge, closed-lip seam, outer silhouette, eyeliner wing, anatomical canthus, and shading transition are different targets. Repeat placements on shuffled or transformed views can estimate annotation repeatability; jitter around one placement is only sensitivity analysis. Repeatability does not remove systematic ambiguity or establish anatomical correctness.

For profile fits, inspect the whole visible contour and the opposite view for regressions. Matching a few anterior points can leave lip and chin contours wrong. Keep camera parameters fixed across iterations; re-anchoring to each candidate can accumulate drift.

A surface-point constraint does not automatically bound the visible feature. Qualify projection-to-annotation discrepancy and carry a separate error allowance. If an image mask is used, inspect its registration, component identity, clipping, alpha, threshold sensitivity, and relation to the beauty image. A stable mask boundary is not automatically an anatomical boundary.

## Separate appearance from geometry

Decode provider textures and alpha rather than trusting filenames. Compare old fixtures with the actual selected provider under identical geometry and render conditions. Re-derive appearance from saved preset values and verify the serialized result.

Clay and textured views answer different questions. Offline tint or diffuse composition is not Skyrim shader or ENB parity. Test material changes with frozen geometry, cameras, and lighting; do not sculpt to compensate for a material or camera mismatch. If transferring a donor material, qualify topology, per-corner UVs, coordinate frame, normal space, and color-space interpretation first.

## Decide and stop

Keep training, saved-render, and withheld-view results separate. Once a held-out feature guides a correction, it becomes development evidence. Check the saved artifact after each change and revisit the whole face.

A candidate is not selected if a high-priority feature fails or regresses, even when aggregate fitting error falls. Stop a branch when the next feasible test is unlikely to change the selection decision; record remaining uncertainty rather than claiming geometric impossibility, a recovered camera, or global optimum.