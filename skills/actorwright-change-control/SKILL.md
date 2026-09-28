---
name: actorwright-change-control
description: Plan, authorize, perform, and independently verify a scoped Skyrim mod change.
license: GPL-3.0-only
---

# Change control

Use this workflow for edits to plugins, meshes, textures, scripts, archives, or an installable mod package. Read [startup and safety](../actorwright-startup-and-safety/SKILL.md) first. A request to inspect or diagnose is not approval to write.

## Four deliberate passes

1. **Analyze.** Inspect only the authorized source copy. Record the target identity, current values or bytes, winning providers, and relevant dependencies. Keep originals unchanged.
2. **Propose.** List the exact files and fields that may change, the intended old-to-new values, expected sidecar effects, and falsifiable predictions for the output. Note what must remain unchanged.
3. **Write in a sandbox.** Use the current admitted toolchain and write to a new project-local output. Never perform an ad-hoc byte edit on an engine-loaded file. Do not broaden the file list when a tool reports an unrelated issue.
4. **Verify independently.** Re-open the written files with a separate reader or parser. Compare each result with the proposal, inspect the full output diff, and preserve the command, tool identity, exit status, and findings.

Before packaging, read [gates and validation](../actorwright-gates-and-validation/SKILL.md) and [evidence and packaging](../actorwright-evidence-and-packaging/SKILL.md). Static results do not replace the [runtime proof](../actorwright-runtime-proof/SKILL.md).

## Track fragile relationships

When a plugin record or filename changes, check every dependent file and reference. FormID compaction or renaming can affect FaceGeom and FaceTint filenames, plugin references, and archive paths. Script attachments may require matching PEX files; dialogue edits may require compiled fragments and SEQ files. Verify these relationships from the written package, not from the in-memory plan.

Keep the source, sandbox output, and final package distinguishable. Stage explicit paths only. Do not reset, clean, broadly stage, or overwrite unrelated work. Record a scoped exception only when the current project policy explicitly permits one; a convenient tool flag is not a waiver or approval.