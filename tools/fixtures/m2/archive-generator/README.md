# M2 archive fixture generator

This fixture-only tool creates one valid Fallout 4 `GNRL` BA2 and one valid
Skyrim SE BSA v105 from the same tiny synthetic asset. It uses the staged,
licensed FO4 NPC Manager archive writer only to produce K-local test fixtures;
the product never references this assembly and the generated archives are
independently read by Mutagen and `tools/verify_m2_fixtures.py`.

The generator refuses output outside the project root and refuses to replace a
fixture whose bytes differ from the deterministic output.
