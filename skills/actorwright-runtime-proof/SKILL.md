---
name: actorwright-runtime-proof
description: Verify appearance and gameplay in the actual target Skyrim runtime without treating static checks as visual or behavior proof.
license: GPL-3.0-only
---

# Runtime proof

Use this skill when the claim concerns what Skyrim renders or how a mod behaves in game. A parser, preview, package inventory, or static PASS cannot establish those outcomes. Read [startup and safety](../actorwright-startup-and-safety/SKILL.md) and [change control](../actorwright-change-control/SKILL.md) before any operation that might write to a game profile or save.

## Prepare a controlled test

- Use an explicitly authorized test profile and a disposable or backed-up test save. Do not mutate the user's only save, live profile, or protected game root as a convenience.
- Record the game and script-extender versions, enabled plugins and order, mod-manager profile, relevant settings, and the exact package hash being tested.
- Confirm the target actor by plugin and FormID. Confirm the active plugin winner and the actual winning providers for FaceGeom, FaceTint, body, skin, headparts, textures, scripts, and other claimed dependencies. Record hashes where the provider can be resolved.
- Choose a known-good comparison actor when appearance or lighting is under review. Keep both actors in the same scene and lighting, and avoid camera, weather, ENB, or profile changes between comparison shots.

## Test the claim that was made

For an appearance claim, inspect the whole face and requested features, as well as neck, body, hands, eyes, and outfit under the relevant lighting. A numeric fit or matching isolated feature does not overrule a visible whole-face failure.

For a gameplay claim, write exact steps and expected results before testing: for example, recruitment, dismissal, follow/wait, equipment, dialogue, combat, transitions, and save/reload. Test only the behaviors the package claims to provide.

Retain original comparison images, screenshots, reproduction steps, package and provider identities, and the operator's verdict. State which evidence is objective and which is qualitative. User approval is scoped to the candidate and result actually reviewed.

## Report honestly

Separate these outcomes: static validation, offline preview, in-game observation, and human visual or gameplay acceptance. Record failures and unresolved conditions. If the game, profile, required provider, safe test save, or operator review is unavailable, report runtime proof as pending; do not infer success from absence of a crash.

Use [debugging playbook](../actorwright-debugging-playbook/SKILL.md) when a runtime test fails, and [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md) to bind the result to the package.