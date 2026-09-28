# Actorwright public-release gate

This checklist governs a **binary release**. The owner has separately authorized
the [source-only snapshot](../source-only-publication.md), with no application
executables or native DLLs. Its publication does not mark these binary gates
passed or authorize distribution of an earlier private package.

This document defines public-release policy criteria; it is not a
candidate-specific verification result. The checkboxes below must be completed
for each candidate and do not indicate that any item has passed. Publication is
authorized only after every criterion is satisfied for the exact source and
package and a release-attached verification receipt records the evidence.
Private hosting and handoff do not waive this gate.

- [ ] Confirm ownership and redistribution rights for every source-derived,
  binary, model, runtime, and test asset.
- [ ] Complete GPL-3.0 source and attribution obligations for the exact release.
- [ ] Remove or replace named-person, named-NPC, modlist-specific, and unclear-
  license fixtures with deterministic product-owned fixtures.
- [ ] Produce and review an SBOM and third-party notices for the exact package.
- [ ] Run secret, credential, personal-path, and repository-history scans.
- [ ] Verify a clean clone restores, builds, tests, and packages without the
  former authoring workspace, migration archive, or exchange directory.
- [ ] Verify CLI and desktop packages contain no loose renderer `.py` files and
  expose exactly the current frozen 142-command catalogue.
- [ ] Verify configured protected game roots remain look-only in source and
  packaged behavior, including a synthetic root on a different drive. Preserve
  the private installation default until explicit configuration replaces it.
- [ ] Record user-approved Skyrim and Fallout 4 runtime scope separately;
  static/off-engine evidence cannot satisfy runtime claims.
- [ ] Obtain explicit owner approval for publication after all preceding
  criteria pass.

## Protected-root configuration

`ACTORWRIGHT_PROTECTED_ROOT` selects the look-only game root. An absolute path
is used directly; a relative path is resolved against the admitted workspace
root, not the process working directory. If the variable is absent, the
default is the neutral example `F:\ExampleGame`; users must configure their actual installation. Invalid or blank settings are refused
rather than silently falling back. This setting does not permit writes outside
the K-local workspace or waive the public-release checks above.

## Retained build and dependency-audit evidence

A publication gate must run from a committed, clean tree. Invoke the canonical
build with `-RequireCleanTree` and `-NoHttpCache`; the release builder does this
automatically. Retain the complete output. Its `EVIDENCE_*` header and footer
bind the source commit,
Git tree, initial and final cleanliness, selected restore configuration, the
tracked `nuget.config`, process-level `NuGetAudit` setting, and HTTP-cache mode.

`-RestoreConfigFile` applies only to the canonical restore. A NuGet
configuration can change package sources and trust settings, so an override is
not assumed to be transport-only. Keep it outside tracked product files and
retain its upstream URL, retrieval time, TLS-verification method, and response
SHA-256 values beside the build log. That separate provenance is not embedded
or hash-bound by the release package.

The release vulnerability scan does not use `-RestoreConfigFile`: it requires
direct access to `https://api.nuget.org/v3/index.json`, forces audit on, and
bypasses NuGet's HTTP cache. A cached, `NuGetAudit=false`, or
`NETWORK_REQUIRED` result is compilation evidence only and blocks release
assembly.

Evidence from an uncommitted tree, a log missing either identity stamp, or a
run whose final identity/status differs from its header does not satisfy this
gate.

## Response and workspace-output boundaries

This public repository starts from a cleaned source snapshot. The seven
historical response drafts remain in the private repository. Those drafts
and their issue bundles are not included in this snapshot and do not serve
as public release evidence. Do not recreate them with placeholder inputs or bind an old response to
a new candidate.

Retain a review disposition for the exact public source and binary candidate,
including unresolved findings and the limits of each verification result.
Any Exchange responses used as current release evidence must be generated
from the actual issue bundle and exact candidate with
`tools/exchange/new_response.py`, and pass
`tools/exchange/validate_bundle.py --expect-kind response`. A draft,
placeholder candidate hash, or response bound to another candidate is not
release evidence. The checklist above remains required independently of
historical Exchange correspondence.

An archive written inside the authorized K-local workspace by `npc finish
apply` is workflow output, not release or Exchange publication. Its mutation
still requires the explicit Finish apply transition; that transition does not
itself prove human review. Candidate assembly, release ZIP creation, and
Exchange writes remain separately gated.

Protocol-2 inline `schema export` is a discovery command and may run outside a
K-local workspace. Protocol-1 retains its frozen bytes, exit codes, and K-local
refusal. Do not broaden the legacy exception to satisfy a protocol-2 discovery
requirement.
