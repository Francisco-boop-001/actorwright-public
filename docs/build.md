# Reproducible build

Build Actorwright from the repository root or an isolated worktree.
The pinned .NET SDK is 10.0.301; `global.json` disables roll-forward. The bootstrap
uses the repository-local ZIP and its SHA-512 manifest in
`tools/manifests/dotnet-sdk-10.0.301-win-x64.json`. That tracked manifest also
pins the extracted `dotnet.exe` byte length and SHA-256 used by the firewall.

```powershell
.\tools\scripts\bootstrap-dotnet10.ps1
$dotnet = Join-Path (Get-Location) 'artifacts\tools\dotnet-sdk-10.0.301\dotnet.exe'
.\tools\build\build.ps1 -Configuration Release -DotNetPath $dotnet
python -m pytest tests -q --basetemp artifacts\pytest-tmp
```

The canonical build performs locked restore and treats warnings as errors.
For a Release build, the Source compatibility firewall runs after the complete
standalone selector matrix against the source-built CLI. It requires the
authenticated `publicSynthetic` baseline and writes a fresh report under this
repository's `artifacts/test-work` directory.
Package restore or vulnerability-query failures must be reported; a focused
build with a warning override is not a passing canonical gate. Dependencies
and generated output stay in repository-local ignored directories. Use a fresh
`artifacts/` pytest base directory when a retained run already owns the example.

Set `ACTORWRIGHT_WORKSPACE_ROOT` to the separate K-local workspace that a packaged
binary may operate on; otherwise the current directory is used. Product tests
use isolated synthetic workspaces under `artifacts/`. Keep mod projects and game
runtime evidence outside the product repository.

### Package and Full Skyrim master input

The public current-staging Package and Full workflow checks require
`ACTORWRIGHT_TEST_SKYRIM_MASTER` to point to a user-supplied, readable, ordinary
local `Skyrim.esm` from a lawfully obtained Skyrim installation. The verifier
reads it without modification and admits only the canonical 532-byte PACK
record after checking its identity, group path, and digest. It writes that
record only into its owned temporary workflow workspace. The supplied path is
not included in reports. There is no automatic download or bundled fallback;
missing or invalid input fails before workflow scratch is created. The source
and selector test suite remains separate from this external-input requirement.

After the canonical gate, `tools/build/package.ps1` accepts an explicit
`-DotNetPath` and fresh `-OutputRoot` for untagged staging. It publishes
self-contained Windows x64 `cli/actorwright.exe` and
`desktop/Actorwright.Desktop.exe`; RID-specific restore prerequisites must be
satisfied before its no-restore publish. Validate staging using
`python tools/release/verify_release.py <staging-root> --package-staging --json`.
Staging does not publish a candidate, assign a new preview identity, promote a
release, or prove game behavior. The private Exchange handoff is documented in
`docs/product-to-mod-handoff.md`.
After Release staging evidence is written, `package.ps1` runs the Package
firewall against the source CLI and staged release root. Debug packaging keeps
its staging checks without a Package firewall milestone.

A final private release starts from a clean, committed, untagged tree whose
expected tag is absent. `tools/release/build_release.ps1` runs the canonical
build, assembles and fully verifies the release root and deterministic ZIP, then
creates a no-force annotated tag pointing to the captured source commit. It does
not create a tag after any failed build, package, or release verification.
The release verifier creates the deterministic ZIP and checks strict release
identity. The Full firewall then checks the source CLI, release root, and ZIP;
only a passing `FULL_COMPATIBLE` report allows the sole tag operation.
The Full firewall reruns the standalone selectors and canonical Python suite.
When renderer authorities live outside an isolated worktree, set the same
absolute `ACTORWRIGHT_TEST_RENDERER_AUTHORITY_ROOT` for the build and tag gate.

