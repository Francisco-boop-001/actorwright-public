# Actorwright agent contract

Actorwright is an independent product repository. Work here is product code,
tests, documentation, packaging, and release evidence for Actorwright only.
Keep mod projects, game-facing outputs, screenshots, save data, load-order
state, and game profiles in their owning workspaces.

## Workspace and protected-root configuration

- Keep Actorwright source, Git data, credentials, fixtures, and build output in
  the product repository. Do not copy them into a mod-work repository.
- Before a workspace-bound command, set ACTORWRIGHT_WORKSPACE_ROOT to an
  absolute K-local path for the repository or a fresh K-local sandbox. These
  commands refuse a workspace on another drive; discovery commands remain
  available.
- Set ACTORWRIGHT_PROTECTED_ROOT to the absolute path of the game/work subtree
  that must be protected, or to a path relative to ACTORWRIGHT_WORKSPACE_ROOT.
  An unset variable uses the product's compiled default. A malformed value is
  refused; do not work around that refusal.
- Follow docs/product-to-mod-handoff.md for the generic release and mod-work
  handoff. Keep each repository independent of the other's source tree and
  Git database.
- Keep generated build and test output under repository-local ignored paths.
- Preserve internal NpcManager.* namespaces and established wire/schema names
  unless a separately reviewed compatibility break is approved.

## Two protocols, one product

Actorwright has two agent protocols. Both are supported; neither replaces the
other.

- Protocol 1 is the complete current 142-command interface. Select it by
  omitting --protocol (all binaries) or passing exact --protocol 1
  (preview.266 and later). Every command whose protocol-2 registry entry says
  readiness: "legacy" is called this way. Legacy means "not yet typed for
  protocol 2"; it never means unavailable. The frozen Preview.272 package
  remains a complete 136-command historical interface.
- Protocol 2 (--protocol 2 --json) is the strict typed kernel for commands
  whose registry entry says readiness: "v2". Prefer it when the command is
  v2; otherwise use protocol 1. Unsupported protocol values are refused;
  there is no protocol 3.

Prerequisites before any invocation or build:

1. Set ACTORWRIGHT_WORKSPACE_ROOT to a K-local path. Outside-K workspace-bound
   commands refuse with workspace-root-not-k-local; discovery commands still
   run.
2. Use a self-contained binary or build with the pinned .NET SDK 10.0.301.
   Pass artifacts/tools/dotnet-sdk-10.0.301/dotnet.exe through the build
   script's -DotNetPath option. The framework-dependent bin/Release apphost
   needs a .NET 10 runtime that is not installed machine-wide.
3. Discover from the exact binary: actorwright version --protocol 2 --json,
   actorwright capabilities --json (protocol 1, exhaustive),
   actorwright capabilities --protocol 2 --json (readiness),
   actorwright <command> --help (scoped options), and
   actorwright schema export --protocol 2 --json --command <command>
   (typed options, document schemas, limitations, effects, authority).
4. Report discovery as blocked only after steps 1-3 were satisfied and the
   binary still cannot be queried.

A capability never grants mutation approval, human review, visual authority,
game-runtime authority, or release promotion.

## Workflow

1. Add a failing behavior-bearing test before a feature or bug fix.
2. Run tools/build/build.ps1 from the repository root.
3. Run the repository-independence and registered standalone-selector
   validators before packaging.
4. Keep runtime, visual, and game-behavior claims distinct from static proof.

The public guidance tests cover protocol selection and the generic handoff.
Private developer-agent permission profiles and their permission-probe tests
are intentionally not distributed. Product runtime protected-root tests
remain part of the build and are not replaced by those documentation checks.
