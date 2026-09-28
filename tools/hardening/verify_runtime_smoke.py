#!/usr/bin/env python3
"""Validate operator-supplied FO4/SSE runtime smoke reports without judging pixels."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
from typing import Any


REQUIRED_SCREENSHOTS = {
    "fallout4": ("face", "neck", "body", "hands", "eyes", "outfit"),
    "skyrimse": ("face", "neck", "body", "hands", "eyes", "hair", "outfit"),
}
MAXIMUM_REPORT_BYTES = 2 * 1024 * 1024
MAXIMUM_SCREENSHOT_BYTES = 64 * 1024 * 1024


def _has_supported_image_signature(path: Path) -> bool:
    """Match the typed verifier's bounded image-format gate without decoding pixels."""

    try:
        header = path.read_bytes()[:12]
    except OSError:
        return False
    return (
        header[:8] == b"\x89PNG\r\n\x1a\n"
        or header[:3] == b"\xff\xd8\xff"
        or header[:6] in (b"GIF87a", b"GIF89a")
        or header[:2] == b"BM"
        or (len(header) >= 12 and header[:4] == b"RIFF" and header[8:12] == b"WEBP")
    )


def _non_empty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _is_sha256(value: Any) -> bool:
    return isinstance(value, str) and len(value) == 64 and all(
        character in "0123456789abcdefABCDEF" for character in value
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _load(path: Path) -> dict[str, Any]:
    # Windows PowerShell's UTF-8 writer may emit a BOM; accepting it here
    # keeps the independent verifier aligned with the CLI's UTF-8 JSON output
    # without weakening the schema or path gates.
    try:
        size = path.stat().st_size
    except OSError as exc:
        raise ValueError(f"report could not be inspected safely: {path}") from exc
    if size <= 0 or size > MAXIMUM_REPORT_BYTES:
        raise ValueError(f"report exceeds the accepted size bound: {path}")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"report is not a JSON object: {path}")
    return value


def _under(root: Path, candidate: str) -> Path | None:
    if _has_alternate_data_stream_delimiter(candidate):
        return None
    path = Path(candidate)
    if path.is_absolute():
        raw = Path(os.path.abspath(path))
    else:
        raw = Path(os.path.abspath(root / path))
    if _has_reparse_component(raw):
        return None
    try:
        resolved = raw.resolve()
    except OSError:
        return None
    if resolved == root or root not in resolved.parents:
        return None
    return resolved


def _has_alternate_data_stream_delimiter(candidate: str) -> bool:
    """Reject Windows alternate-data-stream syntax while allowing a drive colon."""

    text = str(candidate)
    if len(text) >= 2 and text[1] == ":":
        text = text[2:]
    return ":" in text


def _has_reparse_component(path: Path) -> bool:
    """Reject symlinks and Windows reparse points before resolving a lab path."""

    current = path
    while True:
        try:
            stat_result = current.lstat()
            if current.is_symlink() or bool(getattr(stat_result, "st_file_attributes", 0) & 0x400):
                return True
        except FileNotFoundError:
            # A missing leaf is diagnosed by the caller; inspect existing parents.
            pass
        except OSError:
            return True
        parent = current.parent
        if parent == current:
            return False
        current = parent


def _local_input(project_root: Path, path: Path, label: str, errors: list[str]) -> Path | None:
    resolved = _under(project_root, str(path))
    if resolved is None:
        errors.append(f"{label} must be an existing K-local project file")
        return None
    return resolved


