"""Independent PIRT/TRI and preset oracle for BodySlide slider resolution."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
from pathlib import Path


EXCLUDED = {"weightthin", "weightmuscular", "weightfat"}


class Cursor:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.position = 0

    @property
    def remaining(self) -> int:
        return len(self.data) - self.position

    def take(self, count: int) -> bytes:
        if count < 0 or self.remaining < count:
            raise AssertionError("PIRT field exceeds the input boundary")
        value = self.data[self.position : self.position + count]
        self.position += count
        return value

    def u8(self) -> int:
        return self.take(1)[0]

    def u16(self) -> int:
        return struct.unpack("<H", self.take(2))[0]

    def i16(self) -> int:
        return struct.unpack("<h", self.take(2))[0]

    def f32(self) -> float:
        value = struct.unpack("<f", self.take(4))[0]
        if not math.isfinite(value):
            raise AssertionError("PIRT multiplier is not finite")
        return value

    def name(self, kind: str) -> str:
        raw = self.take(self.u8())
        if any(value > 0x7F for value in raw):
            raise AssertionError(f"PIRT {kind} name is not ASCII")
        return raw.decode("ascii")


def parse_tri(path: Path) -> tuple[dict[str, dict[str, tuple[str, list[tuple[int, float, float, float]]]]], list[str]]:
    data = path.read_bytes()
    if data[:4] != b"PIRT":
        raise AssertionError("TRI is not PIRT")
    cursor = Cursor(data[4:])
    shapes: dict[str, dict[str, tuple[str, list[tuple[int, float, float, float]]]]] = {}
    duplicate: list[str] = []
    for morph_type in ("position", "uv"):
        for _ in range(cursor.u16()):
            shape_name = cursor.name("shape")
            morphs = shapes.setdefault(shape_name, {})
            for _ in range(cursor.u16()):
                morph_name = cursor.name("morph")
                multiplier = cursor.f32()
                offsets: list[tuple[int, float, float, float]] = []
                for _ in range(cursor.u16()):
                    index = cursor.u16()
                    x = cursor.i16() * multiplier
                    y = cursor.i16() * multiplier
                    z = cursor.i16() * multiplier if morph_type == "position" else 0.0
                    if x != 0.0 or y != 0.0 or z != 0.0:
                        offsets.append((index, x, y, z))
                key = morph_name.casefold()
                if not offsets or key in morphs:
                    if offsets and key in morphs:
                        duplicate.append(f"{shape_name}/{morph_name}")
                    continue
                morphs[key] = (morph_name, offsets)
    if cursor.remaining:
        raise AssertionError("PIRT contains trailing bytes")
    return shapes, duplicate


def preset_values(path: Path, edition: str) -> dict[str, float]:
    root = json.loads(path.read_text(encoding="utf-8"))
    if edition == "fallout4":
        return {str(name): float(value) for name, value in root.get("BodyMorphs", {}).items()}
    values: dict[str, float] = {}
    for item in root.get("bodyMorphs", []):
        keys = item.get("keys", [])
        if not keys:
            continue
        values[str(item["name"])] = float(keys[0]["value"])
    return values


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tri", required=True, type=Path)
    parser.add_argument("--preset", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    response = json.loads(args.response.read_text(encoding="utf-8-sig"))
    assert response["isValid"], "CLI response is invalid"
    edition = response["edition"]
    shapes, duplicates = parse_tri(args.tri)
    expected_values = preset_values(args.preset, edition)
    actual_values = {item["name"]: item["value"] for item in response["requested"]}
    assert actual_values == expected_values, (actual_values, expected_values)
    assert response["triSha256"] == hashlib.sha256(args.tri.read_bytes()).hexdigest()
    assert any(item["code"] == "body-tri-duplicate-morph" for item in response["diagnostics"]) == bool(duplicates)
    expected_channels: list[tuple[str, str, float, list[tuple[int, float, float, float]]]] = []
    missing: set[str] = set()
    excluded: set[str] = set()
    for slider, weight in expected_values.items():
        if abs(weight) < 0.001:
            continue
        folded = slider.casefold()
        if folded in EXCLUDED or (folded.startswith("morphregion") and folded[11:].isdigit()):
            excluded.add(slider)
            continue
        found = False
        for shape, morphs in shapes.items():
            morph = morphs.get(folded)
            if morph is None:
                continue
            found = True
            expected_channels.append((shape, morph[0], weight, morph[1]))
        if not found:
            missing.add(slider)
    actual_channels = response["channels"]
    assert len(actual_channels) == len(expected_channels)
    for actual, expected in zip(actual_channels, expected_channels):
        shape, slider, weight, offsets = expected
        assert actual["shape"] == shape and actual["slider"] == slider
        assert math.isclose(actual["weight"], weight, rel_tol=0, abs_tol=1e-6)
        actual_offsets = [(item["vertexIndex"], item["x"], item["y"], item["z"]) for item in actual["offsets"]]
        assert actual_offsets == offsets, (actual_offsets, offsets)
    assert set(response["missingSliders"]) == missing
    assert set(response["excludedSliders"]) == excluded
    print(f"BODysLIDE PIRT INDEPENDENT PASS edition={edition} channels={len(actual_channels)}")


if __name__ == "__main__":
    main()
