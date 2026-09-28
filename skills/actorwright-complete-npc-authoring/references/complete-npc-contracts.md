# NPC lifecycle and conflict contracts

Apply only the sections admitted by the target role contract. A checklist
organizes review; it does not grant write authority or prove behavior.

## Role and architecture

| Role | Minimum closed lifecycle | Do not add by default |
|---|---|---|
| Stationary or decorative | safe start, bounded sandbox/idle, fallback | follower, merchant, travel scripts |
| Resident or worker | home, work/leisure/sleep stack, fallback, ownership/crime coherence | custom controller |
| Ordinary follower | recruitment, follow, wait, trade, dismiss, return home, combat role | a parallel custom follower system |
| Merchant | resident routine plus complete vendor service contract | follower status unless requested |
| Custom follower | owned recruit/follow/wait/trade/dismiss/home/recovery state machine | competing vanilla controller |
| Quest actor | aliases, stages, scenes/dialogue, recovery and end state | always-running logic without need |

Record the target base/reference/alias/plugin/FormID, accepted appearance,
start/home, roles, daily states, combat identity, controller owner, merchant
needs, protected policy, allowed shared signatures, exclusions and proof.

## Placement

| Need | Preferred route | Required proof |
|---|---|---|
| Routine change for a placed actor | NPC/PACK changes using existing persistent markers | package semantics and runtime pathing |
| New actor in shared interior | start-enabled quest and marker alias | alias binding, SEQ, first load and save/reload |
| Fixed interior identity | complete intended CELL winner plus ACHR | whole CELL semantics and load-order review |
| New exterior actor | existing persistent marker when viable | spawn, pathing, unload/reload and fast travel |
| Fixed exterior coordinate | faithful WRLD/CELL hierarchy and conflict plan | hierarchy, terrain, water and runtime checks |

For quest-alias placement, confirm the quest start condition is coherent,
marker aliases resolve, actor aliases create at the marker, dependencies are
ordered, and packages target explicit aliases or references. Regenerate and
inspect SEQ whenever the relevant start-enabled quests change.

A winning CELL replaces the record. Compare lighting, water, ownership,
encounter-zone and child-reference data against intended providers. Do not
assume an editor-ID-only override is harmless if it wins. For exterior work,
inspect the full world and child-cell hierarchy; avoid partial WRLD parents and
unexplained LAND, WATR, NAVM/NAVI or related records.

## Packages, schedules and AI

Order packages by priority:

1. narrow emergency, quest or scene conditions;
2. scheduled role work or travel;
3. meals, social activity, leisure or home;
4. sleep;
5. broad safe fallback.

For every package resolve its template/procedure, schedule, conditions, target,
radius, owner quest, VMAD/fragments, interrupt and must-reach/must-complete
flags. Check reachable markers and behavior in dialogue, combat, recruitment,
dismissal, cell transition and save/reload.

Choose aggression, confidence, assistance, morality, responsibility, energy and
mood deliberately. Review crime, ownership, relationship and reaction factions.
Choose one combat role and align class, level bounds, attributes, combat style,
AI data, equipment, spells and perks. A perk is useful only if the actor can
exercise it. For equipment, verify ARMA race/sex coverage, body/accessory slots,
mesh variants, ammo, weapon selection, outfit and trade behavior.

Prefer furniture and idle-marker sandboxing to scripted animation loops. Guard
every script event, interruption and recovery path. Repeated package evaluation,
scene starts, actor spawning or unbounded update loops are failure surfaces.

## Follower lifecycle

For vanilla follower behavior, check actor uniqueness, essential/protected
policy, follower factions and ranks, relationship/dialogue prerequisites,
voice support, follow/wait/trade/dismiss/home/recruit paths and framework
compatibility. Home packages must not outrank active follower behavior.

For a custom controller, assign one owner for recruitment, follow, wait, trade,
dismiss, alias cleanup, home return and recovery. Test death/bleedout, combat,
alarm, teleport/summon policy, save/load and quest restart. Never let two
systems own the same actor without a tested handoff.

## Merchant service

A real merchant needs the correct vendor faction and flag, buy/sell list,
filters, unique container, service hours and location/radius, barter conditions,
package behavior, container ownership/reset policy and runtime stock/gold/buy/
sell checks. A job/profession faction does not replace vendor data.

## Dialogue and quest closure

Player dialogue should close through the owning quest:

- DIAL belongs to a valid DLBR branch;
- the branch belongs to the intended QUST;
- the quest has a reachable player/top-level starting topic;
- INFO responses, conditions and links are attached to the intended topics;
- VMAD properties and fragments resolve;
- SEQ encodes the correct quest IDs for the target plugin.

Inspect typed relationships and independent raw link fields. Test recruitment,
dismissal, interruption, unlock timing and save/reload. A selected topic is not
proof that a completion event ran; a compiled script is not proof that VMAD
binds or the controller executes.

## Proposal and verification

A proposal should identify the accepted input hashes, target identity, role,
placement route, controller owner, admitted and prohibited records, package
order and targets, expected binary/semantic delta, protected appearance data,
conflict assumptions, static checks, runtime checkpoints and rollback artifact.

After writing:

- reopen the final output path and bind its hash and length;
- compare exact signature/FormID changes with the proposal;
- check headers, masters, flags, ownership and record counts;
- inspect NPC_, PACK, FACT, RELA, QUST, DIAL/DLBR/INFO, CELL/WRLD, ACHR,
  NAVM/NAVI and their relevant references;
- verify VMAD/fragments, SEQ and FaceGen/FaceTint/voice paths;
- compare protected providers and bytes;
- inspect the installable archive for only runtime-required files.

Do not promote an undeclared record or protected-byte change as a warning. Static
verification proves artifact properties, not in-game behavior.

## Runtime checkpoints

For every NPC verify the clicked actor/base/plugin/FormID and winning provider,
single spawn, intended location, complete routine, door/cell transitions,
combat/alarm behavior, save/load, recovery and preserved appearance.

For a follower also test recruitment, dialogue close, follow through cell
transitions, wait, save/reload, trade/equip, combat, dismissal away from home,
return home and re-recruit. Test under the intended framework or record its
explicit exclusion.

For a merchant test service start/end, location/radius, barter visibility,
stock, gold, buy/sell filters, container ownership, wait, save/reload and reset.

For exterior changes test neighboring-cell travel, terrain, water, weather,
lighting, LOD, persistence, package pathing and intended load order. Keep
operator acceptance, visual review and game-runtime proof distinct.
