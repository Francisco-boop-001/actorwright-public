"""Independently verify the copied FO4 slot-32 FaceGen-head cull contract."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def verify(scene_path: Path, image_path: Path, response_path: Path | None) -> None:
    scene = read(scene_path)
    image = scene.get("renderedImage")
    zap = scene.get("hairZap")
    require(scene.get("edition") == "fallout4", "face-cull fixture must bind Fallout 4")
    require(image and zap, "scene did not preserve face-cull artifacts")
    require(zap.get("renderHeadwear") and zap.get("faceGenHeadCovered"),
            "slot 32 was not bound as a rendered FaceGen-head cull")
    require(not zap.get("topCovered") and not zap.get("longCovered"),
            "slot 32 unexpectedly zapped a hair partition")
    require(Path(image["path"]).resolve() == image_path.resolve(), "image path is not bound")
    require(image["sha256"].casefold() == hashlib.sha256(image_path.read_bytes()).hexdigest().casefold(),
            "image hash is not bound")
    require(image.get("faceCullApplied") and image.get("faceCullAffectedMeshCount", 0) > 0,
            "render adapter did not prove a copied face mesh was culled")
    if response_path is not None:
        response = read(response_path)
        require(response.get("written"), "NIF export did not report a written artifact")
        require(response.get("faceCullApplied") and response.get("faceCullAffectedMeshCount", 0) > 0,
                "NIF export adapter did not prove a copied face mesh was culled")
        require(response.get("hairZap") and response["hairZap"].get("faceGenHeadCovered"),
                "NIF export response lost the slot-32 plan")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--scene", type=Path, required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--response", type=Path)
    args = parser.parse_args()
    verify(args.scene, args.image, args.response)
    print(f"PREVIEW FACE-CULL INDEPENDENT PASS scene={args.scene}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"PREVIEW FACE-CULL INDEPENDENT FAIL: {error}")
