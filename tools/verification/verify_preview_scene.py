"""Independent oracle for semantic preview scene evidence."""

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
    parser.add_argument("--visible-output", required=False, type=Path)
    parser.add_argument("--morph-output", required=False, type=Path)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = load(args.manifest)
    artifact = load(args.output)
    repeat = load(args.repeat)
    response = load(args.response)
    assert artifact == repeat, "Repeated preview scene artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "preview-scene-semantic-build"
    assert artifact["edition"] == manifest["edition"]
    assert artifact["npcFormId"] == "0x00000800"
    assert artifact["assetCount"] == len(manifest["assets"]) == 5
    assert [item["category"] for item in artifact["assets"]] == ["face", "body", "hair", "outfit", "accessory"]
    assert [item["category"] for item in artifact["categoryCounts"]] == ["face", "body", "hair", "outfit", "accessory"]
    assert all(len(item["sha256"]) == 64 for item in artifact["assets"])
    assert response["written"] is True
    assert response["artifactKind"] == "preview-scene-semantic-build"
    assert response["assetCount"] == 5
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    if args.visible_output:
        visible = load(args.visible_output)
        assert visible["assetCount"] == 5 and visible["visibleAssetCount"] == 2
        assert [item["visible"] for item in visible["assets"]] == [True, False, True, False, False]
    if args.morph_output:
        morph = load(args.morph_output)
        assert morph["appliedMorphCount"] == 1
        assert [item["applied"] for item in morph["morphs"]] == [True, False]
    print(f"PREVIEW SCENE INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
