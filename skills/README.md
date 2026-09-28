# Actorwright mod-creation skills

These agent skills adapt the project's mod-authoring playbooks for public use.
They guide work in a separate Skyrim mod workspace; they do not install
Actorwright, provide game assets, or grant permission to modify a game.

**This repository is source only.** The skills require an explicitly selected,
verified Actorwright executable for application commands. They do not make the
public snapshot runnable or satisfy its unfinished binary-release gates. See
[build prerequisites](../docs/source-only-publication.md).

## Install and use

Copy the `actorwright-*` folders into your mod workspace's `.agents/skills/`
for agents that support that convention. Keep the folders together so relative
skill links resolve, and keep this collection's `LICENSE` with redistributed
copies. Inspect existing folders before replacing any. For another agent, use
its documented skill directory; the `SKILL.md` files remain ordinary Markdown.
Do not install these mod-workflow skills into the Actorwright product checkout
merely to develop the application.

Configure your own authoring workspace, protected game root, executable, and
tool/provider locations. Current workspace-bound Actorwright commands require
`K:`. Set `ACTORWRIGHT_WORKSPACE_ROOT` and `ACTORWRIGHT_PROTECTED_ROOT` explicitly;
a private default game path does not protect your installation. The startup
skill explains exact-binary discovery and the two protocols.

Start with `$actorwright-startup-and-safety`, then invoke the skill matching
your request, for example `$actorwright-preset-pipeline` or
`$actorwright-follower-kit-authoring`. Agents that do not support `$` invocation
can be directed to read the corresponding `SKILL.md`.

## Choose a skill

| Skill | Use it for |
|---|---|
| [Startup and safety](actorwright-startup-and-safety/SKILL.md) | Workspace boundaries, executable identity, providers and command discovery. |
| [Image to preset](actorwright-image-to-preset/SKILL.md) | Reference-guided RaceMenu face work and actual-geometry likeness review. |
| [Image to BodySlide](actorwright-image-to-bodyslide/SKILL.md) | BodySlide/OBody presets using the selected body ecosystem and real morph inputs. |
| [Preset pipeline](actorwright-preset-pipeline/SKILL.md) | Taking an accepted preset and its provider assets into NPC authoring. |
| [Complete NPC authoring](actorwright-complete-npc-authoring/SKILL.md) | Placement, routines, combat, services and their conflict surface. |
| [Follower kit](actorwright-follower-kit-authoring/SKILL.md) | Follower lifecycle, equipment, dialogue and voice closure. |
| [NPC transformation](actorwright-npc-transformation/SKILL.md) | Alternate appearances and abilities on the same actor. |
| [Mutagen patchers](actorwright-mutagen-patchers/SKILL.md) | Bounded, authorized Skyrim plugin record edits. |
| [Change control](actorwright-change-control/SKILL.md) | Preserving inputs, approving scoped changes and verifying written outputs. |
| [Gates and validation](actorwright-gates-and-validation/SKILL.md) | Selecting meaningful static checks and interpreting refusals or skipped checks. |
| [Evidence and packaging](actorwright-evidence-and-packaging/SKILL.md) | Traceable evidence and clean mod-package handoffs. |
| [Runtime proof](actorwright-runtime-proof/SKILL.md) | Target-specific in-game behavior, appearance and operator acceptance. |
| [Debugging playbook](actorwright-debugging-playbook/SKILL.md) | Diagnosing failures from actual artifacts and controlled comparisons. |

## Scope and provenance

This is an adaptation of the owner's workspace guidance, licensed under
GPL-3.0-only with Actorwright. Private project names, machine paths, historical
case records, screenshots, assets, and unpublished helper implementations are
excluded. Generic third-party agent plugins are not bundled or relicensed.
Necessary references are included beside their skills; tools and game/provider
content must be acquired separately under their own licenses.

An unavailable private helper is not replaced by a pretend command. Use the
current executable's capabilities/help/schema and qualified local tools; report
an unsupported step rather than inventing evidence. User authorization is
specific to the requested work and does not come from loading a skill.

These skills are workflow guidance, not a certificate of mod compatibility.
Static checks, preview renders, packaging, in-game behavior, and human visual
acceptance are distinct. The final skill review checks format, links, privacy,
and consistency with the documented application interface; it does not execute
a game workflow or supply runtime proof.
