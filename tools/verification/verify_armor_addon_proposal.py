"""Independent oracle for typed ARMA create/override proposal evidence."""

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
    assert artifact["artifactKind"] == "armor-addon-record-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["mode"].casefold() == mode
    assert artifact["sourcePlugin"].casefold() == str(source.resolve()).casefold()
    assert artifact["sourceFormId"] == "0x00000801"
    assert artifact["editorId"] == editor_id
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["patchSha256"]
    assert artifact["slotMask"] == 16384
    assert artifact["race"] == "Source.esp|0x00000900"
    if mode == "new":
        assert artifact["footstepSet"] == "Source.esp|0x00000901"
        assert artifact["maleModel"] == "meshes/armor/male.nif"
        assert artifact["femaleModel"] == "meshes/armor/female.nif"
        assert artifact["maleFirstPersonModel"] == "meshes/armor/male_fp.nif"
        assert artifact["femaleFirstPersonModel"] == "meshes/armor/female_fp.nif"
        assert artifact["maleSkinTexture"] == "Source.esp|0x00000902"
        assert artifact["femaleMaterialSwap"] == "Source.esp|0x00000907"
        assert artifact["additionalRaces"] == ["Source.esp|0x0000090B"]
        assert artifact["sculpt"] == [{"gender": 0, "boneName": "Breast_skin", "deltaX": 1.0, "deltaY": 0.0, "deltaZ": -1.0}]
    else:
        assert artifact["changedFields"] == ["slotMask", "race", "maleModel", "femaleModel"]
    assert artifact["noUnrelatedRecords"] is True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--new", required=True, type=Path)
    parser.add_argument("--override", required=True, type=Path)
    parser.add_argument("--new-response", required=True, type=Path)
    parser.add_argument("--override-response", required=True, type=Path)
    args = parser.parse_args()
    check(args.new, args.source, "new", "FixtureAddon")
    check(args.override, args.source, "override", "M6SourceArmorAddonFO4")
    assert load(args.new_response)["written"] is True
    assert load(args.override_response)["written"] is True
    print(f"ARMOR ADDON PROPOSAL INDEPENDENT PASS new={args.new} override={args.override}")


if __name__ == "__main__":
    main()
