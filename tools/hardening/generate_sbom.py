#!/usr/bin/env python3
"""Generate a deterministic SBOM/license inventory from locked NuGet inputs.

This intentionally uses only project-local lock files and the reviewed license
intake. Unknown SPDX expressions remain visible and block a distributable
release; they are never guessed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


def _load(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _license_index(review: dict[str, Any]) -> dict[str, str]:
    result: dict[str, str] = {}
    for name in review.get("gpl3Only", []):
        result[name] = "GPL-3.0-only"
    for name in review.get("mit", []):
        result[name] = "MIT"
    for name in review.get("nuspecWithoutSpdxExpression", []):
        result[name] = "UNKNOWN_REVIEW_REQUIRED"
    return result


def _sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _components(project_root: Path, license_index: dict[str, str]) -> list[dict[str, Any]]:
    found: dict[tuple[str, str], dict[str, Any]] = {}
    for lock_path in sorted((project_root / "src").glob("**/packages.lock.json")):
        lock = _load(lock_path)
        for framework in lock.get("dependencies", {}).values():
            for package, info in framework.items():
                if not isinstance(info, dict) or info.get("type") == "Project":
                    continue
                version = info.get("resolved")
                if not isinstance(version, str):
                    continue
                key = (package.lower(), version)
                entry = found.setdefault(key, {
                    "type": "library",
                    "bom-ref": f"pkg:nuget/{package}@{version}",
                    "group": "",
                    "name": package,
                    "version": version,
                    "scope": "required",
                    "hashes": [],
                    "licenses": [],
                    "evidence": [],
                })
                content_hash = info.get("contentHash")
                if isinstance(content_hash, str):
                    entry["hashes"] = [{"alg": "SHA-512", "content": content_hash}]
                license_name = license_index.get(f"{package}/{version}", "UNKNOWN_REVIEW_REQUIRED")
                entry["licenses"] = [{"license": {"id": license_name}}]
                evidence = str(lock_path.relative_to(project_root)).replace("\\", "/")
                if evidence not in entry["evidence"]:
                    entry["evidence"].append(evidence)

    components = []
    for entry in sorted(found.values(), key=lambda value: (value["name"].lower(), value["version"])):
        entry["evidence"] = sorted(entry["evidence"])
        components.append(entry)
    return components


def build(project_root: Path) -> dict[str, Any]:
    review_path = project_root / "05-reports" / "m2-mutagen-license-review.json"
    review = _load(review_path)
    components = _components(project_root, _license_index(review))
    unknown = [
        f"{item['name']}/{item['version']}"
        for item in components
        if item["licenses"][0]["license"]["id"] == "UNKNOWN_REVIEW_REQUIRED"
    ]
    source = project_root / "tools" / "hardening" / "generate_sbom.py"
    source_hash = _sha256_file(source)
    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": "urn:uuid:npcmanager-reimplementation-locked-nuget-v1",
        "version": 1,
        "metadata": {
            "component": {"type": "application", "name": "NpcManagerReimplementation", "version": "0.1.0"},
            "tools": [{"vendor": "NpcManagerReimplementation", "name": "generate_sbom.py", "version": source_hash[:12]}],
        },
        "components": components,
        "properties": [
            {"name": "source.locked", "value": "true"},
            {"name": "license.review.status", "value": "PROVISIONAL_M8" if unknown else "PASS"},
            {"name": "license.review.unknownCount", "value": str(len(unknown))},
            {"name": "license.review.unknownPackages", "value": ",".join(unknown)},
        ],
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
    report = build(project_root)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"result": "PASS", "output": str(output), "components": len(report["components"])}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
