"""Independent oracle for hash-bound OTFT create/override proposal evidence."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def check(path: Path, source: Path, mode: str, editor_id: str) -> None:
    artifact = load(path)
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest().upper()
    assert artifact["artifactKind"] == "outfit-record-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["mode"].casefold() == mode
    assert artifact["sourcePlugin"].casefold() == str(source.resolve()).casefold()
    assert artifact["sourceFormId"] == "0x00000801"
    assert artifact["editorId"] == editor_id
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["items"] == ["Source.esp|0x00000803"]
    assert artifact["masterDependencies"] == []
    assert artifact["noUnrelatedRecords"] is True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--create", required=True, type=Path)
    parser.add_argument("--override", required=True, type=Path)
    parser.add_argument("--create-response", required=True, type=Path)
    parser.add_argument("--override-response", required=True, type=Path)
    args = parser.parse_args()
    check(args.create, args.source, "new", "FixtureNewOutfit")
    check(args.override, args.source, "override", "M3DefaultOutfitFO4")
    assert load(args.create_response)["written"] is True
    assert load(args.override_response)["written"] is True
    print(f"OUTFIT PROPOSAL INDEPENDENT PASS create={args.create} override={args.override}")


if __name__ == "__main__":
    main()
