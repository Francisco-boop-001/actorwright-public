#!/usr/bin/env python3
"""Independently verify a hash-bound FaceGen pack deployment."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path


LAB_ROOT = r"K:\ExampleWorkspace"


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def under_lab(path: Path) -> bool:
    try:
        return os.path.commonpath((str(path).lower(), LAB_ROOT.lower())) == LAB_ROOT.lower()
    except ValueError:
        return False


def load_manifest(path: Path) -> dict:
    manifest = path.resolve()
    if not under_lab(manifest) or not manifest.is_file() or manifest.is_symlink():
        fail("manifest-outside-k-or-missing")
    try:
        value = json.loads(manifest.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"manifest-invalid={exc}")
    if not isinstance(value, dict) or value.get("artifactKind") != "facegen-pack":
        fail("artifact-kind")
    return value


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--mode", choices=("deployed", "conflict", "unsafe"), default="deployed")
    args = parser.parse_args()
    manifest_path = args.manifest.resolve()
    data_root = args.data_root.resolve()
    if args.mode == "unsafe":
        if under_lab(data_root):
            fail("unsafe-data-root-is-under-k")
        print("RESULT PASS mode=unsafe destination-outside-k")
        return 0
    if not under_lab(data_root) or not data_root.is_dir() or data_root.is_symlink():
        fail("data-root-outside-k-or-missing")

    artifact = load_manifest(manifest_path)
    if artifact.get("edition") != args.edition or artifact.get("noWriteToSource") is not True or artifact.get("runtimeProof") is not False:
        fail("edition-or-boundary")
    package_root = manifest_path.parent
    if Path(artifact.get("outputRoot", "")).resolve() != package_root:
        fail("output-root-binding")
    entries = artifact.get("files")
    if not isinstance(entries, list) or not entries:
        fail("files")
    conflicts = 0
    for entry in entries:
        if not isinstance(entry, dict):
            fail("file-entry-shape")
        relative = entry.get("relativePath")
        declared = entry.get("sha256", {}).get("value", "")
        if not isinstance(relative, str) or not relative.startswith("Data/") or ".." in relative.split("/"):
            fail("relative-path")
        source = (package_root / Path(*relative.split("/"))).resolve()
        destination = (data_root / Path(*relative.split("/")[1:])).resolve()
        if not under_lab(source) or not source.is_file() or source.is_symlink():
            fail(f"source-missing={relative}")
        source_hash = digest(source)
        if source_hash != str(declared).lower() or source.stat().st_size != entry.get("size"):
            fail(f"source-binding={relative}")
        if destination.is_file() and not destination.is_symlink():
            destination_hash = digest(destination)
            if destination_hash != source_hash or destination.stat().st_size != source.stat().st_size:
                conflicts += 1
        elif destination.exists():
            conflicts += 1
        elif args.mode == "deployed":
            fail(f"destination-missing={relative}")

    if args.mode == "deployed" and conflicts:
        fail("unexpected-conflict")
    if args.mode == "conflict" and conflicts == 0:
        fail("conflict-not-observed")
    print(f"RESULT PASS edition={args.edition} mode={args.mode} files={len(entries)} conflicts={conflicts}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
