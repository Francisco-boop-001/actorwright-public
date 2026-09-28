"""Independent oracle for canonical Skyrim RaceMenu .jslot export and reload."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()

    source = load(args.input)
    exported = load(args.output)
    response = load(args.response)

    assert exported["actor"] == source["actor"]
    assert exported["headParts"] == source["headParts"]
    assert exported["morphs"]["default"] == source["morphs"]["default"]
    assert exported["morphs"]["custom"] == source["morphs"]["custom"]
    assert exported["morphs"]["sculptDivisor"] == source["morphs"]["sculptDivisor"]
    assert exported["morphs"]["sculpt"] == source["morphs"]["sculpt"]
    assert exported["bodyMorphs"] == source["bodyMorphs"]
    exported_overlay = exported["overrides"][0]
    source_overlay = source["overrides"][0]
    assert exported_overlay["node"] == source_overlay["node"]
    assert exported_overlay["values"][0]["data"] == "textures/actors/character/tattoo.dds"
    assert exported_overlay["values"][1]["data"] == "textures/actors/character/tattoo_n.dds"
    assert exported_overlay["values"][2]["key"] == 7 and exported_overlay["values"][2]["data"] == source_overlay["values"][2]["data"]
    assert exported_overlay["values"][3]["key"] == 8 and abs(exported_overlay["values"][3]["data"] - source_overlay["values"][3]["data"]) < 1e-5
    exported_transform = exported["transforms"][0]
    source_transform = source["transforms"][0]
    assert exported_transform["firstPerson"] == source_transform["firstPerson"]
    assert exported_transform["node"] == source_transform["node"]
    for actual, expected in zip(exported_transform["keys"][0]["values"], source_transform["keys"][0]["values"]):
        assert actual["key"] == expected["key"] and actual["type"] == expected["type"] and actual["index"] == expected["index"]
        assert abs(actual["data"] - expected["data"]) < 1e-5
    assert exported["skinOverrides"] == source["skinOverrides"]
    assert "customMorphs" not in exported
    assert "sliderMorphs" not in exported

    appearance = response["appearance"]
    assert response["format"] == "racemenu-jslot"
    assert response["edition"] == "skyrimse"
    assert response["isValid"] is True
    assert appearance["raceMenu"]["headTexture"] == source["actor"]["headTexture"]
    assert appearance["customMorphs"] == {"Smile": 0.2}
    assert appearance["raceMenu"]["bodyMorphsKeyed"]["Breast"] == {"Base": 0.4, "Outfit": 0.2}
    assert appearance["raceMenu"]["bodyOverlays"][0]["alpha"] == 0.8
    assert appearance["raceMenu"]["nodeTransforms"][0]["scale"] == 1.02
    assert appearance["raceMenu"]["skinOverrides"][0]["alpha"] == 0.75
    print(f"RACEMENU SAVE INDEPENDENT PASS input={args.input} output={args.output} response={args.response}")


if __name__ == "__main__":
    main()
