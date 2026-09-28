"""Independent oracle for the typed Skyrim SE RaceMenu .jslot load boundary."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    source = load(args.input)
    response = load(args.response)
    appearance = response["appearance"]
    race_menu = appearance["raceMenu"]

    assert response["format"] == "racemenu-jslot"
    assert response["edition"] == "skyrimse"
    assert response["isValid"] is True
    assert appearance["sliderMorphs"] == source["morphs"]["default"]["morphs"]
    assert appearance["weight"]["value"] == source["actor"]["weight"]
    assert race_menu["headTexture"] == source["headTexture"]
    assert race_menu["faceMorphPresets"] == source["morphs"]["default"]["presets"]
    assert race_menu["sculptDivisor"] == source["morphs"]["sculptDivisor"]
    assert race_menu["sculptParts"][0]["vertices"][0]["dx"] == 0.01
    assert race_menu["bodyMorphsKeyed"]["Breast"] == {"Base": 0.4, "Outfit": 0.2}
    assert race_menu["bodyOverlays"][0]["node"] == "Body [Ovl1]"
    assert race_menu["nodeTransforms"][0]["scale"] == 1.02
    assert race_menu["skinOverrides"][0]["alpha"] == 0.75
    assert any(item["path"] == "$.EngineExtra" for item in appearance["unknownFields"])
    assert any(item["path"] == "$.morphs.default.future" for item in appearance["unknownFields"])
    print(f"RACEMENU LOAD INDEPENDENT PASS input={args.input} response={args.response}")


if __name__ == "__main__":
    main()