The Full gate's audit:source-scope prerequisite preserves the private
repository's exact pinned-tranche anchor. A public snapshot opts into a separate
mode with the tracked tools/manifests/public-source-scope.json marker. Its
when HEAD is its unique root commit, the gate requires a clean, non-shallow
repository and binds that root commit, HEAD tree, and tracked entry count as
public-root-baseline identity evidence. This baseline is not a
tranche diff or a claim of human review. Later commits must descend from that
root, and their changed paths must pass the existing narrow source-scope
allowlist. If the private anchor exists, it remains authoritative even when the
public marker is present. If the anchor is absent, public mode requires the
marker in both HEAD and its actual history root; otherwise the gate blocks.

To inspect the same tiers directly, run these commands from this repository
after producing the corresponding inputs. Each report path must be a new file
directly beneath this repository's `artifacts/test-work` directory:

```powershell
$sourceCli = Join-Path (Get-Location) 'src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe'
$releaseRoot = Join-Path (Get-Location) 'artifacts\releases\actorwright-<version>'
$releaseZip = Join-Path (Get-Location) 'artifacts\releases\actorwright-<version>.zip'
$reportRoot = Join-Path (Get-Location) 'artifacts\test-work'
$report = Join-Path $reportRoot ('compatibility-firewall-' + [Guid]::NewGuid().ToString('N') + '.json')
pwsh tools\verification\compatibility-firewall.ps1 verify -Tier Source -Baseline publicSynthetic -SourceCli $sourceCli -Output $report
$report = Join-Path $reportRoot ('compatibility-firewall-' + [Guid]::NewGuid().ToString('N') + '.json')
pwsh tools\verification\compatibility-firewall.ps1 verify -Tier Package -Baseline publicSynthetic -SourceCli $sourceCli -ReleaseRoot $releaseRoot -Output $report
$report = Join-Path $reportRoot ('compatibility-firewall-' + [Guid]::NewGuid().ToString('N') + '.json')
pwsh tools\verification\compatibility-firewall.ps1 verify -Tier Full -Baseline publicSynthetic -SourceCli $sourceCli -ReleaseRoot $releaseRoot -ReleaseZip $releaseZip -Output $report
```

`publicSynthetic` is the initial repeatability baseline, not a historical
release or a sealed-package claim. Its v2 provenance binds a clean harness
commit/tree, the Release source apphost and managed assembly, the exact package
manifest and CLI built by the capture command, the invoked package configuration
and build log identities, the selector build evidence, the rebuilt journey
runner, and the pinned SDK. It records the two structured journeys from that
candidate. It does not grant runtime or visual authority. The baseline is
captured only after the first clean source commit and then added in a later
commit; a missing baseline is a hard failure for required verification. Before capture,
check out that first commit detached and confirm git status --porcelain is empty.
The capture authenticator rejects a symbolic branch HEAD and any dirty harness:

```powershell
git checkout --detach HEAD
git status --porcelain
```

For the one-time bootstrap, build the source CLI in Release. The capture command
invokes `package.ps1` itself with an explicit candidate configuration and a
fresh repository-local output directory. Use Debug for the initial bootstrap;
that invokes the normal staging verifier and does not claim the Release-only
Package gate. A later Release candidate runs that gate normally and fails if
the required baseline is absent. Use the exact pinned SDK path when it is
installed outside the repository:

```powershell
$root = (Get-Location).Path
$dotnet = Join-Path $root 'artifacts\tools\dotnet-sdk-10.0.301\dotnet.exe'
$candidate = Join-Path $root 'artifacts\packages' ('compatibility-firewall-candidate-' + [Guid]::NewGuid().ToString('N'))
$capture = Join-Path $root 'artifacts\test-work' ('compatibility-firewall-' + [Guid]::NewGuid().ToString('N'))
& $dotnet build (Join-Path $root 'src\NpcManager.Cli\NpcManager.Cli.csproj') --configuration Release --no-restore
pwsh tools\verification\compatibility-firewall.ps1 capture-synthetic -DotNetPath $dotnet -CandidateConfiguration Debug -SourceCli (Join-Path $root 'src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe') -CandidateOutputRoot $candidate -Output $capture
```

