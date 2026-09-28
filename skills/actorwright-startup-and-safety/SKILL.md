---
name: actorwright-startup-and-safety
description: Establish workspace, protected-root, executable, and command authority before Actorwright-assisted Skyrim mod work.
license: GPL-3.0-only
---

# Startup and safety

Use this skill before a workspace-bound command, build, or game-facing change. A skill describes a workflow; it does not grant permission to read, write, install, or send anything.

## Establish the work boundary

- Identify the exact mod project and the files the user authorized. Keep product source/build work in the Actorwright checkout and game-facing work in the mod project. Do not mix their source trees, Git data, credentials, or outputs.
- Workspace-bound Actorwright commands require a K-local workspace. Set ACTORWRIGHT_WORKSPACE_ROOT to the intended absolute workspace before running them.
- Set ACTORWRIGHT_PROTECTED_ROOT explicitly for the game or subtree that must not be modified. It may be absolute or relative to the workspace root. A malformed setting is a refusal; correct it instead of bypassing the refusal.
- Treat the actual game installation, profile, and saves as protected unless the user explicitly authorizes a specific operation there. Prefer copied inputs and a project-local sandbox.
- Read whatever current project notes or manifests exist, but do not require a particular private workspace file layout. A documented path or old skill does not prove a file, tool, or provider currently exists.

## Identify and query the executable

The public source repository is source-only. Do not assume it contains an installed Actorwright executable or that a skill will install one. Use an executable the user has selected and authorized. Record its absolute path, version, and SHA-256 before relying on its results.

Set $actorwright to the user's verified absolute executable path, replace <command> with the actual command tokens, and quote a schema --command value containing spaces (for example, --command '<command>'). The examples below are templates, not literal commands.

Query that exact executable, not a different checkout or a guessed wrapper:

    & $actorwright version --protocol 2 --json
    & $actorwright capabilities --json
    & $actorwright capabilities --protocol 2 --json
    & $actorwright <command> --help
    & $actorwright schema export --protocol 2 --json --command <command>

Protocol 1 is the complete compatibility catalogue and is selected by omitting --protocol or specifying --protocol 1. Protocol 2 is a typed kernel: use it only for commands whose current capability entry says readiness is v2. Use the actual binary's help and exported schema; do not reuse old command counts, examples, or interface claims as current evidence.

A capability or successful query does not authorize a mutation. Check command effects and authorities, confirm the target path, and obtain any approval required for the proposed operation. If no verified executable is available, report that dependency instead of inventing output or silently building/installing the application.

## Continue

For a proposed game-facing change, use [change control](../actorwright-change-control/SKILL.md). For checks and their limits, use [gates and validation](../actorwright-gates-and-validation/SKILL.md). Preserve the evidence and package boundaries in [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md).