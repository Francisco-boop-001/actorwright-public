"""Independent oracle for the typed Fallout 4 LooksMenu load boundary."""

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
    assert response["format"] == "looksmenu"
    assert response["edition"] == "fallout4"
    assert response["isValid"] is True
    assert appearance["gender"] == source["Gender"]
    assert appearance["weight"]["thin"] == source["Weight"][0]
    assert appearance["weight"]["muscular"] == source["Weight"][1]
    assert appearance["weight"]["fat"] == source["Weight"][2]
    assert appearance["bodyMorphs"]["CBBE Breast"] == source["BodyMorphs"]["CBBE Breast"]
    assert appearance["chargenFaceMorphs"]["11259375"] == source["Morphs"]["Presets"]["00ABCDEF"]
    assert appearance["faceBoneRegions"]["4"] == source["Morphs"]["Regions"]["00000004"]
    assert appearance["facialMorphIntensity"] == source["Morphs"]["Intensity"]
    assert [t["index"] for t in appearance["tints"]] == [1, 2]
    assert appearance["tints"][0]["percent"] == 10
    assert appearance["tints"][1]["type"] == 3
    assert any(item["path"] == "$.EngineExtra" for item in appearance["unknownFields"])
    print(f"LOOKSMENU LOAD INDEPENDENT PASS input={args.input} response={args.response}")


if __name__ == "__main__":
    main()
