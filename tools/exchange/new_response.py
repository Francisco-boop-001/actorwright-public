#!/usr/bin/env python3
"""Create one immutable v2 response bound to validated issue/candidate bytes."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

from validate_bundle import (
    BundleError,
    CLASSIFICATIONS,
    MANIFEST_SCHEMA,
    RESPONSE_V2_SCHEMA,
    UTC,
    _cleanup_stage,
    _destination,
    _assert_stage_identity,
    _owned_stage,
    _publish_no_replace,
    _read_json,
    _validate_artifacts,
    validate,
)


def write_json(path: Path, value: object) -> None:
    path.write_bytes((json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8"))


def _artifact_rows(path: str | None) -> list[object]:
    if path is None:
        return []
    value = _read_json(Path(path))
    _validate_artifacts(value)
    return value


def create_response(
    issue_bundle: Path,
    candidate_bundle: Path,
    output: Path,
    *,
    classification: str,
    summary: str,
    recommended_action: str,
    created_utc: str,
    artifacts: list[object] | None = None,
) -> dict[str, object]:
    """Validate both source bundles, then publish a response in one move."""
    issue_result = validate(issue_bundle, "issue")
    candidate_result = validate(candidate_bundle, "release-candidate")
    if classification not in CLASSIFICATIONS:
        raise BundleError(f"invalid response classification: {classification}")
    if not UTC.fullmatch(created_utc):
        raise BundleError("created UTC must be a UTC second timestamp ending in Z")
    if not summary or not recommended_action:
        raise BundleError("summary and recommended action must be non-empty")
    rows = [] if artifacts is None else artifacts
    _validate_artifacts(rows)
    issue_payload = _read_json(Path(issue_result["payloadPath"]))
    issue_id = issue_payload.get("issueId") if isinstance(issue_payload, dict) else None
    if not isinstance(issue_id, str):
        raise BundleError("validated issue did not expose an issue ID")

    parent, final = _destination(output)
    stage = _owned_stage(parent, final.name)
    try:
        _assert_stage_identity(stage)
        payload = {
            "$schema": RESPONSE_V2_SCHEMA,
            "version": 2,
            "issueId": issue_id,
            "createdUtc": created_utc,
            "classification": classification,
            "issueManifestSha256": str(issue_result["manifestSha256"]),
            "candidateManifestSha256": str(candidate_result["manifestSha256"]),
            "summary": summary,
            "recommendedAction": recommended_action,
            "artifacts": rows,
        }
        payload_path = stage.path / "response.json"
        write_json(payload_path, payload)
        _assert_stage_identity(stage)
        raw = payload_path.read_bytes()
        write_json(stage.path / "bundle-manifest.json", {
            "$schema": MANIFEST_SCHEMA,
            "version": 1,
            "kind": "response",
            "payload": "response.json",
            "files": [{
                "path": "response.json", "size": len(raw),
                "sha256": hashlib.sha256(raw).hexdigest().upper(),
            }],
        })
        # Validate the complete staged tree, including the physical manifest
        # and the v2 payload, before exposing the final directory.
        result = validate(stage.path, "response")
        _publish_no_replace(stage, final)
        result["output"] = str(final)
        return result
    except Exception:
        if stage.path.exists():
            _cleanup_stage(stage)
        raise


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--issue-bundle", required=True)
    candidate_group = parser.add_mutually_exclusive_group(required=True)
    candidate_group.add_argument("--candidate-bundle", "--candidate")
    candidate_group.add_argument("--candidate-manifest")
    parser.add_argument("--output", required=True)
    parser.add_argument("--classification", choices=sorted(CLASSIFICATIONS), required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--recommended-action", required=True)
    parser.add_argument("--created-utc", required=True)
    parser.add_argument("--artifacts-json")
    args = parser.parse_args()
    try:
        candidate_root = (Path(args.candidate_bundle) if args.candidate_bundle
                          else Path(args.candidate_manifest).parent)
        if args.candidate_manifest and Path(args.candidate_manifest).name != "bundle-manifest.json":
            raise BundleError("--candidate-manifest must name bundle-manifest.json")
        result = create_response(
            Path(args.issue_bundle), candidate_root, Path(args.output),
            classification=args.classification, summary=args.summary,
            recommended_action=args.recommended_action,
            created_utc=args.created_utc, artifacts=_artifact_rows(args.artifacts_json),
        )
    except (BundleError, OSError, json.JSONDecodeError) as exc:
        print(f"FAIL: response publication refused: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
