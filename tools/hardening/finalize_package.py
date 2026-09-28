#!/usr/bin/env python3
"""Create a deterministic package manifest and archive from a K-local root."""

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
from pathlib import Path
from typing import Any


def _safe(project_root: Path, candidate: Path) -> Path:
    resolved = candidate.resolve()
    root = project_root.resolve()
    if resolved != root and root not in resolved.parents:
        raise ValueError(f"path escapes project root: {candidate}")
    return resolved


def _file_identity(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            length += len(chunk)
            digest.update(chunk)
    return length, digest.hexdigest()


def _sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _files(package_root: Path) -> list[dict[str, Any]]:
    files: list[dict[str, Any]] = []
    for path in sorted(package_root.rglob("*")):
        if not path.is_file():
            continue
        if path.is_symlink():
            raise ValueError(f"symlink is not allowed in package: {path}")
        relative = path.relative_to(package_root).as_posix()
        if relative in {"package-manifest.json", "package.hashes.sha256"}:
            continue
        length, sha256 = _file_identity(path)
        files.append({"path": relative, "bytes": length, "sha256": sha256})
    return files


def _write_json(path: Path, value: dict[str, Any]) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


def _deterministic_zip(package_root: Path, zip_path: Path) -> str:
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        paths = sorted(
            (path for path in package_root.rglob("*") if path.is_file()),
            key=lambda path: path.relative_to(package_root).as_posix(),
        )
        for path in paths:
            relative = path.relative_to(package_root).as_posix()
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes())
    return _sha256_file(zip_path)


def finalize(project_root: Path, package_root: Path, zip_path: Path, report: Path, report_md: Path) -> dict[str, Any]:
    package_root = _safe(project_root, package_root)
    zip_path = _safe(project_root, zip_path)
    report = _safe(project_root, report)
    report_md = _safe(project_root, report_md)
    if not package_root.is_dir():
        raise ValueError(f"package root does not exist: {package_root}")
    for path in (zip_path, report, report_md):
        if path.exists():
            raise ValueError(f"refusing to overwrite existing artifact: {path}")

    files = _files(package_root)
    required = {
        "cli/npcm.dll",
        "cli/npcm.exe",
        "desktop/NpcManager.Desktop.exe",
        "schemas/cli.schema.json",
        "README.md",
        "licenses/LICENSE",
        "licenses/NOTICE",
        "licenses/third-party-licenses.md",
        "licenses/Reloaded.Memory-9.4.1-LICENSE.md",
        "licenses/Microsoft.NETCore.App.Runtime-10.0.9-LICENSE.txt",
        "licenses/Microsoft.NETCore.App.Runtime-10.0.9-THIRD-PARTY-NOTICES.txt",
        "licenses/Microsoft.WindowsDesktop.App.Runtime-10.0.9-LICENSE.txt",
        "licenses/Microsoft.AspNetCore.App.Runtime-10.0.9-LICENSE.txt",
        "licenses/Microsoft.AspNetCore.App.Runtime-10.0.9-THIRD-PARTY-NOTICES.txt",
        "licenses/m8-license-closure.json", "licenses/m8-license-closure.md",
        "evidence/feature-ledger.json", "evidence/m8-runtime-smoke-readiness-2026-07-17.json",
        "evidence/m8-dependency-vulnerability-2026-07-17.json",
        "evidence/m8-dependency-vulnerability-public-2026-07-17.json",
        "evidence/m9-open-gaps-audit-2026-07-17.json",
        "evidence/release-gate.json",
    }
    actual = {entry["path"] for entry in files}
    missing = sorted(required - actual)
    if missing:
        raise ValueError(f"package is missing required files: {', '.join(missing)}")

    manifest = {
        "schemaVersion": 1,
        "package": "NpcManagerReimplementation",
        "version": package_root.name.removeprefix("npcmanager-"),
        "status": "PASS_WITH_SCOPED_LIMITS",
        "runtimeReleaseClaim": False,
        "licenseReview": "PASS_M8",
        "hashScope": "all package files except package-manifest.json and package.hashes.sha256",
        "files": files,
    }
    manifest_path = package_root / "package-manifest.json"
    _write_json(manifest_path, manifest)
    files_with_manifest = _files(package_root)
    hashes_path = package_root / "package.hashes.sha256"
    hashes_path.write_text(
        "".join(f"{entry['sha256']}  {entry['path']}\n" for entry in files_with_manifest),
        encoding="utf-8",
        newline="\n",
    )
    zip_hash = _deterministic_zip(package_root, zip_path)
    report_value = {
        "status": "PASS_WITH_SCOPED_LIMITS",
        "version": manifest["version"],
        "packageRoot": str(package_root),
        "packageArchive": str(zip_path),
        "packageArchiveSha256": zip_hash,
        "fileCount": len(files_with_manifest),
        "manifest": "package-manifest.json",
        "hashes": "package.hashes.sha256",
        "builder": "tools/hardening/build_package_candidate.ps1",
        "independentVerifier": "tools/hardening/verify_package.py",
        "runtimeReleaseClaim": False,
        "licenseReview": "PASS_M8",
        "requiredRuntimeEvidence": ["Fallout 4 smoke", "Skyrim SE smoke"],
    }
    _write_json(report, report_value)
    report_md.write_text(
        f"# Package candidate acceptance — {manifest['version']}\n\n"
        "Status: **PASS_WITH_SCOPED_LIMITS**.\n\n"
        f"The K-local package contains the Release CLI, WPF desktop executable, "
        f"versioned schema, source snapshot, dual-game fixtures, license files, "
        f"and evidence. It contains {len(files_with_manifest)} hashed files.\n\n"
        f"Archive SHA-256: `{zip_hash}`\n\n"
        "The package is not a final runtime release: Fallout 4/Skyrim SE runtime "
        "smoke evidence is still required.\n",
        encoding="utf-8",
        newline="\n",
    )
    return report_value


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-root", type=Path, required=True)
    parser.add_argument("--zip", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--report-md", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    result = finalize(project_root, args.package_root, args.zip, args.report, args.report_md)
    print(json.dumps({"result": "PASS", "files": result["fileCount"], "archiveSha256": result["packageArchiveSha256"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
