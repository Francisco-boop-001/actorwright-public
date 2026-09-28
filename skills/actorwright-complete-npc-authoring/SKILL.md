---
name: actorwright-complete-npc-authoring
description: "Design or audit a Skyrim NPC lifecycle spanning placement, routines, combat, follower or merchant roles, dialogue ownership, and record conflicts."
license: GPL-3.0-only
---

# Complete NPC authoring

Build the smallest lifecycle that satisfies the approved role. “Complete” is
role-specific: do not add follower, merchant, travel, quest, or controller
systems without a requirement.

## Establish scope and authority

Start from the assigned mod workspace and its current project records. Identify
the target actor, plugin and FormID; any placed reference or alias; accepted
appearance and provider identities; requested roles; starting location; branch;
and the records or files explicitly in scope. Reconcile conflicting evidence
before choosing a target.

Follow [startup and safety](../actorwright-startup-and-safety/SKILL.md) for the
actual workspace boundary, tool inventory and executable discovery. Query
capabilities, command help and schemas from the exact executable selected for
the task. A source checkout, historical report, or similarly named command is
not proof that an executable is available or that a command is supported.

A skill does not grant write, package, installation, runtime, or promotion
authority. Carry forward authorization already granted for its stated scope; seek new authority only if a proposed step materially exceeds it. Keep source inputs read-only, write only to an approved sandbox
output, and protect accepted appearance, provider, body, skin, voice and
identity layers unless the request explicitly changes them. If the workspace
policy, protected root, tool provenance or authority is absent or ambiguous,
continue read-only analysis and resolve the boundary before a dependent write.
Do not infer a private default path or recreate a missing local helper.

## Define a role contract

Before selecting records, record:

- target identity and accepted appearance/provider hashes;
- start and home location;
- roles in scope and explicit exclusions;
- daily states, fallback behavior and combat identity;
- follower-controller owner, if applicable;
- merchant services, if applicable;
- essential, protected and respawn policy;
- permitted shared-record signatures;
- static and target-specific runtime proof.

Select the least complex architecture that closes this contract:

1. stationary or decorative actor;
2. resident or worker with a bounded package stack;
3. ordinary follower using one established follower lifecycle;
4. merchant with a complete vendor contract;
5. custom quest/controller only when simpler routes cannot meet the requirement.

Do not combine vanilla and custom controllers accidentally. Do not treat a
faction, package name, editor ID, or available command as proof that the
behavioral contract is complete.

## Choose the lowest-conflict placement

Prefer, in order:

- routine changes to an existing placed actor using existing persistent
  markers;
- a Start Game Enabled quest whose aliases bind an existing marker and create
  the actor there, with the matching SEQ generated and checked;
- a fixed reference in an existing interior only when the requirement needs
  that identity and the winning CELL is reviewed as a whole-record override;
- fixed exterior placement only with explicit world hierarchy and conflict
  analysis.

For alias placement, verify quest start conditions, alias dependencies and
order, actor creation, package targets, SEQ FormID encoding, first-load
behavior and save/reload without duplicate actors.

A winning CELL is not a field merge. Review omitted fields and intended
providers such as ownership, lighting, encounter zones and child references.
An exterior NPC can introduce WRLD, exterior CELL, LAND, WATR, NAVM/NAVI and
related world changes. Never submit a partial world parent or unexplained
navigation or landscape data. State the conflict tradeoff before writing.

## Close behavior and record contracts

Order packages from narrow emergency, quest or scene conditions, through
scheduled work or travel, leisure and sleep, to a broad safe fallback. For
each package inspect the template and procedure, schedule, conditions, target,
radius, owner quest, VMAD and fragments, must-reach/complete flags, interrupt
behavior and path reachability. Test combat, dialogue, furniture contention,
recruit/dismiss, cell transitions and save/reload. Avoid perpetual scripted
idles, unbounded update loops and repeated package or scene evaluation.

Align class, level model, attributes, combat style, AI data, equipment, spells,
perks, crime and faction behavior with one declared role. Perk count is not
completeness. Check friendly fire, assistance, morality, relationship and
alarm behavior. Qualify outfit and armor-addon support against the already
accepted body and race route; do not alter appearance to make an outfit fit.

Treat follower and merchant status as separate closed contracts. Follower
faction membership alone does not create recruitment dialogue. Merchant
service requires the intended vendor faction and flag, buy/sell lists, filters,
container, hours, service target/radius, barter dialogue and runtime trade
checks.

Player-facing dialogue must form a closed graph from DIAL through its DLBR
branch and owning QUST to the correct top-level starting topic and INFO
responses. Verify typed links and the relevant raw link fields. Orphaned,
branchless or unreachable topics block promotion. Preserve voice identity and
bind the final voice asset to the correct plugin/FormID path; generated audio,
LIP/FUZ structure and in-game playback are separate checks.

See the included [NPC lifecycle and conflict contracts](references/complete-npc-contracts.md)
for detailed record and verification checklists.

## Analyze, propose, write, verify

1. Inventory masters, providers, current record signatures, winning cells,
   relevant environment identity and protected inputs without writing.
2. Save a typed proposal with exact records and paths, predicted signature and
   FormID delta, placement, package order, controller ownership, protected
   surfaces and required runtime checks.
3. Use already-granted authority within its scope; seek new authority only for an action outside it. Use the registered
   supported operation when it covers the task. If it does not, a bounded,
   approved local patcher may serve a specific gap; follow
   [Mutagen patcher guidance](../actorwright-mutagen-patchers/SKILL.md).
   This does not authorize changing Actorwright product code or replacing the selected executable.
4. Write a new sandbox artifact, reopen that exact output, then check it with
   typed and independent raw readers. Verify the declared record delta,
   protected bytes, dialogue/SEQ closure, asset paths and absence of undeclared
   records.
5. Run applicable static and package gates. Report only the proof obtained.
   Static staging does not prove behavior, appearance or compatibility in game.

Use [change control](../actorwright-change-control/SKILL.md),
[gates and validation](../actorwright-gates-and-validation/SKILL.md),
[evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md) and
[target-specific runtime proof](../actorwright-runtime-proof/SKILL.md) for
those stages.

## Stop conditions

Stop the dependent write or promotion when target, role, controller ownership,
provider closure or placement authority is unresolved; a reused package cannot
be semantically inspected; a shared CELL/world surface is unexplained; dialogue
or SEQ links are incomplete; protected records cannot be preserved; or a
required runtime checkpoint fails. Continue independent read-only analysis and
unblocked work. Do not describe a static pass as a completed NPC lifecycle.
