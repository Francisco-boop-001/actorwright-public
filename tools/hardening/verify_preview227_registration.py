#!/usr/bin/env python3
"""Verify preview.227 package continuity and its static placement boundary."""

from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
from typing import Any


_PREVIOUS = Path(__file__).with_name("verify_preview226_registration.py")
_SPEC = importlib.util.spec_from_file_location("_preview226_registration", _PREVIOUS)
if _SPEC is None or _SPEC.loader is None:
    raise RuntimeError(f"could not load prior registration verifier: {_PREVIOUS}")
_MODULE = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_MODULE)

EXPECTED_PREVIOUS_COMMANDS = 133
EXPECTED_CURRENT_COMMANDS = 136
EXPECTED_ADDITIONS = {
    "npc placement interior analyze",
    "npc placement interior apply",
    "npc placement interior verify",
}
EXPECTED_SOURCE_FILES = {
    "src/NpcManager.Application/SkyrimInteriorPlacementContracts.cs",
    "src/NpcManager.Cli/SkyrimInteriorPlacementCommandHandler.cs",
    "src/NpcManager.Formats.Bethesda/BethesdaSkyrimInteriorPlacementTopologyVerifier.cs",
    "src/NpcManager.Formats.Bethesda/BethesdaSkyrimInteriorPlacementTopologyWriter.cs",
    "src/NpcManager.Infrastructure/SkyrimInteriorPlacementDocumentCodec.cs",
    "src/NpcManager.Pipeline/SkyrimInteriorPlacementService.cs",
}


def _sha256(path: Path) -> str:
    return _MODULE._sha256(path)


def _archive_path(package_root: Path) -> Path:
    return _MODULE._archive_path(package_root)


def _command_names(executable: Path) -> set[str]:
    return _MODULE._command_names(executable)


def _verify_package(package_root: Path, expected_version: str) -> list[str]:
    return _MODULE._verify_package(package_root, expected_version)


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
        checks.append(
            {
                "id": identifier,
                "expected": expected,
                "actual": actual,
                "outcome": "FAIL" if failed else "PASS",
            }
        )

    if mode not in {"candidate", "registered"}:
        errors.append("mode must be candidate or registered")

    try:
        previous_names = _command_names(previous_package / "cli" / "npcm.exe")
        current_names = _command_names(candidate_package / "cli" / "npcm.exe")
        additions = current_names - previous_names
        removals = previous_names - current_names
        failed = (
            len(previous_names) != EXPECTED_PREVIOUS_COMMANDS
            or len(current_names) != EXPECTED_CURRENT_COMMANDS
            or additions != EXPECTED_ADDITIONS
            or bool(removals)
        )
        if failed:
            errors.append(
                "command catalogue mismatch: "
                f"previous={len(previous_names)}, current={len(current_names)}, "
                f"additions={sorted(additions)}, removals={sorted(removals)}"
            )
        check(
            "command-catalogue",
            "133 -> 136 with exactly the three interior-placement commands",
            {
                "previous": len(previous_names),
                "current": len(current_names),
                "additions": sorted(additions),
                "removals": sorted(removals),
            },
            failed,
        )
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        errors.append(f"command catalogue verification failed: {exc}")
        check("command-catalogue", "133 -> 136", str(exc), True)

    package_errors = _verify_package(candidate_package, "1.0.0-preview.227")
    errors.extend(package_errors)
    check(
        "candidate-package",
        "manifest, hash manifest, archive, and scoped runtime claim",
        "verified" if not package_errors else package_errors,
        bool(package_errors),
    )

    try:
        delta = json.loads(source_delta.read_text(encoding="utf-8"))
        expected_source = (candidate_package / "source").resolve().as_posix()
        actual_source = Path(str(delta.get("workingProject", ""))).resolve().as_posix()
        delta_rows = delta.get("delta")
        present = {
            row.get("path")
            for row in delta_rows
            if isinstance(row, dict) and isinstance(row.get("path"), str)
        } if isinstance(delta_rows, list) else set()
        missing_source_files = sorted(EXPECTED_SOURCE_FILES - present)
        delta_ok = delta.get("outcome") == "PASS" and not delta.get("unclassified")
        if not delta_ok:
            errors.append("source-delta report is not a complete PASS")
        if actual_source != expected_source:
            errors.append("source-delta report does not bind the candidate source root")
        if missing_source_files:
            errors.append(f"placement source files are absent from source delta: {missing_source_files}")
        check(
            "source-delta",
            {"outcome": "PASS", "workingProject": expected_source, "placementFiles": sorted(EXPECTED_SOURCE_FILES)},
            {"outcome": delta.get("outcome"), "workingProject": actual_source, "missingPlacementFiles": missing_source_files},
            not delta_ok or actual_source != expected_source or bool(missing_source_files),
        )
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        errors.append(f"source-delta report is invalid: {exc}")
        check("source-delta", "PASS", str(exc), True)

    check(
        "placement-authority-boundary",
        {
            "runtimeAuthority": False,
            "visualAuthority": False,
            "pathingAuthority": False,
            "conflictFree": False,
        },
        {
            "runtimeAuthority": False,
            "visualAuthority": False,
            "pathingAuthority": False,
            "conflictFree": False,
        },
    )
    report = {
        "schemaVersion": 1,
        "artifactKind": "npc-manager-preview227-candidate-continuity",
        "mode": mode,
        "outcome": "PASS" if not errors else "FAIL",
        "runtimeAuthority": False,
        "visualAuthority": False,
        "pathingAuthority": False,
        "conflictFree": False,
        "checks": checks,
        "errors": errors,
        "packages": {
            "previous": previous_package.resolve().as_posix(),
            "candidate": candidate_package.resolve().as_posix(),
            "candidateArchiveSha256": _sha256(_archive_path(candidate_package))
            if _archive_path(candidate_package).is_file()
            else None,
        },
        "repositoryRoot": repo_root.resolve().as_posix(),
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
