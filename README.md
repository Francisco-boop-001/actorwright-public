# Actorwright

Actorwright is an AI-first toolkit for creating NPCs and followers, primarily for Skyrim Special Edition, with Fallout 4 capabilities too. You supply the character; your AI agent operates the tools; you get to argue about the eyebrows.

**A hobbyist project, for hobbyists. This is my first public GitHub release. Ever.** I have tried to make it presentable, but the rough corners are more like rough mountains, and even I can see them.

**Current release: source only. No application executables or native DLLs are included. Windows authoring requires a workspace on `K:` and explicit protected-game configuration.** Read the [setup limitations](#build-and-missing-runtime-inputs) before committing your afternoon.

## How this happened

I wanted to turn presets into NPCs. I fought the Creation Kit and the surrounding tools, then realized an AI could operate a toolchain for me. This was **before the computer-use era**. The laborious part was not making one NPC; it was teaching the AI how to make them.

The result is a workflow I use for presets, reference images, and characters described from imagination. For images, bring both front and side views when possible. AI-led creation is not deterministic: sometimes the first attempt is great; sometimes your new companion needs a serious conversation with its maker. Tell the agent what is wrong, ask for focused fixes, and inspect the next result. That describes the agent workflow, not a claim that every CLI operation is random.

Actorwright grew from [MANOLOV02's FO4_NPC_Manager](https://github.com/MANOLOV02/FO4_NPC_Manager), a great human-facing tool that you should check out. Its GPL baseline gave me a starting point. I kidnapped it, twisted its arm, administered involuntary plastic surgery, and transplanted a few organs. The patient emerged oriented toward AI first, second, and third. The joke is affectionate; the credit and license are serious. See [NOTICE](NOTICE) and [the license](LICENSE).

## What this release is

Actorwright is an AI-first CLI for NPC-authoring workflows, with a Windows desktop GUI too. **There is a GUI. My recommendation: do not use it.** My hobbyist instincts demanded windows and buttons, not because they were necessary. The intended operator is an AI agent using the CLI; the human supplies direction and judges the results. Skyrim is the immediate target, and Fallout 4 capabilities are also present. The current catalogue lists 142 commands, but a listed command is not proof that an end-to-end game workflow has been verified.

This source snapshot is version `1.0.0-preview.281`, source line `preview.281-public`, derived from Preview.280. It is the first public source release, not a verified application release. I chose to publish source only; application packaging and binary-release gates remain unfinished. The previous Preview.280 release and its private history remain separate; neither that release's results nor older private test results certify this snapshot. The [public-release gate](docs/security/public-release-gate.md) explains the evidence required for a later binary release.

## How I use it for mod creation

The [mod-creation skills](skills/README.md) and [prompts](prompts/README.md) show my working approach. Give the agent your NPC profile and biography, a reference `.wav`, the BodySlide preset/body route you want, and the face preset you created or downloaded. Include the real project files, intended workspace, enabled dependencies, and reference images when you have them. Front and side views are especially useful, but lighting, camera angle, and missing views affect what can be inferred. A written concept can guide an attempt, not guarantee a matching face.

The [image-to-preset skill](skills/actorwright-image-to-preset/SKILL.md) calls for comparison panels. **Insist on them.** For likeness work, ask for a side-by-side comparison of the original reference and the current actual-geometry render at comparable framing. A score, measurement, or isolated feature does not overrule a whole-face visual mismatch. Keep the human visual verdict separate from static checks and preview results.

### The missing-export trap

My workflow includes a “Step Zero”: check that the preset actually works in game before investing in the rest of the NPC. Agents sometimes turn that into “you must hand me a manual preset export or I cannot continue.” Do not accept that conclusion without investigation. Actorwright has a supported FaceGen bake route; with its required preset, providers and dependencies, the agent should investigate baking the needed assets itself. See the [CLI contract](docs/cli-contract.md).

My practical instruction is: “Prefer V1 commands. Break the job into small operations. If an export is missing, inspect the supported bake route before declaring the task impossible.” A missing manual export is not proof of a missing capability. Equally, this is not permission to fabricate an in-game check, ignore a genuine dependency failure, or claim Actorwright can perform every conceivable operation. If runtime proof is deferred, label it as deferred. Confidence is not a file format.

### V1, V2, and teaching the agent to stop sulking

I prefer **V1 / Protocol 1** for the full compatibility catalogue (`--protocol 1`, or omit the protocol flag). V2 is meant to make AI interaction better. It can—when the particular route works. Protocol 2 requires `--protocol 2 --json` and is callable only for commands currently marked `readiness: v2`. When a Protocol 2 path cannot cover a task, split it into smaller supported Protocol 1 operations; do not bypass a refusal or safety boundary. Always discover capabilities and help from the exact executable being used. The source-only checkout does not contain that executable. The [agent CLI cookbook](docs/cli-llm-cookbook.md) explains discovery and both protocols.

### Give the NPC something to say

Actorwright can author voice/dialogue workflows using an external speech backend. I use the [Mantella XTTS API server on Nexus](https://www.nexusmods.com/skyrimspecialedition/mods/113445). Give the agent the character biography and voice reference `.wav`; it still needs to generate or import the lines and bind them to the NPC correctly. A WAV is an ingredient, not an entire talking follower.

Use another voice tool if you prefer, provided its output can follow the supported import/dialogue route; arbitrary servers are not automatically compatible. See [XTTS setup](docs/voice-server-setup.md) and the [voice/dialogue cookbook](docs/cli-llm-cookbook.md#author-a-hash-bound-custom-voice-and-dialogue-overlay).

## Workspace and protected game

Workspace-bound authoring commands require an ordinary writable workspace on `K:`, separate from the game installation. Set `ACTORWRIGHT_WORKSPACE_ROOT` to its absolute path; if omitted, the current directory is used. Set `ACTORWRIGHT_PROTECTED_ROOT` to the game installation that must remain read-only. An absolute value is simplest; a relative value resolves against the admitted workspace. The neutral example default does not protect your installation. Invalid or blank protected-root settings are refused. This setting never authorizes deployment or writes into the game.

For single-file application packages, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to an ordinary writable directory beneath the authoring workspace, outside the protected game root. Bundled native libraries extract there.

A virtual `K:` mapping is not a verified installation route. NTFS reparse/junction coverage and clean-clone CI have not passed for this source snapshot.

## Build and missing runtime inputs

**Put your AI to work here.** Point it at this section and the linked prerequisite documents. Tell it to inspect your setup, identify what is missing, and help you obtain and install the required tools from their official sources, under their licenses. It can save you a great deal of dependency archaeology. This is exactly the sort of tedious work we recruited the machine for.

Have it explain what it will install and where, verify the pinned versions and hashes, and report anything it cannot satisfy. “Installed something with a similar name” is not a successful prerequisite check. Neither is declaring victory over a missing DLL through sheer enthusiasm.

This is source only: no Actorwright executable and no native DLLs are included. A clean clone is not currently a clone-and-run build. The application still expects these six app-local files under `runtime/reference-preset/`, with exact versions, lengths, and SHA-256 values recorded in [runtime-asset-manifest.json](runtime/reference-preset/runtime-asset-manifest.json):

- `libmediapipe.dll`
- `opencv_world3410.dll`
- `concrt140.dll`
- `msvcp140.dll`
- `vcruntime140.dll`
- `vcruntime140_1.dll`

MediaPipe and OpenCV are permissively licensed, but their candidate runtime outputs are not present in this source snapshot. Their source, build provenance, and limits are documented in [source-only publication](docs/source-only-publication.md) and [third-party components](docs/third-party-licenses.md). The four Microsoft runtime files are omitted because this public distribution does not provide a cleared redistribution route for them. Obtain them from [Microsoft](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) and install/use them locally under its terms. Private use is not a blanket license exemption. The existing loader expects the exact app-local inventory, so installing a system redistributable alone does not satisfy it. Do not copy files into the project just because they happen to be installed on your machine.

### Set it up locally

There is no ready-to-install application release yet. These are the source-build steps once the prerequisites are available; they are **not a verified fresh-clone recipe**. In particular, a local MediaPipe/OpenCV rebuild may not reproduce the manifest's exact hashes. If you cannot supply the admitted files, that is a real outstanding build prerequisite—not something to fix by deleting a hash check.

1. **Install the tools:** Windows x64, Git, PowerShell 7, Python with `pytest`, and Microsoft's **.NET SDK 10.0.301** (see [the pinned SDK manifest](tools/manifests/dotnet-sdk-10.0.301-win-x64.json)). `global.json` disables SDK roll-forward. The repository bootstrap only validates/extracts an already supplied SDK archive; it does not download one.
2. **Clone the source** into a separate product directory on `K:`. The paths below are examples; do not overwrite an existing checkout:

   ```powershell
   git clone https://github.com/Francisco-boop-001/actorwright-public.git K:\ActorwrightSource
   Set-Location K:\ActorwrightSource
   dotnet --version  # Must report 10.0.301.
   ```

3. **Supply prerequisites before building.** Put the six admitted DLLs in `runtime/reference-preset/` and check their versions, lengths and SHA-256 against the manifest. Obtain the pinned Blender, PyNifly and Texconv installations described in [external rendering prerequisites](docs/external-rendering-prerequisites.md) and [tool provisioning](tools/external/README.md). Missing tools are not successful tests. Read [build instructions](docs/build.md) for external test inputs and package gates.
4. **Run the canonical build** from the repository root in PowerShell 7:

   ```powershell
   .\tools\build\build.ps1
   ```

   This runs checks as well as compilation. A failed prerequisite or gate is not a successful installation. Repository Actions are disabled for this source-only publication; that is not a CI pass. See [source-only publication](docs/source-only-publication.md).
5. **After a successful build, configure a separate mod workspace.** Some legacy helpers require `K:\ExampleWorkspace`, so use that location for those workflows. **Replace the example game path below with your actual installation before running it.** These environment variables apply to the current PowerShell session:

   ```powershell
   New-Item -ItemType Directory -Force K:\ExampleWorkspace | Out-Null
   $env:ACTORWRIGHT_WORKSPACE_ROOT = 'K:\ExampleWorkspace'
   $env:ACTORWRIGHT_PROTECTED_ROOT = 'D:\Games\Skyrim Special Edition'
   $Actorwright = 'K:\ActorwrightSource\src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe'
   & $Actorwright version --protocol 1 --json
   & $Actorwright capabilities --protocol 1 --json
   ```

   Give your agent that exact executable path. Install the [skills](skills/README.md#install-and-use) in the mod workspace and supply the [creation or audit prompt](prompts/README.md). Skills cover presets, followers, patching, validation, packaging and runtime review; they neither supply an executable nor grant permission to write into the game. Game assets, appearance providers and any voice server remain separate prerequisites. Do not drop this source repository into your mod manager as if it were an NPC mod.

## Appearance providers

The private default NPC appearance bundle is not included. Creating an NPC requires a workspace provider manifest plus its template, mesh, texture, and dependency files, obtained under their own licenses. The desktop app checks the selected provider's files and hashes at selection and build time. A request that assumes the absent bundled provider is refused. Synthetic test meshes are test inputs, not game-ready heads.

## License and attribution

Actorwright source is GPL-3.0-only. Third-party parts have their own licenses, notices, and source obligations; the presence of a license declaration is not a substitute for satisfying them. If a dependency cannot be redistributed, obtain it from its official source and follow its stated installation requirements. Details are in [licensing](docs/licenses.md), [third-party components](docs/third-party-licenses.md), [LICENSE](LICENSE), [NOTICE](NOTICE), and the component terms under [licenses](licenses/).

Internal `NpcManager.*` names and protocol/schema identifiers remain for compatibility.

## Reports and help

My NPC creation prompt produces reports. **I would be grateful if you shared those**, whether the NPC turned out beautifully or acquired an exciting new anatomical theory. For help, bugs or useful workflow reports, open a [GitHub issue](https://github.com/Francisco-boop-001/actorwright-public/issues) with a sanitized report: command, version, expected and observed result, and the relevant error excerpt. Remove personal names, absolute machine paths, credentials, saves, and private or copyrighted game/mod assets. Please do not attach a full profile or game files.

## What is and is not verified

- Static file checks and off-engine renders do not prove Skyrim or Fallout 4 appearance, behavior, save compatibility, or human acceptance.
- Protocol 2 has a known root-admission inconsistency. Do not assume every protocol path applies identical workspace checks; explicit protected-root configuration remains necessary.
- Whole-file buffering and failure-evidence coverage still have gaps. They are tracked limitations, not claims of complete modernization or runtime certification.
- Fresh source, package, privacy, license, clean-clone, and NTFS evidence is still required before any binary release. This README describes the source snapshot, not a candidate-specific pass.

## Thanks, and allocation of blame

Finally, a warm thank-you to Rangroo and tyler.maister: their CHIM AI mod inspired me to return to Skyrim and try this project. If Actorwright does not work, blame them first. Affectionately. The bugs are mine; the inspiration is theirs. See [Dwemer Dynamics' CHIM](https://github.com/Dwemer-Dynamics/CHIM).
