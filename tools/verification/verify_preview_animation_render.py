"""Independent verifier for copied HKX animation application in preview rendering."""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def png_shape(path: Path) -> tuple[int, int]:
    data = path.read_bytes()
    require(data[:8] == b"\x89PNG\r\n\x1a\n", "animation preview output is not a PNG")
    require(data[12:16] == b"IHDR", "animation preview PNG is missing IHDR")
    return struct.unpack(">II", data[16:24])


def verify(scene_path: Path, image_path: Path, edition: str) -> None:
    scene = read(scene_path)
    image = scene["renderedImage"]
    animation = scene.get("animation")
    require(scene["edition"] == edition, "scene edition binding drifted")
    require(animation and animation["id"] == "idle", "animation selection is not bound")
    require(animation["frame"] == 1 and animation["playing"] is False, "animation frame/play state drifted")
    require(image["path"].casefold() == str(image_path.resolve()).casefold(), "image path is not bound")
    require(image["sha256"].casefold() == hashlib.sha256(image_path.read_bytes()).hexdigest().casefold(),
            "image hash is not bound")
    require(tuple(png_shape(image_path)) == (128, 128), "animation preview dimensions drifted")
    require(image["animationId"] == "idle" and image["animationApplied"] is True,
            "renderer did not prove HKX animation application")
    require(image["animationFrame"] == 1, "renderer animation frame drifted")
    require(image["animationPoseDeformed"] is True,
            "renderer did not prove the selected animation changed the evaluated pose")
    require(image["deformationMode"] == "nif-skinned-evaluated" and image["armatureCount"] > 0,
            "animation route did not preserve a NIF armature")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--scene", type=Path, required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--edition", choices=("fallout4", "skyrimse"), required=True)
    args = parser.parse_args()
    verify(args.scene, args.image, args.edition)
    print(f"PREVIEW ANIMATION RENDER INDEPENDENT PASS image={args.image}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"PREVIEW ANIMATION RENDER INDEPENDENT FAIL: {error}")
