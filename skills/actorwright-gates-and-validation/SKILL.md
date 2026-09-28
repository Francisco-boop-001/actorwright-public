---
name: actorwright-gates-and-validation
description: Select and interpret current static checks for a Skyrim mod without overstating what they prove.
license: GPL-3.0-only
---

# Gates and validation

Use the checks actually available for the current project and tool version. Read [startup and safety](../actorwright-startup-and-safety/SKILL.md) to establish the executable and workspace, and [change control](../actorwright-change-control/SKILL.md) to keep changes scoped.

## Choose checks from current evidence

- Query the selected executable's capabilities and command help. Inspect the project's current tool manifests and check documentation, if present. Do not copy a historical gate list, command count, or path from an old project.
- Select checks that cover the changed artifact and its dependencies: plugin records, referenced assets, file formats, provider resolution, and packaging as applicable.
- Run checks against the written candidate, not an unsaved buffer or a different package. Record the exact command, executable identity, input hashes, exit code, and full result.
- If a check has known-good and known-bad fixtures, confirm it accepts and rejects the intended cases before relying on it. Do not claim fixture validation when no fixture was run.
- Preserve a failed result before rerunning when the runner would overwrite it. Compare the new output with the preserved failure.

## Interpret the result table

Read individual findings, not only the process exit code or summary. A SKIP, missing input, unavailable tool, or unresolved provider means that condition was not verified. If a required check skipped, call the run incomplete or inconclusive even if the aggregate command exits successfully. Read warnings and advisory findings; a non-blocking status does not make them irrelevant.

Use only severity and waiver rules documented by the current project. Never convert a hard stop into a pass or create a broad waiver. Record any permitted exception with the exact finding, artifact, rationale, and approving authority.

A static pass can establish that inspected files satisfy those checks. It cannot establish that the game renders the intended appearance, that gameplay works, or that a user has accepted the result. For those claims, follow [runtime proof](../actorwright-runtime-proof/SKILL.md). Preserve machine-readable results and package hashes as described in [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md).