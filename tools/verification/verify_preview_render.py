"""Independent structural and pixel-content verifier for a Blender PNG preview."""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import zlib
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read_png(path: Path) -> tuple[int, int, bytes]:
    data = path.read_bytes()
    require(data[:8] == b"\x89PNG\r\n\x1a\n", "preview output is not a PNG")
    offset = 8
    width = height = None
    bit_depth = color_type = interlace = None
    compressed = bytearray()
    while offset < len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        offset += 12 + length
        if kind == b"IHDR":
            width, height, bit_depth, color_type, _, _, interlace = struct.unpack(">IIBBBBB", payload)
        elif kind == b"IDAT":
            compressed.extend(payload)
        elif kind == b"IEND":
            break
    require(width and height and bit_depth == 8 and color_type == 6 and interlace == 0,
            "preview PNG must be non-interlaced 8-bit RGBA")
    raw = zlib.decompress(compressed)
    stride = width * 4
    require(len(raw) == height * (stride + 1), "preview PNG scanline payload length drifted")
    pixels = bytearray(height * stride)
    previous = bytearray(stride)
    cursor = 0
    for row in range(height):
        filter_type = raw[cursor]
        cursor += 1
        current = bytearray(raw[cursor:cursor + stride])
        cursor += stride
        for index in range(stride):
            left = current[index - 4] if index >= 4 else 0
            above = previous[index]
            upper_left = previous[index - 4] if index >= 4 else 0
            if filter_type == 1:
                current[index] = (current[index] + left) & 0xFF
            elif filter_type == 2:
                current[index] = (current[index] + above) & 0xFF
            elif filter_type == 3:
                current[index] = (current[index] + ((left + above) // 2)) & 0xFF
            elif filter_type == 4:
                estimate = left + above - upper_left
                distances = (abs(estimate - left), abs(estimate - above), abs(estimate - upper_left))
                predictor = (left, above, upper_left)[distances.index(min(distances))]
                current[index] = (current[index] + predictor) & 0xFF
            elif filter_type != 0:
                raise ValueError(f"unsupported PNG filter {filter_type}")
        pixels[row * stride:(row + 1) * stride] = current
        previous = current
    return width, height, bytes(pixels)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--png", required=True, type=Path)
    parser.add_argument("--repeat", type=Path)
    parser.add_argument("--status", required=True, type=Path)
    parser.add_argument("--width", required=True, type=int)
    parser.add_argument("--height", required=True, type=int)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()

    status = json.loads(args.status.read_text(encoding="utf-8-sig"))
    image = status.get("renderedImage", status)
    width, height, pixels = read_png(args.png)
    require((width, height) == (args.width, args.height), "preview PNG dimensions drifted")
    digest = hashlib.sha256(args.png.read_bytes()).hexdigest()
    require(str(image.get("path", image.get("output", ""))).casefold() == str(args.png.resolve()).casefold(),
            "renderer status output path drifted")
    require(image["sha256"].casefold() == digest.casefold(), "renderer status hash drifted")
    require(image["width"] == width and image["height"] == height and image["meshCount"] > 0,
            "renderer status dimensions or mesh count drifted")
    require(image.get("edition") == args.edition, "renderer status edition binding drifted")
    require(image.get("deformationMode") in {"nif-skinned-evaluated", "nif-mesh-evaluated"},
            "renderer status deformation mode is not admitted")
    require(isinstance(image.get("armatureCount", 0), int) and image.get("armatureCount", 0) >= 0,
            "renderer status armature count is invalid")
    require(len(set(pixels[index:index + 4] for index in range(0, len(pixels), 4))) > 1,
            "preview pixels are uniform")
    if args.repeat is not None:
        repeat_width, repeat_height, repeat_pixels = read_png(args.repeat)
        require((repeat_width, repeat_height) == (width, height), "repeated preview dimensions changed")
        require(hashlib.sha256(args.repeat.read_bytes()).hexdigest() == digest,
                "repeated preview render is not deterministic")
        require(repeat_pixels == pixels, "repeated preview pixels changed")
    print(f"PREVIEW RENDER INDEPENDENT PASS output={args.png} size={width}x{height} meshCount={image['meshCount']}")


if __name__ == "__main__":
    main()
