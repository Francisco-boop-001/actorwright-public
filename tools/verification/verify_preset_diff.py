"""Independent oracle for deterministic typed preset comparison."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def differences(response):
    return {item["path"]: item for item in response["differences"]}


def diagnostic_codes(response):
    return {item["code"] for item in response["diagnostics"]}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--format", required=True, choices=["looksmenu", "racemenu-jslot"])
    parser.add_argument("--left", required=True, type=Path)
    parser.add_argument("--right", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--repeat", required=True, type=Path)
    args = parser.parse_args()

    left = load(args.left)
    right = load(args.right)
    response = load(args.response)
    repeat = load(args.repeat)
    assert response == repeat, "Repeated diff response changed"
    assert response["equal"] is False
    assert all(item["kind"] == "changed" for item in response["differences"])
    by_path = differences(response)

    if args.format == "looksmenu":
        expected_path = "bodyMorphs.CBBE Breast"
        assert by_path[expected_path]["left"] == str(left["BodyMorphs"]["CBBE Breast"])
        assert by_path[expected_path]["right"] == str(right["BodyMorphs"]["CBBE Breast"])
        assert by_path["gender"]["left"] == str(left["Gender"])
        assert by_path["gender"]["right"] == str(right["Gender"])
        tint_path = "tints[0]"
        assert tint_path in by_path
        assert "preset-diff-unsupported-field" in diagnostic_codes(response)
    else:
        morph_path = "raceMenu.bodyMorphsKeyed.Breast.NPCManager"
        assert by_path[morph_path]["left"] == "0.4"
        assert by_path[morph_path]["right"] == "0.45"
        slider_path = "sliderMorphs[1]"
        assert by_path[slider_path]["left"] == "-0.2"
        assert by_path[slider_path]["right"] == "-0.25"
        assert "preset-diff-unsupported-field" in diagnostic_codes(response)
        assert "preset-diff-unresolved-identifier" in diagnostic_codes(response)

    print(f"PRESET DIFF INDEPENDENT PASS format={args.format} response={args.response}")


if __name__ == "__main__":
    main()
