#!/usr/bin/env python3
"""Independently verify the immutable M0 source-copy manifest."""

from __future__ import annotations

import csv
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path, PurePosixPath


SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
EXPECTED_COMMIT = "2f765c5dc09277f48321eb8cf80ad7925d4699a4"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def project_path(project_root: Path, relative: object) -> Path | None:
    if not isinstance(relative, str) or not relative or "\\" in relative:
        return None
    pure = PurePosixPath(relative)
    if pure.is_absolute() or ".." in pure.parts:
        return None
    candidate = project_root.joinpath(*pure.parts).resolve(strict=False)
    root = project_root.resolve()
    if candidate != root and root not in candidate.parents:
        return None
    return candidate


def main() -> int:
    project_root = Path(__file__).resolve().parents[1]
    manifest_path = project_root / "05-reports" / "source-copy-sha256-manifest.csv"
    errors: list[str] = []

    raw = manifest_path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        errors.append("manifest must be UTF-8 without BOM")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as exc:
        print(json.dumps({"result": "FAIL", "errors": [str(exc)]}, indent=2))
        return 1

    reader = csv.DictReader(text.splitlines())
    if reader.fieldnames != ["relative_path", "sha256"]:
        errors.append("manifest header must be exactly relative_path,sha256")
    rows = list(reader)
    listed_paths: set[str] = set()
    for row_number, row in enumerate(rows, start=2):
        relative = row.get("relative_path", "")
        expected_hash = row.get("sha256", "")
        if "\\" in relative:
            errors.append(f"row {row_number}: relative_path must use forward slashes")
        pure = PurePosixPath(relative)
        if pure.is_absolute() or ".." in pure.parts or not relative.startswith("01-source-copies/"):
            errors.append(f"row {row_number}: unsafe or out-of-scope relative_path {relative!r}")
            continue
        if relative in listed_paths:
            errors.append(f"row {row_number}: duplicate path {relative}")
        listed_paths.add(relative)
        if not SHA256_PATTERN.fullmatch(expected_hash):
            errors.append(f"row {row_number}: invalid lowercase SHA-256")
            continue
        target = project_root.joinpath(*pure.parts)
        if not target.is_file():
            errors.append(f"row {row_number}: file does not exist: {relative}")
            continue
        actual_hash = sha256(target)
        if actual_hash != expected_hash:
            errors.append(f"row {row_number}: hash mismatch for {relative}")

    actual_paths = {
        path.relative_to(project_root).as_posix()
        for path in (project_root / "01-source-copies").rglob("*")
        if path.is_file()
    }
    if listed_paths != actual_paths:
        for missing in sorted(actual_paths - listed_paths):
            errors.append(f"source copy absent from manifest: {missing}")
        for extra in sorted(listed_paths - actual_paths):
            errors.append(f"manifest path absent from source copies: {extra}")

    baseline_path = project_root / "01-source-copies" / "upstream" / "UPSTREAM_BASELINE.json"
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    if baseline.get("upstream_commit") != EXPECTED_COMMIT:
        errors.append("UPSTREAM_BASELINE.json commit mismatch")
    archive_info = baseline.get("source_archive", {})
    archive_path = project_path(project_root, archive_info.get("relative_path"))
    if archive_path is not None and archive_path.is_file():
        with zipfile.ZipFile(archive_path) as source_zip:
            files = sum(not entry.is_dir() for entry in source_zip.infolist())
            directories = sum(entry.is_dir() for entry in source_zip.infolist())
            names = {entry.filename for entry in source_zip.infolist()}
            archive_license = source_zip.read("LICENSE") if "LICENSE" in names else None
        if files != archive_info.get("file_entries") or directories != archive_info.get("directory_entries"):
            errors.append("source archive entry counts disagree with UPSTREAM_BASELINE.json")
        if "LICENSE" not in names:
            errors.append("source archive lacks LICENSE")
        if sha256(archive_path) != archive_info.get("sha256"):
            errors.append("source archive hash disagrees with UPSTREAM_BASELINE.json")
        license_info = baseline.get("license_copy", {})
        license_path = project_path(project_root, license_info.get("relative_path"))
        if license_path is None or not license_path.is_file():
            errors.append("license copy path is absent or unsafe")
        elif archive_license is None or license_path.read_bytes() != archive_license:
            errors.append("license copy is not byte-identical to the archived upstream LICENSE")
    else:
        errors.append("UPSTREAM_BASELINE.json source archive is absent or unsafe")

    executable_info = baseline.get("reference_executable", {})
    executable_path = project_path(project_root, executable_info.get("relative_path"))
    if executable_path is None or not executable_path.is_file() or sha256(executable_path) != executable_info.get("sha256"):
        errors.append("reference executable is absent, unsafe, or disagrees with UPSTREAM_BASELINE.json")

    if errors:
        print(json.dumps({"result": "FAIL", "errors": errors}, indent=2))
        return 1
    print(
        json.dumps(
            {
                "result": "PASS",
                "manifest": manifest_path.relative_to(project_root).as_posix(),
                "verified_files": len(rows),
                "upstream_commit": EXPECTED_COMMIT,
                "archive_file_entries": archive_info["file_entries"],
                "archive_directory_entries": archive_info["directory_entries"],
                "protected_live_root_touched": False
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
