# FaceGeom HairTint regions — CLI and LLM cookbook

Use the Actorwright executable or PowerShell wrapper from the pinned Exchange
candidate selected for the consumer workspace. Query that exact binary for
version, capabilities and each command schema before invoking the workflow.
The paths below are placeholders under a separate K-local sandbox, not product
repository inputs; set ACTORWRIGHT_WORKSPACE_ROOT to that sandbox.

The workflow is always:

1. `facegen hair-regions analyze`
2. complete the generated request by explicitly assigning every region
   `primary`, `accent`, or `preserve`
3. `facegen hair-regions propose`
4. `facegen hair-regions preview`
5. human inspection and explicit acceptance
6. `facegen hair-regions apply`
7. `facegen hair-regions verify`

Every input JSON and NIF is bound by SHA-256. Use new K-local output paths and
never overwrite the source. Primary and Accent must be distinct canonical
`#RRGGBB` colors, at least one physical shader group must receive each color,
and shared physical tint fields cannot receive conflicting assignments. Never
infer a role from an actor, shape, hair, brow, or lash name.

The writer changes only authorized 12-byte HairTint float envelopes. It does
not re-export the NIF or edit a plugin, topology, geometry, skinning, shader
ownership, texture routing, or unrelated bytes. UBE FaceGeom tint authoring is
admitted when the NIF exposes supported structural HairTint shapes; full UBE
MDNR/OBody NPC preview composition remains unsupported.

Every preview must be read as **Off-engine HairTint preview — Skyrim runtime
remains authoritative**. A successful preview or byte verification is not a
visual or Skyrim runtime PASS.

For exact options and JSON schemas, run:

```text
actorwright facegen hair-regions analyze --help
actorwright facegen hair-regions propose --help
actorwright facegen hair-regions preview --help
actorwright facegen hair-regions apply --help
actorwright facegen hair-regions verify --help
actorwright schema export --command "facegen hair-regions analyze" --output <new.schema.json> --json
```
## Detailed phase contract

This contract is for deterministic automation. All paths are absolute,
ordinary, non-reparse paths below the admitted `ACTORWRIGHT_WORKSPACE_ROOT`.
Use a separate K-local mod workspace. Never target the
live game root. Every JSON input is read exactly once, SHA-256 checked when the
CLI accepts an expected hash, strict-parsed, and rebound to its canonical
bytes before a phase begins.

JSON property names are case-sensitive camelCase. Duplicate and unknown
properties are forbidden recursively. `FaceGeomHairRegionRole` is closed to
the exact lowercase strings `preserve`, `primary`, and `accent`; integers,
PascalCase, and other strings are invalid.

## Five-phase command sequence

```powershell
actorwright facegen hair-regions analyze `
  --source K:\ExampleWorkspace\work\00000800.nif `
  --expected-source-sha256 <SOURCE_SHA256> `
  --analysis K:\ExampleWorkspace\work\hair.analysis.json `
  --assignment-template K:\ExampleWorkspace\work\hair.request.json `
  --json

# Edit only primaryColor, accentColor, and every assignment role in the
# generated canonical request. Preserve property order, spelling, whitespace,
# and all other values. Recompute the exact file SHA-256.

actorwright facegen hair-regions propose `
  --analysis K:\ExampleWorkspace\work\hair.analysis.json `
  --analysis-sha256 <ANALYSIS_JSON_SHA256> `
  --request K:\ExampleWorkspace\work\hair.request.json `
  --request-sha256 <REQUEST_JSON_SHA256> `
  --proposal K:\ExampleWorkspace\work\hair.proposal.json `
  --json

actorwright facegen hair-regions preview `
  --request K:\ExampleWorkspace\work\hair.request.json `
  --request-sha256 <REQUEST_JSON_SHA256> `
  --proposal K:\ExampleWorkspace\work\hair.proposal.json `
  --proposal-sha256 <PROPOSAL_JSON_SHA256> `
  --intake K:\ExampleWorkspace\work\reviewed-intake.json `
  --output-root K:\ExampleWorkspace\work\hair-preview `
  --json

actorwright facegen hair-regions apply `
  --request K:\ExampleWorkspace\work\hair.request.json `
  --request-sha256 <REQUEST_JSON_SHA256> `
  --proposal K:\ExampleWorkspace\work\hair.proposal.json `
  --proposal-sha256 <PROPOSAL_JSON_SHA256> `
  --output K:\ExampleWorkspace\work\hair.request.output.nif `
  --manifest K:\ExampleWorkspace\work\hair.request.output.manifest.json `
  --json

actorwright facegen hair-regions verify `
  --request K:\ExampleWorkspace\work\hair.request.json `
  --request-sha256 <REQUEST_JSON_SHA256> `
  --proposal K:\ExampleWorkspace\work\hair.proposal.json `
  --proposal-sha256 <PROPOSAL_JSON_SHA256> `
  --output K:\ExampleWorkspace\work\hair.request.output.nif `
  --manifest K:\ExampleWorkspace\work\hair.request.output.manifest.json `
  --json
```

`analyze` deterministically suggests `<assignment-template-stem>.output.nif`
and `<assignment-template-stem>.output.manifest.json` beside the assignment
template. `apply` and `verify` require the command-line paths to equal the
request and proposal paths exactly.

For an LLM editing the generated request:

1. Change only `primaryColor`, `accentColor`, and assignment `role` values.
2. Use distinct uppercase canonical `#RRGGBB` colors.
3. Classify every structural ID exactly once; never infer a role from a
   display name such as hair, brow, lash, or highlight.
