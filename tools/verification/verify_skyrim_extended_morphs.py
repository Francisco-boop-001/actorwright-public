#!/usr/bin/env python3
"""Independently verify a RaceMenu customMorphs JSON patch."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def hash_value(value: object) -> object:
    return value.get("value") if isinstance(value, dict) else value


def custom_map(document: dict) -> dict[str, float]:
    values = document.get("customMorphs", [])
    if not isinstance(values, list):
        raise AssertionError("customMorphs is not an array")
    result: dict[str, float] = {}
    for item in values:
        if not isinstance(item, dict) or set(item) != {"name", "value"}:
            raise AssertionError("customMorphs entry is not the strict name/value shape")
        name = item["name"]
        value = item["value"]
        if not isinstance(name, str) or not name or not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value):
            raise AssertionError("customMorphs entry has invalid name/value")
        folded = name.casefold()
        if folded in {key.casefold() for key in result}:
            raise AssertionError(f"duplicate custom morph {name}")
        result[name] = float(value)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--json", type=Path, required=True, help="CLI evidence JSON")
    parser.add_argument("--expected", type=Path, required=True, help="patch JSON")
    args = parser.parse_args()

    before = json.loads(args.before.read_text(encoding="utf-8-sig"))
    after = json.loads(args.after.read_text(encoding="utf-8-sig"))
    evidence = json.loads(args.json.read_text(encoding="utf-8-sig"))
    expected = json.loads(args.expected.read_text(encoding="utf-8-sig"))
    if evidence.get("applied") is not True:
        raise AssertionError("CLI evidence does not report applied=true")
    if hash_value(evidence.get("inputSha256")) != sha256(args.before):
        raise AssertionError("CLI evidence input hash mismatch")
    if hash_value(evidence.get("outputSha256")) != sha256(args.after):
        raise AssertionError("CLI evidence output hash mismatch")
    if not isinstance(expected, dict) or not isinstance(expected.get("morphs"), list):
        raise AssertionError("expected patch is not an object with morphs")

    before_map = custom_map(before)
    after_map = custom_map(after)
    desired = dict(before_map)
    for item in expected["morphs"]:
        name = item["name"]
        value = float(item["value"])
        existing = next((key for key in desired if key.casefold() == name.casefold()), None)
        if abs(value) < 0.0001:
            if existing is not None:
                del desired[existing]
        else:
            desired[existing or name] = value
    if after_map != desired:
        raise AssertionError(f"customMorphs mismatch: expected {desired!r}, got {after_map!r}")

    before_without = dict(before)
    after_without = dict(after)
    before_without.pop("customMorphs", None)
    after_without.pop("customMorphs", None)
    if before_without != after_without:
        raise AssertionError("unrelated root JSON fields changed")
    print("RESULT PASS customMorphs semantic patch, unknown-name retention, and unrelated-field preservation")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
