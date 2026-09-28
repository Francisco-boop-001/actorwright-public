---
name: actorwright-follower-kit-authoring
description: "Plan or verify a follower lifecycle for an appearance-qualified Skyrim NPC, including recruitment, equipment, combat, placement, dialogue, voice and save behavior."
license: GPL-3.0-only
---

# Follower-kit authoring

Add only the follower behavior admitted by the task. Preserve the accepted
appearance and provider route; an accepted preset is not proof that the
assembled actor or its runtime providers are correct.

## Establish the target and authority

Resolve the actor base, plugin/FormID, placed reference or alias, accepted
appearance/provider identities, current branch, requested follower behaviors
and starting-save scope. Inspect the actual workspace policy, project evidence,
registered tools and exact executable. Use
[startup and safety](../actorwright-startup-and-safety/SKILL.md) for current
tool discovery. A skill or capability does not grant game-facing writes,
installation, runtime testing or release promotion. Carry forward authorization
already granted for its stated scope; seek new authority only if a proposed step
exceeds it.

Read [complete NPC authoring](../actorwright-complete-npc-authoring/SKILL.md)
for placement and shared-record conflicts and
[preset pipeline](../actorwright-preset-pipeline/SKILL.md) for appearance.
Qualify clothing and armor against the accepted race/body route; do not change
the body to make gear fit. If a required helper or provider manifest is absent,
do not invent its behavior or substitute an unqualified tool.

## Choose one lifecycle owner

Decide whether the actor uses the supported vanilla follower lifecycle or an
explicitly owned custom controller. One system owns recruitment, follow, wait,
trade, command mode, dismissal and home return. A personality/quest layer may
supplement that lifecycle only when it does not compete for those transitions.

For vanilla behavior, verify the actual faction ranks, relationship and
dialogue prerequisites, voice support, and all required follow/wait/trade/
dismiss/home/recruit paths. A faction alone does not create recruitment
dialogue. Review follower-framework hooks and compatibility or state a scoped
exclusion.

For a custom controller, define every state and transition, including alias
enrollment/cleanup, combat and alarm interruption, bleedout/death handling,
recovery, save/load and quest restart. Establish how active ownership transfers
if another framework is supported. Do not let two controllers own the actor
simultaneously without a tested handoff.

## Build the role contract

Align class, level bounds, attributes, combat style, AI data, weapons, armor,
spells, perks, inventory and protection policy with one declared combat role.
Inspect winning providers and record semantics; editor IDs and perk counts are
not proof. Preserve the accepted outfit/body route and verify slot, race, mesh
variant, ammo, weapon selection and trade behavior.

For placement, prefer an existing persistent marker or a start-enabled quest
with a marker alias when that meets the requirement. Verify alias dependencies,
actor creation, package targets and SEQ. A fixed reference has its real CELL or
WRLD conflict surface; an editor-ID-only or partial world record does not
remove that risk. Use the detailed
[placement and dialogue contracts](../actorwright-complete-npc-authoring/references/complete-npc-contracts.md).

## Close dialogue and voice behavior

A player-facing topic must resolve through the owning quest, correct branch,
top-level starting topic and INFO responses. Verify conditions, VMAD properties,
fragments, actor/quest ownership, raw links and SEQ separately. For each
reaction define its trigger, conditions, cooldown owner, once-only state,
interrupt behavior and unlock/completion event. Unlocking after a line requires
a completion path, not just topic selection.

Preserve approved text and voice identity. Verify generation or recording,
WAV integrity, LIP/FUZ encoding, final voice type and plugin/FormID paths, and
INFO binding as distinct steps. Listen to representative lines and test
in-game playback and interruption. PEX compilation alone proves neither VMAD
binding nor controller execution. Never ship placeholder fragments or inert
guards as implemented behavior.

Before bulk dialogue/audio work, validate representative conditions against
the actual source plugin/provider closure and integrate one response through
the intended route. Reuse matching evidence where valid. A synthesis ledger,
schema match or audio-structure check cannot manufacture agreement or replace
listening.

## Analyze, write and verify

Inventory inputs and providers read-only. Save a proposal that identifies the
controller owner, records, dialogue/voice sidecars, allowed changes, protected
appearance, output path and acceptance tests. Use already-granted authority
within its scope; seek new authority only for an action outside it. Create a
new sandbox output and reopen that exact artifact. Use typed plus independent
raw verification for record links, FormIDs, VMAD, SEQ, asset paths and
protected bytes. At the first master merge, verify explicit master order
separately from link resolution; recheck local FormID encodings in SEQ and other
sidecars after every master-list change.

Use the registered Actorwright operation when its current help/schema supports
the task. For an approved, bounded gap, follow
[Mutagen patcher guidance](../actorwright-mutagen-patchers/SKILL.md); do not
change product code or replace the selected executable.

## Acceptance

Keep static/package proof separate from runtime proof. The scoped runtime plan
may include actor/provider identity, recruit/dismiss/home return, wait/follow,
trade/command mode, outfit, combat, voice/interruption, save/reload, transitions
and preserved appearance. Run only the checkpoints required by the task and
report unavailable ones as unverified, not passed.

Use [runtime proof](../actorwright-runtime-proof/SKILL.md),
[gates and validation](../actorwright-gates-and-validation/SKILL.md),
[change control](../actorwright-change-control/SKILL.md) and
[evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md) for
their respective scopes.
