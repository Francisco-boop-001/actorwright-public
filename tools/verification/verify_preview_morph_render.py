"""Independent oracle for hash-bound TRI-backed preview morph rendering."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def read(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def verify_image(status_path: Path, expected: Path, morph_names: list[str], deformed: bool) -> str:
    status = read(status_path)
    image = status["renderedImage"]
    assert Path(image["path"]).resolve() == expected.resolve()
    assert image["sha256"].casefold() == hashlib.sha256(expected.read_bytes()).hexdigest().casefold()
    assert image.get("morphNames", []) == morph_names
    assert image.get("morphDeformed", False) is deformed
    assert image.get("deformationMode") in {"nif-skinned-evaluated", "nif-mesh-evaluated"}
    assert isinstance(image.get("armatureCount", 0), int) and image.get("armatureCount", 0) >= 0
    if deformed:
        assert image["deformationMode"] == "nif-skinned-evaluated" and image["armatureCount"] > 0
    return image["sha256"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--neutral-status", type=Path, required=True)
    parser.add_argument("--neutral-image", type=Path, required=True)
    parser.add_argument("--morph-status", type=Path, required=True)
    parser.add_argument("--morph-image", type=Path, required=True)
    parser.add_argument("--repeat-status", type=Path, required=True)
    parser.add_argument("--repeat-image", type=Path, required=True)
    parser.add_argument("--morph-response", type=Path, required=True)
    args = parser.parse_args()

    neutral_hash = verify_image(args.neutral_status, args.neutral_image, [], False)
    morph_hash = verify_image(args.morph_status, args.morph_image, ["Aah"], True)
    repeat_hash = verify_image(args.repeat_status, args.repeat_image, ["Aah"], True)
    assert neutral_hash != morph_hash, "morph render did not change canonical pixels"
    assert morph_hash == repeat_hash, "repeated morph render was not deterministic"
    response = read(args.morph_response)
    assert response["written"] is True
    assert response["renderedMorphNames"] == ["Aah"]
    assert response["renderedMorphDeformed"] is True
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"PREVIEW MORPH RENDER INDEPENDENT PASS morphSha256={morph_hash}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
