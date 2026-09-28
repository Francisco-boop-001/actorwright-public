# External rendering prerequisites

Actorwright's binary package does not bundle Blender, PyNifly, Texconv, or
their licenses. These are user-installed, third-party authorities. The
third-party binaries are not bundled in any release artifact. Product code
and the release handoff currently admit the following exact private preview
profile:

| Authority | Official project | Supported identity | License ownership | Product evidence |
| --- | --- | --- | --- | --- |
| Blender | [Blender previous versions](https://www.blender.org/download/previous-versions/) | Blender 4.5.1 Windows x64 portable; `blender.exe` SHA-256 `B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794` | Blender Foundation / Blender Institute; GNU General Public License, version 3 or later, as supplied by the upstream distribution | `src/NpcManager.Cli/NpcVisualPreviewCliComposition.cs`, `src/NpcManager.Rendering/BlenderNpcVisualPreviewRenderer.cs` |
| PyNifly | [BadDogSkyrim/PyNifly](https://github.com/BadDogSkyrim/PyNifly) | PyNifly 27.4.0; admitted archive SHA-256 `296427A5E30F151223700298A7875DF2C0A61B5F422818FC7002B4F4D8DDAEE7`; admitted profile fingerprint `A909978665FBF6927F53F97726D73F14EF69B62E14D1F29DBF5F8FC65089D9A3` | PyNifly upstream authors and repository license; obtain and retain the upstream license with the local installation | `src/NpcManager.Cli/FaceGeomHairRegionsPreviewCliComposition.cs`, `runtime/rendering/npc-preview-profile-manifest.json`, `src/NpcManager.Rendering/BlenderPreviewNifExporter.cs` |
| Texconv (supporting texture boundary) | [Microsoft DirectXTex releases](https://github.com/microsoft/DirectXTex/releases) | DirectXTex Texconv 2026.5.7; `texconv.exe` SHA-256 `DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06` | Microsoft / DirectXTex upstream license and notices | `src/NpcManager.Cli/NpcVisualPreviewCliComposition.cs`, `src/NpcManager.Infrastructure/PreviewDependencyPreflightService.cs` |

The PyNifly version is the product's supported adapter identity; it is not a
claim that the upstream repository is owned by Actorwright. Before use, the
operator must review the license shipped by each upstream project. Actorwright
does not copy, mirror, or redistribute these tools.

## Expected local layout

The paths below are relative to the K-local workspace root identified by
`ACTORWRIGHT_WORKSPACE_ROOT` (or the current working directory when that is
the workspace root):

Canonical relative spellings used by the release inventory are
`blender-4.5.1-windows-x64/blender-4.5.1-windows-x64/blender.exe` and
`blender-4.5.1-pynifly-profile/scripts/addons/io_scene_nifly`.

```text
<workspace>/tools/external/
  blender-4.5.1-windows-x64/
    blender-4.5.1-windows-x64/blender.exe
  blender-4.5.1-pynifly-profile/
    config/
    data/
    scripts/addons/io_scene_nifly/
  directxtex-texconv-2026.5.7/texconv.exe
```

The Blender process receives these profile variables so that it cannot fall
back to an unrelated user profile:

```text
BLENDER_USER_CONFIG=<workspace>/tools/external/blender-4.5.1-pynifly-profile/config
BLENDER_USER_SCRIPTS=<workspace>/tools/external/blender-4.5.1-pynifly-profile/scripts
BLENDER_USER_DATA=<workspace>/tools/external/blender-4.5.1-pynifly-profile/data
```

The profile manifest is an exact path, length, and SHA-256 inventory. The
renderer separately checks the PyNifly addon inventory under
`scripts/addons/io_scene_nifly`; an absent, extra, reparse, or changed file is
a refusal. No environment variable may redirect the renderer outside the
K-local workspace policy.

## Status vocabulary

The following statuses are deliberately separate:

- **absent** — the expected executable, archive, profile directory, profile
  manifest, or exact file inventory is missing. The command refuses and no
  render authority is established.
- **admitted** — the path is ordinary, within the workspace, and its exact
  executable/profile/archive hashes and inventory match the product pins.
  Admission is dependency evidence only; it is not a rendered image.
- **executed** — the admitted Blender/PyNifly process completed the requested
  operation and returned structured status bound to the exact input/output
  hashes, dimensions, and renderer identity. Only this status supports an
  off-engine render artifact.

The package verifier checks the protocol and dependency contract but does not
pretend that third-party tools executed. A release or consumer handoff must
report absent/admitted/executed status from the actual workspace. Even an
executed off-engine render leaves `humanVisualAcceptance` and
`gameRuntimeVerification` separate and does not establish promotion authority.
