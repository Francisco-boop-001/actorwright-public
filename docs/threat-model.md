# Threat model (M1)

## Assets

- User presets, copied plugin data, generated plugin/asset packages, hashes,
  diagnostics, and local source/dependency provenance.

## Trust boundaries

1. Untrusted preset, plugin, archive, texture, mesh, TRI, JSON/XML/INI, and PEX
   bytes enter through explicit input paths.
2. Typed parsers and bounded readers cross into the application model.
3. Proposal writers cross into K-local new output files.
4. Independent readers cross back over written bytes before packaging.
5. Runtime game installs and mod-manager state are outside the write boundary;
   the configured protected game root is read-only/copy-out-only.

## Controls

- Require fully-qualified K-local paths and reject traversal, device/UNC paths, reparse points,
  protected roots, archive escapes, and alternate data streams.
- Enforce size, count, depth, and finite-number limits before allocating or
  traversing untrusted data.
- Do not execute mod-supplied code. External tools use allowlisted adapters,
  isolated K-local working directories, timeouts, cancellation, captured logs,
  and changed-file inventories.
- Use atomic new-file writes, collision refusal, flush/close before verify, and
  hash-bound proposals. Never overwrite an input or use persisted output paths.
- Treat desktop startup arguments as untrusted input. The optional RaceMenu
  startup route accepts only one fully qualified K-local request plus its
  explicit SHA-256, rejects unknown, repeated, partial, or outside-workspace
  options, and passes the selection through the ordinary bounded request loader.
  Startup may review a request; it never starts a build automatically.
- Runtime networking and telemetry are disabled by default.
- Redact personal paths, preset content, and mod inventories from diagnostics
  unless an evidence report explicitly requests them.

M1 proves the path-policy boundary. Format-specific parser limits and external
tool adapters are introduced and tested in later milestones.

## FaceGeom HairTint-region boundary

Registered provisional `preview.225` treats the source NIF, analysis,
assignment request, proposal, reviewed intake, resolved textures, and renderer
artifacts as untrusted exact-byte inputs. It accepts only bounded ordinary
non-reparse K-local files; requires hash-bound explicit roles; authorizes only
declared 12-byte tint envelopes; writes new no-overwrite outputs; and verifies
all remaining bytes plus topology, geometry, skinning, shader ownership, and
texture routing through an independent parser lineage. Preview is off-engine
and never grants Skyrim runtime authority. The implementation also enforces
these fail-closed boundaries:

- Analysis accepts one ordinary, non-reparse K-local NIF from 1 byte through
  128 MiB and at most 64 recognized HairTint shapes. UNC, device, alternate
  data stream, protected/live-root, and reparse traversal paths fail closed.
- Roles come only from a strict hash-bound request. Display names never confer
  trust, duplicate names are ordinary, and shapes sharing one physical tint
  field cannot receive conflicting roles.
- Proposal and apply authorize only explicit 12-byte tint envelopes. Apply
  reopens the source read-only, validates every preimage, creates a bounded
  clone, and publishes new output and manifest artifacts without overwrite.
  Source/output aliases and existing destinations are refused.
- Independent verification uses a separate parser lineage and compares every
  byte outside the authorized envelopes, plus topology, geometry, skinning,
  shader ownership, texture routing, length, and final hash.
- Preview materializes only into a private K-local staging root, runs the
  allowlisted Blender/PyNifly adapter with bounded inputs, and promotes the
  complete bundle once. Missing proof, partial output, renderer drift, or a
  surviving cleanup artifact is an explicit failure.
- Preview images are evidence derived from untrusted game assets, not
  executable content and not a runtime verdict. The response keeps
  `visualAuthority:false` and `runtimeAuthority:false`; Skyrim remains the
  final rendering authority.
