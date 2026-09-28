"""Independent oracle for the bounded Skyrim overlay-fold fixture.

This intentionally does not import the C# compositor.  It checks source/output
hashes, the DDS header, the pinned layer order, and the expected BGRA8 pixels
using the source formulas in a small separate implementation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
from pathlib import Path


def clamp(value: float) -> float:
    return max(0.0, min(1.0, value))


def srgb_to_linear(value: float) -> float:
    value = clamp(value)
    return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4


def linear_to_srgb(value: float) -> float:
    value = clamp(value)
    return value * 12.92 if value <= 0.0031308 else 1.055 * value ** (1 / 2.4) - 0.055


def quantize(value: float) -> int:
    # Python round is ties-to-even, matching the C# and VB byte seams.
    return max(0, min(255, int(round(value * 255))))


def fold(request: dict) -> tuple[int, int, bytes, list[dict]]:
    base = request["base"]
    width, height = base["width"], base["height"]
    pixels = list(map(float, base["pixels"]))
    facetint = request.get("facetint")
    detail = request.get("detail")
    if facetint is not None:
        for pixel in range(width * height):
            offset = pixel * 4
            for channel, correction in enumerate((1 / 255, 0.0, 1 / 255)):
                base_linear = srgb_to_linear(pixels[offset + channel])
                detail_value = 0.5 if detail is None else float(detail["pixels"][offset + channel])
                softlight = base_linear * base_linear + 2 * base_linear * detail_value * (1 - base_linear)
                fg_tint = (float(facetint["pixels"][offset + channel]) + correction) * (255 / 64)
                pixels[offset + channel] = linear_to_srgb(softlight * fg_tint)

    skee = [layer for layer in request["layers"] if layer["source"].lower() == "skeemask"]
    face = [layer for layer in request["layers"] if layer["source"].lower() == "faceoverlay"]
    face.sort(key=lambda layer: (int(layer["node"].split("[Ovl", 1)[1][:-1]), request["layers"].index(layer)))
    ordered = skee + face
    for layer in ordered:
        color = list(map(float, layer["color"]))
        opacity = float(layer.get("opacity", 1.0))
        texture = layer.get("texture")
        texels = [] if texture is None else list(map(float, texture["pixels"]))
        for pixel in range(width * height):
            offset = pixel * 4
            tr, tg, tb, ta = (1.0, 1.0, 1.0, 1.0) if not texels else texels[offset:offset + 4]
            if layer["source"].lower() == "faceoverlay":
                red, green, blue, source_alpha, coverage = tr * color[0], tg * color[1], tb * color[2], ta, ta * opacity
            elif layer.get("layerType") == 1:
                red, green, blue, source_alpha, coverage = color[0], color[1], color[2], tr * color[3], tr * color[3] * opacity
            elif layer.get("layerType") == 2:
                red, green, blue, source_alpha, coverage = color[0], color[1], color[2], color[3], color[3] * opacity
            else:
                red, green, blue, source_alpha, coverage = tr * color[0], tg * color[1], tb * color[2], ta * color[3], ta * color[3] * opacity
            coverage = clamp(coverage)
            if coverage <= 0:
                continue
            pixels[offset] = red * coverage + pixels[offset] * (1 - coverage)
            pixels[offset + 1] = green * coverage + pixels[offset + 1] * (1 - coverage)
            pixels[offset + 2] = blue * coverage + pixels[offset + 2] * (1 - coverage)

    payload = bytearray()
    for pixel in range(width * height):
        offset = pixel * 4
        payload.extend((quantize(pixels[offset + 2]), quantize(pixels[offset + 1]),
                        quantize(pixels[offset]), quantize(pixels[offset + 3])))
    return width, height, bytes(payload), ordered


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    source = args.input.read_bytes()
    request = json.loads(source.decode("utf-8-sig"))
    response = json.loads(args.response.read_text(encoding="utf-8-sig"))
    output = args.output.read_bytes()
    if hashlib.sha256(source).hexdigest() != response.get("sourceSha256"):
        raise SystemExit("source hash mismatch")
    if hashlib.sha256(output).hexdigest() != response.get("outputSha256"):
        raise SystemExit("output hash mismatch")
    width, height, expected, ordered = fold(request)
    if output[:4] != b"DDS " or struct.unpack_from("<I", output, 12)[0] != height or struct.unpack_from("<I", output, 16)[0] != width:
        raise SystemExit("DDS header mismatch")
    if output[128:] != expected:
        raise SystemExit(f"pixel mismatch: {output[128:].hex()} != {expected.hex()}")
    actual_order = response.get("orderedLayers", [])
    expected_order = [{"source": "SkeeMask" if layer["source"].lower() == "skeemask" else "FaceOverlay",
                      "node": layer.get("node"), "sourceIndex": request["layers"].index(layer)}
                      for layer in ordered]
    if actual_order != expected_order:
        raise SystemExit(f"ordered layer mismatch: {actual_order} != {expected_order}")
    print(f"SSE OVERLAY FOLD INDEPENDENT PASS {args.output} sha256={hashlib.sha256(output).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
