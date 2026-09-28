"""Independent oracle for typed ARMO create/override proposal evidence."""

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
    assert artifact["artifactKind"] == "armor-record-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["mode"].casefold() == mode
    assert artifact["sourcePlugin"].casefold() == str(source.resolve()).casefold()
    assert artifact["sourceFormId"] == "0x00000801"
    assert artifact["editorId"] == editor_id
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["patchSha256"]
    assert artifact["name"] == "Fixture Armor"
    assert artifact["slotMask"] == 16384
    assert artifact["race"] == "Source.esp|0x00000900"
    assert artifact["maleWorldModel"] == "meshes/armor/male.nif"
    assert artifact["femaleWorldModel"] == "meshes/armor/female.nif"
    assert artifact["value"] == 125
    assert artifact["weight"] == 12.5
    assert artifact["health"] == 80
    assert artifact["armorRating"] == 35
    assert artifact["keywords"] == ["Source.esp|0x00000901"]
    assert artifact["armorAddons"] == [{"index": 0, "addon": "Source.esp|0x00000902"}]
    assert artifact["noUnrelatedRecords"] is True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--new", required=True, type=Path)
    parser.add_argument("--override", required=True, type=Path)
    parser.add_argument("--new-response", required=True, type=Path)
    parser.add_argument("--override-response", required=True, type=Path)
    args = parser.parse_args()
    check(args.new, args.source, "new", "FixtureArmor")
    check(args.override, args.source, "override", "M6SourceArmorFO4")
    assert load(args.new_response)["written"] is True
    assert load(args.override_response)["written"] is True
    print(f"ARMOR PROPOSAL INDEPENDENT PASS new={args.new} override={args.override}")


if __name__ == "__main__":
    main()
