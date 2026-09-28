# Architecture contract

The application is split into bounded modules with one-way dependencies:

```text
Domain <- Application
Domain + Application <- Assets / BodyGen / FaceGen / Formats.Bethesda / Pipeline / Presets / Rendering
Application + Domain + Formats.Bethesda + Rendering <- Infrastructure
Domain <- Verification
Application + required feature modules <- CLI / Desktop composition roots
```

`NpcManager.Domain` owns immutable value objects and invariants. It does not
read files, serialize JSON, render, or reference UI. `NpcManager.Application`
owns typed use-case contracts, diagnostics, command descriptors, cancellation,
and ports. Infrastructure supplies explicit file-system and policy adapters.
The CLI and WPF shell are thin composition/presentation layers.

The composition roots may reference every feature module they instantiate; that
is composition, not business-logic ownership. External packages are closed to a
three-project allowlist: Mutagen readers in `Assets` and `Formats.Bethesda`, and
the in-process BCnEncoder codec in `FaceGen`. The production architecture
validator fails any undeclared project, project reference, or package reference.

Feature modules are intentionally separate so plugin parsing, preset mapping,
asset resolution, FaceGen, BodyGen, pipeline composition, and rendering cannot
become one God object. `NpcManager.Pipeline` depends only on typed Application
contracts and Domain values; it coordinates services but does not parse plugin
bytes or own format-specific writers. The CLI composition root assembles an
immutable `CliRunnerServices` bundle. `CliRunner` receives that bundle plus its
two output streams, so adding a command service does not grow the router's
public constructor or force optional dependency ordering through the dispatch
layer.
The independent verification module must not reuse the writer's serializer.

The Actor Assembly preflight follows the same boundary. Application owns the
closed schema-1 contract, evidence records, identity/result observations, and
the command descriptor. Infrastructure owns strict K-local JSON admission and
the package-independent typed/raw Skyrim identity reader. Presets exposes the
read-only PIRT TRI inspector, while Pipeline owns only orchestration and pure
outfit/provenance collision evaluation. The CLI projects the result/error
envelopes and performs no file or plugin mutation. Every admitted result is
static evidence only (`noWrite=true`, `runtimeAuthority=false`); the command
is included in the registered preview.226 package as a static-only route; a
separate runtime/release registration gate remains open.

The FaceGeom HairTint-region slice preserves the same boundaries. Application
owns the versioned analysis/request/proposal/manifest and preview contracts.
Infrastructure owns structural NIF analysis, strict JSON/file authority,
fixed-width proposal and apply transactions, atomic no-overwrite publication,
and preview orchestration. Formats.Bethesda owns the separately implemented
output parser/verifier and reviewed provider composition; Rendering owns the
pinned Blender/PyNifly process and artifact authority. CLI and Desktop remain
adapters over those contracts. The writer never re-serializes a NIF: it
changes only proposal-authorized 12-byte tint envelopes, while the separate
verifier proves all other bytes and structural fingerprints unchanged.

The Skyrim paired-follower finishing slice follows the same split. Application
owns the schema-3 request/proposal/result contracts while retaining schema 2
as the legacy companion-immutable form; Formats.Bethesda owns the bounded
TES4/record read-write-readback adapter; CLI owns option parsing and
presentation; and `tools/verify_skyrim_follower_finish_pair_package.py` is a
separate raw package oracle. Actor names, provider hashes, outfit choices,
placement, and FormID allocations belong to request data rather than reusable
service policy. Schema 2 keeps the companion byte-identical; schema 3 may
change only its explicitly bound hair/outfit finish surfaces. The subject
plugin remains limited to proposal-declared surfaces.

Architecture alarms are reviewed when a class exceeds 500 logical lines, a
method exceeds 60 logical lines, complexity exceeds 15, or a constructor has
more than seven dependencies. These are review triggers, not mechanical split
targets.
