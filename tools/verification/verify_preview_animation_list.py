"""Independently verify the manifest-bound animation picker taxonomy artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def verify(path: Path, manifest: Path, expected_filter: str, expected_visible: int,
           expected_id: str, expected_category: str | None) -> None:
    response = read(path)
    artifact = response.get("artifact")
    require(response.get("succeeded") is True and artifact, f"{path} did not report success")
    require(artifact.get("schemaVersion") == "1", "unexpected animation-list schema")
    require(artifact.get("artifactKind") == "preview-animation-list", "unexpected artifact kind")
    require(artifact.get("edition") == "fallout4", "fixture must bind Fallout 4")
    require(artifact.get("inputManifestSha256", "").casefold() ==
            hashlib.sha256(manifest.read_bytes()).hexdigest().casefold(),
            "manifest hash is not bound")
    require(artifact.get("filter") == expected_filter, "filter was not preserved")
    require(artifact.get("visibleCount") == expected_visible, "unexpected visible count")
    items = artifact.get("items")
    require(isinstance(items, list) and items and items[0].get("id") == expected_id,
            "expected animation row is missing")
    if expected_category is not None:
        require(items[0].get("category") == expected_category, "category was not preserved")
        require(items[0].get("fromBehaviorGraph") is False,
                "IDLE/dialogue row was incorrectly marked as behavior-graph sourced")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--default", type=Path, required=True)
    parser.add_argument("--filtered", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    verify(args.default, args.manifest, "", 3, "walk", None)
    verify(args.filtered, args.manifest, "talk gesture", 1, "talk", "Talk")
    print(f"PREVIEW ANIMATION-LIST INDEPENDENT PASS default={args.default}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError) as error:
        raise SystemExit(f"PREVIEW ANIMATION-LIST INDEPENDENT FAIL: {error}")
