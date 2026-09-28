#!/usr/bin/env python3
"""Fail-closed storage retention audit and application for NPC Manager.

The audit is read-only except for its new JSON report.  The apply command
accepts only an exact SHA-256-bound audit, rechecks protected authorities, and
deletes only paths enumerated by that audit.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import time
import zipfile
import zlib
from pathlib import Path
from typing import Any, Iterable


SCHEMA = "npcmanager-storage-retention/1"
PREVIEW_PATTERN = re.compile(r"^npcmanager-1\.0\.0-preview\.(\d+)$")
RETAIN_EXPANDED_PREVIEWS = {102, 169, 221, 223}
RETAIN_WORK_PATHS = {
    "preview223-packaged-cli-sophia-render",
    "npc-visual-brigitte-v05-final-correction-20260729-5",
    "desktop-npc-preview/F8377667B8197D2F-fc5ef59b911f46d5b81293b9cf6b0307",
}
PROTECTED_AUTHORITIES = {
    "preview221.archive": (
        "04-packages/npcmanager-1.0.0-preview.221.zip",
        "B09F9B046AB1BF9D015830185357B5779AEEC0F17E60B670478AE29AB73F39EA",
    ),
    "preview221.desktop": (
        "04-packages/npcmanager-1.0.0-preview.221/desktop/NpcManager.Desktop.exe",
        "442EDA86DD20D65C6788280DED7147553762F6ED94F72F039700DE6B9627D164",
    ),
    "preview221.cli": (
        "04-packages/npcmanager-1.0.0-preview.221/cli/npcm.exe",
        "2FCFC9B51541B43ECD0C53284B9E226871B787D3E61D29AC5319CB7319F30624",
    ),
    "preview221.packageManifest": (
        "04-packages/npcmanager-1.0.0-preview.221/package-manifest.json",
        "FFF3EF8472355FDD8503B668E525F0BC4B56FCE181537A28470F8B081F890780",
    ),
    "preview221.hashManifest": (
        "04-packages/npcmanager-1.0.0-preview.221/package.hashes.sha256",
        "D113673C5B100A5F7610A0185D01F5A8C61CCC112121DC44BD3422A3F3B233F5",
    ),
    "preview222.archive": (
        "04-packages/npcmanager-1.0.0-preview.222.zip",
        "F03779E955581FFD1009CFBF4779B58A13E50B10C30BA0D1280ABBE6F7F73700",
    ),
    "preview222.desktop": (
        "04-packages/npcmanager-1.0.0-preview.222/desktop/NpcManager.Desktop.exe",
        "D87F1BD892489CDD3DB68D0E195237D6EEA20C25D72DC30E897DC507BCEB1B5D",
    ),
    "preview222.cli": (
        "04-packages/npcmanager-1.0.0-preview.222/cli/npcm.exe",
        "1CBC4678FE6F8E092D22EF49BD1417E508A5913D754E1427EA1D67957BF22703",
    ),
    "preview222.packageManifest": (
        "04-packages/npcmanager-1.0.0-preview.222/package-manifest.json",
        "BB7B1C2F93EFCDD79176FA1D233648ED44122550693592EC9CA048166A087FB7",
    ),
    "preview222.hashManifest": (
        "04-packages/npcmanager-1.0.0-preview.222/package.hashes.sha256",
        "9EBC1D1CDDCD81BC5EDAA60F17E5AB7A556EEC99DFDBDC881ACC1AF0DF3350FE",
    ),
    "preview223.archive": (
        "04-packages/npcmanager-1.0.0-preview.223.zip",
        "452267291D416BBFED83B6796FCCE60F8B45CE88BA3D5CE3E04E0DFB70BDC08D",
    ),
    "preview223.desktop": (
        "04-packages/npcmanager-1.0.0-preview.223/desktop/NpcManager.Desktop.exe",
        "D1BA44F001B28F9552BE077C84B9901B0786B9CEA5CB08078E577AA3C674ADF5",
    ),
    "preview223.cli": (
        "04-packages/npcmanager-1.0.0-preview.223/cli/npcm.exe",
        "B915DAA7CD1A363041D79B5D6218D428793F154CA56FAD1BE2DFE819F5854E68",
    ),
    "preview223.packageManifest": (
        "04-packages/npcmanager-1.0.0-preview.223/package-manifest.json",
        "39506CFD9A07519261EF2F5AB1F499EE7E21C255A0CFE9480D175D084A6DCD56",
    ),
    "preview223.hashManifest": (
        "04-packages/npcmanager-1.0.0-preview.223/package.hashes.sha256",
        "493F305B6B9FEC9B93960A436E19FF18B7F177B0089C208ED3B305E095FC4188",
    ),
}


class RetentionError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def is_reparse(path: Path) -> bool:
    info = path.lstat()
    attributes = getattr(info, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return path.is_symlink() or bool(attributes & reparse_flag)


def assert_inside(path: Path, root: Path, *, allow_root: bool = False) -> Path:
    resolved = path.resolve(strict=True)
    root_resolved = root.resolve(strict=True)
    if resolved == root_resolved:
        if allow_root:
            return resolved
        raise RetentionError(f"Refusing root target: {resolved}")
    try:
        resolved.relative_to(root_resolved)
    except ValueError as error:
        raise RetentionError(f"Path escapes allowed root: {resolved}") from error
    return resolved


def walk_files(root: Path) -> Iterable[Path]:
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for directory in list(directories):
            child = current_path / directory
            if is_reparse(child):
                raise RetentionError(f"Reparse directory refused: {child}")
        for filename in files:
            child = current_path / filename
            if is_reparse(child):
                raise RetentionError(f"Reparse file refused: {child}")
            yield child


def measure(path: Path) -> tuple[int, int]:
    if path.is_file():
        return (1, path.stat().st_size)
    count = 0
    size = 0
    for child in walk_files(path):
        count += 1
        size += child.stat().st_size
    return (count, size)


def crc32_file(path: Path) -> int:
    checksum = 0
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum = zlib.crc32(chunk, checksum)
    return checksum & 0xFFFFFFFF


def normalized_zip_entries(archive: Path) -> dict[str, zipfile.ZipInfo]:
    result: dict[str, zipfile.ZipInfo] = {}
    with zipfile.ZipFile(archive, "r") as package:
        for entry in package.infolist():
            name = entry.filename.replace("\\", "/")
            if entry.is_dir():
                continue
            if entry.flag_bits & 0x1:
                raise RetentionError(f"Encrypted ZIP entry refused: {archive}!{name}")
            parts = Path(name).parts
            if not parts or name.startswith("/") or ".." in parts:
                raise RetentionError(f"Unsafe ZIP entry refused: {archive}!{name}")
            key = "/".join(parts)
            if key in result:
                raise RetentionError(f"Duplicate ZIP entry refused: {archive}!{key}")
            result[key] = entry
    return result


def verify_expanded_archive(directory: Path, archive: Path) -> dict[str, Any]:
    expanded: dict[str, Path] = {}
    for child in walk_files(directory):
        key = child.relative_to(directory).as_posix()
        expanded[key] = child
    zipped = normalized_zip_entries(archive)
    missing = sorted(set(zipped) - set(expanded))
    extra = sorted(set(expanded) - set(zipped))
    mismatches: list[dict[str, Any]] = []
    if not missing and not extra:
        for key in sorted(zipped):
            entry = zipped[key]
            child = expanded[key]
            actual_size = child.stat().st_size
            if actual_size != entry.file_size:
                mismatches.append(
                    {
                        "path": key,
                        "reason": "size",
                        "expanded": actual_size,
                        "archive": entry.file_size,
                    }
                )
                continue
            actual_crc = crc32_file(child)
            if actual_crc != entry.CRC:
                mismatches.append(
                    {
                        "path": key,
                        "reason": "crc32",
                        "expanded": f"{actual_crc:08X}",
                        "archive": f"{entry.CRC:08X}",
                    }
                )
    return {
        "status": "EQUIVALENT" if not missing and not extra and not mismatches else "MISMATCH",
        "expandedFileCount": len(expanded),
        "archiveFileCount": len(zipped),
        "missing": missing,
        "extra": extra,
        "mismatches": mismatches,
    }


def candidate_records_digest(items: list[dict[str, Any]]) -> str:
    digest = hashlib.sha256()
    for item in sorted(items, key=lambda value: value["path"].lower()):
        digest.update(
            (
                f"{item['path']}\0{int(item['fileCount'])}\0"
                f"{int(item['bytes'])}\n"
            ).encode("utf-8")
        )
    return digest.hexdigest().upper()


def git_value(workspace: Path, *arguments: str) -> bytes:
    result = subprocess.run(
        ["git", *arguments],
        cwd=workspace,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        raise RetentionError(
            f"Git command failed ({result.returncode}): git {' '.join(arguments)}\n"
            f"{result.stderr.decode('utf-8', errors='replace')}"
        )
    return result.stdout


def validate_head_transition(
    workspace: Path,
    audited_commit: str,
    current_commit: str,
    expected_current_commit: str | None,
    allowed_paths: list[str],
) -> list[str]:
    if current_commit == audited_commit:
        if expected_current_commit or allowed_paths:
            raise RetentionError(
                "HEAD did not change, but an intervening-commit allowance was supplied"
            )
        return []
    if expected_current_commit is None:
        raise RetentionError(
            f"HEAD changed since audit: {audited_commit} -> {current_commit}"
        )
    if current_commit != expected_current_commit.upper():
        raise RetentionError(
            "Current HEAD does not equal the exact allowed descendant: "
            f"{current_commit} != {expected_current_commit.upper()}"
        )
    ancestor = subprocess.run(
        ["git", "merge-base", "--is-ancestor", audited_commit, current_commit],
        cwd=workspace,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if ancestor.returncode != 0:
        raise RetentionError(
            f"Audited commit is not an ancestor of allowed HEAD: "
            f"{audited_commit} -> {current_commit}"
        )
    changed = [
        item.decode("utf-8")
        for item in git_value(
            workspace,
            "diff",
            "--name-only",
            "-z",
            f"{audited_commit}..{current_commit}",
        ).split(b"\0")
        if item
    ]
    normalized_allowed = sorted(
        {item.replace("\\", "/").strip("/") for item in allowed_paths}
    )
    if sorted(changed) != normalized_allowed:
        raise RetentionError(
            "Intervening tracked paths differ from the exact allowance: "
            f"actual={sorted(changed)!r}, allowed={normalized_allowed!r}"
        )
    return changed


def authority_inventory(project: Path) -> list[dict[str, Any]]:
    result = []
    for name, (relative, expected) in PROTECTED_AUTHORITIES.items():
        requested = project / relative
        source = relative
        if requested.exists():
            path = assert_inside(requested, project)
            actual = sha256_file(path)
        else:
            match = re.fullmatch(
                r"04-packages/(npcmanager-1\.0\.0-preview\.\d+)/(.*)",
                relative,
            )
            if match is None:
                raise RetentionError(f"Protected authority is missing: {requested}")
            package_name, member = match.groups()
            archive_relative = f"04-packages/{package_name}.zip"
            archive = assert_inside(project / archive_relative, project)
            entries = normalized_zip_entries(archive)
            entry = entries.get(member)
            if entry is None:
                raise RetentionError(
                    f"Protected authority is absent from archive: {archive}!{member}"
                )
            digest = hashlib.sha256()
            with zipfile.ZipFile(archive, "r") as zipped:
                with zipped.open(entry, "r") as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                        digest.update(chunk)
            actual = digest.hexdigest().upper()
            source = f"{archive_relative}!{member}"
        result.append(
            {
                "name": name,
                "path": relative,
                "source": source,
                "expectedSha256": expected,
                "actualSha256": actual,
                "status": "PASS" if actual == expected else "FAIL",
            }
        )
    return result


def find_generated_candidates(project: Path) -> list[dict[str, Any]]:
    candidates: list[dict[str, Any]] = []
    for base_name in ("src", "tests", "tmp"):
        base = project / base_name
        if not base.exists():
            continue
        for current, directories, _ in os.walk(base, topdown=True, followlinks=False):
            current_path = Path(current)
            for directory in list(directories):
                child = current_path / directory
                if is_reparse(child):
                    raise RetentionError(f"Reparse directory refused: {child}")
                if directory.lower() in {"bin", "obj"}:
                    files, size = measure(child)
                    candidates.append(
                        {
                            "kind": "generated-build-output",
                            "path": child.relative_to(project).as_posix(),
                            "fileCount": files,
                            "bytes": size,
                            "status": "GENERATED",
                        }
                    )
                    directories.remove(directory)
    for child in sorted(
        (project / "src").glob("**/*_wpftmp.csproj"),
        key=lambda item: item.as_posix().lower(),
    ):
        if is_reparse(child):
            raise RetentionError(f"Reparse generated file refused: {child}")
        files, size = measure(child)
        candidates.append(
            {
                "kind": "generated-test-residue",
                "path": child.relative_to(project).as_posix(),
                "fileCount": files,
                "bytes": size,
                "status": "GENERATED",
            }
        )
    test_residue = re.compile(r"^\.(?:scratch|work|output)-[A-Za-z0-9_.-]+$")
    tests = project / "tests"
    for current, directories, _ in os.walk(tests, topdown=True, followlinks=False):
        current_path = Path(current)
        for directory in list(directories):
            child = current_path / directory
            if is_reparse(child):
                raise RetentionError(f"Reparse generated directory refused: {child}")
            if test_residue.fullmatch(directory):
                files, size = measure(child)
                candidates.append(
                    {
                        "kind": "generated-test-residue",
                        "path": child.relative_to(project).as_posix(),
                        "fileCount": files,
                        "bytes": size,
                        "status": "GENERATED",
                    }
                )
                directories.remove(directory)
    return candidates


def compare_trees(left: Path, right: Path) -> dict[str, Any]:
    left_files = {child.relative_to(left).as_posix(): child for child in walk_files(left)}
    right_files = {child.relative_to(right).as_posix(): child for child in walk_files(right)}
    missing = sorted(set(right_files) - set(left_files))
    extra = sorted(set(left_files) - set(right_files))
    mismatches: list[dict[str, str]] = []
    if not missing and not extra:
        for relative in sorted(left_files):
            left_path = left_files[relative]
            right_path = right_files[relative]
            if left_path.stat().st_size != right_path.stat().st_size:
                mismatches.append({"path": relative, "reason": "size"})
                continue
            if sha256_file(left_path) != sha256_file(right_path):
                mismatches.append({"path": relative, "reason": "sha256"})
    return {
        "status": "EQUIVALENT" if not missing and not extra and not mismatches else "MISMATCH",
        "comparedFileCount": len(left_files),
        "missing": missing,
        "extra": extra,
        "mismatches": mismatches,
    }


def find_work_candidates(project: Path) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    work = project / "03-builds" / "work"
    candidates: list[dict[str, Any]] = []
    retained: list[dict[str, Any]] = []
    retained_parts = {Path(item) for item in RETAIN_WORK_PATHS}
    for child in sorted(work.iterdir(), key=lambda item: item.name.lower()):
        if is_reparse(child):
            raise RetentionError(f"Reparse work path refused: {child}")
        relative = child.relative_to(work)
        descendants = [item for item in retained_parts if item == relative or relative in item.parents]
        if relative in retained_parts:
            files, size = measure(child)
            retained.append(
                {
                    "path": child.relative_to(project).as_posix(),
                    "fileCount": files,
                    "bytes": size,
                    "reason": "explicit-visual-authority",
                }
            )
            continue
        if descendants and child.is_dir():
            for nested in sorted(child.iterdir(), key=lambda item: item.name.lower()):
                if is_reparse(nested):
                    raise RetentionError(f"Reparse work path refused: {nested}")
                nested_relative = nested.relative_to(work)
                files, size = measure(nested)
                if nested_relative in retained_parts:
                    retained.append(
                        {
                            "path": nested.relative_to(project).as_posix(),
                            "fileCount": files,
                            "bytes": size,
                            "reason": "explicit-visual-authority",
                        }
                    )
                else:
                    candidates.append(
                        {
                            "kind": "disposable-work-output",
                            "path": nested.relative_to(project).as_posix(),
                            "fileCount": files,
                            "bytes": size,
                            "status": "DISPOSABLE",
                        }
                    )
            continue
        files, size = measure(child)
        candidates.append(
            {
                "kind": "disposable-work-output",
                "path": child.relative_to(project).as_posix(),
                "fileCount": files,
                "bytes": size,
                "status": "DISPOSABLE",
            }
        )
    return candidates, retained


def find_package_candidates(project: Path) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    packages = project / "04-packages"
    candidates: list[dict[str, Any]] = []
    retained: list[dict[str, Any]] = []
    directories = [
        child
        for child in packages.iterdir()
        if child.is_dir() and PREVIEW_PATTERN.match(child.name)
    ]
    total = len(directories)
    for index, directory in enumerate(sorted(directories, key=lambda item: item.name), start=1):
        match = PREVIEW_PATTERN.match(directory.name)
        if match is None:
            continue
        version = int(match.group(1))
        archive = packages / f"{directory.name}.zip"
        files, size = measure(directory)
        if version in RETAIN_EXPANDED_PREVIEWS or not archive.exists():
            retained.append(
                {
                    "path": directory.relative_to(project).as_posix(),
                    "fileCount": files,
                    "bytes": size,
                    "reason": (
                        "explicit-expanded-retention"
                        if version in RETAIN_EXPANDED_PREVIEWS
                        else "archive-absent"
                    ),
                }
            )
            print(
                f"PACKAGE {index}/{total} RETAIN {directory.name}",
                flush=True,
            )
            continue
        print(
            f"PACKAGE {index}/{total} VERIFY {directory.name}",
            flush=True,
        )
        equivalence = verify_expanded_archive(directory, archive)
        candidates.append(
            {
                "kind": "zip-equivalent-expanded-package",
                "path": directory.relative_to(project).as_posix(),
                "archivePath": archive.relative_to(project).as_posix(),
                "archiveSha256": sha256_file(archive),
                "fileCount": files,
                "bytes": size,
                **equivalence,
            }
        )
    return candidates, retained


def write_new_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    serialized = (json.dumps(value, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    with path.open("xb") as stream:
        stream.write(serialized)


def audit(args: argparse.Namespace) -> int:
    project = Path(args.project_root).resolve(strict=True)
    workspace = project.parents[1]
    output = Path(args.output).resolve(strict=False)
    if output.exists():
        raise RetentionError(f"Output already exists: {output}")
    if is_reparse(project):
        raise RetentionError(f"Project root is a reparse path: {project}")
    source_commit = git_value(workspace, "rev-parse", "HEAD").decode("ascii").strip().upper()
    dirty = git_value(workspace, "status", "--porcelain=v1", "-z")
    authorities = authority_inventory(project)
    if any(item["status"] != "PASS" for item in authorities):
        raise RetentionError("One or more protected authorities failed before audit")

    started = time.time()
    print("AUDIT package equivalence", flush=True)
    package_candidates, retained_packages = find_package_candidates(project)
    mismatched = [item for item in package_candidates if item["status"] != "EQUIVALENT"]
    if mismatched:
        raise RetentionError(
            "Expanded package mismatch: "
            + ", ".join(item["path"] for item in mismatched)
        )
    print("AUDIT disposable work inventory", flush=True)
    work_candidates, retained_work = find_work_candidates(project)
    print("AUDIT generated build inventory", flush=True)
    generated_candidates = find_generated_candidates(project)

    cache_candidate: list[dict[str, Any]] = []
    local_dotnet_home = project / ".dotnet-home"
    local_cache = local_dotnet_home / ".nuget" / "packages"
    canonical_cache = project / "03-builds" / "nuget-packages"
    if local_cache.exists():
        cache_equivalence = compare_trees(local_cache, canonical_cache)
        files, size = measure(local_dotnet_home)
        cache_candidate.append(
            {
                "kind": "duplicate-nuget-cache",
                "path": local_dotnet_home.relative_to(project).as_posix(),
                "comparedPath": local_cache.relative_to(project).as_posix(),
                "canonicalPath": canonical_cache.relative_to(project).as_posix(),
                "fileCount": files,
                "bytes": size,
                **cache_equivalence,
            }
        )
        if cache_equivalence["status"] != "EQUIVALENT":
            raise RetentionError("Project-local NuGet cache is not identical to canonical cache")

    candidates = (
        package_candidates + work_candidates + generated_candidates + cache_candidate
    )
    before_files, before_bytes = measure(project)
    reclaim_bytes = sum(int(item["bytes"]) for item in candidates)
    report = {
        "schema": SCHEMA,
        "operation": "AUDIT",
        "status": "PASS",
        "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "sourceCommit": source_commit,
        "projectRoot": str(project),
        "dirtyStatusSha256": sha256_bytes(dirty),
        "dirtyStatusEntryCount": len([item for item in dirty.split(b"\0") if item]),
        "preCleanup": {
            "fileCount": before_files,
            "bytes": before_bytes,
            "gib": round(before_bytes / (1024**3), 6),
        },
        "protected": {
            "authorities": authorities,
            "untouchedRoots": [
                "01-source-copies",
                "02-normalized-resources",
                "05-reports",
                "Screenshots",
                "docs",
            ],
            "retainedExpandedPackages": retained_packages,
            "retainedWorkEvidence": retained_work,
        },
        "candidates": candidates,
        "candidateInventorySha256": candidate_records_digest(candidates),
        "candidateCount": len(candidates),
        "estimatedReclaimBytes": reclaim_bytes,
        "estimatedReclaimGiB": round(reclaim_bytes / (1024**3), 6),
        "durationSeconds": round(time.time() - started, 3),
    }
    write_new_json(output, report)
    print(f"AUDIT PASS {output}", flush=True)
    print(f"RECLAIM {report['estimatedReclaimGiB']:.3f} GiB", flush=True)
    return 0


def load_bound_manifest(path: Path, expected_sha256: str) -> dict[str, Any]:
    actual = sha256_file(path)
    if actual != expected_sha256.upper():
        raise RetentionError(
            f"Manifest hash mismatch: expected {expected_sha256.upper()}, actual {actual}"
        )
    value = json.loads(path.read_text(encoding="utf-8"))
    if value.get("schema") != SCHEMA or value.get("operation") != "AUDIT":
        raise RetentionError("Unsupported audit manifest")
    if value.get("status") != "PASS":
        raise RetentionError("Audit manifest is not PASS")
    return value


def load_resume_report(
    path: Path,
    expected_sha256: str,
    manifest: dict[str, Any],
) -> dict[str, Any]:
    actual = sha256_file(path)
    if actual != expected_sha256.upper():
        raise RetentionError(
            f"Resume-report hash mismatch: expected {expected_sha256.upper()}, actual {actual}"
        )
    value = json.loads(path.read_text(encoding="utf-8"))
    if (
        value.get("schema") != SCHEMA
        or value.get("operation") != "APPLY"
        or value.get("status") != "FAIL"
    ):
        raise RetentionError("Resume report must be a failed APPLY report")
    if value.get("auditManifestSha256") != manifest.get("_boundSha256"):
        raise RetentionError("Resume report references a different audit manifest")
    deleted = value.get("deleted")
    if not isinstance(deleted, list):
        raise RetentionError("Resume report has no deleted-path sequence")
    candidates = manifest["candidates"]
    if len(deleted) > len(candidates):
        raise RetentionError("Resume report deleted count exceeds audit candidates")
    for index, prior in enumerate(deleted):
        expected = candidates[index]
        for field in ("kind", "path", "fileCount", "bytes"):
            if prior.get(field) != expected.get(field):
                raise RetentionError(
                    f"Resume report is not the audit prefix at index {index}: {field}"
                )
    return value


def validate_candidate(project: Path, item: dict[str, Any]) -> Path:
    path = assert_inside(project / item["path"], project)
    kind = item["kind"]
    if is_reparse(path):
        raise RetentionError(f"Reparse candidate refused: {path}")
    if kind == "zip-equivalent-expanded-package":
        if item.get("status") != "EQUIVALENT":
            raise RetentionError(f"Package candidate is not equivalent: {path}")
        if path.parent != (project / "04-packages").resolve():
            raise RetentionError(f"Package candidate outside package root: {path}")
        match = PREVIEW_PATTERN.match(path.name)
        if match is None or int(match.group(1)) in RETAIN_EXPANDED_PREVIEWS:
            raise RetentionError(f"Protected expanded package refused: {path}")
    elif kind == "disposable-work-output":
        work = (project / "03-builds" / "work").resolve()
        assert_inside(path, work)
        relative = path.relative_to(work)
        if relative in {Path(item) for item in RETAIN_WORK_PATHS}:
            raise RetentionError(f"Protected work evidence refused: {path}")
    elif kind == "generated-build-output":
        if path.name.lower() not in {"bin", "obj"}:
            raise RetentionError(f"Non-generated path refused: {path}")
        allowed = [(project / name).resolve() for name in ("src", "tests", "tmp")]
        if not any(path.is_relative_to(root) for root in allowed):
            raise RetentionError(f"Generated path outside allowed roots: {path}")
    elif kind == "generated-test-residue":
        relative = path.relative_to(project).as_posix()
        is_wpf_temp = (
            path.is_file()
            and path.name.endswith("_wpftmp.csproj")
            and path.is_relative_to((project / "src").resolve())
        )
        is_test_scratch = (
            path.is_dir()
            and path.is_relative_to((project / "tests").resolve())
            and re.fullmatch(
                r"\.(?:scratch|work|output)-[A-Za-z0-9_.-]+",
                path.name,
            )
            is not None
        )
        if not is_wpf_temp and not is_test_scratch:
            raise RetentionError(f"Unexpected generated test residue refused: {relative}")
    elif kind == "duplicate-nuget-cache":
        expected = (project / ".dotnet-home").resolve()
        if path != expected or item.get("status") != "EQUIVALENT":
            raise RetentionError(f"Unexpected cache target refused: {path}")
    else:
        raise RetentionError(f"Unknown candidate kind: {kind}")
    files, size = measure(path)
    if files != int(item["fileCount"]) or size != int(item["bytes"]):
        raise RetentionError(
            f"Candidate changed since audit: {path} "
            f"(expected {item['fileCount']}/{item['bytes']}, actual {files}/{size})"
        )
    return path


def remove_exact_path(path: Path) -> None:
    if not path.is_dir():
        path.unlink()
        return
    target = path.resolve(strict=True)

    def clear_readonly_and_retry(
        function: Any,
        failing_path_value: str,
        error: BaseException,
    ) -> None:
        failing_path = Path(failing_path_value)
        if not isinstance(error, PermissionError):
            raise error
        resolved = failing_path.resolve(strict=True)
        if resolved != target and not resolved.is_relative_to(target):
            raise RetentionError(
                f"Read-only retry escaped exact deletion target: {resolved}"
            )
        if is_reparse(failing_path):
            raise RetentionError(f"Read-only retry refused reparse path: {resolved}")
        attributes = failing_path.stat().st_mode
        os.chmod(failing_path, attributes | stat.S_IWRITE)
        function(failing_path_value)

    shutil.rmtree(path, onexc=clear_readonly_and_retry)


def apply(args: argparse.Namespace) -> int:
    manifest_path = Path(args.manifest).resolve(strict=True)
    manifest = load_bound_manifest(manifest_path, args.expected_manifest_sha256)
    manifest["_boundSha256"] = args.expected_manifest_sha256.upper()
    project = Path(manifest["projectRoot"]).resolve(strict=True)
    output = Path(args.output).resolve(strict=False)
    if output.exists():
        raise RetentionError(f"Output already exists: {output}")
    workspace = project.parents[1]
    source_commit = git_value(workspace, "rev-parse", "HEAD").decode("ascii").strip().upper()
    intervening_paths = validate_head_transition(
        workspace,
        manifest["sourceCommit"],
        source_commit,
        args.expected_current_head,
        args.allow_intervening_path,
    )
    project_prefix = project.relative_to(workspace).as_posix().rstrip("/") + "/"
    candidate_prefixes = [
        project_prefix + item["path"].replace("\\", "/").strip("/") + "/"
        for item in manifest["candidates"]
    ]
    authority_paths = {
        project_prefix + item["path"].replace("\\", "/").strip("/")
        for item in manifest["protected"]["authorities"]
    }
    for changed in intervening_paths:
        changed_prefix = changed.rstrip("/") + "/"
        if changed in authority_paths or any(
            changed == prefix.rstrip("/")
            or changed_prefix.startswith(prefix)
            or prefix.startswith(changed_prefix)
            for prefix in candidate_prefixes
        ):
            raise RetentionError(
                f"Intervening tracked path intersects cleanup/protected scope: {changed}"
            )
    authorities = authority_inventory(project)
    if any(item["status"] != "PASS" for item in authorities):
        raise RetentionError("One or more protected authorities failed before apply")
    all_candidates = manifest["candidates"]
    signed_records_digest = candidate_records_digest(all_candidates)
    if signed_records_digest != args.expected_candidate_records_sha256.upper():
        raise RetentionError(
            "Signed candidate-record digest mismatch: "
            f"{signed_records_digest} != "
            f"{args.expected_candidate_records_sha256.upper()}"
        )
    prior_deleted: list[dict[str, Any]] = []
    resume_report: dict[str, Any] | None = None
    if args.resume_from_report:
        if not args.expected_resume_report_sha256:
            raise RetentionError(
                "--expected-resume-report-sha256 is required with --resume-from-report"
            )
        resume_path = Path(args.resume_from_report).resolve(strict=True)
        resume_report = load_resume_report(
            resume_path,
            args.expected_resume_report_sha256,
            manifest,
        )
        prior_deleted = resume_report["deleted"]
        for item in prior_deleted:
            if (project / item["path"]).exists():
                raise RetentionError(
                    f"Previously deleted resume target exists again: {item['path']}"
                )
    elif args.expected_resume_report_sha256:
        raise RetentionError(
            "--expected-resume-report-sha256 requires --resume-from-report"
        )
    candidates = all_candidates[len(prior_deleted) :]
    validated = [validate_candidate(project, item) for item in candidates]

    started = time.time()
    deleted: list[dict[str, Any]] = []
    failure: str | None = None
    for index, (item, path) in enumerate(zip(candidates, validated), start=1):
        print(f"DELETE {index}/{len(candidates)} {item['path']}", flush=True)
        try:
            path = validate_candidate(project, item)
            remove_exact_path(path)
            deleted.append(
                {
                    "kind": item["kind"],
                    "path": item["path"],
                    "fileCount": item["fileCount"],
                    "bytes": item["bytes"],
                }
            )
        except (OSError, PermissionError) as error:
            failure = f"{type(error).__name__}: {error}"
            break

    post_authorities = authority_inventory(project)
    protected_pass = all(item["status"] == "PASS" for item in post_authorities)
    after_files, after_bytes = measure(project)
    deleted_bytes = sum(int(item["bytes"]) for item in deleted)
    report = {
        "schema": SCHEMA,
        "operation": "APPLY",
        "status": (
            "PASS"
            if failure is None and len(deleted) == len(candidates) and protected_pass
            else "FAIL"
        ),
        "createdUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "sourceCommit": source_commit,
        "auditedSourceCommit": manifest["sourceCommit"],
        "interveningTrackedPaths": intervening_paths,
        "projectRoot": str(project),
        "auditManifest": str(manifest_path),
        "auditManifestSha256": args.expected_manifest_sha256.upper(),
        "auditInventorySha256": manifest["candidateInventorySha256"],
        "candidateRecordsSha256": signed_records_digest,
        "resumeFromReport": (
            str(Path(args.resume_from_report).resolve(strict=True))
            if args.resume_from_report
            else None
        ),
        "resumeReportSha256": (
            args.expected_resume_report_sha256.upper()
            if args.expected_resume_report_sha256
            else None
        ),
        "priorDeletedCount": len(prior_deleted),
        "priorDeletedBytes": sum(int(item["bytes"]) for item in prior_deleted),
        "deleted": deleted,
        "deletedCount": len(deleted),
        "deletedBytes": deleted_bytes,
        "deletedGiB": round(deleted_bytes / (1024**3), 6),
        "cumulativeDeletedCount": len(prior_deleted) + len(deleted),
        "cumulativeDeletedBytes": (
            sum(int(item["bytes"]) for item in prior_deleted) + deleted_bytes
        ),
        "cumulativeDeletedGiB": round(
            (
                sum(int(item["bytes"]) for item in prior_deleted)
                + deleted_bytes
            )
            / (1024**3),
            6,
        ),
        "failure": failure,
        "postCleanup": {
            "fileCount": after_files,
            "bytes": after_bytes,
            "gib": round(after_bytes / (1024**3), 6),
        },
        "protectedAuthorities": post_authorities,
        "durationSeconds": round(time.time() - started, 3),
    }
    write_new_json(output, report)
    print(f"APPLY {report['status']} {output}", flush=True)
    print(f"RECLAIMED {report['deletedGiB']:.3f} GiB", flush=True)
    return 0 if report["status"] == "PASS" else 1


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    audit_parser = subparsers.add_parser("audit")
    audit_parser.add_argument("--project-root", required=True)
    audit_parser.add_argument("--output", required=True)
    apply_parser = subparsers.add_parser("apply")
    apply_parser.add_argument("--manifest", required=True)
    apply_parser.add_argument("--expected-manifest-sha256", required=True)
    apply_parser.add_argument("--expected-candidate-records-sha256", required=True)
    apply_parser.add_argument("--output", required=True)
    apply_parser.add_argument("--expected-current-head")
    apply_parser.add_argument("--allow-intervening-path", action="append", default=[])
    apply_parser.add_argument("--resume-from-report")
    apply_parser.add_argument("--expected-resume-report-sha256")
    return parser.parse_args()


def main() -> int:
    try:
        args = parse_arguments()
        if args.command == "audit":
            return audit(args)
        return apply(args)
    except (RetentionError, OSError, ValueError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        print(f"ERROR {type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
