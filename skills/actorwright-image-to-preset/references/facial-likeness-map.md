# Facial likeness map

Use this map to find and reduce visible differences that make a rendered face unlike its reference. It is a search aid, not a requirement to manufacture measurements for hidden or irrelevant features.

## Build the target map

Start with every feature the user marked or described. Add visually distinctive traits from each original view, including asymmetries. Rank them by recognition value and by how reliably the image shows them. Record pose, expression, occlusion, makeup, stylization, and likely geometry or appearance ownership. If views conflict, preserve the conflict instead of averaging it into invented anatomy.

Choose checks that would change a decision. Use named points or traced contours with definitions and image coordinates. Apply the same definitions to actual saved-preset renders. Record uncertainty and the stable scale used for normalization. Refer to [measurement and camera qualification](measurement-and-camera-qualification.md) before treating a difference as geometric.

## Feature inventory

| Region | Useful visible traits |
|---|---|
| Head and face frame | Forehead and cranial outline, hairline framing, face width at temples/cheeks/jaw/chin, vertical thirds, cheek fullness and projection, jaw taper, chin width and contour, left/right asymmetry. Measure hair separately from head geometry. |
| Eyes, each side independently | Anatomical inner and outer corners, opening angle, upper/lower lid arcs, aperture at several stations, exposed sclera, iris center/diameter, gaze, interocular spacing, under-eye contour, and eye-to-brow/nose relationships. Separate eyeliner, lash tips, shadow, and visible globe edge from anatomy. |
| Brows, each side independently | Inner start, arch, tail, centerline trajectory, thickness at several stations, taper, length, eye-to-brow distance, and asymmetry. Separate mesh/skin geometry from hair, tint, and painted texture. |
| Nose | Root and bridge shape, frontal tip and alar contour, nostril width/angle/exposure, columella, asymmetry, spacing to eyes and upper lip, and profile projection. A distinctive tip requires more than bridge-width measurements. |
| Mouth and lips | Each corner and its height, seam curve, asymmetry, width, Cupid's bow, upper/lower contours, fullness, philtrum and nose/chin spacing, plus profile projection when visible. Separate painted lip edge, highlight, and shadow from physical contour. |
| Ears and nearby profile | Placement, height, projection, outer silhouette, and relation to cheek/jaw where visible. Do not infer an obscured ear from a hair edge. |
| Appearance and expression | Eye and hair color, skin tone, makeup, hair silhouette, expression, pose, and gaze. Track these separately and route each to a component that can represent it. |

## Turn features into checks

For eye slant, compare the same defined inner/outer eye-opening boundary on each side, then measure eyeliner and lash direction separately. Never substitute a wing endpoint for an anatomical corner. For brows, compare inner/arch/tail heights, path, and thickness. For nose character, compare frontal tip/alar/nostril contour with profile projection. For a smirk, compare each corner, the seam, lip contours, and profile where available. For the face, compare cheek-to-jaw-to-chin silhouette and cross-feature spacing after registering stable anchors.

A point pair measures a position or distance; a three-point path describes an arch; a traced boundary describes a contour. State which one a check uses. If lighting, makeup, or expression could explain a mismatch, inspect a neutral and appearance-rendered view before attributing it to geometry.

## Compare and decide

For each priority feature, record the source definition, prior retained candidate, actual current render, uncertainty, observed direction, responsible-layer hypothesis, and status: improved, regressed, unresolved, or unavailable. Show clean context and feature close-ups beside the measurement overlay.

Do not sum unrelated ratios into a likeness score. A smaller aggregate residual cannot outweigh an obviously wrong defining feature. Mark a candidate not selected if a marked feature is unmeasured without a justified limitation, visibly wrong, or worse than the last retained candidate without a justified whole-face gain. Keep rejected attempts as failure evidence, not as the comparison control for a fresh attempt.

State assistant assessment, explicit user acceptance, and in-game proof separately.