4. Give every row in one `sharedShaderGroupId` the same role.
5. Assign at least one physical shader group to `primary` and one to `accent`;
   leave every unselected region as `preserve`.
6. Keep the generated source, analysis hash, output, and manifest authorities
   unchanged, then recompute the exact request-file SHA-256.
7. Run `propose`, inspect its authorized envelopes and changed-byte offsets,
   and run `preview` before `apply`.

The production preview renders the exact proposed bytes through the admitted
K-local Blender/PyNifly boundary. It publishes the complete output directory
only after validating the staged FaceGeom, proposal, reviewed intake, semantic
load order, source texture providers, renderer/tool hashes, exactly one
detected face, detected landmarks, 31 semantic anchors, structural IDs, and
nonempty image/mask pixels. It does not recolor or repair the candidate merely
to produce an attractive image.

Every preview visibly carries:

`Off-engine HairTint preview — Skyrim runtime remains authoritative`

The response keeps `visualAuthority: false` and `runtimeAuthority: false`.
The user, not the renderer, decides whether the face and accent regions look
correct. Full UBE MDNR/OBody NPC composition remains unsupported, but the
fixed-width FaceGeom tint transaction itself remains available for any
structurally admitted UBE FaceGeom.

## Golden success envelope

Paths and hashes below are tokens; replace each token consistently. Field
names, nulls, booleans, verdicts, and diagnostic codes are exact.

```json
{
  "command": "facegen hair-regions analyze",
  "phaseVerdict": "PASS",
  "artifacts": [
    {
      "role": "analysis",
      "structuralId": null,
      "path": "<ANALYSIS_PATH>",
      "sha256": "<ANALYSIS_SHA256>",
      "byteLength": 1,
      "nonEmptyPixelCount": null
    },
    {
      "role": "assignmentTemplate",
      "structuralId": null,
      "path": "<REQUEST_PATH>",
      "sha256": "<REQUEST_SHA256>",
      "byteLength": 1,
      "nonEmptyPixelCount": null
    },
    {
      "role": "suggestedOutput",
      "structuralId": null,
      "path": "<REQUEST_STEM>.output.nif",
      "sha256": null,
      "byteLength": 0,
      "nonEmptyPixelCount": null
    },
    {
      "role": "suggestedManifest",
      "structuralId": null,
      "path": "<REQUEST_STEM>.output.manifest.json",
      "sha256": null,
      "byteLength": 0,
      "nonEmptyPixelCount": null
    }
  ],
  "diagnostics": [
    {
      "code": "facegeom-hair-regions-analyzed",
      "severity": "info",
      "message": "Canonical analysis and Preserve-default assignment template were written without overwrite."
    }
  ],
  "previewEvidence": null,
  "visualAuthority": false,
  "runtimeAuthority": false
}
```

Success exits with `0`. The proposal response contains one `proposal`
artifact. Apply and verify contain exact `request`, `proposal`, `output`, and
`manifest` artifact rows, each with a path, hash, and byte length.

## Golden failure envelope

Every JSON failure keeps the same top-level shape:

```json
{
  "command": "<EXACT_COMMAND>",
  "phaseVerdict": "REFUSED",
  "artifacts": [],
  "diagnostics": [
    {
      "code": "<STABLE_CODE>",
      "severity": "error",
      "message": "<DIAGNOSTIC_MESSAGE>"
    }
  ],
  "previewEvidence": null,
  "visualAuthority": false,
  "runtimeAuthority": false
}
```

Use these golden substitutions:

| Failure | Stable code | Exact/representative message | Exit |
|---|---|---|---:|
| Duplicate property | `facegeom-hair-regions-document-invalid` | `Duplicate JSON property 'schema' is forbidden.` | 4 |
| Unknown property | `facegeom-hair-regions-document-invalid` | `The exact JSON file is invalid.` | 4 |
| Numeric enum | `facegeom-hair-regions-document-invalid` | `The exact JSON file is invalid.` | 4 |
| PascalCase/unknown enum | `facegeom-hair-regions-document-invalid` | `The exact JSON file is invalid.` | 4 |
| Wrong expected JSON hash | `facegeom-hair-regions-document-hash-mismatch` | `The exact JSON file does not match the expected SHA-256.` | 4 |
| CLI output/manifest differs from documents | `facegeom-hair-regions-output-mismatch` | `CLI --output/--manifest must exactly match the request and proposal paths.` | 4 |
| Any new output collides | `facegeom-hair-regions-output-exists` | Phase-specific no-overwrite message | 4 |
| Preview proof incomplete | `facegeom-hair-regions-preview-proof-incomplete` | The renderer did not produce the complete face/landmark/anchor/artifact proof. | 4 |
| Preview publication failure | `facegeom-hair-regions-preview-bundle-promotion-failed` | The private preview bundle could not be promoted to the requested new root. | 4 |
| Preview cleanup survivor | `facegeom-hair-regions-preview-cleanup-failed` | The diagnostic names every artifact that could not be rolled back. | 4 |
| Missing or unknown option | `usage-error` | Phase-specific required/unknown-option message | 2 |
| Outside-workspace/device/ADS/reparse path | `facegeom-hair-regions-security-refused` | Boundary-specific refusal message | 3 |
| Cancellation | `cancelled` | `The FaceGeom hair-region command was cancelled.` | 5 |

The existing process exit contract is:

```json
{
  "success": 0,
  "generalFailure": 1,
  "usageError": 2,
  "securityRefusal": 3,
  "validationFailure": 4,
  "cancelled": 5
}
```

Do not treat a zero exit, an off-engine image, or a successful independent
byte verification as visual or Skyrim runtime authority.
