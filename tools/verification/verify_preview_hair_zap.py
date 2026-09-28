"""Independently verify copied-asset preview hair-partition masking."""

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


def verify(scene_path: Path, image_path: Path, mode: str, compare: Path | None, edition: str) -> None:
    scene = read(scene_path)
    image = scene.get("renderedImage")
    zap = scene.get("hairZap")
    require(scene["edition"] == edition, f"hair-zap fixture must bind {edition}")
    require(image and zap, "scene did not preserve hair-zap and rendered-image artifacts")
    require(image["path"].casefold() == str(image_path.resolve()).casefold(), "image path is not bound")
    image_hash = hashlib.sha256(image_path.read_bytes()).hexdigest()
    require(image["sha256"].casefold() == image_hash.casefold(), "image hash is not bound")
    data = image_path.read_bytes()
    require(data[:8] == b"\x89PNG\r\n\x1a\n" and data[12:16] == b"IHDR", "output is not a PNG")
    require(struct.unpack(">II", data[16:24]) == (128, 128), "hair-zap preview dimensions drifted")
    if mode == "all":
        require(zap["renderHeadwear"] and zap["topCovered"] and zap["longCovered"],
                "all-partition headwear mask was not bound")
        require(image["hairZapApplied"] and image["hairZapAffectedMeshCount"] > 0 and
                image["hairZapRemovedFaceCount"] > 0, "all-partition mask did not remove copied hair faces")
    elif mode == "partial":
        require(zap["renderHeadwear"] and zap["topCovered"] and not zap["longCovered"],
                "partial top-only mask was not bound")
        require(image["hairZapApplied"] and image["hairZapAffectedMeshCount"] > 0 and
                image["hairZapRemovedFaceCount"] > 0, "top-only mask did not remove copied hair faces")
    elif mode == "disabled":
        require(not zap["topCovered"] and not zap["longCovered"], "disabled headwear mask remained active")
        require(not image["hairZapApplied"] and image["hairZapAffectedMeshCount"] == 0 and
                image["hairZapRemovedFaceCount"] == 0, "disabled mask changed copied geometry")
    elif mode == "fo4":
        require(edition == "fallout4", "FO4 mode must bind Fallout 4")
        require(zap["renderHeadwear"] and zap["topCovered"] and zap["longCovered"],
                "FO4 partition headwear mask was not bound")
        require(image["hairZapApplied"] and image["hairZapAffectedMeshCount"] > 0 and
                image["hairZapRemovedFaceCount"] > 0, "FO4 partition mask did not remove copied hair faces")
    else:
        raise ValueError(f"unknown mode {mode}")
    if compare is not None:
        other = read(compare)
        require(other["renderedImage"]["sha256"].casefold() != image_hash.casefold(),
                "hair-zap and comparison images unexpectedly match")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--scene", type=Path, required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--mode", choices=("all", "partial", "disabled", "fo4"), required=True)
    parser.add_argument("--edition", choices=("fallout4", "skyrimse"), default="skyrimse")
    parser.add_argument("--compare", type=Path)
    args = parser.parse_args()
    verify(args.scene, args.image, args.mode, args.compare, args.edition)
    print(f"PREVIEW HAIR-ZAP INDEPENDENT PASS mode={args.mode} image={args.image}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"PREVIEW HAIR-ZAP INDEPENDENT FAIL: {error}")
