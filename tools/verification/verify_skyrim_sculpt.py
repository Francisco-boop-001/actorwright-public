#!/usr/bin/env python3
"""Independently verify a RaceMenu morphs.sculpt JSON patch."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def hash_value(value: object) -> object:
    return value.get("value") if isinstance(value, dict) else value


def sculpt(document: dict) -> tuple[int, list[dict]]:
    morphs = document.get("morphs")
    if not isinstance(morphs, dict):
        raise AssertionError("morphs is not an object")
    divisor = morphs.get("sculptDivisor", 10_000)
    if not isinstance(divisor, int) or isinstance(divisor, bool) or divisor < 1:
        raise AssertionError("invalid sculptDivisor")
    blocks = morphs.get("sculpt", [])
    if not isinstance(blocks, list):
        raise AssertionError("morphs.sculpt is not an array")
    return divisor, blocks


def expected_blocks(document: dict, patch: dict) -> list[dict]:
    _, before_blocks = sculpt(document)
    if not isinstance(patch, dict) or not isinstance(patch.get("parts"), list):
        raise AssertionError("expected patch is not a parts object")
    result: list[dict] = []
    divisor = patch["divisor"]
    for part in patch["parts"]:
        if not isinstance(part, dict):
            raise AssertionError("patch part is not an object")
        rows = []
        for vertex in part["verts"]:
            rows.append([
                vertex["index"],
                int(round(float(vertex["dx"]) * divisor)),
                int(round(float(vertex["dy"]) * divisor)),
                int(round(float(vertex["dz"]) * divisor)),
            ])
        result.append({"host": part["host"], "vertices": part["vertices"], "data": rows})
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--json", type=Path, required=True, help="CLI evidence JSON")
    parser.add_argument("--expected", type=Path, required=True, help="typed sculpt patch JSON")
    args = parser.parse_args()

    before = json.loads(args.before.read_text(encoding="utf-8-sig"))
    after = json.loads(args.after.read_text(encoding="utf-8-sig"))
    evidence = json.loads(args.json.read_text(encoding="utf-8-sig"))
    patch = json.loads(args.expected.read_text(encoding="utf-8-sig"))
    if evidence.get("applied") is not True:
        raise AssertionError("CLI evidence does not report applied=true")
    if hash_value(evidence.get("inputSha256")) != sha256(args.before):
        raise AssertionError("CLI evidence input hash mismatch")
    if hash_value(evidence.get("outputSha256")) != sha256(args.after):
        raise AssertionError("CLI evidence output hash mismatch")
    if not isinstance(before, dict) or not isinstance(after, dict):
        raise AssertionError("preset root is not an object")

    divisor, before_blocks = sculpt(before)
    after_divisor, after_blocks = sculpt(after)
    expected_divisor = patch.get("divisor")
    if after_divisor != expected_divisor:
        raise AssertionError(f"sculptDivisor mismatch: expected {expected_divisor}, got {after_divisor}")
    desired = expected_blocks(before, patch)
    if after_blocks != desired:
        raise AssertionError(f"sculpt block mismatch: expected {desired!r}, got {after_blocks!r}")
    if not before_blocks or len(after_blocks) < 2:
        raise AssertionError("fixture did not exercise replacement and multiple shape blocks")

    before_without = dict(before)
    after_without = dict(after)
    before_morphs = dict(before_without.pop("morphs"))
    after_morphs = dict(after_without.pop("morphs"))
    before_morphs.pop("sculpt", None)
    before_morphs.pop("sculptDivisor", None)
    after_morphs.pop("sculpt", None)
    after_morphs.pop("sculptDivisor", None)
    if before_without != after_without or before_morphs != after_morphs:
        raise AssertionError("unrelated root or morphs fields changed")
    print("RESULT PASS RaceMenu sculpt divisor, per-shape host/vertex rows, and unrelated-field preservation")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
