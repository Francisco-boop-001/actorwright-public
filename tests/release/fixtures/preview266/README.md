# Frozen release schema test inputs

These two documents were extracted from the exact local
`v1.0.0-preview.266:src/NpcManager.Infrastructure/CommandDocumentSchemaCatalog.cs`
for the historical release harness. It must not read current source schemas.

Canonical JSON SHA-256 (sorted keys, compact UTF-8):

- npc-create-request.schema.json: `B9ABF5511BE3901E50DCD220678F6EC1FB7D13E11C7AE2F67D27C239E186D68D`, matching the existing verifier pin.
- reviewed-game-intake.schema.json: `6363433B72731A81D3429033C65F9344272D3AE92E31EAE2BBA5223751DC5136`.

This changes only test input selection; frozen product probe files and release
schema expectations are unchanged.
