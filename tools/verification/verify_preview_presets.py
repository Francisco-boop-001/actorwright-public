"""Independent oracle for semantic preview camera/lighting preset evidence."""

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
    assert artifact == repeat, "Repeated preset scene artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "preview-scene-semantic-build"
    assert artifact["camera"] == {
        "id": "portrait",
        "version": 3,
        "yawDegrees": 15,
        "pitchDegrees": -4,
        "distance": 5.5,
        "fieldOfViewDegrees": 42,
    }
    assert artifact["lighting"]["id"] == "studio"
    assert artifact["lighting"]["version"] == 2
    assert artifact["lighting"]["ambientIntensity"] == 0.35
    assert artifact["lighting"]["lights"][0]["id"] == "key"
    assert response["written"] is True
    assert response["cameraPresetId"] == "portrait"
    assert response["cameraPresetVersion"] == 3
    assert response["lightingPresetId"] == "studio"
    assert response["lightingPresetVersion"] == 2
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"PREVIEW PRESETS INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
