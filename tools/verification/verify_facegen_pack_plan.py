#!/usr/bin/env python3
"""Independent verifier for the read-only FaceGen pack-bundle plan."""
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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--debug", action="store_true")
    parser.add_argument("--shared-neutral", action="store_true")
    args = parser.parse_args()

    if not under_workspace(args.json) or not under_workspace(args.data_root):
        print(json.dumps({"status": "FAIL", "errors": ["evidence and data roots must remain under K workspace"]}, indent=2))
        return 1

    doc = json.loads(args.json.read_text(encoding="utf-8-sig"))
    errors: list[str] = []
    if doc.get("planned") is not True or not doc.get("artifact"):
        errors.append("plan was not emitted")
    artifact = doc.get("artifact") or {}
    if artifact.get("edition") != args.edition:
        errors.append("edition mismatch")
    if artifact.get("anchorPlugin") != args.plugin:
        errors.append("anchor plugin mismatch")
    if artifact.get("debugSandbox") is not args.debug:
        errors.append("debug flag mismatch")
    if artifact.get("usesSharedNeutralDetail") is not args.shared_neutral:
        errors.append("shared-neutral flag mismatch")

    entries = artifact.get("entries", [])
    expected = 4 if args.edition == "fallout4" else 4 + (1 if args.shared_neutral else 0)
    if len(entries) != expected:
        errors.append(f"expected {expected} entries, got {len(entries)}")
    if artifact.get("wouldCommit") is not True:
        errors.append("complete fixture did not report wouldCommit=true")
    roles = {entry.get("kind"): entry.get("archiveRole") for entry in entries}
    if roles.get("faceGeom") != "main":
        errors.append("FaceGeom was not assigned to the main archive role")
    for entry in entries:
        source_value = entry["sourcePath"]
        if isinstance(source_value, dict):
            source_value = source_value.get("value")
        canonical_value = entry.get("canonicalEntryPath")
        if isinstance(canonical_value, dict):
            canonical_value = canonical_value.get("value")
        if not isinstance(canonical_value, str) or canonical_value.endswith("_2.nif") or canonical_value.endswith("_2.dds"):
            errors.append("canonical entry retained a debug suffix")
        if args.debug and isinstance(source_value, str) and not (source_value.endswith("_2.nif") or source_value.endswith("_2.dds")):
            errors.append("debug source omitted the _2 suffix")
        source = args.data_root / Path(source_value.replace("/", "\\"))
        status = entry.get("status")
        if status == "present":
            if not source.is_file():
                errors.append(f"present source is absent: {source}")
                continue
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            digest_value = entry.get("sha256")
            if isinstance(digest_value, dict):
                digest_value = digest_value.get("value")
            if digest != digest_value:
                errors.append(f"hash mismatch: {source}")
        elif status == "missingRequired":
            errors.append(f"required source missing: {source}")
        elif status != "missingOptional":
            errors.append(f"unknown source status: {status}")
        if entry.get("archiveRole") not in ("main", "textures"):
            errors.append("invalid archive role")

    if errors:
        print(json.dumps({"status": "FAIL", "errors": errors}, indent=2))
        return 1
    print(json.dumps({"status": "PASS", "entries": len(entries), "hashes": "verified"}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
