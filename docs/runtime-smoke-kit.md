# Dual-game runtime smoke kit

Static verification is not runtime proof. This kit is the operator handoff for
the final release gate and writes evidence only under the K workspace.

## Intake and environment identity

1. Verify the pinned Exchange candidate with its release verifier and record
   the exact archive SHA-256 and package acceptance report. Use its Actorwright
   executable or wrapper for the commands below and discover its capabilities.
2. Before launching either game, use the consumer's profile-fingerprint tool in
   read-only mode. Save its evidence under the separate K-local workspace, for
   example `K:\ExampleWorkspace\reports\env-fingerprint-fo4-before.json`.
   Repeat for Skyrim SE. Set `ACTORWRIGHT_WORKSPACE_ROOT` to that workspace.
   Never place output under the configured protected game root.
3. Record the exact package plugin, target FormID, winning plugin provider,
   FaceGeom and FaceTint hashes, body/skin/headpart providers, and the known-good
   control NPC before judging appearance.

## Fallout 4 smoke

Use `tools/templates/runtime-smoke-fo4.txt` as the console batch template.
Replace its placeholders with the package's resolved target and a known-good
control NPC. Run it in the same lighting and capture face, neck, body, hands,
eyes, outfit, and one control-NPC frame. Do not accept a screenshot without the
control NPC in-frame.

## Skyrim Special Edition smoke

Use `tools/templates/runtime-smoke-sse.txt` with the resolved load-order FormID
and control NPC. Capture the same coverage, including hair/ears and any body
overlay or RaceMenu-driven surface. Record whether the target is the package's
winning provider before interpreting the image.

## Evidence acceptance

For each game, return a K-local JSON report named
`reports/runtime-smoke-<game>-<date>.json` containing:

- environment-fingerprint path and hash;
- package archive SHA-256 and plugin/FaceGeom/FaceTint provider hashes;
- target/control FormIDs and provider identities;
- screenshot paths for every required view;
- observed result, failures, and operator name/time.

Validate each completed report through the typed CLI before submitting it to
the release gate:

```powershell
actorwright runtime smoke verify --edition fallout4 `
  --runtime-report K:\ExampleWorkspace\reports\runtime-smoke-fallout4-<date>.json `
  --package-acceptance K:\ExampleWorkspace\reports\m8-package-acceptance-<stamp>.json `
  --json
```

Use `--edition skyrimse` for the Skyrim report. The command validates paths,
hashes, identity fields, required screenshot names, and common PNG/JPEG/GIF/BMP/WebP
image signatures; it deliberately does not judge pixels or claim that a game was
launched. Both verifiers also enforce K-local input containment, strict typed
identity fields, the 2 MiB report and 64 MiB screenshot bounds. The
independent Python verifier applies the same gates so a second verifier cannot
turn an arbitrary or oversized existing file into screenshot evidence.
Keep the resulting JSON as the machine-readable preflight record alongside the
operator evidence.

After both per-game reports pass, the typed CLI can run the combined gate in one
step:

```powershell
actorwright runtime smoke verify-all `
  --fallout4-report K:\ExampleWorkspace\reports\runtime-smoke-fallout4-<date>.json `
  --skyrimse-report K:\ExampleWorkspace\reports\runtime-smoke-skyrimse-<date>.json `
  --package-acceptance K:\ExampleWorkspace\reports\m8-package-acceptance-<stamp>.json `
  --json
```

The aggregate result contains the two nested typed results and is PASS only
when both reports pass. It remains an evidence-contract check; it does not
launch either game, inspect pixels, or bypass the runtime authority protocol.

The final gate is **PASS** only when both reports contain all required fields,
the target/provider identities match the package, and the screenshots show the
required coverage. Until then `runtime_release_claim` remains `false`.
