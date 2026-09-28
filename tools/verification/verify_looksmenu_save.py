"""Independent oracle for canonical Fallout 4 LooksMenu export and reload."""

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

    assert exported["Gender"] == source["Gender"]
    assert exported["HeadParts"] == source["HeadParts"]
    assert exported["Weight"] == source["Weight"]
    assert list(exported["BodyMorphs"]) == ["Alpha", "Zeta"]

    morphs = exported["Morphs"]
    assert morphs["Values"] == [0.1, 0.2, 0.0, 0.0, 0.0]
    assert morphs["Presets"] == {"ABCDEF": 0.25}
    assert morphs["Regions"] == {"4": [1.0, -2.0, 3.0, 0.0, 0.0, 0.0, 0.0, 0.0]}
    assert morphs["Intensity"] == source["Morphs"]["Intensity"]

    assert list(exported["Tints"]) == ["2", "3"]
    assert exported["Tints"]["2"] == {"Percent": 50, "Type": 2}
    assert exported["Tints"]["3"] == {"Color": 3, "ColorID": 33, "Percent": 20, "Type": 1}
    assert exported["TintOrder"] == ["3", "2"]
    assert "EngineExtra" not in exported

    appearance = response["appearance"]
    assert response["format"] == "looksmenu"
    assert response["edition"] == "fallout4"
    assert response["isValid"] is True
    assert appearance["weight"] == {
        "value": 0.2,
        "thin": 0.2,
        "muscular": 0.5,
        "fat": 0.3,
    }
    assert appearance["chargenFaceMorphs"] == {"11259375": 0.25}
    assert appearance["faceBoneRegions"]["4"] == [1.0, -2.0, 3.0, 0.0, 0.0, 0.0, 0.0, 0.0]
    assert [t["index"] for t in appearance["tints"]] == [3, 2]
    print(f"LOOKSMENU SAVE INDEPENDENT PASS input={args.input} output={args.output} response={args.response}")


if __name__ == "__main__":
    main()
