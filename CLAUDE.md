# Actorwright Claude instructions

Read and follow AGENTS.md before product work. It is the repository authority;
this file is a concise entrypoint, not a second independent policy. Follow the
Two protocols, one product section before planning or invoking Actorwright.

Actorwright is an independent product repository and application. Keep source, tests,
documentation, build tooling, packaging, and release evidence here. Keep mod
projects, game-facing artifacts, screenshots, save data, game profiles, and
load-order work in their owning workspaces. Do not copy Actorwright source,
Git data, credentials, fixtures, or development internals into a mod-work
repository.

Use docs/product-to-mod-handoff.md for the issue, response, release-candidate,
human-promotion, consumer-install, and rollback sequence. A commit, tag, build,
or candidate does not by itself update a mod workspace.

For non-trivial changes, add a behavior-bearing test, run
tools/build/build.ps1, and run the repository-independence validation before
handoff. Keep static product proof distinct from game runtime, visual,
likeness, pathing, conflict-free, and release authority.

The public-release criteria are documented in
docs/security/public-release-gate.md.
