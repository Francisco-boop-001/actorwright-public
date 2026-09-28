"""Independent oracle for the semantic FaceGeom build artifact."""

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

    assert artifact == repeat, "Repeated semantic FaceGeom artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest() == artifact["inputManifestSha256"]
    assert artifact["artifactKind"] == "facegeom-semantic-build"
    assert artifact["edition"] == manifest["edition"]
    assert artifact["npcFormId"] == manifest["npcFormId"]
    included = [shape for shape in manifest["shapes"] if shape["role"] == "head" and shape["applicable"]
                and shape["includedInOutput"] and shape["vertexCount"] > 0]
    assert len(included) == sum(shape["included"] for shape in artifact["shapes"])
    assert artifact["shapes"][0]["decision"] == "included-head-shape"
    assert artifact["headParts"] == manifest["headParts"]
    assert artifact["morphs"] == manifest["morphs"]
    assert artifact["textureRoutes"] == manifest["textureRoutes"]
    assert response["written"] is True
    assert response["artifactKind"] == "facegeom-semantic-build"
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"FACEGEOM BUILD INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
