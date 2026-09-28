#!/usr/bin/env python3
"""Fail-closed Actorwright exchange-bundle validator.

The exchange contains a small, deliberately closed compatibility surface.  The
validator never rewrites an input bundle: it hashes the bytes that are on disk
and validates the JSON only after the physical inventory is closed.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import re
import shutil
import stat
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any


KINDS = {"issue", "response", "release-candidate", "promotion"}
SCHEMAS = {kind: f"urn:actorwright:exchange:v1:{kind}" for kind in KINDS}
RESPONSE_V2_SCHEMA = "actorwright-exchange-response/2"
MANIFEST_SCHEMA = "urn:actorwright:exchange:v1:bundle-manifest"
HASH = re.compile(r"^[A-Fa-f0-9]{64}$")
ISSUE_ID = re.compile(r"^NPCM-[0-9]{8}-[0-9]{4}$")
UTC = re.compile(r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")
CLASSIFICATIONS = {
    "BUG", "FEATURE_REQUEST", "EXPECTED_TYPED_REFUSAL",
    "CONSUMER_OR_ENVIRONMENT_FAILURE", "NEEDS_MORE_EVIDENCE",
}
LEGACY_NARRATIVE_CLASSIFICATIONS = CLASSIFICATIONS | {"DEFECT"}
SECRET_KEYS = {
    "token", "password", "passwd", "secret", "authorization", "apikey",
    "api_key", "privatekey", "private_key", "accesskey", "access_key",
}
FORBIDDEN_SUFFIXES = {".cs", ".csproj", ".sln", ".py", ".ps1", ".pem", ".key", ".pfx"}
REPARSE_POINT = 0x0400


class BundleError(ValueError):
    pass


class PublicationError(BundleError):
    pass


@dataclass(frozen=True)
class StageHandle:
    path: Path
    device: int
    inode: int


def _pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise BundleError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=_pairs)
    except BundleError:
        raise
    except Exception as exc:
        raise BundleError(f"invalid JSON {path.name}: {exc}") from exc


def _lexical_absolute(path: Path) -> Path:
    """Make a path absolute without resolving symlinks/reparse points."""
    return Path(os.path.abspath(os.fspath(path)))


def _lexists(path: Path) -> bool:
    return os.path.lexists(os.fspath(path))


def _assert_ordinary(path: Path, *, directory: bool) -> None:
    path = _lexical_absolute(path)
    try:
        value = path.lstat()
    except OSError as exc:
        raise BundleError(f"path is missing or cannot be inspected: {path}") from exc
    if path.is_symlink() or bool(getattr(value, "st_file_attributes", 0) & REPARSE_POINT):
        raise BundleError(f"reparse point refused: {path}")
    mode = value.st_mode
    if directory and not stat.S_ISDIR(mode):
        raise BundleError(f"ordinary directory required: {path}")
    if not directory and not stat.S_ISREG(mode):
        raise BundleError(f"ordinary file required: {path}")


def _assert_ordinary_ancestors(path: Path) -> None:
    """Reject reparse/non-directory ancestors without following them."""
    current = _lexical_absolute(path)
    while True:
        try:
            value = current.lstat()
        except OSError as exc:
            raise BundleError(f"path ancestor is missing or cannot be inspected: {current}") from exc
        if current.is_symlink() or bool(getattr(value, "st_file_attributes", 0) & REPARSE_POINT):
            raise BundleError(f"reparse ancestor refused: {current}")
        if not stat.S_ISDIR(value.st_mode):
            raise BundleError(f"non-directory ancestor refused: {current}")
        parent = current.parent
        if parent == current:
            break
        current = parent


def _relative(value: Any) -> str:
    if not isinstance(value, str) or not value or "\x00" in value or "\\" in value or ":" in value:
        raise BundleError(f"invalid relative path: {value!r}")
    pure = PurePosixPath(value)
    if (pure.is_absolute() or pure.as_posix() != value or
            any(part in {"", ".", ".."} for part in pure.parts)):
        raise BundleError(f"path traversal or non-canonical path: {value!r}")
    return value


def _require(obj: dict[str, Any], fields: set[str], allowed: set[str] | None = None) -> None:
    missing = fields - obj.keys()
    if missing:
        raise BundleError(f"missing required fields: {', '.join(sorted(missing))}")
    if allowed is not None:
        extra = obj.keys() - allowed
        if extra:
            raise BundleError(f"unexpected fields: {', '.join(sorted(extra))}")


def _scan_secrets(value: Any, location: str = "payload") -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if key.lower() in SECRET_KEYS:
                raise BundleError(f"secret-like field refused at {location}.{key}")
            _scan_secrets(child, f"{location}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _scan_secrets(child, f"{location}[{index}]")


def _common_payload(payload: dict[str, Any], kind: str) -> None:
    if payload.get("$schema") != SCHEMAS[kind] or payload.get("version") != 1:
        raise BundleError(f"payload schema/version mismatch for {kind}")
    _scan_secrets(payload)


_CLOSED_ARTIFACT_FIELDS = {
    "path", "sha256", "size", "provenance", "license", "redistributable",
}


def _validate_closed_artifact_rows(value: Any, *, context: str) -> None:
    if not isinstance(value, list):
        raise BundleError(f"{context} artifacts must be an array")
    for artifact in value:
        if not isinstance(artifact, dict):
            raise BundleError(f"{context} artifact must be an object")
        _require(artifact, _CLOSED_ARTIFACT_FIELDS, _CLOSED_ARTIFACT_FIELDS)
        if (not isinstance(artifact["provenance"], str) or not artifact["provenance"] or
                not isinstance(artifact["license"], str) or not artifact["license"] or
                artifact["redistributable"] is not True):
            raise BundleError(f"{context} artifact is non-redistributable or malformed")
        _relative(artifact["path"])
        if (not isinstance(artifact["sha256"], str) or
                not HASH.fullmatch(artifact["sha256"])):
            raise BundleError(f"{context} artifact hash is malformed")
        if (isinstance(artifact["size"], bool) or
                not isinstance(artifact["size"], int) or artifact["size"] < 0):
            raise BundleError(f"{context} artifact size is malformed")


def _validate_narrative_artifacts(value: Any) -> None:
    """Validate the closed artifact row shape admitted by both narratives."""
    _validate_closed_artifact_rows(value, context="historical narrative")


def _validate_artifacts(value: Any, *, strict_issue: bool = False) -> None:
    if not isinstance(value, list):
        raise BundleError("artifacts must be an array")
    if strict_issue:
        _validate_closed_artifact_rows(value, context="v1 issue")
        return
    for artifact in value:
        if not isinstance(artifact, dict):
            raise BundleError("artifact must be an object")
        if not artifact:
            raise BundleError("artifact must not be empty")
        if "path" in artifact:
            _relative(artifact["path"])
        if ("sha256" in artifact and
                (not isinstance(artifact["sha256"], str) or
                 not HASH.fullmatch(artifact["sha256"]))):
            raise BundleError("artifact hash is malformed")
        if ("size" in artifact and
                (isinstance(artifact["size"], bool) or
                 not isinstance(artifact["size"], int) or artifact["size"] < 0)):
            raise BundleError("artifact size is malformed")


def _validate_payload(payload: Any, kind: str, *, historical_layout: str | None = None) -> None:
    if not isinstance(payload, dict):
        raise BundleError("payload must be a JSON object")
    if kind == "issue" and historical_layout == "historical-follower-finish":
        required = {
            "$schema", "version", "issueId", "createdUtc", "toolVersion", "command",
            "requestedOutcome", "observedBehavior", "impact", "expectedBehavior",
            "reproduction", "diagnosticsObserved", "consumerErrorsAlreadyEliminated",
            "documentationGapsEncountered", "environment", "authorityNotClaimed",
        }
        _require(payload, required, required)
        _common_payload(payload, kind)
        if (not ISSUE_ID.fullmatch(str(payload["issueId"])) or
                not UTC.fullmatch(str(payload["createdUtc"]))):
            raise BundleError("historical follower-finish ID or UTC timestamp is invalid")
        narrative = (
            "toolVersion", "command", "requestedOutcome", "observedBehavior",
            "impact", "expectedBehavior",
        )
        if any(not isinstance(payload[name], str) or not payload[name] for name in narrative):
            raise BundleError("historical follower-finish narrative is malformed")
        arrays = (
            "reproduction", "diagnosticsObserved", "consumerErrorsAlreadyEliminated",
            "documentationGapsEncountered", "authorityNotClaimed",
        )
        if (any(not isinstance(payload[name], list) for name in arrays) or
                not isinstance(payload["environment"], dict)):
            raise BundleError("historical follower-finish evidence is malformed")
        return
    if kind == "issue" and historical_layout == "historical-one-file-narrative":
        required = {
            "$schema", "version", "issueId", "createdUtc", "toolVersion", "command",
            "classificationHint", "relatedTo", "purpose", "whatIsAlreadyWorking",
            "items", "whatWeWillNotDo", "environmentFacts", "artifacts", "amendedUtc",
            "amendments", "whatWeDidInstead", "itemStatusAfterAmendment",
        }
        _require(payload, required, required)
        if payload["$schema"] != SCHEMAS["issue"] or payload["version"] != 1:
            raise BundleError("historical narrative schema/version mismatch")
        if (not ISSUE_ID.fullmatch(str(payload["issueId"])) or
                not UTC.fullmatch(str(payload["createdUtc"])) or
                not UTC.fullmatch(str(payload["amendedUtc"]))):
            raise BundleError("historical narrative ID or UTC timestamp is invalid")
        if payload["classificationHint"] not in LEGACY_NARRATIVE_CLASSIFICATIONS:
            raise BundleError("historical narrative classification is invalid")
        if not isinstance(payload["relatedTo"], list) or not isinstance(payload["items"], list) or not isinstance(payload["amendments"], list):
            raise BundleError("historical narrative arrays are malformed")
        _validate_narrative_artifacts(payload["artifacts"])
        _scan_secrets(payload)
        return
    # Response v2 intentionally uses its own stable schema identifier while
    # retaining the v1 bundle-manifest envelope.
    if not (kind == "response" and payload.get("$schema") == RESPONSE_V2_SCHEMA):
        _common_payload(payload, kind)
    if kind == "issue":
        required = {"$schema", "version", "issueId", "createdUtc", "toolVersion", "command", "requestedOutcome", "observedBehavior", "expectedBehavior", "classificationHint", "capabilitiesSha256", "sanitizedExecutionResult", "environmentFacts", "artifacts"}
        _require(payload, required, required)
        if not ISSUE_ID.fullmatch(str(payload["issueId"])) or not UTC.fullmatch(str(payload["createdUtc"])):
            raise BundleError("invalid issue ID or UTC timestamp")
        if payload["classificationHint"] not in CLASSIFICATIONS or not HASH.fullmatch(str(payload["capabilitiesSha256"])):
            raise BundleError("invalid issue classification or capabilities hash")
        _validate_artifacts(payload["artifacts"], strict_issue=True)
    elif kind == "response":
        if payload.get("$schema") == RESPONSE_V2_SCHEMA:
            _scan_secrets(payload)
            required = {"$schema", "version", "issueId", "createdUtc", "classification", "issueManifestSha256", "candidateManifestSha256", "summary", "recommendedAction", "artifacts"}
            _require(payload, required, required)
            if payload["version"] != 2:
                raise BundleError("response v2 version mismatch")
            if (payload["classification"] not in CLASSIFICATIONS or
                    not ISSUE_ID.fullmatch(str(payload["issueId"])) or
                    not UTC.fullmatch(str(payload["createdUtc"])) or
                    not HASH.fullmatch(str(payload["issueManifestSha256"])) or
                    not HASH.fullmatch(str(payload["candidateManifestSha256"]))):
                raise BundleError("response v2 does not contain valid bindings")
            if not isinstance(payload["summary"], str) or not payload["summary"] or not isinstance(payload["recommendedAction"], str) or not payload["recommendedAction"]:
                raise BundleError("response v2 summary/action must be non-empty")
            if not isinstance(payload["artifacts"], list):
                raise BundleError("response v2 artifacts must be an array")
        else:
            required = {"$schema", "version", "issueId", "createdUtc", "classification", "issueManifestSha256", "summary", "recommendedAction", "artifacts"}
            _require(payload, required, required)
            if payload["classification"] not in CLASSIFICATIONS or not ISSUE_ID.fullmatch(str(payload["issueId"])) or not UTC.fullmatch(str(payload["createdUtc"])) or not HASH.fullmatch(str(payload["issueManifestSha256"])):
                raise BundleError("response does not contain a valid issue binding")
            # v1 deliberately promised only an array here.  Keep that reader
            # contract broad; v2 writers may choose the same opaque rows.
            if not isinstance(payload["artifacts"], list):
                raise BundleError("response artifacts must be an array")
    elif kind == "release-candidate":
        required = {"$schema", "version", "productVersion", "sourceCommit", "sourceTag", "capabilitiesSha256", "packageSha256", "sbomSha256", "testSummarySha256", "runtimeAuthority", "visualAuthority"}
        _require(payload, required, required)
        if not re.fullmatch(r"[A-Fa-f0-9]{40}", str(payload["sourceCommit"])) or any(not HASH.fullmatch(str(payload[name])) for name in ("capabilitiesSha256", "packageSha256", "sbomSha256", "testSummarySha256")):
            raise BundleError("invalid release candidate binding")
        if payload["runtimeAuthority"] is not False or payload["visualAuthority"] is not False:
            raise BundleError("release candidate overclaims authority")
    else:
        required = {"$schema", "version", "createdUtc", "candidateManifestSha256", "decision", "decidedBy"}
        _require(payload, required, required)
        if payload["decision"] not in {"APPROVED", "REJECTED"} or not HASH.fullmatch(str(payload["candidateManifestSha256"])):
            raise BundleError("invalid promotion decision or candidate binding")


def _enumerate_named_streams(path: Path) -> list[str]:
    """Return non-default NTFS stream names without opening/copying them."""
    if os.name != "nt":
        return []

    class _FindStreamData(ctypes.Structure):
        _fields_ = [
            ("stream_size", ctypes.c_longlong),
            ("stream_name", ctypes.c_wchar * 296),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    find_first = kernel32.FindFirstStreamW
    find_first.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32,
                           ctypes.POINTER(_FindStreamData), ctypes.c_uint32]
    find_first.restype = ctypes.c_void_p
    find_next = kernel32.FindNextStreamW
    find_next.argtypes = [ctypes.c_void_p, ctypes.POINTER(_FindStreamData)]
    find_next.restype = ctypes.c_int
    find_close = kernel32.FindClose
    find_close.argtypes = [ctypes.c_void_p]
    find_close.restype = ctypes.c_int

    data = _FindStreamData()
    handle = find_first(os.fspath(path), 0, ctypes.byref(data), 0)
    invalid = ctypes.c_void_p(-1).value
    if handle == invalid:
        error = ctypes.get_last_error()
        if error in {1, 50, 87, 38, 2}:  # non-NTFS/unsupported/invalid API target
            return []
        raise BundleError(f"cannot enumerate file streams: {path} (Win32 {error})")
    streams: list[str] = []
    try:
        while True:
            streams.append(data.stream_name)
            if not find_next(handle, ctypes.byref(data)):
                error = ctypes.get_last_error()
                if error != 38:  # ERROR_HANDLE_EOF
                    raise BundleError(
                        f"cannot enumerate file streams: {path} (Win32 {error})")
                break
    finally:
        find_close(handle)
    return [stream for stream in streams if stream != "::$DATA"]


def _inventory(root: Path) -> set[str]:
    actual: set[str] = set()
    folded: dict[str, str] = {}
    stack: list[tuple[Path, str]] = [(root, "")]
    while stack:
        current, prefix = stack.pop()
        _assert_ordinary(current, directory=True)
        try:
            entries = list(os.scandir(current))
        except OSError as exc:
            raise BundleError(f"cannot enumerate bundle directory: {current}") from exc
        for entry in entries:
            name = entry.name
            relative = f"{prefix}/{name}" if prefix else name
            if "\x00" in name or ":" in name:
                raise BundleError(f"ADS or invalid filename refused: {relative}")
            relative = _relative(relative)
            path = Path(entry.path)
            try:
                entry_stat = entry.stat(follow_symlinks=False)
            except OSError as exc:
                raise BundleError(f"cannot inspect bundle entry: {relative}") from exc
            if entry.is_symlink() or bool(getattr(entry_stat, "st_file_attributes", 0) & REPARSE_POINT):
                raise BundleError(f"reparse entry refused: {relative}")
            if stat.S_ISDIR(entry_stat.st_mode):
                stack.append((path, relative))
            elif stat.S_ISREG(entry_stat.st_mode):
                streams = _enumerate_named_streams(path)
                if streams:
                    raise BundleError(
                        f"alternate data stream refused: {relative} ({streams})")
                key = relative.casefold()
                if key in folded:
                    raise BundleError(f"case-colliding duplicate path: {folded[key]} / {relative}")
                folded[key] = relative
                if relative != "bundle-manifest.json":
                    actual.add(relative)
            else:
                raise BundleError(f"nonordinary bundle entry refused: {relative}")
    return actual


def _manifest_rows(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    if not isinstance(manifest.get("files"), list) or not manifest["files"]:
        raise BundleError("manifest files must be a non-empty array")
    declared: dict[str, dict[str, Any]] = {}
    folded: dict[str, str] = {}
    for row in manifest["files"]:
        if not isinstance(row, dict):
            raise BundleError("manifest file row must be an object")
        _require(row, {"path", "size", "sha256"}, {"path", "size", "sha256"})
        relative = _relative(row["path"])
        if relative in declared or relative.casefold() in folded:
            raise BundleError(f"duplicate declared path: {relative}")
        if PurePosixPath(relative).suffix.lower() in FORBIDDEN_SUFFIXES:
            raise BundleError(f"forbidden source or credential extension: {relative}")
        if not isinstance(row["size"], int) or row["size"] < 0 or not HASH.fullmatch(str(row["sha256"])):
            raise BundleError(f"malformed file inventory row: {relative}")
        declared[relative] = row
        folded[relative.casefold()] = relative
    return declared


def _validate_physical_files(root: Path, declared: dict[str, dict[str, Any]]) -> None:
    actual = _inventory(root)
    if actual != set(declared):
        extra = sorted(actual - set(declared))
        missing = sorted(set(declared) - actual)
        raise BundleError(f"undeclared or missing files: undeclared={extra}, missing={missing}")
    for relative, row in declared.items():
        path = root.joinpath(*PurePosixPath(relative).parts)
        _assert_ordinary(path, directory=False)
        raw = path.read_bytes()
        if row["size"] != len(raw):
            raise BundleError(f"size mismatch: {relative}")
        digest = hashlib.sha256(raw).hexdigest().upper()
        if digest != str(row["sha256"]).upper():
            raise BundleError(f"hash mismatch: {relative}")


def _select_root(root: Path) -> tuple[Path, str]:
    root = _lexical_absolute(root)
    _assert_ordinary_ancestors(root)
    _assert_ordinary(root, directory=True)
    direct_manifest = root / "bundle-manifest.json"
    if _lexists(direct_manifest):
        return root, "standard"
    nested = root / "issue"
    if _lexists(nested):
        _assert_ordinary(nested, directory=True)
        names = {entry.name for entry in os.scandir(root)}
        if names != {"issue"}:
            raise BundleError("historical issue root must contain exactly the issue directory")
        return nested, "historical"
    raise BundleError("bundle manifest is missing or is a reparse point")


def _validate_manifest_shape(manifest: Any, layout_kind: str) -> tuple[dict[str, dict[str, Any]], str | None, str | None]:
    if not isinstance(manifest, dict):
        raise BundleError("bundle manifest must be an object")
    if layout_kind == "historical" and set(manifest) == {"$schema", "version", "issueId", "createdUtc", "files"}:
        if (manifest["$schema"] != MANIFEST_SCHEMA or manifest["version"] != 1 or
                not ISSUE_ID.fullmatch(str(manifest["issueId"])) or
                not UTC.fullmatch(str(manifest["createdUtc"]))):
            raise BundleError("historical FaceGeom manifest schema/version mismatch")
        declared = _manifest_rows(manifest)
        if set(declared) != {"facegeom-build-geom-nif-evidence.json", "issue.json", "reproduction.md"}:
            raise BundleError("historical FaceGeom inventory shape is not admitted")
        return declared, "issue.json", "historical-three-file-facegeom"
    fields = {"$schema", "version", "kind", "payload", "files"}
    _require(manifest, fields, fields)
    if manifest["$schema"] != MANIFEST_SCHEMA or manifest["version"] != 1 or manifest["kind"] not in KINDS:
        raise BundleError("bundle manifest schema/version/kind mismatch")
    declared = _manifest_rows(manifest)
    payload_relative = _relative(manifest["payload"])
    if payload_relative not in declared or not payload_relative.endswith(".json"):
        raise BundleError("payload must be one declared JSON file")
    if layout_kind == "historical":
        if manifest["kind"] != "issue":
            raise BundleError("historical wrapper must contain an issue bundle")
    return declared, payload_relative, None


def validate(root: Path, expected_kind: str | None = None) -> dict[str, Any]:
    selected_root, outer_layout = _select_root(root)
    manifest_path = selected_root / "bundle-manifest.json"
    _assert_ordinary(manifest_path, directory=False)
    manifest = _read_json(manifest_path)
    declared, payload_relative, historical_layout = _validate_manifest_shape(manifest, outer_layout)
    _validate_physical_files(selected_root, declared)
    if payload_relative is None:
        payload_relative = "issue.json"
    payload_path = selected_root.joinpath(*PurePosixPath(payload_relative).parts)
    payload = _read_json(payload_path)
    if historical_layout == "historical-three-file-facegeom":
        _validate_payload(payload, "issue")
        if payload.get("issueId") != manifest.get("issueId") or payload.get("createdUtc") != manifest.get("createdUtc"):
            raise BundleError("historical FaceGeom manifest does not bind issue identity")
    elif outer_layout == "historical":
        narrative_fields = {
            "$schema", "version", "issueId", "createdUtc", "toolVersion", "command",
            "classificationHint", "relatedTo", "purpose", "whatIsAlreadyWorking",
            "items", "whatWeWillNotDo", "environmentFacts", "artifacts", "amendedUtc",
            "amendments", "whatWeDidInstead", "itemStatusAfterAmendment",
        }
        follower_finish_fields = {
            "$schema", "version", "issueId", "createdUtc", "toolVersion", "command",
            "requestedOutcome", "observedBehavior", "impact", "expectedBehavior",
            "reproduction", "diagnosticsObserved", "consumerErrorsAlreadyEliminated",
            "documentationGapsEncountered", "environment", "authorityNotClaimed",
        }
        if set(payload) == follower_finish_fields:
            historical_layout = "historical-follower-finish"
            _validate_payload(payload, "issue", historical_layout=historical_layout)
        elif set(payload) == narrative_fields:
            historical_layout = "historical-one-file-narrative"
            _validate_payload(payload, "issue", historical_layout=historical_layout)
        else:
            historical_layout = (
                "historical-one-file-current" if len(declared) == 1
                else "historical-standard-wrapper")
            _validate_payload(payload, "issue")
    else:
        _validate_payload(payload, manifest["kind"])
    kind = "issue" if historical_layout else manifest["kind"]
    if expected_kind and kind != expected_kind:
        raise BundleError(f"bundle kind {kind!r} does not match expected {expected_kind!r}")
    return {
        "status": "PASS", "kind": kind, "files": len(declared),
        "manifestSha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest().upper(),
        "manifestPath": str(manifest_path), "payloadPath": str(payload_path),
        "bundleRoot": str(selected_root), "outerRoot": str(_lexical_absolute(root)),
        "layout": historical_layout or "standard",
        "issueId": payload.get("issueId") if isinstance(payload, dict) else None,
    }


def _destination(output: Path) -> tuple[Path, Path]:
    output = _lexical_absolute(output)
    if not output.name or output.name in {".", ".."}:
        raise PublicationError("output must be a named fresh directory")
    _assert_ordinary_ancestors(output.parent)
    _assert_ordinary(output.parent, directory=True)
    if _lexists(output):
        raise PublicationError(f"publication output already exists: {output}")
    return output.parent, output


def _owned_stage(parent: Path, name: str) -> StageHandle:
    try:
        stage = Path(tempfile.mkdtemp(prefix=f".{name}.staging-", dir=os.fspath(parent)))
    except OSError as exc:
        raise PublicationError(f"unable to create owned staging directory under {parent}") from exc
    _assert_ordinary(stage, directory=True)
    identity = stage.lstat()
    return StageHandle(stage, identity.st_dev, identity.st_ino)


def _assert_stage_identity(stage: StageHandle) -> None:
    if not isinstance(stage, StageHandle):
        raise PublicationError("publication staging handle is not owned")
    try:
        current = stage.path.lstat()
    except OSError as exc:
        raise PublicationError("owned staging directory disappeared") from exc
    if (stage.path.is_symlink() or
            bool(getattr(current, "st_file_attributes", 0) & REPARSE_POINT) or
            not stat.S_ISDIR(current.st_mode) or
            (current.st_dev, current.st_ino) != (stage.device, stage.inode)):
        raise PublicationError("owned staging directory identity changed")


def _cleanup_stage(stage: StageHandle) -> None:
    _assert_stage_identity(stage)
    if not _lexists(stage.path):
        return
    try:
        shutil.rmtree(stage.path)
    except OSError as exc:
        raise PublicationError(f"owned staging cleanup failed: {stage.path}") from exc


def _rename_noreplace_windows(stage: Path, output: Path) -> None:
    # Windows os.rename maps to MoveFileEx without replacement for directories.
    os.rename(os.fspath(stage), os.fspath(output))


def _rename_noreplace_posix(stage: Path, output: Path) -> None:
    """Use a kernel no-replace directory rename; never plain os.rename."""
    libc = ctypes.CDLL(None, use_errno=True)
    renameat2 = getattr(libc, "renameat2", None)
    if renameat2 is not None:
        renameat2.argtypes = [ctypes.c_int, ctypes.c_char_p,
                              ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        renameat2.restype = ctypes.c_int
        result = renameat2(-100, os.fsencode(stage), -100, os.fsencode(output), 1)
        if result == 0:
            return
        error = ctypes.get_errno()
        if error not in {38, 22, 95}:  # ENOSYS, EINVAL, ENOTSUP
            raise OSError(error, os.strerror(error))

    renameatx_np = getattr(libc, "renameatx_np", None)
    if renameatx_np is not None:
        renameatx_np.argtypes = [ctypes.c_int, ctypes.c_char_p,
                                 ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        renameatx_np.restype = ctypes.c_int
        result = renameatx_np(-100, os.fsencode(stage), -100,
                              os.fsencode(output), 0x00000004)  # RENAME_EXCL
        if result == 0:
            return
        error = ctypes.get_errno()
        if error not in {38, 22, 95}:
            raise OSError(error, os.strerror(error))
    raise PublicationError("atomic no-replace directory rename is unavailable")


def _atomic_noreplace(stage: Path, output: Path) -> None:
    if os.name == "nt":
        _rename_noreplace_windows(stage, output)
    else:
        _rename_noreplace_posix(stage, output)


def _publish_no_replace(stage: StageHandle, output: Path) -> None:
    _assert_stage_identity(stage)
    if _lexists(output):
        raise PublicationError(f"publication destination became occupied: {output}")
    try:
        _atomic_noreplace(stage.path, output)
    except OSError as exc:
        raise PublicationError(f"no-replace publication failed: {output}") from exc


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("bundle")
    parser.add_argument("--expect-kind", choices=sorted(KINDS))
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    try:
        result = validate(Path(args.bundle), args.expect_kind)
    except BundleError as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True) if args.json else f"PASS {result['kind']} files={result['files']} manifest={result['manifestSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
