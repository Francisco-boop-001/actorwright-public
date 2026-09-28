"""Independent oracle for Skyrim _0/_1 body-weight interpolation math."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path


SPARSE_SQUARED_THRESHOLD = 0.0000001


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.input.read_text(encoding="utf-8"))
    response = json.loads(args.response.read_text(encoding="utf-8-sig"))
    assert response["isValid"], "CLI response is invalid"
    assert response["edition"] == "skyrimse"
    assert response["sourceSha256"] == hashlib.sha256(args.input.read_bytes()).hexdigest()

    base = manifest["baseVertices"]
    twin = manifest["twinVertices"]
    flags = int(manifest["weightSliderFlags"])
    base_digit = int(manifest["baseDigit"])
    t = max(0.0, min(1.0, float(manifest["weightPercent"]) / 100.0))
    channel_weight = t if base_digit == 0 else 1.0 - t
    assert response["sliderEnabled"] == bool(flags & 0x02)
    if not (flags & 0x02):
        assert not response["applied"] and response["deltas"] == []
        print("SSE BODY WEIGHT INDEPENDENT PASS disabled-noop")
        return

    assert len(base) == len(twin)
    expected = []
    for index, (left, right) in enumerate(zip(base, twin)):
        delta = [float(right[i]) - float(left[i]) for i in range(3)]
        if sum(value * value for value in delta) < SPARSE_SQUARED_THRESHOLD:
            continue
        expected.append({"index": index, "delta": {"x": delta[0], "y": delta[1], "z": delta[2]}})
    assert math.isclose(response["clampedWeight"], t, rel_tol=0, abs_tol=1e-6)
    assert math.isclose(response["channelWeight"], channel_weight, rel_tol=0, abs_tol=1e-6)
    assert response["deltas"] == expected, (response["deltas"], expected)
    assert response["applied"] == bool(expected)
    print(f"SSE BODY WEIGHT INDEPENDENT PASS deltas={len(expected)} channelWeight={channel_weight}")


if __name__ == "__main__":
    main()
