"""Independent oracle for typed ARMO addon-entry proposal evidence."""

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
    assert artifact["artifactKind"] == "armor-addon-model-entries-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["sourcePlugin"].casefold() == str(args.source.resolve()).casefold()
    assert artifact["sourceFormId"] == "0x00000801"
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["entries"] == [
        {"index": 2, "addon": "Source.esp|0x00000900"},
        {"index": 7, "addon": "Source.esp|0x00000901"},
    ]
    assert artifact["masterDependencies"] == []
    assert artifact["noUnrelatedRecords"] is True
    assert load(args.response)["written"] is True
    print(f"ARMOR ADDON MODELS INDEPENDENT PASS proposal={args.proposal}")


if __name__ == "__main__":
    main()
