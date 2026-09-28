#!/usr/bin/env python3
"""Independent FaceGen manifest safety verifier; does not import the C# parser."""

from __future__ import annotations

import hashlib
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
    data = path.read_bytes()
    if len(data) > 4 * 1024 * 1024:
        raise ValueError("manifest exceeds size limit")
    value = json.loads(data.decode("utf-8"), object_pairs_hook=reject_duplicates)
    return value, hashlib.sha256(data).hexdigest().upper()


def valid_heads(manifest):
    return [
        shape for shape in manifest["shapes"]
        if shape["role"] == "head" and shape["applicable"] and shape["includedInOutput"]
        and shape["vertexCount"] > 0 and len(shape["topologySha256"]) == 64
    ]


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    fixture_root = root / "01-source-copies" / "m5-fixtures"
    fo4, fo4_hash = load(fixture_root / "fo4-facegen-valid.json")
    sse, sse_hash = load(fixture_root / "sse-facegen-valid.json")
    zero, _ = load(fixture_root / "zero-shapes.json")
    poison, _ = load(fixture_root / "poison-shapes.json")
    assert fo4["schemaVersion"] == 1 and sse["schemaVersion"] == 1
    assert fo4["edition"] == "fallout4" and len(valid_heads(fo4)) == 1
    assert sse["edition"] == "skyrimse" and len(valid_heads(sse)) == 1
    assert not valid_heads(zero)
    assert len(valid_heads(poison)) == 1
    assert sum(shape["includedInOutput"] and shape["role"] in {"hair", "collider"} for shape in poison["shapes"]) == 2
    print(f"RESULT PASS manifests=4 fo4-sha256={fo4_hash} sse-sha256={sse_hash} zero-shape=verified poison-shapes=verified")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"RESULT FAIL {exc}")
        raise SystemExit(1)
