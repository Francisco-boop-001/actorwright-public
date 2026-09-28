#!/usr/bin/env python3
"""Independently verify a provider-bound FaceTint build response."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


def under_workspace(path: Path) -> bool:
    try:
        path.resolve().relative_to(WORKSPACE)
        return True
    except ValueError:
        return False


def value(item):
    return item.get("value") if isinstance(item, dict) else item


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--canonical", required=True)
    args = parser.parse_args()

    errors: list[str] = []
    for path, label in ((args.json, "evidence"), (args.data_root, "data"), (args.output_root, "output")):
        if not under_workspace(path):
            errors.append(f"{label} root is outside K workspace")
    try:
        args.output_root.resolve().relative_to(args.data_root.resolve())
        errors.append("output root overlaps data root")
    except ValueError:
        try:
            args.data_root.resolve().relative_to(args.output_root.resolve())
            errors.append("output root overlaps data root")
        except ValueError:
            pass

    doc = json.loads(args.json.read_text(encoding="utf-8-sig"))
    if doc.get("written") is not True or not doc.get("artifact"):
        errors.append("bound build was not written")
    artifact = doc.get("artifact") or {}
    if artifact.get("artifactKind") != "facetint-provider-bound-build":
        errors.append("artifact kind mismatch")
    if artifact.get("edition") != args.edition:
        errors.append("edition mismatch")
    if artifact.get("winningPlugin") != args.plugin or artifact.get("originatingPlugin") != args.plugin:
        errors.append("plugin provenance mismatch")
    expected_kind = "faceCustomizationDiffuse" if args.edition == "fallout4" else "faceTint"
    if artifact.get("providerKind") != expected_kind:
        errors.append("provider kind mismatch")
    if value(artifact.get("providerPath")) != args.canonical or value(artifact.get("outputPath")) != args.canonical:
        errors.append("canonical provider/output path mismatch")

    source = args.data_root / Path(args.canonical.replace("/", "\\"))
    output = args.output_root / Path(args.canonical.replace("/", "\\"))
    if not source.is_file() or not output.is_file():
        errors.append("provider source or canonical output DDS is missing")
    else:
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        output_hash = hashlib.sha256(output.read_bytes()).hexdigest()
        if source_hash != artifact.get("providerSha256", "").lower():
            errors.append("provider hash mismatch")
        if output_hash != value(doc.get("textureOutputSha256")):
            errors.append("top-level output hash mismatch")
        build = artifact.get("buildArtifact") or {}
        if build.get("rasterSource") != "provider-sampled":
            errors.append("build artifact did not record provider sampling")
        if value(build.get("textureDdsSha256")) != output_hash:
            errors.append("nested output hash mismatch")
        if value(build.get("textureOutputPath")) != str(output):
            errors.append("nested output path mismatch")
        if not output.read_bytes().startswith(b"DDS "):
            errors.append("output is not a DDS")

    if errors:
        print(json.dumps({"status": "FAIL", "errors": errors}, indent=2))
        return 1
    print(json.dumps({"status": "PASS", "edition": args.edition, "provider": args.canonical, "hashes": "verified"}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
