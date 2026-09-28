#!/usr/bin/env python3
"""Verify the unregistered preview.226 package and its source boundary."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any


EXPECTED_PREVIOUS_COMMANDS = 130
EXPECTED_CURRENT_COMMANDS = 133
EXPECTED_ADDITIONS = {
    "npc finish analyze",
    "npc finish apply",
    "npc finish verify",
}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _archive_path(package_root: Path) -> Path:
    # The version contains a dot (preview.226), so Path.with_suffix would
    # incorrectly drop the numeric preview suffix.
    return Path(str(package_root) + ".zip")


def _safe_child(root: Path, value: str, label: str) -> Path:
    if not isinstance(value, str) or not value:
        raise ValueError(f"{label} must be a non-empty relative path")
    relative = PurePosixPath(value)
    if relative.is_absolute() or any(part in {"", ".", ".."} for part in relative.parts):
        raise ValueError(f"{label} must be canonical and relative")
    candidate = (root / Path(*relative.parts)).resolve()
    resolved_root = root.resolve()
    if candidate != resolved_root and resolved_root not in candidate.parents:
        raise ValueError(f"{label} escapes its package root")
    return candidate


def _command_names(executable: Path) -> set[str]:
    result = subprocess.run(
        [str(executable), "capabilities", "--json"],
        cwd=executable.parent,
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        raise ValueError(f"capabilities exited {result.returncode}: {result.stderr[-500:]}")
    payload = json.loads(result.stdout)
    commands = payload.get("commands")
    if not isinstance(commands, list):
        raise ValueError("capabilities response has no commands array")
    names = {
        item if isinstance(item, str) else item.get("name")
        for item in commands
    }
    if not all(isinstance(name, str) and name for name in names):
        raise ValueError("capabilities response contains an invalid command")
    return names


def _verify_package(package_root: Path, expected_version: str) -> list[str]:
    errors: list[str] = []
    manifest_path = package_root / "package-manifest.json"
    hash_path = package_root / "package.hashes.sha256"
    archive_path = _archive_path(package_root)
    if not manifest_path.is_file() or not hash_path.is_file() or not archive_path.is_file():
        return ["package-manifest.json, package.hashes.sha256, or adjacent archive is missing"]
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        return [f"package manifest is invalid: {exc}"]
    if manifest.get("schemaVersion") != 1:
        errors.append("package manifest schemaVersion is not 1")
    if manifest.get("version") != expected_version:
        errors.append("package manifest version does not match the candidate")
    if manifest.get("status") != "PASS_WITH_SCOPED_LIMITS":
        errors.append("package manifest status is not PASS_WITH_SCOPED_LIMITS")
    if manifest.get("runtimeReleaseClaim") is not False:
        errors.append("candidate package must not claim runtime release authority")
    declared = manifest.get("files")
    if not isinstance(declared, list):
        return errors + ["package manifest files is not an array"]
    manifest_map: dict[str, str] = {}
    for row in declared:
        if not isinstance(row, dict) or not isinstance(row.get("path"), str):
            errors.append("package manifest contains an invalid file row")
            continue
        name = PurePosixPath(row["path"]).as_posix()
        if name in manifest_map:
            errors.append(f"duplicate package file row: {name}")
            continue
        target = package_root / Path(*PurePosixPath(name).parts)
        digest = str(row.get("sha256", "")).lower()
        manifest_map[name] = digest
        if not target.is_file():
            errors.append(f"declared package file is missing: {name}")
            continue
        if target.stat().st_size != row.get("bytes"):
            errors.append(f"declared byte length mismatch: {name}")
        if _sha256(target) != digest:
            errors.append(f"declared hash mismatch: {name}")
    hash_map: dict[str, str] = {}
    try:
        for line in hash_path.read_text(encoding="utf-8").splitlines():
            if line.strip():
                digest, name = line.split(maxsplit=1)
                hash_map[name.replace("\\", "/")] = digest.lower()
    except (OSError, ValueError) as exc:
        errors.append(f"package hash manifest is invalid: {exc}")
    if hash_map != manifest_map:
        errors.append("package.hashes.sha256 differs from package-manifest.json")
    names = set(manifest_map) | {"package-manifest.json", "package.hashes.sha256"}
    try:
        with zipfile.ZipFile(archive_path) as archive:
            if set(archive.namelist()) != names:
                errors.append("archive member inventory differs from the package manifest")
            for name in names:
                if name not in archive.namelist():
                    continue
                expected = (
                    (package_root / Path(*PurePosixPath(name).parts)).read_bytes()
                    if name in manifest_map
                    else (manifest_path if name == "package-manifest.json" else hash_path).read_bytes()
                )
                if archive.read(name) != expected:
                    errors.append(f"archive bytes differ for {name}")
    except (OSError, zipfile.BadZipFile) as exc:
        errors.append(f"archive readback failed: {exc}")
    return errors


def verify(
    repo_root: Path,
    previous_package: Path,
    candidate_package: Path,
    source_delta: Path,
    mode: str = "candidate",
) -> dict[str, Any]:
    errors: list[str] = []
    checks: list[dict[str, Any]] = []

    def check(identifier: str, expected: Any, actual: Any, failed: bool = False) -> None:
        checks.append({
            "id": identifier,
            "expected": expected,
            "actual": actual,
            "outcome": "FAIL" if failed else "PASS",
        })

    if mode not in {"candidate", "registered"}:
        errors.append("mode must be candidate or registered")
    previous_names: set[str] = set()
    current_names: set[str] = set()
    try:
        previous_names = _command_names(previous_package / "cli" / "npcm.exe")
        current_names = _command_names(candidate_package / "cli" / "npcm.exe")
        additions = current_names - previous_names
        removals = previous_names - current_names
        catalogue_failed = (
            len(previous_names) != EXPECTED_PREVIOUS_COMMANDS
            or len(current_names) != EXPECTED_CURRENT_COMMANDS
            or additions != EXPECTED_ADDITIONS
            or bool(removals)
        )
        if catalogue_failed:
            errors.append(
                "command catalogue mismatch: "
                f"previous={len(previous_names)}, current={len(current_names)}, "
                f"additions={sorted(additions)}, removals={sorted(removals)}"
            )
        check(
            "command-catalogue",
            "130 -> 133 with exactly the three Finish Core commands",
            {
                "previous": len(previous_names),
                "current": len(current_names),
                "additions": sorted(additions),
                "removals": sorted(removals),
            },
            catalogue_failed,
        )
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        errors.append(f"command catalogue verification failed: {exc}")
        check("command-catalogue", "130 -> 133", str(exc), True)

    package_errors = _verify_package(candidate_package, "1.0.0-preview.226")
    errors.extend(package_errors)
    check(
        "candidate-package",
        "manifest, hash manifest, archive, and scoped runtime claim",
        "verified" if not package_errors else package_errors,
        bool(package_errors),
    )

    try:
        delta = json.loads(source_delta.read_text(encoding="utf-8"))
        delta_ok = delta.get("outcome") == "PASS" and not delta.get("unclassified")
        if not delta_ok:
            errors.append("source-delta report is not a complete PASS")
        expected_source = (candidate_package / "source").resolve().as_posix()
        actual_source = Path(str(delta.get("workingProject", ""))).resolve().as_posix()
        if actual_source != expected_source:
            errors.append("source-delta report does not bind the candidate source root")
        check(
            "source-delta",
            {"outcome": "PASS", "workingProject": expected_source},
            {"outcome": delta.get("outcome"), "workingProject": actual_source},
            not delta_ok or actual_source != expected_source,
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        errors.append(f"source-delta report is invalid: {exc}")
        check("source-delta", "PASS", str(exc), True)

    check(
        "runtime-boundary",
        {"runtimeAuthority": False, "visualAuthority": False, "placementIncluded": False},
        {"runtimeAuthority": False, "visualAuthority": False, "placementIncluded": False},
    )
    report = {
        "schemaVersion": 1,
        "artifactKind": "npc-manager-preview226-candidate-continuity",
        "mode": mode,
        "outcome": "PASS" if not errors else "FAIL",
        "runtimeAuthority": False,
        "visualAuthority": False,
        "placementIncluded": False,
        "checks": checks,
        "errors": errors,
        "packages": {
            "previous": previous_package.resolve().as_posix(),
            "candidate": candidate_package.resolve().as_posix(),
            "candidateArchiveSha256": _sha256(_archive_path(candidate_package))
            if _archive_path(candidate_package).is_file()
            else None,
        },
    }
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--previous-package", required=True, type=Path)
    parser.add_argument("--candidate-package", required=True, type=Path)
    parser.add_argument("--source-delta", required=True, type=Path)
    parser.add_argument("--mode", choices=["candidate", "registered"], default="candidate")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = verify(
        args.repo_root.resolve(),
        args.previous_package.resolve(),
        args.candidate_package.resolve(),
        args.source_delta.resolve(),
        args.mode,
    )
    if args.output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {args.output}")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["outcome"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
