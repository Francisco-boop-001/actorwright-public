# Desktop accessibility contract

The WPF shell uses keyboard-first focus order, visible focus cues, explicit
automation names, scalable layout, and no information conveyed by color alone.
Minimum window sizes prevent clipped controls. Long operations must be
asynchronous, cancellable, and progress-reporting; the UI thread never reads a
plugin, archive, mesh, or texture.

Structural acceptance proves that the window loads, headings and controls have
automation names, focus cues are present, and progress bindings are safe. It is
supporting evidence only: visible journey inspection and screen-reader/manual
review remain required before GUI acceptance.

The current shell presents task-based blank creation, RaceMenu preset, and
existing-NPC edit workflows alongside the command catalog. The preset task uses
mode-specific headings, labels, action names, and automation identities; source
paths and hashes wrap; retained existing-NPC identity remains selectable but
read-only; invalid startup requests appear in the diagnostics and live status;
and active transactions must be completed or cancelled before the window can
close. Static verification and unproven Skyrim runtime authority are stated
separately. The desktop layer remains an adapter over the shared application
services rather than a second implementation of NPC authoring logic.

Registered provisional `preview.225` adds a resizable five-step Dual-tone
HairTint wizard: Source, Regions, Colors and Preview, Byte-plan Review, and
Write and Verify. Every structural region has keyboard-reachable
Primary/Accent/Preserve controls, text and color indicators, shape/shader
details, preview and mask evidence, explicit Render/Cancel/Back actions, and
no automatic visual PASS. Rendering remains manual and Skyrim remains
authoritative. Every structurally recognized region is exposed as a
keyboard-reachable card with a text role, current/target color values,
shape/shader/texture details, shared-region status, isolated thumbnail, and
role mask. Color is never the only role indicator. Filtering, multi-selection,
bulk Primary/Accent/Preserve assignment, zoom, scrolling, Back, Cancel,
Render, proposal acceptance, and final write all require explicit automation
names and deterministic focus order.

Changing roles or colors updates text and swatches immediately but never
starts Blender. Render is a manual, cancellable operation; stale results cannot
advance the wizard, and Back invalidates any later proposal, preview, or
acceptance. Writing remains disabled until the current proposal has a
successful preview and explicit acceptance. Every render visibly states
**Off-engine HairTint preview — Skyrim runtime remains authoritative** and the
wizard never announces an automatic visual PASS.
