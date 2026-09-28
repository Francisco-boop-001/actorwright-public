# The prompts I use with Actorwright

These are the prompts I use to create NPCs and to audit, debug and repair their
mods with an AI coding agent and Actorwright. I am sharing my working prompts so
others can reuse and adapt the workflow. Machine-specific paths have been replaced
with placeholders; the instructions otherwise preserve my originals.

- [Create an NPC/follower](create-npc.txt): start from a character concept, bio,
  preset and voice references, then implement and verify a complete follower.
- [Preventive NPC audit and repair](preventive-npc-audit-and-repair.txt): inspect
  an existing candidate, repair evidenced defects and prepare focused in-game tests.
  This is an offline audit prompt, not a substitute for runtime testing.

## Use

Copy the appropriate prompt into your agent conversation and supply your project,
character references and exact candidate files. Replace the bracketed placeholders.
Use a writable authoring workspace on `K:` for `[AUTHORING_WORKSPACE_ROOT]` and your
separate, read-only game installation for `[PROTECTED_GAME_ROOT]`; configure
`ACTORWRIGHT_WORKSPACE_ROOT` and `ACTORWRIGHT_PROTECTED_ROOT` accordingly.

These are opinionated prompts: exterior placement and three alternatives per
recurring follower response are my creation requirements, not universal Actorwright
requirements. Adapt them deliberately to your project before running the prompt.
References to Step Zero, Tier-0 gates, visual protocols and workspace policies refer
to the author's configured workflow. Supply applicable local policies or have the
agent identify what is missing; do not assume those facilities are bundled or claim
that unavailable checks passed.

The prompts do not install Actorwright, game assets, voice tools or dependencies,
and do not authorize changes to a live game. See the [build prerequisites and
verification limits](../docs/source-only-publication.md) and the complementary
[mod-creation skills](../skills/README.md). Static checks do not prove in-game
appearance, behavior or save compatibility.

Shared under the repository's [GPL-3.0-only license](../LICENSE).
