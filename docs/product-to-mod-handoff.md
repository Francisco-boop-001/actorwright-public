# Actorwright product-to-mod handoff

This is the operator contract between the independent Actorwright product
repository and a separate mod-work repository. Nothing is synchronized merely
because Actorwright was committed, tagged, built, or published.

## Ownership

- Actorwright owns application source, tests, build tooling, product
  documentation, packaging, and release evidence.
- The mod-work repository owns its mod projects, game-facing artifacts,
  package gates, and runtime evidence.
- Keep the repositories independent of each other's source tree and Git
  database. Exchange only exact, schema-validated bundles.

## Issue and response

When a pinned Actorwright binary fails or lacks a required capability, the
mod-work repository preserves the refusal and writes a schema-valid sanitized
issue beneath its configured exchange root. The issue includes the exact tool
version, command, requested outcome, observed and expected behavior,
capabilities hash, exit status, and admitted evidence.

Actorwright reads but does not modify that incoming bundle. Product work
reproduces the report, adds a failing test, implements the smallest fix, and
writes a schema-valid response. Validators preserve exact inventory sizes and
SHA-256 values and never normalize signed evidence bytes. A response binds the
physical incoming issue-manifest hash and exact candidate-manifest hash.
Source, credentials, live-game state, and unrelated mod artifacts never cross
the exchange.

## Release candidate

Release work starts from a clean, committed, untagged source tree whose
expected release tag does not exist. The release builder creates self-contained
CLI and desktop applications, assembles the release root, creates and reopens
the deterministic ZIP through the release verifier, and only then creates the
no-force annotated tag on the exact verified source commit. A build, package,
or verifier failure leaves the expected tag absent.

After that verified local sequence passes, assemble a schema-valid candidate
under ignored repository-local artifacts/exchange/<version>/candidate staging
and publish it with the governed no-replace writer. Replace
<exchange-root> with the separately configured exchange directory:

    $exchangeRoot = '<exchange-root>'
    python tools/exchange/publish_candidate.py --source 'artifacts/exchange/<version>/candidate' --output "${exchangeRoot}/releases/candidates/actorwright-<version>" --expect-kind release-candidate

The candidate declares the product version, source commit and tag, package
SHA-256, capability-catalogue hash, SBOM hash, test-summary hash, and exact
file inventory. It contains distributable binaries and release evidence only.
It must not contain Actorwright source, Git data, credentials, or
non-redistributable fixtures.

Candidate and response writers validate sources before staging. They create
an owned temporary sibling beneath an ordinary destination parent, copy or
write the exact inventory, validate it again, and publish with a no-replace
rename. An occupied destination remains untouched; failures remove only the
writer's own staging sibling. No final directory is created early and no
overwrite operation is allowed.

The release package carries the exact exchange schemas; an existing envelope
may carry compatible response payload versions. A release candidate may be
inspected or staged, but it is not active.

## Human promotion

Actorwright development cannot approve its own release candidate. Activation
requires a separate schema-valid human promotion bundle. After the human
explicitly approves the exact candidate, the operator may publish that
validated promotion bundle with a no-replace operation:

    python tools/exchange/publish_candidate.py --source 'artifacts/exchange/<version>/promotion' --output "${exchangeRoot}/releases/approved/actorwright-<version>" --expect-kind promotion

The promotion records an explicit APPROVED decision and binds the SHA-256 of
the exact candidate bundle manifest. Approval applies to exact bytes, not
merely a version label. Replacing any candidate byte invalidates the
promotion. Candidate and promotion publication do not require access to a
mod-work repository or perform consumer installation.

## Consumer installation

The mod-work repository reads the candidate and matching promotion with its
own independent validator and installer. Before invoking the installer, it
prepares a fresh target-version consumer manifest from the candidate's exact
release metadata. That staged manifest describes the candidate being
installed; it is not the still-active consumer manifest. The active manifest
and wrapper remain unchanged until the new installation passes verification.
The promotion input for analyze, apply, and verify is the human-approved
bundle; issue responses are not promotion evidence.

1. prepare creates the target-version consumer manifest, preserving the
   current active version as rollback and binding the candidate version, ZIP,
   capabilities, entry points, sizes, and hashes.
2. analyze receives that target-version manifest and checks schemas,
   inventory, hashes, version, source commit and tag, capabilities, SBOM, test
   summary, destination, and exact human-promotion binding without writing an
   installation.
3. apply receives the same target-version manifest and extracts the approved
   package into a new no-overwrite version-specific consumer directory, then
   makes the files read-only.
4. verify receives the same target-version manifest, independently re-hashes
   the installed package, and checks the exact pinned CLI and desktop entry
   points.
5. activate updates the tracked active consumer manifest and exact wrapper
   path only after verify succeeds, then confirms the live wrapper identity.

Passing the still-active manifest to analyze is invalid: its previous package
hash must reject the new candidate. The mod-work repository receives binaries
and manifests, never Actorwright source. No wrapper searches PATH, an exchange
directory, or the product repository. If the target installation directory
already exists, use verify with the target manifest as a read-only identity
check; do not rerun apply, overwrite the destination, or weaken the
no-overwrite rule.

## Mod use and authority

Mod work invokes only its pinned consumer wrapper. Actorwright output remains
a candidate mod artifact: it must still pass the owning mod project's
two-pass write discipline, static gates, packaging checks, and required
in-game runtime and visual proof. Product integrity does not grant game
runtime, likeness, placement, pathing, conflict-free, recruitment, or release
authority.

## Failure and rollback

If a newly promoted consumer fails, preserve its evidence and submit a new
issue bundle. Do not patch the installed executable or copy product source
into the mod-work repository.

Rollback is an explicit consumer decision to a previously approved immutable
version. Preserve the previous install and its manifest until the successor is
verified; never silently replace or overwrite it. A corrected Actorwright
build returns through the same release-candidate, human-promotion, analyze,
apply, and verify sequence.
