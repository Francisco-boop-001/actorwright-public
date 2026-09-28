"""Independently verify a materialized K-local FaceGen package."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load(path: Path) -> dict:
    resolved = path.resolve()
    try:
        resolved.relative_to(WORKSPACE)
    except ValueError:
        fail("evidence path outside K workspace")
    try:
        value = json.loads(resolved.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    if not isinstance(value, dict):
        fail("JSON root must be an object")
    return value


def hash_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--package-root", required=True, type=Path)
    parser.add_argument("--source-data", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--anchor-plugin", required=True)
    args = parser.parse_args()

    response = load(args.response)
    if response.get("written") is not True:
        fail("pack did not report written=true")
    artifact = response.get("artifact")
    if not isinstance(artifact, dict) or artifact.get("artifactKind") != "facegen-pack":
        fail("artifact kind")
    package = args.package_root.resolve()
    source_data = args.source_data.resolve()
    if artifact.get("edition") != args.edition or artifact.get("anchorPlugin") != args.anchor_plugin:
        fail("package identity")
    files = artifact.get("files")
    if not isinstance(files, list) or not files:
        fail("files")
    if artifact.get("noWriteToSource") is not True or artifact.get("runtimeProof") is not False:
        fail("boundary flags")
    seen: set[str] = set()
    for item in files:
        if not isinstance(item, dict):
            fail("file entry shape")
        relative = item.get("relativePath")
        source_relative = item.get("sourcePath")
        if not isinstance(relative, str) or not isinstance(source_relative, str):
            fail("file paths")
        if relative in seen or not relative.startswith("Data/") or not source_relative.startswith("Data/"):
            fail("file path safety or duplicate")
        seen.add(relative)
        output = package.joinpath(*relative.split("/"))
        source = source_data.joinpath(*source_relative.split("/")[1:])
        if not output.is_file() or not source.is_file():
            fail(f"missing file {relative}")
        output_hash = hash_file(output)
        source_hash = hash_file(source)
        declared = item.get("sha256", {}).get("value", "")
        if output_hash != source_hash or declared.lower() != output_hash:
            fail(f"hash mismatch {relative}")
        if item.get("size") != output.stat().st_size:
            fail(f"size mismatch {relative}")
    manifest = package / "facegen-pack.json"
    if not manifest.is_file():
        fail("package manifest missing")
    if load(manifest).get("artifactKind") != "facegen-pack":
        fail("package manifest artifact")
    diagnostics = response.get("diagnostics")
    if not isinstance(diagnostics, list) or any(item.get("severity") in {"error", "Error"} for item in diagnostics):
        fail("unexpected error diagnostic")
    print(f"RESULT PASS edition={args.edition} files={len(files)} hashes=verified")


if __name__ == "__main__":
    main()
