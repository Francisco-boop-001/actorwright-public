# M2 fixture generation

The checked-in FO4 and Skyrim SE plugins are deterministic, minimal test
fixtures. They are generated only in this K-local fixture directory with the
pinned Mutagen 0.52.0 packages and the staged archive-writer fixture tool; the
product never writes them. The expected record, loose-asset, and BA2/BSA member
facts in `01-source-copies/m2-fixtures/fixture-expected.json` are hand-authored
and checked independently by `tools/verify_m2_fixtures.py`.

Run `generate.ps1` from the project root after changing the generator. It
requires the staged local SDK and NuGet feed and refuses a destination outside
the project root.

The archive generator creates one valid FO4 GNRL BA2 and one valid SSE BSA v105
fixture using the staged licensed archive writer. It refuses byte drift in an
existing fixture and never writes outside K.
