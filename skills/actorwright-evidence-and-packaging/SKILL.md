---
name: actorwright-evidence-and-packaging
description: Keep mod-change evidence traceable and assemble a clean, reviewable installable package.
license: GPL-3.0-only
---

# Evidence and packaging

Use this skill for reports, hashes, candidate inventories, and release handoffs. Follow [change control](../actorwright-change-control/SKILL.md) and use [gates and validation](../actorwright-gates-and-validation/SKILL.md) before calling a package ready.

## Make evidence reproducible

For each meaningful check, record the target and version, exact input and output paths relative to the project, tool name/version/hash when available, command, timestamp, exit status, result, and limitations. Hash source and written candidate files with SHA-256; a hash identifies bytes but does not prove those bytes are correct.

Keep failures and corrections traceable. If a runner overwrites same-named reports, preserve the earlier result under a distinct run name before retrying. Do not edit an old report to make it agree with a later run; add a correction that identifies the original and the new evidence.

Avoid personal names, machine-specific absolute paths, protected-root paths, save data, credentials, and unrelated logs in public reports or archives. Use project-relative paths and neutral examples.

## Build a clean package

- Start from a fresh staging directory and include only files required by the documented install and runtime route.
- Include the plugin, assets, scripts, sidecars, and dependencies the mod actually needs. State external prerequisites and their versions; do not imply that a dependency is bundled when it is not.
- Exclude authoring source copies, tool caches, temporary renders, test harnesses, private reports, and unrelated project files from the install archive. If a license requires corresponding source or other materials, deliver them through a separate compliant source bundle or offer; do not treat the install-archive exclusion as permission to omit them.
- Before distribution, verify redistribution rights for every asset and bundled tool; include required notices and license texts. If rights are absent or unknown, omit the item and document a user-supplied prerequisite. Check that license-required source, build/install scripts, and any source offer are actually complete.
- Provide install, upgrade, and removal instructions. Explain what must be replaced or removed so stale files cannot win provider resolution.
- Generate a complete relative-path and SHA-256 inventory. Inspect the archive listing, then extract it to a fresh directory and compare that tree with the intended staging inventory.

A manifest or green static check supports only the claim it directly measures. Keep package hygiene, visual inspection, in-game behavior, and user acceptance distinct. See [runtime proof](../actorwright-runtime-proof/SKILL.md) for the runtime handoff.