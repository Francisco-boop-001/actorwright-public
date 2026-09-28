"""Independent oracle for hash-bound LVLI proposal evidence."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def check(path: Path, source: Path) -> None:
    artifact = load(path)
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    assert artifact["artifactKind"] == "leveled-list-record-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["sourcePlugin"].casefold() == str(source.resolve()).casefold()
    assert artifact["listFormId"] == "0x00000801"
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["editorId"] == "FixtureLeveledList"
    assert artifact["chanceNone"] == 25
    assert artifact["maxCount"] == 3
    assert artifact["calculateAllLevels"] is True
    assert artifact["calculateEachInCount"] is False
    assert artifact["useAll"] is True
    assert artifact["entries"] == [
        {"item": "Source.esp|0x00000900", "level": 2, "count": 1, "chanceNone": 10},
        {"item": "Source.esp|0x00000901", "level": 5, "count": 2, "chanceNone": 0},
    ]
    assert artifact["masterDependencies"] == []
    assert artifact["noUnrelatedRecords"] is True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--proposal", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    check(args.proposal, args.source)
    assert load(args.response)["written"] is True
    print(f"LEVELED LIST PROPOSAL INDEPENDENT PASS proposal={args.proposal}")


if __name__ == "__main__":
    main()
