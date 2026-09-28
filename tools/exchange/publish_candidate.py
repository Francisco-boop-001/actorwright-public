#!/usr/bin/env python3
"""Publish a validated release-candidate or promotion bundle with no replacement."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path, PurePosixPath

from validate_bundle import (
    BundleError,
    PublicationError,
    _cleanup_stage,
    _destination,
    _assert_stage_identity,
    _owned_stage,
    _publish_no_replace,
    _read_json,
    validate,
)


def _copy_exact(source: Path, stage, relative_paths: list[str]) -> None:
    _assert_stage_identity(stage)
    stage_path = stage.path
    for relative in ["bundle-manifest.json", *relative_paths]:
        source_path = source.joinpath(*PurePosixPath(relative).parts)
        target_path = stage_path.joinpath(*PurePosixPath(relative).parts)
        if target_path.parent != stage_path:
            target_path.parent.mkdir(parents=True, exist_ok=True)
        # read/write exact bytes; this avoids text normalization and ensures a
        # late source replacement cannot silently be represented by metadata.
        target_path.write_bytes(source_path.read_bytes())
        _assert_stage_identity(stage)


def publish(
        source: Path,
        output: Path,
        expected_kind: str = "release-candidate") -> dict[str, object]:
    if expected_kind not in {"release-candidate", "promotion"}:
        raise PublicationError(
            f"unsupported publication kind: {expected_kind}")
    source_result = validate(source, expected_kind)
    bundle_name = expected_kind.replace("-", " ")
    if source_result["layout"] != "standard":
        raise PublicationError(f"{bundle_name} must use the standard root layout")
    source_root = Path(str(source_result["bundleRoot"]))
    source_manifest = _read_json(Path(str(source_result["manifestPath"])))
    relative_paths = [str(row["path"]) for row in source_manifest["files"]]
    parent, final = _destination(output)
    stage = _owned_stage(parent, final.name)
    try:
        _copy_exact(source_root, stage, relative_paths)
        staged_result = validate(stage.path, expected_kind)
        if staged_result["manifestSha256"] != source_result["manifestSha256"]:
            raise PublicationError(f"staged {bundle_name} manifest bytes changed")
        _publish_no_replace(stage, final)
        staged_result["output"] = str(final)
        return staged_result
    except Exception:
        if stage.path.exists():
            _cleanup_stage(stage)
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument(
        "--expect-kind",
        choices=("release-candidate", "promotion"),
        default="release-candidate")
    args = parser.parse_args()
    try:
        result = publish(
            Path(args.source), Path(args.output), expected_kind=args.expect_kind)
    except (BundleError, OSError, json.JSONDecodeError) as exc:
        label = "candidate" if args.expect_kind == "release-candidate" else "promotion"
        print(f"FAIL: {label} publication refused: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
