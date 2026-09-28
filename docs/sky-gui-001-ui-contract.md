# SKY-GUI-001 UI contract: copied-Data preflight

Status: locked for implementation  
Version: 1.0  
Updated: 2026-07-21  
Applies to: packaged WPF desktop, Skyrim SE/AE

## Purpose and parity boundary

This is the first task in NPC Studio. It turns an explicit copied Skyrim `Data`
directory and JSON load-order manifest into one immutable, hash-bound intake
that later browse, edit, bake, and create tasks can trust.

It preserves the executable behavior of the pinned upstream
`Preflight_Form`: plugin discovery, active-state presentation, filtering,
selection, transitive-master closure, progress, cancellation, generated-plugin
scan, sibling `.bssliders` inspection, and final handoff. It deliberately
replaces the upstream executable picker with explicit copied-Data and output
paths. The live game is never an accepted input.

## Information hierarchy

The first tab is named `Open workspace`. Its accessible name is
`Open copied Skyrim workspace task`.

1. A compact hero explains that the operation is read-only for the copied Data
   directory and that outputs remain under K.
2. The left column contains the three required paths in task order:
   `Copied Data folder`, `Load-order manifest`, and `Fresh output folder`.
3. The left column then contains plugin controls: a search field, an
   `Active only` filter, `Select active`, `Select shown`, `Clear shown`, and
   `Add required masters`.
4. The plugin list presents selection, plugin name, active state, master count,
   and a short health label. Filtering never changes hidden selection.
5. The right column contains the primary `Review copied workspace` action,
   progress/status, reviewed counts, diagnostics, and the accepted-intake
   verdict.
6. Downstream task tabs remain visible but are disabled until an intake is
   accepted. The command catalog and animation browser remain independent.

## Visual contract

- Use the existing window, card, border, accent, muted, danger, and typography
  resources. Do not introduce a second visual language.
- The primary action is the only filled accent button in the surface.
- Selected healthy plugins use the accent color; warnings use warm neutral
  copy; failures use the existing danger brush plus text and never color alone.
- Plugin rows are at least 36 device-independent pixels high. Primary targets
  are at least 40 pixels high.
- The two-column layout collapses to a single vertical reading order below
  1040 pixels without horizontal scrolling.
- Counts use plain language: `8 selected`, `3 generated plugins`,
  `1 sidecar`, `2,431 asset providers`.

## States

### Empty

The path fields contain safe K-local project defaults but no review is implied.
The plugin list says `Review a copied workspace to see its plugins.` The
primary action is enabled only when all four path values are syntactically
valid absolute paths.

### Reviewing

Path, filter, and selection controls are disabled. The primary action becomes
disabled, `Cancel review` becomes enabled, and the status is announced through
a polite live region. Progress is indeterminate while reading the manifest and
assets and determinate while inspecting known plugins. Closing the app is
blocked until the operation stops.

### Needs attention

No intake is retained. The header reads `Workspace needs attention` and the
first actionable error receives keyboard focus. Missing files, protected or
reparse paths, duplicate order entries, unreadable plugins, missing or
out-of-order masters, malformed sidecars, and archive/index failures are
visible by stable diagnostic code and plain-language message. Partial scans do
not enable downstream tasks.

### Reviewed

The header reads `Workspace reviewed`. It presents the exact selected/master
closure, manifest hash, plugin count, sidecar count, generated-plugin count,
asset-provider count, and `Static intake only - not runtime authority`.
Downstream tasks receive the immutable intake, not mutable text-box values.

### Stale

Changing any input path or plugin selection immediately discards the accepted
intake, disables downstream tasks, and announces `Review required - inputs
changed.` A later filesystem/hash mismatch is also a refusal, never a warning
that leaves the old intake active.

### Cancelled

Cancellation occurs at service boundaries. No intake is retained and no output
is written. The user may edit paths and run the review again.

## Keyboard and accessibility

- Tab order follows the information hierarchy above and then enters the plugin
  rows.
- Every text field, button, filter, list, progress indicator, count, verdict,
  and diagnostic list has an explicit automation name.
- `Enter` invokes review only when focus is outside a multiline or list control.
  `Escape` cancels only while review is active; otherwise it does not close the
  main window unexpectedly.
- Search has an accessible help description stating that filtering does not
  change selection.
- Plugin state and diagnostic severity are exposed as text.
- Status changes and final verdict use polite live announcements. Errors do not
  repeatedly steal focus while the service is still running.

## Safety and data contract

- Edition is fixed to `skyrimse` for this product surface.
- Workspace, copied Data, load-order manifest, output, every plugin, sidecar,
  archive, and retained result must be under the configured authoring workspace (for example `K:\ExampleWorkspace`) and may
  not traverse a reparse point.
- The configured protected game root is always refused, including as a read source.
- The output path may not exist and may not contain or be contained by the
  copied Data directory. Preflight writes nothing.
- The manifest is parsed once per review. Every listed plugin is read at most
  once per review. The accepted intake binds the manifest and selected plugin
  bytes by SHA-256.
- Only selected plugins and their complete transitive masters enter the
  accepted closure. Required masters are ordered by the explicit manifest.
- Unreadable plugin headers, missing masters, malformed sidecars, or invalid
  archives are fatal before handoff.
- Runtime authority is always false.

## Acceptance evidence

`SKY-GUI-001` is accepted only when all of the following are retained:

1. A fresh packaged desktop visibly completes the reviewed state against a
   copied K-local Skyrim fixture.
2. Keyboard traversal and automation inspection expose the names and states in
   this contract.
3. A production CLI/readback of the same fixture yields the same ordered plugin
   closure and hashes.
4. Protected-root, malformed/duplicate order, missing-master, malformed
   sidecar, cancellation, and stale-input paths all retain no intake.
5. The accepted intake is handed to at least one downstream task boundary.

A unit test, build, process ID, window handle, screenshot without interaction,
or CLI-only result cannot satisfy this contract.