The capture command records the exact package-script invocation and process
exit/log hashes, independently verifies staging, compares source and candidate
command projections, runs both structured journeys, and refuses a dirty or
identity-mismatched harness. Copy only the returned manifest and its declared
members into `tests/fixtures/compatibility-firewall/publicSynthetic`, then
update the closed baseline registry in the follow-up baseline commit. The
normal Release Source, Package, and Full gates remain required afterward.

Candidate verification normally takes `-SourceCli` and reruns Full. To reuse a
completed Full report, pass `-FullReport <path>` and
`-FullReportSha256 <sha256>` together and omit `-SourceCli`. The caller must
carry the digest from the trusted Full invocation; it is an integrity pin, not
a signature. Candidate validates the exact report bytes, closed passing gates,
source/package CLI identities, and a fresh Full pin graph for the supplied
release root and ZIP before checking the candidate bundle. It reuses the saved
source, selector, suite, probe, and journey results; it does not claim those
checks ran again. The resulting `pins:candidate` evidence records the saved
report path and digest and marks Full evidence as `REUSED`.
The saved Full `verification:release` gate's ZIP length and SHA-256 must match
the supplied ZIP bytes. Only the `source-overlap:releaseFileInventory`
consumer path is rebound to that supplied ZIP, so the retained Full ZIP and
published Candidate ZIP may have different filenames; every other Full pin
field must still match the fresh graph.

Replace `<version>` with the actual release version, or use explicit paths
passed to the release builder. The wrapper requires
Python 3.12.4 and the pinned .NET SDK at the repository-local
`artifacts/tools/dotnet-sdk-10.0.301/dotnet.exe` path, or the explicit path in
`ACTORWRIGHT_TEST_DOTNET`. Derive
selector, command, and test counts from the final run and its reports; historical
Preview.275 counts are baseline evidence, not estimates for a later release.
The firewall does not install, elevate, activate, or promote anything, and it
requires no approval between its verification steps.

After separately assembling the schema-valid bundle inputs under ignored
repository-local `artifacts\exchange\<version>\candidate` and `promotion`
staging, publish the verified candidate and, only after explicit human approval,
its manifest-bound promotion:

```powershell
python tools\exchange\publish_candidate.py --source artifacts\exchange\<version>\candidate --output K:\ExampleExchange\releases\candidates\actorwright-<version> --expect-kind release-candidate
python tools\exchange\publish_candidate.py --source artifacts\exchange\<version>\promotion --output K:\ExampleExchange\releases\approved\actorwright-<version> --expect-kind promotion
```

The paths above are examples; substitute your configured exchange directory.

Both commands validate and publish with no replacement. They use the Exchange
boundary only; consumer analyze, apply, verify, and activation remain later,
separate operations in the mod-work repository.

The full voice/dialogue checks must run from a K-local checkout outside
the reserved exchange and migration archive roots: those product
flows deliberately reject inputs and tools under either reserved root (see `ActorwrightWorkspace` in the source). An isolated
checkout under `K:\ExampleCheckout\artifacts\` satisfies that location requirement.
Do not relax the exclusions to make a worktree under Exchange pass.

## Example paths in the public source

Machine-specific directory names have been replaced with neutral examples.
Some legacy verification scripts and the preview-NIF export helper still require
`K:\ExampleWorkspace`; use that workspace for those paths. This anonymization
preserves their existing fixed-root checks rather than making them configurable.
The protected-game default is `F:\ExampleGame`: set `ACTORWRIGHT_PROTECTED_ROOT`
to your actual game installation before use. Voice authoring continues to reject
all of `F:` and the reserved `K:\ExampleExchange` and
`K:\ExampleMigrationArchive` roots.
