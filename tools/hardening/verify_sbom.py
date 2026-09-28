#!/usr/bin/env python3
"""Independently verify the deterministic locked-dependency SBOM."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from generate_sbom import build


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("sbom", type=Path)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    path = args.sbom if args.sbom.is_absolute() else project_root / args.sbom
    try:
        actual = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(json.dumps({"result": "FAIL", "errors": [str(exc)]}, indent=2))
        return 1

    expected = build(project_root)
    errors: list[str] = []
    if actual != expected:
        errors.append("SBOM does not match the project-local lock/license inputs")
    if actual.get("bomFormat") != "CycloneDX" or actual.get("specVersion") != "1.5":
        errors.append("SBOM format/version is not CycloneDX 1.5")
    components = actual.get("components", [])
    if len(components) != 31:
        errors.append(f"expected 31 locked components, found {len(components)}")
    for component in components:
        if not component.get("hashes"):
            errors.append(f"missing content hash: {component.get('name')}/{component.get('version')}")
        if not component.get("evidence"):
            errors.append(f"missing lock evidence: {component.get('name')}/{component.get('version')}")
    if errors:
        print(json.dumps({"result": "FAIL", "errors": errors}, indent=2))
        return 1
    unknown = next(item["value"] for item in actual["properties"] if item["name"] == "license.review.unknownCount")
    print(json.dumps({"result": "PASS", "components": len(components), "unknown_license_count": int(unknown)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
