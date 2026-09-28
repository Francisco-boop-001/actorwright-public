# Synthetic COtR-style brow collision

This fixture contains no third-party game or mod asset. `GoldenSkyrimWaveBFixture.cs` materializes it in a fresh repository-local `artifacts/task38/run-*` sandbox, using the existing golden Skyrim plugin/NIF/DDS helpers.

The selected brow has a 141-vertex model and independently present 501-vertex Race, dialogue and Chargen TRI files. The exact unchanged default model SHA256 is `85811BA859D4833CC60D72098406D810E6BFC3A3BAE40928AE0979EB119A41B5`. Separate explicit 141-vertex TRI replacements are supplied for the reviewed private HDPT operation. No mismatched TRI is rewritten or substituted by the build.

The copied provider includes a selected eye HDPT with a green TXST override while its model requests a different texture path. A separate `SyntheticHair.esp` owns the selected hair and RACE `SyntheticHair.esp|0x00000900` (`SyntheticCotrRace`), cloned from the admitted synthetic base race. The NPC, HDPT valid-race lists, request and record authority all bind that custom-owner RACE. The hair source HairTint is `#222222`; the preset's output-owned CLFM is `#5C5850`. This exercises a COtR-style topology/race-list collision without claiming to reproduce or distribute a real COtR mod.

The race's WNAM skin ARMO and body/hands/feet ARMAs are also cloned into the same synthetic provider and admit its custom race, preserving their existing texture routes. The separate vanilla outfit intentionally remains race-excluding until the reviewed Finish clone policy; the fixture does not confuse naked-skin admission with that outfit refusal.

After the initial build refusal, the actual V1 `records propose`, `plugin write`, and `npc face-patch` commands author the private HDPT, copy its coherent assets, and replace the source NPC PNAM. The test refreshes ordinary copied-input hashes, then runs create again with the real topology guard enabled.

The integrated Finish fixture extends the actual create source prefix to seven masters before binding the Finish request, preserving its generated masters. The eighth provider owns the selected outfit; its armor reaches an ARMA whose race list excludes the actor until Finish's explicit clone policy is selected. No local CSTY is manually seeded. The test retains every command response and artifact hash and makes no runtime or visual claim.

Run through the controller's serialized build/test workflow with the CLI.Tests selector `--test-wave-b-v1-transcript`. The three new partial test files own orchestration, synthetic input construction, and integrated Finish/placement assertions. Successful evidence is recorded in the dated transcript report; this README alone is not a passing test claim.
