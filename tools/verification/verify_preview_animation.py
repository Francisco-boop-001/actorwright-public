"""Independent oracle for semantic preview animation selection evidence."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--repeat", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = load(args.manifest)
    artifact = load(args.output)
    repeat = load(args.repeat)
    response = load(args.response)
    assert artifact == repeat, "Repeated animation scene artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "preview-scene-semantic-build"
    animation = artifact["animation"]
    assert animation["id"] == "walk"
    assert animation["path"] == "animations/walk.hkx"
    assert animation["skeleton"] == "meshes/skeleton.nif"
    assert animation["frame"] == 2
    assert abs(animation["timeSeconds"] - (2 / 30)) < 1e-6
    assert animation["playbackRate"] == 24
    assert animation["playing"] is True
    assert response["written"] is True
    assert response["animationId"] == "walk"
    assert response["animationFrame"] == 2
    assert response["animationPlaybackRate"] == 24
    assert response["animationPlaying"] is True
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"PREVIEW ANIMATION INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
