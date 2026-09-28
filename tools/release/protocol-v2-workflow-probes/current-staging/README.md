# Current source staging probes

This directory belongs to the current untagged source staging profile, selected by
`verify_release.py --package-staging` for the Preview.272 source identity.
It requires exactly eleven V2-ready commands. Frozen preview.266 final-release
verification keeps the historical eight-ready maps and root catalog unchanged;
use its frozen verifier/release route for an old eight-ready staging artifact.
Readiness never selects the profile, so losing a current command cannot silently
fall back to historical acceptance.

The catalog, schema inventory and Finish policy template have canonical SHA-256
pins in the verifier. Schema files are complete exports from the exact current
source apphost. The fixture rows bind ordinary input bytes by size and SHA-256;
identical bytes share one `inputs/<SHA256>.bin` file across destination paths.
Thirty-three portable inputs were materialized by the existing real synthetic
`ProtocolV2FinishCoreTests` / golden create helpers. These are product-owned test
inputs, not consumer files. No prior output package, workflow, receipt, success
response or operation journal is shipped as proof.

The gate executes invalid Actor Assembly admission, workspace preflight, preset
inspection, NPC preflight, actual creation, Finish analyze, apply and independent
verify. Fresh product-authored workflow documents carry the chain. Finish policy
binds the newly created package's physical hashes and the existing documented
ordinal `path|size|SHA256` source-tree digest. A copied synthetic Skyrim authority
supplies the existing admitted Sandbox template; no runtime is involved.

The verifier reopens returned artifact hashes and all retained workflow bindings,
checks source bytes remain unchanged and retains `review-required` with the real
GUI next action. Preview is explicitly a pinned contract/schema check; this is
not authentic preview rendering, visual approval, runtime proof or promotion.
