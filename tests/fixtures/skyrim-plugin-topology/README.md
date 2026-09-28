# Synthetic Skyrim topology fixtures

These two plugins are product-owned test data generated from scratch with Mutagen.Bethesda.Skyrim 0.52.0. `synthetic-v1-repaired-cell-anchor.esp` contains one NPC, one interior CELL, and one persistent ACHR. The orphan variant preserves the same bytes and moves the CELL-children GRUP before its CELL anchor, producing the intended `orphan-cell-children` diagnostic.

The shared source is `tools/fixtures/skyrim-plugin-topology/SyntheticSkyrimPluginTopology.cs`. To explicitly regenerate the two committed files, run from the repository root:

```powershell
pwsh -File tools/fixtures/skyrim-plugin-topology/generate.ps1 -Regenerate
```

The test route also regenerates into its temporary Actorwright work directory and byte-compares the results with these committed fixtures. Ordinary build and test commands never overwrite them.
