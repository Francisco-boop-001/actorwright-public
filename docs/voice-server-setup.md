# Choosing and changing an XTTS server

Open the existing Voice panel and enter the server address, platform, server
working folder and expected server output folder. **Check** probes the address
and inspects storage without generating audio. **Check and save** repeats that
check and saves the accepted settings for this workspace. You can edit them and
check again whenever you move or replace the server.

The server must already be running on an HTTP loopback address. Checking does
not launch, stop, reconfigure or synthesize through it. The displayed address is
the endpoint Actorwright queried. The working folder is your declaration of
where the server resolves relative paths; it is not proof of the process's
working directory.

## Native Windows

Choose `windows`. Enter the absolute working folder used when launching XTTS
and its expected output folder. Actorwright resolves the server's reported
speaker, model and output paths against that working folder and checks them.
The expected output folder must match the resolved server response. A mismatch
requires correcting your saved setup or the separately managed server setup;
Actorwright does not call XTTS administration endpoints.

Samples remain in the admitted Actorwright workspace. The adapter sends their
native Windows paths to a Windows server. The Voice panel's sample-output field
is separate from XTTS's output folder: it controls Actorwright's own normalized
sample artifacts.

## WSL

Choose `wsl` and leave the two Windows-folder fields empty. This preserves the
existing automatic storage check and `/mnt/<drive>/...` sample paths. A WSL
server with relative folders still needs inspectable backing-storage evidence;
selecting WSL does not bypass an access denial or grant access to a distribution.

## Using the same setup from the CLI

The panel saves `voice-services.json` in the workspace root. It is local machine
configuration and is ignored by this repository. Existing files containing only
`endpoints` remain supported. A configured Windows server has this shape:

```json
{
  "endpoints": ["http://127.0.0.1:8020"],
  "defaultEndpoint": "http://127.0.0.1:8020",
  "setups": [
    {
      "endpoint": "http://127.0.0.1:8020",
      "platform": "windows",
      "serverFolder": "H:\\MyXttsServer",
      "outputFolder": "H:\\MyXttsServer\\output"
    }
  ]
}
```

Agents can edit that file and run `actorwright npc voice discover --json`.
Saving makes the chosen endpoint the default while retaining other entries.
Old configurations without a default retain the existing candidate-selection behavior.
An explicit `--endpoint` uses only the setup bound to that same endpoint; it
does not borrow another server's folders. CLI commands remain noninteractive.

Checks refuse invalid, protected or reparse-point storage. Saving never writes
into the server's folders. Actual synthesis is a separate operation and can
cause the external server to write its own cache or logs. Do not synthesize
against a server whose storage must remain read-only.

Changing the endpoint or its bound platform/folders changes the backend identity
used by synthesis, so resume cannot treat the new setup as the old backend.

## Consumer delivery checks

Select the offline workspace with `ACTORWRIGHT_WORKSPACE_ROOT` or the working directory before voice/dialogue commands. The registered consumer workspace is admitted; the live game, Exchange and migration archive remain protected. Native Windows speaker references remain Windows paths in the synthesis ledger.

Synthesis audio is an intermediate. Dialogue apply preserves those originals and emits PCM16 mono 44100 Hz loose WAVs, using the existing converter for 24000 Hz input. Verification checks final format and reproducible source-to-delivery content, not just the output ledger hash. Do not relabel or rewrite synthesis provenance to attach unrelated audio.

Lookup filenames are derived from final quest/topic/INFO/response records; only combined quest/topic lengths over 25 are truncated. Never rename a plugin, compact IDs or move a response without regenerating affected sidecars. Old proposals with incorrect filenames must be re-analyzed from their unchanged source; do not manually repair their hashes.

Supply the entire ordered copied master closure, including transitive dependencies. Analysis reports the actual missing or out-of-order dependency edge, and apply rejects changed copied dependency bytes.

Missing LIP tools remain a warning in both apply and verify. A successful audio-only verification is not lip-sync or game-playback acceptance. Existing INFO-owned input, imported external audio and arbitrary private callback attachments still require supported authoring contracts; preserve saved recordings rather than manufacturing a native ledger or overwriting the shared follower script. Built-in cooldown values remain advisory, not elapsed-time timers.
