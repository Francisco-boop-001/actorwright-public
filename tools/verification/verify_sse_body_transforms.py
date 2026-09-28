"""Independent oracle for the bounded RaceMenu transform/skin metadata route."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def value_rows(section: dict) -> dict[tuple[int, int], object]:
    return {
        (int(row["key"]), int(row["index"])): row["data"]
        for key_group in section.get("keys", [{"values": section.get("values", [])}])
        for row in key_group.get("values", [])
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--transforms", required=True, type=Path)
    parser.add_argument("--skins", required=True, type=Path)
    args = parser.parse_args()

    source = json.loads(args.input.read_text(encoding="utf-8-sig"))
    output = json.loads(args.output.read_text(encoding="utf-8-sig"))
    response = json.loads(args.response.read_text(encoding="utf-8-sig"))
    transforms = json.loads(args.transforms.read_text(encoding="utf-8-sig"))
    skins = json.loads(args.skins.read_text(encoding="utf-8-sig"))

    assert response["schemaVersion"] == 1
    assert response["game"] == "skyrimse"
    assert response["isValid"] and response["applied"]
    assert response["inputSha256"] == sha256(args.input)
    assert response["outputSha256"] == sha256(args.output)
    assert output["marker"] == source["marker"]

    expected_transform = transforms[0]
    actual_transform = output["transforms"][0]
    assert actual_transform["node"] == expected_transform["node"]
    actual_transform_values = value_rows(actual_transform)
    assert math.isclose(actual_transform_values[(30, 0)], expected_transform["scale"], rel_tol=0, abs_tol=1e-6)
    assert actual_transform_values[(33, 0)] == expected_transform["scaleMode"]
    assert all(math.isclose(actual_transform_values[(31, index)], expected_transform["position"][index], rel_tol=0, abs_tol=1e-6) for index in range(3))
    assert all(math.isclose(actual_transform_values[(32, index)], expected_transform["rotation"][index], rel_tol=0, abs_tol=1e-6) for index in range(9))

    expected_skin = skins[0]
    actual_skin = output["skinOverrides"][0]
    assert actual_skin["slotMask"] == expected_skin["slotMask"]
    actual_skin_values = value_rows(actual_skin)
    for index, texture in expected_skin["textures"].items():
        assert actual_skin_values[(9, int(index))] == texture
    assert math.isclose(actual_skin_values[(8, -1)], expected_skin["alpha"], rel_tol=0, abs_tol=1e-6)
    tint = actual_skin_values[(7, -1)]
    assert tint == -855670720, tint  # 0.8/1.0/0.5/0.25 packed as signed ARGB after nearest-even rounding.
    assert len(response["effectiveTransforms"]) == 1 and len(response["effectiveSkinOverrides"]) == 1
    print("SSE BODY TRANSFORMS INDEPENDENT PASS transforms=1 skins=1")


if __name__ == "__main__":
    main()
