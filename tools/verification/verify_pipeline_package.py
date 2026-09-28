#!/usr/bin/env python3
"""Independent verifier for bounded preset-to-NPC package manifests."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


HEX64 = re.compile(r"^[0-9a-fA-F]{64}$")


def fail(message: str) -> int:
    print(f"RESULT FAIL {message}")
    return 1


def parse_json(path: Path) -> dict:
    def pairs(items: list[tuple[str, object]]) -> dict:
        result: dict[str, object] = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"duplicate key: {key}")
            result[key] = value
        return result

    with path.open("r", encoding="utf-8") as handle:
        value = json.load(handle, object_pairs_hook=pairs)
    if not isinstance(value, dict):
        raise ValueError("manifest root must be an object")
    return value


def safe_relative(value: object) -> Path:
    if not isinstance(value, str) or not value or "\\" in value:
        raise ValueError("artifact path must use a non-empty forward-slash relative path")
    path = Path(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts) or ":" in value:
        raise ValueError(f"unsafe artifact path: {value}")
    return path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--manifest", default="npcmanager-package.json")
    args = parser.parse_args()
    package = args.package.resolve()
    if package.drive.upper() != "K:" or not package.is_dir():
        return fail("package must be an existing K: directory")
    manifest_path = (package / args.manifest).resolve()
    if manifest_path.parent != package or not manifest_path.is_file():
        return fail("manifest must be directly inside the package root")
    try:
        manifest = parse_json(manifest_path)
        if manifest.get("schemaVersion") != 1:
            return fail("unsupported schemaVersion")
        for key in ("edition", "presetFormat", "sourcePreset", "sourcePlugin", "outputPlugin", "targetFormId"):
            if key not in manifest:
                return fail(f"missing {key}")
        for key in ("sourcePresetSha256", "sourcePluginSha256"):
            if not HEX64.fullmatch(str(manifest[key])):
                return fail(f"invalid {key}")
        artifacts = manifest.get("artifacts")
        if not isinstance(artifacts, list) or not artifacts:
            return fail("artifacts must be a non-empty array")
        seen: set[Path] = set()
        plugin_paths: list[Path] = []
        for artifact in artifacts:
            if not isinstance(artifact, dict):
                return fail("artifact must be an object")
            relative = safe_relative(artifact.get("relativePath"))
            if relative in seen:
                return fail(f"duplicate artifact: {relative}")
            seen.add(relative)
            destination = (package / relative).resolve()
            if package not in destination.parents or not destination.is_file():
                return fail(f"artifact is missing or escapes package: {relative}")
            raw_hash = str(artifact.get("sha256", ""))
            if not HEX64.fullmatch(raw_hash):
                return fail(f"invalid artifact hash: {relative}")
            data = destination.read_bytes()
            if len(data) != artifact.get("byteLength") or hashlib.sha256(data).hexdigest() != raw_hash.lower():
                return fail(f"artifact hash or length mismatch: {relative}")
            kind = artifact.get("kind")
            if kind == "plugin":
                plugin_paths.append(relative)
            elif kind != "bodygen":
                return fail(f"unsupported artifact kind: {kind}")
        if len(plugin_paths) != 1 or plugin_paths[0].as_posix() != manifest["outputPlugin"]:
            return fail("outputPlugin does not match the sole plugin artifact")
    except (OSError, ValueError, json.JSONDecodeError) as error:
        return fail(str(error))
    print(f"RESULT PASS artifacts={len(artifacts)} package={package}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
