#!/usr/bin/env python3
"""Verify the registered preview.225 package and its source-snapshot boundary."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import zipfile
from pathlib import Path, PureWindowsPath


EXPECTED_VERSION = "1.0.0-preview.225"
EXPECTED_PREVIOUS_VERSION = "1.0.0-preview.224"
EXPECTED_PREVIOUS_COMMANDS = 129
EXPECTED_CURRENT_COMMANDS = 130
EXPECTED_ADDITION = {"npc assembly preflight"}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def relative(root: Path, value: object, label: str) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{label} must be a non-empty relative path")
    windows = PureWindowsPath(value)
    if windows.is_absolute() or windows.drive:
        raise ValueError(f"{label} must be relative")
    candidate = (root / Path(value)).resolve()
    root = root.resolve()
    if candidate != root and root not in candidate.parents:
        raise ValueError(f"{label} escapes the selected root")
    if windows.drive.casefold() == "f:":
        raise ValueError(f"{label} may not target F:")
    return candidate


def source_snapshot_digest(root: Path) -> tuple[int, str]:
    records: list[bytes] = []
    files = sorted(path for path in root.rglob("*") if path.is_file())
    for path in files:
        name = path.relative_to(root).as_posix()
        records.append(f"{name}\0{hashlib.sha256(path.read_bytes()).hexdigest()}\n".encode("utf-8"))
    return len(files), hashlib.sha256(b"".join(records)).hexdigest()


def verify_package(root: Path, package: dict[str, object]) -> list[str]:
    errors: list[str] = []
    try:
        expanded = relative(root, package["expandedRoot"], "expandedRoot")
        archive = relative(root, package["archive"], "archive")
    except (KeyError, ValueError) as exc:
        return [str(exc)]
    manifest = expanded / "package-manifest.json"
    hashes = expanded / "package.hashes.sha256"
    if not manifest.is_file() or not hashes.is_file() or not archive.is_file():
        return ["registered package manifest, hash manifest, or archive is missing"]
    try:
        value = json.loads(manifest.read_text(encoding="utf-8"))
        entries = value["files"]
        if not isinstance(entries, list):
            raise ValueError("package manifest files is not an array")
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, ValueError) as exc:
        return [f"package manifest is invalid: {exc}"]
    manifest_map: dict[str, str] = {}
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            errors.append("package manifest contains an invalid entry")
            continue
        name = PureWindowsPath(entry["path"]).as_posix()
        target = expanded / Path(name)
        digest = str(entry.get("sha256", "")).casefold()
        manifest_map[name] = digest
        if not target.is_file():
            errors.append(f"manifest file is missing: {name}")
            continue
        if target.stat().st_size != entry.get("bytes"):
            errors.append(f"manifest byte mismatch: {name}")
        if sha256_file(target) != digest:
            errors.append(f"manifest hash mismatch: {name}")
    hash_map: dict[str, str] = {}
    for line in hashes.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        digest, name = line.split(maxsplit=1)
        hash_map[name.replace("\\", "/")] = digest.casefold()
    if hash_map != manifest_map:
        errors.append("package.hashes.sha256 does not match package-manifest.json")
    if package.get("manifestedFileCount") != len(entries):
        errors.append("manifested file count does not match declaration")
    for field, path in (("packageManifestSha256", manifest), ("hashManifestSha256", hashes)):
        if str(package.get(field, "")).casefold() != sha256_file(path):
            errors.append(f"{field} mismatch")
    if str(package.get("archiveSha256", "")).casefold() != sha256_file(archive):
        errors.append("archiveSha256 mismatch")
    expected_members = set(manifest_map) | {"package-manifest.json", "package.hashes.sha256"}
    try:
        with zipfile.ZipFile(archive) as handle:
            if set(handle.namelist()) != expected_members:
                errors.append("archive member inventory mismatch")
            for name in expected_members:
                if name not in handle.namelist():
                    continue
                expected = (expanded / Path(name)).read_bytes() if name in manifest_map else (manifest if name == "package-manifest.json" else hashes).read_bytes()
                if handle.read(name) != expected:
                    errors.append(f"archive bytes mismatch: {name}")
    except (OSError, zipfile.BadZipFile) as exc:
        errors.append(f"archive inspection failed: {exc}")
    return errors


def command_names(cli: Path) -> set[str]:
    result = subprocess.run([str(cli), "capabilities", "--json"], cwd=cli.parent, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise ValueError(f"capabilities exited {result.returncode}")
    value = json.loads(result.stdout)
    commands = value.get("commands")
    if not isinstance(commands, list):
        raise ValueError("capabilities response has no commands array")
    names = {item if isinstance(item, str) else item.get("name") for item in commands}
    if not all(isinstance(name, str) for name in names):
        raise ValueError("capabilities response has an invalid command")
    return names


def verify(repo_root: Path, declaration: dict[str, object]) -> dict[str, object]:
    errors: list[str] = []
    checks: list[dict[str, object]] = []
    def check(name: str, expected: object, actual: object, failed: bool = False) -> None:
        checks.append({"id": name, "expected": expected, "actual": actual, "outcome": "FAIL" if failed else "PASS"})
    if declaration.get("schema") != "npcmanager-provisional-registration/2":
        errors.append("declaration schema must be npcmanager-provisional-registration/2")
    if declaration.get("product") != "NpcManagerReimplementation":
        errors.append("declaration product is invalid")
    if declaration.get("version") != EXPECTED_VERSION:
        errors.append("declaration version is not preview.225")
    decision = declaration.get("registrationDecision", {})
    if not isinstance(decision, dict) or decision.get("visualAuthority") is not False or decision.get("runtimeAuthority") is not False:
        errors.append("registration must keep visualAuthority and runtimeAuthority false")
    package = declaration.get("registeredPackage")
    if not isinstance(package, dict):
        errors.append("registeredPackage is missing")
    else:
        package_errors = verify_package(repo_root, package)
        errors.extend(package_errors)
        check("package-bytes", "manifest, hashes, archive, and declared file count", "valid" if not package_errors else package_errors, bool(package_errors))
    source = declaration.get("sourceSnapshot")
    if not isinstance(source, dict):
        errors.append("sourceSnapshot is missing")
    else:
        try:
            snapshot = relative(repo_root, source["root"], "source snapshot root")
            count, digest = source_snapshot_digest(snapshot)
            if count != source.get("fileCount"):
                errors.append(f"source snapshot file count is {count}, expected {source.get('fileCount')}")
            if digest.casefold() != str(source.get("sha256", "")).casefold():
                errors.append("source snapshot aggregate hash mismatch")
        except (KeyError, OSError, ValueError) as exc:
            errors.append(f"source snapshot verification failed: {exc}")
        check("source-snapshot", source.get("fileCount"), "verified" if not errors else "see errors", bool(errors))
    if isinstance(package, dict):
        try:
            expanded = relative(repo_root, package["expandedRoot"], "expandedRoot")
            previous = relative(repo_root, package["previousExpandedRoot"], "previousExpandedRoot")
            current_names = command_names(expanded / "cli" / "npcm.exe")
            previous_names = command_names(previous / "cli" / "npcm.exe")
            additions = current_names - previous_names
            missing = previous_names - current_names
            if len(previous_names) != EXPECTED_PREVIOUS_COMMANDS or len(current_names) != EXPECTED_CURRENT_COMMANDS or missing or additions != EXPECTED_ADDITION:
                errors.append(f"command catalogue mismatch: previous={len(previous_names)}, current={len(current_names)}, missing={sorted(missing)}, additions={sorted(additions)}")
            if "npc assembly preflight" not in current_names:
                errors.append("registered CLI does not expose npc assembly preflight")
            check("command-catalogue", "129 -> 130 with exactly npc assembly preflight", {"previous": len(previous_names), "current": len(current_names), "additions": sorted(additions)}, bool(missing or additions != EXPECTED_ADDITION))
        except (KeyError, OSError, ValueError, json.JSONDecodeError) as exc:
            errors.append(f"command catalogue verification failed: {exc}")
    source_authority = declaration.get("sourceAuthority", {})
    if not isinstance(source_authority, dict) or source_authority.get("baseCommit") != "64ce5c524fc18e4dfb5475e7e17c806035f53c11" or source_authority.get("workingTreeState") != "DIRTY_WORKTREE_SNAPSHOT_CAPTURED":
        errors.append("source authority must identify the captured solveig-build working-tree snapshot")
    check("runtime-boundary", "runtimeAuthority=false and visualAuthority=false", "valid" if not errors else "see errors", bool(errors))
    return {"schemaVersion": 2, "outcome": "PASS" if not errors else "FAIL", "checks": checks, "errors": errors}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--declaration", required=True, type=Path)
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    declaration = json.loads(args.declaration.read_text(encoding="utf-8"))
    report = verify(args.repo_root.resolve(), declaration)
    if args.output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {args.output}")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["outcome"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
