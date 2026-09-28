"""Independent oracle for selected-section preset copy/paste."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--target", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--inspect", required=True, type=Path)
    args = parser.parse_args()

    source = load(args.source)
    target = load(args.target)
    response = load(args.response)
    inspected = load(args.inspect)
    assert response["format"] == "looksmenu"
    assert response["edition"] == "fallout4"
    assert response["written"] is True
    assert response["sections"] == ["body-weight", "face-tints"]
    assert all(item["severity"] != "error" for item in response["diagnostics"])

    appearance = inspected["appearance"]
    assert appearance["weight"]["value"] == source["Weight"][0]
    assert appearance["weight"]["muscular"] == source["Weight"][1]
    assert appearance["weight"]["fat"] == source["Weight"][2]
    assert appearance["tints"][0]["percent"] == source["Tints"]["00000001"]["Percent"]
    assert appearance["bodyMorphs"]["CBBE Breast"] == target["BodyMorphs"]["CBBE Breast"]
    print(f"APPEARANCE COPY INDEPENDENT PASS response={args.response}")


if __name__ == "__main__":
    main()
