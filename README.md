# Actorwright

**Source-only preview. No application executables or native DLLs are distributed.** See [local build prerequisites and verification limits](docs/source-only-publication.md).

**Windows preview: authoring workflows require a workspace on `K:`. Configure `ACTORWRIGHT_PROTECTED_ROOT` for your own game installation before use. The example default does not protect your game installation.**

Actorwright provides a CLI and Windows desktop application for NPC authoring, with Skyrim SE as the immediate target and Fallout 4 capabilities also present. Its command catalogue contains 142 commands. Availability of a command does not establish that a complete game workflow has been verified.

Current source line: `1.0.0-preview.281` / `preview.281-public`. Preview.281 is the current source identity; that identity does not record candidate gate results or authorize publication. This README and the [public-release gate](docs/security/public-release-gate.md) describe usage and policy criteria, not a candidate-specific verification result. The owner selected source-only publication; application packaging and binary-release gates remain unfinished and are not claimed to have passed. This source is derived from Preview.280; the prior private release and its history remain separate.

## Mod-creation agent skills

The [Actorwright skills collection](skills/README.md) contains anonymized workflows
for presets, NPC/follower authoring, patching, validation, packaging, and runtime
review. Install them in your mod workspace; their instructions do not supply an
Actorwright executable or grant game-write permission.

## The prompts I use

My [NPC creation and audit/debugging prompts](prompts/README.md) are included for
reuse, with generic path placeholders and notes on adapting my workflow.

## Workspace and game boundary

Set `ACTORWRIGHT_WORKSPACE_ROOT` to an ordinary writable authoring workspace on `K:`. If omitted, the current directory is used. Keep the game installation separate from this workspace.

Set `ACTORWRIGHT_PROTECTED_ROOT` to the absolute path of the game installation that must remain read-only. A relative value resolves against the admitted workspace; an invalid or blank setting is refused. This setting does not authorize deployment into a game installation.

For single-file packages, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to an ordinary writable directory below the authoring workspace before launch. Bundled native libraries extract there. Do not place the extraction directory inside the protected game root.

A virtual `K:` mapping is not yet a verified installation route. NTFS junction/reparse-point coverage and clean-clone CI must pass for the exact public candidate before publication.

## Build and dependencies

First supply the excluded local native inputs described in [source-only publication](docs/source-only-publication.md). A bare clone does not satisfy the native-runtime inventory. Then use the pinned SDK and canonical build from the repository root:

```powershell
.\tools\build\build.ps1
```

Renderer-dependent checks require the pinned external tools described in [external rendering prerequisites](docs/external-rendering-prerequisites.md) and [tool provisioning](tools/external/README.md). Missing dependencies are not successful tests.

Third-party components are included only where their licenses permit redistribution and the corresponding notice/source obligations are satisfied. Otherwise, the documentation must direct users to the official provider and required installation location. See [licensing](docs/licenses.md) and [third-party components](docs/third-party-licenses.md).

## Appearance providers

The public package does not include the private default NPC appearance bundle. Creating an NPC requires a workspace provider manifest and its referenced template, mesh, texture, and dependency files, obtained under their own licenses. In the desktop application, select that manifest before creating an NPC; its files and hashes are checked during selection and again at build time. Existing requests that rely on the missing bundled provider are refused explicitly. Synthetic test meshes are verification inputs, not game-ready appearance assets.

## Verification limits

- Static artifact checks and off-engine renders do not prove Skyrim or Fallout 4 behavior, appearance, save compatibility, or human visual acceptance.
- Protocol 2 has a known root-admission inconsistency; do not assume every protocol path enforces identical workspace admission. Protected-root configuration remains necessary.
- Some whole-file buffering and failure-evidence coverage gaps remain. These are tracked limitations, not claims of complete modernization or runtime certification.
- Historical private release results do not certify this modified source snapshot. The public package needs fresh source, package, privacy, license, and NTFS evidence.

## License

Actorwright is GPL-3.0-only. See [LICENSE](LICENSE), [NOTICE](NOTICE), and the component-specific terms under `licenses/`. Internal `NpcManager.*` names and protocol/schema identifiers remain for compatibility.
