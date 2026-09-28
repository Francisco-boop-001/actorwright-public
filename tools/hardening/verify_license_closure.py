#!/usr/bin/env python3
"""Verify the reviewed license closure against K-local package bytes and evidence."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path
from typing import Any
from zipfile import ZipFile


EXPECTED: dict[str, str] = {
    "K4os.Compression.LZ4/1.3.8": "MIT",
    "K4os.Compression.LZ4.Streams/1.3.8": "MIT",
    "K4os.Hash.xxHash/1.0.8": "MIT",
    "Loqui/3.2.0": "GPL-3.0-only",
    "Noggog.CSharpExt/3.1.0": "GPL-3.0-only",
    "OneOf/3.0.271": "MIT",
    "Reloaded.Memory/9.4.1": "GPL-3.0-only",
}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _review(project_root: Path) -> dict[str, Any]:
    return json.loads(
        (project_root / "05-reports" / "m2-mutagen-license-review.json").read_text(encoding="utf-8")
    )


def _hash_index(project_root: Path) -> dict[str, dict[str, str]]:
    path = project_root / "05-reports" / "m2-mutagen-package-sha256.csv"
    with path.open(encoding="utf-8", newline="") as stream:
        return {f"{row['id']}/{row['version']}": row for row in csv.DictReader(stream)}


def _package_path(project_root: Path, row: dict[str, str]) -> Path:
    return project_root / ".." / ".." / "tools" / "external" / "nuget-feed" / row["file"]


def verify(project_root: Path) -> dict[str, Any]:
    errors: list[str] = []
    review = _review(project_root)
    index = _hash_index(project_root)
    mapped: dict[str, str] = {}
    for expression_key in ("gpl3Only", "mit"):
        for package in review.get(expression_key, []):
            mapped[package] = "GPL-3.0-only" if expression_key == "gpl3Only" else "MIT"
    if review.get("nuspecWithoutSpdxExpression"):
        errors.append("review still contains packages without SPDX expressions")
    if not set(EXPECTED).issubset(mapped):
        errors.append("reviewed package set is missing a formerly unresolved package")
    evidence = review.get("licenseEvidence", {})
    if not isinstance(evidence, dict):
        errors.append("licenseEvidence is missing")
        evidence = {}

    for package, expected_expression in sorted(EXPECTED.items()):
        if mapped.get(package) != expected_expression:
            errors.append(f"license expression mismatch: {package}")
        item = evidence.get(package)
        if not isinstance(item, dict) or item.get("expression") != expected_expression:
            errors.append(f"missing or mismatched source evidence: {package}")
        else:
            for field in ("repository", "licenseUrl", "source"):
                if not isinstance(item.get(field), str) or not item[field].startswith("http") and field != "source":
                    errors.append(f"missing {field} evidence: {package}")
        row = index.get(package)
        if row is None:
            errors.append(f"missing K-local hash row: {package}")
            continue
        path = _package_path(project_root, row)
        if not path.is_file():
            errors.append(f"missing K-local package: {path}")
            continue
        if _sha256(path) != row["sha256"]:
            errors.append(f"package hash changed: {package}")
        with ZipFile(path) as archive:
            names = set(archive.namelist())
            if package == "Reloaded.Memory/9.4.1" and "LICENSE.md" not in names:
                errors.append("Reloaded.Memory package LICENSE.md is missing")

    return {
        "result": "PASS" if not errors else "FAIL",
        "packageCount": len(EXPECTED),
        "unknownLicenseCount": len(review.get("nuspecWithoutSpdxExpression", [])),
        "evidenceCount": len(evidence),
        "errors": errors,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    output = args.output if args.output.is_absolute() else project_root / args.output
    if output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    report = verify(project_root)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(report))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
