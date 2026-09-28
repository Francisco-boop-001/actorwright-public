"""Independent oracle for typed Fallout 4 MSWP proposal evidence."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--proposal", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    artifact = load(args.proposal)
    source_hash = hashlib.sha256(args.source.read_bytes()).hexdigest().upper()
    assert artifact["artifactKind"] == "material-swap-record-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["mode"].casefold() == "new"
    assert artifact["sourcePlugin"].casefold() == str(args.source.resolve()).casefold()
    assert artifact["sourceFormId"] == "0x00000801"
    assert artifact["editorId"] == "FixtureSwap"
    assert artifact["treeFolder"] == "Armor"
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["patchSha256"]
    assert artifact["entries"] == [
        {
            "originalMaterial": "materials/armor/old.bgsm",
            "replacementMaterial": "materials/armor/new.bgsm",
            "colorRemapIndex": 0.25,
            "treeFolder": "Armor",
        },
        {
            "originalMaterial": "materials/armor/old2.bgem",
            "replacementMaterial": "",
            "colorRemapIndex": None,
            "treeFolder": None,
        },
    ]
    assert artifact["noUnrelatedRecords"] is True
    assert load(args.response)["written"] is True
    print(f"MATERIAL SWAP INDEPENDENT PASS proposal={args.proposal}")


if __name__ == "__main__":
    main()
