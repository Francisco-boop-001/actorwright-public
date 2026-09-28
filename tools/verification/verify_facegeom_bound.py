"""Independent verifier for provider-bound canonical FaceGeom output."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from verify_facegeom_binary import parse_nif, require


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def value(item):
    return item.get("value") if isinstance(item, dict) else item


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--canonical", required=True)
    args = parser.parse_args()

    data_root = args.data_root.resolve()
    output_root = args.output_root.resolve()
    canonical = args.canonical.replace("\\", "/")
    source = (data_root / Path(*canonical.split("/"))).resolve()
    output = (output_root / Path(*canonical.split("/"))).resolve()
    require(data_root.is_dir(), "copied Data root is missing")
    require(output_root.is_dir(), "output root is missing")
    require(not (data_root == output_root or data_root in output_root.parents or output_root in data_root.parents),
            "source and output roots overlap")
    require(source.is_relative_to(data_root) and source.is_file(), "canonical source is missing")
    require(output.is_relative_to(output_root) and output.is_file(), "canonical output is missing")

    response = load(args.json)
    artifact = response.get("artifact")
    require(response.get("written") is True and isinstance(artifact, dict), "provider-bound response is not successful")
    require(artifact.get("artifactKind") == "facegeom-provider-bound-build", "unexpected provider-bound artifact kind")
    require(artifact.get("edition") == args.edition, "edition is not bound")
    require(artifact.get("originatingPlugin") == args.plugin and artifact.get("winningPlugin") == args.plugin,
            "plugin provenance is not bound")
    require(artifact.get("providerKind") == "faceGeom", "provider kind is not FaceGeom")
    require(value(artifact.get("providerPath")) == canonical and value(artifact.get("outputPath")) == canonical,
            "canonical provider/output paths are not preserved")

    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    output_hash = hashlib.sha256(output.read_bytes()).hexdigest()
    require(artifact.get("providerSha256", "").casefold() == source_hash.casefold(), "provider hash is not bound")
    require(response.get("outputSha256", "").casefold() == output_hash.casefold(), "response output hash is not bound")

    build = artifact.get("buildArtifact")
    require(isinstance(build, dict), "nested binary build evidence is missing")
    require(Path(build.get("outputPath", "")).resolve() == output, "nested output path is not bound")
    require(build.get("outputSha256", "").casefold() == output_hash.casefold(), "nested output hash is not bound")
    require(build.get("inputSourceSha256", "").casefold() == source_hash.casefold(), "nested source hash is not bound")
    require(build.get("runtimeAuthority") is False, "sandbox output was promoted to runtime authority")
    require(build.get("importMode") == "nif-plus-tri-bake", "unexpected import mode")
    require(build.get("baseVertexSha256") != build.get("bakedVertexSha256"), "morph bake did not change vertices")
    require(any(item.get("value", 0) != 0 for item in build.get("morphs", [])), "non-zero morph evidence is missing")

    for row in build.get("triFiles", []):
        tri = (data_root / Path(*row["path"].replace("\\", "/").split("/"))).resolve()
        require(tri.is_relative_to(data_root) and tri.is_file(), "TRI dependency escapes copied Data")
        require(hashlib.sha256(tri.read_bytes()).hexdigest().casefold() == row["sha256"].casefold(),
                "TRI dependency hash is not bound")
    require(build.get("triFiles"), "TRI dependency evidence is missing")

    nif = parse_nif(output)
    require(nif["shapes"] > 0 and build.get("vertexCount", 0) > 0, "output NIF geometry is missing")
    require(all(item.get("severity") != "error" for item in response.get("diagnostics", [])),
            "provider-bound response contains an error diagnostic")
    print(f"FACEGEOM BOUND INDEPENDENT PASS edition={args.edition} canonical={canonical} blocks={nif['blocks']}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"FACEGEOM BOUND INDEPENDENT FAIL: {error}")
