#!/usr/bin/env python3
"""Independently verify the bounded face-pose resolver output."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def vec(values: list[float] | tuple[float, float, float]) -> tuple[float, float, float]:
    if len(values) != 3 or not all(math.isfinite(float(value)) for value in values):
        raise AssertionError("invalid vector")
    return tuple(float(value) for value in values)  # type: ignore[return-value]


def add(left: tuple[float, float, float], right: tuple[float, float, float]) -> tuple[float, float, float]:
    return tuple(a + b for a, b in zip(left, right))  # type: ignore[return-value]


def scale(value: tuple[float, float, float], amount: float) -> tuple[float, float, float]:
    return tuple(component * amount for component in value)  # type: ignore[return-value]


def lerp(slider: float, minimum: float, maximum: float) -> float:
    slider = max(-1.0, min(1.0, slider))
    return slider * maximum if slider >= 0 else -slider * minimum


def rotation(source: tuple[float, float, float]) -> tuple[float, float, float]:
    yaw, pitch, roll = (-value * math.pi / 180.0 for value in source)
    cz, sz = math.cos(yaw), math.sin(yaw)
    cy, sy = math.cos(pitch), math.sin(pitch)
    cx, sx = math.cos(roll), math.sin(roll)
    a11, a12, a13 = cy, 0.0, sy
    a21, a22, a23 = sx * sy, cx, -sx * cy
    a31, a32, a33 = -cx * sy, sx, cx * cy
    m11, m12, m13 = a11 * cz + a13 * 0, -a11 * sz + a13 * 0, a13
    m21, m22, m23 = a21 * cz + a22 * sz, -a21 * sz + a22 * cz, a23
    m31, m32, m33 = a31 * cz + a32 * sz, -a31 * sz + a32 * cz, a33
    r11, r12, r13 = m33, m32, m31
    r21, r22, r23 = m23, m22, m21
    r31, r32, r33 = m13, m12, m11
    angle = math.acos(max(-1.0, min(1.0, (r11 + r22 + r33 - 1.0) / 2.0)))
    sine = math.sin(angle)
    if abs(sine) < 0.0001:
        axis = ((r32 - r23) * 0.5, (r13 - r31) * 0.5, (r21 - r12) * 0.5)
        length = math.sqrt(sum(value * value for value in axis))
        if length > 0.000001:
            return tuple(value / length * angle for value in axis)  # type: ignore[return-value]
        return angle, 0.0, 0.0
    factor = angle / (2.0 * sine)
    return (r32 - r23) * factor, (r13 - r31) * factor, (r21 - r12) * factor


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    source = json.loads(args.input.read_text(encoding="utf-8-sig"))
    evidence = json.loads(args.evidence.read_text(encoding="utf-8-sig"))
    if evidence.get("resolved") is not True:
        raise AssertionError("CLI evidence does not report resolved=true")
    if evidence.get("sourceSha256") != sha256(args.input):
        raise AssertionError("source hash mismatch")
    if not isinstance(source, dict) or not isinstance(evidence, dict):
        raise AssertionError("source/evidence roots must be objects")

    fmin = source["facialMorphIntensity"] if source["facialMorphIntensity"] > 0 else 1.0
    regions = {region["id"]: region for region in source["regions"]}
    positions: dict[str, tuple[float, float, float]] = {}
    rotations: dict[str, tuple[float, float, float]] = {}
    scales: dict[str, tuple[float, float, float]] = {}
    for morph in source["faceMorphs"]:
        region = regions.get(morph["regionId"])
        if region is None:
            continue
        position, rotate, slider_scale = vec(morph["position"]), vec(morph["rotation"]), morph["scale"]
        if max(abs(value) for value in position + rotate) < 0.0001 and abs(slider_scale) < 0.0001:
            continue
        for bone in region["bones"]:
            target = "skin_" + bone["bone"]
            minimum, maximum = bone["min"], bone["max"]
            delta_position = tuple(lerp(position[i], minimum["position"][i], maximum["position"][i]) * fmin for i in range(3))
            delta_rotation = tuple(lerp(rotate[i], minimum["rotation"][i], maximum["rotation"][i]) * fmin for i in range(3))
            delta_scale = tuple(lerp(slider_scale, minimum["scale"][i], maximum["scale"][i]) * fmin for i in range(3))
            positions[target] = add(positions.get(target, (0.0, 0.0, 0.0)), delta_position)
            rotations[target] = add(rotations.get(target, (0.0, 0.0, 0.0)), delta_rotation)
            scales[target] = add(scales.get(target, (0.0, 0.0, 0.0)), delta_scale)

    actual_bones = evidence.get("bonePoses", [])
    expected_bones = []
    for target in sorted(positions, key=str.casefold):
        expected_bones.append((target, positions[target], rotation(rotations[target]), tuple(1.0 + value for value in scales[target])))
    if len(actual_bones) != len(expected_bones):
        raise AssertionError("bone count mismatch")
    for actual, (target, expected_position, expected_rotation, expected_scale) in zip(actual_bones, expected_bones):
        if actual["bone"] != target:
            raise AssertionError("bone ordering/name mismatch")
        for field, expected in (("position", expected_position), ("rotation", expected_rotation), ("scale", expected_scale)):
            got = vec([actual[field][axis] for axis in ("x", "y", "z")])
            if any(abs(a - b) > 1e-5 for a, b in zip(got, expected)):
                raise AssertionError(f"{field} mismatch for {target}: {got!r} != {expected!r}")

    vertex_sums: dict[int, tuple[float, float, float]] = {}
    for channel in source["vertexMorphs"]:
        for vertex in channel["vertices"]:
            vertex_sums[vertex["index"]] = add(vertex_sums.get(vertex["index"], (0.0, 0.0, 0.0)), scale(vec(vertex["delta"]), channel["weight"]))
    actual_vertices = evidence.get("vertexDeltas", [])
    if [item["index"] for item in actual_vertices] != sorted(vertex_sums):
        raise AssertionError("vertex ordering/index mismatch")
    for actual in actual_vertices:
        expected = vertex_sums[actual["index"]]
        got = vec([actual["delta"][axis] for axis in ("x", "y", "z")])
        if any(abs(a - b) > 1e-5 for a, b in zip(got, expected)):
            raise AssertionError(f"vertex {actual['index']} mismatch")
    expected_order = ["face-bones:region-declaration-order;bone-declaration-order"] + [
        f"vertex[{index}]:{channel['resolver']}/{channel['name']}" for index, channel in enumerate(source["vertexMorphs"])
    ]
    if evidence.get("combinationOrder") != expected_order:
        raise AssertionError("combination order mismatch")
    print("RESULT PASS face-bone FMIN/rotation math and declaration-ordered vertex accumulation")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