def _validate_report(project_root: Path, report_path: Path, expected_game: str, archive_sha: str) -> list[str]:
    errors: list[str] = []
    try:
        report = _load(report_path)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        return [str(exc)]
    if report.get("schemaVersion") != "1":
        errors.append(f"{expected_game}: schemaVersion must be 1")
    if report.get("game") != expected_game:
        errors.append(f"{expected_game}: game field mismatch")
    if report.get("status") != "PASS":
        errors.append(f"{expected_game}: status is not PASS")
    if report.get("controlNpcSameFrame") is not True:
        errors.append(f"{expected_game}: control NPC same-frame attestation is required")
    if report.get("providerMatchesPackage") is not True:
        errors.append(f"{expected_game}: provider-match attestation is required")
    package = report.get("package")
    package_hash = package.get("archiveSha256") if isinstance(package, dict) else None
    if not _is_sha256(package_hash) or package_hash.lower() != archive_sha.lower():
        errors.append(f"{expected_game}: package archive hash does not match acceptance report")

    target = report.get("target")
    if not isinstance(target, dict):
        errors.append(f"{expected_game}: target identity is missing")
    else:
        for field in ("formId", "plugin", "faceGeomProvider", "faceTintProvider", "bodySkinProvider", "outfitProvider"):
            value = target.get(field)
            if not _non_empty_string(value):
                errors.append(f"{expected_game}: target.{field} is missing")
        headparts = target.get("headpartProviders")
        if not isinstance(headparts, list) or not headparts or not all(_non_empty_string(item) for item in headparts):
            errors.append(f"{expected_game}: target.headpartProviders is missing")

    control = report.get("control")
    if not isinstance(control, dict) or not _non_empty_string(control.get("formId")) or not _non_empty_string(control.get("plugin")):
        errors.append(f"{expected_game}: control identity is missing")
    elif isinstance(target, dict) and _non_empty_string(target.get("formId")) and _non_empty_string(target.get("plugin")) and (
        target.get("formId").casefold() == control.get("formId").casefold()
        and target.get("plugin").casefold() == control.get("plugin").casefold()
    ):
        errors.append(f"{expected_game}: control NPC must be distinct from the target NPC")

    fingerprint = report.get("environmentFingerprint")
    if not isinstance(fingerprint, dict):
        errors.append(f"{expected_game}: environment fingerprint is missing")
    else:
        path_text = fingerprint.get("path")
        expected_hash = fingerprint.get("sha256")
        if not _non_empty_string(path_text) or not _is_sha256(expected_hash):
            errors.append(f"{expected_game}: environment fingerprint path/hash is missing")
        else:
            path = _under(project_root, path_text)
            if path is None or not path.is_file():
                errors.append(f"{expected_game}: environment fingerprint must be an existing K-local file")
            elif _sha256(path) != expected_hash:
                errors.append(f"{expected_game}: environment fingerprint hash mismatch")

    screenshots = report.get("screenshots")
    if not isinstance(screenshots, dict):
        errors.append(f"{expected_game}: screenshots are missing")
    else:
        for name in REQUIRED_SCREENSHOTS[expected_game]:
            path_text = screenshots.get(name)
            path = _under(project_root, path_text) if isinstance(path_text, str) else None
            if path is None or not path.is_file():
                errors.append(f"{expected_game}: screenshot '{name}' must be an existing K-local file")
            else:
                try:
                    too_large = path.stat().st_size > MAXIMUM_SCREENSHOT_BYTES
                except OSError:
                    errors.append(f"{expected_game}: screenshot '{name}' could not be inspected safely")
                    continue
                if too_large:
                    errors.append(f"{expected_game}: screenshot '{name}' exceeds the accepted size bound")
                elif not _has_supported_image_signature(path):
                    errors.append(f"{expected_game}: screenshot '{name}' is not a supported image file")

    operator = report.get("operator")
    if not isinstance(operator, dict) or not _non_empty_string(operator.get("name")) or not _non_empty_string(operator.get("capturedAt")):
        errors.append(f"{expected_game}: operator name and capturedAt are required")
    return errors


def verify(project_root: Path, fo4_path: Path, sse_path: Path, acceptance_path: Path) -> dict[str, Any]:
    errors: list[str] = []
    fo4_path = _local_input(project_root, fo4_path, "fallout4 runtime report", errors) or fo4_path
    sse_path = _local_input(project_root, sse_path, "skyrimse runtime report", errors) or sse_path
    acceptance_path = _local_input(project_root, acceptance_path, "package acceptance report", errors) or acceptance_path
    if errors:
        return {"result": "FAIL", "games": ["fallout4", "skyrimse"], "errors": errors}
    try:
        acceptance = _load(acceptance_path)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        return {"result": "FAIL", "errors": [str(exc)]}
    archive_sha = acceptance.get("packageArchiveSha256")
    if not _is_sha256(archive_sha):
        errors.append("package acceptance report has no archive SHA-256")
        archive_sha = ""
    if acceptance.get("status") not in ("PASS", "PASS_WITH_SCOPED_LIMITS"):
        errors.append("package acceptance report is not a passing scoped acceptance")
    if acceptance.get("runtimeReleaseClaim") is not False:
        errors.append("package acceptance report must set runtimeReleaseClaim to false")
    errors.extend(_validate_report(project_root, fo4_path, "fallout4", archive_sha))
    errors.extend(_validate_report(project_root, sse_path, "skyrimse", archive_sha))
    return {"result": "PASS" if not errors else "FAIL", "games": ["fallout4", "skyrimse"], "errors": errors}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fallout4", type=Path, required=True)
    parser.add_argument("--skyrimse", type=Path, required=True)
    parser.add_argument("--package-acceptance", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    output = _under(project_root, str(args.output))
    if output is None:
        raise SystemExit("refusing to write runtime-smoke verification outside the K-local project root or through a reparse point")
    if output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {output}")
    result = verify(
        project_root,
        args.fallout4.resolve(),
        args.skyrimse.resolve(),
        args.package_acceptance.resolve(),
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(result))
    return 0 if result["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
