---
name: actorwright-debugging-playbook
description: Diagnose an unexplained Skyrim mod failure from preserved evidence and discriminating tests before proposing a repair.
license: GPL-3.0-only
---

# Debugging playbook

Diagnosis is read-only unless the user authorizes a repair. A crash-log filename, plugin name, FormID, or nearby timestamp is a lead, not a root-cause finding.

Start with [startup and safety](../actorwright-startup-and-safety/SKILL.md). Do not change a plugin, asset, save, load order, profile, or game installation while diagnosing.

## Preserve and classify

Record the exact symptom and trigger, first bad and last known-good state, game and runtime versions, plugin order, mod-manager profile, relevant providers, reproduction steps, and available logs. Preserve the failing package and save; use copies or a disposable profile for experiments.

Resolve identities in separate layers: the plugin that owns a FormID, the winning plugin record, and the file provider that supplies each asset. A named plugin does not prove it supplied the mesh or texture that the game loaded. Papyrus logs can support script diagnosis but are not substitutes for native crash evidence.

## Test causes, not guesses

For every plausible cause, write down its mechanism, supporting and contradicting evidence, and a result that would distinguish it from alternatives. Change one causal plane at a time in an isolated test. Examples include new game versus affected save, actor naked versus equipped, record winner versus file provider, or the target versus a known-good control.

Use the smallest test that separates the hypotheses. A binary search can narrow a set of interacting mods, but it does not explain the interaction. Do not apply blanket cleaning, load-order changes, save repair, or asset replacement without a prediction tied to evidence.

## Stop and report

Classify each conclusion as a clue, correlated lead, environment-specific mechanism, or runtime-proven result. If evidence cannot discriminate causes, report UNKNOWN and name the missing observation. Stop if the only save or profile would be changed, provider identity is unresolved, environment drift makes runs incomparable, or the required operation is unauthorized.

When a root cause and repair authority exist, route the change through [change control](../actorwright-change-control/SKILL.md), rerun the relevant [gates](../actorwright-gates-and-validation/SKILL.md), and verify the specific claim in game using [runtime proof](../actorwright-runtime-proof/SKILL.md).