#!/usr/bin/env python3
"""Generate a deterministic SPDX 2.3 package inventory from restored assets files."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path


def spdx_id(name: str, version: str) -> str:
    token = re.sub(r"[^A-Za-z0-9.-]", "-", f"{name}-{version}")
    return f"SPDXRef-Package-{token}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--created-utc", required=True)
    args = parser.parse_args()
    root = Path(args.root).resolve()
    output = Path(args.output).resolve()
    if output.exists() or output.is_symlink():
        print(f"FAIL: SBOM output already exists: {output}", file=sys.stderr)
        return 2
    packages: dict[tuple[str, str], dict] = {}
    assets_files = sorted(root.glob("**/obj/project.assets.json"))
    if not assets_files:
        print("FAIL: no restored project.assets.json files found", file=sys.stderr)
        return 2
    for assets_path in assets_files:
        assets = json.loads(assets_path.read_text(encoding="utf-8-sig"))
        for key, metadata in assets.get("libraries", {}).items():
            if metadata.get("type") != "package" or "/" not in key:
                continue
            name, version = key.rsplit("/", 1)
            packages[(name, version)] = {
                "SPDXID": spdx_id(name, version),
                "name": name,
                "versionInfo": version,
                "downloadLocation": f"https://www.nuget.org/packages/{name}/{version}",
                "filesAnalyzed": False,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": "NOASSERTION",
                "copyrightText": "NOASSERTION",
                "externalRefs": [{
                    "referenceCategory": "PACKAGE-MANAGER",
                    "referenceType": "purl",
                    "referenceLocator": f"pkg:nuget/{name}@{version}",
                }],
            }
    document = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"Actorwright-{args.version}",
        "documentNamespace": f"https://actorwright.invalid/spdx/{args.version}/{args.commit}",
        "creationInfo": {"created": args.created_utc, "creators": ["Tool: Actorwright-generate-sbom"]},
        "packages": [packages[key] for key in sorted(packages, key=lambda item: (item[0].lower(), item[1]))],
        "documentDescribes": [packages[key]["SPDXID"] for key in sorted(packages, key=lambda item: (item[0].lower(), item[1]))],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    print(f"PASS SPDX packages={len(packages)} output={output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
