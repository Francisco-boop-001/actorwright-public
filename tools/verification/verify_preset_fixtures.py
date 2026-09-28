#!/usr/bin/env python3
"""Independent M4 fixture verifier; intentionally does not import the production codecs."""

from __future__ import annotations

import json
import sys
from pathlib import Path


def reject_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate key: {key}")
        result[key] = value
    return result


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=reject_duplicates)


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    fixture_root = root / "01-source-copies" / "m4-fixtures"
    fo4 = load(fixture_root / "fo4-looksmenu.json")
    assert set(("Gender", "HeadParts", "Weight", "Morphs", "BodyMorphs", "Tints", "Overlays", "Skin")) <= fo4.keys()
    assert fo4["Weight"] == [0.2, 0.5, 0.3]
    assert fo4["BodyMorphs"]["CBBE Breast"] == 0.25
    sse = load(fixture_root / "sse-racemenu.jslot")
    assert len(sse["headParts"]) == 2
    assert sse["actor"]["weight"] == 0.62
    assert sse["bodyMorphs"][0]["keys"][0]["value"] == 0.4
    load_order = load(fixture_root / "load-order.json")
    assert load_order["ExampleHair.esp"] == 2
    print("RESULT PASS fixtures=2 duplicate-key-policy=verified form-resolution-map=verified")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, OSError, ValueError) as exc:
        print(f"RESULT FAIL {exc}")
        raise SystemExit(1)
