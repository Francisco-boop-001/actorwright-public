"""Independent oracle for deterministic preview outfit/morph variant selection."""

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
    assert artifact == repeat, "Repeated preview variant artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "preview-scene-semantic-build"
    assert artifact["variant"] == {"id": "battle", "outfit": "M2FixtureFO4.esp|0x00000801", "includedAssetCount": 2, "includedMorphCount": 1}
    assert artifact["includedAssetCount"] == 2
    assert artifact["visibleAssetCount"] == 2
    assert artifact["appliedMorphCount"] == 1
    assert [item["included"] for item in artifact["assets"]] == [False, True, True]
    assert [item["included"] for item in artifact["morphs"]] == [True, False]
    assert artifact["morphs"][0]["value"] == 0.75
    assert response["written"] is True and response["variantId"] == "battle"
    assert response["includedAssetCount"] == 2
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"PREVIEW VARIANT INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
